// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Deterministic implementation of <see cref="IAlertRuleEngine"/>. Stateless apart from its two
/// injected singletons, so it can be instantiated directly in tests.
///
/// DEDUPLICATION IS DELIBERATELY ABSENT. A catalogue alert is never suppressed because a model
/// observation appears to describe the same finding, and vice versa. Matching on code strings
/// cannot work here — three codes were measured with naming variants across runs of the SAME
/// document — so any such match would be a guess. A visible duplicate is acceptable; a real
/// finding lost to over-eager deduplication is not. Do not "optimise" this away.
///
/// In particular <see cref="FlujoNoConcilia"/> intentionally coexists with the cash-flow
/// reconciliation ANOMALY that AnalysisController already appends: one finding, surfaced in two
/// channels, by design.
/// </summary>
public sealed class AlertRuleEngine : IAlertRuleEngine
{
    // ── The closed set of implemented rule codes ──────────────────────────────
    // A catalogue row whose Code is not one of these is skipped, never guessed at. This is the
    // whole point of the catalogue: the model's `code` was a free-form string with no closed set.

    /// <summary>Entire liability side is current: no non-current liabilities reported.</summary>
    public const string PasivoCienCorriente = "PASIVO_100_CORRIENTE";

    /// <summary>Inventory represents more than the configured share of current assets.</summary>
    [RuleThreshold(RuleThresholdUnit.Proportion)]
    public const string InventarioElevado = "INVENTARIO_ELEVADO";

    /// <summary>Total liabilities represent more than the configured share of total assets.</summary>
    [RuleThreshold(RuleThresholdUnit.Proportion)]
    public const string EndeudamientoElevado = "ENDEUDAMIENTO_ELEVADO";

    /// <summary>Interest coverage cannot be computed: financial expenses not stated separately.</summary>
    public const string CoberturaInteresesIndeterminable = "COBERTURA_INTERESES_INDETERMINABLE";

    /// <summary>Cash flow sections do not sum to the reported net change.</summary>
    public const string FlujoNoConcilia = "FLUJO_NO_CONCILIA";

    /// <summary>Cash flow statement reports zero financing activity.</summary>
    public const string FlujoFinanciacionCero = "FLUJO_FINANCIACION_CERO";

    // The rule-code constants above, reflected once. Both public members below are projections of
    // this, so the codes and their threshold units are declared in exactly one place — the constant
    // itself — and cannot drift by being restated elsewhere. Declaration order matters: static
    // initializers run top to bottom, so this must precede the members that read it.
    private static readonly FieldInfo[] CodeFields =
        typeof(AlertRuleEngine)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToArray();

    /// <summary>
    /// The codes above, as a set — the authority on what "implemented" means for callers outside
    /// the engine. The configuration API uses it to REJECT enabling a rule this engine does not
    /// implement, rather than accepting the change and letting <see cref="SkipUnknownCode"/>
    /// quietly drop it on every analysis.
    ///
    /// A test locks the set to the known six, so a future public constant that is not a rule code
    /// cannot widen the gate unnoticed.
    /// </summary>
    public static readonly IReadOnlySet<string> ImplementedCodes =
        CodeFields
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The codes that actually CONSUME <c>AlertRuleDefinition.ThresholdValue</c>, mapped to the unit
    /// the engine reads them in, derived from <see cref="RuleThresholdAttribute"/> on the constants.
    ///
    /// A code absent from this map takes no threshold. Storing one against it would be accepted,
    /// rendered on the configuration screen, and then silently ignored by
    /// <see cref="Evaluate"/> — so configuration refuses it. Adding a threshold to a future rule is
    /// a matter of marking its constant; no validation elsewhere needs editing.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RuleThresholdUnit> ThresholdUnits =
        CodeFields
            .Select(f => (Code: (string)f.GetRawConstantValue()!,
                          Threshold: f.GetCustomAttribute<RuleThresholdAttribute>()))
            .Where(x => x.Threshold is not null)
            .ToFrozenDictionary(x => x.Code, x => x.Threshold!.Unit, StringComparer.Ordinal);

    /// <summary>Metric key reported by <see cref="InventarioElevado"/>.</summary>
    private const string InventoryMetricKey = "inventory_over_current_assets";

    /// <summary>Metric key reported by <see cref="EndeudamientoElevado"/>.</summary>
    private const string LeverageMetricKey = "liabilities_over_assets";

    // A proportion beyond this is corrupt extraction, not a finding. It also keeps the double →
    // decimal cast below inside decimal's range, so a nonsense figure can never throw
    // OverflowException inside a block whose whole purpose is to be dependable.
    private const double MaxPlausibleRatio = 1e9;

    // es-AR so the substituted figures carry the same thousands/decimal convention as the rest of
    // the analyst-facing report.
    private static readonly CultureInfo EsArFormat = CultureInfo.GetCultureInfo("es-AR");

    private readonly ICashFlowReconciliationService _reconciliation;
    private readonly ILogger<AlertRuleEngine>       _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AlertRuleEngine"/>.
    /// </summary>
    public AlertRuleEngine(
        ICashFlowReconciliationService reconciliation,
        ILogger<AlertRuleEngine>       logger)
    {
        _reconciliation = reconciliation;
        _logger         = logger;
    }

    // ── IAlertRuleEngine ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyList<CatalogueAlert> Evaluate(
        IReadOnlyList<AlertRuleDefinition> rules,
        BalanceAccounts?                   accounts,
        CashFlowData?                      cashFlow)
    {
        var fired = new List<CatalogueAlert>();

        // Enabled is re-checked here even though the repository already filters on it, so the
        // guarantee "a disabled rule never fires" holds for any caller. Ordinal ordering is applied
        // on top of the repository's ORDER BY because the database's collation ordering is not
        // necessarily ordinal — output order must not depend on the query plan.
        foreach (var rule in rules.Where(r => r.Enabled).OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            var alert = rule.Code switch
            {
                PasivoCienCorriente              => EvaluatePasivoCienCorriente(rule, accounts),
                InventarioElevado                => EvaluateInventarioElevado(rule, accounts),
                EndeudamientoElevado             => EvaluateEndeudamientoElevado(rule, accounts),
                CoberturaInteresesIndeterminable => EvaluateCoberturaIntereses(rule, accounts),
                FlujoNoConcilia                  => EvaluateFlujoNoConcilia(rule, cashFlow),
                FlujoFinanciacionCero            => EvaluateFlujoFinanciacionCero(rule, cashFlow),
                _                                => SkipUnknownCode(rule),
            };

            if (alert is not null) fired.Add(alert);
        }

        return fired;
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// PASIVO_100_CORRIENTE — non_current_liabilities == 0 AND liabilities &gt; 0. No threshold.
    /// Requires non_current_liabilities to be an explicitly reported zero: an absent figure is
    /// unknown, not zero, and must not fire.
    /// </summary>
    private static CatalogueAlert? EvaluatePasivoCienCorriente(
        AlertRuleDefinition rule, BalanceAccounts? accounts)
    {
        if (accounts is null) return null;
        if (accounts.NonCurrentLiabilities is not { } nonCurrent || nonCurrent != 0d) return null;
        if (accounts.TotalLiabilities      is not { } total      || total      <= 0d) return null;

        return Build(rule, rule.DescriptionTemplateEs);
    }

    /// <summary>
    /// INVENTARIO_ELEVADO — inventory / current_assets exceeds ThresholdValue. Strictly greater,
    /// so a ratio sitting exactly on the threshold does NOT fire.
    /// </summary>
    private CatalogueAlert? EvaluateInventarioElevado(
        AlertRuleDefinition rule, BalanceAccounts? accounts)
    {
        if (accounts is null) return null;

        if (rule.ThresholdValue is not { } threshold)
        {
            _logger.LogWarning(
                "Alert rule [{Code}] tenant {TenantId} is enabled but has no ThresholdValue; skipped.",
                rule.Code, rule.TenantId);
            return null;
        }

        if (accounts.Inventory     is not { } inventory) return null;
        if (accounts.CurrentAssets is not { } currentAssets || currentAssets <= 0d) return null;

        var ratioAsDouble = inventory / currentAssets;
        if (!double.IsFinite(ratioAsDouble) || Math.Abs(ratioAsDouble) > MaxPlausibleRatio) return null;

        // Compared in decimal against the configured threshold so the boundary is exact rather
        // than subject to binary floating-point representation.
        var ratio = (decimal)ratioAsDouble;
        if (ratio <= threshold) return null;

        var description = rule.DescriptionTemplateEs
            .Replace("{valor}",  FormatPercent(ratio),     StringComparison.Ordinal)
            .Replace("{umbral}", FormatPercent(threshold), StringComparison.Ordinal);

        return Build(rule, description, InventoryMetricKey, ratio, threshold);
    }

    /// <summary>
    /// ENDEUDAMIENTO_ELEVADO — liabilities / assets exceeds ThresholdValue. Strictly greater, so a
    /// ratio sitting exactly on the threshold does NOT fire.
    ///
    /// Measured on the single analysed balance. The §7.2 wording speaks of leverage SUSTAINED over
    /// 24 months, which this deliberately does not implement: <see cref="Evaluate"/> receives only
    /// the accounts and the cash flow, never a monthly debt series, so a "sustained" claim would be
    /// invented rather than measured.
    /// </summary>
    private CatalogueAlert? EvaluateEndeudamientoElevado(
        AlertRuleDefinition rule, BalanceAccounts? accounts)
    {
        if (accounts is null) return null;

        if (rule.ThresholdValue is not { } threshold)
        {
            _logger.LogWarning(
                "Alert rule [{Code}] tenant {TenantId} is enabled but has no ThresholdValue; skipped.",
                rule.Code, rule.TenantId);
            return null;
        }

        if (accounts.TotalLiabilities is not { } liabilities) return null;
        if (accounts.TotalAssets      is not { } assets || assets <= 0d) return null;

        var ratioAsDouble = liabilities / assets;
        if (!double.IsFinite(ratioAsDouble) || Math.Abs(ratioAsDouble) > MaxPlausibleRatio) return null;

        // Compared in decimal against the configured threshold so the boundary is exact rather
        // than subject to binary floating-point representation.
        var ratio = (decimal)ratioAsDouble;
        if (ratio <= threshold) return null;

        var description = rule.DescriptionTemplateEs
            .Replace("{valor}",  FormatPercent(ratio),     StringComparison.Ordinal)
            .Replace("{umbral}", FormatPercent(threshold), StringComparison.Ordinal);

        return Build(rule, description, LeverageMetricKey, ratio, threshold);
    }

    /// <summary>
    /// COBERTURA_INTERESES_INDETERMINABLE — financial_expenses is null. No threshold.
    /// The extraction prompt requires financial_expenses to be null (never a consolidated figure)
    /// when the income statement does not state it separately, so null here is a real finding about
    /// the document, not missing data about our own processing.
    /// </summary>
    private static CatalogueAlert? EvaluateCoberturaIntereses(
        AlertRuleDefinition rule, BalanceAccounts? accounts)
    {
        if (accounts is null) return null;
        if (accounts.FinancialExpenses is not null) return null;

        return Build(rule, rule.DescriptionTemplateEs);
    }

    /// <summary>
    /// FLUJO_NO_CONCILIA — delegates entirely to <see cref="ICashFlowReconciliationService"/>.
    /// Neither the arithmetic nor the tolerance is reimplemented here; that gate stays in exactly
    /// one place. <c>Reconcile</c> is used rather than <c>Check</c> because its tri-state
    /// distinguishes "did not reconcile" from "could not be checked" — only the former fires.
    /// </summary>
    private CatalogueAlert? EvaluateFlujoNoConcilia(AlertRuleDefinition rule, CashFlowData? cashFlow)
    {
        if (cashFlow is not { Present: true }) return null;

        var reconciliation = _reconciliation.Reconcile(cashFlow);
        if (reconciliation.SectionsVsNetChange.State != ReconciliationState.Failed) return null;

        return Build(rule, rule.DescriptionTemplateEs);
    }

    /// <summary>
    /// FLUJO_FINANCIACION_CERO — cash_flow.present AND financing == 0. No threshold.
    /// An absent financing figure is unknown, not zero, and must not fire.
    /// </summary>
    private static CatalogueAlert? EvaluateFlujoFinanciacionCero(
        AlertRuleDefinition rule, CashFlowData? cashFlow)
    {
        if (cashFlow is not { Present: true }) return null;
        if (cashFlow.Financing is not { } financing || financing != 0d) return null;

        return Build(rule, rule.DescriptionTemplateEs);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private CatalogueAlert? SkipUnknownCode(AlertRuleDefinition rule)
    {
        _logger.LogInformation(
            "Alert rule [{Code}] tenant {TenantId} is enabled but no engine rule implements it; skipped.",
            rule.Code, rule.TenantId);
        return null;
    }

    private static CatalogueAlert Build(
        AlertRuleDefinition rule,
        string              description,
        string?             metricKey      = null,
        decimal?            metricValue    = null,
        decimal?            thresholdValue = null)
        => new()
        {
            Level          = rule.Severity,
            Code           = rule.Code,
            Title          = rule.DisplayNameEs,
            Description    = description,
            MetricKey      = metricKey,
            MetricValue    = metricValue,
            ThresholdValue = thresholdValue,
            // The metric and the threshold are compared directly by the rule that reports them, so
            // they necessarily share a unit — the one the rule's own [RuleThreshold] declares.
            // Derived from that single declaration rather than from a metric-key → unit map, which
            // would be a second copy of knowledge that already lives on the constant. Null for a
            // rule that reports no metric, and (honestly) for one that reports a metric without
            // declaring a unit — none exists today.
            MetricUnit     = metricKey is not null && ThresholdUnits.TryGetValue(rule.Code, out var unit)
                                 ? unit.ToWire()
                                 : null,
        };

    /// <summary>Renders a proportion (0.5000) as an es-AR percentage figure ("50,0").</summary>
    private static string FormatPercent(decimal proportion)
        => (proportion * 100m).ToString("N1", EsArFormat);
}
