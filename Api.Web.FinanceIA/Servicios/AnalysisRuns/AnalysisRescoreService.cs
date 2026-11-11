// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// Re-scores a balance analysis in place. It reads the PERSISTED extraction (BalanceJson, and
/// RatiosJson as the fallback the pipeline itself used), queries the BCRA live, reads the entity's
/// CURRENT LUFE / CASFOG / ARCA / NOSIS snapshots, and recomputes ratios, score, breakdown and
/// narrative through <see cref="AnalysisScoringStage"/> — the same code a fresh analysis runs.
/// </summary>
/// <remarks>
/// <para>
/// What a plain re-score never does: call the Claude extraction, touch the extraction-only columns
/// (BalanceJson, RiskAlerts, Anomalies, ExecutiveSummary, CatalogueAlerts, the Alerts rows, ValidationStatus,
/// the AI hashes and tokens — a balance correction alone rewrites BalanceJson and ValidationStatus, see
/// below), re-run the cash-flow reconciliation (it APPENDS to the anomalies), write an Analyses row, count
/// against the monthly quota or notify anyone.
/// </para>
/// <para>
/// A re-score must never leave a result more degraded than it found it: a BCRA that still fails, or
/// an entity that no longer resolves, aborts with 503 BEFORE anything is spent on the narrative or
/// written. Every refusal leaves the result and the history untouched.
/// </para>
/// <para>
/// A persona humana analysis passes the same eligibility rules and the same cooldown, then is handed to
/// <see cref="IPersonAnalysisRescoreService"/>, which re-evaluates it in place instead of re-scoring it.
/// </para>
/// <para>
/// A balance correction (<see cref="CorrectBalanceAsync"/>, Phase 10-F3a) runs the SAME pipeline over an
/// IN-MEMORY corrected BalanceJson: same eligibility, cooldown and gates, same engine. Only its write differs:
/// the corrected BalanceJson, the ValidationStatus recomputed from its re-evaluated integrity (the extraction's
/// own rule, <see cref="BalanceValidationStatus"/>: Passed when that integrity passes, Warning otherwise) and one
/// [AnalysisEdits] row per figure ride in the one transaction with the re-score's update and history row, so all
/// of it lands or none of it does. The history row keeps the previous BalanceJson and ValidationStatus.
/// </para>
/// <para>
/// Logs carry AnalysisId / ResultId / TenantId / UserId and counts only — never a CUIT, a business name, a
/// figure, a correction reason or a payload.
/// </para>
/// </remarks>
public sealed class AnalysisRescoreService : IAnalysisRescoreService
{
    private static readonly JsonSerializerOptions HistoryJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // The same deterministic checks the extraction runs. Stateless, so one instance serves every call.
    private static readonly IBalanceIntegrityService BalanceIntegrity = new BalanceIntegrityService();

    private readonly IAnalysisResultRepository   _analysisResultRepository;
    private readonly IAnalysisRepository         _analysisRepository;
    private readonly IDocumentRepository         _documentRepository;
    private readonly IAnalysisRescoreRepository  _rescoreRepository;
    private readonly IApiCallLogService          _apiCallLogService;
    private readonly IAuditService               _auditService;
    private readonly RescoreOptions              _options;
    private readonly TimeProvider                _timeProvider;
    private readonly ILogger<AnalysisRescoreService> _logger;
    private readonly AnalysisScoringStage        _scoringStage;
    private readonly IPersonAnalysisRescoreService _personRescoreService;

    /// <summary>Initializes a new instance of <see cref="AnalysisRescoreService"/> on the system clock.</summary>
    public AnalysisRescoreService(
        IAnalysisResultRepository        analysisResultRepository,
        IAnalysisRepository              analysisRepository,
        IDocumentRepository              documentRepository,
        IAnalysisRescoreRepository       rescoreRepository,
        IEntityRepository                entityRepository,
        IBcraService                     bcraService,
        IScoringService                  scoringService,
        IRatioCalculationService         ratioCalculationService,
        IClaudeService                   claudeService,
        IApiCallLogService               apiCallLogService,
        IAuditService                    auditService,
        IPersonAnalysisRescoreService    personRescoreService,
        IOptions<RescoreOptions>         options,
        ILogger<AnalysisRescoreService>  logger)
        : this(analysisResultRepository, analysisRepository, documentRepository, rescoreRepository,
               entityRepository, bcraService, scoringService, ratioCalculationService, claudeService,
               apiCallLogService, auditService, personRescoreService, options, logger, TimeProvider.System)
    {
    }

    /// <summary>Clock-injectable constructor, so tests can step past the cooldown without waiting.</summary>
    internal AnalysisRescoreService(
        IAnalysisResultRepository        analysisResultRepository,
        IAnalysisRepository              analysisRepository,
        IDocumentRepository              documentRepository,
        IAnalysisRescoreRepository       rescoreRepository,
        IEntityRepository                entityRepository,
        IBcraService                     bcraService,
        IScoringService                  scoringService,
        IRatioCalculationService         ratioCalculationService,
        IClaudeService                   claudeService,
        IApiCallLogService               apiCallLogService,
        IAuditService                    auditService,
        IPersonAnalysisRescoreService    personRescoreService,
        IOptions<RescoreOptions>         options,
        ILogger<AnalysisRescoreService>  logger,
        TimeProvider                     timeProvider)
    {
        _analysisResultRepository = analysisResultRepository;
        _analysisRepository       = analysisRepository;
        _documentRepository       = documentRepository;
        _rescoreRepository        = rescoreRepository;
        _apiCallLogService        = apiCallLogService;
        _auditService             = auditService;
        _personRescoreService     = personRescoreService;
        _options                  = options.Value;
        _timeProvider             = timeProvider;
        _logger                   = logger;
        _scoringStage             = new AnalysisScoringStage(
            entityRepository, bcraService, scoringService, ratioCalculationService, claudeService, logger);
    }

    /// <inheritdoc />
    public Task<AnalysisRescoreOutcome> RescoreAsync(
        Guid resultId, string tenantId, Guid userId, string? ipAddress, string? requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return RunAsync(resultId, tenantId, userId, correction: null, ipAddress, requestId, ct);
    }

    /// <inheritdoc />
    public Task<AnalysisRescoreOutcome> CorrectBalanceAsync(
        Guid resultId, string tenantId, Guid userId, BalanceCorrectionCommand command,
        string? ipAddress, string? requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(command);

        return RunAsync(resultId, tenantId, userId, command, ipAddress, requestId, ct);
    }

    /// <summary>
    /// The re-score, over the persisted balance (<paramref name="correction"/> null) or over that balance
    /// with <paramref name="correction"/> applied in memory. Every outcome but a 200 writes nothing.
    /// </summary>
    private async Task<AnalysisRescoreOutcome> RunAsync(
        Guid resultId, string tenantId, Guid userId, BalanceCorrectionCommand? correction,
        string? ipAddress, string? requestId, CancellationToken ct)
    {

        // ── Load: the full row, because every overwritten column is copied into the history ────
        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, ct)
            .ConfigureAwait(false);

        if (result is null)
            return AnalysisRescoreOutcome.NotFound();

        var analysis = await _analysisRepository
            .GetByIdAsync(result.AnalysisId, ct)
            .ConfigureAwait(false);

        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return AnalysisRescoreOutcome.NotFound();

        // ── A persona humana analysis has no extracted balance: nothing to correct ─────────────
        if (correction is not null
            && string.Equals(analysis.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Balance correction of analysis {AnalysisId} result {ResultId} tenant {TenantId} refused by user {UserId}: persona humana",
                analysis.AnalysisId, resultId, tenantId, userId);
            return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.PersonAnalysisNotCorrectable);
        }

        // ── Eligibility (the same rules the detail's canRescore reports) ───────────────────────
        var blocked = await EvaluateAsync(analysis, result, tenantId, ct).ConfigureAwait(false);
        if (blocked is not null)
        {
            _logger.LogInformation(
                "Re-score of analysis {AnalysisId} result {ResultId} tenant {TenantId} refused by user {UserId}: {Reason}",
                analysis.AnalysisId, resultId, tenantId, userId, blocked);
            return AnalysisRescoreOutcome.Refused(409, blocked);
        }

        // ── Cooldown, off the persisted RescoredAt so it holds across instances ────────────────
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _options.CooldownSeconds));
        if (result.RescoredAt is { } lastRescore && now - lastRescore < cooldown)
        {
            var retryAfter = (int)Math.Ceiling((cooldown - (now - lastRescore)).TotalSeconds);
            return AnalysisRescoreOutcome.Refused(429, RescoreErrorCodes.TooManyRequests, Math.Max(1, retryAfter));
        }

        // ── A persona humana analysis is re-evaluated, not re-scored ──────────────────────────
        if (string.Equals(analysis.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
        {
            return await _personRescoreService
                .RescoreAsync(analysis, result, tenantId, userId, now, ipAddress, requestId, ct)
                .ConfigureAwait(false);
        }

        // ── A correction: apply it to a COPY of the balance, in memory; the figures it fails on ──
        //    (a missing line, an unchanged value) are 400s, before anything is spent.
        var balanceJson = result.BalanceJson;
        BalanceCorrectionApplication? applied = null;
        if (correction is not null)
        {
            applied = BalanceCorrectionApplier.Apply(result.BalanceJson, correction, userId, now, BalanceIntegrity);

            if (applied.Unreadable)
            {
                _logger.LogWarning(
                    "Balance correction of analysis {AnalysisId} tenant {TenantId} refused: the stored balance is unreadable",
                    analysis.AnalysisId, tenantId);
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.ResultMissing);
            }

            if (!applied.IsApplied)
            {
                _logger.LogInformation(
                    "Balance correction of analysis {AnalysisId} tenant {TenantId} refused by user {UserId}: " +
                    "{ErrorCount} correction(s) fail the stored balance",
                    analysis.AnalysisId, tenantId, userId, applied.Errors.Count);
                return AnalysisRescoreOutcome.Invalid(applied.Errors);
            }

            if (applied.IntegrityUnavailable)
                _logger.LogWarning(
                    "Balance correction of analysis {AnalysisId} tenant {TenantId}: the integrity re-evaluation failed; " +
                    "the corrected balance carries integrity=null",
                    analysis.AnalysisId, tenantId);

            balanceJson = applied.BalanceJson;
        }

        // ── Live BCRA + entity snapshots. A degraded enrichment aborts BEFORE anything is spent ─
        var enrichment = await _scoringStage
            .ResolveEnrichmentAsync(analysis.EntityId, tenantId, analysis.AnalysisId, ct)
            .ConfigureAwait(false);

        if (enrichment.Enrichment.Outcome == BcraEnrichmentOutcome.Degraded)
        {
            _logger.LogWarning(
                "Re-score of analysis {AnalysisId} tenant {TenantId} aborted: the BCRA lookup or the entity is " +
                "still unavailable. Nothing was changed",
                analysis.AnalysisId, tenantId);
            return AnalysisRescoreOutcome.Refused(
                503, RescoreErrorCodes.BcraUnavailable, Math.Max(1, _options.BcraRetryAfterSeconds));
        }

        // ── Ratios from the persisted extraction; score; breakdown ─────────────────────────────
        var ratios = _scoringStage.ComputeRatios(
            balanceJson, result.RatiosJson, analysis.AnalysisId, tenantId);

        ScoreOutcome scored;
        try
        {
            scored = await _scoringStage
                .ScoreAsync(
                    tenantId, analysis.AnalysisId, enrichment.Entity, ratios, enrichment.BcraData,
                    balanceJson, analysis.FiscalYear, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Re-score of analysis {AnalysisId} tenant {TenantId} failed in the scoring engine. Nothing was changed",
                analysis.AnalysisId, tenantId);
            return AnalysisRescoreOutcome.Refused(500, RescoreErrorCodes.Failed);
        }

        // ── Second gate, still before anything is spent: a failed cheques lookup is degraded when
        //    the penalty counts for this tenant, which only the engine's read of the config can tell.
        //    The stored score — with whatever cheques penalty it carries — survives untouched.
        var assessment = AnalysisScoringStage.AssessAfterScoring(enrichment, scored.Scoring);
        if (assessment.Outcome == BcraEnrichmentOutcome.Degraded)
        {
            _logger.LogWarning(
                "Re-score of analysis {AnalysisId} tenant {TenantId} aborted: the BCRA cheques lookup is " +
                "unavailable and the cheques penalty counts for this tenant. Nothing was changed",
                analysis.AnalysisId, tenantId);
            return AnalysisRescoreOutcome.Refused(
                503, RescoreErrorCodes.BcraUnavailable, Math.Max(1, _options.BcraRetryAfterSeconds));
        }

        // ── Narrative. A failure keeps the new score and stores NO narrative: the old one ──────
        //    describes a different score and must not survive next to the new one.
        //    It states the reliability of the NEW score: the assessment above, this breakdown, and `now` —
        //    the RescoredAt this write stamps — as the score's instant.
        var narrative = await _scoringStage
            .GenerateNarrativeAsync(
                scored, ratios.Ratios, enrichment.BcraData, assessment, analysis.FiscalYear,
                result.GeneratedAt, now, analysis.AnalysisId, tenantId, ct)
            .ConfigureAwait(false);

        var scoringNarrative = string.IsNullOrWhiteSpace(narrative.Text) ? null : narrative.Text;
        if (scoringNarrative is null)
            _logger.LogWarning(
                "Re-score of analysis {AnalysisId} tenant {TenantId}: the scoring narrative is unavailable; " +
                "the new score is stored without one",
                analysis.AnalysisId, tenantId);

        // ── Persist: update + history in one transaction, guarded by RescoreCount ──────────────
        var entity  = enrichment.Entity;
        var sources = RescoreSources.FromEnrichment(enrichment, now);

        var history = new AnalysisRescore
        {
            RescoreId                = Guid.NewGuid(),
            TenantId                 = tenantId,
            AnalysisId               = analysis.AnalysisId,
            ResultId                 = result.ResultId,
            RescoredAt               = now,
            RescoredBy               = userId,
            PreviousScore            = result.AiSuggestedScore,
            NewScore                 = scored.Scoring.Score,
            PreviousLevel            = result.AiSuggestedLevel,
            NewLevel                 = scored.Scoring.Level,
            PreviousEnrichmentStatus = result.EnrichmentStatus,
            NewEnrichmentStatus      = assessment.Outcome.ToString(),
            PreviousValuesJson       = JsonSerializer.Serialize(PreviousValues(result, applied), HistoryJsonOpts),
            SourcesJson              = JsonSerializer.Serialize(sources, RescoreSources.JsonOpts)
        };

        var write = new AnalysisRescoreWrite
        {
            ResultId             = result.ResultId,
            AnalysisId           = analysis.AnalysisId,
            TenantId             = tenantId,
            ExpectedRescoreCount = result.RescoreCount,
            RescoredAt           = now,
            RescoredBy           = userId,
            RatiosJson           = ratios.ComputedRatiosJson ?? result.RatiosJson,
            BcraDataJson         = enrichment.BcraDataJson,
            MacroDataJson        = enrichment.MacroDataJson,
            AiSuggestedScore     = scored.Scoring.Score,
            AiSuggestedLevel     = scored.Scoring.Level,
            ScoreBreakdownJson   = scored.ScoreBreakdownJson,
            ScoringNarrative     = scoringNarrative,
            // The same point-in-time copy the pipeline takes: what the entity holds NOW.
            LufeDataJson         = entity?.LufeDataJson,
            FiscalDataJson       = entity?.FiscalDataJson,
            NosisDataJson        = entity?.NosisDataJson,
            CasfogDataJson       = entity?.CasfogDataJson,
            EnrichmentStatus     = assessment.Outcome.ToString(),
            EnrichmentNotes      = assessment.Notes,
            History              = history,
            Correction           = applied is null ? null : new BalanceCorrectionWrite
            {
                BalanceJson      = applied.BalanceJson!,
                // Integrity alone decides: the stored balanced/difference_pct are the model's report of the
                // ORIGINAL reading, which the correction does not rewrite. An unavailable integrity is a Warning.
                ValidationStatus = CorrectedValidationStatus(applied),
                Edits            = applied.Applied.Select(a => new AnalysisEdit
                {
                    AnalysisId = analysis.AnalysisId,
                    TenantId   = tenantId,
                    EditedBy   = userId,
                    FieldPath  = a.Path,
                    OldValue   = Invariant(a.OldValue),
                    NewValue   = Invariant(a.NewValue),
                    EditReason = correction!.Reason,
                    EditedAt   = now
                }).ToList()
            }
        };

        var writeOutcome = await _rescoreRepository.TryApplyAsync(write, ct).ConfigureAwait(false);

        // The narrative was paid for whatever the write did, so it is logged either way.
        if (narrative.HasUsage)
            AnalysisScoringStage.LogNarrativeCall(_apiCallLogService, tenantId, userId, analysis.AnalysisId, narrative);

        switch (writeOutcome)
        {
            case AnalysisRescoreWriteOutcome.LostRace:
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.InProgress);
            case AnalysisRescoreWriteOutcome.NotPending:
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.NotPending);
            case AnalysisRescoreWriteOutcome.HasVotes:
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.HasVotes);
        }

        if (applied is not null)
        {
            AuditCorrection(tenantId, userId, analysis.AnalysisId, result.BalanceJson, applied, correction!.Origin, ipAddress, requestId);

            _logger.LogInformation(
                "Analysis {AnalysisId} result {ResultId} tenant {TenantId}: {CorrectionCount} balance figure(s) " +
                "corrected by user {UserId}; integrity passed {IntegrityPassed}, validation status " +
                "{PreviousValidationStatus} -> {ValidationStatus}",
                analysis.AnalysisId, result.ResultId, tenantId, applied.Applied.Count, userId,
                applied.IntegrityPassed, result.ValidationStatus, CorrectedValidationStatus(applied));
        }
        else
        {
            _ = _auditService.LogAsync(
                tenantId:     tenantId,
                userId:       userId,
                action:       "Analysis.Rescored",
                severity:     "Info",
                entityType:   "Analysis",
                entityId:     analysis.AnalysisId.ToString(),
                ipAddress:    ipAddress,
                requestId:    requestId,
                oldValueJson: JsonSerializer.Serialize(new
                {
                    score            = history.PreviousScore,
                    level            = history.PreviousLevel,
                    enrichmentStatus = history.PreviousEnrichmentStatus
                }),
                newValueJson: JsonSerializer.Serialize(new
                {
                    score            = history.NewScore,
                    level            = history.NewLevel,
                    enrichmentStatus = history.NewEnrichmentStatus
                }),
                ct:           CancellationToken.None);
        }

        _logger.LogInformation(
            "Analysis {AnalysisId} result {ResultId} tenant {TenantId} re-scored by user {UserId}: " +
            "score {PreviousScore} -> {NewScore}, enrichment {PreviousEnrichment} -> {NewEnrichment}",
            analysis.AnalysisId, result.ResultId, tenantId, userId,
            history.PreviousScore, history.NewScore,
            history.PreviousEnrichmentStatus, history.NewEnrichmentStatus);

        return AnalysisRescoreOutcome.Rescored(new AnalysisRescoreSummary(
            history.PreviousScore,
            history.NewScore,
            history.PreviousLevel,
            history.NewLevel,
            history.PreviousEnrichmentStatus,
            history.NewEnrichmentStatus,
            now,
            sources));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRescoreHistoryItem>?> GetHistoryAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var analysisId = await _rescoreRepository
            .GetAnalysisIdAsync(resultId, tenantId, ct)
            .ConfigureAwait(false);

        if (analysisId is null)
            return null;

        return await _rescoreRepository
            .GetHistoryAsync(analysisId.Value, tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The history row's PreviousValuesJson shape: a plain re-score's, or — for a correction — the same plus
    /// the trigger and the BalanceJson as it was, so the correction is reversible by hand from that row.
    /// </summary>
    private static RescorePreviousValues PreviousValues(AnalysisResult result, BalanceCorrectionApplication? applied)
    {
        var previous = RescorePreviousValues.From(result);

        return applied is null
            ? previous
            : previous with
            {
                Trigger          = RescorePreviousValues.BalanceCorrectionTrigger,
                BalanceJson      = result.BalanceJson,
                ValidationStatus = result.ValidationStatus
            };
    }

    /// <summary>The corrected balance's ValidationStatus: its re-evaluated integrity alone decides.</summary>
    private static string CorrectedValidationStatus(BalanceCorrectionApplication applied)
        => BalanceValidationStatus.Derive(
            modelBalanceCheck: null,
            integrityPassed:   BalanceValidationStatus.IntegrityEvidence(
                applied.IntegrityPassed, applied.IntegrityFailedChecks, applied.IntegrityNetProfitSignFlip,
                applied.IntegrityNetProfitColumnMix));

    private static string? Invariant(double? value)
        => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The one "Analysis.BalanceCorrected" AuditLogs row, fire-and-forget like every audit write: paths and
    /// counts only — never a figure, never the reason.
    /// </summary>
    private void AuditCorrection(
        string tenantId, Guid userId, Guid analysisId, string? previousBalanceJson,
        BalanceCorrectionApplication applied, string origin, string? ipAddress, string? requestId)
    {
        var before = ManualCorrectionsReader.Read(previousBalanceJson);

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Analysis.BalanceCorrected",
            severity:     "Info",
            entityType:   "Analysis",
            entityId:     analysisId.ToString(),
            ipAddress:    ipAddress,
            requestId:    requestId,
            oldValueJson: JsonSerializer.Serialize(new
            {
                correctedFigures = before?.Count ?? 0
            }),
            newValueJson: JsonSerializer.Serialize(new
            {
                paths            = applied.Applied.Select(a => a.Path).ToArray(),
                count            = applied.Applied.Count,
                correctedFigures = ManualCorrectionsReader.Read(applied.BalanceJson)?.Count ?? 0,
                integrityPassed  = applied.IntegrityPassed,
                origin
            }),
            ct:           CancellationToken.None);
    }

    private async Task<string?> EvaluateAsync(
        Analysis analysis, AnalysisResult result, string tenantId, CancellationToken ct)
    {
        var (blocked, _) = await CorrectionEligibility
            .EvaluateAsync(analysis, result, tenantId, _rescoreRepository, _documentRepository, ct)
            .ConfigureAwait(false);
        return blocked;
    }
}
