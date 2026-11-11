// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-22

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// One tenant's business database (EF InMemory) with the REAL repositories, the real ratio and scoring
/// engines and the real pipeline, plus stubs for the three things that leave the process: the BCRA,
/// Claude and the clock. Every context it hands out is a fresh one over the same store, as one per
/// request would be in production — which is also what keeps EF's identity map from hiding a bug.
/// </summary>
internal sealed class RescoreTestWorld
{
    public const string Tenant      = "tenant-rescore";
    public const string OtherTenant = "tenant-other";
    public const string Cuit        = "30712345678";

    /// <summary>
    /// A balance as ClaudeService persists it, with a cash-flow statement that does NOT reconcile, so the
    /// pipeline appends two cash-flow anomalies — the non-idempotent augmentation a re-score must never
    /// re-apply.
    /// </summary>
    public const string BalanceJson =
        """
        {"current_assets":5000000,"non_current_assets":3000000,"assets":8000000,
         "current_liabilities":2500000,"non_current_liabilities":1500000,"liabilities":4000000,
         "equity":4000000,"net_revenue":12000000,"net_profit":900000,"operating_result":1500000,
         "financial_expenses":300000,"inventory":1000000,
         "cash_flow":{"present":true,"cash_beginning":100,"cash_end":50,"net_change":999,
                      "operating":100,"investing":-20,"financing":-10}}
        """;

    public const string ModelAnomaliesJson = """["Anomalía informada por el modelo."]""";

    private readonly string _databaseName = $"rescore-{Guid.NewGuid()}";

    public RescoreTestWorld()
    {
        Documents = new StoreDocumentRepository(DocumentStore);
    }

    public InMemoryDocumentStore   DocumentStore { get; } = new();
    public StoreDocumentRepository Documents     { get; }
    public StubBcra                Bcra          { get; } = new();
    public StubRescoreClaude       Claude        { get; } = new();
    public RecordingAudit          Audit         { get; } = new();
    public RecordingApiCallLog     ApiCalls      { get; } = new();
    public MutableClock            Clock         { get; } = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    public RecordingPersonRescore  PersonRescore { get; } = new();
    public Guid                    UserId        { get; } = Guid.NewGuid();

    public FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>().UseInMemoryDatabase(_databaseName).Options);

    /// <summary>The re-score service as DI would build it for one request (a fresh context).</summary>
    public AnalysisRescoreService Service(
        IAnalysisRescoreRepository? rescoreRepository = null, RescoreOptions? options = null, FinanciaDbContext? db = null)
    {
        db ??= NewContext();
        return new AnalysisRescoreService(
            new AnalysisResultRepository(db, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(db, NullLogger<AnalysisRepository>.Instance),
            Documents,
            rescoreRepository ?? new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance),
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),
            Bcra,
            new ScoringService(db, NullLogger<ScoringService>.Instance),
            new RatioCalculationService(),
            Claude,
            ApiCalls,
            Audit,
            PersonRescore,
            Options.Create(options ?? new RescoreOptions()),
            NullLogger<AnalysisRescoreService>.Instance,
            Clock);
    }

    /// <summary>The REAL pipeline over this world, for one synchronous run (a fresh context).</summary>
    public AnalysisPipeline Pipeline()
    {
        var db = NewContext();
        return new AnalysisPipeline(
            Documents,                                                         // documentRepository
            new AnalysisResultRepository(db, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(db, NullLogger<AnalysisRepository>.Instance),
            new FakeStorage(),                                                 // storageService
            Claude,                                                            // claudeService
            Bcra,                                                              // bcraService
            new ScoringService(db, NullLogger<ScoringService>.Instance),       // scoringService
            new RatioCalculationService(),                                     // ratioCalculationService
            new CashFlowReconciliationService(),                               // cashFlowReconciliationService
            new AlertRatioReconciliationService(),                             // alertRatioReconciliationService
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),   // entityRepository
            null!,                                                             // alertRepository — AlertsJson is empty
            new RecordingAudit(),                                              // auditService
            new NoopApiCallLog(),                                              // apiCallLogService
            null!,                                                             // alertRuleCatalogueRepository — the rule block absorbs the NRE
            null!,                                                             // alertRuleEngine
            new RecordingNotifier(),                                           // completionNotifier
            null!,                                                             // legajoDocumentRepository — synchronous run
            null!,                                                             // analysisRunStateRepository — synchronous run
            NullLogger<AnalysisPipeline>.Instance);
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────

    /// <summary>The company, with a CUIT and LUFE / CASFOG / ARCA / NOSIS snapshots dated in the past.</summary>
    public async Task<Entity> SeedEntityAsync(string? cuit = Cuit, string tenantId = Tenant)
    {
        var entity = new Entity
        {
            EntityId            = Guid.NewGuid(),
            TenantId            = tenantId,
            CUIT                = cuit ?? string.Empty,
            BusinessName        = "Empresa de Prueba SA",
            Sector              = null,
            CreatedAt           = Utc(2026, 1, 1),
            UpdatedAt           = Utc(2026, 1, 1),
            LufeDataJson        = """{"cuit":"30712345678"}""",
            LufeDataUpdatedAt   = Utc(2026, 9, 1),
            CasfogDataJson      = """{"garantias":null}""",
            CasfogDataUpdatedAt = Utc(2026, 9, 2),
            FiscalDataJson      = """{"estadoClave":"ACTIVO"}""",
            FiscalDataUpdatedAt = Utc(2026, 9, 3),
            NosisDataJson       = """{"score":700}""",
            NosisDataUpdatedAt  = Utc(2026, 9, 4)
        };

        await using var db = NewContext();
        db.Entities.Add(entity);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>A document in the store with the given status.</summary>
    public Document SeedDocument(Entity entity, string status = "Processing")
    {
        var document = AnalysisRunTestData.Document(entity.TenantId, status: status);
        document.EntityId = entity.EntityId;
        DocumentStore.Add(document);
        return document;
    }

    /// <summary>
    /// Runs the REAL pipeline on a document of <paramref name="entity"/> and returns the persisted result.
    /// The Claude stub returns <see cref="BalanceJson"/> and <see cref="ModelAnomaliesJson"/>.
    /// </summary>
    public async Task<AnalysisResult> RunPipelineAsync(Entity entity)
    {
        var document = SeedDocument(entity);
        var run      = AnalysisRunTestData.Run(document, isBackground: false, requestedBy: UserId);

        var outcome = await Pipeline().RunAsync(run, CancellationToken.None);
        Assert.NotNull(outcome.Result);

        return (await ReadResultAsync(outcome.Result!.ResultId))!;
    }

    /// <summary>
    /// A persisted balance analysis written directly (no pipeline), for the refusal rules. Defaults: a
    /// PendingReview balance analysis with a balance, Degraded, scored 40, document Completed.
    /// </summary>
    public async Task<(Analysis Analysis, AnalysisResult Result, Document Document)> SeedAnalysisAsync(
        Entity entity,
        Action<Analysis>?       configureAnalysis = null,
        Action<AnalysisResult>? configureResult   = null,
        string                  documentStatus    = "Completed")
    {
        var document = SeedDocument(entity, documentStatus);

        var analysis = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = entity.TenantId,
            EntityId      = entity.EntityId,
            DocumentId    = document.DocumentId,
            RequestedBy   = UserId,
            Status        = AnalysisStatuses.PendingReview,
            FiscalYear    = 2024,
            AiModel       = "claude-sonnet-4-6",
            PromptVersion = "2.0",
            CreatedAt     = Utc(2026, 10, 1)
        };
        configureAnalysis?.Invoke(analysis);

        var result = new AnalysisResult
        {
            ResultId           = Guid.NewGuid(),
            AnalysisId         = analysis.AnalysisId,
            TenantId           = entity.TenantId,
            BalanceJson        = BalanceJson,
            RatiosJson         = "{}",
            Anomalies          = ModelAnomaliesJson,
            RiskAlerts         = "[]",
            ExecutiveSummary   = "Resumen ejecutivo.",
            AiSuggestedScore   = 40m,
            AiSuggestedLevel   = "High",
            ScoringNarrative   = "Narrativa anterior.",
            ScoreBreakdownJson = """{"scoringAlgoVersion":"8"}""",
            AiInputHash        = "in-hash",
            AiOutputHash       = "out-hash",
            ValidationStatus   = "Passed",
            EnrichmentStatus   = nameof(BcraEnrichmentOutcome.Degraded),
            EnrichmentNotes    = BcraEnrichmentClassifier.NotesThrew,
            GeneratedAt        = Utc(2026, 10, 1)
        };
        configureResult?.Invoke(result);

        await using var db = NewContext();
        db.Analyses.Add(analysis);
        db.AnalysisResults.Add(result);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();

        return (analysis, result, document);
    }

    public async Task AddVoteAsync(Guid analysisId, string vote = AnalysisVoteValues.Approved)
    {
        await using var db = NewContext();
        db.AnalysisApprovalVotes.Add(new AnalysisApprovalVote
        {
            VoteId     = Guid.NewGuid(),
            AnalysisId = analysisId,
            UserId     = Guid.NewGuid(),
            TenantId   = Tenant,
            Vote       = vote,
            VotedAt    = Utc(2026, 10, 2)
        });
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();
    }

    // ── Reading back ────────────────────────────────────────────────────────────

    public async Task<AnalysisResult?> ReadResultAsync(Guid resultId)
    {
        await using var db = NewContext();
        return await db.AnalysisResults.AsNoTracking().SingleOrDefaultAsync(r => r.ResultId == resultId);
    }

    public async Task<List<AnalysisRescore>> ReadHistoryAsync()
    {
        await using var db = NewContext();
        return await db.AnalysisRescores.AsNoTracking().OrderBy(h => h.RescoredAt).ToListAsync();
    }

    /// <summary>Every column of the row, for a byte-for-byte "nothing changed" assertion.</summary>
    public static string Fingerprint(AnalysisResult result) => JsonSerializer.Serialize(result);

    public static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A BCRA answer with a debt record: the payload the pipeline scores the BCRA component on.</summary>
    public static BcraData BcraWithDebt(int situacion = 1) => new()
    {
        Deuda = new BcraDeudaData
        {
            Estado               = BcraDeudaConsultaEstado.DebtFound,
            SituacionMaxima      = situacion,
            MontoTotal           = 1500m,
            PeriodoConsultado    = "202609",
            HistorialSituaciones =
            [
                new BcraSituacionPeriodo { Periodo = "202609", SituacionMaxima = situacion },
                new BcraSituacionPeriodo { Periodo = "202608", SituacionMaxima = situacion }
            ]
        },
        // Every real BcraService answer carries a cheques block; a clean one by default. Without it
        // the cheques lookup reads as failed, which (penalty on by default) degrades the enrichment.
        Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.NoChequesReported }
    };
}

/// <summary>The BCRA, live: answers <see cref="Payload"/>, or throws <see cref="Throws"/>.</summary>
internal sealed class StubBcra : IBcraService
{
    private int _calls;

    public BcraData? Payload { get; set; } = RescoreTestWorld.BcraWithDebt();
    public Exception? Throws { get; set; }
    public int Calls => Volatile.Read(ref _calls);

    public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Throws is not null) throw Throws;
        return Task.FromResult(Payload);
    }

    public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct) => throw new NotImplementedException();
    public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct) => throw new NotImplementedException();
    public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct) => throw new NotImplementedException();
}

/// <summary>
/// Claude: an extraction that returns the world's balance (counted — a re-score must never call it),
/// and a narrative that returns <see cref="NarrativeText"/>, or nothing when <see cref="NarrativeFails"/>.
/// </summary>
internal sealed class StubRescoreClaude : IClaudeService
{
    private int _analyzeCalls;
    private int _narrativeCalls;

    public int  AnalyzeCalls   => Volatile.Read(ref _analyzeCalls);
    public int  NarrativeCalls => Volatile.Read(ref _narrativeCalls);
    public bool NarrativeFails { get; set; }
    public string NarrativeText { get; set; } = "Narrativa del score.";

    /// <summary>The reliability verdict each narrative call was handed, in call order.</summary>
    public List<ScoreReliabilityResponse?> NarrativeReliabilities { get; } = [];

    public Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
    {
        Interlocked.Increment(ref _analyzeCalls);
        return Task.FromResult(AnalysisRunTestData.ClaudeResult() with
        {
            BalanceJson   = RescoreTestWorld.BalanceJson,
            AnomaliesJson = RescoreTestWorld.ModelAnomaliesJson,
            RatiosJson    = """{"current_ratio":9.99}"""
        });
    }

    public Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(
        ScoringResult scoring, RatiosData ratios, BcraData? bcraData, BcraMacroData? macroData, ScoreReliabilityResponse? reliability, CancellationToken ct)
    {
        Interlocked.Increment(ref _narrativeCalls);
        lock (NarrativeReliabilities)
            NarrativeReliabilities.Add(reliability);
        return Task.FromResult(NarrativeFails
            ? new ClaudeNarrativeResult(string.Empty, 0, 0)
            : new ClaudeNarrativeResult($"{NarrativeText} ({scoring.Score})", 120, 80));
    }

    public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history,
        string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
        => throw new NotImplementedException();

    /// <summary>The correction suggestion's answer for a question; null makes the call throw (it must not run).</summary>
    public Func<BalanceSuggestionQuestion, BalanceSuggestionReply>? Suggestion { get; set; }

    /// <summary>Every suggestion call: the PDF bytes it was handed and the question.</summary>
    public List<(byte[] Pdf, BalanceSuggestionQuestion Question)> SuggestionCalls { get; } = [];

    /// <summary>
    /// An awaitable answer, which wins over <see cref="Suggestion"/>: it receives the token the service passed,
    /// so a test can hold the call open and watch whether it is cancelled.
    /// </summary>
    public Func<BalanceSuggestionQuestion, CancellationToken, Task<BalanceSuggestionReply>>? SuggestionAsync { get; set; }

    public Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
        byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
    {
        lock (SuggestionCalls)
            SuggestionCalls.Add((pdfBytes, question));
        if (SuggestionAsync is { } awaitable)
            return awaitable(question, ct);
        return Suggestion is { } answer
            ? Task.FromResult(answer(question))
            : throw new InvalidOperationException("No suggestion was expected.");
    }
}

internal sealed class RecordingApiCallLog : IApiCallLogService
{
    public List<(string Operation, Guid? AnalysisId, int? InputTokens)> Entries { get; } = [];

    public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
        int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
        int durationMs, string? operation = null, string? model = null, Guid? userId = null,
        Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
    {
        lock (Entries) Entries.Add((operation ?? string.Empty, analysisId, inputTokens));
        return Task.CompletedTask;
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The persona humana branch, recorded: the balance world only checks that a persona analysis is handed
/// over after the shared rules, never re-scored. The branch itself is exercised end to end by
/// PersonAnalysesControllerTests.Reevaluation.
/// </summary>
internal sealed class RecordingPersonRescore : IPersonAnalysisRescoreService
{
    public List<(Guid AnalysisId, DateTime Now)> Calls { get; } = [];

    public Task<AnalysisRescoreOutcome> RescoreAsync(
        Analysis analysis, AnalysisResult result, string tenantId, Guid userId, DateTime now,
        string? ipAddress, string? requestId, CancellationToken ct)
    {
        Calls.Add((analysis.AnalysisId, now));
        return Task.FromResult(AnalysisRescoreOutcome.Refused(409, "recorded"));
    }
}
