// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// <see cref="LegajoExtractionController"/>: the effective view (no holder CUIT, checks recomputed,
/// queues), corrections (reason, path and type validation, append-only, recomputed checks, Ready →
/// NeedsReview), and the review with its override rule. Synthetic data; hand-written fakes.
/// </summary>
public sealed class LegajoExtractionControllerTests
{
    private sealed class Harness
    {
        public FakeLegajoDocumentRepository Legajos { get; } = new();
        public StubUserRepository Users { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public RecordingReconciliationService Reconciliation { get; } = new();
        public LegajoExtractionController Sut { get; }

        public Harness(
            string tenant = LegajoTestData.Tenant,
            Func<FakeLegajoDocumentRepository, ILegajoReconciliationService>? reconciliation = null)
        {
            Sut = new LegajoExtractionController(
                new StubTenantContextAccessor { TenantContext = LegajoTestData.BuildTenant(tenant) },
                new ListDocumentRepository(Legajos.Documents),
                Legajos,
                Users,
                Audit,
                new LegajoIntegrityService(),
                reconciliation?.Invoke(Legajos) ?? Reconciliation,
                Options.Create(new LegajoOptions()),
                NullLogger<LegajoExtractionController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("uid", LegajoTestData.FirebaseUid)], "Test"))
                    }
                }
            };
        }

        /// <summary>A BP document with a current extraction whose totalBienes is off (one failed check).</summary>
        public (Document Document, LegajoExtraction Run) Extracted(string status = LegajoStatuses.NeedsReview, bool clean = false)
        {
            var legajo = Legajos.AddLegajo(consent: true);
            var document = Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, status);
            document.PeriodEnd = new DateTime(2025, 12, 31);

            var input = LegajoExtractionFixtures.BienesPersonales();
            if (!clean)
                LegajoJsonPointer.Set(input, "/totals/totalBienes/value", 1_900_000m);

            var run = new LegajoExtraction
            {
                ExtractionId   = Guid.NewGuid(),
                TenantId       = document.TenantId,
                PersonLegajoId = legajo.PersonLegajoId,
                DocumentId     = document.DocumentId,
                Kind           = LegajoExtractionKinds.Extraction,
                DocumentType   = DocumentTypes.BienesPersonales,
                SchemaVersion  = "1.0",
                PromptVersion  = "bp-1.0",
                Model          = "claude-sonnet-4-6",
                ReadMode       = LegajoReadModes.Document,
                Status         = LegajoExtractionStatuses.Succeeded,
                ResultJson     = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input).ToJsonString(),
                Confidence     = 0.95m,
                IsCurrent      = true,
                CreatedAt      = DateTime.UtcNow.AddHours(-1)
            };
            Legajos.Runs.Add(run);
            return (document, run);
        }

        /// <summary>A document of <paramref name="documentType"/> with a current extraction of its clean fixture, optionally mutated.</summary>
        public (Document Document, LegajoExtraction Run) ExtractedOf(
            string documentType, Action<JsonObject>? mutate = null, string status = LegajoStatuses.NeedsReview)
        {
            var schema   = LegajoExtractionPrompts.For(documentType)!;
            var legajo   = Legajos.AddLegajo(consent: true);
            var document = Legajos.AddDocument(legajo, documentType, status);
            document.PeriodEnd = new DateTime(2025, 12, 31);

            var run = new LegajoExtraction
            {
                ExtractionId   = Guid.NewGuid(),
                TenantId       = document.TenantId,
                PersonLegajoId = legajo.PersonLegajoId,
                DocumentId     = document.DocumentId,
                Kind           = LegajoExtractionKinds.Extraction,
                DocumentType   = documentType,
                SchemaVersion  = schema.SchemaVersion,
                PromptVersion  = schema.PromptVersion,
                Model          = "claude-sonnet-4-6",
                ReadMode       = LegajoReadModes.Document,
                Status         = LegajoExtractionStatuses.Succeeded,
                ResultJson     = LegajoExtractionFixtures.NormalizedClean(documentType, mutate).ToJsonString(),
                Confidence     = 0.95m,
                IsCurrent      = true,
                CreatedAt      = DateTime.UtcNow.AddHours(-1)
            };
            Legajos.Runs.Add(run);
            return (document, run);
        }

        public Task<IActionResult> CorrectAsync(Document document, string path, string valueJson, string reason = "Leido del original.")
        {
            using var value = JsonDocument.Parse(valueJson);
            return Sut.CorrectAsync(document.DocumentId,
                new LegajoCorrectionRequest { FieldPath = path, Value = value.RootElement.Clone(), Reason = reason },
                CancellationToken.None);
        }
    }

    private static LegajoExtractionViewResponse View(IActionResult result)
        => Assert.IsType<LegajoExtractionViewResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static void Status(IActionResult result, int status)
        => Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);

    // ── GET ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_EffectiveView_WithQueuesAndChecks_NeverTheHolderCuit()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();

        var view = View(await h.Sut.GetAsync(document.DocumentId, CancellationToken.None));

        Assert.Equal(run.ExtractionId, view.ExtractionId);
        Assert.Equal("bp-1.0", view.PromptVersion);
        Assert.True(view.HolderCuitMatches);
        Assert.Equal("Pesos", view.AmountsUnit);
        Assert.False(view.Checks.Passed);
        Assert.Contains(view.Flagged, f => f.FieldPath == "/totals/totalBienes"
                                        && f.Reasons.Contains(LegajoExtractionAssessment.ReasonCheckFailed));
        Assert.Empty(view.Missing);

        var total = Assert.Single(view.Fields, f => f.FieldPath == "/totals/totalBienes");
        Assert.Equal(1, total.Page);
        Assert.Equal(0.95m, total.Confidence);
        Assert.Equal(LegajoReadModes.Document, total.ReadMode);
        Assert.False(total.Corrected);

        var rowValue = Assert.Single(view.Fields, f => f.FieldPath == "/items/1/fiscalValue");
        Assert.Equal(2, rowValue.Page);   // the row's provenance
        Assert.Contains(view.Fields, f => f.FieldPath == "/items/0/matchKey/value");

        Assert.DoesNotContain(view.Fields, f => f.FieldPath.StartsWith(LegajoExtractionVocabulary.HolderCuitPath, StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, json);
        Assert.DoesNotContain(LegajoTestData.LegajoCuit, json);
    }

    [Fact]
    public async Task Get_NoExtraction_404_OtherTenant_404()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var classifiedOnly = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.ReadyForExtraction);
        Status(await h.Sut.GetAsync(classifiedOnly.DocumentId, CancellationToken.None), 404);

        var (document, _) = h.Extracted();
        var other = new Harness(LegajoTestData.OtherTenant);
        other.Legajos.Documents.AddRange(h.Legajos.Documents);
        other.Legajos.Legajos.AddRange(h.Legajos.Legajos);
        other.Legajos.Runs.AddRange(h.Legajos.Runs);
        Status(await other.Sut.GetAsync(document.DocumentId, CancellationToken.None), 404);
    }

    // ── Phase 3a: a DDJJ IVA end to end ─────────────────────────────────────────

    [Fact]
    public async Task Iva_View_FlagsThePrintedTotals_CorrectionRecomputes_ThenReviewWithoutReason()
    {
        var h = new Harness();
        // The model misread the débito fiscal of 2025-11: its lines add up to 230.000, and so does the saldo técnico.
        var (document, run) = h.ExtractedOf(DocumentTypes.DdjjIva,
            i => LegajoJsonPointer.Set(i, "/periods/0/debitoFiscal", 240_000m));

        var view = View(await h.Sut.GetAsync(document.DocumentId, CancellationToken.None));

        Assert.Equal("iva-1.0", view.PromptVersion);
        Assert.True(view.HolderCuitMatches);
        Assert.Equal([LegajoIntegrityService.IvaDebitoLinesCheck, LegajoIntegrityService.IvaSaldoTecnicoCheck],
                     view.Checks.Failed.Select(c => c.Name));
        // Only the printed totals are flagged; the débito lines stay in the check's fieldPaths.
        Assert.Equal(["/periods/0/debitoFiscal", "/periods/0/saldoTecnico/amount", "/periods/0/saldoTecnico/side"],
                     view.Flagged.Select(f => f.FieldPath));
        Assert.Contains("/alicuotaLines/0/impuesto", view.Checks.Failed.First().FieldPaths);

        // Every leaf of the new schema is emitted, with its row's provenance, and correctable.
        var period = Assert.Single(view.Fields, f => f.FieldPath == "/periods/0/period");
        Assert.Equal("2025-11", LegajoJsonValues.String(period.Value));
        Assert.True(period.Correctable);
        Assert.True(Assert.Single(view.Fields, f => f.FieldPath == "/periods/0/saldoTecnico/side").Correctable);
        Assert.DoesNotContain(view.Fields, f => f.FieldPath.StartsWith(LegajoExtractionVocabulary.HolderCuitPath, StringComparison.Ordinal));

        var json = JsonSerializer.Serialize(view);
        Assert.Contains("\"flaggedPaths\":[\"/periods/0/debitoFiscal\"]", json);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, json);

        // A malformed period is a 400 that quotes the path, never the value.
        var bad = Assert.IsAssignableFrom<ObjectResult>(await h.CorrectAsync(document, "/periods/1/period", "\"12-2025\""));
        Assert.Equal(400, bad.StatusCode);
        Assert.DoesNotContain("12-2025", JsonSerializer.Serialize(bad.Value));

        // The analyst corrects the débito: both checks pass again, nothing is flagged, no override needed.
        var corrected = View(await h.CorrectAsync(document, "/periods/0/debitoFiscal", "230000"));
        Assert.True(corrected.Checks.Passed);
        Assert.Empty(corrected.Flagged);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Contains("240000", run.ResultJson);   // the run itself is never edited

        View(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None));
        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Contains("\"override\":false", Assert.Single(h.Audit.Entries, e => e.Action == "Document.ExtractionReviewed").NewJson);
    }

    [Fact]
    public async Task Monotributo_MissingCategoriaAndPayments_ReviewNeedsAReason()
    {
        var h = new Harness();
        var (document, _) = h.ExtractedOf(DocumentTypes.Monotributo, i =>
        {
            LegajoJsonPointer.Set(i, "/header/categoria/value", null);
            i["payments"] = new JsonArray();
        });

        var view = View(await h.Sut.GetAsync(document.DocumentId, CancellationToken.None));
        Assert.Equal(["/header/categoria"], view.Missing);

        Status(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None), 400);

        var fixedView = View(await h.CorrectAsync(document, "/header/categoria", "\"D\""));
        Assert.Empty(fixedView.Missing);
    }

    // ── Corrections ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Correct_AppendsOneRow_Audits_WithoutValues_AndRecomputesTheChecks()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();

        var view = View(await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50"));

        var correction = Assert.Single(h.Legajos.Corrections);
        Assert.Equal(run.ExtractionId, correction.ExtractionId);
        Assert.Equal("1900000", correction.OriginalValueJson);
        Assert.Equal("1700000.50", correction.CorrectedValueJson);
        Assert.Equal(h.Users.Analyst.UserId, correction.CorrectedBy);

        Assert.True(view.Checks.Passed);
        Assert.Empty(view.Flagged);
        var total = Assert.Single(view.Fields, f => f.FieldPath == "/totals/totalBienes");
        Assert.True(total.Corrected);
        Assert.Equal("Analista Sintetico", Assert.Single(total.History).CorrectedByName);

        // The run itself is never edited.
        Assert.Contains("1900000", run.ResultJson);

        var audit = Assert.Single(h.Audit.Entries, e => e.Action == "Document.ExtractionCorrected");
        Assert.Contains("/totals/totalBienes", audit.NewJson);
        Assert.DoesNotContain("1700000", audit.NewJson);
        Assert.DoesNotContain("1900000", audit.NewJson);
    }

    [Fact]
    public async Task Correct_Twice_BothKept_LatestWins()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        await h.CorrectAsync(document, "/items/1/fiscalValue", "260000");
        var view = View(await h.CorrectAsync(document, "/items/1/fiscalValue", "250000", reason: "Segunda lectura."));

        Assert.Equal(2, h.Legajos.Corrections.Count);
        var field = Assert.Single(view.Fields, f => f.FieldPath == "/items/1/fiscalValue");
        Assert.Equal(250_000m, LegajoJsonValues.Decimal(field.Value));
        Assert.Equal(2, field.History.Count);
        Assert.Equal(2, view.CorrectionsCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc ")]
    public async Task Correct_ReasonRequired_400(string reason)
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        Status(await h.CorrectAsync(document, "/totals/totalBienes", "1", reason), 400);
        Assert.Empty(h.Legajos.Corrections);
    }

    [Fact]
    public async Task Correct_ReasonOrPathBeyondTheColumns_400()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        Status(await h.CorrectAsync(document, "/totals/totalBienes", "1", new string('x', 1001)), 400);
        Status(await h.CorrectAsync(document, "/" + new string('a', 300), "1"), 400);
        Assert.Empty(h.Legajos.Corrections);
    }

    [Theory]
    [InlineData("/totals/nope", "1")]
    [InlineData("/totals/totalBienes", "\"uno\"")]
    [InlineData("/header/holderCuit", "\"27112233445\"")]
    public async Task Correct_BadPathOrType_400(string path, string json)
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        Status(await h.CorrectAsync(document, path, json), 400);
        Assert.Empty(h.Legajos.Corrections);
    }

    [Theory]
    [InlineData(LegajoStatuses.Extracting)]
    [InlineData(LegajoStatuses.Failed)]
    public async Task Correct_OnlyInNeedsReviewOrReady_409(string status)
    {
        var h = new Harness();
        var (document, _) = h.Extracted(status);

        Status(await h.CorrectAsync(document, "/totals/totalBienes", "1"), 409);
    }

    [Fact]
    public async Task Correct_ReadyDocument_BackToNeedsReview_ReviewStampCleared()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(LegajoStatuses.Ready, clean: true);
        run.ReviewedBy = h.Users.Analyst.UserId;
        run.ReviewedAt = DateTime.UtcNow.AddMinutes(-10);

        var view = View(await h.CorrectAsync(document, "/totals/impuestoDeterminado", "8600"));

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Equal(LegajoStatuses.NeedsReview, view.LegajoStatus);
        Assert.Null(run.ReviewedAt);
        Assert.Null(view.ReviewedAt);
    }

    // ── Review ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Review_WithAFailedCheck_RequiresAReason_ThenIsAnAuditedOverride()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();

        Status(await h.Sut.ReviewAsync(document.DocumentId, new LegajoReviewRequest(), CancellationToken.None), 400);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);

        var view = View(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { Reason = "El total impreso esta mal sumado en el original." }, CancellationToken.None));

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal(h.Users.Analyst.UserId, run.ReviewedBy);
        Assert.NotNull(view.ReviewedAt);
        Assert.Equal("Analista Sintetico", view.ReviewedByName);

        var audit = Assert.Single(h.Audit.Entries, e => e.Action == "Document.ExtractionReviewed");
        Assert.Contains("\"override\":true", audit.NewJson);
        Assert.Contains("mal sumado", audit.NewJson);
    }

    [Fact]
    public async Task Review_AfterTheCorrectionFixedIt_NoReasonNeeded()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();
        await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50");

        View(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None));

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Contains("\"override\":false", Assert.Single(h.Audit.Entries, e => e.Action == "Document.ExtractionReviewed").NewJson);
    }

    [Fact]
    public async Task Review_MissingRequiredField_RequiresAReason()
    {
        var h = new Harness();
        var (document, _) = h.Extracted(clean: true);
        await h.CorrectAsync(document, "/header/fiscalYear", "null");

        Status(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None), 400);
    }

    [Fact]
    public async Task Review_ReasonOver1000Characters_400_NotApproved()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();

        Status(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { Reason = new string('m', 1001) }, CancellationToken.None), 400);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Null(run.ReviewedAt);

        View(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { Reason = new string('m', 1000) }, CancellationToken.None));
        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
    }

    [Fact]
    public async Task Correct_TextOverItsMaxLength_400_NothingStored()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        Status(await h.CorrectAsync(document, "/items/1/description", "\"" + new string('x', 201) + "\""), 400);
        Assert.Empty(h.Legajos.Corrections);
    }

    [Fact]
    public async Task Review_OnlyFromNeedsReview_409()
    {
        var h = new Harness();
        var (document, _) = h.Extracted(LegajoStatuses.Ready, clean: true);

        Status(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None), 409);
    }

    // ── Blocked ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Blocked_Get_Correct_Review_All404_ExtractionUnavailable_NothingWritten()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(LegajoStatuses.Blocked);

        var get     = await h.Sut.GetAsync(document.DocumentId, CancellationToken.None);
        var correct = await h.CorrectAsync(document, "/totals/totalBienes", "1");
        var review  = await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { Reason = "Motivo suficiente para aprobar." }, CancellationToken.None);

        foreach (var result in new[] { get, correct, review })
        {
            Status(result, 404);
            Assert.Equal("extraction_unavailable", ErrorCode(result));
        }

        Assert.Empty(h.Legajos.Corrections);
        Assert.Null(run.ReviewedAt);
        Assert.Equal(LegajoStatuses.Blocked, document.LegajoStatus);
    }

    // ── Arithmetic ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Correct_HugeAmount_400()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        var result = await h.CorrectAsync(document, "/totals/totalBienes", "1e16");

        Status(result, 400);
        Assert.Empty(h.Legajos.Corrections);
    }

    [Fact]
    public async Task Get_StoredFiguresThatOverflow_200_WithArithmeticError()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(clean: true);
        var tree = System.Text.Json.Nodes.JsonNode.Parse(run.ResultJson)!.AsObject();
        LegajoJsonPointer.Set(tree, "/header/amountsUnit/value", "Millones");
        LegajoJsonPointer.Set(tree, "/totals/totalBienes/value", 1e26m);   // hand-edited: past the reader's bound
        run.ResultJson = tree.ToJsonString();

        var view = View(await h.Sut.GetAsync(document.DocumentId, CancellationToken.None));

        Assert.Contains(view.Checks.Checks, c => c is { Name: LegajoIntegrityService.ArithmeticErrorCheck, Passed: false });
        Assert.False(view.Checks.Passed);
    }

    // ── Race: correction vs review ─────────────────────────────────────────────

    private static string ErrorCode(IActionResult result)
        => Assert.IsAssignableFrom<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value)
               .Extensions["errorCode"]!.ToString()!;

    [Fact]
    public async Task Race_CorrectionLandsBetweenTheReviewsReadAndItsApproval_ReviewLoses409()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(clean: true);   // nothing to override: no reason needed

        // Another analyst's correction commits after the review evaluated zero corrections.
        h.Legajos.BeforeApprove = () => h.Legajos.Corrections.Add(new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = document.TenantId, ExtractionId = run.ExtractionId,
            FieldPath = "/totals/impuestoDeterminado", CorrectedValueJson = "9000", Reason = "Otro analista",
            CorrectedBy = h.Users.Analyst.UserId, CorrectedAt = DateTime.UtcNow
        });

        var result = await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None);

        Status(result, 409);
        Assert.Equal("extraction_changed", ErrorCode(result));
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Null(run.ReviewedAt);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Action == "Document.ExtractionReviewed");
    }

    [Fact]
    public async Task Race_ApprovalLandsBetweenTheCorrectionsReadAndItsCommit_CorrectionReopens()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(clean: true);

        // The correction read NeedsReview; an approval commits before the correction does.
        h.Legajos.BeforeCorrectionCommit = () =>
        {
            document.LegajoStatus = LegajoStatuses.Ready;
            run.ReviewedBy = h.Users.Analyst.UserId;
            run.ReviewedAt = DateTime.UtcNow;
        };

        var view = View(await h.CorrectAsync(document, "/totals/impuestoDeterminado", "9000"));

        Assert.Single(h.Legajos.Corrections);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Null(run.ReviewedBy);
        Assert.Null(run.ReviewedAt);
        Assert.Equal(LegajoStatuses.NeedsReview, view.LegajoStatus);
        Assert.Null(view.ReviewedAt);
    }

    [Fact]
    public async Task Review_RunReplacedMeanwhile_Loses409()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(clean: true);
        h.Legajos.BeforeApprove = () => run.IsCurrent = false;

        var result = await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None);

        Assert.Equal("extraction_changed", ErrorCode(result));
    }

    [Fact]
    public async Task Names_OnlyUsersOfTheTenant()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();
        h.Legajos.Corrections.Add(new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, ExtractionId = run.ExtractionId,
            FieldPath = "/totals/totalBienes", CorrectedValueJson = "1700000.5", Reason = "Corregido afuera",
            CorrectedBy = h.Users.Foreigner.UserId, CorrectedAt = DateTime.UtcNow
        });

        var view = View(await h.Sut.GetAsync(document.DocumentId, CancellationToken.None));

        var history = Assert.Single(Assert.Single(view.Fields, f => f.FieldPath == "/totals/totalBienes").History);
        Assert.Null(history.CorrectedByName);
    }

    // ── Contract follow-ups (Portal #326) ───────────────────────────────────────

    private static ProblemDetails ProblemOf(IActionResult result, int status)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task Correct_NotEditableStatus_409_ExtractionNotEditable_DetailUnchanged()
    {
        var h = new Harness();
        var (document, _) = h.Extracted(LegajoStatuses.Failed);

        var problem = ProblemOf(await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50"), 409);

        Assert.Equal("Conflict", problem.Title);
        Assert.Equal("Solo se corrige la extracción de un documento en estado NeedsReview o Ready.", problem.Detail);
        Assert.Equal("extraction_not_editable", problem.Extensions["errorCode"]);
        Assert.Empty(h.Legajos.Corrections);
    }

    [Fact]
    public async Task Review_NotNeedsReview_409_ExtractionNotReviewable_DetailUnchanged()
    {
        var h = new Harness();
        var (document, _) = h.Extracted(LegajoStatuses.Ready, clean: true);

        var problem = ProblemOf(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None), 409);

        Assert.Equal("Conflict", problem.Title);
        Assert.Equal("Solo se aprueba la extracción de un documento en estado NeedsReview.", problem.Detail);
        Assert.Equal("extraction_not_reviewable", problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task Review_StaleExtractionId_409_ExtractionChanged_NothingApproved()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(clean: true);

        var problem = ProblemOf(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { ExtractionId = Guid.NewGuid(), CorrectionsCount = 0 }, CancellationToken.None), 409);

        Assert.Equal("extraction_changed", problem.Extensions["errorCode"]);
        Assert.Equal("La extracción cambió mientras revisabas. Recargá y volvé a revisar.", problem.Detail);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Null(run.ReviewedAt);
        Assert.DoesNotContain(h.Audit.Entries, e => e.Action == "Document.ExtractionReviewed");
    }

    [Fact]
    public async Task Review_StaleCorrectionsCount_409_ExtractionChanged_NothingApproved()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();
        await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50");   // the analyst's screen predates this

        var problem = ProblemOf(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { ExtractionId = run.ExtractionId, CorrectionsCount = 0 }, CancellationToken.None), 409);

        Assert.Equal("extraction_changed", problem.Extensions["errorCode"]);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Null(run.ReviewedAt);
    }

    [Fact]
    public async Task Review_Stale_WithAFailedCheckAndNoReason_Is409_NotTheReason400()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();   // one failed check: an override would need a reason

        var problem = ProblemOf(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { CorrectionsCount = 3 }, CancellationToken.None), 409);

        Assert.Equal("extraction_changed", problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task Review_MatchingExtractionIdAndCorrectionsCount_200_Ready()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();
        await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50");

        var view = View(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { ExtractionId = run.ExtractionId, CorrectionsCount = 1 }, CancellationToken.None));

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal(LegajoStatuses.Ready, view.LegajoStatus);
        Assert.NotNull(run.ReviewedAt);
    }

    [Fact]
    public async Task Get_Fields_CarryCorrectable()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        var view = View(await h.Sut.GetAsync(document.DocumentId, CancellationToken.None));

        Assert.True(Assert.Single(view.Fields, f => f.FieldPath == "/totals/totalBienes").Correctable);
        Assert.True(Assert.Single(view.Fields, f => f.FieldPath == "/items/1/fiscalValue").Correctable);
    }

    // ── Phase 3b: the approval override, and the reconciliation triggers ────────

    [Fact]
    public async Task Review_Override_IsPersistedAndShown_AndRebuildsTheLegajo()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();

        var view = View(await h.Sut.ReviewAsync(document.DocumentId,
            new LegajoReviewRequest { Reason = "  El total impreso esta mal sumado.  " }, CancellationToken.None));

        Assert.True(run.ReviewOverride);
        Assert.Equal("El total impreso esta mal sumado.", run.ReviewReason);
        Assert.True(view.ReviewedWithOverride);
        Assert.Equal("El total impreso esta mal sumado.", view.ReviewReason);
        Assert.Equal((document.PersonLegajoId!.Value, LegajoTestData.Tenant, "approval"), Assert.Single(h.Reconciliation.Triggers));
    }

    [Fact]
    public async Task Review_Plain_IsNotAnOverride_AndItsOptionalReasonIsKept()
    {
        var h = new Harness();
        var (document, run) = h.Extracted();
        await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50");

        var view = View(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None));

        Assert.False(run.ReviewOverride);
        Assert.Null(run.ReviewReason);
        Assert.False(view.ReviewedWithOverride);
        Assert.Null(view.ReviewReason);
        Assert.Single(h.Reconciliation.Triggers, t => t.Trigger == "approval");
    }

    [Fact]
    public async Task Summary_CarriesTheOverride_OnlyWhileApproved()
    {
        var h = new Harness();
        var (_, run) = h.Extracted();
        run.ReviewOverride = true;
        run.ReviewReason   = "Motivo de la aprobacion";

        var evaluated = LegajoExtractionReview.Evaluate(run, [], new LegajoIntegrityService(), null, LegajoTestData.LegajoCuit, 0.7m)!;
        Assert.Null(LegajoExtractionReview.Summarize(evaluated).ReviewedWithOverride);   // not approved (no ReviewedAt)

        run.ReviewedAt = DateTime.UtcNow;
        var summary = LegajoExtractionReview.Summarize(evaluated);
        Assert.True(summary.ReviewedWithOverride);
        Assert.Equal("Motivo de la aprobacion", summary.ReviewReason);
    }

    [Fact]
    public async Task Correct_ReopeningAReadyDocument_ClearsTheOverride_AndRebuildsTheLegajo()
    {
        var h = new Harness();
        var (document, run) = h.Extracted(LegajoStatuses.Ready, clean: true);
        run.ReviewedBy     = h.Users.Analyst.UserId;
        run.ReviewedAt     = DateTime.UtcNow.AddMinutes(-10);
        run.ReviewOverride = true;
        run.ReviewReason   = "Motivo anterior";

        var view = View(await h.CorrectAsync(document, "/totals/impuestoDeterminado", "8600"));

        Assert.Null(run.ReviewOverride);
        Assert.Null(run.ReviewReason);
        Assert.Null(view.ReviewedWithOverride);
        Assert.Null(view.ReviewReason);
        Assert.Equal((document.PersonLegajoId!.Value, LegajoTestData.Tenant, "correction"), Assert.Single(h.Reconciliation.Triggers));
    }

    [Fact]
    public async Task Correct_OfANeedsReviewDocument_DoesNotRebuild()
    {
        var h = new Harness();
        var (document, _) = h.Extracted();

        View(await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50"));

        Assert.Empty(h.Reconciliation.Triggers);
    }

    [Fact]
    public async Task Review_AFailingRebuild_NeverFailsTheApproval()
    {
        // The real service over a store that throws on save: TryRebuildAsync swallows it.
        var h = new Harness(reconciliation: legajos => new LegajoReconciliationService(
            legajos, new ThrowingReconciliationRepository(), new LegajoIntegrityService(),
            Options.Create(new LegajoOptions()), NullLogger<LegajoReconciliationService>.Instance));
        var (document, _) = h.Extracted();
        await h.CorrectAsync(document, "/totals/totalBienes", "1700000.50");

        var view = View(await h.Sut.ReviewAsync(document.DocumentId, null, CancellationToken.None));

        Assert.Equal(LegajoStatuses.Ready, view.LegajoStatus);
        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
    }
}
