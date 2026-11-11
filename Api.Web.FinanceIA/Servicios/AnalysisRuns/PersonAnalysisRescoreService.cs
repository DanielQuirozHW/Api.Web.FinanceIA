// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// "Reanalizar" for a persona humana analysis: re-evaluates the solicitante and every fiador IN PLACE, on
/// their CURRENT legajos (Ready documents and their current runs, reconciliation, ARCA snapshot), the BCRA
/// snapshot under the same one-retry rule as POST /api/person-analyses, and the tenant's CURRENT pf_
/// parameters — with the SAME operation terms and the SAME per-fiador inputs the analysis was created with.
/// </summary>
/// <remarks>
/// <para>
/// It runs the very code POST /api/person-analyses runs (<see cref="PersonAnalysisEvaluator"/>), with the
/// same readiness refusals, all of them BEFORE anything is written — the BCRA re-query included. It then
/// overwrites the evaluation columns of every AnalysisParticipants row and the AnalysisResults columns the
/// evaluation derives, and records their previous values in [AnalysisRescores], in one transaction.
/// </para>
/// <para>
/// What it never does: change the terms or the list of fiadores, write an Analyses row (so it consumes no
/// unit of the monthly quota), touch a participant's identity, or call a model.
/// </para>
/// <para>
/// BCRA: unlike the balance re-score, which queries the BCRA live and refuses with 503 when it fails, the
/// persona evaluation reads the LEGAJO's snapshot and asks the BCRA again only when that snapshot is
/// unusable, exactly as at creation; a BCRA still down re-evaluates with SinDatos and EnrichmentStatus
/// Degraded, as the POST would.
/// </para>
/// <para>
/// PRIVACY: logs and audit carry ids and counts only — never a name or a CUIT of the solicitante or a fiador.
/// </para>
/// </remarks>
public sealed class PersonAnalysisRescoreService : IPersonAnalysisRescoreService
{
    /// <summary>The solicitante's headline indicators the summary compares.</summary>
    private static readonly string[] SummaryIndicatorIds = ["cobertura_servicio_deuda", "cuota_ingreso", "solvencia_pn"];

    private static readonly JsonSerializerOptions HistoryJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string PersonFailedDetail = "No se pudo reevaluar el análisis. El análisis no se modificó.";

    private readonly IEntityRepository                _entityRepository;
    private readonly IPersonAnalysisRepository        _personAnalysisRepository;
    private readonly IPersonAnalysisRescoreRepository _rescoreRepository;
    private readonly IAuditService                    _auditService;
    private readonly IPersonExpectedDocumentsService  _expectedDocuments;
    private readonly ILogger<PersonAnalysisRescoreService> _logger;
    private readonly PersonAnalysisEvaluator          _evaluator;

    /// <summary>Initializes a new instance of <see cref="PersonAnalysisRescoreService"/>.</summary>
    public PersonAnalysisRescoreService(
        IEntityRepository                   entityRepository,
        IPersonAnalysisRepository           personAnalysisRepository,
        IPersonLegajoRepository             personLegajoRepository,
        ILegajoDocumentRepository           legajoDocumentRepository,
        ILegajoReconciliationRepository     reconciliationRepository,
        IBcraService                        bcraService,
        IFiadorRepository                   fiadorRepository,
        IPersonLegajoSnapshotRefreshService snapshotRefreshService,
        IPersonAnalysisRescoreRepository    rescoreRepository,
        IAuditService                       auditService,
        IPersonExpectedDocumentsService     expectedDocuments,
        ILogger<PersonAnalysisRescoreService> logger)
    {
        _entityRepository         = entityRepository;
        _personAnalysisRepository = personAnalysisRepository;
        _rescoreRepository        = rescoreRepository;
        _auditService             = auditService;
        _expectedDocuments        = expectedDocuments;
        _logger                   = logger;
        _evaluator                = new PersonAnalysisEvaluator(
            personLegajoRepository, legajoDocumentRepository, reconciliationRepository, bcraService,
            fiadorRepository, snapshotRefreshService, logger);
    }

    /// <inheritdoc />
    public async Task<AnalysisRescoreOutcome> RescoreAsync(
        Analysis analysis, AnalysisResult result, string tenantId, Guid userId, DateTime now,
        string? ipAddress, string? requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // ── The rows to re-evaluate: the solicitante and the fiadores, as stored ──────────────
        var participants = await _personAnalysisRepository
            .GetParticipantsAsync(analysis.AnalysisId, tenantId, ct)
            .ConfigureAwait(false);
        var solicitanteRow = participants.FirstOrDefault(p => p.Role == ParticipantRoles.Solicitante);
        var fiadorRows     = PersonEvaluationJson.FiadorRowsOf(participants);

        if (solicitanteRow is null)
            return Refuse(analysis, tenantId, userId, AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.PersonAnalysis));

        // ── Readiness, all before any write: the same checks as POST /api/person-analyses ─────
        var entity = await _entityRepository.GetByIdAsync(analysis.EntityId, tenantId, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return Refuse(analysis, tenantId, userId, AnalysisRescoreOutcome.Refused(new PersonEvaluationRefusal(
                404, "Not Found", PersonEvaluationErrorCodes.EntityNotFound, "No se encontró la entidad.")));
        }

        var (solicitanteRefusal, legajo, documents) = await _evaluator
            .CheckSolicitanteAsync(entity, tenantId, ct)
            .ConfigureAwait(false);
        if (solicitanteRefusal is not null)
            return Refuse(analysis, tenantId, userId, AnalysisRescoreOutcome.Refused(solicitanteRefusal));

        // The row is re-evaluated in place, so it must still be the same person's legajo.
        if (legajo!.PersonLegajoId != solicitanteRow.PersonLegajoId)
            return Refuse(analysis, tenantId, userId, AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.PersonAnalysis));

        var fiadores = new List<(AnalysisParticipant Row, Fiador Fiador, List<Document> Documents)>();
        foreach (var row in fiadorRows)
        {
            var fiadorId = row.FiadorId!.Value;
            var (refusal, fiador, fiadorDocuments) = await _evaluator
                .CheckFiadorAsync(fiadorId, entity, tenantId, ct)
                .ConfigureAwait(false);
            if (refusal is not null)
                return Refuse(analysis, tenantId, userId, AnalysisRescoreOutcome.Refused(refusal));

            if (fiador!.PersonLegajoId != row.PersonLegajoId)
            {
                return Refuse(analysis, tenantId, userId, AnalysisRescoreOutcome.Refused(PersonAnalysisEvaluator.FiadorNotReady(
                    fiadorId, PersonEvaluationErrorCodes.FiadorReasons.LegajoChanged,
                    "El legajo del fiador cambió desde la evaluación original: creá un análisis nuevo para evaluarlo con su legajo actual.")));
            }

            fiadores.Add((row, fiador, fiadorDocuments!));
        }

        // ── BCRA: the solicitante's one live retry when his legajo's snapshot is unusable ─────
        var bcraRefreshed = await _evaluator
            .RefreshUnusableBcraSnapshotAsync(entity, legajo, tenantId, ct)
            .ConfigureAwait(false);

        // ── Evaluate: current legajos and pf_ parameters, the stored terms ───────────────────
        var configRows = await _personAnalysisRepository.GetEvaluationConfigAsync(tenantId, ct).ConfigureAwait(false);
        var parameters = PersonEvaluationParameters.From(configRows);
        // The tenant's CURRENT expected-documents matrix (engine 0.10), frozen anew. Never throws: a settings
        // fault reads the code defaults, so it can never fail a re-evaluation.
        var expectedDocuments = await _expectedDocuments.ForAsync(tenantId, legajo.FiscalRegime, ct).ConfigureAwait(false);
        // The stored periodicity and agro override (DDL 078). A row written before 078 has NULL CuotasPorAnio:
        // its cuota was stated as monthly, so it is re-read as 12, never as the tenant's agro default. The declared
        // cuotas vigentes (DDL 079) are reused as stored; NULL — any row written before 079 — estimates, as before.
        var terms      = new PersonOperationTerms(
            solicitanteRow.AmountAtCharge, solicitanteRow.Currency, solicitanteRow.InstallmentAmount,
            solicitanteRow.CuotasPorAnio ?? PersonAgroModeResolver.CuotasMensuales, solicitanteRow.AgroModeOverride,
            solicitanteRow.ExistingInstallmentsAmount);

        PersonSolicitanteEvaluation solicitante;
        var fiadorEvaluations = new List<(AnalysisParticipant Row, PersonFiadorEvaluation Evaluation)>();
        try
        {
            solicitante = await _evaluator
                .EvaluateSolicitanteAsync(entity, legajo, documents!, terms, parameters, expectedDocuments, tenantId, now, ct)
                .ConfigureAwait(false);

            foreach (var (row, fiador, fiadorDocuments) in fiadores)
            {
                var fiadorTerms = new PersonFiadorTerms(
                    fiador.FiadorId, row.AmountAtCharge, row.GuaranteeType ?? string.Empty,
                    row.SpouseConsent, row.HousingInopposable, row.GuaranteesGivenAmount, row.ExistingInstallmentsAmount);

                var evaluation = await _evaluator
                    .EvaluateFiadorAsync(fiadorTerms, fiador, fiadorDocuments, terms, entity.CUIT, solicitante, parameters,
                        tenantId, now, ct)
                    .ConfigureAwait(false);
                fiadorEvaluations.Add((row, evaluation));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Re-evaluation of persona analysis {AnalysisId} tenant {TenantId} failed in the evaluation. Nothing was changed",
                analysis.AnalysisId, tenantId);
            return new AnalysisRescoreOutcome
            {
                StatusCode = 500,
                ErrorCode  = RescoreErrorCodes.Failed,
                Detail     = PersonFailedDetail
            };
        }

        // ── The new rows, the summary and the history ────────────────────────────────────────
        var newSolicitante = new PersonParticipantReevaluation
        {
            ParticipantId        = solicitanteRow.ParticipantId,
            DocumentVersionsJson = solicitante.DocumentVersionsJson,
            FiltersJson          = solicitante.FiltersJson,
            IndicatorsJson       = solicitante.IndicatorsJson,
            CoverageJson         = solicitante.CoverageJson,
            EvaluationVersion    = PersonEvaluationService.Version,
            CreatedAt            = now
        };
        var newFiadores = fiadorEvaluations
            .Select(f => new PersonParticipantReevaluation
            {
                ParticipantId        = f.Row.ParticipantId,
                DocumentVersionsJson = f.Evaluation.DocumentVersionsJson,
                FiltersJson          = f.Evaluation.FiltersJson,
                IndicatorsJson       = f.Evaluation.IndicatorsJson,
                CoverageJson         = f.Evaluation.CoverageJson,
                EvaluationVersion    = PersonEvaluationService.Version,
                CreatedAt            = now
            })
            .ToList();

        var previousSuggestion = PersonEvaluationService.Suggest(PersonEvaluationJson.FiltersOf(solicitanteRow.FiltersJson));
        var newSuggestion      = solicitante.Evaluation.Suggestion;
        var sources            = Sources(entity, legajo, solicitante.Sources, bcraRefreshed);

        var previousValues = new PersonRescorePreviousValues
        {
            Result       = PersonRescorePreviousValues.ResultValues.From(result),
            Participants = new[] { solicitanteRow }.Concat(fiadorRows)
                .Select(PersonRescorePreviousValues.ParticipantValues.From)
                .ToList(),
            Suggestions  = new PersonRescorePreviousValues.SuggestionValues(previousSuggestion, newSuggestion)
        };

        var history = new AnalysisRescore
        {
            RescoreId                = Guid.NewGuid(),
            TenantId                 = tenantId,
            AnalysisId               = analysis.AnalysisId,
            ResultId                 = result.ResultId,
            RescoredAt               = now,
            RescoredBy               = userId,
            PreviousScore            = null,
            NewScore                 = null,
            PreviousLevel            = null,
            NewLevel                 = null,
            PreviousEnrichmentStatus = result.EnrichmentStatus,
            NewEnrichmentStatus      = solicitante.EnrichmentStatus,
            PreviousValuesJson       = JsonSerializer.Serialize(previousValues, HistoryJsonOpts),
            SourcesJson              = JsonSerializer.Serialize(sources, RescoreSources.JsonOpts)
        };

        var write = new PersonAnalysisRescoreWrite
        {
            ResultId             = result.ResultId,
            AnalysisId           = analysis.AnalysisId,
            TenantId             = tenantId,
            ExpectedRescoreCount = result.RescoreCount,
            RescoredAt           = now,
            RescoredBy           = userId,
            BcraDataJson         = solicitante.BcraDataJson,
            MacroDataJson        = solicitante.MacroDataJson,
            FiscalDataJson       = solicitante.FiscalDataJson,
            LufeDataJson         = solicitante.LufeDataJson,
            AiInputHash          = solicitante.AiInputHash,
            AiOutputHash         = solicitante.AiOutputHash,
            EnrichmentStatus     = solicitante.EnrichmentStatus,
            EnrichmentNotes      = solicitante.EnrichmentNotes,
            Participants         = [newSolicitante, .. newFiadores],
            History              = history
        };

        // ── Persist: one transaction, guarded by the decision gate and RescoreCount ──────────
        var applied = await _rescoreRepository.TryApplyAsync(write, ct).ConfigureAwait(false);
        switch (applied)
        {
            case AnalysisRescoreWriteOutcome.LostRace:
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.InProgress);
            case AnalysisRescoreWriteOutcome.NotPending:
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.NotPending);
            case AnalysisRescoreWriteOutcome.HasVotes:
                return AnalysisRescoreOutcome.Refused(409, RescoreErrorCodes.HasVotes);
        }

        var fiadorIds = fiadorRows.Select(f => f.FiadorId).ToList();
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Analysis.PersonReevaluated",
            severity:     "Info",
            entityType:   "Analysis",
            entityId:     analysis.AnalysisId.ToString(),
            ipAddress:    ipAddress,
            requestId:    requestId,
            oldValueJson: JsonSerializer.Serialize(new
            {
                suggestion        = previousSuggestion,
                evaluationVersion = solicitanteRow.EvaluationVersion,
                enrichmentStatus  = history.PreviousEnrichmentStatus,
                fiadorCount       = fiadorRows.Count,
                fiadorIds
            }),
            newValueJson: JsonSerializer.Serialize(new
            {
                rescoreId         = history.RescoreId,
                resultId          = result.ResultId,
                suggestion        = newSuggestion,
                evaluationVersion = PersonEvaluationService.Version,
                enrichmentStatus  = history.NewEnrichmentStatus,
                fiadorCount       = fiadorRows.Count,
                fiadorIds
            }),
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Persona analysis {AnalysisId} result {ResultId} tenant {TenantId} re-evaluated by user {UserId}: " +
            "suggestion {PreviousSuggestion} -> {NewSuggestion}, enrichment {PreviousEnrichment} -> {NewEnrichment}, " +
            "fiadores={FiadorCount}, bcraRefreshed={BcraRefreshed}",
            analysis.AnalysisId, result.ResultId, tenantId, userId, previousSuggestion, newSuggestion,
            history.PreviousEnrichmentStatus, history.NewEnrichmentStatus, fiadorRows.Count, bcraRefreshed);

        return AnalysisRescoreOutcome.Rescored(Summary(
            history, sources, solicitanteRow, newSolicitante, fiadorRows, newFiadores, configRows,
            previousSuggestion, newSuggestion));
    }

    /// <summary>Logs a refusal (ids and the code only) and returns it.</summary>
    private AnalysisRescoreOutcome Refuse(Analysis analysis, string tenantId, Guid userId, AnalysisRescoreOutcome outcome)
    {
        _logger.LogInformation(
            "Re-evaluation of persona analysis {AnalysisId} tenant {TenantId} refused for user {UserId}: {Reason}",
            analysis.AnalysisId, tenantId, userId, outcome.ErrorCode);
        return outcome;
    }

    /// <summary>
    /// The sources of the new evaluation, dates only: the legajo's BCRA snapshot (live when it was re-queried
    /// now), its ARCA snapshot, the entity's LUFE snapshot (the MiPyME certificate) and the legajo's
    /// reconciled inventory. A snapshot that is not there gets a null date.
    /// </summary>
    private static IReadOnlyList<RescoreSource> Sources(
        Entity entity, PersonLegajo legajo, PersonEvaluationSources evaluated, bool bcraRefreshed)
        =>
        [
            new RescoreSource(RescoreSource.Bcra,   legajo.BcraDataJson     is not null ? legajo.BcraDataUpdatedAt : null, bcraRefreshed),
            new RescoreSource(RescoreSource.Arca,   legajo.ArcaSnapshotJson is not null ? legajo.ArcaDataUpdatedAt : null, false),
            new RescoreSource(RescoreSource.Lufe,   entity.LufeDataJson     is not null ? entity.LufeDataUpdatedAt : null, false),
            new RescoreSource(RescoreSource.Legajo, evaluated.InventoryUpdatedAt, false),
        ];

    /// <summary>The <c>rescore</c> block: what the analyst needs to see changed, read off the rows before and after.</summary>
    private static AnalysisRescoreSummary Summary(
        AnalysisRescore history,
        IReadOnlyList<RescoreSource> sources,
        AnalysisParticipant solicitanteBefore,
        PersonParticipantReevaluation solicitanteAfter,
        IReadOnlyList<AnalysisParticipant> fiadoresBefore,
        IReadOnlyList<PersonParticipantReevaluation> fiadoresAfter,
        IReadOnlyDictionary<string, decimal> configRows,
        string previousSuggestion,
        string newSuggestion)
    {
        var previousIndicators = PersonEvaluationJson.TryDeserialize<List<PersonIndicatorResult>>(solicitanteBefore.IndicatorsJson) ?? [];
        var newIndicators      = PersonEvaluationJson.TryDeserialize<List<PersonIndicatorResult>>(solicitanteAfter.IndicatorsJson) ?? [];

        PersonRescoreCoverageChange? coverage = null;
        if (fiadoresBefore.Count > 0)
        {
            var solicitanteNow = Apply(solicitanteBefore, solicitanteAfter);
            var after = fiadoresBefore
                .Select(row => Apply(row, fiadoresAfter.Single(f => f.ParticipantId == row.ParticipantId)))
                .ToList();

            // Both sides against the tenant's CURRENT minimum, as the decision applies it.
            var previous = PersonEvaluationJson.DecisionRules(configRows, solicitanteBefore, fiadoresBefore).CounterGuaranteeCoverage;
            var current  = PersonEvaluationJson.DecisionRules(configRows, solicitanteNow, PersonEvaluationJson.FiadorRowsOf(after)).CounterGuaranteeCoverage;
            coverage = new PersonRescoreCoverageChange(
                previous?.Ratio, previous?.MeetsMin ?? false, current?.Ratio, current?.MeetsMin ?? false);
        }

        return new AnalysisRescoreSummary(
            history.PreviousScore,
            history.NewScore,
            history.PreviousLevel,
            history.NewLevel,
            history.PreviousEnrichmentStatus,
            history.NewEnrichmentStatus,
            history.RescoredAt,
            sources)
        {
            PreviousSuggestion        = previousSuggestion,
            NewSuggestion             = newSuggestion,
            FilterCounts              = new PersonRescoreFilterCounts(
                PersonFilterCounts.Of(PersonEvaluationJson.FiltersOf(solicitanteBefore.FiltersJson)),
                PersonFilterCounts.Of(PersonEvaluationJson.FiltersOf(solicitanteAfter.FiltersJson))),
            Indicators                = SummaryIndicatorIds
                .Select(id =>
                {
                    var before = previousIndicators.FirstOrDefault(i => i.Id == id);
                    var after  = newIndicators.FirstOrDefault(i => i.Id == id);
                    return new PersonRescoreIndicatorChange(
                        id, before?.Status, before?.Value, before?.Band, after?.Status, after?.Value, after?.Band);
                })
                .ToList(),
            CounterGuaranteeCoverage  = coverage,
            PreviousEvaluationVersion = solicitanteBefore.EvaluationVersion,
            NewEvaluationVersion      = solicitanteAfter.EvaluationVersion,
            FiadoresCount             = fiadoresBefore.Count
        };
    }

    /// <summary>A copy of <paramref name="row"/> carrying the new evaluation columns, for the pure readers.</summary>
    private static AnalysisParticipant Apply(AnalysisParticipant row, PersonParticipantReevaluation evaluation)
        => new()
        {
            ParticipantId              = row.ParticipantId,
            TenantId                   = row.TenantId,
            AnalysisId                 = row.AnalysisId,
            PersonLegajoId             = row.PersonLegajoId,
            Role                       = row.Role,
            AmountAtCharge             = row.AmountAtCharge,
            Currency                   = row.Currency,
            InstallmentAmount          = row.InstallmentAmount,
            DocumentVersionsJson       = evaluation.DocumentVersionsJson,
            FiltersJson                = evaluation.FiltersJson,
            IndicatorsJson             = evaluation.IndicatorsJson,
            CoverageJson               = evaluation.CoverageJson,
            ScoreJson                  = row.ScoreJson,
            EvaluationVersion          = evaluation.EvaluationVersion,
            CreatedAt                  = evaluation.CreatedAt,
            FiadorId                   = row.FiadorId,
            GuaranteeType              = row.GuaranteeType,
            SpouseConsent              = row.SpouseConsent,
            HousingInopposable         = row.HousingInopposable,
            GuaranteesGivenAmount      = row.GuaranteesGivenAmount,
            CuotasPorAnio              = row.CuotasPorAnio,
            AgroModeOverride           = row.AgroModeOverride,
            ExistingInstallmentsAmount = row.ExistingInstallmentsAmount
        };
}
