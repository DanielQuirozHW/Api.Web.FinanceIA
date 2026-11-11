// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Clases;

// GET /api/dashboard/person-calibration/export: the population as one CSV row per analysis. Labels are the
// Portal's es-AR copy (PersonLabels) — the only place in this feature where codes become Spanish.
public sealed partial class PersonCalibrationService
{
    /// <summary>Shown in the Sugerencia column of an analysis the board counts as unreadable.</summary>
    internal const string UnreadableSuggestionLabel = "No legible";

    /// <summary>The CSV header row, in column order.</summary>
    internal static readonly IReadOnlyList<string> CsvHeaders =
    [
        "analysisId",
        "resultId",
        "Fecha de alta",
        "Analista",
        "Equipo",
        "Entidad",
        "Estado",
        "Sugerencia",
        "Decisión",
        "Decidido por",
        "Fecha de decisión",
        "Motivo",
        "Filtros en Rechazo",
        "Filtros sin datos",
        "Indicadores en Rojo",
        "Cobertura de contragarantía (%)",
        "Mínimo congelado (%)",
        "Documentos faltantes",
        "Documentos vencidos",
        "Cantidad de reanálisis",
        "Versión del motor",
        "Aprobado con rechazo sugerido",
        "Aprobado con banda roja",
        "Rechazado en verde",
        "Contragarantía bajo el mínimo"
    ];

    private const string ListSeparator = ", ";

    /// <inheritdoc />
    public async Task<PersonCalibrationExportFile> ExportAsync(
        string tenantId, PersonCalibrationPeriod period, Guid? teamId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var population = await LoadPopulationAsync(tenantId, period, teamId, ct).ConfigureAwait(false);
        var analyses   = population.Analyses;

        var rescores = await _repository
            .GetRescoresAsync(tenantId, analyses.Select(a => a.AnalysisId).ToList(), ct)
            .ConfigureAwait(false);
        var rescoreCounts = rescores
            .GroupBy(r => r.AnalysisId)
            .ToDictionary(g => g.Key, g => g.Count());

        var entities = await EntityNamesAsync(tenantId, analyses.Select(a => a.EntityId), ct).ConfigureAwait(false);
        var users    = await UsersAsync(tenantId,
            analyses.SelectMany(a => new[] { (Guid?)a.RequestedBy, DeciderOf(a).By }), ct).ConfigureAwait(false);
        var teams    = analyses.Count == 0
            ? new Dictionary<Guid, string>()
            : await TeamNamesAsync(tenantId, ct).ConfigureAwait(false);

        var rows = analyses
            .Select(a =>
            {
                population.ByAnalysis.TryGetValue(a.AnalysisId, out var c);
                entities.TryGetValue(a.EntityId, out var entity);
                var (decidedBy, decidedAt) = DeciderOf(a);
                var (_, teamName)          = TeamOf(a.RequestedBy, users, teams);

                return (IReadOnlyList<string?>)
                [
                    a.AnalysisId.ToString(),
                    a.ResultId?.ToString(),
                    PersonCalibrationCsv.Date(a.CreatedAt),
                    ActorName(a.RequestedBy, users),
                    teamName,
                    entity?.BusinessName,
                    StatusLabel(a.Status),
                    c is null ? UnreadableSuggestionLabel : PersonLabels.Suggestion(c.Suggestion),
                    DecisionLabel(a),
                    ActorName(decidedBy, users),
                    PersonCalibrationCsv.Date(decidedAt),
                    ReasonOf(a),
                    c is null ? null : Join(c.RechazoFilterIds.Select(PersonLabels.Filter)),
                    c is null ? null : Join(c.Filters
                        .Where(f => f.Outcome == PersonFilterOutcomes.SinDatos)
                        .Select(f => f.Id)
                        .Distinct(StringComparer.Ordinal)
                        .Select(PersonLabels.Filter)),
                    c is null ? null : Join(c.RojoIndicatorIds.Select(PersonLabels.Indicator)),
                    PersonCalibrationCsv.Decimal(c?.CounterGuarantee?.Ratio is { } ratio ? ratio * 100m : null),
                    c?.CounterGuarantee is null ? null : PersonCalibrationCsv.Integer(c.FrozenMinPct),
                    c is null ? null : Join(DocumentTypes(c, IsMissing)),
                    c is null ? null : Join(DocumentTypes(c, IsStale)),
                    PersonCalibrationCsv.Integer(rescoreCounts.GetValueOrDefault(a.AnalysisId)),
                    c?.Solicitante.EvaluationVersion,
                    PersonCalibrationCsv.Flag(c?.Flags.ApprovedWithRechazoSugerido),
                    PersonCalibrationCsv.Flag(c?.Flags.ApprovedWithRedBand),
                    PersonCalibrationCsv.Flag(c?.Flags.RejectedInGreen),
                    PersonCalibrationCsv.Flag(c?.Flags.CounterGuaranteeBelowMin)
                ];
            })
            .ToList();

        var fileName = $"calibracion_persona_{period.From:yyyy-MM-dd}_{period.To:yyyy-MM-dd}.csv";

        _logger.LogInformation(
            "Person calibration CSV for tenant {TenantId} from {From} to {To} team {TeamId}: rows={Rows} unreadable={Unreadable}",
            tenantId, period.From, period.To, teamId, rows.Count, population.Unreadable.Count);

        return new PersonCalibrationExportFile
        {
            Content     = PersonCalibrationCsv.Write(CsvHeaders, rows),
            FileName    = fileName,
            AnalysisIds = analyses.Select(a => a.AnalysisId).ToList()
        };
    }

    private static string? Join(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return list.Count == 0 ? null : string.Join(ListSeparator, list);
    }

    private static IEnumerable<string> DocumentTypes(CalibrationCase c, Func<PersonCoverageItem, bool> predicate)
        => (c.Coverage.Items ?? [])
            .Where(i => i is not null && predicate(i))
            .Select(i => i.DocumentType)
            .Distinct(StringComparer.Ordinal)
            .Select(PersonLabels.DocumentType);

    /// <summary>The stored status in es-AR — every value CK_Analyses_Status admits, the raw code otherwise.</summary>
    private static string StatusLabel(string status) => status switch
    {
        AnalysisStatuses.PendingReview => "Pendiente de revisión",
        AnalysisStatuses.PreApproved   => "Pre-aprobado",
        AnalysisStatuses.Approved      => "Aprobado",
        AnalysisStatuses.Rejected      => "Rechazado",
        "Queued"                       => "En cola",
        "Processing"                   => "En proceso",
        "Reviewed"                     => "Revisado",
        _                              => status
    };

    /// <summary>
    /// The act the status records: the Portal's decision label (<see cref="PersonLabels.AnalysisStatus"/>) for a
    /// decided or pre-approved analysis, "Devuelto" for one a confirmer returned, "Sin decisión" otherwise.
    /// </summary>
    private static string DecisionLabel(PersonCalibrationAnalysisRow row) => row.Status switch
    {
        AnalysisStatuses.Approved or AnalysisStatuses.Rejected or AnalysisStatuses.PreApproved
            => PersonLabels.AnalysisStatus(row.Status),
        AnalysisStatuses.PendingReview when row.ReviewedBy is not null => "Devuelto",
        _ => "Sin decisión"
    };
}
