// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The persona humana side of GET /api/analyses/{id}/previous-comparison: the solicitante's frozen evaluation
/// and terms (one [dbo].[AnalysisParticipants] row, projected) and the fiadores of the analysis. Raw columns —
/// the JSON is read by the builder with <c>PersonEvaluationJson</c>, the one reader of these columns. Carries
/// no name and no CUIT: the participant rows hold none.
/// </summary>
public sealed class PersonComparisonFacts
{
    /// <summary>JSON array of the solicitante's pre-filters.</summary>
    public string? FiltersJson { get; set; }

    /// <summary>JSON array of the solicitante's indicators.</summary>
    public string? IndicatorsJson { get; set; }

    /// <summary>JSON object: the documentary coverage index.</summary>
    public string? CoverageJson { get; set; }

    /// <summary>JSON object: the sources, including the frozen pf_ parameters and the agro mode.</summary>
    public string? DocumentVersionsJson { get; set; }

    /// <summary>The evaluation engine version that produced the row.</summary>
    public string EvaluationVersion { get; set; } = string.Empty;

    /// <summary>The requested amount, in <see cref="Currency"/>.</summary>
    public decimal AmountAtCharge { get; set; }

    /// <summary>ISO 4217 code of the amounts.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The cuota of the operation, when stated.</summary>
    public decimal? InstallmentAmount { get; set; }

    /// <summary>Cuotas a year as resolved at creation; NULL on rows before DDL 078 (a monthly cuota).</summary>
    public byte? CuotasPorAnio { get; set; }

    /// <summary>The fiadores of the analysis (their [dbo].[Fiadores] ids), in no particular order.</summary>
    public IReadOnlyList<Guid> FiadorIds { get; set; } = [];
}
