// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Per-ratio score with its computed status label.
/// <para>
/// <see cref="Excluded"/> marks an indicator that could not be computed (its ratio value was null):
/// it is excluded from the ratios composite with weight 0 and renormalised away, yet still LISTED
/// here — the per-ratio analogue of <see cref="ScoringResult.BcraExcluded"/>. This is what makes a
/// not-measured indicator distinguishable from a genuine score of 50.
/// </para>
/// <para>
/// <see cref="EffectiveWeight"/> is the renormalised weight actually applied to this ratio inside the
/// composite (0 when excluded; <c>wᵢ / survivingWeightSum</c> for survivors, so survivors sum to 1);
/// <c>null</c> on analyses persisted before this field existed. The per-ratio analogue of
/// <see cref="ScoringResult.EffectiveWeightRatios"/>. Both new fields default so old persisted JSON
/// deserialises to <c>Excluded = false</c> / <c>EffectiveWeight = null</c>.
/// </para>
/// <para>
/// <see cref="ExclusionReason"/> says WHY an excluded ratio could not be computed:
/// <see cref="RatioExclusionReasons.Unread"/> (an operand was not extracted) or
/// <see cref="RatioExclusionReasons.NotMeaningful"/> (operands present, formula meaningless: equity
/// &lt;= 0, a zero denominator). Null when not excluded, or when the reason is unknown (ratios taken
/// from the model's own output); omitted from the JSON when null, so a non-excluded entry
/// serialises exactly as before and an old blob deserialises to null. Reporting only.
/// </para>
/// <para>
/// Since scoring algorithm 10 it can also carry a MARKER on a NON-excluded entry:
/// <see cref="RatioExclusionReasons.NoCurrentLiabilities"/> — liquidez corriente or prueba ácida with
/// current liabilities read as zero, scored at the best band. Read <see cref="Excluded"/> first: only an
/// excluded entry's reason is a reason the ratio was dropped.
/// </para>
/// <para>
/// <see cref="Measured"/> is <c>false</c> on exactly one kind of entry: a ratio that could NOT be computed
/// while the engine took the low-coverage fallback (<see cref="ScoringResult.RatiosLowCoverageFallback"/>),
/// where it is NOT excluded but scored the neutral 50 at full weight. Without it such an entry reads like
/// a measured "Atencion". Null — and omitted from the JSON — everywhere else, so the all-computable and
/// renormalise paths serialise byte-identical to before and an old blob deserialises to null. Reporting
/// only (score credibility 1b-2a, S1): it moves no score.
/// </para>
/// </summary>
public sealed record RatioBreakdownEntry(
    [property: JsonPropertyName("score")]           decimal  Score,
    [property: JsonPropertyName("status")]          string   Status,
    [property: JsonPropertyName("excluded")]        bool     Excluded        = false,
    [property: JsonPropertyName("effectiveWeight")] decimal? EffectiveWeight = null,
    [property: JsonPropertyName("exclusionReason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                    string?  ExclusionReason = null,
    [property: JsonPropertyName("measured")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                                    bool?    Measured        = null);

/// <summary>
/// The closed vocabulary for <see cref="RatioBreakdownEntry.ExclusionReason"/>. Persisted inside
/// <c>ScoreBreakdownJson</c> and read by the Portal: never rename a value.
/// </summary>
public static class RatioExclusionReasons
{
    /// <summary>An operand of the ratio was not extracted (null).</summary>
    public const string Unread = "unread";

    /// <summary>Every operand was read, but the formula has no meaning (equity &lt;= 0, a zero denominator, negative inventory).</summary>
    public const string NotMeaningful = "not_meaningful";

    /// <summary>
    /// NOT an exclusion — a marker (scoring algorithm 10). Liquidez corriente or prueba ácida whose
    /// current liabilities were read as ZERO against a positive numerator: the ratio value stays null
    /// (no infinite number anywhere), but the engine scores it at its best band, keeps its weight in the
    /// composite, never counts it as non-computable and never lets it be critical. The breakdown entry
    /// carries <c>excluded:false</c> with this reason; readers render it as
    /// <see cref="ScoringNoticeTexts.NoCurrentLiabilities"/>.
    /// </summary>
    public const string NoCurrentLiabilities = "no_current_liabilities";
}

/// <summary>
/// The closed vocabulary for <see cref="ScoringResult.BcraExclusionReason"/>.
/// <para>
/// Deliberately <c>const string</c> rather than an enum: the values are persisted inside the
/// free-form <c>ScoreBreakdownJson</c> blob and surfaced to the Portal, so an unrecognised future
/// literal must deserialise rather than throw. Never rename an existing value — old rows carry it.
/// </para>
/// </summary>
public static class BcraExclusionReasons
{
    /// <summary>No BCRA payload reached the engine at all (<c>bcraData</c> was null).</summary>
    public const string NoBcraData = "NoBcraData";

    /// <summary>A payload arrived but carried no Central de Deudores block (<c>bcraData.Deuda</c> was null).</summary>
    public const string NoDeudaBlock = "NoDeudaBlock";

    /// <summary>The Central de Deudores query itself failed (<see cref="BcraDeudaConsultaEstado.LookupFailed"/>).</summary>
    public const string LookupFailed = "LookupFailed";
}

/// <summary>
/// Output produced by <see cref="Api.Web.FinanceIA.Servicios.IScoringService.CalculateAsync"/>.
/// All scores are on a 0–100 scale where 0 = no risk and 100 = maximum risk.
/// </summary>
public sealed class ScoringResult
{
    /// <summary>Weighted composite score (0–100).</summary>
    public decimal Score { get; set; }

    /// <summary>Risk level label: Low / Medium / High / Critical.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Score contribution from financial ratios (0–100).</summary>
    public decimal ScoreRatios { get; set; }

    /// <summary>Score contribution from BCRA Central de Deudores data (0–100).</summary>
    public decimal ScoreBcra { get; set; }

    /// <summary>Score contribution from macroeconomic context (0–100).</summary>
    public decimal ScoreMacro { get; set; }

    /// <summary>
    /// Per-ratio partial scores with status labels for auditability.
    /// Keys: liquidez_corriente, prueba_acida, endeudamiento, leverage,
    ///       cobertura_intereses, roe, roa, margen_neto.
    /// Values: <see cref="RatioBreakdownEntry"/> with score (0–100) and status (Saludable/Atencion/Critico).
    /// </summary>
    public Dictionary<string, RatioBreakdownEntry> Breakdown { get; set; } = new();

    /// <summary>
    /// Ratio names whose partial score reached (&gt;=) the critical-floor trigger threshold — by default
    /// 100 since algorithm version 9, i.e. the ratio is at or beyond its DANGER threshold — and which
    /// therefore floor the score. Empty when no ratio is in critical territory.
    /// <para>
    /// Since algorithm version 10 a profitability ratio (ROE, ROA, margen neto) whose floor was WAIVED
    /// because the operating result is positive is NOT listed here, although its breakdown status still
    /// reads "Critico": it is at danger, it just does not floor the score. It is listed in
    /// <see cref="ProfitabilityFloorWaivedRatios"/> instead. Since version 11 the same holds for a critical
    /// prueba ácida whose floor was waived because liquidez corriente is at or above its warn threshold, see
    /// <see cref="AcidTestFloorWaived"/>. A ratio scored at the best band for having no current liabilities
    /// is never listed.
    /// </para>
    /// </summary>
    public List<string> CriticalRatios { get; set; } = new();

    /// <summary>
    /// The effective floor COMPUTED from the triggered critical ratios (the max of their per-ratio
    /// floors) — whether or not it actually moved the score. Zero when no ratio was critical.
    /// <para>
    /// It is NOT "the floor that was applied", despite the name: the engine only raises the score
    /// when <see cref="ScoreBeforeFloor"/> is below this value, so a composite that already exceeds
    /// its floor leaves the score untouched and still reports the floor here. To know whether the
    /// floor actually bound, test <c>ScoreBeforeFloor &lt; AppliedFloor</c> — that is the engine's
    /// own guard reproduced exactly (it compares the same 2dp-rounded composite against the same
    /// unrounded floor), and today's writer always emits both fields together, so it is recoverable
    /// from the persisted breakdown. Consumers that branch on <c>AppliedFloor &gt; 0</c> alone will
    /// claim a score reduction that never happened; consumers reading a blob whose
    /// <c>scoreBeforeFloor</c> is absent cannot tell either way and should fall back to the older,
    /// more permissive test rather than silently dropping the notice.
    /// </para>
    /// </summary>
    public decimal AppliedFloor { get; set; }

    /// <summary>
    /// The composite score before the critical-ratio floor was applied.
    /// Equals <see cref="Score"/> when no floor was triggered.
    /// </summary>
    public decimal ScoreBeforeFloor { get; set; }

    /// <summary>
    /// The <c>critical_floor_trigger_threshold</c> this result was scored with: the partial score at or
    /// above which a ratio became critical. 100 means "at or beyond its DANGER threshold". Reporting only
    /// (algorithm version 9) — it lets the chat explain a floor with the value that produced it rather
    /// than today's configuration, which may have changed since. Absent from breakdowns persisted before
    /// version 9.
    /// <para>
    /// <c>null</c> when the engine never ran — the neutral result <c>CalculateAsync</c> returns when
    /// scoring throws. Nullable on purpose: as a plain <c>decimal</c> that result carried 0, and a trigger
    /// of 0 reads as "every ratio is critical" to any consumer of the persisted breakdown.
    /// </para>
    /// </summary>
    public decimal? CriticalFloorTrigger { get; set; }

    /// <summary>
    /// Ratio key → the floor that ratio imposes on the final score (0-100 risk scale) when it is
    /// critical, for all eight ratios, clamped into 0-100 exactly as the engine clamps the floor it
    /// applies. Reporting only (algorithm version 9), for the same reason as
    /// <see cref="CriticalFloorTrigger"/>; absent from breakdowns persisted before it, and EMPTY on the
    /// neutral result, where <see cref="CriticalFloorTrigger"/> is null — nothing was read.
    /// </summary>
    public Dictionary<string, decimal> CriticalFloors { get; set; } = new();

    /// <summary>
    /// Ratio keys whose merged ok / warn / danger thresholds (base rows with the entity's sector rows over
    /// them) were not strictly ordered, and which were therefore scored on that ratio's complete DEFAULT
    /// triplet instead (algorithm version 9). Empty when every stored triplet was coherent — always, on a
    /// config the Settings validation accepted. Absent from breakdowns persisted before version 9.
    /// </summary>
    public List<string> ThresholdFallbacks { get; set; } = new();

    /// <summary>Situación steps moved: negative = improvement, positive = worsening, 0 = no change or no data.</summary>
    public int BcraTrendDelta { get; set; }

    /// <summary>
    /// Points ACTUALLY applied to scoreBcra by the trend adjustment: negative reduces, positive
    /// increases, 0 = none. This is the realised movement, after the 0–100 clamp — it can be smaller
    /// in magnitude than the adjustment the per-step configuration proposed, and is 0 when the clamp
    /// absorbed all of it (e.g. an improvement on a situación already scoring 0).
    /// <see cref="BcraTrendDelta"/> still reports the full movement in situación steps.
    /// </summary>
    public decimal BcraTrendAdj { get; set; }

    /// <summary>
    /// Points ACTUALLY added to scoreBcra by the rejected-cheques penalty (§7.1, algorithm version 5):
    /// 0 or positive, never negative — a clean cheques record is never a bonus. Applied AFTER
    /// <see cref="BcraTrendAdj"/> on the post-trend scoreBcra and, like it, reported as the realised
    /// movement after the 0–100 clamp, so <c>scoreBcra == base + BcraTrendAdj + ChequesAdj</c> holds
    /// exactly. 0 whenever the penalty did not apply: BCRA excluded, the tenant switched the signal off
    /// (<c>bcra_cheques_enabled</c>), no cheques block, a failed cheques lookup, no cheques reported, no
    /// rejected amount to penalise, or full recovery of the rejected amount.
    /// Absent (deserialises to 0) on every analysis persisted under an earlier algorithm version.
    /// </summary>
    public decimal ChequesAdj { get; set; }

    /// <summary>
    /// The input the cheques penalty used: the share of the rejected amount that was recovered,
    /// <c>(MontoTotalRechazado − MontoTotalImpago) / MontoTotalRechazado</c>, clamped into [0,1]
    /// (1 = everything recovered → no penalty; 0 = nothing recovered → the full <c>plena</c>).
    /// <c>null</c> when the penalty was not evaluated at all — every case listed on
    /// <see cref="ChequesAdj"/> except full recovery, which is a real reading of 1. The amounts
    /// themselves are not repeated here: they are persisted beside this result in
    /// <c>AnalysisResults.BcraDataJson</c> under <c>cheques</c>.
    /// </summary>
    public decimal? ChequesProporcionRecuperada { get; set; }

    /// <summary>
    /// Whether the cheques penalty took part in this score at all: the BCRA component was not excluded
    /// and the tenant's effective <c>bcra_cheques_enabled</c> (sector overlay included) was on — whatever
    /// the cheques block then said. Read only by the enrichment assessment after scoring, so a failed
    /// cheques lookup is flagged exactly when it could have moved the score. Not part of the breakdown
    /// JSON nor of any serialized payload, hence <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ChequesEvaluated { get; set; }

    /// <summary>
    /// Points ACTUALLY added to the FINAL score by the LUFE sales-drop penalty (§7.3a, algorithm
    /// version 6): 0 or positive, never negative — growth in sales is never a bonus, only a drop
    /// moves the score. Applied AFTER the critical floor, on the score the floor left, and reported
    /// as the realised movement after the 0–100 clamp, so a score already at 100 absorbs the penalty
    /// and reports what it really took. It is an adjustment on the composite, NOT a component: the
    /// effective component weights are identical with and without it.
    /// 0 whenever the penalty did not apply: the tenant switched the signal off
    /// (<c>lufe_ventas_enabled</c>), the entity has no LUFE snapshot or no sales series, no basis
    /// could be formed from the months present, or sales did not fall.
    /// Absent (deserialises to 0) on every analysis persisted under an earlier algorithm version.
    /// </summary>
    public decimal VentasAdj { get; set; }

    /// <summary>
    /// The input the sales-drop penalty used: the signed variation of monthly sales as a fraction —
    /// -0.20 is a 20 % fall, +0.05 a 5 % rise. NOMINAL under algorithm version 6; from version 7
    /// (§7.3b) it is REAL — the monthly figures are deflated by CPI before being compared — except
    /// where the CPI series could not cover the basis, in which case it is the nominal figure and
    /// <see cref="VentasVariacionEsReal"/> says so. Read that flag before comparing two values: a
    /// version-6 and a version-7 reading are not the same measurement even when numerically equal.
    /// <c>null</c> only when no basis could be formed at all (no series, or too few months carrying
    /// sales). A variation of 0 or above is a real reading with a zero penalty, not a missing one —
    /// the same distinction <see cref="ChequesProporcionRecuperada"/> draws. The monthly figures
    /// themselves are not repeated here: they are persisted beside this result in
    /// <c>AnalysisResults.LufeDataJson</c>, the point-in-time snapshot this was computed from.
    /// </summary>
    public decimal? VentasVariacion { get; set; }

    /// <summary>
    /// Which comparison produced <see cref="VentasVariacion"/>: <c>"interanual_3m"</c> (since algorithm
    /// version 9: the three calendar months ending at the most recent month with sales against the same
    /// three months twelve earlier), <c>"interanual"</c> (the most recent month with sales against the
    /// same calendar month twelve earlier, used when one of those six months is missing) or
    /// <c>"trimestral"</c> (the three most recent months with sales against the three before them, used
    /// when the year-earlier month is missing or unusable). <c>null</c> exactly when <see cref="VentasVariacion"/> is —
    /// the penalty was not evaluated. Reported because the two bases answer different questions, and
    /// a variation whose basis is unknown cannot be interpreted.
    /// </summary>
    public string? VentasBase { get; set; }

    /// <summary>
    /// Whether <see cref="VentasVariacion"/> is in REAL terms (§7.3b, algorithm version 7):
    /// <c>true</c> when the monthly figures were deflated by the BCRA monthly-inflation series,
    /// <c>false</c> when that series was absent or did not cover the months
    /// <see cref="VentasBase"/> compares and the variation is therefore the nominal one.
    /// <para>
    /// <c>null</c> exactly when <see cref="VentasVariacion"/> and <see cref="VentasBase"/> are — the
    /// penalty was not evaluated at all — which is the same convention those two and
    /// <see cref="ChequesProporcionRecuperada"/> follow. On an analysis persisted before version 7
    /// the key is ABSENT from <c>ScoreBreakdownJson</c> rather than null, and absence means
    /// "measured nominally, by an engine that had no other option". Do not collapse the two: a
    /// version-6 row was nominal by design, not for want of inflation data.
    /// </para>
    /// </summary>
    public bool? VentasVariacionEsReal { get; set; }

    /// <summary>
    /// Points ACTUALLY added to the FINAL score by the CASFOG guarantee-arrears penalty (§7.4,
    /// algorithm version 8): 0 or positive, never negative — a clean guarantee record is never a
    /// bonus, only reported arrears move the score. Applied AFTER the critical floor and after
    /// <see cref="VentasAdj"/>, on the score those left, and reported as the realised movement after
    /// the 0–100 clamp, so a score already at 100 absorbs the penalty and reports what it really
    /// took. It is an adjustment on the composite, NOT a component: the effective component weights
    /// are identical with and without it.
    /// 0 whenever the penalty did not apply: the tenant switched the signal off
    /// (<c>casfog_mora_enabled</c>), the entity has no CASFOG snapshot, the snapshot reports no
    /// per-SGR rows, no row carries a measurable balance, or nothing is in arrears.
    /// Absent (deserialises to 0) on every analysis persisted under an earlier algorithm version.
    /// </summary>
    public decimal CasfogMoraAdj { get; set; }

    /// <summary>
    /// The input the arrears penalty used: the share of the entity's guaranteed balance that the SGRs
    /// report IN ARREARS, in [0,1] — 0.32 is 32 % of the balance. Weighted by EXPOSURE, not by count:
    /// it is the sum of <c>SaldoBrutoVigente</c> over the <c>PorSgr</c> rows whose
    /// <c>CantidadEnMora</c> is greater than zero, over the same sum across all rows carrying a
    /// measurable balance. The denominator is those rows' own sum, deliberately not the aggregate
    /// <c>Garantias.SaldoBrutoGarantiasVigentes</c>, which can disagree with them.
    /// <c>null</c> only when the penalty was never evaluated: the signal is off, there are no rows,
    /// or no row carries a positive balance, so the proportion is undefined rather than zero. A
    /// proportion of 0 — rows present, balance measurable, nothing in arrears — is a real reading
    /// with a zero penalty, not a missing one; the same distinction
    /// <see cref="ChequesProporcionRecuperada"/> and <see cref="VentasVariacion"/> draw.
    /// The per-SGR figures themselves are not repeated here: they are persisted beside this result in
    /// <c>AnalysisResults.CasfogDataJson</c>, the point-in-time snapshot this was computed from.
    /// </summary>
    public decimal? CasfogProporcionMora { get; set; }

    /// <summary>true when BCRA was excluded from the composite score due to LookupFailed or absent data.</summary>
    public bool BcraExcluded { get; set; }

    /// <summary>
    /// Why BCRA was excluded; <c>null</c> when it was included. One of
    /// <see cref="BcraExclusionReasons.NoBcraData"/>, <see cref="BcraExclusionReasons.NoDeudaBlock"/>,
    /// <see cref="BcraExclusionReasons.LookupFailed"/> — the three situations
    /// <see cref="BcraExcluded"/> collapses into a single boolean.
    /// <para>
    /// Invariant: non-null if and only if <see cref="BcraExcluded"/> is true. Diagnostic only — it
    /// participates in no arithmetic and cannot move the score. Not set on the neutral fallback
    /// result returned by <c>CalculateAsync</c>'s catch-all, where <see cref="BcraExcluded"/> is
    /// false and scoring did not run at all.
    /// </para>
    /// </summary>
    public string? BcraExclusionReason { get; set; }

    /// <summary>Effective weight applied to the ratios component; renormalized when <see cref="BcraExcluded"/> is true.</summary>
    public decimal EffectiveWeightRatios { get; set; }

    /// <summary>Effective weight applied to the BCRA component; 0 when <see cref="BcraExcluded"/> is true.</summary>
    public decimal EffectiveWeightBcra { get; set; }

    /// <summary>Effective weight applied to the macro component; renormalized when <see cref="BcraExcluded"/> is true.</summary>
    public decimal EffectiveWeightMacro { get; set; }

    /// <summary>
    /// Ratio keys whose value could not be computed (null). Populated on both the renormalised and the
    /// low-coverage fallback paths — the canonical "which ratios were n/d" list, parallel to
    /// <see cref="CriticalRatios"/>. Empty when all eight are computable.
    /// </summary>
    public List<string> NonComputableRatios { get; set; } = new();

    /// <summary>
    /// True when one or more ratios were non-computable but the surviving nominal weight fell below the
    /// minimum-coverage threshold, so the engine fell back to the neutral behaviour (non-computable
    /// ratios scored 50 at full weight) instead of renormalising the survivors. Flags a degraded
    /// composite so it is identifiable after the fact; never silent (also logged at Warning by the caller).
    /// </summary>
    public bool RatiosLowCoverageFallback { get; set; }

    /// <summary>
    /// Sum of the nominal weights of the computable ratios (the surviving coverage). Recorded for audit
    /// alongside <see cref="RatiosLowCoverageFallback"/>; equals the full ratio-weight sum when all eight
    /// are computable.
    /// </summary>
    public decimal RatiosSurvivingWeight { get; set; }

    /// <summary>
    /// True when at least one profitability ratio (ROE, ROA, margen neto) reached the critical trigger but
    /// its floor was NOT applied (algorithm version 10): <c>critical_floor_profitability_requires_operating_loss</c>
    /// was on, the operating result was positive, and the interest separately printed (when known) did not
    /// consume it — so the loss came from the financial and holding results (RECPAM included). The ratios
    /// still scored in the composite exactly as before; only the floor was waived. False whenever the rule
    /// changed nothing. Absent (deserialises to false) on every analysis persisted under an earlier version.
    /// </summary>
    public bool ProfitabilityFloorWaived { get; set; }

    /// <summary>
    /// The profitability ratios whose floor was waived — see <see cref="ProfitabilityFloorWaived"/>. Their
    /// breakdown status stays "Critico"; they are left out of <see cref="CriticalRatios"/>. Empty when
    /// nothing was waived.
    /// </summary>
    public List<string> ProfitabilityFloorWaivedRatios { get; set; } = new();

    /// <summary>
    /// The sign of the balance's <c>operating_result</c> the profitability floor rule read: <c>"positive"</c>,
    /// <c>"zero"</c> or <c>"negative"</c>; <c>null</c> when the rule was not consulted (the flag is off, or no
    /// profitability ratio reached the trigger) or the operating result was not read. Only the sign is
    /// recorded — the figure itself lives in <c>BalanceJson</c>. Algorithm version 10.
    /// </summary>
    public string? OperatingResultSign { get; set; }

    /// <summary>
    /// True when prueba ácida reached the critical trigger but its floor was NOT applied (algorithm version
    /// 11): <c>critical_floor_acid_test_requires_weak_current_ratio</c> was on and liquidez corriente was
    /// measured at or above its warn threshold (or had no current liabilities at all), so the stock covers the
    /// current liabilities. The acid test still scored in the composite and its breakdown status stays
    /// "Critico"; it is left out of <see cref="CriticalRatios"/>. False whenever the rule changed nothing.
    /// Absent (deserialises to false) on every analysis persisted under an earlier version.
    /// </summary>
    public bool AcidTestFloorWaived { get; set; }

    /// <summary>
    /// Where liquidez corriente stood against <c>threshold_liquidez_corriente_warn</c> when the acid-test floor
    /// rule read it: <c>"below"</c> (weak — the floor applies) or <c>"at_or_above"</c> (the floor is waived;
    /// zero current liabilities reads as this). <c>null</c> when the rule was not consulted (the flag is off, or
    /// prueba ácida did not reach the trigger) or liquidez corriente was not measured — the floor applies then.
    /// Only the comparison is recorded, never the figure. Algorithm version 11.
    /// </summary>
    public string? CurrentRatioVsWarn { get; set; }

    /// <summary>Canonical sector name used for threshold resolution (e.g. "General", "Industria").</summary>
    public string? SectorProfile { get; set; }

    /// <summary>
    /// Version of the scoring algorithm that produced this result. See
    /// <see cref="Api.Web.FinanceIA.Servicios.ScoringService.ScoringAlgoVersion"/> for the vocabulary
    /// ("1" = BCRA trend window anchored on the wall clock; "2" = anchored on the most recent period
    /// in the debt history; … "5" = the rejected-cheques penalty inside the BCRA component, see
    /// <see cref="ChequesAdj"/>; "6" = the nominal LUFE sales-drop penalty on the final score, see
    /// <see cref="VentasAdj"/>; "7" = that same penalty measured in real, CPI-deflated terms, see
    /// <see cref="VentasVariacionEsReal"/>; "8" = the CASFOG guarantee-arrears penalty on the final
    /// score, see <see cref="CasfogMoraAdj"/>; "9" = critical floors only at DANGER, leverage thresholds
    /// restated from endeudamiento, the three-month sales basis; "10" = zero current liabilities scored at
    /// the best liquidity band (<see cref="RatioExclusionReasons.NoCurrentLiabilities"/>) and the
    /// profitability floor waived on a positive operating result, see <see cref="ProfitabilityFloorWaived"/>;
    /// "11" = the acid-test floor applied only with a weak current ratio, and its default lowered to 50, see
    /// <see cref="AcidTestFloorWaived"/>).
    /// Unrelated to the extraction/narrative prompt version.
    /// <para>
    /// <c>null</c> in two cases that mean different things. On an analysis persisted before this
    /// field existed the key is simply absent from <c>ScoreBreakdownJson</c> and must be read as
    /// version 1 — those rows are never recomputed or backfilled, which is precisely why the field
    /// exists. On the neutral fallback result returned by <c>CalculateAsync</c>'s catch-all it is
    /// left unset for the same reason as <see cref="BcraExclusionReason"/>: no algorithm ran there,
    /// so stamping a version would be a false provenance claim.
    /// </para>
    /// </summary>
    public string? ScoringAlgoVersion { get; set; }

    /// <summary>
    /// The exact cutoffs <see cref="Level"/> was classified with (the tenant's
    /// <c>score_threshold_low/medium/high</c> after the sector overlay, defaults per missing key).
    /// Persisted as <c>levelCutoffs</c> at the top level of <c>ScoreBreakdownJson</c>, beside
    /// <c>scoringAlgoVersion</c>, so every reader labels this row's level and range with the numbers
    /// that produced it rather than today's config or a hardcoded constant.
    /// <para>
    /// Reporting only — it moves no score, so it carries no algorithm version bump. Absent on every
    /// row persisted before it existed; null on the neutral fallback result, where no level was
    /// classified.
    /// </para>
    /// </summary>
    public ScoreLevelCutoffs? LevelCutoffs { get; set; }

    /// <summary>
    /// True only on the neutral result <c>CalculateAsync</c>'s catch-all returns: the engine did NOT
    /// run, and the 50 / Medium it carries is a placeholder, not a score. Never serialised (it is not
    /// part of <c>ScoreBreakdownJson</c>). <c>AnalysisScoringStage.ScoreAsync</c> turns it into a
    /// <c>ScoringEngineFailedException</c> so the placeholder is never persisted as a real score: a
    /// new analysis is saved without a score, and a re-score refuses and keeps the previous one.
    /// </summary>
    [JsonIgnore]
    public bool EngineFailed { get; set; }

    // ── Reliability facts (score credibility 1b-2a) ────────────────────────────
    // Facts the engine already knew while scoring, recorded so a later reader can judge how far the
    // score can be trusted. REPORTING ONLY: nothing in the engine reads them back, none moves a score,
    // and none is serialised from here — they reach ScoreBreakdownJson only through
    // ScoreReliabilityFactsBuilder, under "reliabilityFacts". Hence [JsonIgnore] on every one.

    /// <summary>
    /// S2: the BCRA reading was DebtFound but its situación was null or outside 1..6, so the BCRA
    /// component scored the neutral 50 rather than a measured situación. False on every other path.
    /// </summary>
    [JsonIgnore]
    public bool BcraSituacionUnreadable { get; set; }

    /// <summary>
    /// S10: the inflation figure was missing (no macro payload, or one without <c>InflacionMensual</c>),
    /// so the macro component scored the neutral 50.
    /// </summary>
    [JsonIgnore]
    public bool MacroImputed { get; set; }

    /// <summary>
    /// S11: equity &lt;= 0 made leverage and/or ROE <c>not_meaningful</c>. Null when the exclusion
    /// reasons are unknown (model-supplied ratios), because the fact cannot be established then.
    /// </summary>
    [JsonIgnore]
    public bool? NegativeEquity { get; set; }

    /// <summary>
    /// S6: the entity carried a non-blank sector that matches no known profile, so it was scored on the
    /// "General" thresholds. False when the sector was blank, known, or "General" itself.
    /// </summary>
    [JsonIgnore]
    public bool SectorFallback { get; set; }

    /// <summary>
    /// The raw sector text behind <see cref="SectorFallback"/>, trimmed and capped at 100 characters;
    /// null unless <see cref="SectorFallback"/> is true. A free-text classification, not PII.
    /// </summary>
    [JsonIgnore]
    public string? SectorRaw { get; set; }

    /// <summary>
    /// S4: where the effective values of the scoring keys came from — <c>"defaults"</c> (no tenant or
    /// sector row reached any key), <c>"tenant"</c> (rows reached every key) or <c>"mixed"</c>. A row
    /// replaced by the threshold-ordering fallback counts as a default. See <see cref="ScoringConfigSources"/>.
    /// </summary>
    [JsonIgnore]
    public string? ConfigSource { get; set; }

    /// <summary>
    /// S4: the first 16 hex characters of a SHA-256 over the EFFECTIVE config the engine scored with,
    /// after the threshold-ordering fallback — see <c>ScoringConfigFingerprint</c>. Two scores with the
    /// same hash read the same parameters.
    /// </summary>
    [JsonIgnore]
    public string? ConfigHash { get; set; }
}

/// <summary>The closed vocabulary for <see cref="ScoringResult.ConfigSource"/>.</summary>
public static class ScoringConfigSources
{
    /// <summary>Every scoring key took its code default.</summary>
    public const string Defaults = "defaults";

    /// <summary>A tenant or sector row reached every scoring key.</summary>
    public const string Tenant = "tenant";

    /// <summary>Some keys came from rows, the rest from code defaults.</summary>
    public const string Mixed = "mixed";
}
