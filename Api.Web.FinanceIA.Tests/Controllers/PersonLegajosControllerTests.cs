// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// <see cref="PersonLegajosController"/>: recording and revoking the data-processing authorization
/// (and what that does to the queue), and the two read models — including that neither response ever
/// carries the person's CUIT or name, nor a third party's holder CUIT. Synthetic data only.
/// </summary>
public sealed class PersonLegajosControllerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Harness
    {
        public FakeLegajoDocumentRepository Legajos { get; } = new();
        public StubUserRepository Users { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public RecordingPipelineQueue Queue { get; } = new();
        public StubPersonExpectedDocumentsService ExpectedDocuments { get; } = new();
        public PersonLegajosController Sut { get; }

        public Harness()
        {
            Sut = new PersonLegajosController(
                new StubTenantContextAccessor(),
                Legajos,
                new ListDocumentRepository(Legajos.Documents),
                Users,
                Audit,
                Queue,
                new Api.Web.FinanceIA.Servicios.LegajoIntegrityService(),
                null!,   // the legajo-scoped refreshes: PersonLegajoSnapshotRefreshTests
                ExpectedDocuments,
                null!,   // IConfiguration: read by refresh-arca only
                Microsoft.Extensions.Options.Options.Create(new Api.Web.FinanceIA.Models.LegajoOptions()),
                NullLogger<PersonLegajosController>.Instance)
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
    }

    private static T OkValue<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static void AssertStatus(IActionResult result, int status)
        => Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);

    // ── Record consent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordConsent_SetsTheColumns_Audits_AndQueuesEveryUploadedDocument()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: false);
        var authorization = h.Legajos.AddDocument(legajo, DocumentTypes.AutorizacionDatos, LegajoStatuses.Ready);
        var waitingA = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Uploaded);
        var waitingB = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.Uploaded);
        var failed   = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIibb, LegajoStatuses.Failed);

        var response = OkValue<PersonLegajoResponse>(await h.Sut.RecordConsentAsync(
            legajo.PersonLegajoId, new RecordConsentRequest { DocumentId = authorization.DocumentId }, CancellationToken.None));

        Assert.NotNull(legajo.ConsentRecordedAt);
        Assert.Equal(authorization.DocumentId, legajo.ConsentDocumentId);
        Assert.Equal(h.Users.Analyst.UserId, legajo.ConsentRecordedBy);

        Assert.Equal(authorization.DocumentId, response.Consent!.DocumentId);
        Assert.Equal("Analista Sintetico", response.Consent.RecordedByName);
        Assert.True(response.Consent.IsActive);

        Assert.Equal(
            new[] { waitingA.DocumentId, waitingB.DocumentId }.Order(),
            h.Queue.Items.Select(i => i.DocumentId).Order());
        Assert.Equal(LegajoStatuses.Queued, waitingA.LegajoStatus);
        Assert.Equal(LegajoStatuses.Failed, failed.LegajoStatus);          // not touched
        Assert.Equal(LegajoStatuses.Ready, authorization.LegajoStatus);    // never queued

        Assert.Contains(h.Audit.Entries, e => e.Action == "PersonLegajo.ConsentRecorded"
                                           && e.EntityId == legajo.PersonLegajoId.ToString());
    }

    [Fact]
    public async Task RecordConsent_AlsoQueuesReadyForExtraction_WithoutReclassifyingThem()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: false);
        var authorization = h.Legajos.AddDocument(legajo, DocumentTypes.AutorizacionDatos, LegajoStatuses.Ready);
        var confirmed = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.ReadyForExtraction);

        await h.Sut.RecordConsentAsync(
            legajo.PersonLegajoId, new RecordConsentRequest { DocumentId = authorization.DocumentId }, CancellationToken.None);

        Assert.Equal(confirmed.DocumentId, Assert.Single(h.Queue.Items).DocumentId);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, confirmed.LegajoStatus);   // not sent back to classification
    }

    [Fact]
    public async Task RecordConsent_DocumentOfAnotherLegajo_400()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var other = h.Legajos.AddLegajo();
        var authorization = h.Legajos.AddDocument(other, DocumentTypes.AutorizacionDatos, LegajoStatuses.Ready);

        AssertStatus(await h.Sut.RecordConsentAsync(legajo.PersonLegajoId,
            new RecordConsentRequest { DocumentId = authorization.DocumentId }, CancellationToken.None), 400);
        Assert.Null(legajo.ConsentRecordedAt);
    }

    [Fact]
    public async Task RecordConsent_NotAnAuthorization_400()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var ganancias = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Uploaded);

        AssertStatus(await h.Sut.RecordConsentAsync(legajo.PersonLegajoId,
            new RecordConsentRequest { DocumentId = ganancias.DocumentId }, CancellationToken.None), 400);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task RecordConsent_DeletedAuthorization_400()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var authorization = h.Legajos.AddDocument(legajo, DocumentTypes.AutorizacionDatos, LegajoStatuses.Ready);
        authorization.IsDeleted = true;

        AssertStatus(await h.Sut.RecordConsentAsync(legajo.PersonLegajoId,
            new RecordConsentRequest { DocumentId = authorization.DocumentId }, CancellationToken.None), 400);
    }

    [Fact]
    public async Task RecordConsent_UnknownLegajo_404()
    {
        var h = new Harness();

        AssertStatus(await h.Sut.RecordConsentAsync(Guid.NewGuid(),
            new RecordConsentRequest { DocumentId = Guid.NewGuid() }, CancellationToken.None), 404);
    }

    // ── Revoke consent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeConsent_ClearsTheColumns_Audits_AndUnqueuesWaitingDocuments()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        legajo.ConsentDocumentId = Guid.NewGuid();
        var queued = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Queued);
        var ready  = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.ReadyForExtraction);

        var response = OkValue<PersonLegajoResponse>(await h.Sut.RevokeConsentAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Null(response.Consent);
        Assert.Null(legajo.ConsentRecordedAt);
        Assert.Null(legajo.ConsentDocumentId);
        Assert.Null(legajo.ConsentRecordedBy);
        Assert.Equal(LegajoStatuses.Uploaded, queued.LegajoStatus);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, ready.LegajoStatus);   // existing runs stay
        Assert.Contains(h.Audit.Entries, e => e.Action == "PersonLegajo.ConsentRevoked");
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_Shape_ChecklistWithSatisfiers_NoIdentity()
    {
        var h = new Harness();
        var entityId = Guid.NewGuid();
        var legajo = h.Legajos.AddLegajo(consent: false, entityId: entityId);
        legajo.MonotributoCategory = "C";
        var reibp   = h.Legajos.AddDocument(legajo, DocumentTypes.Reibp, LegajoStatuses.ReadyForExtraction);
        var blocked = h.Legajos.AddDocument(legajo, DocumentTypes.Monotributo, LegajoStatuses.Blocked);

        var response = OkValue<PersonLegajoResponse>(await h.Sut.GetAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(entityId, response.EntityId);
        Assert.Equal(FiscalRegimes.Monotributo, response.FiscalRegime);
        Assert.Equal("C", response.MonotributoCategory);
        Assert.Null(response.Consent);

        var bienes = response.ExpectedDocuments.Single(e => e.DocumentType == DocumentTypes.BienesPersonales);
        Assert.True(bienes.Required);
        Assert.Equal([DocumentTypes.Reibp], bienes.Alternatives);
        Assert.Equal([reibp.DocumentId], bienes.SatisfiedByDocumentIds);

        // Another person's document satisfies nothing.
        Assert.Empty(response.ExpectedDocuments.Single(e => e.DocumentType == DocumentTypes.Monotributo).SatisfiedByDocumentIds);
        Assert.DoesNotContain(response.ExpectedDocuments, e => e.SatisfiedByDocumentIds.Contains(blocked.DocumentId));

        var json = JsonSerializer.Serialize(response, Web);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, json);
        Assert.DoesNotContain(LegajoTestData.LegajoCuit, json);
        Assert.DoesNotContain("PRUEBA", json);
    }

    [Fact]
    public async Task Get_WithoutATenantMatrix_IsTheDefaultChecklist_AndSaysSo()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: false);

        var response = OkValue<PersonLegajoResponse>(await h.Sut.GetAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(ExpectedDocumentsSources.Default, response.ExpectedDocumentsSource);
        Assert.Equal(
            LegajoExpectedDocuments.For(legajo.FiscalRegime).Select(l => l.DocumentType),
            response.ExpectedDocuments.Select(e => e.DocumentType));
        Assert.Contains("\"expectedDocumentsSource\":\"default\"", JsonSerializer.Serialize(response, Web));
    }

    [Fact]
    public async Task Get_FollowsTheTenantMatrix_ForTheLegajosRegime()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: false);
        legajo.FiscalRegime = FiscalRegimes.Monotributo;
        var ganancias = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Ready);
        h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIibb, LegajoStatuses.Ready);
        h.ExpectedDocuments.Resolved = new PersonExpectedDocumentsResolved
        {
            UpdatedAt = DateTime.UtcNow,
            Regimes   =
            [
                new PersonExpectedDocumentsForRegime
                {
                    Regime = ExpectedDocumentRegimes.Monotributo,
                    Source = ExpectedDocumentsSources.Tenant,
                    Lines  =
                    [
                        new(DocumentTypes.AutorizacionDatos, true, []),
                        new(DocumentTypes.Monotributo, true, []),
                        new(DocumentTypes.BienesPersonales, false, [DocumentTypes.Ganancias], MaxAgeMonths: 24)
                    ]
                },
                .. PersonExpectedDocumentsResolved.Defaults.Regimes.Skip(1)
            ]
        };

        var response = OkValue<PersonLegajoResponse>(await h.Sut.GetAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal(ExpectedDocumentsSources.Tenant, response.ExpectedDocumentsSource);
        Assert.Equal(
            [DocumentTypes.AutorizacionDatos, DocumentTypes.Monotributo, DocumentTypes.BienesPersonales],
            response.ExpectedDocuments.Select(e => e.DocumentType));
        var bienes = response.ExpectedDocuments.Single(e => e.DocumentType == DocumentTypes.BienesPersonales);
        Assert.False(bienes.Required);
        Assert.Equal([ganancias.DocumentId], bienes.SatisfiedByDocumentIds);
        Assert.DoesNotContain(response.ExpectedDocuments, e => e.DocumentType == DocumentTypes.DdjjIibb);   // "no se pide"

        // Another regime of the same tenant still reads its default.
        legajo.FiscalRegime = FiscalRegimes.ResponsableInscripto;
        var ri = OkValue<PersonLegajoResponse>(await h.Sut.GetAsync(legajo.PersonLegajoId, CancellationToken.None));
        Assert.Equal(ExpectedDocumentsSources.Default, ri.ExpectedDocumentsSource);
        Assert.Equal(LegajoExpectedDocuments.ResponsableInscripto.Count, ri.ExpectedDocuments.Count);
    }

    [Fact]
    public async Task GetDocuments_Shape_NeverReturnsAHolderCuit()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var mine = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.ReadyForExtraction);
        mine.HolderCuit = LegajoTestData.LegajoCuit;
        mine.PageQualityJson = """[{"page":1,"chars":300,"digits":20,"suspiciousRatio":0,"verdict":"Good"},{"page":2,"chars":0,"digits":0,"suspiciousRatio":0,"verdict":"NoText"}]""";
        mine.PeriodEnd = new DateTime(2025, 12, 31);
        mine.PageCount = 2;
        mine.PdfType = PdfTypes.Mixed;
        var theirs = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Blocked);
        theirs.HolderCuit = "27-11223344-5";
        var waiting = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.Uploaded);

        await h.Legajos.InsertRunAsync(new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, PersonLegajoId = legajo.PersonLegajoId,
            DocumentId = theirs.DocumentId, Kind = LegajoExtractionKinds.Classification, DocumentType = DocumentTypes.Ganancias,
            Status = LegajoExtractionStatuses.Succeeded, IsCurrent = true, Confidence = 0.91m,
            ResultJson = """{"detectedType":"Ganancias","isComposite":false,"holderCuit":{"value":"27112233445"},"reviewReasons":["cuit_mismatch"]}"""
        }, CancellationToken.None);

        var items = OkValue<List<LegajoDocumentResponse>>(await h.Sut.GetDocumentsAsync(legajo.PersonLegajoId, CancellationToken.None));

        var mineItem = items.Single(i => i.DocumentId == mine.DocumentId);
        Assert.True(mineItem.HolderCuitMatches);
        Assert.Equal(new DateOnly(2025, 12, 31), mineItem.PeriodEnd);
        Assert.Equal(1, mineItem.PageQuality.Good);
        Assert.Equal(1, mineItem.PageQuality.NoText);
        Assert.Null(mineItem.Classification);
        Assert.Null(mineItem.CurrentExtraction);

        var theirsItem = items.Single(i => i.DocumentId == theirs.DocumentId);
        Assert.False(theirsItem.HolderCuitMatches);
        Assert.Equal(LegajoStatuses.Blocked, theirsItem.LegajoStatus);
        Assert.Equal("Ganancias", theirsItem.Classification!.DetectedType);
        Assert.Equal(0.91m, theirsItem.Classification.Confidence);
        Assert.Equal(["cuit_mismatch"], theirsItem.Classification.ReviewReasons);

        Assert.Null(items.Single(i => i.DocumentId == waiting.DocumentId).HolderCuitMatches);

        var json = JsonSerializer.Serialize(items, Web);
        Assert.DoesNotContain("27112233445", json);
        Assert.DoesNotContain("27-11223344-5", json);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, json);
        Assert.DoesNotContain(LegajoTestData.LegajoCuit, json);
        Assert.DoesNotContain("holderCuit\"", json);
    }

    [Fact]
    public async Task GetDocuments_CurrentExtraction_IsSummarizedOnTheEffectiveValues()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.NeedsReview);
        document.PeriodEnd = new DateTime(2025, 12, 31);

        var input = LegajoExtractionFixtures.BienesPersonales();
        Api.Web.FinanceIA.Servicios.Legajo.LegajoJsonPointer.Set(input, "/totals/totalBienes/value", 1_900_000m);
        var run = new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, PersonLegajoId = legajo.PersonLegajoId,
            DocumentId = document.DocumentId, Kind = LegajoExtractionKinds.Extraction, DocumentType = DocumentTypes.BienesPersonales,
            SchemaVersion = "1.0", PromptVersion = "bp-1.0", ReadMode = LegajoReadModes.Images,
            Status = LegajoExtractionStatuses.Succeeded, IsCurrent = true, Confidence = 0.9m,
            ResultJson = LegajoExtractionFixtures.Normalize(
                Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionPrompts.BienesPersonales, input).ToJsonString()
        };
        h.Legajos.Runs.Add(run);

        var before = OkValue<List<LegajoDocumentResponse>>(await h.Sut.GetDocumentsAsync(legajo.PersonLegajoId, CancellationToken.None))
            .Single(i => i.DocumentId == document.DocumentId).CurrentExtraction!;
        Assert.Equal(run.ExtractionId, before.ExtractionId);
        Assert.Equal(LegajoReadModes.Images, before.ReadMode);
        Assert.False(before.ChecksPassed);
        Assert.Equal(1, before.FailedChecks);
        Assert.True(before.FlaggedCount > 0);
        Assert.Equal(0, before.CorrectionsCount);

        h.Legajos.Corrections.Add(new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, ExtractionId = run.ExtractionId,
            FieldPath = "/totals/totalBienes", CorrectedValueJson = "1700000.50", Reason = "Leido del original.",
            CorrectedBy = h.Users.Analyst.UserId, CorrectedAt = DateTime.UtcNow
        });

        var after = OkValue<List<LegajoDocumentResponse>>(await h.Sut.GetDocumentsAsync(legajo.PersonLegajoId, CancellationToken.None))
            .Single(i => i.DocumentId == document.DocumentId).CurrentExtraction!;
        Assert.True(after.ChecksPassed);
        Assert.Equal(0, after.FlaggedCount);
        Assert.Equal(1, after.CorrectionsCount);
    }

    [Fact]
    public async Task GetDocuments_StoredFiguresThatOverflow_ListStill200()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.NeedsReview);
        var tree = LegajoExtractionFixtures.Normalize(
            Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonales());
        Api.Web.FinanceIA.Servicios.Legajo.LegajoJsonPointer.Set(tree, "/header/amountsUnit/value", "Millones");
        Api.Web.FinanceIA.Servicios.Legajo.LegajoJsonPointer.Set(tree, "/totals/totalBienes/value", 1e26m);
        h.Legajos.Runs.Add(new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, PersonLegajoId = legajo.PersonLegajoId,
            DocumentId = document.DocumentId, Kind = LegajoExtractionKinds.Extraction, DocumentType = DocumentTypes.BienesPersonales,
            SchemaVersion = "1.0", PromptVersion = "bp-1.0", ReadMode = LegajoReadModes.Document,
            Status = LegajoExtractionStatuses.Succeeded, IsCurrent = true, ResultJson = tree.ToJsonString()
        });

        var items = OkValue<List<LegajoDocumentResponse>>(await h.Sut.GetDocumentsAsync(legajo.PersonLegajoId, CancellationToken.None));

        var summary = items.Single(i => i.DocumentId == document.DocumentId).CurrentExtraction!;
        Assert.False(summary.ChecksPassed);
        Assert.True(summary.FailedChecks > 0);
    }

    [Fact]
    public async Task GetDocuments_BlockedDocument_HasNoCurrentExtraction()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.Blocked);
        var tree = LegajoExtractionFixtures.Normalize(
            Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionPrompts.BienesPersonales,
            LegajoExtractionFixtures.BienesPersonales(holderCuit: LegajoTestData.OtherCuitDigits));
        h.Legajos.Runs.Add(new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, PersonLegajoId = legajo.PersonLegajoId,
            DocumentId = document.DocumentId, Kind = LegajoExtractionKinds.Extraction, DocumentType = DocumentTypes.BienesPersonales,
            SchemaVersion = "1.0", PromptVersion = "bp-1.0", ReadMode = LegajoReadModes.Document,
            Status = LegajoExtractionStatuses.Succeeded, IsCurrent = true, ResultJson = tree.ToJsonString()
        });

        var items = OkValue<List<LegajoDocumentResponse>>(await h.Sut.GetDocumentsAsync(legajo.PersonLegajoId, CancellationToken.None));

        var item = items.Single(i => i.DocumentId == document.DocumentId);
        Assert.Equal(LegajoStatuses.Blocked, item.LegajoStatus);
        Assert.Null(item.CurrentExtraction);
    }

    [Fact]
    public async Task Get_ConsentRecordedAgainstAGoneAuthorization_IsShownInactive()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        h.Legajos.Get(legajo.ConsentDocumentId!.Value).IsDeleted = true;

        var response = OkValue<PersonLegajoResponse>(await h.Sut.GetAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.NotNull(response.Consent);
        Assert.False(response.Consent!.IsActive);
    }

    [Fact]
    public async Task Reads_OtherTenantsLegajo_404()
    {
        var h = new Harness();
        var foreign = h.Legajos.AddLegajo(tenantId: LegajoTestData.OtherTenant);

        AssertStatus(await h.Sut.GetAsync(foreign.PersonLegajoId, CancellationToken.None), 404);
        AssertStatus(await h.Sut.GetDocumentsAsync(foreign.PersonLegajoId, CancellationToken.None), 404);
    }

    [Fact]
    public void PageQualityCounts_BadJson_CountsNothing()
    {
        var counts = LegajoReadModel.PageQualityCounts("not json");

        Assert.Equal(0, counts.Good + counts.Suspect + counts.NoText);
    }

    // ── Contract follow-ups (Portal #326): additive fields and codes ────────────

    [Fact]
    public async Task RecordConsent_NoDocumentId_400_ValidationError_TitleAndDetailUnchanged()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: false);

        var result = Assert.IsAssignableFrom<ObjectResult>(await h.Sut.RecordConsentAsync(
            legajo.PersonLegajoId, new RecordConsentRequest { DocumentId = Guid.Empty }, CancellationToken.None));

        Assert.Equal(400, result.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(result.Value);
        Assert.Equal("Bad Request", problem.Title);
        Assert.Equal("Se requiere documentId.", problem.Detail);
        Assert.Equal("validation_error", problem.Extensions["errorCode"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(problem, Web));
        var error = Assert.Single(json.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("documentId", error.GetProperty("field").GetString());
        Assert.Equal("Se requiere documentId.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RecordConsent_NotAnAuthorization_400_ConsentDocumentInvalid_DetailUnchanged()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: false);
        var ganancias = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Uploaded);

        var result = Assert.IsAssignableFrom<ObjectResult>(await h.Sut.RecordConsentAsync(
            legajo.PersonLegajoId, new RecordConsentRequest { DocumentId = ganancias.DocumentId }, CancellationToken.None));

        Assert.Equal(400, result.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(result.Value);
        Assert.Equal("Bad Request", problem.Title);
        Assert.Equal(
            "El documento debe ser una autorización de tratamiento de datos (AutorizacionDatos) vigente de este legajo.",
            problem.Detail);
        Assert.Equal("consent_document_invalid", problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task GetDocuments_LastErrorCode_OnlyForFailed_AndOnlyAVocabularyCode()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var rateLimited = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Failed);
        rateLimited.LastExtractionError = "extraction_rate_limited";
        var freeText = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.Failed);
        freeText.LastExtractionError = "HttpRequestException";
        var recovered = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIibb, LegajoStatuses.Queued);
        recovered.LastExtractionError = "claude_timeout";   // left over from before the reprocess

        var items = OkValue<List<LegajoDocumentResponse>>(await h.Sut.GetDocumentsAsync(legajo.PersonLegajoId, CancellationToken.None));

        Assert.Equal("extraction_rate_limited", items.Single(i => i.DocumentId == rateLimited.DocumentId).LastErrorCode);
        Assert.Equal("unexpected", items.Single(i => i.DocumentId == freeText.DocumentId).LastErrorCode);
        Assert.Null(items.Single(i => i.DocumentId == recovered.DocumentId).LastErrorCode);

        var json = JsonSerializer.Serialize(items, Web);
        Assert.Contains("\"lastErrorCode\":\"extraction_rate_limited\"", json);
        Assert.DoesNotContain("HttpRequestException", json);
    }
}
