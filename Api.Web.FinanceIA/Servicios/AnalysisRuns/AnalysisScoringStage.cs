// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using static Api.Web.FinanceIA.Servicios.AnalysisRuns.AnalysisPayloadJson;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// What <see cref="AnalysisScoringStage.ResolveEnrichmentAsync"/> found: the entity (null when the lookup
/// failed or found nothing), the live BCRA payload with its two serialised columns, and the provenance
/// verdict persisted as <c>AnalysisResults.EnrichmentStatus</c>/<c>EnrichmentNotes</c>.
/// </summary>
internal sealed record EnrichmentSnapshot(
    Entity?                   Entity,
    BcraData?                 BcraData,
    string?                   BcraDataJson,
    string?                   MacroDataJson,
    BcraEnrichmentAssessment  Enrichment);

/// <summary>The ratios the score reads, and the deterministic recompute when it succeeded.</summary>
internal sealed record RatiosOutcome(RatiosData Ratios, string? ComputedRatiosJson);

/// <summary>The engine's result and the breakdown persisted beside it as <c>ScoreBreakdownJson</c>.</summary>
internal sealed record ScoreOutcome(ScoringResult Scoring, string ScoreBreakdownJson);

/// <summary>
/// The Haiku narrative. <see cref="Text"/> is empty — never null — when the model call failed or was
/// skipped: <see cref="IClaudeService.GenerateScoringNarrativeAsync"/> swallows its own failures.
/// </summary>
internal sealed record NarrativeOutcome(string Text, int InputTokens, int OutputTokens, decimal CostUsd)
{
    public bool HasUsage => InputTokens > 0 || OutputTokens > 0;
}

/// <summary>
/// The integration-dependent half of an analysis, after the Claude extraction: the entity and its live
/// BCRA lookup, the deterministic ratios, the score, its breakdown and the narrative. Shared by
/// <see cref="AnalysisPipeline"/> (a fresh analysis) and <see cref="AnalysisRescoreService"/> (a
/// re-score of a persisted one), so both compute a score with the SAME code and identical inputs give
/// identical results.
/// </summary>
/// <remarks>
/// <para>
/// Every method is a verbatim move of the block that used to live inline in
/// <see cref="AnalysisPipeline.RunAsync"/>, including its fault isolation and its log lines. What stays
/// in the pipeline is everything that depends only on the extraction — the cash-flow reconciliation,
/// the catalogue rule engine and the percentage reconciliation — because a re-score must never run
/// them again (the cash-flow block APPENDS to the anomalies array).
/// </para>
/// <para>
/// Not registered in DI: each caller builds one from the dependencies it already holds, so the
/// pipeline's constructor — and every test that calls it — is unchanged. The logger is the caller's,
/// so the pipeline's log lines keep their category.
/// </para>
/// </remarks>
internal sealed class AnalysisScoringStage
{
    private readonly IEntityRepository        _entityRepository;
    private readonly IBcraService             _bcraService;
    private readonly IScoringService          _scoringService;
    private readonly IRatioCalculationService _ratioCalculationService;
    private readonly IClaudeService           _claudeService;
    private readonly ILogger                  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisScoringStage"/>. Plain assignment, no checks:
    /// like the pipeline it came from, tests pass null for dependencies a given path never reaches.
    /// </summary>
    public AnalysisScoringStage(
        IEntityRepository        entityRepository,
        IBcraService             bcraService,
        IScoringService          scoringService,
        IRatioCalculationService ratioCalculationService,
        IClaudeService           claudeService,
        ILogger                  logger)
    {
        _entityRepository        = entityRepository;
        _bcraService             = bcraService;
        _scoringService          = scoringService;
        _ratioCalculationService = ratioCalculationService;
        _claudeService           = claudeService;
        _logger                  = logger;
    }

    /// <summary>
    /// Fetches the entity, queries the BCRA live for its CUIT and classifies the outcome. Never throws:
    /// both lookups are fault-isolated and a failure is recorded in the assessment, never re-thrown.
    /// </summary>
    public async Task<EnrichmentSnapshot> ResolveEnrichmentAsync(
        Guid entityId, string tenantId, Guid analysisId, CancellationToken ct)
    {
        // ── Fetch entity (shared by BCRA + Scoring) ─────────────────────────────

        Entity? entity = null;
        // Either arm below leaves us without an entity, and therefore without a CUIT to query.
        // The analysis still completes and is still scored WITHOUT the BCRA component, so this
        // is a degraded enrichment, not an inapplicable one. Note TriggerAsync validates the
        // document but never validates that run.EntityId resolves, so the not-found arm is
        // reachable on its own (a stale id, or one belonging to another tenant).
        bool entityUnresolved = false;

        try
        {
            entity = await _entityRepository
                .GetByIdAsync(entityId, tenantId, ct)
                .ConfigureAwait(false);

            if (entity is null)
            {
                entityUnresolved = true;
                _logger.LogWarning(
                    "Entity {EntityId} not found for analysis {AnalysisId} tenant {TenantId}; continuing without entity data",
                    entityId, analysisId, tenantId);
            }
        }
        catch (Exception ex)
        {
            entityUnresolved = true;
            _logger.LogWarning(ex,
                "Entity lookup failed for analysis {AnalysisId} tenant {TenantId}; continuing without entity data",
                analysisId, tenantId);
        }

        // ── Fetch BCRA data for the entity's CUIT (fault-isolated) ────────────────

        string?   bcraDataJson    = null;
        string?   macroDataJson   = null;
        BcraData? bcraData        = null;
        bool      bcraLookupThrew = false;

        try
        {
            if (entity is not null && !string.IsNullOrWhiteSpace(entity.CUIT))
            {
                bcraData = await _bcraService
                    .GetBcraDataForAnalysisAsync(entity.CUIT, ct)
                    .ConfigureAwait(false);

                if (bcraData is not null)
                {
                    bcraDataJson = JsonSerializer.Serialize(bcraData);
                    if (bcraData.Macro is not null)
                        macroDataJson = JsonSerializer.Serialize(bcraData.Macro);
                    // No CUIT in the log: the analysis id identifies the lookup without carrying PII.
                    _logger.LogInformation(
                        "BCRA data enriched for analysis {AnalysisId} tenant {TenantId}",
                        analysisId, tenantId);
                }
            }
        }
        catch (Exception ex)
        {
            bcraLookupThrew = true;
            _logger.LogWarning(ex,
                "BCRA enrichment failed for analysis {AnalysisId} tenant {TenantId}; continuing without BCRA data",
                analysisId, tenantId);
        }

        // Persisted alongside the result so a transient failure — on either the entity side or
        // the BCRA side — is distinguishable from an entity that simply has no CUIT. All three
        // produce a row scored without the whole 0.35 BCRA weight; only the last one is
        // permanent. Fault isolation is unchanged: a failure is recorded, never re-thrown.
        // The DEUDA verdict, before scoring: whether the cheques penalty counts is known only once the
        // engine has read the tenant's config, so a failed cheques lookup is assessed afterwards
        // (AssessAfterScoring). This verdict is also the re-score's first gate.
        var enrichment = BcraEnrichmentClassifier.Classify(
            entityUnresolved, entity?.CUIT, bcraLookupThrew, bcraData, chequesCount: false);

        if (enrichment.Outcome == BcraEnrichmentOutcome.Degraded)
            _logger.LogWarning(
                "BCRA enrichment degraded for analysis {AnalysisId} tenant {TenantId}: {EnrichmentNotes}. " +
                "The score excludes the BCRA component and its weight was renormalised",
                analysisId, tenantId, enrichment.Notes);

        return new EnrichmentSnapshot(entity, bcraData, bcraDataJson, macroDataJson, enrichment);
    }

    /// <summary>
    /// The enrichment verdict once the score exists: the deuda verdict of
    /// <see cref="ResolveEnrichmentAsync"/>, turned <see cref="BcraEnrichmentOutcome.Degraded"/> when the
    /// cheques lookup failed AND the cheques penalty took part in this score
    /// (<see cref="ScoringResult.ChequesEvaluated"/> — the tenant's effective flag, read by the engine
    /// itself, so no second config read). A verdict that is not Complete is returned as it is: its note
    /// wins. Complete implies a resolved entity, no throw and a payload, so re-running the classifier
    /// with those inputs is exact. Pure.
    /// </summary>
    internal static BcraEnrichmentAssessment AssessAfterScoring(EnrichmentSnapshot enrichment, ScoringResult scoring)
    {
        ArgumentNullException.ThrowIfNull(enrichment);
        ArgumentNullException.ThrowIfNull(scoring);

        if (enrichment.Enrichment.Outcome != BcraEnrichmentOutcome.Complete || !scoring.ChequesEvaluated)
            return enrichment.Enrichment;

        return BcraEnrichmentClassifier.Classify(
            entityUnresolved: false,
            cuit:             enrichment.Entity?.CUIT,
            bcraLookupThrew:  false,
            bcraData:         enrichment.BcraData,
            chequesCount:     true);
    }

    /// <summary>
    /// The deterministic ratios from <paramref name="balanceJson"/>, falling back to
    /// <paramref name="fallbackRatiosJson"/> (the model's own ratios) when the balance is absent or the
    /// recompute fails. A divergence between the two is logged, never surfaced. Never throws.
    /// </summary>
    /// <remarks>
    /// A persisted BalanceJson carries the injected <c>cash_flow.reconciliation</c> node, which
    /// <see cref="BalanceAccounts"/> does not map — so the persisted blob yields the same ratios as the
    /// raw extraction did.
    /// </remarks>
    public RatiosOutcome ComputeRatios(
        string? balanceJson, string? fallbackRatiosJson, Guid analysisId, string tenantId)
    {
        try
        {
            var accounts = string.IsNullOrWhiteSpace(balanceJson)
                ? null
                : JsonSerializer.Deserialize<BalanceAccounts>(balanceJson, BcraJsonOpts);

            if (accounts is not null)
            {
                var computedRatios     = _ratioCalculationService.Calculate(accounts);
                var computedRatiosJson = JsonSerializer.Serialize(computedRatios);

                // Cross-check against LLM ratios
                if (!string.IsNullOrWhiteSpace(fallbackRatiosJson))
                {
                    var llmRatios = JsonSerializer.Deserialize<RatiosData>(
                        fallbackRatiosJson, BcraJsonOpts);

                    if (llmRatios is not null)
                    {
                        // Cross-check the LLM ratios against the deterministic recompute.
                        // On a proven divergence the deterministically-computed value is used
                        // (see the return below); the divergence is LOGGED for traceability, never
                        // surfaced as a client-facing anomaly — the anomalies list is reserved for
                        // findings about the client's document, not notes about our own processing.
                        void CheckDivergence(string key, double? llm, double? computed)
                        {
                            if (llm is null || computed is null) return;
                            var absDiff = Math.Abs(llm.Value - computed.Value);
                            var relDiff = computed.Value != 0
                                ? absDiff / Math.Abs(computed.Value)
                                : absDiff;
                            if (absDiff > 0.05 && relDiff > 0.05)
                            {
                                _logger.LogInformation(
                                    "Ratio divergence [{Key}] analysis {AnalysisId} tenant {TenantId}: " +
                                    "llm={Llm} computed={Computed}; using the deterministically-computed value",
                                    key, analysisId, tenantId, llm, computed);
                            }
                        }

                        CheckDivergence("current_ratio",     llmRatios.CurrentRatio,     computedRatios.CurrentRatio);
                        CheckDivergence("acid_test",          llmRatios.AcidTest,         computedRatios.AcidTest);
                        CheckDivergence("debt_ratio",         llmRatios.DebtRatio,        computedRatios.DebtRatio);
                        CheckDivergence("leverage",           llmRatios.Leverage,         computedRatios.Leverage);
                        CheckDivergence("roe",                llmRatios.Roe,              computedRatios.Roe);
                        CheckDivergence("roa",                llmRatios.Roa,              computedRatios.Roa);
                        CheckDivergence("net_margin",         llmRatios.NetMargin,        computedRatios.NetMargin);
                        CheckDivergence("interest_coverage",  llmRatios.InterestCoverage, computedRatios.InterestCoverage);
                    }
                }

                return new RatiosOutcome(computedRatios, computedRatiosJson);
            }

            // BalanceJson absent — fall back to LLM ratios
            return new RatiosOutcome(DeserializeFallbackRatios(fallbackRatiosJson), null);
        }
        catch (Exception balEx)
        {
            _logger.LogWarning(balEx,
                "Deterministic ratio calculation failed for analysis {AnalysisId} tenant {TenantId}; " +
                "falling back to LLM ratios",
                analysisId, tenantId);

            return new RatiosOutcome(DeserializeFallbackRatios(fallbackRatiosJson), null);
        }
    }

    /// <summary>
    /// Scores the analysis and serialises its breakdown. THROWS when the engine does — the caller
    /// decides what a missing score means (the pipeline persists the analysis without one; a re-score
    /// changes nothing). The engine itself never throws: it returns a neutral 50 flagged
    /// <see cref="ScoringResult.EngineFailed"/>, which this method turns into a
    /// <see cref="ScoringEngineFailedException"/> so the placeholder never reaches the database.
    /// <para>
    /// It also records the facts the score was built on (score credibility 1b-2a) under
    /// <c>reliabilityFacts</c>, through <see cref="ScoreReliabilityFactsBuilder"/> — the one builder, so a
    /// fresh analysis and a re-score cannot disagree on them. Reporting only, from what is already in
    /// hand: no extra query, no external call.
    /// </para>
    /// </summary>
    /// <param name="balanceJson">The balance the analysis persists; its integrity verdict becomes a fact.</param>
    /// <param name="fiscalYear">The fiscal year the analysis was requested for (<c>Analyses.FiscalYear</c>).</param>
    public async Task<ScoreOutcome> ScoreAsync(
        string tenantId, Guid analysisId, Entity? entity, RatiosOutcome ratiosOutcome, BcraData? bcraData,
        string? balanceJson, int fiscalYear, CancellationToken ct)
    {
        var ratios    = ratiosOutcome.Ratios;
        var macroData = bcraData?.Macro;

        // The monthly sales series behind the §7.3a sales-drop penalty. Read off the SAME
        // in-memory entity blob that is frozen into AnalysisResults.LufeDataJson by the caller,
        // so the score and the evidence stored beside it can never disagree — a later
        // entity refresh rewrites the live blob, never this analysis's copy of it.
        // No external call and no extra query: the entity is already materialised here.
        // TryDeserializeLufeData never throws (it returns null on malformed JSON), and the
        // caller's scoring try/catch covers this besides, so a bad LUFE snapshot costs
        // the analysis its sales signal and nothing else. Only the series is passed on: the
        // rest of LufeData is identifying data the engine has no use for.
        var lufeSnapshot = entity?.LufeDataJson is { } lufeSnapshotJson
            ? TryDeserializeLufeData(lufeSnapshotJson)
            : null;
        var ventasMensuales = lufeSnapshot?.VentasComprasMensual;

        // The per-SGR guarantee rows behind the §7.4 arrears penalty. Same reasoning, and the
        // same bytes, as the LUFE series above: read off the in-memory entity blob that is
        // frozen into AnalysisResults.CasfogDataJson by the caller, so the score and the
        // evidence stored beside it can never disagree — a later refresh-casfog rewrites the
        // live blob, never this analysis's copy of it. No external call and no extra query;
        // ICasfogService is not reachable from here and does not need to be.
        // TryDeserializeCasfogData never throws (it returns null on malformed JSON), so a bad
        // CASFOG snapshot costs the analysis its arrears signal and nothing else. Only the rows
        // are passed on: the rest of CasfogData carries the related parties' names and CUITs,
        // which the engine has no business holding.
        var casfogSnapshot = entity?.CasfogDataJson is { } casfogSnapshotJson
            ? TryDeserializeCasfogData(casfogSnapshotJson)
            : null;
        var garantiasPorSgr = casfogSnapshot?.Garantias?.PorSgr;

        var scoringResult = await _scoringService
            .CalculateAsync(tenantId, entity?.Sector, ratios, bcraData, macroData, ventasMensuales, garantiasPorSgr, ct)
            .ConfigureAwait(false);

        // The engine's catch-all placeholder (50 / Medium, engine never ran) is NOT a score. Before
        // this guard it was persisted as one; now the analysis goes down the existing "no score" path.
        if (scoringResult.EngineFailed)
        {
            _logger.LogError(
                "Scoring engine failed for analysis {AnalysisId} tenant {TenantId}; the neutral placeholder is " +
                "not persisted as a score",
                analysisId, tenantId);
            throw new ScoringEngineFailedException();
        }

        // A composite computed under degraded ratio coverage must never be silent — record it
        // so it is identifiable after the fact (the breakdown also carries the flag below).
        if (scoringResult.RatiosLowCoverageFallback)
        {
            _logger.LogWarning(
                "Ratios low-coverage neutral fallback for analysis {AnalysisId} tenant {TenantId}: " +
                "non-computable ratios [{NonComputable}], surviving weight {SurvivingWeight} below " +
                "minimum coverage — composite kept at neutral behaviour, survivors not renormalised",
                analysisId, tenantId,
                string.Join(", ", scoringResult.NonComputableRatios),
                scoringResult.RatiosSurvivingWeight);
        }

        var facts = BuildReliabilityFacts(
            scoringResult,
            new ScoreReliabilityInputs(
                RatiosComputed: ratiosOutcome.ComputedRatiosJson is not null,
                BalanceJson:    balanceJson,
                FiscalYear:     fiscalYear,
                BcraData:       bcraData,
                Lufe:           lufeSnapshot,
                LufeSnapshotAt: entity?.LufeDataUpdatedAt,
                Casfog:         casfogSnapshot),
            analysisId, tenantId);

        return new ScoreOutcome(scoringResult, BuildScoreBreakdownJson(scoringResult, facts));
    }

    /// <summary>
    /// The reliability facts for a score, and the one Information line that lists the caveats they
    /// raise (codes only). Facts are reporting: a failure to build them is logged and costs the block —
    /// persisted as <c>reliabilityFacts: null</c> — never the score.
    /// </summary>
    private ScoreReliabilityFacts? BuildReliabilityFacts(
        ScoringResult scoringResult, ScoreReliabilityInputs inputs, Guid analysisId, string tenantId)
    {
        try
        {
            var facts   = ScoreReliabilityFactsBuilder.Build(scoringResult, inputs);
            var caveats = ScoreReliabilityFactsBuilder.CaveatCodes(scoringResult, facts);

            _logger.LogInformation(
                "Score reliability facts for analysis {AnalysisId} tenant {TenantId}: caveats [{Caveats}]",
                analysisId, tenantId, string.Join(", ", caveats));

            return facts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Score reliability facts could not be built for analysis {AnalysisId} tenant {TenantId}; " +
                "the score is persisted without them",
                analysisId, tenantId);
            return null;
        }
    }

    /// <summary>
    /// The Haiku narrative for <paramref name="scored"/>, with its token usage and cost. Does not throw on a
    /// model failure — the text comes back empty.
    /// <para>
    /// Score credibility 1b-3: the narrative is told the reliability verdict of the score it describes — the
    /// ONE adapter every surface uses, over the row as it is about to be persisted (assessed enrichment, this
    /// breakdown, the requested fiscal year, balance kind) and the score's own instant.
    /// </para>
    /// </summary>
    /// <param name="enrichment">The enrichment verdict AFTER scoring (<see cref="AssessAfterScoring"/>).</param>
    /// <param name="generatedAtUtc">The result's GeneratedAt: now for a new analysis, the original on a re-score.</param>
    /// <param name="rescoredAtUtc">Now on a re-score; null for a new analysis.</param>
    public async Task<NarrativeOutcome> GenerateNarrativeAsync(
        ScoreOutcome scored, RatiosData ratios, BcraData? bcraData, BcraEnrichmentAssessment enrichment,
        int fiscalYear, DateTime generatedAtUtc, DateTime? rescoredAtUtc, Guid analysisId, string tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scored);

        var reliability = EvaluateReliability(
            scored, enrichment, fiscalYear, generatedAtUtc, rescoredAtUtc, analysisId, tenantId);

        var narrativeResult = await _claudeService
            .GenerateScoringNarrativeAsync(
                scored.Scoring, ratios, bcraData, bcraData?.Macro, reliability,
                ct)
            .ConfigureAwait(false);

        var cost = narrativeResult.InputTokens > 0 || narrativeResult.OutputTokens > 0
            ? (narrativeResult.InputTokens  * 0.80m +
               narrativeResult.OutputTokens * 4m) / 1_000_000m
            : 0m;

        return new NarrativeOutcome(
            narrativeResult.Text, narrativeResult.InputTokens, narrativeResult.OutputTokens, cost);
    }

    /// <summary>
    /// The reliability verdict of a score that is about to be persisted. Reporting only: a failure costs the
    /// narrative its CALIDAD block (logged), never the narrative or the score. Internal for tests.
    /// </summary>
    internal ScoreReliabilityResponse? EvaluateReliability(
        ScoreOutcome scored, BcraEnrichmentAssessment enrichment, int fiscalYear,
        DateTime generatedAtUtc, DateTime? rescoredAtUtc, Guid analysisId, string tenantId)
    {
        try
        {
            var verdict = ScoreReliability.ScoreReliabilityAdapter.Evaluate(
                new ScoreReliability.ScoreReliabilityRow(
                    AiSuggestedScore: scored.Scoring.Score,
                    EnrichmentStatus: enrichment.Outcome.ToString(),
                    AnalysisKind:     AnalysisKinds.Balance,
                    FiscalYear:       fiscalYear,
                    GeneratedAt:      generatedAtUtc,
                    RescoredAt:       rescoredAtUtc),
                scored.ScoreBreakdownJson);

            if (verdict.BreakdownUnreadable)
                _logger.LogWarning(
                    "Score breakdown of analysis {AnalysisId} tenant {TenantId} could not be read; the narrative " +
                    "reports its reliability as facts_unavailable",
                    analysisId, tenantId);

            return verdict.Reliability;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Score reliability could not be evaluated for the narrative of analysis {AnalysisId} tenant {TenantId}; " +
                "the narrative is generated without it",
                analysisId, tenantId);
            return null;
        }
    }

    /// <summary>
    /// The <c>ApiCallLogs</c> row for one narrative call, fire-and-forget like every Claude log row.
    /// Only meaningful once the Analyses row exists (the log carries its id).
    /// </summary>
    public static void LogNarrativeCall(
        IApiCallLogService apiCallLogService, string tenantId, Guid userId, Guid analysisId, NarrativeOutcome narrative)
    {
        _ = apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "Claude",
            endpoint:         "/v1/messages",
            responseStatus:   200,
            inputTokens:      narrative.InputTokens,
            outputTokens:     narrative.OutputTokens,
            estimatedCostUsd: narrative.CostUsd,
            isSuccess:        true,
            errorMessage:     null,
            durationMs:       0,
            operation:        ApiCallLogOperations.Narrative,
            model:            ClaudeService.NarrativeModel,
            userId:           userId,
            analysisId:       analysisId,
            ct:               CancellationToken.None);
    }

    private static RatiosData DeserializeFallbackRatios(string? ratiosJson)
        => string.IsNullOrWhiteSpace(ratiosJson)
            ? new RatiosData()
            : JsonSerializer.Deserialize<RatiosData>(ratiosJson, BcraJsonOpts) ?? new RatiosData();

    /// <summary>
    /// The persisted <c>ScoreBreakdownJson</c>. Hand-camelCased keys: this Serialize call takes no
    /// naming policy. <paramref name="reliabilityFacts"/> is appended as the LAST key, so every key
    /// before it serialises exactly as it did before the block existed.
    /// </summary>
    internal static string BuildScoreBreakdownJson(ScoringResult scoringResult, ScoreReliabilityFacts? reliabilityFacts)
        => JsonSerializer.Serialize(new
        {
            scoreRatios      = scoringResult.ScoreRatios,
            scoreBcra        = scoringResult.ScoreBcra,
            scoreMacro       = scoringResult.ScoreMacro,
            breakdown        = scoringResult.Breakdown,
            scoreBeforeFloor = scoringResult.ScoreBeforeFloor,
            appliedFloor     = scoringResult.AppliedFloor,
            criticalRatios   = scoringResult.CriticalRatios,
            // The floor configuration the engine read (scoring algorithm 9): the trigger and
            // every ratio's floor, so the chat explains a floor with the numbers that produced
            // it rather than today's config. Absent on every earlier row.
            criticalFloorTrigger  = scoringResult.CriticalFloorTrigger,
            criticalFloors        = scoringResult.CriticalFloors,
            // Ratios whose stored thresholds were out of order and were scored on the default
            // triplet instead (scoring algorithm 9). Empty on a coherent config.
            thresholdFallbacks    = scoringResult.ThresholdFallbacks,
            bcraTrendDelta        = scoringResult.BcraTrendDelta,
            bcraTrendAdj          = scoringResult.BcraTrendAdj,
            // The rejected-cheques penalty inside the BCRA component (scoring algorithm 5).
            // Absent on every earlier row, which reads as "the engine that scored this had no
            // such signal" — not as a zero penalty it chose to apply.
            chequesAdj                  = scoringResult.ChequesAdj,
            chequesProporcionRecuperada = scoringResult.ChequesProporcionRecuperada,
            // The LUFE sales-drop penalty on the FINAL score (scoring algorithm 6, §7.3a;
            // measured in real, CPI-deflated terms since algorithm 7, §7.3b). Absent on every
            // earlier row, which reads as "the engine that scored this had no such signal" —
            // not as a zero penalty it chose to apply. These keys are hand-camelCased because
            // this Serialize call takes no naming policy.
            //
            // ventasVariacionEsReal is what tells the two engines apart on a given row:
            // absent = scored before §7.3b and therefore nominal; false = §7.3b evaluated it
            // but had no CPI covering the basis; true = deflated. Without it a v6 and a v7
            // variation are indistinguishable, and they are not the same measurement.
            ventasAdj             = scoringResult.VentasAdj,
            ventasVariacion       = scoringResult.VentasVariacion,
            ventasBase            = scoringResult.VentasBase,
            ventasVariacionEsReal = scoringResult.VentasVariacionEsReal,
            // The CASFOG guarantee-arrears penalty on the FINAL score (scoring algorithm 8,
            // §7.4). Absent on every earlier row, which reads as "the engine that scored this
            // had no such signal" — not as a zero penalty it chose to apply. Hand-camelCased
            // like the keys above, because this Serialize call takes no naming policy.
            //
            // casfogProporcionMora is null only when no share could be formed at all; a 0 is a
            // real reading — rows present, balance measurable, nothing in arrears — and the two
            // are different statements about the same portfolio.
            casfogMoraAdj         = scoringResult.CasfogMoraAdj,
            casfogProporcionMora  = scoringResult.CasfogProporcionMora,
            sectorProfile         = scoringResult.SectorProfile,
            bcraExcluded          = scoringResult.BcraExcluded,
            bcraExclusionReason   = scoringResult.BcraExclusionReason,
            effectiveWeightRatios = scoringResult.EffectiveWeightRatios,
            effectiveWeightBcra   = scoringResult.EffectiveWeightBcra,
            effectiveWeightMacro  = scoringResult.EffectiveWeightMacro,
            nonComputableRatios       = scoringResult.NonComputableRatios,
            ratiosLowCoverageFallback = scoringResult.RatiosLowCoverageFallback,
            ratiosSurvivingWeight     = scoringResult.RatiosSurvivingWeight,
            // The profitability floor waived on a positive operating result (scoring algorithm 10):
            // whether it changed anything, which ratios kept "Critico" without flooring the score, and
            // the sign of the operating result the rule read (null when it was not consulted). Absent
            // on every earlier row, which reads as "that engine floored any loss".
            profitabilityFloorWaived       = scoringResult.ProfitabilityFloorWaived,
            profitabilityFloorWaivedRatios = scoringResult.ProfitabilityFloorWaivedRatios,
            operatingResultSign            = scoringResult.OperatingResultSign,
            // The acid-test floor waived on a current ratio at or above its warn threshold (scoring
            // algorithm 11), and where the current ratio stood against that threshold (null when the rule
            // was not consulted or the ratio was not measured). Absent on every earlier row, which floored
            // any critical acid test.
            acidTestFloorWaived            = scoringResult.AcidTestFloorWaived,
            currentRatioVsWarn             = scoringResult.CurrentRatioVsWarn,
            // The cutoffs the level was classified with (score <= low → Low, <= medium → Medium,
            // <= high → High, above → Critical). Reporting only, no algorithm bump. The single source
            // a reader builds this row's level labels and NOSIS ranges from; absent on every earlier
            // row, which a reader labels with the tenant's current cutoffs (GET /api/billing/usage).
            levelCutoffs              = scoringResult.LevelCutoffs is { } cutoffs
                ? new { low = cutoffs.Low, medium = cutoffs.Medium, high = cutoffs.High }
                : null,
            // Which scoring algorithm produced this row. Absent on every analysis persisted
            // before the key existed, and those are read as version 1 (BCRA trend window
            // anchored on the wall clock) — they are deliberately never recomputed.
            scoringAlgoVersion        = scoringResult.ScoringAlgoVersion,
            // The facts this score was built on (score credibility 1b-2a): codes, flags, dates and
            // counts, no verdict. Reporting only — nothing reads it back into a score. Absent on every
            // row scored before it existed; null when it could not be built for this one.
            reliabilityFacts          = reliabilityFacts,
        });
}
