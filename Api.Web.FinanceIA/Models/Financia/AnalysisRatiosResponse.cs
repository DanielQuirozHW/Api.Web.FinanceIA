// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The computed ratios of one analysis and their score breakdown, as returned by
/// GET /api/analyses/{id}/ratios — the "Ratios calculados por FinancIA" panel on the PyME detail.
///
/// <para>
/// Property names and types match <see cref="AnalysisResultResponse.RatiosJson"/> and
/// <see cref="AnalysisResultResponse.ScoreBreakdown"/> EXACTLY, so the JSON keys are
/// <c>ratiosJson</c> and <c>scoreBreakdown</c> and the Portal swaps the endpoint URL and changes
/// nothing about its parsing. That is the whole point of this DTO; it is not a place to improve the
/// shape.
/// </para>
///
/// <para>
/// <see cref="ScoreBreakdown"/> is <c>object?</c> and NOT a typed model on purpose. The detail
/// populates it with <c>JsonSerializer.Deserialize&lt;object&gt;</c>, which yields a
/// <c>JsonElement</c>, and a <c>JsonElement</c> re-serialises VERBATIM — the stored blob's own keys
/// (<c>breakdown</c>, <c>scoreRatios</c>, <c>criticalRatios</c>, <c>sectorProfile</c>, …) reach the
/// client untouched by the camelCase naming policy. Typing it would re-case those keys and silently
/// drop every key the type does not declare, which is exactly what the Portal's
/// <c>scoreBreakdown?.breakdown</c> lookup would notice. The blob is a free-form contract with the
/// scoring engine; keep it opaque here.
/// </para>
///
/// <para>
/// <see cref="RatiosJson"/> is likewise the raw stored string, echoed and never parsed on this side.
/// </para>
///
/// <para>
/// Either field is null when the analysis has no such blob, or when the stored blob could not be
/// parsed — deliberately NOT distinguishable from each other, exactly as on the detail response
/// today. Nulls are serialised (<c>DefaultIgnoreCondition</c> is not configured), so an unscored
/// analysis is a 200 emitting <c>"scoreBreakdown": null</c> rather than a 404.
/// </para>
/// </summary>
public sealed class AnalysisRatiosResponse
{
    /// <summary>
    /// JSON object of computed financial ratios, as stored. Echoed verbatim — the client parses it.
    /// </summary>
    public string? RatiosJson { get; set; }

    /// <summary>
    /// Per-component and per-ratio score breakdown for UI display.
    /// Contains keys: scoreRatios, scoreBcra, scoreMacro, breakdown (per-ratio scores 0–100),
    /// scoreBeforeFloor, appliedFloor, criticalRatios, bcraTrendDelta, bcraTrendAdj, chequesAdj,
    /// chequesProporcionRecuperada (both since scoring algorithm version 5), ventasAdj,
    /// ventasVariacion, ventasBase (all three since scoring algorithm version 6),
    /// ventasVariacionEsReal (since version 7 — absent means ventasVariacion is nominal),
    /// casfogMoraAdj, casfogProporcionMora (both since scoring algorithm version 8),
    /// criticalFloorTrigger, criticalFloors, thresholdFallbacks (all three since version 9, which also
    /// adds the ventasBase value "interanual_3m"),
    /// profitabilityFloorWaived, profitabilityFloorWaivedRatios, operatingResultSign (all three since
    /// version 10, which also adds the per-ratio exclusionReason value "no_current_liabilities" on a
    /// NON-excluded liquidez corriente / prueba ácida entry: scored at the best band, value null),
    /// acidTestFloorWaived, currentRatioVsWarn (both since version 11),
    /// sectorProfile.
    /// Null for pre-scoring analyses. See the type summary for why this stays <c>object?</c>.
    /// </summary>
    public object? ScoreBreakdown { get; set; }
}
