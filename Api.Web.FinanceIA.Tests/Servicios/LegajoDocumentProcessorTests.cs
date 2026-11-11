// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Net;
using System.Security.Claims;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoDocumentProcessor"/> and <see cref="LegajoPipelineWorker"/>, end to end over the
/// REAL classification service with a hand-written HTTP handler: the claim, the consent gate (no HTTP
/// call without it — asserted on the handler), the C# outcome, run/ApiCallLogs bookkeeping, failures,
/// the attempt cap, the stale requeue, and the request payload's freedom from the legajo's identity.
/// </summary>
public sealed class LegajoDocumentProcessorTests
{
    private sealed class Harness
    {
        public FakeLegajoDocumentRepository Repo { get; } = new();
        public InMemoryStorage Storage { get; } = new();
        public RecordingApiCallLogService ApiCalls { get; } = new();
        public ListDocumentRepository Documents { get; }
        public ScriptedHandler Handler { get; }
        public RecordingPipelineQueue Queue { get; } = new();
        public RecordingReconciliationService Reconciliation { get; } = new();
        public LegajoDocumentProcessor Sut { get; }

        public Harness(ScriptedHandler handler, int maxAttempts = 2, decimal threshold = 0.7m)
        {
            Handler   = handler;
            Documents = new ListDocumentRepository(Repo.Documents);

            var classifier = new LegajoClassificationService(
                LegajoTestClients.Messages(handler, ApiCalls),
                new StubPageRenderer(),
                Options.Create(new LegajoClassificationOptions { ApiKey = "test-key" }),
                NullLogger<LegajoClassificationService>.Instance);

            Sut = new LegajoDocumentProcessor(
                Repo,
                Documents,
                Storage,
                classifier,
                new LegajoExtractionService(
                    LegajoTestClients.Messages(handler, ApiCalls),
                    new StubPageRenderer(),
                    Options.Create(new LegajoExtractionOptions { ApiKey = "test-key" }),
                    Options.Create(new LegajoOptions()),
                    NullLogger<LegajoExtractionService>.Instance),
                new LegajoIntegrityService(),
                Queue,
                Reconciliation,
                Options.Create(new LegajoOptions { ClassificationConfidenceThreshold = threshold, StaleMinutes = 10 }),
                Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = maxAttempts }),
                NullLogger<LegajoDocumentProcessor>.Instance);
        }

        // REIBP has no extractor: a clean classification stays ReadyForExtraction, so these tests observe the
        // classification stage alone. (Monotributo was the stand-in until Phase 3a gave it an extractor.)
        public Document QueuedDocument(bool consent = true, string type = DocumentTypes.Reibp)
        {
            var legajo = Repo.AddLegajo(consent: consent);
            var document = Repo.AddDocument(legajo, type, LegajoStatuses.Queued);
            Storage.Files[document.StoragePath] = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);
            return document;
        }

        public Task RunAsync(Document document)
            => Sut.ProcessAsync(new LegajoWorkItem(document.DocumentId, document.TenantId), CancellationToken.None);
    }

    private static ScriptedHandler Ok(string? body = null)
        => new((HttpStatusCode.OK, body ?? ClassificationEnvelopes.ToolUse(documentType: DocumentTypes.Reibp)));

    // ── Claim ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotQueued_ClaimIsLost_NothingHappens()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        document.LegajoStatus = LegajoStatuses.Classifying;

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Empty(h.Repo.Runs);
        Assert.Equal(LegajoStatuses.Classifying, document.LegajoStatus);
    }

    [Fact]
    public async Task TwoWorkersOnTheSameDocument_OnlyOneCallsTheModel()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();

        await Task.WhenAll(h.RunAsync(document), h.RunAsync(document));

        Assert.Equal(1, h.Handler.Hits);
        Assert.Single(h.Repo.Runs);
        Assert.Single(h.Repo.Transitions, t => t.From == LegajoStatuses.Queued && t.To == LegajoStatuses.Classifying);
    }

    // ── Consent ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutConsent_NoHttpCall_DocumentBackToUploaded()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument(consent: false);

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Uploaded, document.LegajoStatus);
        Assert.Empty(h.ApiCalls.Calls);
        Assert.Empty(h.Repo.Runs);
    }

    [Fact]
    public async Task ConsentRevokedJustBeforeTheCall_NoHttpCall_DocumentBackToUploaded()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument(consent: true);
        h.Repo.ConsentOverride = () => false;  // the re-check right before the request says no

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.True(h.Repo.HasConsentCalls >= 1);
        Assert.Equal(LegajoStatuses.Uploaded, document.LegajoStatus);
        Assert.Empty(h.Repo.Failures);
    }

    // ── Outcomes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchingCuit_ReadyForExtraction_RunAndCostRowStored()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
        Assert.Equal(LegajoTestData.LegajoCuit, document.HolderCuit);
        Assert.Equal(new DateTime(2025, 12, 31), document.PeriodEnd);
        Assert.Equal(EngagementTypes.Certificacion, document.EngagementType);

        var run = Assert.Single(h.Repo.Runs);
        Assert.Equal(LegajoExtractionKinds.Classification, run.Kind);
        Assert.Equal(LegajoExtractionStatuses.Succeeded, run.Status);
        Assert.True(run.IsCurrent);
        Assert.Null(run.CreatedBy);
        Assert.Equal(LegajoReadModes.Document, run.ReadMode);
        Assert.Equal(LegajoClassificationPrompt.PromptVersion, run.PromptVersion);
        Assert.Equal(0.00575m, run.CostUsd);
        Assert.Equal(64, run.InputHash.Length);
        Assert.Equal(64, run.OutputHash.Length);

        var call = Assert.Single(h.ApiCalls.Calls);
        Assert.Equal("Claude", call.Service);
        Assert.Equal("LegajoClassification", call.Operation);
        Assert.True(call.IsSuccess);
        Assert.Equal(document.DocumentId, call.DocumentId);
        Assert.Equal(0.00575m, call.Cost);
    }

    [Fact]
    public async Task MismatchingCuit_Blocked()
    {
        var h = new Harness(Ok(ClassificationEnvelopes.ToolUse(documentType: DocumentTypes.Reibp, cuit: LegajoTestData.OtherCuitDigits)));
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Blocked, document.LegajoStatus);
        Assert.Contains(LegajoClassificationOutcome.ReasonCuitMismatch, Assert.Single(h.Repo.Runs).ResultJson);
    }

    [Fact]
    public async Task MissingCuit_NeedsReview_WithTheReasonInTheRun()
    {
        var h = new Harness(Ok(ClassificationEnvelopes.ToolUse(documentType: DocumentTypes.Reibp, cuit: null)));
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Null(document.HolderCuit);
        Assert.Contains("\"reviewReasons\":[\"cuit_missing\"]", Assert.Single(h.Repo.Runs).ResultJson);
    }

    [Theory]
    [InlineData(DocumentTypes.Ganancias, false, 0.95, "type_mismatch")]
    [InlineData(DocumentTypes.Reibp, true, 0.95, "composite")]
    [InlineData(DocumentTypes.Reibp, false, 0.5, "low_confidence")]
    public async Task ReviewReasons_NeedsReview(string detected, bool composite, double confidence, string reason)
    {
        var h = new Harness(Ok(ClassificationEnvelopes.ToolUse(documentType: detected, isComposite: composite,
                                                               confidence: (decimal)confidence)));
        var document = h.QueuedDocument(type: DocumentTypes.Reibp);

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Contains($"\"{reason}\"", Assert.Single(h.Repo.Runs).ResultJson);
    }

    [Fact]
    public async Task SecondRun_SwapsIsCurrent()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();

        await h.RunAsync(document);
        document.LegajoStatus = LegajoStatuses.Queued;
        await h.RunAsync(document);

        Assert.Equal(2, h.Repo.Runs.Count);
        Assert.Single(h.Repo.Runs, r => r.IsCurrent);
        Assert.True(h.Repo.Runs[1].IsCurrent);
    }

    // ── The request never carries the legajo's identity ─────────────────────────

    [Fact]
    public async Task RequestPayload_ContainsNeitherTheLegajoCuitNorItsFullName()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        var body = Assert.Single(h.Handler.RequestBodies);
        Assert.DoesNotContain(LegajoTestData.LegajoCuit, body);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, body);
        Assert.DoesNotContain(LegajoTestData.LegajoFullName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRUEBA SINTETICA", body, StringComparison.OrdinalIgnoreCase);
    }

    // ── Failures and the attempt cap ────────────────────────────────────────────

    [Fact]
    public async Task ApiFailure_FailedWithShortCode_CountsAnAttempt_RunAndLogStored()
    {
        var h = new Harness(new ScriptedHandler((HttpStatusCode.InternalServerError, "{\"error\":\"x\"}")));
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal(LegajoClassificationErrors.ApiError, document.LastExtractionError);
        Assert.Equal(1, document.ExtractionAttempts);
        Assert.NotNull(document.LastExtractionFailedAt);

        var run = Assert.Single(h.Repo.Runs);
        Assert.Equal(LegajoExtractionStatuses.Failed, run.Status);
        Assert.False(run.IsCurrent);

        // The code stays the prefix; the API's status follows. The body is not an Anthropic error
        // envelope, so nothing of it is kept.
        var call = Assert.Single(h.ApiCalls.Calls);
        Assert.False(call.IsSuccess);
        Assert.Equal($"{LegajoClassificationErrors.ApiError} | HTTP 500", call.Error);
    }

    [Fact]
    public async Task AttemptBudgetSpent_FailedWithoutACall()
    {
        var h = new Harness(Ok(), maxAttempts: 2);
        h.Documents.ExtractionAttemptsBudgetSpent = 2;
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal(LegajoClassificationErrors.AttemptsExhausted, document.LastExtractionError);
        Assert.Equal(0, document.ExtractionAttempts);
    }

    [Fact]
    public async Task AttemptBudgetDisabled_StillCalls()
    {
        var h = new Harness(Ok(), maxAttempts: 0);
        h.Documents.ExtractionAttemptsBudgetSpent = 99;
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(1, h.Handler.Hits);
    }

    [Fact]
    public async Task StorageFailure_FailedWithoutACall_NotCounted()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        h.Storage.ReadThrows = new FileNotFoundException("gone");

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(LegajoClassificationErrors.StorageUnavailable, document.LastExtractionError);
        Assert.Equal(0, document.ExtractionAttempts);
    }

    // ── 429, consent revoked in flight, unexpected failures ─────────────────────

    [Fact]
    public async Task RateLimited_FailedWithItsOwnCode_DoesNotConsumeTheBudget()
    {
        var h = new Harness(new ScriptedHandler((HttpStatusCode.TooManyRequests, "{}")));
        var document = h.QueuedDocument();

        await h.RunAsync(document);

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal(LegajoClassificationErrors.RateLimited, document.LastExtractionError);
        Assert.Equal("rate_limited", document.LastExtractionError);
        Assert.Equal(0, document.ExtractionAttempts);
        Assert.Single(h.ApiCalls.Calls);
    }

    [Fact]
    public async Task ConsentRevokedWhileTheCallWasInFlight_RunKept_DocumentBackToUploaded()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        var checks = 0;
        h.Repo.ConsentOverride = () => ++checks == 1;   // allowed before the request, revoked after it

        await h.RunAsync(document);

        Assert.Equal(1, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Uploaded, document.LegajoStatus);
        Assert.Null(document.HolderCuit);
        Assert.Single(h.Repo.Runs);
        Assert.Single(h.ApiCalls.Calls);
    }

    [Fact]
    public async Task ConsentDocumentDeleted_ConsentNoLongerActive_NoHttpCall()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument(consent: true);
        var legajo = h.Repo.Legajos.Single();
        h.Repo.Get(legajo.ConsentDocumentId!.Value).IsDeleted = true;

        await h.RunAsync(document);

        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(LegajoStatuses.Uploaded, document.LegajoStatus);
    }

    [Fact]
    public async Task UnexpectedExceptionAfterTheClaim_EndsFailed_CostRowFirst_ThenReprocessWorks()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        h.Repo.InsertRunThrows = new InvalidOperationException("database hiccup");

        await h.RunAsync(document);   // must not throw

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("unexpected", document.LastExtractionError);
        Assert.Equal(1, document.ExtractionAttempts);   // the request was billed
        Assert.Single(h.ApiCalls.Calls);                 // written before the throw

        // POST /api/documents/{id}/reprocess recovers it: Failed → Queued, enqueued.
        var queue = new RecordingPipelineQueue();
        var documents = new DocumentsController(
            new StubTenantContextAccessor(), h.Documents, new StubEntityRepository(), h.Storage, new StubUserRepository(),
            new RecordingAuditService(), h.Repo, new FakeFiadorRepository(), queue, new RecordingReconciliationService(),
            Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = 2 }),
            new RecordingRunListRepository(), new Api.Web.FinanceIA.Clases.Concurrency.KeyedAsyncLock(),
            NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", LegajoTestData.FirebaseUid)], "Test"))
                }
            }
        };

        var reprocess = await documents.ReprocessAsync(document.DocumentId, CancellationToken.None);

        Assert.Equal(202, Assert.IsType<ObjectResult>(reprocess).StatusCode);
        Assert.Equal(LegajoStatuses.Queued, document.LegajoStatus);
        h.Repo.InsertRunThrows = null;

        await h.Sut.ProcessAsync(Assert.Single(queue.Items), CancellationToken.None);

        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
    }

    [Fact]
    public async Task UnexpectedExceptionBeforeAnyRequest_EndsFailed_NotCounted()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        h.Documents.SumThrows = new InvalidOperationException("budget read failed");

        await h.RunAsync(document);   // must not throw

        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
        Assert.Equal("unexpected", document.LastExtractionError);
        Assert.Equal(0, document.ExtractionAttempts);
        Assert.Equal(0, h.Handler.Hits);
    }

    // ── Recovery: startup and sweep ─────────────────────────────────────────────

    private static (Document StaleQueued, Document FreshQueued, Document StaleClassifying, Document FreshClassifying, Document Ready)
        SeedRecoverable(Harness h)
    {
        var legajo = h.Repo.AddLegajo(consent: true);
        var old = DateTime.UtcNow.AddMinutes(-30);

        var staleQueued      = h.Repo.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Queued);
        var freshQueued      = h.Repo.AddDocument(legajo, DocumentTypes.Reibp, LegajoStatuses.Queued);
        var staleClassifying = h.Repo.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.Classifying);
        var freshClassifying = h.Repo.AddDocument(legajo, DocumentTypes.DdjjIibb, LegajoStatuses.Classifying);
        var ready            = h.Repo.AddDocument(legajo, DocumentTypes.Reibp, LegajoStatuses.ReadyForExtraction);   // no extractor
        staleQueued.LegajoStatusUpdatedAt      = old;
        freshQueued.LegajoStatusUpdatedAt      = DateTime.UtcNow;
        staleClassifying.LegajoStatusUpdatedAt = old;
        freshClassifying.LegajoStatusUpdatedAt = DateTime.UtcNow;
        ready.LegajoStatusUpdatedAt            = old;

        return (staleQueued, freshQueued, staleClassifying, freshClassifying, ready);
    }

    [Fact]
    public async Task StartupRecovery_EnqueuesEveryQueuedRow_WhateverItsAge_AndStaleClassifying()
    {
        var h = new Harness(Ok());
        var (staleQueued, freshQueued, staleClassifying, freshClassifying, _) = SeedRecoverable(h);

        var items = await h.Sut.RecoverAsync(requeueEveryQueued: true, new HashSet<Guid>(), CancellationToken.None);

        Assert.Equal(
            new[] { staleQueued.DocumentId, freshQueued.DocumentId, staleClassifying.DocumentId }.Order(),
            items.Select(i => i.DocumentId).Order());
        Assert.Equal(LegajoStatuses.Queued, staleClassifying.LegajoStatus);
        Assert.Equal(LegajoStatuses.Classifying, freshClassifying.LegajoStatus);
    }

    [Fact]
    public async Task Sweep_PicksUpStaleClassifyingAndStaleQueued_LeavesFreshRowsAlone()
    {
        var h = new Harness(Ok());
        var (staleQueued, freshQueued, staleClassifying, freshClassifying, _) = SeedRecoverable(h);

        var items = await h.Sut.RecoverAsync(requeueEveryQueued: false, new HashSet<Guid>(), CancellationToken.None);

        Assert.Equal(
            new[] { staleQueued.DocumentId, staleClassifying.DocumentId }.Order(),
            items.Select(i => i.DocumentId).Order());
        Assert.Equal(LegajoStatuses.Queued, staleClassifying.LegajoStatus);
        Assert.Equal(LegajoStatuses.Queued, freshQueued.LegajoStatus);
        Assert.Equal(LegajoStatuses.Classifying, freshClassifying.LegajoStatus);
    }

    [Fact]
    public async Task Sweep_NeverTouchesADocumentThisInstanceIsProcessing()
    {
        var h = new Harness(Ok());
        var (_, _, staleClassifying, _, _) = SeedRecoverable(h);

        var items = await h.Sut.RecoverAsync(false, new HashSet<Guid> { staleClassifying.DocumentId }, CancellationToken.None);

        Assert.DoesNotContain(items, i => i.DocumentId == staleClassifying.DocumentId);
        Assert.Equal(LegajoStatuses.Classifying, staleClassifying.LegajoStatus);
    }

    [Fact]
    public async Task RequeuedStaleClassifyingRow_IsThenClassifiedNormally()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        document.LegajoStatus = LegajoStatuses.Classifying;           // a job died mid-way
        document.LegajoStatusUpdatedAt = DateTime.UtcNow.AddHours(-1);

        var item = Assert.Single(await h.Sut.RecoverAsync(false, new HashSet<Guid>(), CancellationToken.None));
        await h.Sut.ProcessAsync(item, CancellationToken.None);

        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
    }

    // ── Worker ──────────────────────────────────────────────────────────────────

    private sealed class ThrowingProcessor : ILegajoDocumentProcessor
    {
        public List<LegajoWorkItem> Stale { get; } = [];
        public int Processed { get; private set; }

        public Task ProcessAsync(LegajoWorkItem item, CancellationToken ct)
        {
            Processed++;
            throw new InvalidOperationException("boom");
        }

        public List<bool> RecoveryCalls { get; } = [];

        public Task<IReadOnlyList<LegajoWorkItem>> RecoverAsync(bool requeueEveryQueued, IReadOnlySet<Guid> inFlight, CancellationToken ct)
        {
            RecoveryCalls.Add(requeueEveryQueued);
            return Task.FromResult<IReadOnlyList<LegajoWorkItem>>(Stale);
        }
    }

    private static (LegajoPipelineWorker Worker, LegajoPipelineQueue Queue) BuildWorker(ILegajoDocumentProcessor processor,
                                                                                         int concurrency = 1)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => processor);
        var provider = services.BuildServiceProvider();
        var queue = new LegajoPipelineQueue();

        var worker = new LegajoPipelineWorker(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LegajoOptions { StaleMinutes = 10, WorkerConcurrency = concurrency }),
            NullLogger<LegajoPipelineWorker>.Instance);

        return (worker, queue);
    }

    [Fact]
    public async Task Worker_StartupRecovery_AsksForEveryQueuedRow_AndEnqueuesThem()
    {
        var processor = new ThrowingProcessor();
        var item = new LegajoWorkItem(Guid.NewGuid(), LegajoTestData.Tenant);
        processor.Stale.Add(item);
        var (worker, queue) = BuildWorker(processor);

        await worker.RecoverAsync(startup: true, CancellationToken.None);

        Assert.Equal([true], processor.RecoveryCalls);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var queued in queue.ReadAllAsync(cts.Token))
        {
            Assert.Equal(item, queued);
            break;
        }
    }

    [Fact]
    public async Task Worker_Sweep_AsksOnlyForStaleRows_OnTheStaleMinutesPeriod()
    {
        var processor = new ThrowingProcessor();
        var (worker, _) = BuildWorker(processor);

        await worker.RecoverAsync(startup: false, CancellationToken.None);

        Assert.Equal([false], processor.RecoveryCalls);
        Assert.Equal(TimeSpan.FromMinutes(10), worker.SweepInterval);
    }

    [Fact]
    public async Task Worker_AJobThatThrows_DoesNotStopTheWorker()
    {
        var processor = new ThrowingProcessor();
        var (worker, _) = BuildWorker(processor);

        await worker.ProcessOneAsync(new LegajoWorkItem(Guid.NewGuid(), LegajoTestData.Tenant), CancellationToken.None);
        await worker.ProcessOneAsync(new LegajoWorkItem(Guid.NewGuid(), LegajoTestData.Tenant), CancellationToken.None);

        Assert.Equal(2, processor.Processed);
    }

    /// <summary>
    /// The first job on a document blocks until released; every later one ends at once, as a duplicate
    /// whose claim loses does. Records the in-flight set each recovery sees.
    /// </summary>
    private sealed class BlockingProcessor : ILegajoDocumentProcessor
    {
        private int _calls;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DuplicateDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<IReadOnlySet<Guid>> SeenInFlight { get; } = [];

        public async Task ProcessAsync(LegajoWorkItem item, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.SetResult();
                await Release.Task.WaitAsync(ct);
                FirstDone.SetResult();
                return;
            }

            DuplicateDone.SetResult();
        }

        public Task<IReadOnlyList<LegajoWorkItem>> RecoverAsync(bool requeueEveryQueued, IReadOnlySet<Guid> inFlight, CancellationToken ct)
        {
            lock (SeenInFlight)
                SeenInFlight.Add(inFlight);
            return Task.FromResult<IReadOnlyList<LegajoWorkItem>>([]);
        }
    }

    [Fact]
    public async Task Worker_ADuplicateItemFinishingFirst_LeavesTheRunningDocumentInFlight()
    {
        var processor = new BlockingProcessor();
        var (worker, queue) = BuildWorker(processor, concurrency: 2);
        var item = new LegajoWorkItem(Guid.NewGuid(), LegajoTestData.Tenant);
        var timeout = TimeSpan.FromSeconds(10);

        queue.Enqueue(item);
        await worker.StartAsync(CancellationToken.None);
        await processor.FirstStarted.Task.WaitAsync(timeout);

        queue.Enqueue(item);                                  // the second consumer takes the duplicate
        await processor.DuplicateDone.Task.WaitAsync(timeout);
        await Task.Delay(50);                                 // let its job's finally run

        await worker.RecoverAsync(startup: false, CancellationToken.None);
        Assert.Contains(item.DocumentId, processor.SeenInFlight[^1]);

        processor.Release.SetResult();
        await processor.FirstDone.Task.WaitAsync(timeout);
        await Task.Delay(50);

        await worker.RecoverAsync(startup: false, CancellationToken.None);
        Assert.DoesNotContain(item.DocumentId, processor.SeenInFlight[^1]);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_RunsQueuedItems_UntilStopped()
    {
        var h = new Harness(Ok());
        var document = h.QueuedDocument();
        var (worker, queue) = BuildWorker(h.Sut);

        queue.Enqueue(new LegajoWorkItem(document.DocumentId, document.TenantId));
        await worker.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (document.LegajoStatus != LegajoStatuses.ReadyForExtraction && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
        Assert.Equal(1, h.Handler.Hits);
    }
}
