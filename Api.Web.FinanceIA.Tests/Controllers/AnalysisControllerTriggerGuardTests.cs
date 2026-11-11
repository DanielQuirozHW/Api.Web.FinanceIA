// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-10

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the terminal-state guard on <see cref="AnalysisController.TriggerAsync"/>: once a
/// document is set to "Processing", a fault must drive it to a terminal state instead of
/// stranding it. Cancellation is exempt ONLY once the server owns the work (the Claude call
/// onward, all on CancellationToken.None); a cancellation BEFORE that point is a plain
/// abandonment and must still reach "Failed" (re-analysable), not stay "Processing".
///
///   • a non-cancellation fault (missing file) ⇒ "Processing" then "Failed";
///   • a cancellation BEFORE the Claude call (storage read) ⇒ "Processing" then "Failed";
///   • a cancellation AT/AFTER the Claude call ⇒ "Processing" only, NEVER "Failed";
///   • a clean run ⇒ ends "Completed" and returns 201.
///
/// Hand-rolled private sealed stubs (no mock library). Only the dependencies the guarded path
/// touches are stubbed; the rest of the ~20 ctor dependencies are passed as null! (the ctor does
/// plain assignment, and the fault-isolated enrichment blocks swallow any NRE they would raise).
/// Argument order mirrors the ctor signature.
/// </summary>
public sealed class AnalysisControllerTriggerGuardTests
{
    private const string Tenant      = "tenant-1";

    // The document and the request name the SAME entity: TriggerAsync refuses a document of another
    // entity (document_entity_mismatch) before any work.
    private static readonly Guid FixtureEntityId = Guid.Parse("6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b");
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public Document? DocumentToReturn { get; set; }

        /// <summary>Ordered spy of every ProcessingStatus written via UpdateStatusAsync.</summary>
        public List<string> StatusWrites { get; } = new();

        public Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => Task.FromResult(DocumentToReturn);

        public Task UpdateStatusAsync(Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
        {
            StatusWrites.Add(status);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Document> CreateAsync(Document document, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SoftDeleteAsync(Guid documentId, string tenantId, DateTime deletedAt, CancellationToken ct)
            => throw new NotImplementedException();

        /// <summary>Failed-extraction attempts already spent on the file. Zero unless a test says otherwise.</summary>
        public int ExtractionAttempts { get; set; }

        /// <summary>When set, SumExtractionAttemptsByHashAsync throws it.</summary>
        public Exception? SumThrows { get; set; }

        /// <summary>Ordered spy of every reason code written via RecordExtractionFailureAsync.</summary>
        public List<string?> RecordedExtractionFailures { get; } = new();

        public Task<Document?> FindRecentByHashAsync(Guid entityId, string tenantId, string? fileHashSha256, string documentType, DateTime uploadedSinceUtc, CancellationToken ct)
            => Task.FromResult<Document?>(null);

        public Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<int> SumExtractionAttemptsByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => SumThrows is null ? Task.FromResult(ExtractionAttempts) : Task.FromException<int>(SumThrows);

        public Task RecordExtractionFailureAsync(
            Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct)
        {
            RecordedExtractionFailures.Add(error);
            // Like the real repository: a charged failure spends one attempt of the file's budget.
            if (countsAsAttempt)
                ExtractionAttempts++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDocumentStorageService : IDocumentStorageService
    {
        /// <summary>When set, ReadAsync throws this; otherwise it returns <see cref="ReadReturns"/>.</summary>
        public Exception? ReadThrows { get; set; }
        public byte[] ReadReturns { get; set; } = Array.Empty<byte>();

        public Task<byte[]> ReadAsync(string storagePath, CancellationToken ct)
        {
            if (ReadThrows is not null)
                throw ReadThrows;
            return Task.FromResult(ReadReturns);
        }

        public Task<string> SaveAsync(string tenantId, Guid documentId, Stream content, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubClaudeService : IClaudeService
    {
        public Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
            byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
            => throw new NotImplementedException();

        public ClaudeAnalysisResult? ResultToReturn { get; set; }

        /// <summary>When set, AnalyzeDocumentAsync throws this instead of returning a result.</summary>
        public Exception? AnalyzeThrows { get; set; }

        /// <summary>Runs when AnalyzeDocumentAsync is called, before it throws or returns.</summary>
        public Action? OnAnalyze { get; set; }

        public Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
        {
            OnAnalyze?.Invoke();
            if (AnalyzeThrows is not null)
                throw AnalyzeThrows;
            return Task.FromResult(ResultToReturn!);
        }

        public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
            => throw new NotImplementedException();
        public Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(ScoringResult scoring, RatiosData ratios, BcraData? bcraData, BcraMacroData? macroData, ScoreReliabilityResponse? reliability, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        // The quorum-crossing write (DDL 067). Throws because no test in this suite confirms a
        // pre-approval, so an unexpected call must fail loudly rather than pass silently.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        /// <summary>When set, CreateAsync throws this — a persistence fault AFTER the Claude call.</summary>
        public Exception? CreateThrows { get; set; }

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => CreateThrows is not null
                ? Task.FromException<Analysis>(CreateThrows)
                : Task.FromResult(analysis);
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => Task.FromResult<Analysis?>(null);

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => Task.FromResult(result);

        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(Guid entityId, string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null, string? requestId = null,
            string? oldValueJson = null, string? newValueJson = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class StubApiCallLogService : IApiCallLogService
    {
        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Spy on the completion notifier: every call, in order, with its arguments.</summary>
    private sealed class RecordingCompletionNotifier : IAnalysisCompletionNotifier
    {
        public List<(Guid UserId, Guid AnalysisId, Guid ResultId, int FiscalYear)> Completed { get; } = new();
        public List<(Guid UserId, Guid DocumentId, Guid EntityId)> Failed { get; } = new();

        /// <summary>When set, both methods throw this — the controller must still be unaffected.</summary>
        public Exception? Throws { get; set; }

        public Task NotifyCompletedAsync(string tenantId, Guid userId, Guid analysisId, Guid resultId,
            string? entityName, int fiscalYear, decimal? score, CancellationToken ct)
        {
            Completed.Add((userId, analysisId, resultId, fiscalYear));
            return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
        }

        public Task NotifyFailedAsync(string tenantId, Guid userId, Guid documentId, Guid entityId, CancellationToken ct)
        {
            Failed.Add((userId, documentId, entityId));
            return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static Document BuildPendingDocument(Guid documentId) => new()
    {
        DocumentId       = documentId,
        TenantId         = Tenant,
        EntityId         = FixtureEntityId,
        StoragePath      = "/tmp/financia-uploads/tenant-1/doc.pdf",
        ProcessingStatus = "Pending",
        IsDeleted        = false,
        DocumentType     = "Balance",
        FiscalYear       = 2024
    };

    private static TriggerAnalysisRequest BuildRequest(Guid documentId) => new()
    {
        DocumentId      = documentId,
        EntityId        = FixtureEntityId,
        FiscalYear      = 2024,
        RequestedAmount = null
    };

    private static ClaudeAnalysisResult BuildClaudeResult() => new(
        RatiosJson:           "",
        AlertsJson:           "",   // empty ⇒ alert-persistence block is skipped
        AnomaliesJson:        "",
        BalanceJson:          "",   // empty ⇒ ratio calculation is skipped
        ExecutiveSummary:     "Resumen ejecutivo.",
        ConfidenceScore:      0.95m,
        HighestAlertLevel:    null,
        BalanceCheckBalanced: true,
        BalanceDifferencePct: 0.0,
        InputTokens:          1000,
        OutputTokens:         500,
        InputHash:            "in-hash",
        OutputHash:           "out-hash")
    {
        ModelUsed     = "claude-sonnet-4-6",
        PromptVersion = "2.0"
    };

    private static AnalysisController BuildSut(
        StubDocumentRepository     docRepo,
        StubDocumentStorageService storage,
        IClaudeService?            claude = null,
        Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository? legajos = null,
        IAnalysisCompletionNotifier? completion = null,
        StubAnalysisRepository?      analyses   = null,
        Microsoft.Extensions.Logging.ILogger<AnalysisController>? logger = null,
        BillingOptions?              billing    = null)
    {
        // The long part of TriggerAsync lives in AnalysisPipeline; it gets the dependencies the
        // controller used to hold for it, so every assertion below still describes the endpoint.
        var analysisRepo = analyses ?? new StubAnalysisRepository();
        var pipeline = new AnalysisPipeline(
            docRepo,                                                        // documentRepository
            new StubAnalysisResultRepository(),                             // analysisResultRepository
            analysisRepo,                                                   // analysisRepository
            storage,                                                        // storageService
            claude!,                                                        // claudeService
            null!,                                                          // bcraService
            null!,                                                          // scoringService
            null!,                                                          // ratioCalculationService
            null!,                                                          // cashFlowReconciliationService
            new AlertRatioReconciliationService(),                          // alertRatioReconciliationService — real, stateless
            null!,                                                          // entityRepository
            null!,                                                          // alertRepository
            new StubAuditService(),                                         // auditService
            new StubApiCallLogService(),                                    // apiCallLogService
            null!,                                                          // alertRuleCatalogueRepository
            null!,                                                          // alertRuleEngine
            completion ?? new RecordingCompletionNotifier(),                // completionNotifier
            null!,                                                          // legajoDocumentRepository — background re-validation only
            null!,                                                          // analysisRunStateRepository — background only
            NullLogger<AnalysisPipeline>.Instance);

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            docRepo,                                                         // documentRepository
            new StubAnalysisResultRepository(),                             // analysisResultRepository
            analysisRepo,                                                   // analysisRepository
            claude!,                                                        // claudeService
            null!,                                                          // cashFlowReconciliationService
            null!,                                                          // cashFlowNarrativeService
            new AlertRatioReconciliationService(),                          // alertRatioReconciliationService — real, stateless; existing tests use BalanceJson: "" so this is a no-op for them
            null!,                                                          // entityRepository
            new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid(), DisplayName = "Someone" } }, // userRepository
            null!,                                                          // chatRepository
            null!,                                                          // creditLineRepository
            null!,                                                          // operationRepository
            new StubAuditService(),                                         // auditService
            new StubApiCallLogService(),                                    // apiCallLogService
            null!,                                                          // excelCashFlowParser
            Options.Create(billing ?? new BillingOptions()),                 // billingOptions — real defaults (extraction budget = 2)
            null!,                                                          // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService — this suite never reaches a quorum vote
            legajos ?? new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            pipeline,                                                       // analysisPipeline — real, over the same stubs
            null!,                                                          // analysisJobQueue — the synchronous path never queues
            new AnalysisRunRegistry(Options.Create(new AnalysisOptions())), // analysisRunRegistry
            null!,                                                          // analysisRunStateRepository — the synchronous path never claims
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            logger ?? NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── Tests ───────────────────────────────────────────────────────────────
    /// <summary>
    /// A non-cancellation fault after "Processing" (here: the stored file is gone, the exact
    /// production bug) must leave the row terminal ("Failed"), not stranded, and still surface
    /// the original exception.
    /// </summary>
    /// <summary>
    /// A persona humana legajo document (DDL 070) never reaches balance extraction: it may go to the
    /// model only through the legajo pipeline, which checks consent. Refused with 400 before the
    /// "Processing" flip, the storage read and any Claude call.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_LegajoDocument_Returns400_BeforeAnyWorkOrClaudeCall()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.PersonLegajoId = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var storage = new StubDocumentStorageService { ReadThrows = new InvalidOperationException("storage must not be read") };
        var sut     = BuildSut(docRepo, storage, claude: null);

        var result = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        var details = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.ProblemDetails>(problem.Value);
        Assert.Equal("legajo_document_not_analyzable", details.Extensions["errorCode"]);
        Assert.Equal("Bad Request", details.Title);
        Assert.Empty(docRepo.StatusWrites);
        Assert.Empty(docRepo.RecordedExtractionFailures);
    }

    /// <summary>
    /// A persona humana's paperwork uploaded WITHOUT a legajo (e.g. as "Otro"): the document carries no
    /// PersonLegajoId, but its ENTITY is linked to a legajo. Without active consent on that legajo the
    /// trigger refuses with 409 consent_required — before any status write or Claude call.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_PersonaHumanaEntityWithoutConsent_Returns409ConsentRequired()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        var legajos  = new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository();
        legajos.AddLegajo(tenantId: Tenant, consent: false, entityId: document.EntityId);
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var storage = new StubDocumentStorageService { ReadThrows = new InvalidOperationException("storage must not be read") };
        var sut     = BuildSut(docRepo, storage, claude: null, legajos: legajos);

        var result = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("consent_required", Assert.IsType<ProblemDetails>(conflict.Value).Extensions["errorCode"]);
        Assert.Empty(docRepo.StatusWrites);
    }

    [Fact]
    public async Task TriggerAsync_PersonaHumanaEntityWhoseAuthorizationWasDeleted_Returns409ConsentRequired()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        var legajos  = new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository();
        var legajo   = legajos.AddLegajo(tenantId: Tenant, consent: true, entityId: document.EntityId);
        legajos.Get(legajo.ConsentDocumentId!.Value).IsDeleted = true;
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var sut     = BuildSut(docRepo, new StubDocumentStorageService(), claude: null, legajos: legajos);

        var result = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(docRepo.StatusWrites);
    }

    [Fact]
    public async Task TriggerAsync_PersonaHumanaEntityWithActiveConsent_Proceeds()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        var legajos  = new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository();
        legajos.AddLegajo(tenantId: Tenant, consent: true, entityId: document.EntityId);
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var sut     = BuildSut(docRepo, storage, claude, legajos);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.Equal("Completed", docRepo.StatusWrites[^1]);
    }

    [Fact]
    public async Task TriggerAsync_WhenStorageReadThrows_MarksDocumentFailed()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadThrows = new FileNotFoundException("file gone") };
        var sut     = BuildSut(docRepo, storage);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => sut.TriggerAsync(BuildRequest(docId), CancellationToken.None));

        Assert.Equal(new[] { "Processing", "Failed" }, docRepo.StatusWrites);
    }

    /// <summary>
    /// A cancellation BEFORE the server owns the work (here: the client disconnects during the
    /// storage read, which uses the request token) is a plain abandonment — nothing is in
    /// flight, so the row must reach the terminal, re-analysable "Failed" state, not stay
    /// stranded in "Processing". The original cancellation still propagates.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenStorageReadCancelled_MarksDocumentFailed()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadThrows = new OperationCanceledException() };
        var sut     = BuildSut(docRepo, storage);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.TriggerAsync(BuildRequest(docId), CancellationToken.None));

        Assert.Equal(new[] { "Processing", "Failed" }, docRepo.StatusWrites);
    }

    /// <summary>
    /// A cancellation AT/AFTER the Claude call (once the server owns the work and runs on
    /// CancellationToken.None) must NEVER be turned into "Failed" — the regression guard for
    /// MAYTOM, where the gateway cut the client at ~230s while the server completed at 192s.
    /// The row is left untouched ("Processing" only, no "Failed") and the cancellation propagates.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenClaudeCallCancelled_DoesNotMarkFailed()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService { AnalyzeThrows = new OperationCanceledException() };
        var sut     = BuildSut(docRepo, storage, claude);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.TriggerAsync(BuildRequest(docId), CancellationToken.None));

        Assert.Equal(new[] { "Processing" }, docRepo.StatusWrites);
        Assert.DoesNotContain("Failed", docRepo.StatusWrites);
    }

    /// <summary>
    /// The happy path is unaffected by the guard: the row ends "Completed" (never "Failed") and
    /// the endpoint returns 201 Created.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenPipelineSucceeds_MarksDocumentCompleted()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var sut     = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.Equal("Completed", docRepo.StatusWrites[^1]);
        Assert.DoesNotContain("Failed", docRepo.StatusWrites);
    }

    /// <summary>
    /// TriggerAsync's write-time reconciliation (PR #125, extended here to anomalies by
    /// verifiable-percentages v2.15) rewrites a wrong-denominator percentage inside a model-
    /// authored anomaly string before it is persisted — same fix already covered for
    /// RiskAlerts/ExecutiveSummary. Populating BalanceJson is safe even with
    /// ratioCalculationService/scoringService stubbed null!: both failures are already
    /// fault-isolated (see class doc) and the anomaly-reconciliation block runs earlier in the
    /// pipeline, before the scoring call that fails.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_AnomalyWithWrongPercentage_PersistsCorrectedText()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService
        {
            ResultToReturn = BuildClaudeResult() with
            {
                BalanceJson   = """{"assets": 12239430766.71}""",
                AnomaliesJson = """["Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del activo total de $12.239.430.766,71."]""",
            }
        };
        var sut = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedAtActionResult>(response).Value);
        Assert.Contains("51,0% del activo total", body.Anomalies);
        Assert.DoesNotContain("55,4%", body.Anomalies);
    }

    // ── Balance integrity (M11) ───────────────────────────────────────────────

    /// <summary>
    /// The model's self-reported balance_check cannot see two wrong subtotals that still add up to
    /// the right total; the deterministic integrity checks can. A failed check turns the persisted
    /// ValidationStatus to Warning even when the model said balanced=true.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_IntegrityFailed_ValidationStatusIsWarning()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService
        {
            ResultToReturn = BuildClaudeResult() with { BalanceIntegrityPassed = false }
        };
        var sut = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedAtActionResult>(response).Value);
        Assert.Equal("Warning", body.ValidationStatus);
    }

    /// <summary>
    /// Reliability policy 5: net_profit_reconciliation as the ONLY failed check is the income statement's
    /// presentation; the balance sheet adds up, so the persisted flag stays Passed. Beside any other failed check
    /// it is a Warning as before.
    /// </summary>
    [Theory]
    [InlineData(new[] { "net_profit_reconciliation" }, false, "Passed")]
    [InlineData(new[] { "net_profit_reconciliation" }, true,  "Warning")]   // a sign-flipped net profit is a misread
    [InlineData(new[] { "net_profit_reconciliation", "current_assets_items" }, false, "Warning")]
    public async Task TriggerAsync_NetProfitOnlyFailure_ValidationStatusIsPassed(string[] failedChecks, bool signFlip, string expected)
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService
        {
            ResultToReturn = BuildClaudeResult() with
            {
                BalanceIntegrityPassed       = false,
                BalanceIntegrityFailedChecks      = failedChecks,
                BalanceIntegrityNetProfitSignFlip = signFlip,
            }
        };
        var sut = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedAtActionResult>(response).Value);
        Assert.Equal(expected, body.ValidationStatus);
    }

    /// <summary>
    /// null — older output, or the integrity evaluation failed — keeps the model-only rule, and a
    /// passed evaluation changes nothing either.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task TriggerAsync_IntegrityPassedOrUnknown_KeepsTheModelRule(bool? integrityPassed)
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService
        {
            ResultToReturn = BuildClaudeResult() with { BalanceIntegrityPassed = integrityPassed }
        };
        var sut = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedAtActionResult>(response).Value);
        Assert.Equal("Passed", body.ValidationStatus);
    }

    /// <summary>
    /// A balance "expresado en miles" persists pesos (amounts_scale = 1000) while the model's prose
    /// quotes the printed amounts. The write-time reconciliation must compare like with like, so a
    /// CORRECT percentage stays untouched instead of being "corrected" by a factor of 1000.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ThousandsBalance_CorrectPercentageQuotingPrintedAmountsIsUntouched()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService
        {
            ResultToReturn = BuildClaudeResult() with
            {
                BalanceJson   = """{"assets": 12239430766710, "amounts_unit": "miles", "amounts_scale": 1000}""",
                AnomaliesJson = """["Los bienes de cambio ascienden a $6.236.534.182,74, representando el 51,0% del activo total."]""",
            }
        };
        var sut = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedAtActionResult>(response).Value);
        Assert.Contains("51,0% del activo total", body.Anomalies);
    }

    // ── Completion notification ───────────────────────────────────────────────
    //
    // The analyst may leave the processing screen; the bell is where they learn how the run ended.
    // Exactly one notification per run that the server owned, and none for a synchronous refusal the
    // caller sees on the screen. (429 is not covered here: it comes from [BillingQuota] —
    // BillingQuotaFilter short-circuits before TriggerAsync runs, so the action body never executes.)

    /// <summary>
    /// A clean run notifies "completed" exactly once, pointing at the ResultId the 201 route names —
    /// the id the Portal's /dashboard/analyses/{id} deep link needs.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenPipelineSucceeds_NotifiesCompletedOnce_WithTheResultId()
    {
        var docId      = Guid.NewGuid();
        var docRepo    = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage    = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude     = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var completion = new RecordingCompletionNotifier();
        var sut        = BuildSut(docRepo, storage, claude, completion: completion);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(response);
        var call    = Assert.Single(completion.Completed);
        Assert.Equal(created.RouteValues!["id"], call.ResultId);
        Assert.NotEqual(call.AnalysisId, call.ResultId);
        Assert.Equal(2024, call.FiscalYear);
        Assert.Empty(completion.Failed);
    }

    /// <summary>
    /// Claude gives up after the server owned the work: the document is "Failed", the caller gets 502,
    /// and the requester is told once — they may have left the screen minutes ago.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenClaudeFails_NotifiesFailedOnce()
    {
        var docId      = Guid.NewGuid();
        var request    = BuildRequest(docId);
        var docRepo    = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage    = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude     = new StubClaudeService { AnalyzeThrows = new ClaudeApiException("upstream 529", 529) };
        var completion = new RecordingCompletionNotifier();
        var sut        = BuildSut(docRepo, storage, claude, completion: completion);

        var response = await sut.TriggerAsync(request, CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal("Failed", docRepo.StatusWrites[^1]);
        var call = Assert.Single(completion.Failed);
        Assert.Equal(docId, call.DocumentId);
        Assert.Equal(request.EntityId, call.EntityId);
        Assert.Empty(completion.Completed);
    }

    /// <summary>
    /// An answer no JSON could be read from is not an outage: the 502 says so and asks for a retry. A file
    /// with no financial statements asks for the complete PDF. Every other model failure keeps the
    /// "temporarily unavailable" text. Same status either way.
    /// </summary>
    [Theory]
    [InlineData("unparseable",   ClaudeApiException.UnparseableAnswerClientMessage)]
    [InlineData("no_statements", ClaudeApiException.NoFinancialStatementsClientMessage)]
    [InlineData("schema",        ClaudeApiException.DefaultClientMessage)]
    public async Task TriggerAsync_WhenClaudeFails_WordsThe502ByTheFailure(string kind, string expectedDetail)
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var failure = kind switch
        {
            "unparseable"   => new ClaudeApiException("no readable JSON", clientMessage: ClaudeApiException.UnparseableAnswerClientMessage)
                               { FailureCode = ClaudeService.AnswerUnparseable },
            "no_statements" => new ClaudeApiException("read no financial statements", clientMessage: ClaudeApiException.NoFinancialStatementsClientMessage)
                               { FailureCode = ClaudeService.AnswerNoFinancialStatements },
            _               => new ClaudeApiException("schema") { FailureCode = ClaudeService.AnswerSchemaInvalid },
        };
        var claude  = new StubClaudeService { AnalyzeThrows = failure };
        var sut     = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(502, problem.StatusCode);
        Assert.Equal(expectedDetail, Assert.IsType<ProblemDetails>(problem.Value).Detail);
    }

    /// <summary>
    /// The 502 also carries, as ProblemDetails extensions, the failure's finer reason, whose move it is and
    /// what the FILE has left of its budget — re-read AFTER the run, so a charged failure (claude_api_error)
    /// is already counted and an uncharged one (no statements, a 5xx) is not. Status and detail unchanged.
    /// </summary>
    [Theory]
    [InlineData("no_statements", "no_financial_statements", "document", 1)]
    [InlineData("server_5xx",    "service_unavailable",     "ours",     1)]
    [InlineData("schema",        "unreadable_document",     "document", 0)]
    [InlineData("api_400",       "file_rejected",           "document", 0)]
    public async Task TriggerAsync_WhenClaudeFails_The502CarriesReasonGroupAndAttemptsLeft(
        string kind, string expectedReason, string expectedGroup, int expectedRemaining)
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.FileHashSHA256 = "abc123";
        var docRepo  = new StubDocumentRepository { DocumentToReturn = document, ExtractionAttempts = 1 };
        var storage  = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var failure  = kind switch
        {
            "no_statements" => new ClaudeApiException("read no financial statements", clientMessage: ClaudeApiException.NoFinancialStatementsClientMessage)
                               { FailureCode = ClaudeService.AnswerNoFinancialStatements },
            "server_5xx"    => new ClaudeApiException("upstream 503", 503),
            "api_400"       => new ClaudeApiException("upstream 400", 400),
            _               => new ClaudeApiException("schema") { FailureCode = ClaudeService.AnswerSchemaInvalid },
        };
        var sut = BuildSut(docRepo, storage, new StubClaudeService { AnalyzeThrows = failure });

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var result  = Assert.IsType<ObjectResult>(response);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal(502, problem.Status);
        Assert.Equal(kind == "no_statements"
            ? ClaudeApiException.NoFinancialStatementsClientMessage
            : ClaudeApiException.DefaultClientMessage, problem.Detail);
        Assert.Equal(expectedReason, problem.Extensions["failureReason"]);
        Assert.Equal(expectedGroup, problem.Extensions["causeGroup"]);
        Assert.Equal(expectedRemaining, problem.Extensions["attemptsRemaining"]);
        Assert.False(problem.Extensions.ContainsKey("lastFailureCode"));
        Assert.DoesNotContain(problem.Extensions.Values, v => v is string text && text.StartsWith("claude_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TriggerAsync_WhenClaudeFails_WithTheBudgetOff_AttemptsRemainingIsNull()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.FileHashSHA256 = "abc123";
        var docRepo  = new StubDocumentRepository { DocumentToReturn = document };
        var storage  = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var sut      = BuildSut(docRepo, storage, new StubClaudeService { AnalyzeThrows = new ClaudeApiException("upstream 503", 503) },
            billing: new BillingOptions { MaxExtractionAttemptsPerDocument = 0 });

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(
            await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None)).Value);

        Assert.Equal(502, problem.Status);
        Assert.Equal("service_unavailable", problem.Extensions["failureReason"]);
        Assert.True(problem.Extensions.ContainsKey("attemptsRemaining"));
        Assert.Null(problem.Extensions["attemptsRemaining"]);
    }

    [Theory]
    [InlineData(false)] // blank hash: never a match
    [InlineData(true)]  // the re-read fails: the 502 still goes out
    public async Task TriggerAsync_WhenClaudeFails_AnUnknownCount_IsNull_AndThe502Stands(bool readThrows)
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        if (readThrows)
            document.FileHashSHA256 = "abc123";
        var docRepo  = new StubDocumentRepository { DocumentToReturn = document };
        var storage  = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude   = new StubClaudeService { AnalyzeThrows = new ClaudeApiException("upstream 503", 503) };
        var sut      = BuildSut(docRepo, storage, claude);
        // Only the re-read after the run fails: the trigger's own count (fail-open) has already run.
        claude.OnAnalyze = () => docRepo.SumThrows = readThrows ? new InvalidOperationException("database unreachable") : null;

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(
            await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None)).Value);

        Assert.Equal(502, problem.Status);
        Assert.Null(problem.Extensions["attemptsRemaining"]);
    }

    /// <summary>
    /// A fault after the Claude call (here: persisting the Analysis row) drives the document to
    /// "Failed" through the catch-all, notifies "failed" once, and still rethrows the original fault.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenPersistenceFailsAfterClaude_NotifiesFailedOnce_AndRethrows()
    {
        var docId      = Guid.NewGuid();
        var docRepo    = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage    = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude     = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var analyses   = new StubAnalysisRepository { CreateThrows = new TimeoutException("sql timeout") };
        var completion = new RecordingCompletionNotifier();
        var sut        = BuildSut(docRepo, storage, claude, completion: completion, analyses: analyses);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.TriggerAsync(BuildRequest(docId), CancellationToken.None));

        Assert.Equal(new[] { "Processing", "Failed" }, docRepo.StatusWrites);
        Assert.Single(completion.Failed);
        Assert.Empty(completion.Completed);
    }

    /// <summary>
    /// A fault BEFORE the server owned the work reaches "Failed" too, but within seconds and while the
    /// caller is still on the screen, so it is not announced in the bell.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenStorageReadThrows_DoesNotNotify()
    {
        var docId      = Guid.NewGuid();
        var docRepo    = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage    = new StubDocumentStorageService { ReadThrows = new FileNotFoundException("file gone") };
        var completion = new RecordingCompletionNotifier();
        var sut        = BuildSut(docRepo, storage, completion: completion);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => sut.TriggerAsync(BuildRequest(docId), CancellationToken.None));

        Assert.Equal(new[] { "Processing", "Failed" }, docRepo.StatusWrites);
        Assert.Empty(completion.Failed);
        Assert.Empty(completion.Completed);
    }

    /// <summary>
    /// Synchronous refusals — the caller is still on the screen and sees the error — never notify.
    /// </summary>
    [Theory]
    [InlineData("Processing", 0)] // 409 DenyProcessing
    [InlineData("Pending",    2)] // 409 extraction budget exhausted (BillingOptions default: 2)
    public async Task TriggerAsync_WhenRefusedWith409_DoesNotNotify(string processingStatus, int extractionAttempts)
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.ProcessingStatus = processingStatus;
        var docRepo    = new StubDocumentRepository { DocumentToReturn = document, ExtractionAttempts = extractionAttempts };
        var completion = new RecordingCompletionNotifier();
        var sut        = BuildSut(docRepo, new StubDocumentStorageService(), completion: completion);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Empty(docRepo.StatusWrites);
        Assert.Empty(completion.Completed);
        Assert.Empty(completion.Failed);
    }

    [Fact]
    public async Task TriggerAsync_LegajoDocument_DoesNotNotify()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.PersonLegajoId = Guid.NewGuid();
        var completion = new RecordingCompletionNotifier();
        var sut        = BuildSut(new StubDocumentRepository { DocumentToReturn = document },
            new StubDocumentStorageService(), completion: completion);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Empty(completion.Completed);
        Assert.Empty(completion.Failed);
    }

    /// <summary>
    /// The notification is strictly secondary: a notifier that throws cannot cost the analyst the
    /// 201, nor turn a completed document into a failed one.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenCompletionNotifierThrows_StillReturns201()
    {
        var docId      = Guid.NewGuid();
        var docRepo    = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage    = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude     = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var completion = new RecordingCompletionNotifier { Throws = new InvalidOperationException("Invalid object name 'dbo.Notifications'.") };
        var sut        = BuildSut(docRepo, storage, claude, completion: completion);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.Equal("Completed", docRepo.StatusWrites[^1]);
        Assert.DoesNotContain("Failed", docRepo.StatusWrites);
        Assert.Single(completion.Completed);
    }

    /// <summary>
    /// On the catch-all path the notifier runs before the rethrow; if it throws, the ORIGINAL fault
    /// must still be what surfaces.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenFailureNotifierThrows_TheOriginalFaultStillSurfaces()
    {
        var docId      = Guid.NewGuid();
        var docRepo    = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage    = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude     = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var analyses   = new StubAnalysisRepository { CreateThrows = new TimeoutException("sql timeout") };
        var completion = new RecordingCompletionNotifier { Throws = new InvalidOperationException("notifier down") };
        var sut        = BuildSut(docRepo, storage, claude, completion: completion, analyses: analyses);

        await Assert.ThrowsAsync<TimeoutException>(
            () => sut.TriggerAsync(BuildRequest(docId), CancellationToken.None));

        Assert.Single(completion.Failed);
    }

    // ── Extraction guards, phase 1: the document must be THIS entity's balance ──

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// The balance is read from the document while BCRA, LUFE and CASFOG come from the request's
    /// entity: a document of another entity is refused with 400 document_entity_mismatch before the
    /// "Processing" flip, the storage read and any Claude call (claude is null: reaching it would throw).
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DocumentOfAnotherEntity_Returns400_BeforeAnyWorkOrClaudeCall()
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.EntityId = Guid.NewGuid();   // not FixtureEntityId, which the request names
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var storage = new StubDocumentStorageService { ReadThrows = new InvalidOperationException("storage must not be read") };
        var sut     = BuildSut(docRepo, storage, claude: null);

        var result = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        Assert.Equal("document_entity_mismatch", details.Extensions["errorCode"]);
        Assert.Equal("El documento no pertenece a la entidad indicada en la solicitud.", details.Detail);
        Assert.Empty(docRepo.StatusWrites);
        Assert.Empty(docRepo.RecordedExtractionFailures);
    }

    [Theory]
    [InlineData("Otro", "Otro")]
    [InlineData("Complemento", "Complemento")]
    [InlineData("certificado", "Certificado")]
    [InlineData("ManifestacionBienes", "ManifestacionBienes")]
    public async Task TriggerAsync_DocumentThatIsNotABalance_Returns400_BeforeAnyWorkOrClaudeCall(string storedType, string shownType)
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.DocumentType = storedType;
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var storage = new StubDocumentStorageService { ReadThrows = new InvalidOperationException("storage must not be read") };
        var sut     = BuildSut(docRepo, storage, claude: null);

        var result = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        Assert.Equal("document_not_balance", details.Extensions["errorCode"]);
        Assert.Equal(
            $"El documento no es un balance (tipo «{shownType}»). Solo los documentos de tipo Balance pueden analizarse.",
            details.Detail);
        Assert.Empty(docRepo.StatusWrites);
        Assert.Empty(docRepo.RecordedExtractionFailures);
    }

    /// <summary>
    /// The legacy upload validated the type case-insensitively but stored it as sent, so a real balance
    /// may read "balance". It is analysed, with a Warning naming the document and its stored type.
    /// </summary>
    [Theory]
    [InlineData("balance")]
    [InlineData(" BALANCE ")]
    public async Task TriggerAsync_LegacySpellingOfBalance_LogsAWarningAndProceeds(string storedType)
    {
        var docId    = Guid.NewGuid();
        var document = BuildPendingDocument(docId);
        document.DocumentType = storedType;
        var docRepo = new StubDocumentRepository { DocumentToReturn = document };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var logger  = new RecordingLogger<AnalysisController>();
        var sut     = BuildSut(docRepo, storage, claude, logger: logger);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.Equal("Completed", docRepo.StatusWrites[^1]);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains(docId.ToString(), StringComparison.Ordinal)
            && e.Message.Contains(storedType, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Balance",        "Balance",             "Balance")]
    [InlineData("balance",        "BalanceNonCanonical", "Balance")]
    [InlineData("Otro",           "NotBalance",          "Otro")]
    [InlineData("DdjjIva",        "NotBalance",          "DdjjIva")]
    [InlineData("EstadoContable", "Unknown",             null)]
    [InlineData("",               "Unknown",             null)]
    public void ClassifyDocumentType_ReadsTheCkVocabularyCaseInsensitively(
        string storedType, string expected, string? canonical)
    {
        var (verdict, resolved) = AnalysisController.ClassifyDocumentType(storedType);

        Assert.Equal(expected, verdict.ToString());
        Assert.Equal(canonical, resolved);
    }

    /// <summary>
    /// The synchronous trigger's response carries the new extractionIntegrity members: verified (the
    /// three structural checks ran) and the fiscal year read vs requested, plus the es-AR anomaly.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ResponseCarriesVerifiedAndTheFiscalYearCheck()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildPendingDocument(docId) };
        var storage = new StubDocumentStorageService { ReadReturns = new byte[] { 1, 2, 3 } };
        var claude  = new StubClaudeService
        {
            ResultToReturn = BuildClaudeResult() with
            {
                // The request asks for 2024; the document states 2023.
                BalanceJson =
                    """
                    {"assets": 100, "fiscal_year_detected": 2023,
                     "integrity": {"passed": true, "skipped": [], "affectedRatios": [],
                                   "amountsUnit": "miles", "amountsUnitAssumed": false,
                                   "checks": [{"name": "assets_components", "passed": true},
                                              {"name": "liabilities_components", "passed": true},
                                              {"name": "accounting_equation", "passed": true}]}}
                    """,
            }
        };
        var sut = BuildSut(docRepo, storage, claude);

        var response = await sut.TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var body      = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedAtActionResult>(response).Value);
        var integrity = Assert.IsType<ExtractionIntegrityResponse>(body.ExtractionIntegrity);
        Assert.True(integrity.Verified);
        Assert.Empty(integrity.NotVerifiedReasons);
        Assert.Equal("miles", integrity.AmountsUnit);
        Assert.False(integrity.AmountsUnitAssumed);
        var fiscalYear = Assert.IsType<ExtractionFiscalYearResponse>(integrity.FiscalYear);
        Assert.Equal(2024, fiscalYear.Requested);
        Assert.Equal(2023, fiscalYear.Detected);
        Assert.Equal("mismatch", fiscalYear.Status);
        Assert.Contains(
            "El ejercicio detectado en el documento (2023) no coincide con el ejercicio del análisis (2024).",
            System.Text.Json.JsonSerializer.Deserialize<List<string>>(body.Anomalies!)!);
    }
}
