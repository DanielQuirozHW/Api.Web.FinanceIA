// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The extraction stage of <see cref="LegajoDocumentProcessor"/> (M10 Phase 2b), end to end over the
/// REAL extraction and classification services with a scripted HTTP handler: the claim by type, the
/// consent gate (before any call, and a revoke in flight), the two passes and which one is kept, both
/// runs stored with the IsCurrent swap, the outcomes (Ready, NeedsReview by each queue, Blocked), the
/// failures and their stage marker, recovery, and the hand-off from classification.
/// </summary>
public sealed class LegajoDocumentProcessorExtractionTests
{
    private sealed class Harness
    {
        public FakeLegajoDocumentRepository Repo { get; } = new();
        public InMemoryStorage Storage { get; } = new();
        public RecordingApiCallLogService ApiCalls { get; } = new();
        public RecordingPipelineQueue Queue { get; } = new();
        public ListDocumentRepository Documents { get; }
        public ScriptedHandler Handler { get; }
        public RecordingReconciliationService Reconciliation { get; } = new();
        public LegajoDocumentProcessor Sut { get; }

        public Harness(ScriptedHandler handler, int maxAttempts = 3, decimal fieldThreshold = 0.7m,
                       ILegajoIntegrityService? integrity = null)
        {
            Handler   = handler;
            Documents = new ListDocumentRepository(Repo.Documents);
            var messages = LegajoTestClients.Messages(handler, ApiCalls);

            Sut = new LegajoDocumentProcessor(
                Repo,
                Documents,
                Storage,
                new LegajoClassificationService(messages, new StubPageRenderer(),
                    Options.Create(new LegajoClassificationOptions { ApiKey = "test-key" }),
                    NullLogger<LegajoClassificationService>.Instance),
                new LegajoExtractionService(messages, new StubPageRenderer(),
                    Options.Create(new LegajoExtractionOptions { ApiKey = "test-key" }),
                    Options.Create(new LegajoOptions()),
                    NullLogger<LegajoExtractionService>.Instance),
                integrity ?? new LegajoIntegrityService(),
                Queue,
                Reconciliation,
                Options.Create(new LegajoOptions { StaleMinutes = 10, FieldConfidenceThreshold = fieldThreshold }),
                Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = maxAttempts }),
                NullLogger<LegajoDocumentProcessor>.Instance);
        }

        /// <summary>A document waiting for extraction; a native PDF reads as a Document block, a blank one as images.</summary>
        public Document Ready(string type = DocumentTypes.BienesPersonales, bool consent = true, bool scanned = false)
        {
            var legajo = Repo.AddLegajo(consent: consent);
            var document = Repo.AddDocument(legajo, type, LegajoStatuses.ReadyForExtraction);
            document.PeriodEnd = new DateTime(2025, 12, 31);
            Storage.Files[document.StoragePath] = scanned
                ? LegajoPdfFactory.BlankPdf(2)
                : LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);
            return document;
        }

        public Task RunAsync(Document document)
            => Sut.ProcessAsync(new LegajoWorkItem(document.DocumentId, document.TenantId), CancellationToken.None);

        public List<LegajoExtraction> Extractions => Repo.Runs.Where(r => r.Kind == LegajoExtractionKinds.Extraction).ToList();
    }

    private static ScriptedHandler Replies(params string[] bodies)
        => new(bodies.Select(b => (HttpStatusCode.OK, b)).ToArray());

    private static JsonObject Bp(Action<JsonObject>? mutate = null)
    {
        var input = LegajoExtractionFixtures.BienesPersonales();
        mutate?.Invoke(input);
        return input;
    }

    private static string BpReply(Action<JsonObject>? mutate = null) => LegajoExtractionFixtures.BpEnvelope(Bp(mutate));

    private static void OffTotal(JsonObject input) => LegajoJsonPointer.Set(input, "/totals/totalBienes/value", 1_900_000m);

    private static void Invalid(JsonObject input) => LegajoJsonPointer.Set(input, "/items/0/rubro", "Yates");

    // ── Clean path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CleanBp_OnePass_Ready_OneCurrentRun_WithChecksAndCost()
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal(1, h.Handler.Hits);

        var run = Assert.Single(h.Extractions);
        Assert.True(run.IsCurrent);
        Assert.Equal(LegajoExtractionStatuses.Succeeded, run.Status);
        Assert.Equal(LegajoReadModes.Document, run.ReadMode);
        Assert.Equal("1.0", run.SchemaVersion);
        Assert.Equal("bp-1.1", run.PromptVersion);
        Assert.Equal("claude-sonnet-4-6", run.Model);
        Assert.Equal(0.95m, run.Confidence);
        Assert.Equal(0.12m, run.CostUsd);   // 20.000 × $3 + 4.000 × $15, per million

        using var checks = JsonDocument.Parse(run.ChecksJson!);
        Assert.True(checks.RootElement.GetProperty("passed").GetBoolean());

        var call = Assert.Single(h.ApiCalls.Calls);
        Assert.Equal(LegajoMessagesClient.ExtractionOperation, call.Operation);
        Assert.Equal(document.DocumentId, call.DocumentId);

        Assert.Equal(
            [(LegajoStatuses.ReadyForExtraction, LegajoStatuses.Extracting), (LegajoStatuses.Extracting, LegajoStatuses.Ready)],
            h.Repo.Transitions.Where(t => t.DocumentId == document.DocumentId).Select(t => (t.From, t.To)));
    }

    [Fact]
    public async Task Completed_Extraction_RebuildsTheLegajo()
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal((document.PersonLegajoId!.Value, document.TenantId, "extraction"), Assert.Single(h.Reconciliation.Triggers));
    }

    [Fact]
    public async Task NoConsent_NothingMoved_NoRebuild()
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready(consent: false);

        await h.RunAsync(document);

        Assert.Empty(h.Reconciliation.Triggers);
    }

    [Fact]
    public async Task CleanMb_Ready()
    {
        var h = new Harness(Replies(LegajoExtractionFixtures.MbEnvelope()));
        var document = h.Ready(DocumentTypes.ManifestacionBienes);

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal("mb-1.3", Assert.Single(h.Extractions).PromptVersion);
    }

    // ── Claim ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentTypes.Reibp)]
    [InlineData(DocumentTypes.FlujoProyectado)]
    public async Task TypeWithoutAnExtractor_IsNeverClaimed_StaysReadyForExtraction(string type)
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready(type);

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
        Assert.Equal(0, h.Handler.Hits);
        Assert.Empty(h.Repo.Transitions);
    }

    [Fact]
    public async Task TwoWorkers_OnlyOneExtracts()
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready();

        await Task.WhenAll(h.RunAsync(document), h.RunAsync(document));

        Assert.Equal(1, h.Handler.Hits);
        Assert.Single(h.Extractions);
    }

    // ── Two passes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckFailsOnADocumentRead_SecondPassAsImages_Better_IsCurrent_BothStored()
    {
        var h = new Harness(Replies(BpReply(OffTotal), BpReply()));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(2, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);

        var runs = h.Extractions;
        Assert.Equal(2, runs.Count);
        Assert.Equal([LegajoReadModes.Document, LegajoReadModes.Images], runs.Select(r => r.ReadMode));
        Assert.False(runs[0].IsCurrent);   // swapped off by the second insert
        Assert.True(runs[1].IsCurrent);

        using var second = JsonDocument.Parse(h.Handler.RequestBodies[1]);
        Assert.Equal("image", second.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task SecondPassNoBetter_FirstStaysCurrent_NeedsReview_CheckFailedQueue()
    {
        var h = new Harness(Replies(BpReply(OffTotal), BpReply(i =>
        {
            OffTotal(i);
            LegajoJsonPointer.Set(i, "/items/1/fiscalValue", 1m);   // a second failed check: worse
        })));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        var runs = h.Extractions;
        Assert.True(runs[0].IsCurrent);
        Assert.False(runs[1].IsCurrent);
        Assert.Equal(LegajoExtractionStatuses.Succeeded, runs[1].Status);
    }

    [Fact]
    public async Task InvalidOutput_SecondPass_Valid_IsKept()
    {
        var h = new Harness(Replies(BpReply(Invalid), BpReply()));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        var runs = h.Extractions;
        Assert.Equal(LegajoExtractionStatuses.Failed, runs[0].Status);
        Assert.False(runs[0].IsCurrent);
        Assert.Contains("unknown_enum_value", runs[0].ResultJson);
        Assert.DoesNotContain("Yates", runs[0].ResultJson);   // violations carry pointers and codes, never values
        Assert.True(runs[1].IsCurrent);
    }

    [Fact]
    public async Task InvalidOutputTwice_Failed_WithTheExtractionStageMarker_CountsOneAttempt()
    {
        var h = new Harness(Replies(BpReply(Invalid), BpReply(Invalid)));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("extraction_invalid_output", document.LastExtractionError);
        Assert.Equal(1, document.ExtractionAttempts);
        Assert.Equal(2, h.Extractions.Count);
        Assert.DoesNotContain(h.Extractions, r => r.IsCurrent);
    }

    [Fact]
    public async Task CheckFailsOnAnImageRead_NoSecondPass_NeedsReview()
    {
        var h = new Harness(Replies(BpReply(OffTotal), BpReply()));
        var document = h.Ready(scanned: true);

        await h.RunAsync(document);

        Assert.Equal(1, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Equal(LegajoReadModes.Images, Assert.Single(h.Extractions).ReadMode);
    }

    [Fact]
    public async Task InvalidOutputOnAnImageRead_IsRetriedOnce()
    {
        var h = new Harness(Replies(BpReply(Invalid), BpReply()));
        var document = h.Ready(scanned: true);

        await h.RunAsync(document);

        Assert.Equal(2, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
    }

    // ── Outcomes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequiredFieldMissing_NeedsReview()
    {
        var h = new Harness(Replies(BpReply(i => LegajoJsonPointer.Set(i, "/header/fiscalYear/value", null))));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
    }

    [Fact]
    public async Task MbWithoutAUnit_NeedsReview_WithAmountsUnitMissing()
    {
        var input = LegajoExtractionFixtures.ManifestacionBienes();
        LegajoJsonPointer.Set(input, "/header/amountsUnit/value", null);
        var h = new Harness(Replies(LegajoExtractionFixtures.MbEnvelope(input)));
        var document = h.Ready(DocumentTypes.ManifestacionBienes);

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
    }

    [Fact]
    public async Task LowConfidence_NeedsReview()
    {
        var h = new Harness(Replies(BpReply(i => LegajoJsonPointer.Set(i, "/items/2/confidence", 0.4m))));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
    }

    [Fact]
    public async Task HolderCuitOfAnotherPerson_Blocked_AndStored()
    {
        var h = new Harness(Replies(LegajoExtractionFixtures.BpEnvelope(
            LegajoExtractionFixtures.BienesPersonales(holderCuit: LegajoTestData.OtherCuitDigits))));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Blocked, document.LegajoStatus);
        Assert.Equal("27-11223344-5", document.HolderCuit);
    }

    // ── Consent ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoConsent_NoCall_BackToReadyForExtraction_NotUploaded()
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready(consent: false);

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
    }

    [Fact]
    public async Task ConsentRevokedInFlight_RunStored_BackToReadyForExtraction()
    {
        var h = new Harness(Replies(BpReply()));
        var document = h.Ready();
        var checks = 0;
        h.Repo.ConsentOverride = () => ++checks <= 1;   // allowed before the request, revoked after it

        await h.RunAsync(document);

        Assert.Equal(1, h.Handler.Hits);
        Assert.Single(h.Extractions);                   // paid for: kept
        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
    }

    // ── Failures ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AttemptBudgetSpent_NoCall_Failed_WithTheStageMarker()
    {
        var h = new Harness(Replies(BpReply()), maxAttempts: 2);
        h.Documents.ExtractionAttemptsBudgetSpent = 2;
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal("extraction_attempts_exhausted", document.LastExtractionError);
        Assert.True(LegajoExtractionErrors.IsExtractionStage(document.LastExtractionError));
    }

    [Fact]
    public async Task Timeout_OneCallOnly_NoPass2_FailedAsExtractionTimeout()
    {
        var h = new Harness(new ScriptedHandler((HttpStatusCode.OK, LegajoExtractionFixtures.BpEnvelope())) { TimeoutsFirst = 5 });
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("extraction_timeout", document.LastExtractionError);
        Assert.True(LegajoExtractionErrors.IsExtractionStage(document.LastExtractionError));
        Assert.Equal(1, h.Handler.Hits);
        Assert.Equal("extraction_timeout", Assert.Single(h.ApiCalls.Calls).Error);
    }

    [Fact]
    public async Task RateLimited_Failed_NotCountedAsAnAttempt()
    {
        var h = new Harness(new ScriptedHandler((HttpStatusCode.TooManyRequests, "{}")));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal("extraction_rate_limited", document.LastExtractionError);
        Assert.Equal(0, document.ExtractionAttempts);
    }

    [Fact]
    public async Task UnexpectedException_AfterTheClaim_FailedFromExtracting()
    {
        var h = new Harness(Replies(BpReply()));
        h.Repo.InsertRunThrows = new InvalidOperationException("boom");
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("extraction_unexpected", document.LastExtractionError);
        Assert.Single(h.ApiCalls.Calls);   // the cost row was written before the throw
    }

    // ── Arithmetic ──────────────────────────────────────────────────────────────

    private sealed class ThrowingIntegrity : ILegajoIntegrityService
    {
        public LegajoIntegrityReport Evaluate(string documentType, JsonObject result, DateOnly? periodEnd)
            => throw new InvalidOperationException("a bug while reading the figures");
    }

    [Fact]
    public async Task HugeModelValue_IsInvalidOutput_BothPassesStored_Failed()
    {
        static void Huge(JsonObject i) => LegajoJsonPointer.Set(i, "/totals/totalBienes/value", 1e20m);
        var h = new Harness(Replies(BpReply(Huge), BpReply(Huge)));
        var document = h.Ready();

        await h.RunAsync(document);

        Assert.Equal("extraction_invalid_output", document.LastExtractionError);
        Assert.Equal(2, h.Extractions.Count);
        Assert.All(h.Extractions, r => Assert.Contains("out_of_range", r.ResultJson));
    }

    [Fact]
    public async Task AssessmentThrows_TheRunIsStoredFirst_ThenTheDocumentFails()
    {
        var h = new Harness(Replies(BpReply()), integrity: new ThrowingIntegrity());
        var document = h.Ready();

        await h.RunAsync(document);

        var run = Assert.Single(h.Extractions);
        Assert.Equal(LegajoExtractionStatuses.Succeeded, run.Status);   // the reading is kept
        Assert.False(run.IsCurrent);                                     // but never current unchecked
        Assert.Null(run.ChecksJson);
        Assert.Contains("totalBienes", run.ResultJson);
        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("extraction_unexpected", document.LastExtractionError);
    }

    // ── Recovery and hand-off ───────────────────────────────────────────────────

    [Fact]
    public async Task Recovery_StaleExtractingBackToReadyForExtraction_WaitingOnesEnqueued_InFlightSkipped()
    {
        var h = new Harness(Replies(BpReply()));
        var stale = h.Ready();
        stale.LegajoStatus = LegajoStatuses.Extracting;
        stale.LegajoStatusUpdatedAt = DateTime.UtcNow.AddHours(-1);
        var waiting = h.Ready(DocumentTypes.ManifestacionBienes);
        var noConsent = h.Ready(consent: false);
        var reibp = h.Ready(DocumentTypes.Reibp);
        var running = h.Ready();

        var items = await h.Sut.RecoverAsync(requeueEveryQueued: true, new HashSet<Guid> { running.DocumentId }, CancellationToken.None);

        Assert.Equal(new[] { stale.DocumentId, waiting.DocumentId }.Order(), items.Select(i => i.DocumentId).Order());
        Assert.Equal(LegajoStatuses.ReadyForExtraction, stale.LegajoStatus);
        Assert.DoesNotContain(items, i => i.DocumentId == noConsent.DocumentId || i.DocumentId == reibp.DocumentId);
    }

    // ── Phase 3a: the five new types ────────────────────────────────────────────

    public static TheoryData<string, string, int> TaxTypes => new()
    {
        { DocumentTypes.Ganancias, "gan-1.0", 16000 },
        { DocumentTypes.DdjjIva, "iva-1.0", 24000 },
        { DocumentTypes.DdjjIibb, "iibb-1.0", 24000 },
        { DocumentTypes.CertificacionIngresos, "ci-1.0", 16000 },
        { DocumentTypes.Monotributo, "mono-1.0", 16000 }
    };

    [Theory]
    [MemberData(nameof(TaxTypes))]
    public async Task TaxType_Clean_IsClaimedAndExtracted_OnePass_Ready(string type, string promptVersion, int maxTokens)
    {
        var h = new Harness(Replies(LegajoExtractionFixtures.EnvelopeFor(type)));
        var document = h.Ready(type);

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal(1, h.Handler.Hits);
        Assert.Contains((document.DocumentId, LegajoStatuses.ReadyForExtraction, LegajoStatuses.Extracting), h.Repo.Transitions);

        var run = Assert.Single(h.Extractions);
        Assert.True(run.IsCurrent);
        Assert.Equal(type, run.DocumentType);
        Assert.Equal(promptVersion, run.PromptVersion);
        using (var checks = JsonDocument.Parse(run.ChecksJson!))
            Assert.True(checks.RootElement.GetProperty("passed").GetBoolean());

        using var request = JsonDocument.Parse(Assert.Single(h.Handler.RequestBodies, b => b.Contains("\"tool_choice\"")));
        Assert.Equal(maxTokens, request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(LegajoExtractionFixtures.Schemas[type].ToolName,
                     request.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
    }

    [Fact]
    public async Task TaxType_FailedCheckOnADocumentRead_SecondPassAsImages_Better_IsCurrent()
    {
        var off = LegajoExtractionFixtures.DdjjIva();
        LegajoJsonPointer.Set(off, "/periods/0/debitoFiscal", 240_000m);
        var h = new Harness(Replies(LegajoExtractionFixtures.EnvelopeFor(DocumentTypes.DdjjIva, off),
                                    LegajoExtractionFixtures.EnvelopeFor(DocumentTypes.DdjjIva)));
        var document = h.Ready(DocumentTypes.DdjjIva);

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal(2, h.Handler.Hits);
        Assert.Equal([LegajoReadModes.Document, LegajoReadModes.Images], h.Extractions.Select(r => r.ReadMode));
        Assert.Equal(LegajoReadModes.Images, Assert.Single(h.Extractions, r => r.IsCurrent).ReadMode);
    }

    [Fact]
    public async Task TaxType_Truncated_Failed_WithTheStageMarker_NoSecondPass()
    {
        var h = new Harness(Replies(LegajoExtractionFixtures.EnvelopeFor(DocumentTypes.DdjjIibb, stopReason: "max_tokens")));
        var document = h.Ready(DocumentTypes.DdjjIibb);

        await h.RunAsync(document);

        Assert.Equal(1, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("extraction_claude_truncated", document.LastExtractionError);
        Assert.False(Assert.Single(h.Extractions).IsCurrent);
    }

    [Fact]
    public async Task TaxType_NoConsent_NoCall_StaysReadyForExtraction()
    {
        var h = new Harness(Replies(LegajoExtractionFixtures.EnvelopeFor(DocumentTypes.Monotributo)));
        var document = h.Ready(DocumentTypes.Monotributo, consent: false);

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
    }

    [Fact]
    public async Task Recovery_PicksUpPreExistingTaxTypes_ThatWaitedForAnExtractor_ThenExtractsThem()
    {
        var h = new Harness(Replies(LegajoExtractionFixtures.EnvelopeFor(DocumentTypes.Ganancias)));
        var waited = h.Ready(DocumentTypes.Ganancias);
        waited.LegajoStatusUpdatedAt = DateTime.UtcNow.AddDays(-5);
        var withoutConsent = h.Ready(DocumentTypes.DdjjIva, consent: false);

        var items = await h.Sut.RecoverAsync(requeueEveryQueued: true, new HashSet<Guid>(), CancellationToken.None);

        Assert.Equal([waited.DocumentId], items.Select(i => i.DocumentId));
        Assert.DoesNotContain(items, i => i.DocumentId == withoutConsent.DocumentId);

        await h.Sut.ProcessAsync(items[0], CancellationToken.None);
        Assert.Equal(LegajoStatuses.Ready, waited.LegajoStatus);
        Assert.Equal("gan-1.0", Assert.Single(h.Extractions).PromptVersion);
    }

    [Fact]
    public async Task CleanClassification_IsHandedToExtraction_ReviewIsNot()
    {
        var clean = new Harness(Replies(ClassificationEnvelopes.ToolUse(documentType: DocumentTypes.BienesPersonales)));
        var legajo = clean.Repo.AddLegajo(consent: true);
        var document = clean.Repo.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.Queued);
        clean.Storage.Files[document.StoragePath] = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);

        await clean.RunAsync(document);

        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
        Assert.Equal(document.DocumentId, Assert.Single(clean.Queue.Items).DocumentId);

        var review = new Harness(Replies(ClassificationEnvelopes.ToolUse(documentType: DocumentTypes.Ganancias)));
        var other = review.Repo.AddDocument(review.Repo.AddLegajo(consent: true), DocumentTypes.BienesPersonales, LegajoStatuses.Queued);
        review.Storage.Files[other.StoragePath] = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);

        await review.RunAsync(other);

        Assert.Equal(LegajoStatuses.NeedsReview, other.LegajoStatus);
        Assert.Empty(review.Queue.Items);
    }

    [Fact]
    public async Task Classify_ThenExtract_TheSecondItemRunsTheExtraction()
    {
        var h = new Harness(Replies(ClassificationEnvelopes.ToolUse(documentType: DocumentTypes.BienesPersonales), BpReply()));
        var legajo = h.Repo.AddLegajo(consent: true);
        var document = h.Repo.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.Queued);
        h.Storage.Files[document.StoragePath] = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);

        await h.RunAsync(document);
        await h.Sut.ProcessAsync(Assert.Single(h.Queue.Items), CancellationToken.None);

        Assert.Equal(LegajoStatuses.Ready, document.LegajoStatus);
        Assert.Equal(
            [LegajoMessagesClient.ClassificationOperation, LegajoMessagesClient.ExtractionOperation],
            h.ApiCalls.Calls.Select(c => c.Operation));
    }
}
