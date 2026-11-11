// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The executable patrimony of one fiador and its asset breakdown (M10 Phase 5a-2), frozen as
/// AnalysisParticipants.CoverageJson of the Fiador row. The aggregate counter-guarantee coverage is
/// recomputed from these rows on every read, never stored.
/// </summary>
public sealed record FiadorCoverageResult
{
    /// <summary>The operation's ISO currency.</summary>
    public required string OperationCurrency { get; init; }

    /// <summary>
    /// False when the operation is not in pesos: the legajo's values are pesos and nothing converts currency,
    /// so no coverage ratio is computed.
    /// </summary>
    public required bool Comparable { get; init; }

    /// <summary>The amount at the fiador's charge.</summary>
    public required decimal AmountAtCharge { get; init; }

    /// <summary>Every asset read, counted or not.</summary>
    public required IReadOnlyList<FiadorAssetResult> Assets { get; init; }

    /// <summary>Σ executable value of the counted assets.</summary>
    public required decimal GrossExecutable { get; init; }

    /// <summary>The liabilities subtracted.</summary>
    public required decimal Liabilities { get; init; }

    /// <summary>Where <see cref="Liabilities"/> came from: <c>manifestacion</c>, <c>bcra</c> or <c>ninguno</c>.</summary>
    public required string LiabilitiesSource { get; init; }

    /// <summary>The Pasivo items of the legajo.</summary>
    public IReadOnlyList<Guid> LiabilityItemIds { get; init; } = [];

    /// <summary>Guarantees the fiador already gave, as declared by the analyst (0 when not stated).</summary>
    public required decimal GuaranteesGivenAmount { get; init; }

    /// <summary><see cref="GrossExecutable"/> − <see cref="Liabilities"/> − <see cref="GuaranteesGivenAmount"/>; may be negative.</summary>
    public required decimal ExecutablePatrimony { get; init; }

    /// <summary>pf_cobertura_rojo as the fiador's own cobertura_patrimonial_fiador indicator applied it (the aggregate band does not use it).</summary>
    public required decimal CoberturaRojo { get; init; }

    /// <summary>pf_cobertura_verde as the fiador's own indicator applied it.</summary>
    public required decimal CoberturaVerde { get; init; }
}
