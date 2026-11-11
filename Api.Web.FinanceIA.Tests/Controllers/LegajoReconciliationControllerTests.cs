// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Controllers;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// <see cref="LegajoReconciliationController"/> end to end over the real <see cref="LegajoReconciliationService"/>
/// and <see cref="LegajoReconciliationRepository"/> (EF InMemory), with the legajo documents in the hand-written
/// fake: which documents feed a rebuild, tenant isolation, the inventory and incomes views, the PATCH rules and
/// their errorCodes, edits surviving a rebuild, the audit without values, and the rebuild endpoint.
/// </summary>
public sealed class LegajoReconciliationControllerTests
{
    private sealed class Harness
    {
        public FakeLegajoDocumentRepository Legajos { get; } = new();
        public StubUserRepository Users { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public FinanciaDbContext Db { get; }
        public LegajoReconciliationRepository Repository { get; }
        public LegajoReconciliationService Service { get; }
        public LegajoReconciliationController Sut { get; }

        public Harness(string tenant = LegajoTestData.Tenant, string? databaseName = null)
        {
            Db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
                .UseInMemoryDatabase(databaseName ?? $"reconciliation-{Guid.NewGuid()}").Options);
            Repository = new LegajoReconciliationRepository(Db);
            Service = new LegajoReconciliationService(
                Legajos, Repository, new LegajoIntegrityService(), Options.Create(new LegajoOptions()),
                NullLogger<LegajoReconciliationService>.Instance);
            Sut = new LegajoReconciliationController(
                new StubTenantContextAccessor { TenantContext = LegajoTestData.BuildTenant(tenant) },
                Legajos, Repository, Service, Users, Audit, NullLogger<LegajoReconciliationController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", LegajoTestData.FirebaseUid)], "Test"))
                    }
                }
            };
        }

        /// <summary>A document of <paramref name="type"/> in <paramref name="status"/> with a current run of <paramref name="input"/>.</summary>
        public Document Add(PersonLegajo legajo, string type, JsonObject? input = null, string status = LegajoStatuses.Ready)
        {
            var schema   = LegajoExtractionPrompts.For(type)!;
            var document = Legajos.AddDocument(legajo, type, status, hash: Guid.NewGuid().ToString("N"));
            Legajos.Runs.Add(new LegajoExtraction
            {
                ExtractionId   = Guid.NewGuid(),
                TenantId       = legajo.TenantId,
                PersonLegajoId = legajo.PersonLegajoId,
                DocumentId     = document.DocumentId,
                Kind           = LegajoExtractionKinds.Extraction,
                DocumentType   = type,
                SchemaVersion  = schema.SchemaVersion,
                PromptVersion  = schema.PromptVersion,
                Model          = "claude-sonnet-4-6",
                ReadMode       = LegajoReadModes.Document,
                Status         = LegajoExtractionStatuses.Succeeded,
                ResultJson     = LegajoExtractionFixtures.Normalize(schema, input ?? LegajoExtractionFixtures.Clean(type)).ToJsonString(),
                IsCurrent      = true,
                CreatedAt      = DateTime.UtcNow.AddHours(-1)
            });
            return document;
        }

        /// <summary>A legajo whose BP and MB pair on the Inmueble (PART-0001), the MB valuing it at <paramref name="marketMiles"/>.</summary>
        public PersonLegajo LegajoWithPair(decimal marketMiles = 8_000m, string tenant = LegajoTestData.Tenant)
        {
            var legajo = Legajos.AddLegajo(tenantId: tenant, consent: true);
            Add(legajo, DocumentTypes.BienesPersonales);
            Add(legajo, DocumentTypes.ManifestacionBienes, Mb(marketMiles));
            return legajo;
        }

        public static JsonObject Mb(decimal marketMiles)
        {
            var mb = LegajoExtractionFixtures.ManifestacionBienes();
            var inmueble = (JsonObject)mb["annexItems"]!.AsArray()[0]!;
            inmueble["matchKey"]     = new JsonObject { ["kind"] = "Partida", ["value"] = "PART-0001" };
            inmueble["ownershipPct"] = 50m;
            inmueble["marketValue"]  = marketMiles;
            return mb;
        }

        public async Task<LegajoItem> ItemAsync(PersonLegajo legajo, string key)
            => (await Repository.GetItemsAsync(legajo.PersonLegajoId, legajo.TenantId, CancellationToken.None))
                .Single(i => i.ItemKey == key);
    }

    private static T Ok<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static (int Status, string? ErrorCode) Problem(IActionResult result)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        var pd  = Assert.IsAssignableFrom<ProblemDetails>(obj.Value);
        return (obj.StatusCode ?? pd.Status ?? 0, pd.Extensions.TryGetValue("errorCode", out var code) ? code?.ToString() : null);
    }

    // ── Rebuild: what feeds it ──────────────────────────────────────────────────

    [Fact]
    public async Task Rebuild_ReadsOnlyReadyDocumentsWithACurrentRun()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var ready  = h.Add(legajo, DocumentTypes.DdjjIva);
        h.Add(legajo, DocumentTypes.Ganancias, status: LegajoStatuses.NeedsReview);
        h.Add(legajo, DocumentTypes.Monotributo, status: LegajoStatuses.Blocked);
        var deleted = h.Add(legajo, DocumentTypes.CertificacionIngresos);
        deleted.IsDeleted = true;
        var noRun = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIibb, LegajoStatuses.Ready, hash: "no-run");
        var other = h.Legajos.AddLegajo(consent: true);
        h.Add(other, DocumentTypes.Monotributo);

        var result = Ok<LegajoReconciliationResultResponse>(await h.Sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal([ready.DocumentId], result.DocumentsUsed);
        Assert.Empty(result.DocumentsSkipped);
        Assert.DoesNotContain(noRun.DocumentId, result.DocumentsUsed);
        var incomes = await h.Repository.GetIncomesAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.Equal(2, incomes.Count);
        Assert.All(incomes, i => Assert.Equal(LegajoIncomeSources.Iva, i.Source));
        Assert.Equal(2, result.IncomesCount);
    }

    [Fact]
    public async Task Rebuild_UsesTheEffectiveValues_CorrectionsApplied()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var document = h.Add(legajo, DocumentTypes.DdjjIva);
        var run = h.Legajos.Runs.Single(r => r.DocumentId == document.DocumentId);
        h.Legajos.Corrections.Add(new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, ExtractionId = run.ExtractionId,
            FieldPath = "/periods/0/ventasNetoGravado", CorrectedValueJson = "1300000", Reason = "Leído mal",
            CorrectedBy = Guid.NewGuid(), CorrectedAt = DateTime.UtcNow
        });

        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var november = (await h.Repository.GetIncomesAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None))
            .Single(i => i.Period == "2025-11");
        Assert.Equal(1_350_000m, november.Amount);
    }

    [Fact]
    public async Task Rebuild_IsTenantIsolated()
    {
        var database = $"reconciliation-{Guid.NewGuid()}";
        var mine   = new Harness(databaseName: database);
        var theirs = new Harness(LegajoTestData.OtherTenant, database);

        var myLegajo    = mine.LegajoWithPair();
        var theirLegajo = theirs.LegajoWithPair(tenant: LegajoTestData.OtherTenant);
        await theirs.Service.RebuildAsync(theirLegajo.PersonLegajoId, LegajoTestData.OtherTenant, CancellationToken.None);
        var theirCount = theirs.Db.LegajoItems.AsNoTracking().Count();

        // Another tenant's legajo is not found from this tenant, and its rows are untouched.
        Assert.Equal((404, "legajo_not_found"), Problem(await mine.Sut.RebuildAsync(theirLegajo.PersonLegajoId, CancellationToken.None)));
        Assert.Equal((404, "legajo_not_found"), Problem(await mine.Sut.GetInventoryAsync(theirLegajo.PersonLegajoId, CancellationToken.None)));
        Assert.Equal((404, "legajo_not_found"), Problem(await mine.Sut.GetIncomesAsync(theirLegajo.PersonLegajoId, CancellationToken.None)));

        await mine.Service.RebuildAsync(myLegajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var all = mine.Db.LegajoItems.AsNoTracking().ToList();
        Assert.Equal(theirCount, all.Count(i => i.TenantId == LegajoTestData.OtherTenant));
        Assert.All(all.Where(i => i.PersonLegajoId == myLegajo.PersonLegajoId), i => Assert.Equal(LegajoTestData.Tenant, i.TenantId));
        Assert.Empty(await mine.Repository.GetItemsAsync(theirLegajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));

        // An item id of the other tenant is not found either.
        var foreignItem = all.First(i => i.TenantId == LegajoTestData.OtherTenant);
        Assert.Equal((404, "legajo_not_found"), Problem(await mine.Sut.UpdateItemAsync(
            theirLegajo.PersonLegajoId, foreignItem.ItemId, new LegajoItemUpdateRequest { IsHousing = true, Reason = "Prueba de aislamiento" },
            CancellationToken.None)));
        Assert.Equal((404, "item_not_found"), Problem(await mine.Sut.UpdateItemAsync(
            myLegajo.PersonLegajoId, foreignItem.ItemId, new LegajoItemUpdateRequest { IsHousing = true, Reason = "Prueba de aislamiento" },
            CancellationToken.None)));
    }

    [Fact]
    public async Task Rebuild_ARectificativaUploadedLater_ReplacesTheOlderDocumentsRows()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var original = h.Add(legajo, DocumentTypes.Ganancias);
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var rect = LegajoExtractionFixtures.Ganancias();
        rect["header"]!["isRectificativa"]     = LegajoExtractionFixtures.F(true);
        rect["header"]!["rectificativaNumber"] = LegajoExtractionFixtures.F(1);
        rect["justification"]!["montoConsumido"] = LegajoExtractionFixtures.F(5_000_000m);
        var rectificativa = h.Add(legajo, DocumentTypes.Ganancias, rect);

        var result = Ok<LegajoReconciliationResultResponse>(await h.Sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None));

        var incomes = await h.Repository.GetIncomesAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.All(incomes, i => Assert.Equal(rectificativa.DocumentId, i.DocumentId));
        Assert.Equal(5_000_000m, incomes.Single(i => i.Concept == LegajoIncomeConcepts.MontoConsumido).Amount);
        Assert.Contains(result.DocumentsSkipped, s => s.DocumentId == original.DocumentId && s.Reason == "replaced");
        Assert.Equal(4, incomes.Count);   // updated in place, nothing duplicated
    }

    [Fact]
    public async Task Rebuild_ADocumentLeavingReady_TakesItsRowsWithIt()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.Contains(await h.Repository.GetItemsAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None),
                        i => i.MarketValue is not null);

        h.Legajos.Documents.Single(d => d.DocumentType == DocumentTypes.ManifestacionBienes).LegajoStatus = LegajoStatuses.NeedsReview;
        var result = Ok<LegajoReconciliationResultResponse>(await h.Sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None));

        var items = await h.Repository.GetItemsAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.All(items, i => Assert.Null(i.MarketValue));
        Assert.All(items, i => Assert.Equal(LegajoReconciliationStatuses.OnlyInBienesPersonales, i.ReconciliationStatus));
        Assert.Empty(await h.Repository.GetIncomesAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));
        Assert.True(result.ItemsRemoved > 0);
    }

    // ── Views ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inventory_GroupsBothValuesAndTotals()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var view = Ok<LegajoInventoryResponse>(await h.Sut.GetInventoryAsync(legajo.PersonLegajoId, CancellationToken.None));

        var inmueble = view.Groups.Single(g => g.Kind == LegajoItemKinds.Inmueble).Items.Single(i => i.MatchKey == "PART0001");
        Assert.Equal(8_000_000m, inmueble.MarketValue);
        Assert.Equal(1_000_000m, inmueble.FiscalValue);
        Assert.NotNull(inmueble.MarketValueDocumentId);
        Assert.NotNull(inmueble.FiscalValueDocumentId);
        Assert.Equal(LegajoReconciliationStatuses.Matched, inmueble.ReconciliationStatus);
        Assert.Equal(8_300_000m, view.Totals.PatrimonioNetoMarketValue);
        Assert.NotNull(view.LastUpdatedAt);
    }

    [Fact]
    public async Task Inventory_CarriesTheManifestacionsGuaranteesGiven_WithItsDocument()
    {
        // mb-1.2: "avales y/o coobligaciones otorgados" 1.500 (en miles) → $ 1.500.000, beside the items.
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        h.Add(legajo, DocumentTypes.BienesPersonales);
        var mb = Harness.Mb(8_000m);
        mb["contingencies"] = new JsonObject { ["guaranteesGiven"] = LegajoExtractionFixtures.F(1_500m) };
        var manifestacion = h.Add(legajo, DocumentTypes.ManifestacionBienes, mb);
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var view = Ok<LegajoInventoryResponse>(await h.Sut.GetInventoryAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(1_500_000m, view.GarantiasOtorgadasDeclaradas);
        Assert.Equal(manifestacion.DocumentId, view.GarantiasOtorgadasDocumentId);
    }

    [Fact]
    public async Task Inventory_OfAManifestacionWithoutTheLine_HasNoGuaranteesGiven()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var view = Ok<LegajoInventoryResponse>(await h.Sut.GetInventoryAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Single(view.ManifestacionDocumentIds);
        Assert.Null(view.GarantiasOtorgadasDeclaradas);
        Assert.Null(view.GarantiasOtorgadasDocumentId);
    }

    [Fact]
    public async Task Incomes_ByPeriodWithCoherence()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        h.Add(legajo, DocumentTypes.DdjjIva);
        h.Add(legajo, DocumentTypes.DdjjIibb);
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var view = Ok<LegajoIncomesResponse>(await h.Sut.GetIncomesAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(["2025-12", "2025-11"], view.Periods.Select(p => p.Period!));
        var december = view.Periods[0];
        Assert.Equal(2, december.Entries.Count);
        Assert.Equal(0.5556m, december.Coherence.MaxRelativeDiff);   // (1.800.000 − 800.000) / 1.800.000
        Assert.Equal(LegajoIncomeSources.Iva, december.PrimarySalesSource);
    }

    [Fact]
    public async Task Views_OfAnEmptyLegajo_AreEmpty()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);

        var inventory = Ok<LegajoInventoryResponse>(await h.Sut.GetInventoryAsync(legajo.PersonLegajoId, CancellationToken.None));
        var incomes   = Ok<LegajoIncomesResponse>(await h.Sut.GetIncomesAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Empty(inventory.Groups);
        Assert.Null(inventory.LastUpdatedAt);
        Assert.Equal(0m, inventory.Totals.ActivoMarketValue);
        Assert.Empty(incomes.Periods);
    }

    // ── PATCH ───────────────────────────────────────────────────────────────────

    private static LegajoItemUpdateRequest? InvalidRequest(string name) => name switch
    {
        "no-body"        => null,
        "no-reason"      => new LegajoItemUpdateRequest { IsHousing = true },
        "short-reason"   => new LegajoItemUpdateRequest { IsHousing = true, Reason = " abc " },
        "long-reason"    => new LegajoItemUpdateRequest { IsHousing = true, Reason = new string('x', 1001) },
        "no-change"      => new LegajoItemUpdateRequest { Reason = "Sin ningún cambio" },
        "confirm-false"  => new LegajoItemUpdateRequest { ConfirmConflict = false, Reason = "Sin ningún cambio" },
        "pct-over"       => new LegajoItemUpdateRequest { OwnershipPct = 100.01m, Reason = "Porcentaje inválido" },
        "pct-negative"   => new LegajoItemUpdateRequest { OwnershipPct = -1m, Reason = "Porcentaje inválido" },
        "long-note"      => new LegajoItemUpdateRequest { AnalystNote = new string('n', 1001), Reason = "Nota demasiado larga" },
        "class-unknown"  => new LegajoItemUpdateRequest { InvestmentClass = "Bono", Reason = "Clase de inversión" },
        "class-casing"   => new LegajoItemUpdateRequest { InvestmentClass = "titulopublico", Reason = "Clase de inversión" },
        _                => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [InlineData("no-body", "reason")]
    [InlineData("no-reason", "reason")]
    [InlineData("short-reason", "reason")]
    [InlineData("long-reason", "reason")]
    [InlineData("no-change", "request")]
    [InlineData("confirm-false", "request")]
    [InlineData("pct-over", "ownershipPct")]
    [InlineData("pct-negative", "ownershipPct")]
    [InlineData("long-note", "analystNote")]
    [InlineData("class-unknown", "investmentClass")]
    [InlineData("class-casing", "investmentClass")]
    public async Task Patch_Invalid_Is400ValidationError_BeforeAnyRead(string requestName, string field)
    {
        var h = new Harness();
        var request = InvalidRequest(requestName);

        var result = await h.Sut.UpdateItemAsync(Guid.NewGuid(), Guid.NewGuid(), request, CancellationToken.None);

        Assert.Equal((400, "validation_error"), Problem(result));
        var errors = JsonSerializer.Serialize(((ProblemDetails)((ObjectResult)result).Value!).Extensions["errors"]);
        Assert.Contains($"\"field\":\"{field}\"", errors);
    }

    [Fact]
    public async Task Patch_UnknownLegajoOrItem_Is404WithItsCode()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        var request = new LegajoItemUpdateRequest { IsHousing = true, Reason = "Motivo suficiente" };

        Assert.Equal((404, "legajo_not_found"), Problem(await h.Sut.UpdateItemAsync(Guid.NewGuid(), Guid.NewGuid(), request, CancellationToken.None)));
        Assert.Equal((404, "item_not_found"), Problem(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, Guid.NewGuid(), request, CancellationToken.None)));
    }

    [Fact]
    public async Task Patch_EditsSurviveARebuild_AndTheAuditCarriesNoValues()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var item = await h.ItemAsync(legajo, "Partida:PART0001");

        var response = Ok<LegajoItemResponse>(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest
            {
                IsHousing = false, IsActivityAsset = true, OwnershipPct = 60m, AnalystNote = "  Nota confidencial  ",
                Reason = "Escritura revisada"
            }, CancellationToken.None));

        Assert.False(response.IsHousing);
        Assert.True(response.IsActivityAsset);
        Assert.Equal(60m, response.OwnershipPct);
        Assert.Equal("Nota confidencial", response.AnalystNote);
        Assert.Equal(["IsHousing", "IsActivityAsset", "OwnershipPct"], response.EditedFields);
        Assert.Equal(h.Users.Analyst.DisplayName, response.UpdatedByName);

        var entry = Assert.Single(h.Audit.Entries, e => e.Action == "LegajoItem.Updated");
        Assert.Contains("Escritura revisada", entry.NewJson);
        Assert.Contains("ownershipPct", entry.NewJson);
        Assert.DoesNotContain("Nota confidencial", entry.NewJson);
        // The JSON carries the legajo id, and a random GUID may itself contain "60".
        Assert.DoesNotContain("60", entry.NewJson!.Replace(legajo.PersonLegajoId.ToString(), string.Empty, StringComparison.Ordinal));

        // New documents change the market value; the analyst's fields and the note stand.
        h.Legajos.Documents.Single(d => d.DocumentType == DocumentTypes.ManifestacionBienes).IsDeleted = true;
        h.Add(legajo, DocumentTypes.ManifestacionBienes, Harness.Mb(9_000m));
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        var rebuilt = await h.ItemAsync(legajo, "Partida:PART0001");
        Assert.Equal(item.ItemId, rebuilt.ItemId);
        Assert.Equal(9_000_000m, rebuilt.MarketValue);
        Assert.False(rebuilt.IsHousing);
        Assert.True(rebuilt.IsActivityAsset);
        Assert.Equal(60m, rebuilt.OwnershipPct);
        Assert.Equal("Nota confidencial", rebuilt.AnalystNote);
        Assert.Null(rebuilt.UpdatedBy);   // a rebuild wrote it last
    }

    // ── PATCH: the investment class (DDL 080) ───────────────────────────────────

    /// <summary>The clean manifestación's unkeyed investment (annex B).</summary>
    private const string InversionKey = "MB:Inversion:ITEM SINTETICO INVERSION";

    [Fact]
    public async Task Patch_InvestmentClass_OnAnInversion_IsEdited_SurvivesARebuild_AndIsAuditedByName()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var item = await h.ItemAsync(legajo, InversionKey);
        Assert.Null(item.InvestmentClass);   // a manifestación read before mb-1.3

        var response = Ok<LegajoItemResponse>(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest { InvestmentClass = " TituloPublico ", Reason = "Es un bono del Tesoro" },
            CancellationToken.None));

        Assert.Equal(LegajoInvestmentClasses.TituloPublico, response.InvestmentClass);
        Assert.Equal(["InvestmentClass"], response.EditedFields);
        Assert.Equal(LegajoReconciliationStatuses.OnlyInManifestacion, response.ReconciliationStatus);

        var entry = Assert.Single(h.Audit.Entries, e => e.Action == "LegajoItem.Updated");
        Assert.Contains("\"investmentClass\"", entry.NewJson);
        Assert.Contains("Es un bono del Tesoro", entry.NewJson);
        Assert.DoesNotContain("TituloPublico", entry.NewJson);   // the field's name, never its value

        // A rebuild from the same (unclassified) documents keeps the analyst's class.
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var rebuilt = await h.ItemAsync(legajo, InversionKey);
        Assert.Equal(item.ItemId, rebuilt.ItemId);
        Assert.Equal(LegajoInvestmentClasses.TituloPublico, rebuilt.InvestmentClass);
        Assert.Equal("InvestmentClass", rebuilt.EditedFields);

        var inventory = Ok<LegajoInventoryResponse>(await h.Sut.GetInventoryAsync(legajo.PersonLegajoId, CancellationToken.None));
        var exposed = inventory.Groups.Single(g => g.Kind == LegajoItemKinds.Inversion).Items.Single();
        Assert.Equal(LegajoInvestmentClasses.TituloPublico, exposed.InvestmentClass);
    }

    [Fact]
    public async Task Patch_InvestmentClass_EmptyString_ClearsIt_AndARebuildDoesNotRefillIt()
    {
        var h = new Harness();
        var legajo = Legajo(h, LegajoExtractionFixtures.ManifestacionBienesWithInvestments());
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var bono = (await h.Repository.GetItemsAsync(legajo.PersonLegajoId, legajo.TenantId, CancellationToken.None))
            .Single(i => i.Description == "Bono sintetico 2030");
        Assert.Equal(LegajoInvestmentClasses.TituloPublico, bono.InvestmentClass);

        var response = Ok<LegajoItemResponse>(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, bono.ItemId,
            new LegajoItemUpdateRequest { InvestmentClass = "", Reason = "El documento no lo aclara" }, CancellationToken.None));

        Assert.Null(response.InvestmentClass);
        Assert.Equal(["InvestmentClass"], response.EditedFields);

        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.Null((await h.Repository.GetItemsAsync(legajo.PersonLegajoId, legajo.TenantId, CancellationToken.None))
            .Single(i => i.ItemId == bono.ItemId).InvestmentClass);
    }

    [Theory]
    [InlineData("TituloPublico")]
    [InlineData("")]
    public async Task Patch_InvestmentClass_OnANonInversion_Is400OnTheField_AndChangesNothing(string value)
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var inmueble = await h.ItemAsync(legajo, "Partida:PART0001");

        var result = await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, inmueble.ItemId,
            new LegajoItemUpdateRequest { InvestmentClass = value, IsHousing = false, Reason = "Clase de inversión" },
            CancellationToken.None);

        Assert.Equal((400, "validation_error"), Problem(result));
        var errors = JsonSerializer.Serialize(((ProblemDetails)((ObjectResult)result).Value!).Extensions["errors"]);
        Assert.Contains("\"field\":\"investmentClass\"", errors);
        Assert.Empty(h.Audit.Entries);

        var unchanged = await h.ItemAsync(legajo, "Partida:PART0001");
        Assert.Null(unchanged.InvestmentClass);
        Assert.Null(unchanged.EditedFields);
        Assert.True(unchanged.IsHousing);
    }

    private static PersonLegajo Legajo(Harness h, JsonObject manifestacion)
    {
        var legajo = h.Legajos.AddLegajo(tenantId: LegajoTestData.Tenant, consent: true);
        h.Add(legajo, DocumentTypes.ManifestacionBienes, manifestacion);
        return legajo;
    }

    [Fact]
    public async Task Patch_ConfirmConflict_SurvivesARebuild_UntilTheConflictChanges()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair(marketMiles: 900m);   // below the 1.000.000 fiscal floor
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var item = await h.ItemAsync(legajo, "Partida:PART0001");
        Assert.Equal(LegajoReconciliationStatuses.Conflict, item.ReconciliationStatus);

        var confirmed = Ok<LegajoItemResponse>(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest { ConfirmConflict = true, Reason = "Tasación vieja, aceptada" }, CancellationToken.None));
        Assert.Equal(LegajoReconciliationStatuses.AnalystConfirmed, confirmed.ReconciliationStatus);
        Assert.Equal([LegajoItemConflictReasons.BelowFiscalFloor], confirmed.ConflictReasons);

        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.Equal(LegajoReconciliationStatuses.AnalystConfirmed, (await h.ItemAsync(legajo, "Partida:PART0001")).ReconciliationStatus);

        // Confirming again: no longer a Conflict.
        Assert.Equal((409, "item_not_in_conflict"), Problem(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest { ConfirmConflict = true, Reason = "Otra confirmación" }, CancellationToken.None)));

        // A new manifestación with another value: the confirmation lapses.
        h.Legajos.Documents.Single(d => d.DocumentType == DocumentTypes.ManifestacionBienes).IsDeleted = true;
        h.Add(legajo, DocumentTypes.ManifestacionBienes, Harness.Mb(800m));
        var result = Ok<LegajoReconciliationResultResponse>(await h.Sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(1, result.ConfirmationsLapsed);
        Assert.Equal(LegajoReconciliationStatuses.Conflict, (await h.ItemAsync(legajo, "Partida:PART0001")).ReconciliationStatus);
    }

    [Fact]
    public async Task Patch_ConfirmConflict_OfAMatchedItem_Is409()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var item = await h.ItemAsync(legajo, "Partida:PART0001");

        Assert.Equal((409, "item_not_in_conflict"), Problem(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest { ConfirmConflict = true, Reason = "Confirmación indebida" }, CancellationToken.None)));
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public async Task Patch_RacingAnotherWrite_Is409ItemChanged()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var item = await h.ItemAsync(legajo, "Partida:PART0001");

        // The repository read the row; then another write moved it before this edit saved.
        var stale = new LegajoItem { ItemId = item.ItemId, TenantId = item.TenantId, PersonLegajoId = item.PersonLegajoId,
                                     ItemKey = item.ItemKey, IsHousing = true, ReconciliationStatus = item.ReconciliationStatus,
                                     UpdatedAt = DateTime.UtcNow };
        Assert.False(await h.Repository.TryUpdateItemAsync(stale, item.UpdatedAt.AddSeconds(-5), CancellationToken.None));

        // And end to end: the edit after a rebuild that changed the row still works (it reads the new version).
        var ok = await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest { IsHousing = false, Reason = "Corrección del analista" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(ok);
    }

    [Fact]
    public async Task Patch_WithoutAResolvableUser_IsUnauthorized()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();
        await h.Service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        var item = await h.ItemAsync(legajo, "Partida:PART0001");
        h.Sut.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.IsType<UnauthorizedResult>(await h.Sut.UpdateItemAsync(legajo.PersonLegajoId, item.ItemId,
            new LegajoItemUpdateRequest { IsHousing = true, Reason = "Motivo suficiente" }, CancellationToken.None));
    }

    // ── POST rebuild ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RebuildEndpoint_ReturnsTheCounts_AndAudits()
    {
        var h = new Harness();
        var legajo = h.LegajoWithPair();

        var result = Ok<LegajoReconciliationResultResponse>(await h.Sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.True(result.ItemsAdded > 0);
        Assert.Equal(result.ItemsAdded, result.ItemsCount);
        Assert.Equal(2, result.DocumentsUsed.Count);
        Assert.Single(h.Audit.Entries, e => e.Action == "PersonLegajo.ReconciliationRebuilt");

        // Idempotent: a second rebuild changes nothing.
        var again = Ok<LegajoReconciliationResultResponse>(await h.Sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None));
        Assert.Equal((0, 0, 0), (again.ItemsAdded, again.ItemsUpdated, again.ItemsRemoved));
        Assert.Equal(result.ItemsCount, again.ItemsCount);
    }

    [Fact]
    public async Task RebuildEndpoint_LosingTheRaceTwice_Is409ReconciliationBusy()
    {
        var legajos = new FakeLegajoDocumentRepository();
        var legajo  = legajos.AddLegajo(consent: true);
        var losing  = new LosingRepository();
        var service = new LegajoReconciliationService(legajos, losing, new LegajoIntegrityService(),
            Options.Create(new LegajoOptions()), NullLogger<LegajoReconciliationService>.Instance);
        var sut = new LegajoReconciliationController(
            new StubTenantContextAccessor { TenantContext = LegajoTestData.BuildTenant() }, legajos, losing, service,
            new StubUserRepository(), new RecordingAuditService(), NullLogger<LegajoReconciliationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        Assert.Equal((409, "reconciliation_busy"), Problem(await sut.RebuildAsync(legajo.PersonLegajoId, CancellationToken.None)));
        Assert.Equal(2, losing.Attempts);
    }

    [Fact]
    public async Task Service_RetriesOnce_AfterALostRace()
    {
        var legajos = new FakeLegajoDocumentRepository();
        var legajo  = legajos.AddLegajo(consent: true);
        var losing  = new LosingRepository { LoseTimes = 1 };
        var service = new LegajoReconciliationService(legajos, losing, new LegajoIntegrityService(),
            Options.Create(new LegajoOptions()), NullLogger<LegajoReconciliationService>.Instance);

        var outcome = await service.RebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        Assert.False(outcome.LostRace);
        Assert.NotNull(outcome.Result);
        Assert.Equal(2, losing.Attempts);
    }

    [Fact]
    public async Task TryRebuild_NeverThrows()
    {
        var legajos = new FakeLegajoDocumentRepository();
        var legajo  = legajos.AddLegajo(consent: true);
        var service = new LegajoReconciliationService(legajos, new LosingRepository { Throw = new InvalidOperationException("boom") },
            new LegajoIntegrityService(), Options.Create(new LegajoOptions()), NullLogger<LegajoReconciliationService>.Instance);

        await service.TryRebuildAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, "approval", CancellationToken.None);
    }

    /// <summary>A repository whose rebuild loses the race <see cref="LoseTimes"/> times (or throws <see cref="Throw"/>).</summary>
    private sealed class LosingRepository : ILegajoReconciliationRepository
    {
        public int LoseTimes { get; init; } = int.MaxValue;
        public Exception? Throw { get; init; }
        public int Attempts { get; private set; }

        public Task<IReadOnlyList<LegajoItem>> GetItemsAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LegajoItem>>([]);

        public Task<IReadOnlyList<LegajoIncome>> GetIncomesAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LegajoIncome>>([]);

        public Task<LegajoItem?> GetItemAsync(Guid itemId, Guid personLegajoId, string tenantId, CancellationToken ct)
            => Task.FromResult<LegajoItem?>(null);

        public Task<bool> TryUpdateItemAsync(LegajoItem item, DateTime expectedUpdatedAt, CancellationToken ct)
            => Task.FromResult(false);

        public Task<LegajoReconciliationMergeResult> ApplyRebuildAsync(
            Guid personLegajoId, string tenantId, LegajoReconciliationBuild build, DateTime now, CancellationToken ct)
        {
            Attempts++;
            if (Throw is not null)
                throw Throw;
            if (Attempts <= LoseTimes)
                throw new DbUpdateException("UX_LegajoItems_Key");

            return Task.FromResult(LegajoReconciliation.Merge([], [], build, now));
        }
    }
}
