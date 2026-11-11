// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The evaluation of one fiador of a persona humana analysis (M10 Phase 5a-2), on
/// <see cref="PersonEvaluationResponse.Fiadores"/>. Read from the frozen AnalysisParticipants row, never
/// recomputed; only <see cref="Name"/> is read live from the Fiadores row.
/// </summary>
public sealed record PersonFiadorEvaluationResponse
{
    /// <summary>The participant row.</summary>
    public required Guid ParticipantId { get; init; }

    /// <summary>The Fiadores row.</summary>
    public required Guid FiadorId { get; init; }

    /// <summary>Fiadores.Denominacion; null when the row has none.</summary>
    public string? Name { get; init; }

    /// <summary>Fiadores.CUIT, read live like <see cref="Name"/>; null when the row was not found.</summary>
    public string? Cuit { get; init; }

    /// <summary>The fiador's legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>One of <see cref="GuaranteeTypes.Known"/>.</summary>
    public string? GuaranteeType { get; init; }

    /// <summary>The amount at the fiador's charge.</summary>
    public required decimal AmountAtCharge { get; init; }

    /// <summary>ISO code of the amounts.</summary>
    public required string Currency { get; init; }

    /// <summary>The full cuota of the operation, the stress cuota/ingreso's numerator.</summary>
    public decimal? InstallmentAmount { get; init; }

    /// <summary>The spouse consented to execute the housing.</summary>
    public bool? SpouseConsent { get; init; }

    /// <summary>The housing is not protected (inopponibility).</summary>
    public bool? HousingInopposable { get; init; }

    /// <summary>Guarantees already given, declared by the analyst.</summary>
    public decimal? GuaranteesGivenAmount { get; init; }

    /// <summary>
    /// The fiador's monthly cuotas vigentes as declared by the analyst (engine 0.8); null when not declared (they
    /// were estimated from his Central de Deudores) and on every evaluation frozen before 0.8.
    /// </summary>
    public decimal? CuotasVigentesDeclaradas { get; init; }

    /// <summary>The engine version that produced the row.</summary>
    public required string EvaluationVersion { get; init; }

    /// <summary>One of <see cref="PersonSuggestions"/>, derived from <see cref="Filters"/>.</summary>
    public required string Suggestion { get; init; }

    /// <summary>The pre-filters.</summary>
    public required IReadOnlyList<PersonFilterResult> Filters { get; init; }

    /// <summary>The indicators.</summary>
    public required IReadOnlyList<PersonIndicatorResult> Indicators { get; init; }

    /// <summary>The executable patrimony and the asset breakdown; null only if the stored JSON could not be read.</summary>
    public FiadorCoverageResult? Coverage { get; init; }

    /// <summary>What the evaluation read; null only if the stored JSON could not be read.</summary>
    public PersonEvaluationSources? Sources { get; init; }

    /// <summary>When the evaluation was made.</summary>
    public required DateTime CreatedAt { get; init; }
}
