// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using System.Globalization;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Deterministic credit-scoring engine implementation.
/// See <see cref="IScoringService"/> for the full contract.
/// <para>
/// The computation reads no clock. Every input it uses arrives as a parameter, so the same
/// (config, ratios, BCRA, macro) tuple yields the same <see cref="ScoringResult"/> whenever it is
/// scored. The BCRA trend window used to be the exception — it was anchored on
/// <c>DateTime.UtcNow</c> and slid forward every month; it is now anchored on the most recent
/// period in the debt history itself (see <see cref="ScoringAlgoVersion"/>).
/// </para>
/// </summary>
public sealed class ScoringService : IScoringService
{
    private readonly FinanciaDbContext    _db;
    private readonly ILogger<ScoringService> _logger;

    // Minimum surviving nominal ratio-weight for the renormalised composite to be trusted.
    // 0.50 admits the realistic four-null cluster {cobertura, prueba_acida, leverage, roe} (0.55
    // survives) but rejects income-statement-only (~0.22) and liquidity-only (~0.30), where too
    // little of the ratio picture remains to renormalise honestly. Move only with that trade-off in mind.
    private const decimal MinRatioSurvivingWeight = 0.50m;

    // Cap on the raw sector text recorded as a reliability fact (S6). Defensive: Entities.Sector is short.
    private const int MaxSectorRawLength = 100;

    /// <summary>
    /// Version of the scoring ALGORITHM that produced a <see cref="ScoringResult"/>. Distinct from
    /// <c>ClaudeService.ExtractionPromptVersion</c>, which versions the prompts and nothing else —
    /// the two move independently and must never be conflated.
    /// <list type="bullet">
    /// <item><c>"1"</c> — implicit, never written. The BCRA trend window was anchored on
    /// <c>DateTime.UtcNow</c>, so the same payload could score differently in a different month.</item>
    /// <item><c>"2"</c> — the trend window is anchored on the most recent period in the debt history,
    /// which makes the engine a pure function of its inputs.</item>
    /// <item><c>"3"</c> — a macro payload whose inflation figure is missing is treated as neutral
    /// (50) instead of the benign base (30), restoring monotonicity in macro data availability: a
    /// lost inflation reading can no longer make a borrower score healthier than a known one.</item>
    /// <item><c>"4"</c> — a degenerate component-weight config (ratios and macro both zero under
    /// BCRA exclusion) falls back to the default component weights instead of collapsing every
    /// score to 0.00, and a critical floor is clamped into 0-100 before it is applied.</item>
    /// <item><c>"5"</c> — rejected cheques (BCRA "cheques rechazados") become a score signal (§7.1 of
    /// the scoring redesign): a bounded penalty INSIDE the BCRA component, sibling of the trend
    /// adjustment and applied after it, <c>plena × (1 − proporciónRecuperada)</c> where recovery is
    /// measured by AMOUNT (<c>MontoTotalRechazado</c> vs <c>MontoTotalImpago</c>), added to scoreBcra
    /// and clamped into 0-100. It fires on the DebtFound and NoDebtReported paths only, never when
    /// BCRA is excluded; a null, failed or empty cheques block is a zero penalty, never a fabricated
    /// risk. Component weights are untouched. Parametrised by <c>bcra_cheques_penalizacion_plena</c>
    /// (default 15) and switched by <c>bcra_cheques_enabled</c> (default on), both ScoringConfig keys.
    /// The persisted breakdown gains <c>chequesAdj</c> and <c>chequesProporcionRecuperada</c>.</item>
    /// <item><c>"6"</c> — a drop in the monthly sales LUFE reports becomes a score signal (§7.3a of
    /// the scoring redesign, its NOMINAL first phase): a bounded penalty on the FINAL composite
    /// score, applied AFTER the critical floor and clamped into 0-100 on its own. It is deliberately
    /// NOT a component and NOT inside one — LUFE is optional per tenant (§7.0b), so a weighted
    /// component would force renormalisation on every tenant that does not use it; as a
    /// post-composite adjustment an entity with no LUFE data simply takes a zero penalty and the
    /// component weights are untouched. The signal is the signed year-over-year variation of the most
    /// recent month carrying sales against the same calendar month twelve earlier, matched by the
    /// "YYYYMM" period key; when that pair cannot be formed it falls back to the last three months
    /// carrying sales against the three before them, and the basis actually used is reported. It is
    /// ASYMMETRIC: only a drop moves the score, growth and flat never do. Parametrised by
    /// <c>lufe_ventas_penalizacion_plena</c>, <c>lufe_ventas_umbral_inicio</c> and
    /// <c>lufe_ventas_umbral_plena</c> — which version 6 shipped as 10, 0 and -0.25, NOT the values
    /// that ship today (see the version-7 item) — and switched by <c>lufe_ventas_enabled</c>
    /// (default on), all ScoringConfig keys. The persisted breakdown gains <c>ventasAdj</c>,
    /// <c>ventasVariacion</c> and <c>ventasBase</c>. Figures are used AS DECLARED — nominal. §7.3b will
    /// deflate them by CPI to measure the drop in real terms, which changes what the variation means
    /// and so must bump this constant again under its own version.</item>
    /// <item><c>"7"</c> — the same sales-drop penalty now measures the drop in REAL terms (§7.3b).
    /// The monthly figures are deflated by the BCRA "inflación mensual" series (variable 27) before
    /// being compared: the accumulated price factor between the two months being compared is
    /// <c>∏(1 + iₘ/100)</c> over the months separating them, and the reported variation becomes
    /// <c>(1 + nominal) / D − 1</c>. Nothing else moved — the month selection, the ramp, the
    /// asymmetry and the four <c>lufe_ventas_*</c> keys are all exactly as version 6 left them; this
    /// is a substitution at one seam.
    /// <para>
    /// Their DEFAULT VALUES have since moved, and deliberately without a version bump:
    /// <c>lufe_ventas_umbral_inicio</c> is now -0.10 and <c>lufe_ventas_umbral_plena</c> -0.35,
    /// recalibrating a nominal-era ramp that this version had made far too strict in real terms. A
    /// default is CONFIGURATION, not arithmetic — a tenant with its own ScoringConfig rows already
    /// scores differently under the same version — so the constant below stamps the algorithm and
    /// never claimed to pin the parameters. Two analyses both stamped <c>"7"</c> may therefore have
    /// used different default thresholds; every analysis persisted its own breakdown and none is
    /// ever recomputed. Those thresholds are PROVISIONAL: see the note above the keys in
    /// <c>ScoringDefaults</c>.
    /// </para>
    /// <para>
    /// <c>ventasVariacion</c> CHANGES MEANING under this version. A version-6 value and a version-7
    /// value are NOT comparable even when numerically equal — one is nominal, the other real. The
    /// new breakdown key <c>ventasVariacionEsReal</c> is what distinguishes them on a row-by-row
    /// basis; a row with no such key at all was scored before §7.3b and is nominal.
    /// </para>
    /// <para>
    /// The CPI series travels inside the existing <c>BcraDataJson</c> snapshot, so the deflator a
    /// score used is frozen with it and re-scoring one payload still yields one answer. When the
    /// series is absent, or does not cover the months the chosen basis needs, the variation is the
    /// NOMINAL one and says so (<c>ventasVariacionEsReal</c> false) — the signal degrades in the
    /// open rather than disappearing, because a real sales drop is information and losing it to a
    /// missing macro auxiliary is worse than measuring it nominally. A failed deflation never
    /// re-selects the basis: which months are compared is settled before any CPI is consulted.
    /// </para>
    /// <para>
    /// The thresholds were NOT recalibrated here, knowingly. Deflating can only make the variation
    /// more negative, so version 7 applies a calibration chosen for nominal figures to a real signal
    /// and is materially stricter than version 6 on the same data — see the ramp comment in
    /// <c>Compute</c> for the magnitude. That is a separate decision, deliberately not folded into
    /// this one.
    /// </para></item>
    /// <item><c>"8"</c> — the guarantee arrears CASFOG reports become a score signal (§7.4 of the
    /// scoring redesign): a bounded penalty on the FINAL composite score, applied AFTER the critical
    /// floor and after the sales-drop penalty, and clamped into 0-100 on its own. Like that penalty
    /// and for the same reason it is deliberately NOT a component and NOT inside one — CASFOG is
    /// optional per tenant (§7.0b) and meaningless to a non-SGR lender, so a weighted component would
    /// force renormalisation on every tenant that does not use it; as a post-composite adjustment an
    /// entity with no CASFOG data simply takes a zero penalty and the component weights are untouched.
    /// The signal is the share of the guaranteed balance IN ARREARS, weighted by EXPOSURE rather than
    /// by count: the sum of <c>SaldoBrutoVigente</c> over the <c>PorSgr</c> rows reporting
    /// <c>CantidadEnMora</c> greater than zero, over that same sum across every row carrying a
    /// measurable balance — never the aggregate <c>SaldoBrutoGarantiasVigentes</c>, which can disagree
    /// with the rows the numerator is built from. One arrears row at a large SGR is a graver fact than
    /// one at a small SGR, and a count would call them equal. A row whose balance is null (the vendor's
    /// "-" sentinel) is exposure that cannot be measured and joins neither sum; a row whose
    /// <c>CantidadEnMora</c> is null is NOT in arrears, because the absence of a count is not evidence
    /// of one. The CASFOG <c>SituacionDeuda</c> scale is deliberately not read at all — it is a 0-10
    /// vendor scale and must never be mixed with this repo's BCRA 1-6 situación. Parametrised by
    /// <c>casfog_mora_penalizacion_plena</c> (default 12), <c>casfog_mora_umbral_inicio</c> (default
    /// 0.05) and <c>casfog_mora_umbral_plena</c> (default 0.40), and switched by
    /// <c>casfog_mora_enabled</c> (default on), all ScoringConfig keys; those three are a PROVISIONAL
    /// calibration, see the note above the keys in <c>ScoringDefaults</c>. The persisted breakdown
    /// gains <c>casfogMoraAdj</c> and <c>casfogProporcionMora</c>.</item>
    /// <item><c>"9"</c> — critical-floor and leverage calibration, plus a wider LUFE sales window. Three
    /// changes, all prompted by one real case (a Comercio dealer at endeudamiento 0.70 and leverage 2.29,
    /// floored from a composite of ≈ 18 to 65 by a ratio worth 5 % of the total):
    /// <list type="number">
    /// <item>The default <c>critical_floor_trigger_threshold</c> is 100 (was 85). A ratio is critical, and
    /// its floor applies, only when it reaches or passes its DANGER threshold — ROE, ROA and margen neto
    /// at 0 or below, liquidez at 0.70 or below, and so on. <c>RatioStatus</c> reads the same trigger, so
    /// "Crítico" and the floor still always agree; "Atención" is now 50 ≤ partial &lt; 100.</item>
    /// <item>The default leverage thresholds are 1.00 / 2.33 / 5.67 (were 1.00 / 1.50 / 2.50) — the
    /// endeudamiento anchors 0.50 / 0.70 / 0.85 restated through L = E / (1 − E), so the two ratios, which
    /// measure the same thing, can no longer disagree about one balance sheet.</item>
    /// <item>The sales-drop signal prefers a new basis, <c>"interanual_3m"</c>: the last three calendar
    /// months carrying sales against the same three months a year earlier, all six required. One lumpy
    /// month (a fleet sale in the base, a slow month now) no longer decides the comparison on its own.
    /// Each month pair is deflated by its own twelve-month CPI factor before the sums are compared; when
    /// the series cannot cover all three pairs, one deflator over the endpoint span stands in for them
    /// (and <c>ventasVariacionEsReal</c> is false only when even that span is not covered).
    /// When the six months are not all present the engine falls back to the single-month
    /// <c>"interanual"</c> basis and then to <c>"trimestral"</c>, both exactly as version 8 computed them.
    /// <c>ventasBase</c> gains that third value.</item>
    /// </list>
    /// The breakdown also records the floor configuration the engine read — <c>criticalFloorTrigger</c> and
    /// <c>criticalFloors</c> — so the chat can explain a floor with the numbers that produced it; that is
    /// reporting and moves no score. And the engine now refuses to score on a threshold triplet that is not
    /// strictly ordered after base and sector rows merge: that ratio is scored on its complete default
    /// triplet, logged at Warning and listed in <c>thresholdFallbacks</c>. A coherent config is untouched;
    /// the case it exists for is a partial override (a lone leverage danger of 2.00) that the v8 default
    /// warn 1.50 left coherent and the v9 default 2.33 inverts. The first two changes are DEFAULTS, and a
    /// default reaches a tenant per KEY: rows for a key keep that key on its stored value until the rows
    /// change. A tenant that saved the Settings page holds leverage rows but no trigger row (the page never
    /// exposes the trigger), so it takes trigger 100 at once and keeps leverage 1.50 / 2.50 — the "mixed"
    /// state, one of three (see <c>docs/db-changes/068_scoring_config_v9_calibration.md</c>). Analyses already saved keep their
    /// score and their <c>scoringAlgoVersion</c>; none is recomputed.</item>
    /// <item><c>"10"</c> — two corrections validated with the SGR (03/11):
    /// <list type="number">
    /// <item>ZERO CURRENT LIABILITIES IS THE BEST LIQUIDITY. Liquidez corriente with current liabilities read as
    /// 0 and current assets above 0 — and prueba ácida with the same zero and (current assets − inventory −
    /// prepaid expenses) above 0 — used to be a division by zero: null, excluded as <c>not_meaningful</c>, the
    /// composite renormalised without the two ratios where a first-year, debt-free company is strongest, and
    /// the reliability verdict raised <c>ratios_not_meaningful</c>. The value is still null — no infinite
    /// number anywhere — but <c>RatioCalculationService</c> marks it <c>no_current_liabilities</c>, and the
    /// engine scores it at its best band (<see cref="PartialScoreAt"/> fed the OK threshold), keeps its
    /// nominal weight, leaves it out of <c>nonComputableRatios</c> and never lets it be critical. The
    /// breakdown entry is <c>excluded:false</c> with <c>exclusionReason:"no_current_liabilities"</c>. A
    /// numerator at or below zero is <c>not_meaningful</c> as before; an unread current liabilities figure is
    /// <c>unread</c> as before.</item>
    /// <item>THE PROFITABILITY FLOOR NEEDS AN OPERATING LOSS. ROE, ROA and margen neto still score any net
    /// loss in the composite exactly as before — RECPAM is a real economic loss — but their critical FLOOR
    /// applies only when <c>operating_result</c> is not positive or was not read, or when
    /// <c>financial_expenses</c> (pure interest, printed on its own line) is known and
    /// <c>operating_result − |financial_expenses| ≤ 0</c>. Switched by
    /// <c>critical_floor_profitability_requires_operating_loss</c> (0/1, default 1; 0 is version 9). A waived
    /// ratio keeps its "Critico" status — the one exception to version 9's "Crítico ⇔ floor" — but is left
    /// out of <c>criticalRatios</c>, so every reader that explains a floor from that list stays right. The
    /// breakdown gains <c>profitabilityFloorWaived</c>, <c>profitabilityFloorWaivedRatios</c> and
    /// <c>operatingResultSign</c>.</item>
    /// </list>
    /// Analyses already saved keep their score and their <c>scoringAlgoVersion</c>; only a re-score or a new
    /// analysis is scored under version 10.</item>
    /// <item><c>"11"</c> — THE ACID-TEST FLOOR NEEDS A WEAK CURRENT RATIO. A critical prueba ácida says that
    /// much of the current assets is stock; while liquidez corriente is at or above its WARN threshold that
    /// stock still covers the current liabilities, so the acid test keeps its "Critico" status and its weight
    /// in the composite, exactly as before, but its critical FLOOR applies only when liquidez corriente is
    /// measured and strictly below <c>threshold_liquidez_corriente_warn</c> (the merged value, sector rows
    /// included). An unread current ratio keeps the floor; zero current liabilities (the best band) is not
    /// weak. Switched by <c>critical_floor_acid_test_requires_weak_current_ratio</c> (0/1, default 1; 0 is
    /// version 10). And the default <c>critical_floor_prueba_acida</c> is 50 (was 60) — a default, reaching
    /// only tenants with no row for it (<c>docs/database/082_scoring_v11_acid_test_floor.sql</c> moves rows
    /// still at 60). A waived acid test is left out of <c>criticalRatios</c>, like a waived profitability
    /// ratio. The breakdown gains <c>acidTestFloorWaived</c> and <c>currentRatioVsWarn</c>. Analyses already
    /// saved keep their score and their <c>scoringAlgoVersion</c> until re-scored.</item>
    /// </list>
    /// Persisted inside <c>AnalysisResults.ScoreBreakdownJson</c> as <c>scoringAlgoVersion</c>. Rows
    /// written before this field existed carry no such key and deserialise to <c>null</c> — read that
    /// as version 1. Existing analyses are NOT recomputed and NOT backfilled; the field exists so an
    /// old score stays identifiable as one an earlier engine produced, not so it can be repaired.
    /// <para>
    /// CONVENTION — this is a rule for whoever edits the engine next, not a per-change judgement
    /// call: every change to the engine's arithmetic, or to its comparison-period or component
    /// selection, that can make two results incomparable MUST bump this constant in the same commit
    /// and add its own <c>&lt;item&gt;</c> above; changes that are prompt-only or reporting-only
    /// MUST NOT bump it, because they leave every computed score bit-identical.
    /// </para>
    /// <para>
    /// Reporting-only additions made under version 10, none of which bumped it: <c>levelCutoffs</c>
    /// (the <c>score_threshold_*</c> values the level was classified with, see
    /// <see cref="ScoreLevelCutoffs"/>). Absent on every row scored before it existed.
    /// </para>
    /// </summary>
    public const string ScoringAlgoVersion = "11";

    public ScoringService(FinanciaDbContext db, ILogger<ScoringService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── IScoringService ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ScoringResult> CalculateAsync(
        string                      tenantId,
        string?                     entitySector,
        RatiosData                  ratios,
        BcraData?                   bcraData,
        BcraMacroData?              macroData,
        List<LufeVentaCompraMes>?   ventasMensuales,
        List<CasfogGarantiaSgr>?    garantiasPorSgr,
        CancellationToken           ct)
    {
        try
        {
            var sector = SectorProfiles.Canonicalize(entitySector);
            var cfg = await LoadConfigAsync(tenantId, sector, ct).ConfigureAwait(false);

            // Reporting only (score credibility 1b-2a, S4): at this point the merged map holds ONLY the
            // keys a tenant or sector row supplied. Taken before the ordering fallback writes defaults in.
            var rowKeys = new HashSet<string>(cfg.Keys, StringComparer.Ordinal);

            var thresholdFallbacks = ApplyThresholdOrderingFallback(cfg);
            foreach (var fallback in thresholdFallbacks)
            {
                _logger.LogWarning(
                    "Scoring thresholds for ratio {Ratio} are not strictly ordered for tenant {TenantId}, " +
                    "sector {Sector} (ok {Ok}, warn {Warn}, danger {Danger}); scoring that ratio on its " +
                    "complete default triplet instead",
                    fallback.Ratio, tenantId, sector, fallback.Ok, fallback.Warn, fallback.Danger);
            }

            var result = Compute(
                ratios, bcraData, macroData, ventasMensuales, garantiasPorSgr, cfg, sector,
                thresholdFallbacks.Select(f => f.Ratio).ToList());

            // ── Reliability facts (score credibility 1b-2a) — reporting only ───────────
            // Read AFTER the score is final and never fed back: none of these can move it.

            // A triplet the ordering fallback refused was scored on its code defaults, whatever is stored.
            foreach (var fallback in thresholdFallbacks)
            {
                rowKeys.Remove($"threshold_{fallback.Ratio}_ok");
                rowKeys.Remove($"threshold_{fallback.Ratio}_warn");
                rowKeys.Remove($"threshold_{fallback.Ratio}_danger");
            }

            result.ConfigSource = ScoringConfigFingerprint.ClassifySource(rowKeys);
            result.ConfigHash   = ScoringConfigFingerprint.ComputeHash(cfg);

            // S6: a sector that names no known profile is scored as "General" with no other trace.
            result.SectorFallback = !string.IsNullOrWhiteSpace(entitySector) && !SectorProfiles.IsKnown(entitySector);
            result.SectorRaw      = result.SectorFallback ? TruncateSector(entitySector!) : null;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Scoring calculation failed for tenant {TenantId}; returning neutral result",
                tenantId);

            // A placeholder, flagged: the caller (AnalysisScoringStage) refuses to persist it.
            return new ScoringResult
            {
                Score          = 50m,
                Level          = "Medium",
                ScoreRatios    = 50m,
                ScoreBcra      = 50m,
                ScoreMacro     = 50m,
                SectorProfile  = "General",
                EngineFailed   = true
            };
        }
    }

    /// <inheritdoc />
    public async Task<ScoreLevelCutoffs> GetLevelCutoffsAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // The same loader and the same rule the engine classifies with, on the base ("General")
        // profile: no sector overlay, because this answers "what are the tenant's cutoffs", not
        // "what did one sector's analysis use" — that is the row's own levelCutoffs.
        var cfg = await LoadConfigAsync(tenantId, "General", ct).ConfigureAwait(false);
        return ScoreLevelCutoffs.Resolve(cfg);
    }

    // ── Config loading ────────────────────────────────────────────────────────

    private async Task<Dictionary<string, decimal>> LoadConfigAsync(
        string tenantId, string canonicalSector, CancellationToken ct)
    {
        var rows = await _db.ScoringConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Base dict: rows without any __sector__ suffix
        var baseCfg = rows
            .Where(c => !c.ConfigKey.Contains("__sector__"))
            .ToDictionary(c => c.ConfigKey, c => c.ConfigValue);

        // Overlay sector-specific rows when sector is not "General"
        if (canonicalSector != "General")
        {
            var suffix = SectorProfiles.SectorSuffix(canonicalSector);
            foreach (var row in rows)
            {
                if (row.ConfigKey.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var baseKey = row.ConfigKey[..^suffix.Length];
                    baseCfg[baseKey] = row.ConfigValue;
                }
            }
        }

        return baseCfg;
    }

    /// <summary>The raw sector text for the reliability facts: trimmed, at most 100 characters.</summary>
    private static string TruncateSector(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length <= MaxSectorRawLength ? trimmed : trimmed[..MaxSectorRawLength];
    }

    private static decimal Get(Dictionary<string, decimal> cfg, string key, decimal fallback)
        => cfg.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>A ratio whose merged threshold triplet was refused, and the values that were refused.</summary>
    private readonly record struct ThresholdFallback(string Ratio, decimal Ok, decimal Warn, decimal Danger);

    /// <summary>
    /// Refuses to score on a threshold triplet that is not strictly ordered, and substitutes that ratio's
    /// COMPLETE default triplet — all three values, never a mix of stored and default ones, because a
    /// mix is how an incoherent triplet arises in the first place.
    /// <para>
    /// Runs on the MERGED map (base rows, then the entity's sector rows over them), because that is the
    /// map the interpolation reads and the place an inversion actually appears: a tenant holding only
    /// <c>threshold_leverage_danger__sector__Comercio = 2.00</c> was coherent against the v8 default warn
    /// (1.50) and is inverted against the v9 one (2.33). PartialScore on 1.00 / 2.33 / 2.00 jumps from
    /// ≈ 37 straight to 100 at 2.00 — a critical ratio, and a floor, out of a config nobody chose — and
    /// the same stored rows make every Settings save for that profile fail its coherence check, so the
    /// tenant could not even see the problem through the UI. The rule is the one Settings validates
    /// with, <see cref="ScoringDefaults.IsThresholdTripletOrdered"/>.
    /// </para>
    /// <para>
    /// A coherent triplet is never touched, so on any config Settings would accept this changes nothing.
    /// Every substitution is returned for the caller to log and for the breakdown to record as
    /// <c>thresholdFallbacks</c> (scoring algorithm version 9).
    /// </para>
    /// </summary>
    private static List<ThresholdFallback> ApplyThresholdOrderingFallback(Dictionary<string, decimal> cfg)
    {
        var fallbacks = new List<ThresholdFallback>();

        foreach (var ratio in ScoringDefaults.ThresholdRatios)
        {
            var okKey     = $"threshold_{ratio}_ok";
            var warnKey   = $"threshold_{ratio}_warn";
            var dangerKey = $"threshold_{ratio}_danger";

            decimal ok     = Get(cfg, okKey,     ScoringDefaults.Get(okKey));
            decimal warn   = Get(cfg, warnKey,   ScoringDefaults.Get(warnKey));
            decimal danger = Get(cfg, dangerKey, ScoringDefaults.Get(dangerKey));

            if (ScoringDefaults.IsThresholdTripletOrdered(ratio, ok, warn, danger))
                continue;

            fallbacks.Add(new ThresholdFallback(ratio, ok, warn, danger));

            cfg[okKey]     = ScoringDefaults.Get(okKey);
            cfg[warnKey]   = ScoringDefaults.Get(warnKey);
            cfg[dangerKey] = ScoringDefaults.Get(dangerKey);
        }

        return fallbacks;
    }

    // ── Core computation ──────────────────────────────────────────────────────

    private static ScoringResult Compute(
        RatiosData             ratios,
        BcraData?              bcraData,
        BcraMacroData?         macroData,
        List<LufeVentaCompraMes>? ventasMensuales,
        List<CasfogGarantiaSgr>? garantiasPorSgr,
        Dictionary<string, decimal> cfg,
        string                 canonicalSector,
        List<string>           thresholdFallbacks)
    {
        // ── Load weights ──────────────────────────────────────────────────────

        decimal wLiquidez   = Get(cfg, "weight_liquidez_corriente",  ScoringDefaults.Get("weight_liquidez_corriente"));
        decimal wPrueba     = Get(cfg, "weight_prueba_acida",        ScoringDefaults.Get("weight_prueba_acida"));
        decimal wEndeudam   = Get(cfg, "weight_endeudamiento",       ScoringDefaults.Get("weight_endeudamiento"));
        decimal wLeverage   = Get(cfg, "weight_leverage",            ScoringDefaults.Get("weight_leverage"));
        decimal wCobertura  = Get(cfg, "weight_cobertura_intereses", ScoringDefaults.Get("weight_cobertura_intereses"));
        decimal wRoe        = Get(cfg, "weight_roe",                 ScoringDefaults.Get("weight_roe"));
        decimal wRoa        = Get(cfg, "weight_roa",                 ScoringDefaults.Get("weight_roa"));
        decimal wMargen     = Get(cfg, "weight_margen_neto",         ScoringDefaults.Get("weight_margen_neto"));

        // ── Compute partial scores per ratio ──────────────────────────────────

        // Zero current liabilities against a positive numerator (algorithm version 10). The ratio value
        // stays null — no infinite number is ever produced — but RatioCalculationService marks it, and
        // the engine scores it at its BEST band: the partial its own "at or beyond the OK threshold"
        // branch returns, fed the OK threshold itself, so no score is hardcoded here and a tenant's
        // thresholds still decide it. Ratios taken from the model's own output carry no marker and are
        // excluded exactly as before.
        bool liquidezAtBest = HasNoCurrentLiabilities(ratios, "liquidez_corriente");
        bool pruebaAtBest   = HasNoCurrentLiabilities(ratios, "prueba_acida");

        // liquidez_corriente — higher is better
        decimal okLiquidez     = Get(cfg, "threshold_liquidez_corriente_ok",     ScoringDefaults.Get("threshold_liquidez_corriente_ok"));
        decimal warnLiquidez   = Get(cfg, "threshold_liquidez_corriente_warn",   ScoringDefaults.Get("threshold_liquidez_corriente_warn"));
        decimal dangerLiquidez = Get(cfg, "threshold_liquidez_corriente_danger", ScoringDefaults.Get("threshold_liquidez_corriente_danger"));
        decimal sLiquidez = liquidezAtBest
            ? PartialScoreAt(okLiquidez, okLiquidez, warnLiquidez, dangerLiquidez)
            : PartialScore(ratios.CurrentRatio, okLiquidez, warnLiquidez, dangerLiquidez);

        // prueba_acida — higher is better
        decimal okPrueba     = Get(cfg, "threshold_prueba_acida_ok",     ScoringDefaults.Get("threshold_prueba_acida_ok"));
        decimal warnPrueba   = Get(cfg, "threshold_prueba_acida_warn",   ScoringDefaults.Get("threshold_prueba_acida_warn"));
        decimal dangerPrueba = Get(cfg, "threshold_prueba_acida_danger", ScoringDefaults.Get("threshold_prueba_acida_danger"));
        decimal sPrueba = pruebaAtBest
            ? PartialScoreAt(okPrueba, okPrueba, warnPrueba, dangerPrueba)
            : PartialScore(ratios.AcidTest, okPrueba, warnPrueba, dangerPrueba);

        // endeudamiento — lower is better (ok < danger)
        decimal sEndeudam = PartialScore(ratios.DebtRatio,
            Get(cfg, "threshold_endeudamiento_ok",     ScoringDefaults.Get("threshold_endeudamiento_ok")),
            Get(cfg, "threshold_endeudamiento_warn",   ScoringDefaults.Get("threshold_endeudamiento_warn")),
            Get(cfg, "threshold_endeudamiento_danger", ScoringDefaults.Get("threshold_endeudamiento_danger")));

        // leverage — lower is better
        decimal sLeverage = PartialScore(ratios.Leverage,
            Get(cfg, "threshold_leverage_ok",     ScoringDefaults.Get("threshold_leverage_ok")),
            Get(cfg, "threshold_leverage_warn",   ScoringDefaults.Get("threshold_leverage_warn")),
            Get(cfg, "threshold_leverage_danger", ScoringDefaults.Get("threshold_leverage_danger")));

        // cobertura_intereses — higher is better
        decimal sCobertura = PartialScore(ratios.InterestCoverage,
            Get(cfg, "threshold_cobertura_intereses_ok",     ScoringDefaults.Get("threshold_cobertura_intereses_ok")),
            Get(cfg, "threshold_cobertura_intereses_warn",   ScoringDefaults.Get("threshold_cobertura_intereses_warn")),
            Get(cfg, "threshold_cobertura_intereses_danger", ScoringDefaults.Get("threshold_cobertura_intereses_danger")));

        // roe — higher is better (%)
        decimal sRoe = PartialScore(ratios.Roe,
            Get(cfg, "threshold_roe_ok",     ScoringDefaults.Get("threshold_roe_ok")),
            Get(cfg, "threshold_roe_warn",   ScoringDefaults.Get("threshold_roe_warn")),
            Get(cfg, "threshold_roe_danger", ScoringDefaults.Get("threshold_roe_danger")));

        // roa — higher is better (%)
        decimal sRoa = PartialScore(ratios.Roa,
            Get(cfg, "threshold_roa_ok",     ScoringDefaults.Get("threshold_roa_ok")),
            Get(cfg, "threshold_roa_warn",   ScoringDefaults.Get("threshold_roa_warn")),
            Get(cfg, "threshold_roa_danger", ScoringDefaults.Get("threshold_roa_danger")));

        // margen_neto — higher is better (%)
        decimal sMargen = PartialScore(ratios.NetMargin,
            Get(cfg, "threshold_margen_neto_ok",     ScoringDefaults.Get("threshold_margen_neto_ok")),
            Get(cfg, "threshold_margen_neto_warn",   ScoringDefaults.Get("threshold_margen_neto_warn")),
            Get(cfg, "threshold_margen_neto_danger", ScoringDefaults.Get("threshold_margen_neto_danger")));

        // ── Weighted ratios score (exclude non-computable ratios; renormalise survivors) ──────
        //
        // A ratio whose value is null cannot be measured. Rather than pinning it at the neutral 50
        // and weighting it in full, it is excluded from the composite (weight 0) and the surviving
        // ratio weights are renormalised — the per-ratio analogue of the BCRA component exclusion
        // below. Guarded by a minimum-coverage rule: when too little weight survives, a renormalised
        // composite would be confidently wrong, so we fall back to the neutral behaviour (and flag it).

        var ratioItems = new (string Key, decimal Score, decimal Weight, bool Computable)[]
        {
            // A ratio scored at the best band for having no current liabilities is MEASURED: it keeps its
            // nominal weight and never enters the renormalisation as an exclusion (algorithm version 10).
            ("liquidez_corriente",  sLiquidez,  wLiquidez,  ratios.CurrentRatio.HasValue || liquidezAtBest),
            ("prueba_acida",        sPrueba,    wPrueba,    ratios.AcidTest.HasValue     || pruebaAtBest),
            ("endeudamiento",       sEndeudam,  wEndeudam,  ratios.DebtRatio.HasValue),
            ("leverage",            sLeverage,  wLeverage,  ratios.Leverage.HasValue),
            ("cobertura_intereses", sCobertura, wCobertura, ratios.InterestCoverage.HasValue),
            ("roe",                 sRoe,       wRoe,       ratios.Roe.HasValue),
            ("roa",                 sRoa,       wRoa,       ratios.Roa.HasValue),
            ("margen_neto",         sMargen,    wMargen,    ratios.NetMargin.HasValue),
        };

        // The neutral 50 lives only in PartialScore. Both the all-computable path and the low-coverage
        // fallback consume this same raw weighted sum — one behaviour, not two copies.
        decimal rawWeightedSum     = ratioItems.Sum(i => i.Score * i.Weight);
        decimal survivingWeightSum = ratioItems.Where(i => i.Computable).Sum(i => i.Weight);
        decimal survivorWeighted   = ratioItems.Where(i => i.Computable).Sum(i => i.Score * i.Weight);

        var  nonComputableRatios = ratioItems.Where(i => !i.Computable).Select(i => i.Key).ToList();
        bool anyExcluded         = nonComputableRatios.Count > 0;

        decimal scoreRatios;
        bool    renormaliseRatios;              // true => survivors renormalised, excluded ratios dropped
        bool    ratiosLowCoverageFallback = false;

        if (!anyExcluded)
        {
            scoreRatios       = rawWeightedSum;           // identical to the historical expression
            renormaliseRatios = false;
        }
        else if (survivingWeightSum >= MinRatioSurvivingWeight && survivingWeightSum > 0m)
        {
            scoreRatios       = survivorWeighted / survivingWeightSum;
            renormaliseRatios = true;
        }
        else
        {
            // Too little of the ratio picture survives to renormalise honestly — keep today's
            // behaviour (non-computable ratios scored 50 at full weight), but flag the degraded
            // composite so it is never silent (the caller also logs it at Warning with the analysis id).
            scoreRatios               = rawWeightedSum;
            renormaliseRatios         = false;
            ratiosLowCoverageFallback = true;
        }

        // Excluded ratios (renormalise path only) carry no measured score and must not enter the
        // critical-ratio floor. Harmless in practice — a non-computable ratio scores 50 < trigger —
        // but kept explicit so the floor operates only on measured indicators.
        var excludedRatioKeys = renormaliseRatios
            ? new HashSet<string>(nonComputableRatios, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // ── BCRA score ────────────────────────────────────────────────────────

        bool    bcraExcluded   = false;
        string? bcraExclusionReason = null;   // non-null iff bcraExcluded; carries no weight
        decimal scoreBcra      = 0m;   // only used when !bcraExcluded
        int     bcraTrendDelta = 0;
        decimal bcraTrendAdj   = 0m;
        bool    bcraSituacionUnreadable = false;   // reporting only (1b-2a, S2): DebtFound scored the neutral 50

        // The guards — no payload, no Deuda block, a failed lookup, no debt, debt — live in
        // BcraSituacionRule.Read, in this order, so the dashboard reads a payload exactly as the engine
        // does. The three exclusions were once one compound condition
        // (`bcraData?.Deuda is null || estado == LookupFailed`); splitting it is score-neutral, and each
        // still does exactly `bcraExcluded = true`. Only the recorded reason is new — it feeds no
        // arithmetic. Pinned by ScoringServiceTests.
        var bcraReading = BcraSituacionRule.Read(bcraData);

        if (bcraReading.Kind == BcraDeudaReadingKind.Excluded)
        {
            // Nothing came back, no Central de Deudores block, or unreliable data — exclude BCRA from
            // the composite; weights will be renormalized below.
            bcraExcluded        = true;
            bcraExclusionReason = bcraReading.ExclusionReason;
        }
        else if (bcraReading.Kind == BcraDeudaReadingKind.NoDebt)
        {
            scoreBcra = 0m;  // optimal BASE: no reported debt — equivalent to situación 1
            // No trend adjustment — deliberately, even when HistorialSituaciones carries past months
            // from Deudas/Historicas. Whether past bad situaciones should weigh on a company with no
            // current debt is a separate product decision, not taken here. The cheques penalty below
            // still applies on this path: a company with no bank debt but unrecovered rejected
            // cheques is exactly the case that signal exists for.
        }
        else // DebtFound — the reading guarantees a payload with a Deuda block
        {
            var situacion = bcraReading.SituacionMaxima;
            bcraSituacionUnreadable = situacion is not (>= 1 and <= 6);
            scoreBcra = situacion.HasValue ? situacion.Value switch
            {
                1 =>   0m,
                2 =>  20m,
                3 =>  45m,
                4 =>  70m,
                5 =>  90m,
                6 => 100m,
                _ =>  50m
            } : 50m;

            // ── BCRA trend adjustment (Feature 5c) ──────────────────────────────────
            var historial = bcraData!.Deuda!.HistorialSituaciones;
            if (historial is { Count: >= 2 } && situacion.HasValue)
            {
                decimal trendWindowMonths = Get(cfg, "bcra_trend_window_months",        ScoringDefaults.Get("bcra_trend_window_months"));
                decimal ptsPerStepImprove = Get(cfg, "bcra_trend_pts_per_step_improve", ScoringDefaults.Get("bcra_trend_pts_per_step_improve"));
                decimal ptsPerStepWorsen  = Get(cfg, "bcra_trend_pts_per_step_worsen",  ScoringDefaults.Get("bcra_trend_pts_per_step_worsen"));
                decimal maxImproveAdj     = Get(cfg, "bcra_trend_max_improve_adj",      ScoringDefaults.Get("bcra_trend_max_improve_adj"));
                decimal maxWorsenAdj      = Get(cfg, "bcra_trend_max_worsen_adj",       ScoringDefaults.Get("bcra_trend_max_worsen_adj"));

                // The window is anchored on the DATA, never on the wall clock (finding D2). The
                // anchor is the most recent period present in the history, taken by ORDINAL MAX
                // rather than historial[0]: BcraService.MapDeuda emits the list newest-first, but
                // nothing re-imposes that order on a payload read back from BcraDataJson, so the
                // anchor must not depend on stored position. Ordinal because Comparer<string>.Default
                // is culture-sensitive, which would be a second source of non-determinism.
                //
                // With a clock anchor the same payload scored in two different months picked two
                // different comparison periods, so the engine was not the pure function that both it
                // and IScoringService document. It is now.
                var anchorPeriodo = historial.MaxBy(p => p.Periodo, StringComparer.Ordinal)?.Periodo;
                var cutoff        = DeflactorIpc.ShiftPeriodoMonths(anchorPeriodo, -(int)trendWindowMonths);

                // A malformed anchor — legacy or hand-edited data whose newest Periodo is not a
                // YYYYMM value — yields no cutoff, and the whole adjustment is skipped: delta 0 and
                // adj 0, exactly as when the history holds fewer than two periods. Falling back to
                // DateTime.UtcNow here would put the non-determinism straight back.
                if (cutoff is not null)
                {
                    var comparison = historial
                        .Where(p => string.Compare(p.Periodo, cutoff, StringComparison.Ordinal) <= 0)
                        .OrderByDescending(p => p.Periodo, StringComparer.Ordinal)
                        .FirstOrDefault()
                        ?? historial[^1];

                    bcraTrendDelta = situacion.Value - comparison.SituacionMaxima;

                    if (bcraTrendDelta < 0)
                    {
                        decimal adj = Math.Min(Math.Abs(bcraTrendDelta) * ptsPerStepImprove, maxImproveAdj);
                        bcraTrendAdj = -adj;
                    }
                    else if (bcraTrendDelta > 0)
                    {
                        decimal adj = Math.Min(bcraTrendDelta * ptsPerStepWorsen, maxWorsenAdj);
                        bcraTrendAdj = adj;
                    }

                    // bcraTrendAdj is reported as the points ACTUALLY applied, not the points proposed.
                    // The two differ whenever the clamp absorbs part of the adjustment — situación 1 is
                    // already at the 0 floor of the BCRA scale, so a -20 "improvement" moves nothing, and
                    // situación 5 worsening by four steps can only climb the last 10 points to 100.
                    // Reporting the proposed figure made the PDF and the chat context state an "ajuste
                    // aplicado" the score never carried. BcraTrendDelta still reports the full movement in
                    // situación steps, so nothing about the trend itself is hidden.
                    decimal scoreBcraBeforeTrend = scoreBcra;
                    scoreBcra    = Math.Clamp(scoreBcra + bcraTrendAdj, 0m, 100m);
                    bcraTrendAdj = scoreBcra - scoreBcraBeforeTrend;
                }
            }
        }

        // ── BCRA cheques rechazados penalty (§7.1) ──────────────────────────────
        //
        // The second bounded adjustment inside the BCRA component: sibling of the trend adjustment
        // above and applied AFTER it, on whatever scoreBcra the trend left, so the two realised deltas
        // add up exactly — scoreBcra == base + bcraTrendAdj + chequesAdj. It runs on both paths where
        // scoreBcra is real (NoDebtReported and DebtFound) and never when BCRA is excluded, where the
        // component carries no weight and ScoreBcra is reported as 0 regardless. It does NOT touch the
        // component weights or their renormalisation.
        //
        // Recovery is measured by AMOUNT, not by count of cheques:
        //   proporcionRecuperada = (MontoTotalRechazado - MontoTotalImpago) / MontoTotalRechazado
        //   chequesAdj           = plena * (1 - proporcionRecuperada)
        // Everything recovered → 0; nothing recovered → the full plena; 60 % recovered → 40 % of plena.
        //
        // Estado is checked BEFORE the amounts. The LookupFailed block BcraService returns on an outage
        // carries zero amounts, byte-identical to a clean NoChequesReported, so an amounts-first rule
        // would score an outage as a clean record — and, mirrored, could never distinguish "no cheques"
        // from "no answer". Missing data (null block, failed lookup) and a clean record (no cheques
        // reported) both mean no penalty: the engine never invents risk from data it does not have, the
        // same refusal the BCRA exclusion makes. Only a ChequesFound block with real exposure moves the
        // score. MontoTotalRechazado == 0 is guarded explicitly — no exposure to penalise, and it is the
        // divisor — and the proportion is clamped into [0,1] so a stored or hand-edited blob with
        // impago > rechazado cannot yield a negative penalty or one above plena.
        //
        // Reads only Estado and the two stored totals — never Detalle, never a clock — so the penalty
        // is a pure function of the persisted BcraChequesData and the tenant configuration.

        decimal  chequesAdj                  = 0m;     // points ACTUALLY applied, after the clamp
        decimal? chequesProporcionRecuperada = null;   // the [0,1] input used; null when not evaluated

        // Whether the penalty takes part at all — reported, never scored, so the enrichment assessment
        // can flag a failed cheques lookup exactly when it could have moved this score.
        bool chequesEvaluated = !bcraExcluded &&
            Get(cfg, "bcra_cheques_enabled", ScoringDefaults.Get("bcra_cheques_enabled")) > 0m;

        if (chequesEvaluated)
        {
            var cheques = bcraData?.Cheques;

            if (cheques is { Estado: BcraChequesConsultaEstado.ChequesFound, MontoTotalRechazado: > 0m })
            {
                // Plena is a number of points on the 0-100 BCRA scale, so a value outside that range
                // cannot be one. SettingsService rejects such a value at the door; this is the second
                // layer, the same shape as the critical-floor clamp further down.
                decimal plena = Math.Clamp(
                    Get(cfg, "bcra_cheques_penalizacion_plena", ScoringDefaults.Get("bcra_cheques_penalizacion_plena")),
                    0m, 100m);

                decimal proporcion = Math.Clamp(
                    (cheques.MontoTotalRechazado - cheques.MontoTotalImpago) / cheques.MontoTotalRechazado,
                    0m, 1m);

                decimal proposed = plena * (1m - proporcion);

                // Reported as the points ACTUALLY applied, exactly as bcraTrendAdj is above: a situación
                // already at 100 — or pushed there by the trend — absorbs the whole penalty, and the PDF
                // and the chat context must not claim a "penalidad aplicada" the score never carried.
                decimal scoreBcraBeforeCheques = scoreBcra;
                scoreBcra  = Math.Clamp(scoreBcra + proposed, 0m, 100m);
                chequesAdj = scoreBcra - scoreBcraBeforeCheques;

                chequesProporcionRecuperada = proporcion;
            }
        }

        // ── Macro score ───────────────────────────────────────────────────────

        // 50 is the "not known" reading, and it has to cover BOTH ways the inflation figure can go
        // missing: no macro payload at all, and a payload whose InflacionMensual is null.
        // BcraService.GetMacroAsync returns null only when BOTH its variables fail (inflación #27
        // and TAMAR #135), so a payload carrying only TAMAR is a real, reachable shape. It used to
        // take the 30 base — the BEST macro outcome the engine can produce — which made losing the
        // one macro variable scoring actually reads look HEALTHIER than a total outage (50) or a
        // confirmed above-threshold reading (35). The base and its penalty only mean something once
        // the figure is known, so knowing it is now the entry condition. TAMAR stays unread by
        // scoring: this is about not rewarding a missing inflation figure, not about using the rate.

        decimal scoreMacro = 50m; // neutral when the inflation figure is unavailable
        if (macroData?.InflacionMensual is { } inflacionMensual)
        {
            decimal inflacionThreshold = Get(cfg, "macro_inflacion_penalizacion_threshold", ScoringDefaults.Get("macro_inflacion_penalizacion_threshold"));
            decimal penalizacion       = Get(cfg, "macro_penalizacion_puntos",               ScoringDefaults.Get("macro_penalizacion_puntos"));

            scoreMacro = 30m; // base — Argentine macro always carries some risk
            if ((decimal)inflacionMensual > inflacionThreshold)
            {
                scoreMacro += penalizacion;
            }
            if (scoreMacro > 100m) scoreMacro = 100m;
        }

        // ── Composite score ────────────────────────────────────────────────────────

        decimal wRatios = Get(cfg, "weight_component_ratios", ScoringDefaults.Get("weight_component_ratios"));
        decimal wBcra   = Get(cfg, "weight_component_bcra",   ScoringDefaults.Get("weight_component_bcra"));
        decimal wMacro  = Get(cfg, "weight_component_macro",  ScoringDefaults.Get("weight_component_macro"));

        decimal effWRatios, effWBcra, effWMacro;
        if (bcraExcluded)
        {
            decimal denom = wRatios + wMacro;
            if (denom <= 0m)
            {
                // Degenerate config: the two components that survive BCRA exclusion are both
                // weightless, so there is nothing to renormalise. The previous guard fell back to
                // the NOMINAL weights — the same two zeros — which drove every effective weight,
                // and therefore every company's score, to 0.00 / "Low": maximum apparent health
                // for anyone at all. Fall back to the shipped defaults instead: they are valid by
                // construction (0.50 / 0.35 / 0.15, three non-zero, so ratios+macro is never 0),
                // which keeps the composite a real weighted average. SettingsService now rejects
                // such a config at the door; this arm only catches a row written before that
                // check existed, or one that slips through a future validation gap.
                decimal dRatios = ScoringDefaults.Get("weight_component_ratios");
                decimal dMacro  = ScoringDefaults.Get("weight_component_macro");
                decimal dDenom  = dRatios + dMacro;

                effWRatios = dRatios / dDenom;
                effWBcra   = 0m;
                effWMacro  = dMacro  / dDenom;
            }
            else
            {
                effWRatios = wRatios / denom;
                effWBcra   = 0m;
                effWMacro  = wMacro  / denom;
            }
        }
        else
        {
            effWRatios = wRatios;
            effWBcra   = wBcra;
            effWMacro  = wMacro;
        }

        decimal scoreFinal =
            scoreRatios * effWRatios +
            scoreBcra   * effWBcra   +
            scoreMacro  * effWMacro;

        scoreFinal = Math.Clamp(scoreFinal, 0m, 100m);
        scoreFinal = Math.Round(scoreFinal, 2);

        // ── Critical-ratio floor (Feature 5a) ──────────────────────────────

        decimal scoreBeforeFloor = scoreFinal;
        decimal effectiveFloor   = 0m;
        var     criticalRatios   = new List<string>();

        decimal triggerThreshold = Get(cfg, "critical_floor_trigger_threshold",
            ScoringDefaults.Get("critical_floor_trigger_threshold"));

        var partialScores = new Dictionary<string, decimal>
        {
            ["liquidez_corriente"]  = sLiquidez,
            ["prueba_acida"]        = sPrueba,
            ["endeudamiento"]       = sEndeudam,
            ["leverage"]            = sLeverage,
            ["cobertura_intereses"] = sCobertura,
            ["roe"]                 = sRoe,
            ["roa"]                 = sRoa,
            ["margen_neto"]         = sMargen,
        };

        // The per-ratio floors in force, recorded for every ratio — not only the critical ones — so the
        // chat can say what floor each ratio WOULD impose (version 9). Reporting only: each is the value
        // the loop below reads, clamped the way the effective floor is clamped after it, so a floor
        // reported here is a floor the engine could actually have applied.
        var criticalFloors = new Dictionary<string, decimal>(ScoringDefaults.RatioFloorKeyMap.Count, StringComparer.Ordinal);

        // ── Profitability floor needs an operating loss (algorithm version 10) ─────────
        //
        // ROE, ROA and margen neto reach DANGER on any net loss. Under RT 6 that loss is often only
        // "Resultados financieros y por tenencia (incluido RECPAM)" while the business itself operates at
        // a profit. RECPAM is a real economic loss, so the three ratios keep scoring it in the composite
        // above, untouched; what changes is that their FLOOR — "this company loses money" — applies only
        // when the operating result says so: not positive, or not read at all (the floor as it always
        // was). One exception keeps the floor: interest printed on its own line that consumes the whole
        // operating profit. That loss is financing, not inflation, and it is exactly what a floor is for.
        // Off (0) restores version 9. Ratios from the model's own output carry no operating result, so
        // they keep the version-9 floor too.
        bool profitabilityRuleOn = Get(cfg, "critical_floor_profitability_requires_operating_loss",
            ScoringDefaults.Get("critical_floor_profitability_requires_operating_loss")) > 0m;
        bool operatingProfitStands = ratios.OperatingResult is { } operatingResult
            && operatingResult > 0
            && !(ratios.FinancialExpenses is { } financialExpenses
                 && operatingResult - Math.Abs(financialExpenses) <= 0);

        var  profitabilityWaived = new List<string>();
        bool profitabilityRuleConsulted = false;

        // ── Acid-test floor needs a weak current ratio (algorithm version 11) ───────────
        //
        // A critical prueba ácida means the current assets lean on stock. While liquidez corriente is at or
        // above its WARN threshold that stock still covers the current liabilities, so the acid test keeps
        // scoring in the composite and keeps "Critico", but does not floor the score. The floor stays when
        // liquidez corriente is weak (strictly below warn) or was not measured at all — an unread ratio is
        // no evidence of coverage. warnLiquidez is the merged value (sector rows over base rows, after the
        // ordering fallback), the same one the liquidez partial was scored on. Zero current liabilities is
        // the best band: measured, never weak. Off (0) restores version 10.
        bool acidTestRuleOn = Get(cfg, "critical_floor_acid_test_requires_weak_current_ratio",
            ScoringDefaults.Get("critical_floor_acid_test_requires_weak_current_ratio")) > 0m;
        bool currentRatioMeasured = ratios.CurrentRatio.HasValue || liquidezAtBest;
        bool currentRatioWeak     = ratios.CurrentRatio is { } currentRatioValue
            && (decimal)currentRatioValue < warnLiquidez;

        bool acidTestWaived        = false;
        bool acidTestRuleConsulted = false;

        foreach (var (ratioKey, floorKey) in ScoringDefaults.RatioFloorKeyMap)
        {
            decimal floor = Get(cfg, floorKey, ScoringDefaults.Get(floorKey));
            criticalFloors[ratioKey] = Math.Clamp(floor, 0m, 100m);

            if (excludedRatioKeys.Contains(ratioKey)) continue;   // not measured → cannot be critical

            // Best liquidity there is (no current liabilities): never critical, whatever the trigger —
            // even a trigger configured at 0, which would otherwise make every ratio critical.
            if ((ratioKey == "liquidez_corriente" && liquidezAtBest) ||
                (ratioKey == "prueba_acida"       && pruebaAtBest))
            {
                continue;
            }

            if (partialScores[ratioKey] >= triggerThreshold)
            {
                if (profitabilityRuleOn && ScoringDefaults.ProfitabilityFloorRatios.Contains(ratioKey))
                {
                    profitabilityRuleConsulted = true;

                    // Still "Critico" in the breakdown — it IS at danger — but it does not floor the
                    // score and is not listed as a critical ratio, so no reader claims a floor from it.
                    if (operatingProfitStands)
                    {
                        profitabilityWaived.Add(ratioKey);
                        continue;
                    }
                }

                if (acidTestRuleOn && ratioKey == "prueba_acida")
                {
                    acidTestRuleConsulted = true;

                    // Same shape as the profitability waiver: still "Critico", still weighted, but no floor
                    // and not listed as a critical ratio.
                    if (currentRatioMeasured && !currentRatioWeak)
                    {
                        acidTestWaived = true;
                        continue;
                    }
                }

                criticalRatios.Add(ratioKey);
                if (floor > effectiveFloor)
                    effectiveFloor = floor;
            }
        }

        // Reporting only: the SIGN the rule read, never the figure. Null when the rule was not consulted.
        string? operatingResultSign = !profitabilityRuleConsulted || ratios.OperatingResult is not { } operatingRead
            ? null
            : operatingRead > 0 ? "positive"
            : operatingRead < 0 ? "negative"
            :                     "zero";

        // Reporting only: where the current ratio stood against its warn threshold when the acid-test rule
        // read it. Null when the rule was not consulted or the current ratio was not measured.
        string? currentRatioVsWarn = !acidTestRuleConsulted || !currentRatioMeasured
            ? null
            : currentRatioWeak ? "below" : "at_or_above";

        // A critical floor is a point on the 0-100 score scale, so a value outside that range
        // cannot be one. The composite is clamped above, but the floor is applied AFTER that clamp,
        // so an out-of-range floor would otherwise carry the final score clean off the scale
        // (critical_floor_endeudamiento = 150 read as Score 150). Clamping the floor itself — not
        // re-clamping the score afterwards — keeps AppliedFloor equal to the floor that was really
        // applied, so the `ScoreBeforeFloor < AppliedFloor` test every consumer reproduces still
        // means what it says. The floor may still RAISE the score; it can no longer raise it
        // above 100. SettingsService rejects such a value at the door; this is the second layer.
        effectiveFloor = Math.Clamp(effectiveFloor, 0m, 100m);

        if (scoreFinal < effectiveFloor)
        {
            scoreFinal = Math.Round(effectiveFloor, 2);
        }

        // ── LUFE sales-drop penalty (§7.3b, real) ──────────────────────────────
        //
        // The one adjustment that acts on the FINAL composite rather than inside a component, and a
        // sibling of the critical floor above: it runs after the floor, on whatever score the floor
        // left, and before the level is read off that score — a penalty the level ignored would be a
        // penalty the analyst never sees.
        //
        // Post-composite ON PURPOSE. LUFE is optional per tenant (§7.0b), so a weighted component
        // would force renormalisation on every tenant that does not use it, and an entity with no
        // LUFE data would have to be "excluded" the way BCRA is. As an adjustment, absence needs no
        // machinery at all: no series, no penalty, component weights untouched.
        //
        // ASYMMETRIC. Only a drop raises risk; growth and flat never move the score, so this can
        // never hand out a bonus for a good quarter — the same refusal as the cheques block, from
        // the other side. The ramp runs from `inicio` (penalty starts) down to `plenaU` (full plena):
        //
        //   v >= inicio                  → 0
        //   v <= plenaU                  → plena
        //   between                      → plena * (inicio - v) / (inicio - plenaU)
        //
        // With the shipped defaults (inicio -0.10, plenaU -0.35) a drop shallower than 10 % REAL sits
        // in the dead-band and costs nothing; past it the penalty scales to the full plena at -35 %.
        //
        // THOSE THRESHOLDS WERE RECALIBRATED AFTER §7.3b, AND THE ARITHMETIC HERE DID NOT MOVE.
        // §7.3a set them while the variation was nominal and predicted that deflating would make
        // them "stricter"; §7.3b deflated it and stricter turned out to be strong enough to be
        // indefensible. Since the real variation is (1+nominal)/D - 1, the penalty starts wherever
        // nominal growth falls short of 0.90*D - 1 and saturates at 0.65*D - 1 — so at a routine
        // 2.5 %/month (D = 1.34 over a year) the ramp now runs from about +21 % nominal growth down
        // to a -13 % nominal FALL, where before it ran from +34 % down to +1 % and handed the full
        // penalty to almost anyone not outpacing inflation. Only the two config defaults moved: the
        // ramp, the clamps and the month selection are exactly as §7.3b left them, which is why the
        // change carried no algorithm-version bump — a default is configuration, not arithmetic.
        //
        // The current numbers are a PROVISIONAL calibration on economic-principle grounds, not a
        // fitted one; the note above the lufe_ventas_* keys in ScoringDefaults carries the terms and
        // the recalibration that is owed once labelled outcome data exists.
        //
        // Both thresholds are clamped into [-1,0] and plena into [0,100] — the second defence layer
        // behind SettingsService, the same shape the floor and the cheques plena carry. A degenerate
        // config where the two thresholds meet or invert would divide by zero, so that arm collapses
        // to a step at plenaU instead.
        //
        // Reads only the stored series, the stored CPI series and the tenant configuration — never a
        // clock. The "most recent month" is the greatest period PRESENT in the data, not the month it
        // happens to be scored in, and the CPI comes off the macro payload this analysis froze, so
        // re-scoring the same payload years later yields the same penalty.

        decimal  ventasAdj       = 0m;     // points ACTUALLY applied, after the clamp
        decimal? ventasVariacion = null;   // the signed fraction used; null when no basis could be formed
        string?  ventasBase      = null;   // "interanual_3m" | "interanual" | "trimestral"; null when not evaluated
        bool?    ventasEsReal    = null;   // whether that fraction is CPI-deflated; null when not evaluated

        if (Get(cfg, "lufe_ventas_enabled", ScoringDefaults.Get("lufe_ventas_enabled")) > 0m &&
            ComputeVentasVariacion(ventasMensuales, macroData?.InflacionMensualSerie) is { } variacion)
        {
            // Reported whenever a basis was formed — including growth, which is a real reading with a
            // zero penalty, not a missing one. Only "no basis at all" stays null, exactly as the
            // cheques proportion distinguishes full recovery (a real 1) from never having looked.
            ventasVariacion = variacion.Valor;
            ventasBase      = variacion.Base;
            ventasEsReal    = variacion.EsReal;

            decimal plena  = Math.Clamp(
                Get(cfg, "lufe_ventas_penalizacion_plena", ScoringDefaults.Get("lufe_ventas_penalizacion_plena")),
                0m, 100m);
            decimal inicio = Math.Clamp(
                Get(cfg, "lufe_ventas_umbral_inicio", ScoringDefaults.Get("lufe_ventas_umbral_inicio")),
                -1m, 0m);
            decimal plenaU = Math.Clamp(
                Get(cfg, "lufe_ventas_umbral_plena", ScoringDefaults.Get("lufe_ventas_umbral_plena")),
                -1m, 0m);

            // Order matters at the boundary: the full-penalty rule is tested BEFORE the dead-band, so
            // a config whose two thresholds meet resolves to the full plena at that point rather than
            // to zero. Both rules describe it, and the stricter one is the one that was meant.
            decimal rango    = inicio - plenaU;
            decimal fraccion =
                  variacion.Valor <= plenaU ? 1m                     // at or past full penalty
                : variacion.Valor >= inicio ? 0m                     // above the start: dead-band, or growth
                : rango <= 0m               ? 1m                     // degenerate config → step at plenaU
                : Math.Clamp((inicio - variacion.Valor) / rango, 0m, 1m);

            decimal proposed = plena * fraccion;

            // Reported as the points ACTUALLY applied, exactly as chequesAdj and bcraTrendAdj are: a
            // score already at 100 — or pushed there by the floor — absorbs the whole penalty, and
            // the PDF and the chat context must not claim a penalty the score never carried.
            decimal scoreBeforeVentas = scoreFinal;
            scoreFinal = Math.Round(Math.Clamp(scoreFinal + proposed, 0m, 100m), 2);
            ventasAdj  = scoreFinal - scoreBeforeVentas;
        }

        // ── CASFOG guarantee-arrears penalty (§7.4) ────────────────────────────
        //
        // The second adjustment on the FINAL composite, and a sibling of the sales-drop block above
        // rather than a refinement of it: it runs after that one, on whatever score it left, and
        // still before the level is read off that score.
        //
        // Post-composite for the same reason, and a stronger one. CASFOG is optional per tenant
        // (§7.0b) AND meaningless to a non-SGR lender — the non-SGR preset is the only preset that
        // switches an integration off, and CASFOG is the integration it switches. A weighted
        // component would force renormalisation on every such tenant; as an adjustment, absence needs
        // no machinery: no rows, no penalty, component weights untouched.
        //
        // ASYMMETRIC. Only reported arrears raise risk; a clean guarantee record never lowers the
        // score, so this can no more hand out a bonus than the two penalties before it.
        //
        // THE SIGNAL IS A PROPORTION OF BALANCE, NOT OF ROWS. p is the sum of SaldoBrutoVigente over
        // the rows reporting CantidadEnMora > 0, over that same sum across every row carrying a
        // measurable balance. Weighting by exposure is the whole point: one arrears row at an SGR
        // holding most of the guaranteed balance is a graver fact than one at an SGR holding a
        // sliver, and counting rows would call them equal. The denominator is those rows' OWN sum and
        // deliberately not Garantias.SaldoBrutoGarantiasVigentes, the aggregate the vendor reports
        // separately: the two can disagree, and a ratio whose halves come from different places is
        // not a ratio. See ComputeProporcionEnMora for the null and non-positive handling.
        //
        // The ramp runs UPWARD from `inicio` (penalty starts) to `plenaU` (full plena) — the mirror
        // of the sales ramp, which runs downward because a drop is negative:
        //
        //   p <= inicio                  → 0
        //   p >= plenaU                  → plena
        //   between                      → plena * (p - inicio) / (plenaU - inicio)
        //
        // With the shipped defaults (inicio 0.05, plenaU 0.40) a portfolio with under 5 % of its
        // balance in arrears sits in the dead-band and costs nothing — that much is ordinary
        // operational friction in a guarantee book, one late instalment at one SGR — and the penalty
        // scales to the full 12 points at 40 %. Those numbers are a PROVISIONAL calibration on
        // economic-principle grounds; the note above the casfog_mora_* keys in ScoringDefaults
        // carries the terms and the recalibration that is owed once labelled outcome data exists.
        //
        // Both thresholds are clamped into [0,1] and plena into [0,100] — the second defence layer
        // behind SettingsService, the same shape the floor and the other two plenas carry. A
        // degenerate config where the two thresholds meet or invert would divide by zero, so that arm
        // collapses to a step at plenaU instead.
        //
        // Reads only the stored snapshot and the tenant configuration — never a clock, and never the
        // CASFOG SituacionDeuda scale, which is the vendor's own 0-10 classification and must not be
        // mixed with the BCRA 1-6 situación this engine uses elsewhere. Re-scoring the same payload
        // years later yields the same penalty.

        decimal  casfogMoraAdj        = 0m;     // points ACTUALLY applied, after the clamp
        decimal? casfogProporcionMora = null;   // the share used; null when it could not be formed

        if (Get(cfg, "casfog_mora_enabled", ScoringDefaults.Get("casfog_mora_enabled")) > 0m &&
            ComputeProporcionEnMora(garantiasPorSgr) is { } proporcionMora)
        {
            // Reported whenever a proportion could be formed — including a clean 0, which is a real
            // reading with a zero penalty, not a missing one. Only "no measurable balance at all"
            // stays null, exactly as the cheques proportion distinguishes full recovery (a real 1)
            // from never having looked.
            casfogProporcionMora = proporcionMora;

            decimal plena  = Math.Clamp(
                Get(cfg, "casfog_mora_penalizacion_plena", ScoringDefaults.Get("casfog_mora_penalizacion_plena")),
                0m, 100m);
            decimal inicio = Math.Clamp(
                Get(cfg, "casfog_mora_umbral_inicio", ScoringDefaults.Get("casfog_mora_umbral_inicio")),
                0m, 1m);
            decimal plenaU = Math.Clamp(
                Get(cfg, "casfog_mora_umbral_plena", ScoringDefaults.Get("casfog_mora_umbral_plena")),
                0m, 1m);

            // Order matters at the boundary, as in the sales block: the full-penalty rule is tested
            // BEFORE the dead-band, so a config whose two thresholds meet resolves to the full plena
            // at that point rather than to zero. Both rules describe it, and the stricter one is the
            // one that was meant.
            decimal rango    = plenaU - inicio;
            decimal fraccion =
                  proporcionMora >= plenaU ? 1m                     // at or past full penalty
                : proporcionMora <= inicio ? 0m                     // below the start: dead-band
                : rango <= 0m              ? 1m                     // degenerate config → step at plenaU
                : Math.Clamp((proporcionMora - inicio) / rango, 0m, 1m);

            decimal proposed = plena * fraccion;

            // Reported as the points ACTUALLY applied, exactly as ventasAdj and chequesAdj are: a
            // score already at 100 — by the floor, or by the sales penalty just above — absorbs the
            // whole penalty, and the PDF and the chat context must not claim a penalty the score
            // never carried.
            decimal scoreBeforeCasfog = scoreFinal;
            scoreFinal    = Math.Round(Math.Clamp(scoreFinal + proposed, 0m, 100m), 2);
            casfogMoraAdj = scoreFinal - scoreBeforeCasfog;
        }

        // ── Level ─────────────────────────────────────────────────────────────

        // The one resolution rule for the level cutoffs (ScoreLevelCutoffs.Resolve): the same values
        // classify the score here and are recorded in the breakdown as levelCutoffs, so a reader can
        // label this row's level and range with the numbers that actually produced it.
        var levelCutoffs = ScoreLevelCutoffs.Resolve(cfg);
        var level        = levelCutoffs.Classify(scoreFinal);

        // ── Ratio breakdown ─────────────────────────────────────────────────────
        //
        // Lists all eight indicators. A non-computable ratio excluded on the renormalise path is
        // marked Excluded=true with EffectiveWeight=0 (distinguishable from a genuine 50, which the
        // Score alone is not); survivors carry their renormalised share (summing to 1). On the
        // all-computable and fallback paths every entry keeps its nominal weight and Excluded=false.

        var breakdown = new Dictionary<string, RatioBreakdownEntry>(ratioItems.Length);
        foreach (var item in ratioItems)
        {
            bool    excluded  = renormaliseRatios && !item.Computable;
            decimal effWeight = renormaliseRatios
                ? (item.Computable ? item.Weight / survivingWeightSum : 0m)
                : item.Weight;

            // Reporting only: why an excluded ratio was not computed. Nothing above reads it, so it
            // moves no score (no ScoringAlgoVersion bump). Null when unknown (model-supplied ratios).
            // Since version 10 a NON-excluded entry can carry one value here too: the no-current-
            // liabilities marker of a ratio scored at the best band, so every reader can say why a
            // measured ratio has no value.
            bool atBest = (item.Key == "liquidez_corriente" && liquidezAtBest) ||
                          (item.Key == "prueba_acida"       && pruebaAtBest);
            string? exclusionReason = atBest
                ? RatioExclusionReasons.NoCurrentLiabilities
                : excluded && ratios.ExclusionReasons is { } reasons
                  && reasons.TryGetValue(item.Key, out var reason)
                    ? reason
                    : null;

            // Reporting only (1b-2a, S1): on the low-coverage fallback a non-computable ratio is NOT
            // excluded — it scores the neutral 50 at full weight — so the entry alone reads like a
            // measured "Atencion". measured:false says it was not. Null (omitted) everywhere else, which
            // keeps the all-computable and renormalise paths byte-identical to before.
            bool? measured = ratiosLowCoverageFallback && !item.Computable ? false : null;

            breakdown[item.Key] = new RatioBreakdownEntry(
                Math.Round(item.Score, 2),
                RatioStatus(item.Score, triggerThreshold),
                excluded,
                Math.Round(effWeight, 4),
                exclusionReason,
                measured);
        }

        // ── Result ────────────────────────────────────────────────────────────

        return new ScoringResult
        {
            Score            = scoreFinal,
            Level            = level,
            ScoreRatios      = Math.Round(scoreRatios, 2),
            ScoreBcra        = bcraExcluded ? 0m : Math.Round(scoreBcra,   2),
            ScoreMacro       = Math.Round(scoreMacro,  2),
            Breakdown        = breakdown,
            CriticalRatios   = criticalRatios,
            AppliedFloor     = effectiveFloor,
            ScoreBeforeFloor = scoreBeforeFloor,
            CriticalFloorTrigger = triggerThreshold,
            CriticalFloors       = criticalFloors,
            ThresholdFallbacks   = thresholdFallbacks,
            BcraTrendDelta        = bcraTrendDelta,
            BcraTrendAdj          = Math.Round(bcraTrendAdj, 2),
            ChequesAdj            = Math.Round(chequesAdj, 2),
            ChequesProporcionRecuperada = chequesProporcionRecuperada is { } proporcionRecuperada
                ? Math.Round(proporcionRecuperada, 4)
                : null,
            ChequesEvaluated      = chequesEvaluated,
            VentasAdj             = Math.Round(ventasAdj, 2),
            VentasVariacion       = ventasVariacion is { } variacionValor
                ? Math.Round(variacionValor, 4)
                : null,
            VentasBase            = ventasBase,
            VentasVariacionEsReal = ventasEsReal,
            CasfogMoraAdj         = Math.Round(casfogMoraAdj, 2),
            CasfogProporcionMora  = casfogProporcionMora is { } proporcionMoraValor
                ? Math.Round(proporcionMoraValor, 4)
                : null,
            BcraExcluded          = bcraExcluded,
            BcraExclusionReason   = bcraExclusionReason,
            EffectiveWeightRatios = Math.Round(effWRatios, 4),
            EffectiveWeightBcra   = Math.Round(effWBcra,   4),
            EffectiveWeightMacro  = Math.Round(effWMacro,  4),
            NonComputableRatios       = nonComputableRatios,
            RatiosLowCoverageFallback = ratiosLowCoverageFallback,
            RatiosSurvivingWeight     = Math.Round(survivingWeightSum, 4),
            ProfitabilityFloorWaived       = profitabilityWaived.Count > 0,
            ProfitabilityFloorWaivedRatios = profitabilityWaived,
            OperatingResultSign            = operatingResultSign,
            AcidTestFloorWaived            = acidTestWaived,
            CurrentRatioVsWarn             = currentRatioVsWarn,
            SectorProfile         = canonicalSector,
            LevelCutoffs          = levelCutoffs,
            ScoringAlgoVersion    = ScoringService.ScoringAlgoVersion,
            // Reliability facts (1b-2a) — reporting only, read by no arithmetic above.
            BcraSituacionUnreadable = bcraSituacionUnreadable,
            MacroImputed            = macroData?.InflacionMensual is null,
            NegativeEquity          = NegativeEquityFrom(ratios.ExclusionReasons)
        };
    }

    /// <summary>
    /// S11: whether equity &lt;= 0 made leverage or ROE meaningless. RatioCalculationService marks
    /// those two — and only those two — <c>not_meaningful</c> exactly when equity is read and not
    /// positive (every other operand read), so the reason IS the fact. Null when no reasons are known
    /// (ratios taken from the model's own output): the fact cannot be established then.
    /// </summary>
    private static bool? NegativeEquityFrom(IReadOnlyDictionary<string, string>? reasons)
    {
        if (reasons is null)
            return null;

        return IsNotMeaningful("leverage") || IsNotMeaningful("roe");

        bool IsNotMeaningful(string key)
            => reasons.TryGetValue(key, out var reason)
               && string.Equals(reason, RatioExclusionReasons.NotMeaningful, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="ratioKey"/> carries the no-current-liabilities marker
    /// (<see cref="RatioExclusionReasons.NoCurrentLiabilities"/>) and has no value — the case version 10
    /// scores at the best band. False for model-supplied ratios, which carry no reasons.
    /// </summary>
    private static bool HasNoCurrentLiabilities(RatiosData ratios, string ratioKey)
    {
        double? value = ratioKey == "liquidez_corriente" ? ratios.CurrentRatio : ratios.AcidTest;

        return value is null
            && ratios.ExclusionReasons is { } reasons
            && reasons.TryGetValue(ratioKey, out var reason)
            && string.Equals(reason, RatioExclusionReasons.NoCurrentLiabilities, StringComparison.Ordinal);
    }

    // ── LUFE sales variation (§7.3a nominal, §7.3b real) ──────────────────────

    /// <summary>
    /// The variation the sales-drop penalty scores, which of the three bases produced it, and whether
    /// it was actually deflated. <paramref name="Valor"/> is a signed fraction: -0.20 is a 20 % fall,
    /// +0.05 a 5 % rise. <paramref name="EsReal"/> is true when <paramref name="Valor"/> is in REAL
    /// terms (CPI-deflated) and false when the CPI series could not cover the basis and the figure is
    /// the nominal one — a distinction the analyst is shown, never silently absorbed.
    /// </summary>
    // Internal (with ComputeVentasVariacion) so the PDF can state the companion figure of the SAME window the
    // score used — the nominal variation beside a real one — through this arithmetic, never a copy of it.
    internal readonly record struct VentasVariacionResult(decimal Valor, string Base, bool EsReal);

    /// <summary>
    /// Computes the signed variation of LUFE-reported monthly sales, or <c>null</c> when no basis can
    /// be formed from the series.
    /// <para>
    /// THIS WAS THE PHASE B SEAM, and §7.3b has now been substituted into it. The figures are
    /// deflated by the monthly CPI series before being compared, so the result is a REAL variation;
    /// the penalty ramp, the configuration keys, the reported fields and every caller are unchanged,
    /// exactly as §7.3a planned. Keep any further substitution confined here too.
    /// </para>
    /// <para>
    /// DEFLATION CHANGES WHICH NUMBERS ARE COMPARED, NEVER WHICH MONTHS ARE CHOSEN. The selection
    /// below never consults the CPI series; the deflator is applied afterwards to whatever basis it
    /// produced. In particular a CPI series that cannot cover an interanual span does NOT cause a
    /// fallback basis to be tried instead — that would let the availability of an
    /// auxiliary series decide which question the score answers.
    /// </para>
    /// <para>
    /// Preferred basis, since version 9, is INTERANUAL_3M: the three calendar months ending at the most
    /// recent month carrying sales, summed, against the same three months twelve earlier. All six must
    /// carry sales. A single month against a single month let one lumpy month decide the whole signal —
    /// a fleet sale in the base month reads as a collapse today — and summing a quarter on each side
    /// dilutes that without giving up the year-on-year alignment that neutralises seasonality.
    /// </para>
    /// <para>
    /// When any of those six is missing the basis falls back to single-month INTERANUAL: the most recent
    /// month carrying sales against the same calendar month twelve earlier. Both interanual bases are
    /// matched through <see cref="DeflactorIpc.ShiftPeriodoMonths"/> on the "YYYYMM" key rather than by position —
    /// the series is ragged (a month with nothing to declare is omitted entirely), so counting back
    /// twelve entries would silently compare the wrong months. When that pair cannot be formed either,
    /// the basis falls back to TRIMESTRAL: the three most recent months carrying sales against the
    /// three before them. The two fallbacks compute exactly what version 8 computed.
    /// </para>
    /// <para>
    /// A month is a datum only when <c>Ventas</c> is non-null: LUFE's own contract is that null means
    /// "no declaration filed for this month", NOT zero sales, and a month can be present carrying
    /// only <c>Compras</c>. Reading those nulls as zeroes would manufacture a total collapse in sales
    /// out of a filing gap — the same refusal to invent risk from absent data the cheques block makes.
    /// A non-positive comparison base is treated as no basis for its own reason: it is the divisor,
    /// and a fraction against zero sales says nothing about a trend.
    /// </para>
    /// <para>
    /// Reads no clock and does not assume the stored order: periods are selected by key, and the
    /// "most recent" month is the greatest period present. A blob written in any order, or by an
    /// older code path, scores identically. The CPI series is read from the analysis's own frozen
    /// macro snapshot for the same reason, so re-scoring one payload always yields one answer.
    /// </para>
    /// </summary>
    /// <param name="serie">The LUFE monthly sales series, as stored.</param>
    /// <param name="ipcMensual">
    /// Monthly inflation keyed "YYYYMM", as a PERCENT (2.5 = 2.5 %) — <c>BcraMacroData.InflacionMensualSerie</c>,
    /// off the same macro payload the analysis freezes. <c>null</c> or short of the months a basis
    /// needs means the variation is reported nominally, never that the signal disappears.
    /// </param>
    internal static VentasVariacionResult? ComputeVentasVariacion(
        List<LufeVentaCompraMes>?            serie,
        IReadOnlyDictionary<string, double>? ipcMensual)
    {
        if (serie is null || serie.Count == 0) return null;

        // Keep only months that actually carry a sales figure under a well-formed period key, and
        // de-duplicate defensively: nothing re-sorts or re-validates a persisted blob on read.
        var meses = new SortedDictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var mes in serie)
        {
            // A property pattern rather than two checks: it rejects a null entry (a stored blob can
            // hold one) and a month with no sales declaration in the same step, and binds the amount.
            if (mes is not { Ventas: { } ventas }) continue;
            if (DeflactorIpc.ShiftPeriodoMonths(mes.Periodo, 0) is not { } periodo) continue;   // validates the key

            meses[periodo] = ventas;
        }

        if (meses.Count == 0) return null;

        var periodos = meses.Keys.ToArray();
        var ultimo   = periodos[^1];

        // ── Interanual, three months (version 9) ──────────────────────────────
        if (VentasInteranualTresMeses(meses, ultimo) is { } tresMeses)
        {
            // PER-PAIR deflation first: each month t against t−12 with ITS OWN twelve-month deflator, and
            // only then summed. The three pairs are separated by three different, staggered windows of
            // twelve readings, and one span standing in for all three is only a small error while
            // monthly CPI is stable — across a disinflation it is not: a window spanning Dec-2023 /
            // Jan-2024 (25.5 % and 20.6 % a month) against the 2-3 % a month a year later mis-deflates
            // the two older pairs by roughly 20 % and 45 %.
            if (DeflactorIpc.VariacionRealPorPares(tresMeses.Pares, ipcMensual) is { } real)
                return new VentasVariacionResult(real, "interanual_3m", EsReal: true);

            // FALLBACK when the series cannot cover all three pairs (it needs readings back to
            // ultimo−13): ONE deflator over the endpoint span, ultimo−12 → ultimo — the same span, and
            // the same CPI coverage, as the single-month basis. Deflactar reports whether even that
            // span was covered: EsReal true with the approximate single deflator, false (nominal) when
            // not. The BASIS is never reconsidered for want of CPI, exactly as before.
            return Deflactar(tresMeses.Nominal, tresMeses.Desde, ultimo, ipcMensual, "interanual_3m");
        }

        // ── Interanual, single month ──────────────────────────────────────────
        if (DeflactorIpc.ShiftPeriodoMonths(ultimo, -12) is { } haceUnAnio &&
            meses.TryGetValue(haceUnAnio, out var ventasBase) &&
            ventasBase > 0m)
        {
            // The whole span is deflated at once: dividing the year-on-year ratio by the accumulated
            // inflation between the two months is the same thing as restating the recent month in the
            // comparison month's pesos before dividing, and it needs only the two endpoints.
            return Deflactar(
                (meses[ultimo] - ventasBase) / ventasBase,
                haceUnAnio, ultimo, ipcMensual, "interanual");
        }

        // ── Trimestral fallback ───────────────────────────────────────────────
        //
        // Six months carrying sales are needed for two full quarters. They need not be contiguous:
        // the quarter is "the three most recent months present", which is the most the series can
        // honestly support when months are missing.
        if (periodos.Length < 6) return null;

        decimal reciente = 0m;
        decimal previo   = 0m;

        for (int i = periodos.Length - 3; i < periodos.Length; i++)
            reciente += meses[periodos[i]];

        for (int i = periodos.Length - 6; i < periodos.Length - 3; i++)
            previo += meses[periodos[i]];

        if (previo <= 0m) return null;

        // ONE deflator for the whole quarter, spanning the most recent month of the previous quarter
        // to the most recent month of this one — NOT each month restated individually. Two reasons,
        // and they point the same way. This basis is already the approximate path (it exists only
        // because the year-earlier month was missing, and its "quarters" are the three most recent
        // months PRESENT, which need not be contiguous), so per-month precision would be false
        // precision on top of an approximation. And per-month deflation would need CPI coverage all
        // the way back to periodos[^6] instead of periodos[^4], making the fallback fail for want of
        // data more often than the basis it is standing in for.
        return Deflactar(
            (reciente - previo) / previo,
            periodos[^4], periodos[^1], ipcMensual, "trimestral");
    }

    /// <summary>
    /// The three month pairs of the three calendar months ending at <paramref name="ultimo"/> against the
    /// same three months twelve earlier, their nominal variation, and the month the fallback deflator must
    /// start from (<paramref name="ultimo"/> minus twelve) — or <c>null</c> when the basis cannot be formed.
    /// <para>
    /// All six months must be present in <paramref name="meses"/>, which holds only months carrying a
    /// sales figure: a missing month is a filing gap, not zero sales, and a sum over a gap would read the
    /// gap as a collapse. The months are CALENDAR months matched by key, not the three most recent present
    /// — that looser reading belongs to the trimestral fallback. A non-positive base sum is no basis, for
    /// the same reason the single-month basis refuses one: it is the divisor.
    /// </para>
    /// </summary>
    private static (decimal Nominal, string Desde, DeflactorIpc.ParInteranual[] Pares)? VentasInteranualTresMeses(
        SortedDictionary<string, decimal> meses,
        string                            ultimo)
    {
        if (DeflactorIpc.ShiftPeriodoMonths(ultimo, -12) is not { } desde) return null;

        var     pares    = new DeflactorIpc.ParInteranual[3];
        decimal reciente = 0m;
        decimal previo   = 0m;

        for (int k = 0; k < 3; k++)
        {
            if (DeflactorIpc.ShiftPeriodoMonths(ultimo, -k)      is not { } mes      ||
                DeflactorIpc.ShiftPeriodoMonths(ultimo, -k - 12) is not { } mesPrevio ||
                !meses.TryGetValue(mes, out var ventasMes)               ||
                !meses.TryGetValue(mesPrevio, out var ventasMesPrevio))
            {
                return null;
            }

            pares[k]  = new DeflactorIpc.ParInteranual(mes, ventasMes, mesPrevio, ventasMesPrevio);
            reciente += ventasMes;
            previo   += ventasMesPrevio;
        }

        if (previo <= 0m) return null;

        return ((reciente - previo) / previo, desde, pares);
    }

    /// <summary>
    /// Restates a nominal variation in real terms using the accumulated inflation between the two
    /// months it compares, or reports the nominal figure when the CPI series cannot cover that span.
    /// <para>
    /// The fallback is REPORTED, never silent: a real sales drop is information, and losing the whole
    /// signal because a macro auxiliary was missing would be worse than measuring it nominally. The
    /// basis is already decided by the time this runs and is never reconsidered here.
    /// </para>
    /// <para>
    /// Note the direction, because it is the point of §7.3b: with positive inflation the deflator is
    /// above 1, so the real variation is always at or below the nominal one. Deflating can only
    /// reveal a drop, never erase one — a nominal rise that fails to keep up with prices becomes the
    /// drop it really was.
    /// </para>
    /// </summary>
    private static VentasVariacionResult Deflactar(
        decimal                              nominal,
        string                               desde,
        string                               hasta,
        IReadOnlyDictionary<string, double>? ipcMensual,
        string                               basis)
    {
        if (DeflactorIpc.AcumularInflacion(ipcMensual, desde, hasta) is { } deflactor)
            return new VentasVariacionResult(((1m + nominal) / deflactor) - 1m, basis, EsReal: true);

        return new VentasVariacionResult(nominal, basis, EsReal: false);
    }

    // ── CASFOG guarantee arrears (§7.4) ───────────────────────────────────────

    /// <summary>
    /// The share of the guaranteed balance the SGRs report IN ARREARS, in [0,1], or <c>null</c> when
    /// no such share can be formed.
    /// <para>
    /// Weighted by EXPOSURE: the numerator sums <c>SaldoBrutoVigente</c> over the rows whose
    /// <c>CantidadEnMora</c> is greater than zero, the denominator over every row carrying a
    /// measurable balance. A row joins NEITHER sum unless its balance is strictly positive — one rule
    /// doing two jobs. A null balance is the vendor's <c>"-"</c> sentinel, i.e. exposure that cannot
    /// be measured, and inventing a zero for it would understate the share; a zero or negative balance
    /// is not exposure either, and letting a negative into the denominator could zero it or invert the
    /// ratio.
    /// </para>
    /// <para>
    /// A null <c>CantidadEnMora</c> means the count did not parse, NOT that the SGR reported arrears:
    /// absence of the count never invents risk, so such a row counts toward the denominator only.
    /// <c>SituacionDeuda</c> and <c>DiasAtraso</c> are deliberately not consulted — the count is the
    /// direct fact, and the CASFOG situación is a 0-10 vendor scale that must never be mixed with the
    /// BCRA 1-6 one used elsewhere in this engine.
    /// </para>
    /// <para>
    /// <c>null</c> when there are no rows or none carries a measurable balance: the share is then
    /// undefined, not zero, and an undefined share is reported as such rather than as a clean bill of
    /// health. The result is clamped into [0,1] defensively — mixed-sign inputs cannot reach it past
    /// the positive-balance filter, but the clamp costs nothing and the reported value must be the one
    /// the ramp actually used.
    /// </para>
    /// </summary>
    private static decimal? ComputeProporcionEnMora(List<CasfogGarantiaSgr>? garantiasPorSgr)
    {
        if (garantiasPorSgr is null || garantiasPorSgr.Count == 0)
            return null;

        decimal saldoTotal  = 0m;
        decimal saldoEnMora = 0m;

        try
        {
            foreach (var fila in garantiasPorSgr)
            {
                if (fila?.SaldoBrutoVigente is not { } saldo || saldo <= 0m)
                    continue;

                saldoTotal += saldo;

                if (fila.CantidadEnMora is > 0)
                    saldoEnMora += saldo;
            }
        }
        catch (OverflowException)
        {
            // decimal addition THROWS on overflow, and CalculateAsync's catch-all would turn that into
            // a neutral 50 — discarding an otherwise good score over one corrupt vendor figure. A
            // balance that cannot be summed is a balance that cannot be measured: no penalty.
            return null;
        }

        if (saldoTotal <= 0m)
            return null;

        return Math.Clamp(saldoEnMora / saldoTotal, 0m, 1m);
    }

    // ── Per-ratio status label ────────────────────────────────────────────────

    private static string RatioStatus(decimal partialScore, decimal triggerThreshold) =>
        partialScore < 50m              ? "Saludable" :
        partialScore < triggerThreshold  ? "Atencion"  :
                                           "Critico";

    // ── Linear interpolation between three anchors ───────────────────────────

    /// <summary>
    /// Returns a partial risk score (0–100) by linearly interpolating the given
    /// <paramref name="value"/> between three threshold anchors:
    /// ok→0, warn→50, danger→100.
    /// The direction (higher-is-better vs lower-is-better) is inferred automatically:
    /// if <paramref name="thresholdOk"/> &gt; <paramref name="thresholdDanger"/>
    /// then higher values are better (e.g. liquidity ratios);
    /// otherwise lower values are better (e.g. debt ratios).
    /// Returns 50 (neutral) when <paramref name="value"/> is null.
    /// </summary>
    private static decimal PartialScore(
        double?  value,
        decimal  thresholdOk,
        decimal  thresholdWarn,
        decimal  thresholdDanger)
    {
        if (value is null) return 50m;

        return PartialScoreAt((decimal)value.Value, thresholdOk, thresholdWarn, thresholdDanger);
    }

    /// <summary>
    /// The interpolation itself, on a known value. Split out of <see cref="PartialScore"/> (algorithm
    /// version 10) so the best band can be read by feeding the OK threshold back in, exactly in decimal:
    /// the "at or beyond OK" branch is the single definition of the best partial score.
    /// </summary>
    private static decimal PartialScoreAt(
        decimal  v,
        decimal  thresholdOk,
        decimal  thresholdWarn,
        decimal  thresholdDanger)
    {
        if (thresholdOk > thresholdDanger)
        {
            // Higher is better: ok > warn > danger
            if (v >= thresholdOk)     return 0m;
            if (v <= thresholdDanger) return 100m;
            if (v >= thresholdWarn)
                return (thresholdOk - v) / (thresholdOk - thresholdWarn) * 50m;
            else
                return 50m + (thresholdWarn - v) / (thresholdWarn - thresholdDanger) * 50m;
        }
        else
        {
            // Lower is better: ok < warn < danger
            if (v <= thresholdOk)     return 0m;
            if (v >= thresholdDanger) return 100m;
            if (v <= thresholdWarn)
                return (v - thresholdOk) / (thresholdWarn - thresholdOk) * 50m;
            else
                return 50m + (v - thresholdWarn) / (thresholdDanger - thresholdWarn) * 50m;
        }
    }
}
