// Project : Api.Web.FinanceIA
// Created : 2026-06-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Single source of truth for the scoring-engine default parameters — read <see cref="Entries"/> for the
/// current key set rather than trusting a count written here.
/// Consumed by <c>ScoringService</c> (fallback when tenant has no DB override)
/// and <c>SettingsService</c> (merge defaults for the Settings UI, plus range validation).
/// </summary>
public static class ScoringDefaults
{
    private static readonly Dictionary<string, (decimal Value, string Description)> Entries = new()
    {
        // Ratio weights
        ["weight_liquidez_corriente"]  = (0.20m, "Weight for current ratio (liquidez corriente)"),
        ["weight_prueba_acida"]        = (0.10m, "Weight for acid test (prueba acida)"),
        ["weight_endeudamiento"]       = (0.20m, "Weight for debt ratio (endeudamiento)"),
        ["weight_leverage"]            = (0.10m, "Weight for leverage"),
        ["weight_cobertura_intereses"] = (0.15m, "Weight for interest coverage (cobertura de intereses)"),
        ["weight_roe"]                 = (0.10m, "Weight for ROE"),
        ["weight_roa"]                 = (0.08m, "Weight for ROA"),
        ["weight_margen_neto"]         = (0.07m, "Weight for net margin (margen neto)"),

        // Ratio thresholds — liquidez corriente
        ["threshold_liquidez_corriente_ok"]     = (1.50m, "Current ratio — OK threshold (higher is better)"),
        ["threshold_liquidez_corriente_warn"]   = (1.00m, "Current ratio — warning threshold"),
        ["threshold_liquidez_corriente_danger"] = (0.70m, "Current ratio — danger threshold"),

        // Ratio thresholds — prueba acida
        ["threshold_prueba_acida_ok"]     = (1.00m, "Acid test — OK threshold (higher is better)"),
        ["threshold_prueba_acida_warn"]   = (0.70m, "Acid test — warning threshold"),
        ["threshold_prueba_acida_danger"] = (0.50m, "Acid test — danger threshold"),

        // Ratio thresholds — endeudamiento
        ["threshold_endeudamiento_ok"]     = (0.50m, "Debt ratio — OK threshold (lower is better)"),
        ["threshold_endeudamiento_warn"]   = (0.70m, "Debt ratio — warning threshold"),
        ["threshold_endeudamiento_danger"] = (0.85m, "Debt ratio — danger threshold"),

        // Ratio thresholds — leverage (algorithm version 9).
        //
        // Leverage (pasivo / patrimonio neto) and endeudamiento (pasivo / activo) are the SAME quantity
        // on two scales: with activo = pasivo + PN, L = E / (1 − E). Their thresholds must therefore be
        // the same points, or one balance sheet reads "Atención" on one ratio and "Crítico" on the
        // other. The endeudamiento anchors 0.50 / 0.70 / 0.85 map to L = 1.00 / 2.333… / 5.666…,
        // rounded here to 2dp. Until version 9 leverage shipped 1.00 / 1.50 / 2.50 — the equivalent of
        // endeudamiento 0.50 / 0.60 / 0.71 — so a firm at endeudamiento 0.70 ("Atención") scored its
        // leverage 2.29 at partial 89.5 and tripped the critical floor. Weights are unchanged.
        ["threshold_leverage_ok"]     = (1.00m, "Leverage — OK threshold (lower is better); equivalent to endeudamiento 0.50"),
        ["threshold_leverage_warn"]   = (2.33m, "Leverage — warning threshold; equivalent to endeudamiento 0.70"),
        ["threshold_leverage_danger"] = (5.67m, "Leverage — danger threshold; equivalent to endeudamiento 0.85"),

        // Ratio thresholds — cobertura de intereses
        ["threshold_cobertura_intereses_ok"]     = (3.00m, "Interest coverage — OK threshold (higher is better)"),
        ["threshold_cobertura_intereses_warn"]   = (1.50m, "Interest coverage — warning threshold"),
        ["threshold_cobertura_intereses_danger"] = (1.00m, "Interest coverage — danger threshold"),

        // Ratio thresholds — ROE
        ["threshold_roe_ok"]     = (15.00m, "ROE — OK threshold (higher is better)"),
        ["threshold_roe_warn"]   = ( 5.00m, "ROE — warning threshold"),
        ["threshold_roe_danger"] = ( 0.00m, "ROE — danger threshold"),

        // Ratio thresholds — ROA
        ["threshold_roa_ok"]     = (8.00m, "ROA — OK threshold (higher is better)"),
        ["threshold_roa_warn"]   = (3.00m, "ROA — warning threshold"),
        ["threshold_roa_danger"] = (0.00m, "ROA — danger threshold"),

        // Ratio thresholds — margen neto
        ["threshold_margen_neto_ok"]     = (10.00m, "Net margin — OK threshold (higher is better)"),
        ["threshold_margen_neto_warn"]   = ( 4.00m, "Net margin — warning threshold"),
        ["threshold_margen_neto_danger"] = ( 0.00m, "Net margin — danger threshold"),

        // Component weights
        ["weight_component_ratios"] = (0.50m, "Weight of the ratios component in the final score"),
        ["weight_component_bcra"]   = (0.35m, "Weight of the BCRA component in the final score"),
        ["weight_component_macro"]  = (0.15m, "Weight of the macro component in the final score"),

        // Score thresholds
        ["score_threshold_low"]    = (35m, "Score at or below this value is classified as Low risk"),
        ["score_threshold_medium"] = (60m, "Score at or below this value is classified as Medium risk"),
        ["score_threshold_high"]   = (80m, "Score at or below this value is classified as High risk"),

        // Macro parameters
        ["macro_inflacion_penalizacion_threshold"] = (10.00m, "Monthly inflation (%) above which a penalty is applied"),
        ["macro_penalizacion_puntos"]              = ( 5.00m, "Penalty points added when inflation exceeds threshold"),

        // Critical-ratio floor (Feature 5a). A ratio is critical when its partial score REACHES the
        // trigger (>=), and PartialScore returns exactly 100 at or beyond the ratio's DANGER threshold —
        // so the default 100 means "critical only at DANGER" (algorithm version 9). It was 85, which
        // fired partway between WARN and DANGER: ROE was critical below 1.5 % rather than at 0, and a
        // leverage short of its own DANGER still floored the score. The per-ratio floors are unchanged.
        ["critical_floor_trigger_threshold"]   = (100.00m, "Partial score at or above which a ratio is considered critical; 100 = the ratio is at or beyond its danger threshold"),
        ["critical_floor_liquidez_corriente"]  = (60.00m, "Minimum final score floor when current ratio is critical"),
        // 60 until algorithm version 11, which lowered it to 50 and made it conditional on a weak current
        // ratio (critical_floor_acid_test_requires_weak_current_ratio, below).
        ["critical_floor_prueba_acida"]        = (50.00m, "Minimum final score floor when acid test is critical (since v11 only while the current ratio is below its warning threshold)"),
        ["critical_floor_endeudamiento"]       = (65.00m, "Minimum final score floor when debt ratio is critical"),
        ["critical_floor_leverage"]            = (65.00m, "Minimum final score floor when leverage is critical"),
        ["critical_floor_cobertura_intereses"] = (70.00m, "Minimum final score floor when interest coverage is critical"),
        ["critical_floor_roe"]                 = (55.00m, "Minimum final score floor when ROE is critical"),
        ["critical_floor_roa"]                 = (55.00m, "Minimum final score floor when ROA is critical"),
        ["critical_floor_margen_neto"]         = (55.00m, "Minimum final score floor when net margin is critical"),

        // Profitability floor needs an operating loss (algorithm version 10). Under RT 6 a net loss is
        // often only "Resultados financieros y por tenencia (incluido RECPAM)" while operations are
        // profitable; RECPAM is a real economic loss, so ROE / ROA / margen neto keep scoring it in the
        // composite, but on (1) their FLOOR applies only when operating_result is not positive — or when
        // the interest printed on its own line consumes it. 0 restores the version-9 floor. A 0/1 flag
        // stored as a decimal, like bcra_cheques_enabled. Validated with the SGR (03/11).
        ["critical_floor_profitability_requires_operating_loss"] = (1.0000m, "El piso por ROE, ROA o margen neto críticos se aplica sólo si el resultado operativo no es positivo, o si los intereses lo absorben (1 = sí, 0 = no: piso ante cualquier pérdida)"),

        // Acid-test floor needs a weak current ratio (algorithm version 11). A critical prueba ácida says
        // much of the current assets is stock; when liquidez corriente is at or above its WARN threshold
        // that stock still covers the current liabilities, so on (1) the acid test keeps its "Critico"
        // status and its weight in the composite but floors the score only while liquidez corriente is
        // measured and strictly below its (sector-merged) warn threshold. An unread current ratio keeps
        // the floor. 0 restores version 10. A 0/1 flag stored as a decimal, like the one above.
        ["critical_floor_acid_test_requires_weak_current_ratio"] = (1.0000m, "El piso por prueba ácida crítica se aplica sólo si la liquidez corriente está por debajo de su umbral de atención o no se leyó (1 = sí, 0 = no: piso ante cualquier prueba ácida crítica)"),

        // BCRA trend (Feature 5c)
        ["bcra_trend_window_months"]        = (6.0000m, "Ventana de meses para calcular la tendencia BCRA"),
        ["bcra_trend_pts_per_step_improve"] = (5.0000m, "Puntos a reducir del score BCRA por escalón de mejora"),
        ["bcra_trend_pts_per_step_worsen"]  = (8.0000m, "Puntos a sumar al score BCRA por escalón de empeoramiento"),
        ["bcra_trend_max_improve_adj"]      = (20.0000m, "Máximo ajuste por mejora (puntos a descontar)"),
        ["bcra_trend_max_worsen_adj"]       = (30.0000m, "Máximo ajuste por empeoramiento (puntos a sumar)"),

        // BCRA cheques rechazados penalty (§7.1). Points on the 0-100 scoreBcra scale, added AFTER the
        // trend adjustment: chequesAdj = plena * (1 - proporcionRecuperada), recovery measured by amount.
        ["bcra_cheques_penalizacion_plena"] = (15.0000m, "Penalización plena por cheques rechazados: puntos sumados al score BCRA cuando no se recuperó nada del importe rechazado (escala 0-100)"),
        ["bcra_cheques_enabled"]            = ( 1.0000m, "Aplica la penalización por cheques rechazados al score BCRA (1 = sí, 0 = no)"),

        // LUFE sales-drop penalty (§7.3a, §7.3b). Points on the 0-100 FINAL score scale, applied
        // AFTER the critical floor: ventasAdj = plena * fracción(v), where v is the signed variation
        // of the monthly sales LUFE reports. Since §7.3b (algorithm version 7) v is measured in REAL
        // terms — the figures are deflated by the BCRA monthly-inflation series before being
        // compared — falling back to nominal, and saying so, only when that series cannot cover the
        // months being compared.
        //
        // The ramp these three describe: nothing up to a 10 % REAL drop, then linearly to the full
        // 10 points at a 35 % REAL drop. A small real drop is inside the measurement's own noise —
        // residual seasonality, one weak quarter, or error in the deflator itself — so it is a
        // dead-band, not a risk signal; a real year-over-year contraction past a third is severe and
        // unambiguous deterioration, and that is where the full penalty belongs.
        //
        // These thresholds REPLACE the nominal calibration §7.3a chose, which §7.3b carried over
        // unchanged onto a real signal and flagged as owed a revision. Deflating can only make v more
        // negative, so the nominal numbers were materially stricter once applied in real terms: at a
        // routine 2.5 %/month inflation they took the FULL penalty off almost any firm not clearly
        // outpacing inflation. See the ramp comment in ScoringService.Compute for the magnitude.
        //
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // PROVISIONAL CALIBRATION — PENDING RECALIBRATION AGAINST PORTFOLIO OUTCOMES.
        //
        // The three values below are NOT empirically derived. They were chosen on economic-principle
        // grounds — what magnitude of real sales contraction is defensible to a risk committee as a
        // deterioration signal — because this model has no mature default history to fit against.
        // Standard practice is to recalibrate thresholds like these against LABELLED OUTCOME DATA
        // once it exists: which observed real sales drops actually preceded default in this
        // portfolio, and at what rate. Until that exercise is run, treat these as a reasoned prior,
        // not as a fitted result, and do not cite them as evidence of anything.
        // ─────────────────────────────────────────────────────────────────────────────────────────
        //
        // The two umbral_* keys are the only NEGATIVE values in this table: a drop is negative by
        // construction, which is why SignedScoringKeys exists.
        ["lufe_ventas_penalizacion_plena"] = (10.0000m, "Penalización plena por caída de ventas LUFE: puntos sumados al score final cuando la caída alcanza el umbral pleno (escala 0-100). Calibración PROVISIONAL, pendiente de recalibrar contra resultados observados de cartera"),
        ["lufe_ventas_umbral_inicio"]      = (-0.1000m, "Variación de ventas (fracción con signo) a partir de la cual empieza a penalizar; por debajo de una caída real del 10 % no hay penalización (banda muerta). Calibración PROVISIONAL, pendiente de recalibrar contra resultados observados de cartera"),
        ["lufe_ventas_umbral_plena"]       = (-0.3500m, "Variación de ventas (fracción con signo) en la que se aplica la penalización plena. Calibración PROVISIONAL, pendiente de recalibrar contra resultados observados de cartera"),
        ["lufe_ventas_enabled"]            = ( 1.0000m, "Aplica la penalización por caída de ventas LUFE al score final (1 = sí, 0 = no)"),

        // CASFOG guarantee-arrears penalty (§7.4). Points on the 0-100 FINAL score scale, applied
        // AFTER the critical floor and after the LUFE adjustment: casfogMoraAdj = plena * fracción(p),
        // where p is the share of the entity's guaranteed balance that the SGRs report IN ARREARS.
        //
        // p is weighted by EXPOSURE, not by count: the numerator sums SaldoBrutoVigente over the
        // PorSgr rows whose CantidadEnMora is greater than zero, the denominator sums it over all of
        // them. One arrears row at a large SGR is a graver fact than one at a small SGR, and counting
        // rows would call them equal. The denominator is the SUM OF THE ROWS, never the aggregate
        // Garantias.SaldoBrutoGarantiasVigentes: the two can disagree, and a ratio whose halves come
        // from different places is not a ratio.
        //
        // The ramp these three describe: nothing up to 5 % of the balance in arrears, then linearly
        // to the full 12 points at 40 %. A sliver in arrears is ordinary operational friction in a
        // guarantee portfolio — one late instalment at one SGR — so it is a dead-band, not a risk
        // signal; past a third of the guaranteed balance it is no longer friction.
        //
        // Why 12, between the other two penalties: arrears is DIRECT payment default on guaranteed
        // debt, graver than the LUFE sales-drop signal (10), which is an activity proxy and not a
        // default at all; and less than the BCRA rejected-cheques penalty (15), which is both more
        // severe and registry-central rather than one vendor's view of one guarantee circuit.
        //
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // PROVISIONAL CALIBRATION — PENDING RECALIBRATION AGAINST PORTFOLIO OUTCOMES.
        //
        // The three values below are NOT empirically derived. They were chosen on economic-principle
        // grounds — what share of a guaranteed portfolio in arrears is defensible to a risk committee
        // as a deterioration signal, and how it ranks against the signals already in the engine —
        // because this model has no mature default history to fit against. Standard practice is to
        // recalibrate thresholds like these against LABELLED OUTCOME DATA once it exists: which
        // observed arrears shares actually preceded default in this portfolio, and at what rate.
        // Until that exercise is run, treat these as a reasoned prior, not as a fitted result, and do
        // not cite them as evidence of anything.
        // ─────────────────────────────────────────────────────────────────────────────────────────
        //
        // Unlike the lufe_ventas_* umbrales these two are POSITIVE: a proportion of a balance, not a
        // signed drop. They are therefore NOT in SignedScoringKeys and pass the blanket ">= 0" rule
        // unchanged; ValidateScoringConfig bounds them into [0,1] instead.
        ["casfog_mora_penalizacion_plena"] = (12.0000m, "Penalización plena por garantías SGR en mora (CASFOG): puntos sumados al score final cuando la proporción del saldo vigente en mora alcanza el umbral pleno (escala 0-100). Calibración PROVISIONAL, pendiente de recalibrar contra resultados observados de cartera"),
        ["casfog_mora_umbral_inicio"]      = ( 0.0500m, "Proporción del saldo vigente en mora a partir de la cual empieza a penalizar; por debajo del 5 % no hay penalización (banda muerta). Calibración PROVISIONAL, pendiente de recalibrar contra resultados observados de cartera"),
        ["casfog_mora_umbral_plena"]       = ( 0.4000m, "Proporción del saldo vigente en mora en la que se aplica la penalización plena. Calibración PROVISIONAL, pendiente de recalibrar contra resultados observados de cartera"),
        ["casfog_mora_enabled"]            = ( 1.0000m, "Aplica la penalización por garantías SGR en mora (CASFOG) al score final (1 = sí, 0 = no)"),
    };

    /// <summary>Key → default numeric value.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> Values =
        Entries.ToDictionary(e => e.Key, e => e.Value.Value);

    /// <summary>Key → human-readable description.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        Entries.ToDictionary(e => e.Key, e => e.Value.Description);

    /// <summary>Returns the default value for <paramref name="key"/>.</summary>
    public static decimal Get(string key) => Values[key];

    // ── Key-group arrays (used by SettingsService for DTO grouping / validation) ──

    public static readonly string[] RatioWeightKeys =
    [
        "weight_liquidez_corriente", "weight_prueba_acida", "weight_endeudamiento",
        "weight_leverage", "weight_cobertura_intereses", "weight_roe", "weight_roa",
        "weight_margen_neto"
    ];

    public static readonly string[] RatioThresholdKeys =
    [
        "threshold_liquidez_corriente_ok", "threshold_liquidez_corriente_warn", "threshold_liquidez_corriente_danger",
        "threshold_prueba_acida_ok", "threshold_prueba_acida_warn", "threshold_prueba_acida_danger",
        "threshold_endeudamiento_ok", "threshold_endeudamiento_warn", "threshold_endeudamiento_danger",
        "threshold_leverage_ok", "threshold_leverage_warn", "threshold_leverage_danger",
        "threshold_cobertura_intereses_ok", "threshold_cobertura_intereses_warn", "threshold_cobertura_intereses_danger",
        "threshold_roe_ok", "threshold_roe_warn", "threshold_roe_danger",
        "threshold_roa_ok", "threshold_roa_warn", "threshold_roa_danger",
        "threshold_margen_neto_ok", "threshold_margen_neto_warn", "threshold_margen_neto_danger"
    ];

    /// <summary>
    /// The eight ratios whose thresholds form an ok / warn / danger triplet
    /// (<c>threshold_{ratio}_ok|warn|danger</c>, all in <see cref="RatioThresholdKeys"/>).
    /// </summary>
    public static readonly string[] ThresholdRatios =
    [
        "liquidez_corriente", "prueba_acida", "endeudamiento", "leverage",
        "cobertura_intereses", "roe", "roa", "margen_neto"
    ];

    /// <summary>
    /// The ONE ordering rule a ratio's threshold triplet must satisfy, shared by the Settings validation
    /// (which refuses to store an incoherent triplet) and the scoring engine (which refuses to score on
    /// one it finds stored anyway). Strictly monotone in the ratio's direction, and the direction comes
    /// from the DEFAULTS, never from the values being checked: higher-is-better (default ok &gt; default
    /// danger) needs ok &gt; warn &gt; danger, lower-is-better needs ok &lt; warn &lt; danger. Inferring it
    /// from the values would let a triplet written backwards pass as a ratio of the other kind.
    /// </summary>
    public static bool IsThresholdTripletOrdered(string ratio, decimal ok, decimal warn, decimal danger)
    {
        decimal defaultOk     = Get($"threshold_{ratio}_ok");
        decimal defaultDanger = Get($"threshold_{ratio}_danger");

        return defaultOk > defaultDanger
            ? ok > warn && warn > danger
            : ok < warn && warn < danger;
    }

    public static readonly string[] ComponentWeightKeys =
    [
        "weight_component_ratios", "weight_component_bcra", "weight_component_macro"
    ];

    public static readonly string[] ScoreThresholdKeys =
    [
        "score_threshold_low", "score_threshold_medium", "score_threshold_high"
    ];

    public static readonly string[] MacroKeys =
    [
        "macro_inflacion_penalizacion_threshold", "macro_penalizacion_puntos"
    ];

    public static readonly string[] CriticalFloorKeys =
    [
        "critical_floor_trigger_threshold",
        "critical_floor_liquidez_corriente", "critical_floor_prueba_acida",
        "critical_floor_endeudamiento", "critical_floor_leverage",
        "critical_floor_cobertura_intereses", "critical_floor_roe",
        "critical_floor_roa", "critical_floor_margen_neto"
    ];

    /// <summary>
    /// The profitability-floor rule (algorithm version 10): <c>critical_floor_profitability_requires_operating_loss</c>
    /// is a 0/1 flag stored as a decimal, for the same reason <c>bcra_cheques_enabled</c> is —
    /// <c>ScoringConfig.ConfigValue</c> is <c>decimal(10,4)</c> and has no boolean form. Deliberately NOT in
    /// <see cref="CriticalFloorKeys"/>, whose keys are points on the 0-100 score scale. Validated by
    /// <c>SettingsService</c> on PUT as 0 or 1. Surfaced in the Settings GET DTO's <c>floorRules</c> group,
    /// first, beside <see cref="AcidTestFloorKeys"/>.
    /// </summary>
    public static readonly string[] ProfitabilityFloorKeys =
    [
        "critical_floor_profitability_requires_operating_loss"
    ];

    /// <summary>
    /// The acid-test-floor rule (algorithm version 11): <c>critical_floor_acid_test_requires_weak_current_ratio</c>
    /// is a 0/1 flag stored as a decimal, exactly like <see cref="ProfitabilityFloorKeys"/>. Deliberately NOT
    /// in <see cref="CriticalFloorKeys"/>, whose keys are points on the 0-100 score scale. Validated by
    /// <c>SettingsService</c> on PUT as 0 or 1. Surfaced in the Settings GET DTO's <c>floorRules</c> group,
    /// after <see cref="ProfitabilityFloorKeys"/>.
    /// </summary>
    public static readonly string[] AcidTestFloorKeys =
    [
        "critical_floor_acid_test_requires_weak_current_ratio"
    ];

    /// <summary>
    /// The ratio keys the profitability-floor rule governs (<see cref="ProfitabilityFloorKeys"/>): the three
    /// whose danger is a LOSS, which under RT 6 may be only the financial and holding results.
    /// </summary>
    public static readonly string[] ProfitabilityFloorRatios =
    [
        "roe", "roa", "margen_neto"
    ];

    public static readonly string[] BcraTrendKeys =
    [
        "bcra_trend_window_months", "bcra_trend_pts_per_step_improve",
        "bcra_trend_pts_per_step_worsen", "bcra_trend_max_improve_adj",
        "bcra_trend_max_worsen_adj"
    ];

    /// <summary>
    /// The rejected-cheques penalty (§7.1). <c>bcra_cheques_penalizacion_plena</c> is a number of points
    /// on the 0-100 BCRA scale; <c>bcra_cheques_enabled</c> is a 0/1 flag stored as a decimal, because
    /// <c>ScoringConfig.ConfigValue</c> is <c>decimal(10,4)</c> and has no boolean form. Both are validated
    /// by <c>SettingsService</c> on PUT and clamped again by the engine. Like <see cref="BcraTrendKeys"/>,
    /// not surfaced as a group in the Settings GET DTO.
    /// </summary>
    public static readonly string[] BcraChequesKeys =
    [
        "bcra_cheques_penalizacion_plena", "bcra_cheques_enabled"
    ];

    /// <summary>
    /// The LUFE sales-drop penalty (§7.3a). <c>lufe_ventas_penalizacion_plena</c> is a number of points
    /// on the 0-100 FINAL score scale; <c>lufe_ventas_umbral_inicio</c> and <c>lufe_ventas_umbral_plena</c>
    /// are signed variation fractions in [-1,0] (see <see cref="SignedScoringKeys"/>); <c>lufe_ventas_enabled</c>
    /// is a 0/1 flag stored as a decimal, for the same reason <c>bcra_cheques_enabled</c> is —
    /// <c>ScoringConfig.ConfigValue</c> is <c>decimal(10,4)</c> and has no boolean form. All four are
    /// validated by <c>SettingsService</c> on PUT and clamped again by the engine. Like
    /// <see cref="BcraChequesKeys"/>, not surfaced as a group in the Settings GET DTO.
    /// </summary>
    public static readonly string[] LufeVentasKeys =
    [
        "lufe_ventas_penalizacion_plena", "lufe_ventas_umbral_inicio",
        "lufe_ventas_umbral_plena", "lufe_ventas_enabled"
    ];

    /// <summary>
    /// The CASFOG guarantee-arrears penalty (§7.4). <c>casfog_mora_penalizacion_plena</c> is a number of
    /// points on the 0-100 FINAL score scale; <c>casfog_mora_umbral_inicio</c> and
    /// <c>casfog_mora_umbral_plena</c> are PROPORTIONS of the guaranteed balance in arrears and so live
    /// in [0,1] — positive by construction, which is why, unlike the <see cref="LufeVentasKeys"/>
    /// umbrales, they are NOT in <see cref="SignedScoringKeys"/> and need no exemption from the blanket
    /// "&gt;= 0" rule; <c>casfog_mora_enabled</c> is a 0/1 flag stored as a decimal, for the same reason
    /// <c>bcra_cheques_enabled</c> is — <c>ScoringConfig.ConfigValue</c> is <c>decimal(10,4)</c> and has
    /// no boolean form. All four are validated by <c>SettingsService</c> on PUT and clamped again by the
    /// engine. Like <see cref="BcraChequesKeys"/>, not surfaced as a group in the Settings GET DTO.
    /// </summary>
    public static readonly string[] CasfogMoraKeys =
    [
        "casfog_mora_penalizacion_plena", "casfog_mora_umbral_inicio",
        "casfog_mora_umbral_plena", "casfog_mora_enabled"
    ];

    /// <summary>
    /// The keys whose values are legitimately NEGATIVE, and the reason <c>SettingsService</c> cannot
    /// apply its blanket "every value must be &gt;= 0" rule to the whole merged map. Both are signed
    /// sales-variation fractions: a DROP is negative, so the shipped defaults are -0.10
    /// (<c>lufe_ventas_umbral_inicio</c>) and -0.35 (<c>lufe_ventas_umbral_plena</c>), and a blanket
    /// check would reject the defaults themselves — failing every PUT for every tenant, on every
    /// key. They are not unvalidated as a result: <c>ValidateScoringConfig</c> bounds them into
    /// [-1,0], which is stricter than the rule they are exempt from.
    /// </summary>
    public static readonly string[] SignedScoringKeys =
    [
        "lufe_ventas_umbral_inicio", "lufe_ventas_umbral_plena"
    ];

    /// <summary>
    /// Maps each ratio breakdown key to its corresponding critical-floor config key.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RatioFloorKeyMap = new Dictionary<string, string>
    {
        ["liquidez_corriente"]  = "critical_floor_liquidez_corriente",
        ["prueba_acida"]        = "critical_floor_prueba_acida",
        ["endeudamiento"]       = "critical_floor_endeudamiento",
        ["leverage"]            = "critical_floor_leverage",
        ["cobertura_intereses"] = "critical_floor_cobertura_intereses",
        ["roe"]                 = "critical_floor_roe",
        ["roa"]                 = "critical_floor_roa",
        ["margen_neto"]         = "critical_floor_margen_neto",
    };
}
