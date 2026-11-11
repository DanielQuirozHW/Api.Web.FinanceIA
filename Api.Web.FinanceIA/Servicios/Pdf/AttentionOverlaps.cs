// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.ScoreReliability;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>The score facts "Puntos de atención" states with figures, which an alert can repeat in words.</summary>
internal enum AttentionFact
{
    /// <summary>"Ventas declaradas (LUFE): −N pts sobre el score."</summary>
    Ventas,

    /// <summary>"Situación BCRA N — …" and/or "empeorando N niveles".</summary>
    Bcra,

    /// <summary>"N cheques rechazados (…)".</summary>
    Cheques,

    /// <summary>A listed reliability reason saying Cobertura de intereses could not be measured or calculated.</summary>
    InterestCoverage,

    /// <summary>"Piso por ratio crítico: …" naming Endeudamiento, or a listed reason that the score rests on that floor.</summary>
    CriticalFloor,
}

/// <summary>
/// THE one place that says which alert codes describe the same thing as a page-1 fact, and which model codes describe
/// the same thing as a catalogue alert. When the fact produced a bullet, the alert is not printed again: the page
/// keeps the fact's line (it carries the figures, or names every ratio) at the alert's rank when that is higher.
/// <para>
/// Model codes are written freely, so the model lists are the codes seen or expected, matched exactly (ignoring case)
/// — never a pattern. Catalogue codes are a closed set (<see cref="AlertRuleEngine"/>), checked one by one against
/// every fact: COBERTURA_INTERESES_INDETERMINABLE repeats the reliability reason that interest coverage was not
/// measured / not calculable; ENDEUDAMIENTO_ELEVADO repeats a critical floor bound by Endeudamiento. The other four —
/// PASIVO_100_CORRIENTE, INVENTARIO_ELEVADO, FLUJO_NO_CONCILIA, FLUJO_FINANCIACION_CERO — repeat no fact: no score
/// fact, reliability reason or floor speaks of non-current liabilities, inventory or the cash-flow statement.
/// Extend it here, with a test row in AttentionPointsTests.
/// </para>
/// </summary>
internal static class AttentionOverlaps
{
    // The ratio keys (score breakdown / reliability RatioKeys) the two ratio facts are about.
    internal const string InterestCoverageRatioKey = "cobertura_intereses";
    internal const string DebtRatioKey             = "endeudamiento";

    /// <summary>Alert codes — model or catalogue — that repeat a fact.</summary>
    internal static readonly IReadOnlyDictionary<AttentionFact, IReadOnlyList<string>> Codes =
        new Dictionary<AttentionFact, IReadOnlyList<string>>
        {
            [AttentionFact.Ventas] =
            [
                "VENTAS_EN_CAIDA",
                "CAIDA_DE_VENTAS",
                "CAIDA_VENTAS",
                "CAIDA_DE_INGRESOS",
                "REVENUE_DECLINE",
                "SALES_DECLINE",
            ],
            [AttentionFact.Bcra] =
            [
                "SITUACION_BCRA",
                "BCRA_SITUACION",
                "BCRA_DETERIORO",
                "DETERIORO_BCRA",
                "BCRA_EMPEORANDO",
            ],
            [AttentionFact.Cheques] =
            [
                "CHEQUES_RECHAZADOS",
            ],
            [AttentionFact.InterestCoverage] =
            [
                AlertRuleEngine.CoberturaInteresesIndeterminable,
            ],
            [AttentionFact.CriticalFloor] =
            [
                AlertRuleEngine.EndeudamientoElevado,
            ],
        };

    /// <summary>
    /// Per catalogue code, the model codes that state the same finding (the catalogue code itself included: the model
    /// may spell it the same way). With both on page 1 the catalogue alert is kept — it is deterministic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lists are wide on purpose: they hold the codes a 90-day production sweep of <c>AnalysisResults.RiskAlerts</c>
    /// found (636 distinct model codes, Spanish and English mixed), not just the obvious spellings. Width costs nothing:
    /// an equivalence only acts when the CATALOGUE alert fired, so a model alert about the same quantity adds nothing
    /// next to it — and with no catalogue alert the model alert is printed as before.
    /// </para>
    /// <para>
    /// Deliberately NOT listed: leverage codes (LEVERAGE_HIGH, HIGH_LEVERAGE, APALANCAMIENTO_ELEVADO) measure
    /// liabilities over equity — another ratio than ENDEUDAMIENTO_ELEVADO's liabilities over total assets; and the
    /// balance sheet's revenue drops (CAIDA_INGRESOS, REVENUE_DROP…) are not the Ventas fact, which is LUFE — another
    /// source. A code appears once across every list: the lookups below are built with ToDictionary, so a repeat would
    /// fail this type's initialisation and drop "Puntos de atención" from every export.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ModelEquivalents =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [AlertRuleEngine.PasivoCienCorriente] =
            [
                AlertRuleEngine.PasivoCienCorriente,
                "PASIVO_TOTALMENTE_CORRIENTE",
                "PASIVO_INTEGRAMENTE_CORRIENTE",
                "SIN_PASIVO_NO_CORRIENTE",
                "PASIVO_NO_CORRIENTE_NULO",
                "NO_NCLIAB",
                "PASIVO_CORRIENTE_CONCENTRADO",
                "PASIVO_CONCENTRADO_CORRIENTE",
                "CONCENTRACION_PASIVO_CORRIENTE",
                "DEBT_CONCENTRATION_SHORT_TERM",
            ],
            [AlertRuleEngine.InventarioElevado] =
            [
                AlertRuleEngine.InventarioElevado,
                "INVENTARIO_ALTO",
                "ALTO_INVENTARIO",
                "BIENES_DE_CAMBIO_ELEVADOS",
                "HIGH_INVENTORY",
                "BIENES_CAMBIO_ELEVADOS",
                "STOCK_ELEVADO",
                "INVENTORY_CONCENTRATION",
                "INV_CONCENTRATION",
                "BIENES_CAMBIO_CONCENTRACION",
                "CONCENTRACION_BIENES_CAMBIO",
                "BIENES_CAMBIO_CONCENTRADOS",
                "INVENTARIO_CONCENTRADO",
                "INVENTARIO_DOMINANTE",
                "BIENES_CAMBIO_DOMINANTES",
                "BIENES_CAMBIO_DOMINAN_AC",
                "INVENTORY_DOMINATES_CURRENT",
                "INVENTORY_DOMINATES_CURRENT_ASSETS",
                "INVENTORY_EXCEEDS_CURRENT_ASSETS",
            ],
            [AlertRuleEngine.EndeudamientoElevado] =
            [
                AlertRuleEngine.EndeudamientoElevado,
                "ENDEUDAMIENTO_ALTO",
                "ALTO_ENDEUDAMIENTO",
                "HIGH_DEBT_RATIO",
                "ENDEUDAMIENTO_ELEVADO_SOBRE_ACTIVO_TOTAL",
                "DEBT_RATIO_HIGH",
            ],
            [AlertRuleEngine.CoberturaInteresesIndeterminable] =
            [
                AlertRuleEngine.CoberturaInteresesIndeterminable,
                "COBERTURA_INTERESES_NO_DETERMINABLE",
                "COBERTURA_DE_INTERESES_NO_DETERMINABLE",
                "COBERTURA_INTERESES_NO_CALCULABLE",
                "GASTOS_FINANCIEROS_NO_INFORMADOS",
                "INTEREST_COVERAGE_UNDETERMINED",
                "COBERTURA_INTERESES_ND",
                "ALT_COBERTURA_INTERESES_INDETERMINABLE",
                "INTEREST_COVERAGE_UNDETERMINABLE",
                "INTEREST_COVERAGE_INDETERMINABLE",
                "INTEREST_COVERAGE_ND",
                "GASTOS_FINANCIEROS_NO_DETERMINABLES",
                "FINANCIAL_EXPENSES_NOT_DETERMINABLE",
                "FINANCIAL_EXPENSES_UNDETERMINED",
                "INTERESES_NO_AISLABLES",
            ],
            [AlertRuleEngine.FlujoNoConcilia] =
            [
                AlertRuleEngine.FlujoNoConcilia,
                "FLUJO_EFECTIVO_NO_CONCILIA",
                "FLUJO_DE_EFECTIVO_NO_CONCILIA",
                "CASH_FLOW_NOT_RECONCILED",
            ],
            [AlertRuleEngine.FlujoFinanciacionCero] =
            [
                AlertRuleEngine.FlujoFinanciacionCero,
                "FINANCIACION_CERO",
                "SIN_ACTIVIDADES_DE_FINANCIACION",
            ],
        };

    // The reliability reasons that carry a ratio fact: (reason codes, the ratio key they must name, the fact).
    private static readonly (IReadOnlyList<string> ReasonCodes, string RatioKey, AttentionFact Fact)[] ReasonFacts =
    [
        ([ScoreReliabilityCodes.RatiosNotMeaningful, ScoreReliabilityCodes.RatiosLowCoverage, ScoreReliabilityCodes.RatiosNotMeasured],
            InterestCoverageRatioKey, AttentionFact.InterestCoverage),
        ([ScoreReliabilityCodes.CriticalFloorUnverified], DebtRatioKey, AttentionFact.CriticalFloor),
    ];

    private static readonly IReadOnlyDictionary<string, AttentionFact> ByCode =
        Codes.SelectMany(pair => pair.Value.Select(code => (code, pair.Key)))
             .ToDictionary(entry => entry.code, entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> CatalogueCodeOfModelCode =
        ModelEquivalents.SelectMany(pair => pair.Value.Select(code => (code, pair.Key)))
                        .ToDictionary(entry => entry.code, entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The fact an alert code repeats, or null when it overlaps none. A model code equivalent to a catalogue code
    /// repeats whatever that catalogue code repeats.
    /// </summary>
    internal static AttentionFact? FactOf(string? code)
    {
        string trimmed = code?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return null;

        if (ByCode.TryGetValue(trimmed, out AttentionFact fact))
            return fact;

        return CatalogueCodeOfModelCode.TryGetValue(trimmed, out string? catalogueCode) && ByCode.TryGetValue(catalogueCode, out fact)
            ? fact
            : null;
    }

    /// <summary>The fact a reliability reason states, or null.</summary>
    internal static AttentionFact? FactOfReason(string? reasonCode, IReadOnlyList<string>? ratioKeys)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || ratioKeys is not { Count: > 0 })
            return null;

        foreach ((IReadOnlyList<string> codes, string ratioKey, AttentionFact fact) in ReasonFacts)
        {
            if (codes.Contains(reasonCode.Trim(), StringComparer.Ordinal) && ratioKeys.Contains(ratioKey, StringComparer.Ordinal))
                return fact;
        }

        return null;
    }

    /// <summary>Whether the critical-floor line repeats <see cref="AttentionFact.CriticalFloor"/>: the floor names Endeudamiento.</summary>
    internal static bool FloorIsCriticalFloorFact(IReadOnlyList<string> criticalRatioKeys) =>
        criticalRatioKeys.Contains(DebtRatioKey, StringComparer.Ordinal);

    /// <summary>
    /// Whether a code appears in any list here — a fact's codes or a catalogue code's model spellings. A model alert
    /// whose code is in none is printed beside a catalogue alert on its own, which is what the export log reports.
    /// </summary>
    internal static bool IsListed(string? code)
    {
        string trimmed = code?.Trim() ?? string.Empty;
        return trimmed.Length > 0 && (ByCode.ContainsKey(trimmed) || CatalogueCodeOfModelCode.ContainsKey(trimmed));
    }

    /// <summary>Whether a model alert states the same finding as a catalogue alert.</summary>
    internal static bool SameFinding(string? catalogueCode, string? modelCode)
    {
        string model = modelCode?.Trim() ?? string.Empty;
        string catalogue = catalogueCode?.Trim() ?? string.Empty;
        return model.Length > 0
               && catalogue.Length > 0
               && CatalogueCodeOfModelCode.TryGetValue(model, out string? equivalent)
               && string.Equals(equivalent, catalogue, StringComparison.OrdinalIgnoreCase);
    }
}
