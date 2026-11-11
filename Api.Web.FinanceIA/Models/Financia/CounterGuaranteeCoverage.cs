// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The aggregate counter-guarantee coverage of a persona humana analysis (M10 Phase 5a-2): the executable
/// patrimony of its fiadores over the requested amount. RECOMPUTED ON READ from the frozen Fiador rows by
/// <c>PersonEvaluationJson</c> — the one reader of GET /api/analyses/{id}, PATCH .../decision and the
/// persona PDF — so the three cannot disagree. The asset values are the frozen ones; the minimum is the
/// tenant's CURRENT pf_contragarantia_cobertura_min_pct, the same rule the decision applies, and the band
/// follows it.
/// </summary>
public sealed record CounterGuaranteeCoverage
{
    /// <summary>
    /// Σ contributions of the counted fiadores: each one's executable patrimony after de-duplicating assets
    /// shared with another fiador, floored at 0 and capped at his amount at charge. Fiadores with a suggested
    /// rejection are left out.
    /// </summary>
    public required decimal ExecutableTotal { get; init; }

    /// <summary>The same total counting the fiadores with a suggested rejection too.</summary>
    public required decimal ExecutableTotalIncludingFlagged { get; init; }

    /// <summary>How many fiadores have a suggested rejection (left out of <see cref="ExecutableTotal"/>).</summary>
    public required int FlaggedFiadoresCount { get; init; }

    /// <summary>The requested amount (the solicitante's amount at charge).</summary>
    public required decimal RequestedAmount { get; init; }

    /// <summary>The operation's ISO currency.</summary>
    public required string Currency { get; init; }

    /// <summary><see cref="ExecutableTotal"/> / <see cref="RequestedAmount"/>; null when the operation is not in pesos.</summary>
    public decimal? Ratio { get; init; }

    /// <summary>pf_contragarantia_cobertura_min_pct, current (whole percent, 0-100).</summary>
    public required int MinPct { get; init; }

    /// <summary>Ratio × 100 ≥ <see cref="MinPct"/>. False when no ratio could be computed and a minimum is required.</summary>
    public required bool MeetsMin { get; init; }

    /// <summary>
    /// <see cref="PersonIndicatorBands.Verde"/> when <see cref="MeetsMin"/>, <see cref="PersonIndicatorBands.Rojo"/>
    /// otherwise; null without a ratio. NOT the per-fiador pf_cobertura_* cuts: each contribution is capped at the
    /// fiador's amount at charge, so on those cuts one fiador covering the whole aval would read Amarillo.
    /// </summary>
    public string? Band { get; init; }

    /// <summary>How many fiadores the analysis has.</summary>
    public required int FiadoresCount { get; init; }

    /// <summary>One entry per fiador, in the participants' order.</summary>
    public required IReadOnlyList<CounterGuaranteeContribution> Contributions { get; init; }
}
