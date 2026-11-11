// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
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
/// Covers the two-channel wiring in <see cref="AnalysisController.TriggerAsync"/>:
///
///   • a rule-engine failure NEVER aborts the analysis and NEVER touches the model's findings —
///     the analysis still completes and RiskAlerts is persisted byte-identical;
///   • a firing rule is persisted to CatalogueAlerts and, again, leaves RiskAlerts untouched;
///   • an empty catalogue leaves CatalogueAlerts null rather than an empty array.
///
/// Hand-rolled private sealed stubs (no mock library), mirroring
/// <see cref="AnalysisControllerTriggerGuardTests"/>. Only the dependencies this path touches are
/// stubbed; the rest are passed as null! (the ctor does plain assignment and the fault-isolated
/// enrichment blocks swallow any NRE they would raise). Argument order mirrors the ctor signature.
/// All figures are synthetic.
/// </summary>
public sealed partial class AnalysisControllerCatalogueAlertsTests
{
    private const string Tenant      = "tenant-1";

    // The document and the request name the SAME entity: TriggerAsync refuses a document of another
    // entity (document_entity_mismatch) before any work.
    private static readonly Guid FixtureEntityId = Guid.Parse("6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b");
    private const string FirebaseUid = "uid-1";

    /// <summary>The model's own findings — the `observations` channel. Must survive untouched.</summary>
    private const string ModelAlertsJson =
        """[{"level":"medium","code":"OBSERVACION_DEL_MODELO","title":"Observación del modelo","description":"Texto emitido por el modelo."}]""";

    /// <summary>
    /// Balance whose entire liability side is current (non_current_liabilities = 0, liabilities &gt; 0),
    /// so PASIVO_100_CORRIENTE fires and nothing else does.
    /// </summary>
    private const string BalanceJson =
        """{"assets":100000000,"current_assets":40000000,"non_current_assets":60000000,"liabilities":20000000,"current_liabilities":20000000,"non_current_liabilities":0,"equity":80000000,"net_revenue":120000000,"net_profit":12000000,"operating_result":20000000,"financial_expenses":4000000,"inventory":10000000,"prepaid_expenses":1000000}""";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public Document? DocumentToReturn { get; set; }
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

        // The extraction budget is not what these tests are about: report zero attempts spent so
        // the guard always lets the trigger through, and accept the failure record silently.
        public Task<Document?> FindRecentByHashAsync(Guid entityId, string tenantId, string? fileHashSha256, string documentType, DateTime uploadedSinceUtc, CancellationToken ct)
            => Task.FromResult<Document?>(null);

        public Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<int> SumExtractionAttemptsByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => Task.FromResult(0);

        public Task RecordExtractionFailureAsync(
            Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StubDocumentStorageService : IDocumentStorageService
    {
        public Task<byte[]> ReadAsync(string storagePath, CancellationToken ct)
            => Task.FromResult(Array.Empty<byte>());

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

        public Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
            => Task.FromResult(ResultToReturn!);

        public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
            => throw new NotImplementedException();

        /// <summary>
        /// Canned, with zero tokens so the caller's usage-logging branch stays untaken. This used to
        /// throw; nothing reached it, because every fixture passed a null scoringService and the
        /// scoring block faulted first. A fixture that DOES supply a scoring service needs it.
        /// </summary>
        public Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(ScoringResult scoring, RatiosData ratios, BcraData? bcraData, BcraMacroData? macroData, ScoreReliabilityResponse? reliability, CancellationToken ct)
            => Task.FromResult(new ClaudeNarrativeResult("narrativa de prueba", 0, 0));
    }

    /// <summary>
    /// Returns a canned <see cref="ScoringResult"/> whatever it is handed. The engine's own arithmetic
    /// is pinned by the ScoringService suites; what a controller fixture needs from it is a result
    /// whose every field is a distinguishable constant, so the blob the controller writes can be read
    /// back field by field.
    /// </summary>
    private sealed class StubScoringService : IScoringService
    {
        public ScoringResult ResultToReturn { get; set; } = new();

        public Task<ScoringResult> CalculateAsync(
            string tenantId, string? entitySector, RatiosData ratios,
            BcraData? bcraData, BcraMacroData? macroData,
            List<Api.Web.FinanceIA.Models.Lufe.LufeVentaCompraMes>? ventasMensuales,
            List<CasfogGarantiaSgr>? garantiasPorSgr,
            CancellationToken ct)
            => Task.FromResult(ResultToReturn);

        public Task<ScoreLevelCutoffs> GetLevelCutoffsAsync(string tenantId, CancellationToken ct)
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
        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => Task.FromResult(analysis);
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => Task.FromResult<Analysis?>(null);

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>Captures the persisted result so the two channels can be asserted on.</summary>
    private sealed class CapturingAnalysisResultRepository : IAnalysisResultRepository
    {
        public AnalysisResult? Created { get; private set; }

        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
        {
            Created = result;
            return Task.FromResult(result);
        }

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

    private sealed class StubAlertRepository : IAlertRepository
    {
        public Task CreateManyAsync(IEnumerable<Alert> alerts, CancellationToken ct) => Task.CompletedTask;
        public Task<int> SupersedePreviousEntityAlertsAsync(Guid entityId, string tenantId, Guid? excludeAnalysisId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<IReadOnlyList<Alert>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Alert?> GetByIdAsync(Guid alertId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsReadAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsResolvedAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllAsReadAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByTypeInPeriodAsync(string tenantId, string alertType, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetOpenByTypesForUpdateAsync(string tenantId, IReadOnlyCollection<string> alertTypes, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetOpenByTypesCreatedBeforeForUpdateAsync(string tenantId, IReadOnlyCollection<string> alertTypes, DateTime createdBeforeUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<(string AlertType, string MetricKey), Alert>> GetLatestByMetricKeysAsync(string tenantId, IReadOnlyCollection<string> alertTypes, IReadOnlyCollection<string> metricKeys, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveReconciliationAsync(string tenantId, IReadOnlyCollection<Alert> toCreate, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<PreviousAlertRow>> GetPreviousByKeysAsync(string tenantId, IReadOnlyCollection<string> metricKeys, CancellationToken ct) => throw new NotImplementedException();
        public Task<AlertUnresolveOutcome> UnresolveAsync(Guid alertId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAlertRuleCatalogueRepository : IAlertRuleCatalogueRepository
    {
        public List<AlertRuleDefinition> RulesToReturn { get; set; } = new();
        public Exception? ThrowOnCall { get; set; }

        public Task<IReadOnlyList<AlertRuleDefinition>> GetEnabledAsync(string tenantId, CancellationToken ct)
        {
            if (ThrowOnCall is not null)
                return Task.FromException<IReadOnlyList<AlertRuleDefinition>>(ThrowOnCall);
            return Task.FromResult<IReadOnlyList<AlertRuleDefinition>>(RulesToReturn);
        }

        // Configuration-API members: the analysis path never calls these.
        public Task<IReadOnlyList<AlertRuleDefinition>> GetAllAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<AlertRuleDefinition?> GetByIdAsync(Guid ruleId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<AlertRuleDefinition?> UpdateAsync(
            Guid ruleId, string tenantId, bool enabled, string severity, decimal? thresholdValue,
            Guid updatedBy, CancellationToken ct)
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

    // ── Fixture helpers ───────────────────────────────────────────────────────

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
        AlertsJson:           ModelAlertsJson,
        AnomaliesJson:        "",
        BalanceJson:          BalanceJson,
        ExecutiveSummary:     "Resumen ejecutivo.",
        ConfidenceScore:      0.95m,
        HighestAlertLevel:    "medium",
        BalanceCheckBalanced: true,
        BalanceDifferencePct: 0.0,
        InputTokens:          1000,
        OutputTokens:         500,
        InputHash:            "in-hash",
        OutputHash:           "out-hash")
    {
        ModelUsed     = "claude-sonnet-4-6",
        PromptVersion = "2.16"
    };

    private static AlertRuleDefinition PasivoCorrienteRule() => new()
    {
        RuleId                = Guid.NewGuid(),
        TenantId              = Tenant,
        Code                  = AlertRuleEngine.PasivoCienCorriente,
        Enabled               = true,
        Severity              = "high",
        ThresholdValue        = null,
        DisplayNameEs         = "Pasivo íntegramente corriente",
        DescriptionTemplateEs = "La totalidad del pasivo es corriente.",
        CreatedAt             = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt             = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static (AnalysisController Sut, CapturingAnalysisResultRepository Results) BuildSut(
        StubAlertRuleCatalogueRepository catalogueRepo,
        Guid                             documentId,
        string?                          balanceJson    = null,
        string?                          anomaliesJson  = null,
        IScoringService?                 scoringService = null,
        IAlertRepository?                alertRepository    = null,
        bool                             overrideAlertsJson = false,
        string?                          alertsJson         = null,
        StubDocumentRepository?          documentRepository = null)
    {
        // overrideAlertsJson lets a fixture pass a null AlertsJson on purpose; without it the canned
        // ModelAlertsJson is used, as every fixture written before the alert-supersede suite expects.
        var docRepo = documentRepository ?? new StubDocumentRepository();
        docRepo.DocumentToReturn = BuildPendingDocument(documentId);
        var results = new CapturingAnalysisResultRepository();
        var baseResult = BuildClaudeResult();
        var claude  = new StubClaudeService
        {
            ResultToReturn = baseResult with
            {
                BalanceJson   = balanceJson   ?? baseResult.BalanceJson,
                AnomaliesJson = anomaliesJson ?? baseResult.AnomaliesJson,
                AlertsJson    = overrideAlertsJson ? alertsJson! : baseResult.AlertsJson,
            }
        };

        // The long part of TriggerAsync lives in AnalysisPipeline; it gets the dependencies the
        // controller used to hold for it, so every assertion below still describes the endpoint.
        var pipeline = new AnalysisPipeline(
            docRepo,                                                         // documentRepository
            results,                                                         // analysisResultRepository
            new StubAnalysisRepository(),                                    // analysisRepository
            new StubDocumentStorageService(),                                // storageService
            claude,                                                          // claudeService
            null!,                                                           // bcraService
            scoringService!,                                                 // scoringService — null! unless a fixture supplies one
            new RatioCalculationService(),                                   // ratioCalculationService — real, stateless
            new CashFlowReconciliationService(),                             // cashFlowReconciliationService — real, stateless
            new AlertRatioReconciliationService(),                           // alertRatioReconciliationService — real, stateless
            null!,                                                           // entityRepository
            alertRepository ?? new StubAlertRepository(),                    // alertRepository
            new StubAuditService(),                                          // auditService
            new StubApiCallLogService(),                                     // apiCallLogService
            catalogueRepo,                                                   // alertRuleCatalogueRepository
            new AlertRuleEngine(new CashFlowReconciliationService(),
                                NullLogger<AlertRuleEngine>.Instance),       // alertRuleEngine
            null!,                                                           // completionNotifier — its guard absorbs the NRE, as before
            null!,                                                           // legajoDocumentRepository — background re-validation only
            null!,                                                           // analysisRunStateRepository — background only
            NullLogger<AnalysisPipeline>.Instance);

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            docRepo,                                                         // documentRepository
            results,                                                         // analysisResultRepository
            new StubAnalysisRepository(),                                    // analysisRepository
            claude,                                                          // claudeService
            new CashFlowReconciliationService(),                             // cashFlowReconciliationService — real, stateless
            new CashFlowNarrativeService(),                                  // cashFlowNarrativeService — real, stateless
            new AlertRatioReconciliationService(),                           // alertRatioReconciliationService — real, stateless
            null!,                                                           // entityRepository
            new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid(), DisplayName = "Alguien" } }, // userRepository
            null!,                                                           // chatRepository
            null!,                                                           // creditLineRepository
            null!,                                                           // operationRepository
            new StubAuditService(),                                          // auditService
            new StubApiCallLogService(),                                     // apiCallLogService
            null!,                                                           // excelCashFlowParser
            Options.Create(new BillingOptions()),                            // billingOptions — real defaults (extraction budget = 2)
            null!,                                                           // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService — this suite never reaches a quorum vote
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            pipeline,                                                        // analysisPipeline — real, over the same stubs
            null!,                                                            // analysisJobQueue — the synchronous path never queues
            new AnalysisRunRegistry(Options.Create(new AnalysisOptions())),  // analysisRunRegistry
            null!,                                                            // analysisRunStateRepository — the synchronous path never claims
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("uid", FirebaseUid)], "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (sut, results);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenRuleEngineThrows_ModelObservationsSurviveUntouched()
    {
        // THE core guarantee: a rule-engine failure must never cost us the model's findings.
        // A missing AlertRuleCatalogue table on a database that has not had DDL 038 applied
        // surfaces exactly like this.
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository
        {
            ThrowOnCall = new InvalidOperationException("Invalid object name 'dbo.AlertRuleCatalogue'.")
        };
        var (sut, results) = BuildSut(catalogueRepo, documentId);

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        Assert.Equal(ModelAlertsJson, persisted.RiskAlerts);
        Assert.Null(persisted.CatalogueAlerts);
    }

    [Fact]
    public async Task TriggerAsync_WhenRuleEngineThrows_LaterEnrichmentStillRuns()
    {
        // Pins the engine block's OWN try/catch, which the "analysis still completes" assertions
        // alone do not: the block sits inside the method's larger try, so without its own catch the
        // outer one would still swallow the fault and the analysis would still complete — but every
        // enrichment step AFTER the engine would be skipped.
        //
        // The percentage reconciliation runs immediately after the engine block, so a corrected
        // anomaly is proof that execution continued past the failure rather than unwinding to the
        // outer handler. 6.236.534.182,74 / 12.239.430.766,71 = 51,0 %, not the stated 55,4 %.
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository
        {
            ThrowOnCall = new InvalidOperationException("Invalid object name 'dbo.AlertRuleCatalogue'.")
        };
        var (sut, results) = BuildSut(
            catalogueRepo, documentId,
            balanceJson:   """{"assets": 12239430766.71}""",
            anomaliesJson: """["Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del activo total de $12.239.430.766,71."]""");

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        Assert.Contains("51,0% del activo total", persisted.Anomalies);
        Assert.DoesNotContain("55,4%", persisted.Anomalies);
    }

    [Fact]
    public async Task TriggerAsync_WhenRuleEngineThrows_AnalysisStillCompletes()
    {
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository
        {
            ThrowOnCall = new InvalidOperationException("catalogue unavailable")
        };
        var (sut, results) = BuildSut(catalogueRepo, documentId);

        var response = await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.NotNull(results.Created);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenRuleFires_PersistsCatalogueAlertsAndLeavesRiskAlertsUntouched()
    {
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository
        {
            RulesToReturn = [PasivoCorrienteRule()]
        };
        var (sut, results) = BuildSut(catalogueRepo, documentId);

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);

        // The deterministic channel carries the catalogue alert...
        Assert.NotNull(persisted.CatalogueAlerts);
        var catalogueAlerts = JsonSerializer.Deserialize<List<CatalogueAlert>>(persisted.CatalogueAlerts!);
        var alert = Assert.Single(catalogueAlerts!);
        Assert.Equal(AlertRuleEngine.PasivoCienCorriente, alert.Code);
        Assert.Equal("high", alert.Level);

        // ...and the model channel is byte-identical to what the model emitted. No dedup, no
        // rewrite, no loss — the two channels never interfere.
        Assert.Equal(ModelAlertsJson, persisted.RiskAlerts);
    }

    [Fact]
    public async Task TriggerAsync_WhenCatalogueEmpty_CatalogueAlertsIsNull()
    {
        // A tenant that has not enabled any rule gets null, not "[]" — indistinguishable from a
        // pre-catalogue analysis, which is exactly the backward-compatible reading we want.
        var documentId = Guid.NewGuid();
        var (sut, results) = BuildSut(new StubAlertRuleCatalogueRepository(), documentId);

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        Assert.Null(persisted.CatalogueAlerts);
        Assert.Equal(ModelAlertsJson, persisted.RiskAlerts);
    }

    [Fact]
    public async Task TriggerAsync_WhenNoRuleMatches_CatalogueAlertsIsNull()
    {
        // The rule is enabled but its condition does not hold: non-current liabilities ARE present.
        // An enabled rule that does not match must leave the channel null, not emit an empty array.
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository
        {
            RulesToReturn = [PasivoCorrienteRule()]
        };
        var (sut, results) = BuildSut(
            catalogueRepo, documentId,
            balanceJson: """{"assets":100000000,"liabilities":50000000,"non_current_liabilities":30000000,"current_liabilities":20000000,"current_assets":40000000,"financial_expenses":4000000,"inventory":10000000}""");

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        Assert.Null(persisted.CatalogueAlerts);
        Assert.Equal(ModelAlertsJson, persisted.RiskAlerts);
    }

    // ── ScoreBreakdownJson wiring ─────────────────────────────────────────────
    //
    // Lives here because this is the only suite that drives TriggerAsync end to end and captures the
    // row that gets persisted; every other controller suite passes a null scoringService, so the
    // scoring block faults into its catch and ScoreBreakdownJson never gets written at all.
    //
    // scoringAlgoVersion records which scoring algorithm produced a row — see
    // ScoringService.ScoringAlgoVersion. It rides INSIDE the existing ScoreBreakdownJson
    // nvarchar(max) blob, so no column and no DDL is involved, and analyses persisted before the key
    // existed simply lack it and are read as version 1.
    //
    // The stub returns a SENTINEL rather than the real constant on purpose: what is under test here
    // is that the controller reads the property and writes the matching key, not what the engine puts
    // in it. That the engine stamps "2" is pinned separately, in
    // ScoringServiceBcraTests.Calculate_StampsTheScoringAlgorithmVersion.

    [Fact]
    public async Task TriggerAsync_PersistsScoringAlgoVersionInsideScoreBreakdownJson()
    {
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository();
        var scoring = new StubScoringService
        {
            ResultToReturn = new ScoringResult
            {
                Score              = 42.00m,
                Level              = "Medium",
                ScoreRatios        = 21.00m,
                ScoreBcra          = 45.00m,
                ScoreMacro         = 30.00m,
                BcraTrendDelta     = 1,
                BcraTrendAdj       = 8.00m,
                ChequesAdj                  = 6.00m,
                ChequesProporcionRecuperada = 0.6000m,
                VentasAdj                   = 8.00m,
                VentasVariacion             = -0.2000m,
                VentasBase                  = "interanual",
                VentasVariacionEsReal       = true,
                CasfogMoraAdj               = 9.60m,
                CasfogProporcionMora        = 0.3300m,
                SectorProfile      = "General",
                ScoringAlgoVersion = "test-algo-version",
            }
        };

        var (sut, results) = BuildSut(catalogueRepo, documentId, scoringService: scoring);

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        Assert.NotNull(persisted.ScoreBreakdownJson);

        using var blob = JsonDocument.Parse(persisted.ScoreBreakdownJson!);
        var root = blob.RootElement;

        Assert.True(
            root.TryGetProperty("scoringAlgoVersion", out var version),
            "ScoreBreakdownJson must carry scoringAlgoVersion — an analysis with no recorded " +
            "algorithm version is indistinguishable from one scored before the field existed.");
        Assert.Equal("test-algo-version", version.GetString());

        // Sanity: the key rides alongside the fields that were already there, so this is an addition
        // to the blob and not a replacement of it.
        Assert.Equal(1, root.GetProperty("bcraTrendDelta").GetInt32());
        Assert.Equal("General", root.GetProperty("sectorProfile").GetString());

        // The rejected-cheques penalty rides in the same blob (scoring algorithm 5). ScoringResult is
        // never serialised directly — the controller hand-builds this object — so a field added to the
        // result type alone would persist NOTHING while every engine-level test still passed. This is
        // the only assertion that catches that, for the same reason the version key above needs one.
        Assert.Equal(6.00m, root.GetProperty("chequesAdj").GetDecimal());
        Assert.Equal(0.6000m, root.GetProperty("chequesProporcionRecuperada").GetDecimal());

        // The LUFE sales-drop penalty rides in the same blob (scoring algorithm 6, §7.3a), and needs
        // its own assertion for the same reason: the controller hand-builds this object, so all three
        // fields could exist on ScoringResult, be computed correctly, and persist NOTHING while every
        // engine-level test still passed. The keys are hand-camelCased at the call site — this is
        // also what catches one of them landing PascalCase.
        Assert.Equal(8.00m, root.GetProperty("ventasAdj").GetDecimal());
        Assert.Equal(-0.2000m, root.GetProperty("ventasVariacion").GetDecimal());
        Assert.Equal("interanual", root.GetProperty("ventasBase").GetString());

        // ventasVariacionEsReal (scoring algorithm 7, §7.3b) is what tells a real variation from a
        // nominal one on a given row, and without it a v6 and a v7 reading are indistinguishable —
        // they are not the same measurement. Same reason as the three keys above: the controller
        // hand-builds this object, so the field could exist on ScoringResult, be computed correctly,
        // and persist NOTHING while every engine-level test still passed.
        Assert.True(root.GetProperty("ventasVariacionEsReal").GetBoolean());

        // The CASFOG guarantee-arrears penalty rides in the same blob (scoring algorithm 8, §7.4) and
        // needs its own assertion for the same reason as every key above: the controller hand-builds
        // this object, so both fields could exist on ScoringResult, be computed correctly, and persist
        // NOTHING while every engine-level test still passed. The keys are hand-camelCased at the call
        // site — this is also what catches one of them landing PascalCase.
        Assert.Equal(9.60m, root.GetProperty("casfogMoraAdj").GetDecimal());
        Assert.Equal(0.3300m, root.GetProperty("casfogProporcionMora").GetDecimal());
    }

    // A row the penalty never evaluated persists an explicit null rather than a false: false would
    // claim the engine looked for inflation data and found none. Absent (a pre-§7.3b row), null (not
    // evaluated) and false (evaluated, no CPI) are three different statements, and both surfaces
    // render them differently.
    [Fact]
    public async Task TriggerAsync_WhenTheVentasPenaltyWasNotEvaluated_PersistsAnExplicitNullFlag()
    {
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository();
        var scoring = new StubScoringService
        {
            ResultToReturn = new ScoringResult
            {
                Score                 = 15.00m,
                Level                 = "Low",
                VentasAdj             = 0m,
                VentasVariacion       = null,
                VentasBase            = null,
                VentasVariacionEsReal = null,
                CasfogMoraAdj         = 0m,
                CasfogProporcionMora  = null,
                ScoringAlgoVersion    = "8",
            }
        };

        var (sut, results) = BuildSut(catalogueRepo, documentId, scoringService: scoring);

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        using var blob = JsonDocument.Parse(persisted.ScoreBreakdownJson!);

        Assert.True(blob.RootElement.TryGetProperty("ventasVariacionEsReal", out var flag));
        Assert.Equal(JsonValueKind.Null, flag.ValueKind);

        // Same three-way distinction for the arrears share (§7.4): absent means the row predates the
        // signal, null means the engine could form no share at all — no rows, or no measurable balance
        // — and a 0 would mean it looked and the portfolio was clean. Persisting a 0 here would claim
        // the third when the second is true.
        Assert.True(blob.RootElement.TryGetProperty("casfogProporcionMora", out var share));
        Assert.Equal(JsonValueKind.Null, share.ValueKind);
        Assert.Equal(0m, blob.RootElement.GetProperty("casfogMoraAdj").GetDecimal());
    }

    // A null version — the neutral catch-all result, which no algorithm produced — is persisted as an
    // explicit null rather than dropped, so the key is present and empty instead of missing. Either
    // shape reads as "no version", but pinning it stops a future serializer-options change from
    // quietly making a v2 row look like a pre-D2 one.
    [Fact]
    public async Task TriggerAsync_WhenScoringReportsNoAlgoVersion_PersistsAnExplicitNull()
    {
        var documentId = Guid.NewGuid();
        var catalogueRepo = new StubAlertRuleCatalogueRepository();
        var scoring = new StubScoringService
        {
            ResultToReturn = new ScoringResult
            {
                Score              = 50.00m,
                Level              = "Medium",
                ScoringAlgoVersion = null,
            }
        };

        var (sut, results) = BuildSut(catalogueRepo, documentId, scoringService: scoring);

        await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);

        var persisted = Assert.IsType<AnalysisResult>(results.Created);
        using var blob = JsonDocument.Parse(persisted.ScoreBreakdownJson!);

        Assert.True(blob.RootElement.TryGetProperty("scoringAlgoVersion", out var version));
        Assert.Equal(JsonValueKind.Null, version.ValueKind);
    }

    // ── Score reliability (score credibility 1b-2b) ─────────────────────────

    /// <summary>
    /// The synchronous trigger answers the score-reliability verdict of the score it just persisted, from the
    /// breakdown the real scoring stage wrote (facts included) — through the same adapter as every surface.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_ResponseCarriesTheScoreReliabilityOfThePersistedScore()
    {
        var documentId = Guid.NewGuid();
        var scoring = new StubScoringService
        {
            ResultToReturn = new ScoringResult { Score = 50.00m, Level = "Medium", ScoringAlgoVersion = "9" }
        };
        var (sut, results) = BuildSut(new StubAlertRuleCatalogueRepository(), documentId, scoringService: scoring);

        var created  = Assert.IsType<CreatedAtActionResult>(await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None));
        var response = Assert.IsType<AnalysisResultResponse>(created.Value);
        var persisted = Assert.IsType<AnalysisResult>(results.Created);

        var expected = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityAdapter.Evaluate(
            new Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityRow(
                persisted.AiSuggestedScore, persisted.EnrichmentStatus, AnalysisKinds.Balance,
                response.FiscalYear, persisted.GeneratedAt, persisted.RescoredAt),
            persisted.ScoreBreakdownJson).Reliability;

        Assert.NotNull(response.ScoreReliability);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(response.ScoreReliability));
        // The facts were recorded by this very run, so the verdict is never "analysed before the facts".
        Assert.DoesNotContain(response.ScoreReliability!.Reasons, r => r.Code == "facts_unavailable");
    }
}
