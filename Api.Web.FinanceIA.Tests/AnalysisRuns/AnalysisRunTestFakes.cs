// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Logging.Abstractions;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>Shared fixture values for the background-analysis suites.</summary>
internal static class AnalysisRunTestData
{
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";

    public static Document Document(string tenantId, Guid? documentId = null, string status = "Processing") => new()
    {
        DocumentId       = documentId ?? Guid.NewGuid(),
        TenantId         = tenantId,
        EntityId         = Guid.NewGuid(),
        UploadedBy       = Guid.NewGuid(),
        StoragePath      = $"/tmp/financia-uploads/{tenantId}/doc.pdf",
        ProcessingStatus = status,
        DocumentType     = "Balance",
        FiscalYear       = 2024
    };

    public static AnalysisRun Run(Document document, bool isBackground = true, Guid? requestedBy = null) => new()
    {
        TenantId           = document.TenantId,
        RequestedBy        = requestedBy ?? Guid.NewGuid(),
        DocumentId         = document.DocumentId,
        EntityId           = document.EntityId,
        FiscalYear         = 2024,
        StoragePath        = document.StoragePath,
        DocumentType       = document.DocumentType,
        DocumentFiscalYear = document.FiscalYear,
        IpAddress          = "10.0.0.1",
        RequestId          = "trace-1",
        AcceptedAtUtc      = new DateTime(2026, 1, 31, 23, 59, 0, DateTimeKind.Utc),
        IsBackground       = isBackground
    };

    public static ClaudeAnalysisResult ClaudeResult() => new(
        RatiosJson:           "",
        AlertsJson:           "",
        AnomaliesJson:        "",
        BalanceJson:          "",
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
}

/// <summary>
/// The Documents table, in memory and tenant-scoped exactly like the real repositories: a lookup with
/// the wrong tenant finds nothing. Every call is recorded with the tenant it carried.
/// </summary>
internal sealed class InMemoryDocumentStore
{
    private readonly object _gate = new();

    public Dictionary<Guid, Document> Rows { get; } = [];
    public List<(string Op, string TenantId, Guid DocumentId, string? Status)> Calls { get; } = [];

    public void Add(Document document)
    {
        lock (_gate) Rows[document.DocumentId] = document;
    }

    public IReadOnlyList<ProcessingDocument> Processing()
    {
        lock (_gate)
        {
            return Rows.Values
                .Where(d => d.ProcessingStatus == "Processing" && !d.IsDeleted)
                .Select(d => new ProcessingDocument(d.DocumentId, d.TenantId, d.EntityId, d.UploadedBy))
                .ToList();
        }
    }

    public string? StatusOf(Guid documentId)
    {
        lock (_gate) return Rows.TryGetValue(documentId, out var d) ? d.ProcessingStatus : null;
    }

    public Document? Find(string op, Guid documentId, string tenantId, string? status = null)
    {
        lock (_gate)
        {
            Calls.Add((op, tenantId, documentId, status));
            return Rows.TryGetValue(documentId, out var d) && d.TenantId == tenantId ? d : null;
        }
    }

    public void Set(Document document, string status, DateTime? processedAt)
    {
        lock (_gate)
        {
            document.ProcessingStatus = status;
            document.ProcessedAt      = processedAt;
        }
    }
}

/// <summary><see cref="IDocumentRepository"/> over <see cref="InMemoryDocumentStore"/>.</summary>
internal sealed class StoreDocumentRepository(InMemoryDocumentStore store) : IDocumentRepository
{
    /// <summary>When set, UpdateStatusAsync(…, "Failed", …) throws this.</summary>
    public Exception? FailedWriteThrows { get; set; }

    public List<(Guid DocumentId, string? Error, bool CountsAsAttempt)> ExtractionFailures { get; } = [];

    public Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var d = store.Find("GetById", documentId, tenantId);
        return Task.FromResult(d is null ? null : Clone(d));
    }

    public Task UpdateStatusAsync(Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
    {
        if (status == "Failed" && FailedWriteThrows is not null)
            throw FailedWriteThrows;

        var d = store.Find("UpdateStatus", documentId, tenantId, status);
        if (d is not null) store.Set(d, status, processedAt);
        return Task.CompletedTask;
    }

    public Task RecordExtractionFailureAsync(Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct)
    {
        store.Find("RecordExtractionFailure", documentId, tenantId);
        lock (ExtractionFailures) ExtractionFailures.Add((documentId, error, countsAsAttempt));
        return Task.CompletedTask;
    }

    public Task<Document?> FindRecentByHashAsync(Guid entityId, string tenantId, string? fileHashSha256, string documentType, DateTime uploadedSinceUtc, CancellationToken ct)
        => Task.FromResult<Document?>(null);

    public Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public Task<int> SumExtractionAttemptsByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
        => Task.FromResult(0);

    public Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task<Document> CreateAsync(Document document, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> SoftDeleteAsync(Guid documentId, string tenantId, DateTime deletedAt, CancellationToken ct) => throw new NotImplementedException();

    // A detached copy, like an AsNoTracking read.
    private static Document Clone(Document d) => new()
    {
        DocumentId       = d.DocumentId,
        TenantId         = d.TenantId,
        EntityId         = d.EntityId,
        UploadedBy       = d.UploadedBy,
        StoragePath      = d.StoragePath,
        ProcessingStatus = d.ProcessingStatus,
        ProcessedAt      = d.ProcessedAt,
        DocumentType     = d.DocumentType,
        FiscalYear       = d.FiscalYear,
        IsDeleted        = d.IsDeleted,
        PersonLegajoId   = d.PersonLegajoId
    };
}

/// <summary><see cref="IAnalysisRunStateRepository"/> over <see cref="InMemoryDocumentStore"/>: the same compare-and-set rules.</summary>
internal sealed class StoreRunStateRepository(InMemoryDocumentStore store) : IAnalysisRunStateRepository
{
    public List<(Guid DocumentId, string Status, DateTime? ProcessedAt)> Restores { get; } = [];

    /// <summary>Runs just before a conditional write reads the row — the moment a concurrent writer could slip in.</summary>
    public Action<Document>? BeforeConditionalWrite { get; set; }

    public Task<bool> TryBeginProcessingAsync(
        Guid documentId, string tenantId, string expectedStatus, DateTime? expectedProcessedAt, CancellationToken ct)
    {
        var d = store.Find("TryBegin", documentId, tenantId, "Processing");
        if (d is not null) BeforeConditionalWrite?.Invoke(d);
        if (d is null || d.IsDeleted || d.ProcessingStatus != expectedStatus || d.ProcessedAt != expectedProcessedAt)
            return Task.FromResult(false);

        store.Set(d, "Processing", null);
        return Task.FromResult(true);
    }

    public Task<bool> TryRestoreAsync(Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
    {
        var d = store.Find("TryRestore", documentId, tenantId, status);
        if (d is null || d.ProcessingStatus != "Processing")
            return Task.FromResult(false);

        Restores.Add((documentId, status, processedAt));
        store.Set(d, status, processedAt);
        return Task.FromResult(true);
    }

    public Task<bool> TryFailProcessingAsync(Guid documentId, string tenantId, DateTime failedAt, CancellationToken ct)
    {
        var d = store.Find("TryFail", documentId, tenantId, "Failed");
        if (d is not null) BeforeConditionalWrite?.Invoke(d);
        if (d is null || d.ProcessingStatus != "Processing")
            return Task.FromResult(false);

        store.Set(d, "Failed", failedAt);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ProcessingDocument>> GetProcessingDocumentsAsync(CancellationToken ct)
    {
        return Task.FromResult(store.Processing());
    }
}

internal sealed class FixedUserRepository(User user) : IUserRepository
{
    public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => Task.FromResult<User?>(user);

    public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
    public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
    public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
    public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
    public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
}

internal sealed class FakeStorage : IDocumentStorageService
{
    public Exception? ReadThrows { get; set; }
    public List<string> Reads { get; } = [];

    /// <summary>What every read returns. Empty by default — not a PDF, so no attachment is unwrapped.</summary>
    public byte[] Bytes { get; set; } = [];

    public Task<byte[]> ReadAsync(string storagePath, CancellationToken ct)
    {
        lock (Reads) Reads.Add(storagePath);
        if (ReadThrows is not null) throw ReadThrows;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Bytes);
    }

    public Task<string> SaveAsync(string tenantId, Guid documentId, Stream content, CancellationToken ct)
        => throw new NotImplementedException();
}

internal sealed class FakeClaude : IClaudeService
{
    public Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
        byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
        => throw new NotImplementedException();

    private int _calls;

    public Exception? AnalyzeThrows { get; set; }

    /// <summary>When set, every call waits on it — lets a test hold a run in flight.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int AnalyzeCalls => Volatile.Read(ref _calls);

    /// <summary>When set, returned instead of <see cref="AnalysisRunTestData.ClaudeResult"/>.</summary>
    public ClaudeAnalysisResult? Result { get; set; }

    /// <summary>The PDF bytes the last extraction call received — what the unwrap handed the model.</summary>
    public byte[]? LastPdfBytes { get; private set; }

    public async Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        LastPdfBytes = pdfBytes;
        if (Gate is not null) await Gate.Task.ConfigureAwait(false);
        if (AnalyzeThrows is not null) throw AnalyzeThrows;
        return Result ?? AnalysisRunTestData.ClaudeResult();
    }

    public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
        => throw new NotImplementedException();
    public Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(ScoringResult scoring, RatiosData ratios, BcraData? bcraData, BcraMacroData? macroData, ScoreReliabilityResponse? reliability, CancellationToken ct)
        => throw new NotImplementedException();
}

internal sealed class RecordingAnalysisRepository : IAnalysisRepository
{
    public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
        => Task.FromResult<BalanceBaseFiguresProjection?>(null);

    public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    public List<Analysis> Created { get; } = [];

    /// <summary>When set, CreateAsync throws it — a fault after the extraction answered.</summary>
    public Exception? CreateThrows { get; set; }

    public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
    {
        if (CreateThrows is not null) throw CreateThrows;
        lock (Created) Created.Add(analysis);
        return Task.FromResult(analysis);
    }

    public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => Task.FromResult<Analysis?>(null);
    public Task<bool> TryConfirmFromPreApprovedAsync(Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
        decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson, string? reviewNotes, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct) => throw new NotImplementedException();
    public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
}

internal sealed class RecordingAnalysisResultRepository : IAnalysisResultRepository
{
    public List<AnalysisResult> Created { get; } = [];

    /// <summary>When set, CreateAsync throws it — a fault after the extraction row was written.</summary>
    public Exception? CreateThrows { get; set; }

    public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
    {
        if (CreateThrows is not null) throw CreateThrows;
        lock (Created) Created.Add(result);
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
    public Task<AnalysisLabelProjection?> GetLabelByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
}

internal sealed class RecordingAudit : IAuditService
{
    public List<(string TenantId, string Action, string? EntityId, string? NewValueJson, string Severity)> Entries { get; } = [];

    public Task LogAsync(string tenantId, Guid? userId, string action, string severity = "Info",
        string? entityType = null, string? entityId = null, string? ipAddress = null, string? requestId = null,
        string? oldValueJson = null, string? newValueJson = null, CancellationToken ct = default)
    {
        lock (Entries) Entries.Add((tenantId, action, entityId, newValueJson, severity));
        return Task.CompletedTask;
    }

    public Task LogSessionAsync(Guid tenantId, Guid? userId, string email, string eventType,
        string? ipAddress, string? userAgent, string? failureReason = null, CancellationToken ct = default)
        => throw new NotImplementedException();
}

internal sealed class NoopApiCallLog : IApiCallLogService
{
    public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
        int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
        int durationMs, string? operation = null, string? model = null, Guid? userId = null,
        Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Records every ApiCallLogs row the pipeline writes (operation, model, tokens, cost).</summary>
internal sealed class CostRecordingApiCallLog : IApiCallLogService
{
    public List<(string? Operation, string? Model, int? InputTokens, int? OutputTokens, decimal? EstimatedCostUsd,
        bool IsSuccess, int? ResponseStatus, string? ErrorMessage, Guid? AnalysisId, Guid? DocumentId, string Service)> Rows { get; } = [];

    /// <summary>When set, every LogAsync throws it synchronously — a logging failure must never break a run.</summary>
    public Exception? Throws { get; set; }

    public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
        int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
        int durationMs, string? operation = null, string? model = null, Guid? userId = null,
        Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
    {
        if (Throws is not null) throw Throws;
        lock (Rows) Rows.Add((operation, model, inputTokens, outputTokens, estimatedCostUsd,
            isSuccess, responseStatus, errorMessage, analysisId, documentId, service));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingNotifier : IAnalysisCompletionNotifier
{
    public List<(string TenantId, Guid UserId, Guid ResultId)> Completed { get; } = [];
    public List<(string TenantId, Guid UserId, Guid DocumentId)> Failed { get; } = [];

    public Task NotifyCompletedAsync(string tenantId, Guid userId, Guid analysisId, Guid resultId,
        string? entityName, int fiscalYear, decimal? score, CancellationToken ct)
    {
        lock (Completed) Completed.Add((tenantId, userId, resultId));
        return Task.CompletedTask;
    }

    public Task NotifyFailedAsync(string tenantId, Guid userId, Guid documentId, Guid entityId, CancellationToken ct)
    {
        lock (Failed) Failed.Add((tenantId, userId, documentId));
        return Task.CompletedTask;
    }
}

/// <summary>A real <see cref="AnalysisPipeline"/> over the fakes above — the same minimal wiring the trigger suites use.</summary>
internal sealed class PipelineHarness
{
    public InMemoryDocumentStore Store { get; } = new();
    public StoreDocumentRepository Documents { get; }
    public StoreRunStateRepository RunState { get; }
    public FakeStorage Storage { get; } = new();
    public FakeClaude Claude { get; } = new();
    public RecordingAnalysisRepository Analyses { get; } = new();
    public RecordingAnalysisResultRepository Results { get; } = new();
    public RecordingAudit Audit { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public CostRecordingApiCallLog ApiCalls { get; } = new();
    public Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository Legajos { get; } = new();

    /// <summary>The pipeline's logger; silent unless a test swaps in a recording one.</summary>
    public Microsoft.Extensions.Logging.ILogger<AnalysisPipeline> Logger { get; set; } = NullLogger<AnalysisPipeline>.Instance;

    public PipelineHarness()
    {
        Documents = new StoreDocumentRepository(Store);
        RunState  = new StoreRunStateRepository(Store);
    }

    public AnalysisPipeline Build() => new(
        Documents,                                   // documentRepository
        Results,                                     // analysisResultRepository
        Analyses,                                    // analysisRepository
        Storage,                                     // storageService
        Claude,                                      // claudeService
        null!,                                       // bcraService — entity is null, never reached
        null!,                                       // scoringService — the scoring block absorbs the NRE
        null!,                                       // ratioCalculationService
        null!,                                       // cashFlowReconciliationService
        new AlertRatioReconciliationService(),       // alertRatioReconciliationService — real, stateless
        null!,                                       // entityRepository — the lookup block absorbs the NRE
        null!,                                       // alertRepository — AlertsJson is empty
        Audit,                                       // auditService
        ApiCalls,                                    // apiCallLogService
        null!,                                       // alertRuleCatalogueRepository — the rule block absorbs the NRE
        null!,                                       // alertRuleEngine
        Notifier,                                    // completionNotifier
        Legajos,                                     // legajoDocumentRepository
        RunState,                                    // analysisRunStateRepository
        Logger);
}
