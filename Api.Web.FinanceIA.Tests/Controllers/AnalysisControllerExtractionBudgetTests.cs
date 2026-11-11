// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using System.Security.Claims;
using System.Text.Json;
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
/// Covers the extraction budget on <see cref="AnalysisController.TriggerAsync"/> — the guard that
/// stops an unreadable balance from buying billable Claude extractions without limit.
///
/// <para>
/// The two halves it pins are the two halves of the billing rule. First, a failed extraction must
/// cost the tenant NO monthly análisis: the <c>[dbo].[Analyses]</c> row the quota counts is written
/// only after Claude answers, so a failure must leave that repository untouched. Second, precisely
/// because the failure is free, something else has to bound the spend — so once the file's budget
/// is gone the trigger must refuse BEFORE the "Processing" flip and before any Claude call, and the
/// budget must follow the file's content hash so re-uploading the same bytes cannot reset it.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs (no mocking library), in the shape used by
/// <see cref="AnalysisControllerTriggerGuardTests"/>. Only the dependencies this path touches are
/// stubbed; the rest of the ctor dependencies are passed as null! (the ctor does plain assignment,
/// and the fault-isolated enrichment blocks swallow any NRE they would raise). Argument order
/// mirrors the ctor signature.
/// </para>
/// </summary>
public sealed class AnalysisControllerExtractionBudgetTests
{
    private const string Tenant      = "tenant-1";

    // The document and the request name the SAME entity: TriggerAsync refuses a document of another
    // entity (document_entity_mismatch) before any work.
    private static readonly Guid FixtureEntityId = Guid.Parse("6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b");
    private const string FirebaseUid = "uid-1";
    private const string FileHash    = "aaaa000000000000000000000000000000000000000000000000000000000000";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public Document? DocumentToReturn { get; set; }

        /// <summary>Attempts the budget query reports, keyed by hash — the production sum is per FILE.</summary>
        public Dictionary<string, int> AttemptsByHash { get; } = new();

        /// <summary>When set, the budget read throws this — exercising the fail-open path.</summary>
        public Exception? SumThrows { get; set; }

        /// <summary>The stored LastExtractionError of the file's newest failure, keyed by hash.</summary>
        public Dictionary<string, string?> LatestErrorByHash { get; } = new();

        /// <summary>When set, the latest-error read throws this — exercising its fail-open path.</summary>
        public Exception? LatestErrorThrows { get; set; }

        /// <summary>Ordered spy of every ProcessingStatus written via UpdateStatusAsync.</summary>
        public List<string> StatusWrites { get; } = new();

        /// <summary>Ordered spy of every reason code written via RecordExtractionFailureAsync.</summary>
        public List<string?> RecordedExtractionFailures { get; } = new();

        public Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => Task.FromResult(DocumentToReturn);

        public Task UpdateStatusAsync(Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
        {
            StatusWrites.Add(status);
            return Task.CompletedTask;
        }

        public Task<int> SumExtractionAttemptsByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
        {
            if (SumThrows is not null)
                throw SumThrows;

            return Task.FromResult(
                fileHashSha256 is not null && AttemptsByHash.TryGetValue(fileHashSha256, out var spent)
                    ? spent
                    : 0);
        }

        public Task<Document?> FindRecentByHashAsync(Guid entityId, string tenantId, string? fileHashSha256, string documentType, DateTime uploadedSinceUtc, CancellationToken ct)
            => Task.FromResult<Document?>(null);

        public Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
        {
            if (LatestErrorThrows is not null)
                throw LatestErrorThrows;

            return Task.FromResult(
                fileHashSha256 is not null && LatestErrorByHash.TryGetValue(fileHashSha256, out var stored)
                    ? stored
                    : null);
        }

        public Task RecordExtractionFailureAsync(
            Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct)
        {
            RecordedExtractionFailures.Add(error);
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
    }

    private sealed class StubDocumentStorageService : IDocumentStorageService
    {
        public byte[] ReadReturns { get; set; } = new byte[] { 1, 2, 3 };

        public Task<byte[]> ReadAsync(string storagePath, CancellationToken ct)
            => Task.FromResult(ReadReturns);

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

        /// <summary>How many times the extraction was actually paid for.</summary>
        public int AnalyzeCallCount { get; private set; }

        public Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
        {
            AnalyzeCallCount++;
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
        /// <summary>The base figures of the latest analysis's balance (§5.8); null = no result row.</summary>
        public BalanceBaseFiguresProjection? FiguresToReturn { get; set; }

        /// <summary>When set, the base-figure read throws this.</summary>
        public Exception? FiguresThrows { get; set; }

        /// <summary>The analysis ids the base figures were read for.</summary>
        public List<Guid> FiguresReadFor { get; } = new();

        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
        {
            FiguresReadFor.Add(analysisId);
            return FiguresThrows is not null
                ? Task.FromException<BalanceBaseFiguresProjection?>(FiguresThrows)
                : Task.FromResult(FiguresToReturn);
        }

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        // The quorum-crossing write (DDL 067). Throws because no test in this suite confirms a
        // pre-approval, so an unexpected call must fail loudly rather than pass silently.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        /// <summary>Rows written to [dbo].[Analyses] — what the monthly quota counts.</summary>
        public List<Analysis> Created { get; } = new();

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
        {
            Created.Add(analysis);
            return Task.FromResult(analysis);
        }

        /// <summary>The document's most recent analysis, consulted only for a Completed document.</summary>
        public Analysis? LatestToReturn { get; set; }

        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => Task.FromResult(LatestToReturn);

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
        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static Document BuildDocument(Guid documentId, string processingStatus = "Failed") => new()
    {
        DocumentId       = documentId,
        TenantId         = Tenant,
        EntityId         = FixtureEntityId,
        StoragePath      = "/tmp/financia-uploads/tenant-1/doc.pdf",
        ProcessingStatus = processingStatus,
        IsDeleted        = false,
        DocumentType     = "Balance",
        FiscalYear       = 2024,
        FileHashSHA256   = FileHash
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
        StubDocumentRepository docRepo,
        StubClaudeService      claude,
        StubAnalysisRepository analysisRepo,
        int                    maxExtractionAttempts = 2)
    {
        // The long part of TriggerAsync lives in AnalysisPipeline; it gets the dependencies the
        // controller used to hold for it, so every assertion below still describes the endpoint.
        var pipeline = new AnalysisPipeline(
            docRepo,                                                        // documentRepository
            new StubAnalysisResultRepository(),                             // analysisResultRepository
            analysisRepo,                                                   // analysisRepository
            new StubDocumentStorageService(),                               // storageService
            claude,                                                         // claudeService
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
            null!,                                                          // completionNotifier — its guard absorbs the NRE, as before
            null!,                                                          // legajoDocumentRepository — background re-validation only
            null!,                                                          // analysisRunStateRepository — background only
            NullLogger<AnalysisPipeline>.Instance);

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            docRepo,                                                         // documentRepository
            new StubAnalysisResultRepository(),                             // analysisResultRepository
            analysisRepo,                                                   // analysisRepository
            claude,                                                         // claudeService
            null!,                                                          // cashFlowReconciliationService
            null!,                                                          // cashFlowNarrativeService
            new AlertRatioReconciliationService(),                          // alertRatioReconciliationService — real, stateless; BalanceJson is "" so it is a no-op here
            null!,                                                          // entityRepository
            new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid(), DisplayName = "Someone" } }, // userRepository
            null!,                                                          // chatRepository
            null!,                                                          // creditLineRepository
            null!,                                                          // operationRepository
            new StubAuditService(),                                         // auditService
            new StubApiCallLogService(),                                    // apiCallLogService
            null!,                                                          // excelCashFlowParser
            Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = maxExtractionAttempts }),
            null!,                                                          // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService — this suite never reaches a quorum vote
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            pipeline,                                                       // analysisPipeline — real, over the same stubs
            null!,                                                          // analysisJobQueue — the synchronous path never queues
            new AnalysisRunRegistry(Options.Create(new AnalysisOptions())), // analysisRunRegistry
            null!,                                                          // analysisRunStateRepository — the synchronous path never claims
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    /// <summary>
    /// The status code of any result these tests can produce. CreatedAtActionResult derives from
    /// ObjectResult, so the first arm covers the 201 as well as the ProblemDetails responses.
    /// </summary>
    private static int StatusCodeOf(IActionResult result) => result switch
    {
        ObjectResult o     => o.StatusCode ?? 0,
        StatusCodeResult s => s.StatusCode,
        _                  => 0
    };

    // ── A failed extraction is free ─────────────────────────────────────────────

    /// <summary>
    /// The billing rule the quota depends on: a failed extraction writes NO row to
    /// [dbo].[Analyses], so it cannot consume one of the tenant's monthly análisis.
    /// CountByTenantInPeriodAsync counts those rows, so no row means no charge.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenExtractionFails_WritesNoAnalysisRow()
    {
        var docId        = Guid.NewGuid();
        var docRepo      = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Pending") };
        var analysisRepo = new StubAnalysisRepository();
        var claude       = new StubClaudeService { AnalyzeThrows = new ClaudeApiException("unreadable", 400) };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(502, StatusCodeOf(result));
        Assert.Empty(analysisRepo.Created);          // nothing for the monthly count to find
        Assert.Contains("Failed", docRepo.StatusWrites);
    }

    /// <summary>
    /// ...and precisely because it is free, it must be charged against the file's extraction
    /// budget instead — otherwise nothing at all bounds the Claude spend.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenExtractionFails_ChargesTheExtractionBudget()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Pending") };
        var claude  = new StubClaudeService { AnalyzeThrows = new ClaudeApiException("unreadable", 400) };

        await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Single(docRepo.RecordedExtractionFailures);
        Assert.Equal("claude_api_error", docRepo.RecordedExtractionFailures[0]);
    }

    // ── The budget bounds the spend ─────────────────────────────────────────────

    /// <summary>
    /// The leak this change closes: retrying a document whose extraction already failed its way
    /// through the budget must NOT call Claude again — and must refuse before the "Processing"
    /// flip, since a request refused after the document has moved and the API has been paid for
    /// has not been refused.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenBudgetSpent_DoesNotCallClaudeAgain()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId) };
        docRepo.AttemptsByHash[FileHash] = 2;
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal(0, claude.AnalyzeCallCount);
        Assert.Empty(docRepo.StatusWrites);          // never flipped to "Processing"
    }

    /// <summary>
    /// The re-upload bypass. A fresh Pending document carrying the SAME FileHashSHA256 inherits
    /// the exhausted budget — a per-row budget would reset the moment the analyst re-uploaded the
    /// identical bytes.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenAHashSiblingSpentTheBudget_RefusesTheFreshUpload()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Pending") };
        docRepo.AttemptsByHash[FileHash] = 2;   // spent by a sibling row, not by this document
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal(0, claude.AnalyzeCallCount);
    }

    /// <summary>
    /// The guard must not over-block: one transient Claude blip is absorbed and the analyst keeps
    /// their retry.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenBudgetRemains_ProceedsAndCallsClaude()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId) };
        docRepo.AttemptsByHash[FileHash] = 1;   // one of two spent
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(201, StatusCodeOf(result));
        Assert.Equal(1, claude.AnalyzeCallCount);
    }

    /// <summary>
    /// The documented escape hatch: Billing:MaxExtractionAttemptsPerDocument at 0 disables the
    /// guard entirely, and skips the budget read along with it.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenBudgetIsDisabled_TheGuardIsNotApplied()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository
        {
            DocumentToReturn = BuildDocument(docId),
            // Would throw if the guard read it at all — with the budget off, it must not.
            SumThrows = new InvalidOperationException("must not be queried")
        };
        docRepo.AttemptsByHash[FileHash] = 99;
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository(), maxExtractionAttempts: 0)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(201, StatusCodeOf(result));
        Assert.Equal(1, claude.AnalyzeCallCount);
    }

    /// <summary>
    /// FAIL OPEN, matching BillingQuotaFilter's stated policy: a budget read that throws must not
    /// become an outage for the endpoint it meters. Over-serving for the length of a blip is
    /// recoverable; refusing an analyst's work because a COUNT failed is not.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_WhenTheBudgetReadThrows_TheRequestProceeds()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository
        {
            DocumentToReturn = BuildDocument(docId),
            SumThrows        = new InvalidOperationException("db unavailable")
        };
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(201, StatusCodeOf(result));
        Assert.Equal(1, claude.AnalyzeCallCount);
    }

    /// <summary>
    /// A completed document is proof that we DID read this balance, so a re-analysis after a
    /// rejection must not be refused for budget — even when the file's counter is well past it.
    /// The budget bounds spend on a file we keep failing to read; this one we did not.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_CompletedDocumentIsReanalysableAfterRejection_DespiteASpentBudget()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Completed") };
        docRepo.AttemptsByHash[FileHash] = 9;
        var claude       = new StubClaudeService { ResultToReturn = BuildClaudeResult() };
        var analysisRepo = new StubAnalysisRepository
        {
            LatestToReturn = new Analysis { AnalysisId = Guid.NewGuid(), TenantId = Tenant, Status = "Rejected" }
        };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(201, StatusCodeOf(result));
        Assert.Equal(1, claude.AnalyzeCallCount);
    }

    // ── §5.8: a Completed document whose latest analysis read none of the base figures ──────

    private static readonly BalanceBaseFiguresProjection NoFigures = new() { HasBalance = true };

    private static (StubDocumentRepository DocRepo, StubAnalysisRepository AnalysisRepo, Analysis Latest, Guid DocId)
        CompletedWithLatest(string latestStatus, BalanceBaseFiguresProjection? figures, int attempts = 0)
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Completed") };
        docRepo.AttemptsByHash[FileHash] = attempts;
        var latest = new Analysis { AnalysisId = Guid.NewGuid(), TenantId = Tenant, DocumentId = docId, Status = latestStatus };
        var analysisRepo = new StubAnalysisRepository { LatestToReturn = latest, FiguresToReturn = figures };
        return (docRepo, analysisRepo, latest, docId);
    }

    [Theory]
    [InlineData("PendingReview")]
    [InlineData("Approved")]
    [InlineData("PreApproved")]
    public async Task TriggerAsync_LatestAnalysisReadNoBaseFigures_IsReanalysedWithoutRejectingIt(string latestStatus)
    {
        var (docRepo, analysisRepo, latest, docId) = CompletedWithLatest(latestStatus, NoFigures);
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(201, StatusCodeOf(result));
        Assert.Equal(1, claude.AnalyzeCallCount);
        Assert.Equal(new[] { latest.AnalysisId }, analysisRepo.FiguresReadFor);
        // A NEW analysis; the empty one is left exactly as it was (never written to).
        var created = Assert.Single(analysisRepo.Created);
        Assert.NotEqual(latest.AnalysisId, created.AnalysisId);
        Assert.Equal(latestStatus, latest.Status);
    }

    [Fact]
    public async Task TriggerAsync_LatestAnalysisWithAnyBaseFigure_KeepsTheNotRejected409()
    {
        var figures = new BalanceBaseFiguresProjection { HasBalance = true, NetProfit = "0" };   // a read zero is a figure
        var (docRepo, analysisRepo, _, docId) = CompletedWithLatest("PendingReview", figures);
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal("analysis_not_rejected", problem.Extensions["errorCode"]);
        Assert.Equal(0, claude.AnalyzeCallCount);
    }

    [Fact]
    public async Task TriggerAsync_UnreadableBaseFigures_FailClosedToTheNotRejected409()
    {
        var (docRepo, analysisRepo, _, docId) = CompletedWithLatest("PendingReview", NoFigures);
        analysisRepo.FiguresThrows = new InvalidOperationException("db unavailable");
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal("analysis_not_rejected", problem.Extensions["errorCode"]);
        Assert.Equal(0, claude.AnalyzeCallCount);
    }

    [Fact]
    public async Task TriggerAsync_LatestAnalysisReadNoBaseFigures_StillRespectsTheExtractionBudget()
    {
        var (docRepo, analysisRepo, _, docId) = CompletedWithLatest("PendingReview", NoFigures, attempts: 2);
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal("extraction_budget_exhausted", problem.Extensions["errorCode"]);
        Assert.Equal(0, claude.AnalyzeCallCount);
    }

    [Fact]
    public async Task TriggerAsync_ARejectedLatestAnalysis_NeverReadsTheBaseFigures()
    {
        var (docRepo, analysisRepo, _, docId) = CompletedWithLatest("Rejected", NoFigures);
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        Assert.Equal(201, StatusCodeOf(result));
        Assert.Empty(analysisRepo.FiguresReadFor);
    }

    /// <summary>
    /// A Processing document is a different and more urgent lock, and reporting "budget spent"
    /// while a run is genuinely in flight would be wrong.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ProcessingDocumentStillReportsItsOwnConflict()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Processing") };
        docRepo.AttemptsByHash[FileHash] = 5;
        var claude  = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result).Value as ProblemDetails;
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Contains("Processing", problem!.Detail);
        Assert.Equal(0, claude.AnalyzeCallCount);
    }

    // ── The budget-exhausted 409: its own errorCode and the real cause ──────────

    private const string OurSideDetail =
        "No pudimos completar la lectura de este balance por un problema de nuestro lado. Avisá a soporte para habilitar un nuevo intento.";

    private const string UnreadableDetail =
        "No pudimos leer este balance en los intentos disponibles. Subí un archivo en mejor calidad (un PDF digital o un escaneo más nítido). Volver a subir el mismo archivo no reinicia los intentos.";

    /// <summary>Triggers a document whose FILE spent its budget, the newest failure having stored <paramref name="stored"/>.</summary>
    private static async Task<(ProblemDetails Problem, int Status, StubClaudeService Claude)> TriggerSpentAsync(
        string? stored, Exception? latestErrorThrows = null)
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository
        {
            DocumentToReturn  = BuildDocument(docId, "Pending"),
            LatestErrorThrows = latestErrorThrows
        };
        docRepo.AttemptsByHash[FileHash]    = 2;
        docRepo.LatestErrorByHash[FileHash] = stored;
        var claude = new StubClaudeService { ResultToReturn = BuildClaudeResult() };

        var result = await BuildSut(docRepo, claude, new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        return (problem, StatusCodeOf(result), claude);
    }

    [Fact]
    public async Task BudgetExhausted_AfterAModelError_TellsTheAnalystToCallSupport()
    {
        var (problem, status, claude) = await TriggerSpentAsync("claude_api_error");

        Assert.Equal(409, status);
        Assert.Equal(0, claude.AnalyzeCallCount);
        Assert.Equal("Se agotaron los intentos de lectura", problem.Title);
        Assert.Equal(OurSideDetail, problem.Detail);
        Assert.Equal("https://serviciosfinancieros.api/errors/extraction_budget_exhausted", problem.Type);
        Assert.Equal("extraction_budget_exhausted", problem.Extensions["errorCode"]);
        Assert.Equal("model_error", problem.Extensions["lastFailureCode"]);
    }

    [Theory]
    [InlineData("claude_refusal")]
    [InlineData("claude_unreadable_answer")]
    public async Task BudgetExhausted_AfterAnUnreadableDocument_AsksForABetterFile(string stored)
    {
        var (problem, status, _) = await TriggerSpentAsync(stored);

        Assert.Equal(409, status);
        Assert.Equal("Se agotaron los intentos de lectura", problem.Title);
        Assert.Equal(UnreadableDetail, problem.Detail);
        Assert.Equal("extraction_budget_exhausted", problem.Extensions["errorCode"]);
        Assert.Equal("unreadable", problem.Extensions["lastFailureCode"]);
    }

    /// <summary>
    /// Every cause that is not the document's says "avisá a soporte". Spent budgets recorded before the
    /// finer codes existed (claude_api_error, a TaskCanceledException type name) read the same way.
    /// </summary>
    [Theory]
    [InlineData("claude_timeout",        "model_error")]
    [InlineData("claude_server_error",   "model_error")]
    [InlineData("claude_rate_limited",   "quota")]
    [InlineData("TaskCanceledException", "interrupted")]
    [InlineData("stale_processing",      "interrupted")]
    [InlineData("DbUpdateException",     "other")]
    [InlineData(null,                    "other")]
    public async Task BudgetExhausted_AfterAFailureOnOurSide_TellsTheAnalystToCallSupport(string? stored, string expected)
    {
        var (problem, status, _) = await TriggerSpentAsync(stored);

        Assert.Equal(409, status);
        Assert.Equal(OurSideDetail, problem.Detail);
        Assert.Equal(expected, problem.Extensions["lastFailureCode"]);
    }

    /// <summary>The stored text never leaves the API: only the closed code does.</summary>
    [Fact]
    public async Task BudgetExhausted_NeverEchoesTheStoredText()
    {
        const string stored = "SomeInternalFaultException";

        var (problem, _, _) = await TriggerSpentAsync(stored);

        var body = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain(stored, body, StringComparison.Ordinal);
        Assert.Equal("other", problem.Extensions["lastFailureCode"]);
    }

    /// <summary>Fail-open like the budget sum: a reason that cannot be read still refuses, as "other".</summary>
    [Fact]
    public async Task BudgetExhausted_WhenTheLastFailureCannotBeRead_StillRefusesAsOther()
    {
        var (problem, status, claude) = await TriggerSpentAsync("claude_refusal", new InvalidOperationException("db unavailable"));

        Assert.Equal(409, status);
        Assert.Equal(0, claude.AnalyzeCallCount);
        Assert.Equal("extraction_budget_exhausted", problem.Extensions["errorCode"]);
        Assert.Equal("other", problem.Extensions["lastFailureCode"]);
        Assert.Equal(OurSideDetail, problem.Detail);
    }

    // ── The genuine conflicts keep their 409 and gain an errorCode ──────────────

    [Fact]
    public async Task GenuineConflict_Processing_KeepsItsDetailAndCarriesItsErrorCode()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Processing") };

        var result = await BuildSut(docRepo, new StubClaudeService(), new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal("Conflict", problem.Title);
        Assert.Equal("El documento ya se encuentra en el estado 'Processing'.", problem.Detail);
        Assert.Equal("document_processing", problem.Extensions["errorCode"]);
        Assert.False(problem.Extensions.ContainsKey("lastFailureCode"));
    }

    [Fact]
    public async Task GenuineConflict_ActiveAnalysis_KeepsItsDetailAndCarriesItsErrorCode()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Completed") };
        docRepo.AttemptsByHash[FileHash] = 9;   // a spent budget must not change this answer
        var analysisRepo = new StubAnalysisRepository
        {
            LatestToReturn = new Analysis { AnalysisId = Guid.NewGuid(), TenantId = Tenant, Status = "Approved" }
        };

        var result = await BuildSut(docRepo, new StubClaudeService(), analysisRepo)
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal("Conflict", problem.Title);
        Assert.Equal(
            "El análisis vigente de este documento no fue rechazado, por lo que no puede reanalizarse. Para generar un análisis nuevo, vuelva a subir el balance: se registra como documento nuevo y el análisis vigente no se modifica.",
            problem.Detail);
        Assert.Equal("analysis_not_rejected", problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task GenuineConflict_CompletedWithoutAnalysis_KeepsItsDetailAndCarriesItsErrorCode()
    {
        var docId   = Guid.NewGuid();
        var docRepo = new StubDocumentRepository { DocumentToReturn = BuildDocument(docId, "Completed") };

        var result = await BuildSut(docRepo, new StubClaudeService(), new StubAnalysisRepository())
            .TriggerAsync(BuildRequest(docId), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal("Conflict", problem.Title);
        Assert.Equal("No se encontró un análisis previo para este documento; no puede reanalizarse.", problem.Detail);
        Assert.Equal("analysis_not_found", problem.Extensions["errorCode"]);
    }
}
