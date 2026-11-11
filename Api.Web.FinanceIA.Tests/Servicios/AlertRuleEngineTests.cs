// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Covers <see cref="AlertRuleEngine"/>: each of the six catalogue rules fires, does not fire,
/// and behaves correctly at its boundary; a disabled rule never fires; an unimplemented code is
/// skipped; and output order is stable.
///
/// All figures are synthetic round numbers. The real <see cref="CashFlowReconciliationService"/>
/// is used rather than a stub, so FLUJO_NO_CONCILIA is exercised against the actual tolerance gate
/// (0,5 % relative AND 1,00 absolute, both of which must be exceeded) instead of a restatement of
/// it — the whole point of that rule delegating rather than reimplementing.
/// </summary>
public sealed class AlertRuleEngineTests
{
    private const string Tenant = "tenant-1";

    private readonly AlertRuleEngine _sut = new(
        new CashFlowReconciliationService(),
        NullLogger<AlertRuleEngine>.Instance);

    // ── Fixture helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Baseline balance on which NONE of the five rules fires: non-current liabilities present,
    /// inventory at 25 % of current assets, financial expenses stated.
    /// </summary>
    private static BalanceAccounts Accounts() => new()
    {
        CurrentAssets         = 40_000_000,
        NonCurrentAssets      = 60_000_000,
        TotalAssets           = 100_000_000,
        CurrentLiabilities    = 20_000_000,
        NonCurrentLiabilities = 30_000_000,
        TotalLiabilities      = 50_000_000,
        Equity                = 50_000_000,
        NetRevenue            = 120_000_000,
        NetProfit             = 12_000_000,
        OperatingResult       = 20_000_000,
        FinancialExpenses     = 4_000_000,
        Inventory             = 10_000_000,
        PrepaidExpenses       = 1_000_000,
    };

    /// <summary>
    /// Baseline cash flow that reconciles on both checks: 100.000 − 40.000 − 30.000 = 30.000,
    /// and 200.000 + 30.000 = 230.000.
    /// </summary>
    private static CashFlowData CashFlow() => new()
    {
        Present       = true,
        Operating     = 100_000,
        Investing     = -40_000,
        Financing     = -30_000,
        NetChange     = 30_000,
        CashBeginning = 200_000,
        CashEnd       = 230_000,
    };

    private static AlertRuleDefinition Rule(
        string   code,
        bool     enabled   = true,
        string   severity  = "medium",
        decimal? threshold = null,
        string?  template  = null) => new()
    {
        RuleId                = Guid.NewGuid(),
        TenantId              = Tenant,
        Code                  = code,
        Enabled               = enabled,
        Severity              = severity,
        ThresholdValue        = threshold,
        DisplayNameEs         = $"Título de {code}",
        DescriptionTemplateEs = template ?? $"Descripción de {code}.",
        CreatedAt             = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt             = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private IReadOnlyList<CatalogueAlert> Evaluate(
        AlertRuleDefinition rule, BalanceAccounts? accounts = null, CashFlowData? cashFlow = null)
        => _sut.Evaluate([rule], accounts, cashFlow);

    // ── PASIVO_100_CORRIENTE ──────────────────────────────────────────────────

    [Fact]
    public void Evaluate_PasivoCorriente_WhenNoNonCurrentLiabilities_Fires()
    {
        var accounts = Accounts() with { NonCurrentLiabilities = 0, TotalLiabilities = 20_000_000 };

        var alert = Assert.Single(Evaluate(Rule(AlertRuleEngine.PasivoCienCorriente), accounts));

        Assert.Equal(AlertRuleEngine.PasivoCienCorriente, alert.Code);
    }

    [Fact]
    public void Evaluate_PasivoCorriente_WhenNonCurrentLiabilitiesPresent_DoesNotFire()
    {
        Assert.Empty(Evaluate(Rule(AlertRuleEngine.PasivoCienCorriente), Accounts()));
    }

    [Fact]
    public void Evaluate_PasivoCorriente_WhenNoLiabilitiesAtAll_DoesNotFire()
    {
        // Boundary: the rule requires liabilities > 0. A company with zero total liabilities has
        // no maturity concentration to report — firing here would be a false positive.
        var accounts = Accounts() with { NonCurrentLiabilities = 0, TotalLiabilities = 0 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.PasivoCienCorriente), accounts));
    }

    [Fact]
    public void Evaluate_PasivoCorriente_WhenNonCurrentLiabilitiesAbsent_DoesNotFire()
    {
        // An absent figure is UNKNOWN, not zero. Only an explicitly reported 0 is a finding.
        var accounts = Accounts() with { NonCurrentLiabilities = null, TotalLiabilities = 20_000_000 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.PasivoCienCorriente), accounts));
    }

    // ── INVENTARIO_ELEVADO ────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_InventarioElevado_WhenRatioAboveThreshold_Fires()
    {
        // 30.000.000 / 40.000.000 = 0,75 > 0,50
        var accounts = Accounts() with { Inventory = 30_000_000, CurrentAssets = 40_000_000 };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m), accounts));

        Assert.Equal(AlertRuleEngine.InventarioElevado, alert.Code);
        Assert.Equal(0.75m, alert.MetricValue);
        Assert.Equal(0.5000m, alert.ThresholdValue);
        Assert.Equal("inventory_over_current_assets", alert.MetricKey);
    }

    [Fact]
    public void Evaluate_InventarioElevado_WhenRatioBelowThreshold_DoesNotFire()
    {
        // Baseline is 10.000.000 / 40.000.000 = 0,25
        Assert.Empty(Evaluate(Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m), Accounts()));
    }

    [Fact]
    public void Evaluate_InventarioElevado_WhenRatioExactlyAtThreshold_DoesNotFire()
    {
        // THE BOUNDARY. 20.000.000 / 40.000.000 = 0,50 exactly. The comparison is strictly
        // greater, so sitting on the configured threshold is not yet an exceedance.
        var accounts = Accounts() with { Inventory = 20_000_000, CurrentAssets = 40_000_000 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m), accounts));
    }

    [Fact]
    public void Evaluate_InventarioElevado_WhenRatioJustAboveThreshold_Fires()
    {
        // The other side of the same boundary: 20.400.000 / 40.000.000 = 0,51
        var accounts = Accounts() with { Inventory = 20_400_000, CurrentAssets = 40_000_000 };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m), accounts));

        Assert.Equal(0.51m, alert.MetricValue);
    }

    [Fact]
    public void Evaluate_InventarioElevado_WhenThresholdMissing_DoesNotFire()
    {
        // A threshold rule with no threshold is unevaluable, not "fires by default".
        var accounts = Accounts() with { Inventory = 30_000_000, CurrentAssets = 40_000_000 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.InventarioElevado, threshold: null), accounts));
    }

    [Fact]
    public void Evaluate_InventarioElevado_WhenCurrentAssetsZero_DoesNotFire()
    {
        // No division by zero, and no alert invented from an undefined ratio.
        var accounts = Accounts() with { Inventory = 30_000_000, CurrentAssets = 0 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m), accounts));
    }

    [Fact]
    public void Evaluate_InventarioElevado_SubstitutesTokensInEsArFormat()
    {
        var accounts = Accounts() with { Inventory = 30_000_000, CurrentAssets = 40_000_000 };
        var rule = Rule(
            AlertRuleEngine.InventarioElevado,
            threshold: 0.5000m,
            template: "Los bienes de cambio representan el {valor} % del activo corriente, " +
                      "por encima del umbral configurado de {umbral} %.");

        var alert = Assert.Single(Evaluate(rule, accounts));

        // es-AR uses a decimal comma.
        Assert.Equal(
            "Los bienes de cambio representan el 75,0 % del activo corriente, " +
            "por encima del umbral configurado de 50,0 %.",
            alert.Description);
    }

    // ── ENDEUDAMIENTO_ELEVADO ─────────────────────────────────────────────────

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenRatioAboveThreshold_Fires()
    {
        // 75.000.000 / 100.000.000 = 0,75 > 0,50
        var accounts = Accounts() with { TotalLiabilities = 75_000_000, TotalAssets = 100_000_000 };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.5000m), accounts));

        Assert.Equal(AlertRuleEngine.EndeudamientoElevado, alert.Code);
        Assert.Equal(0.75m, alert.MetricValue);
        Assert.Equal(0.5000m, alert.ThresholdValue);
        Assert.Equal("liabilities_over_assets", alert.MetricKey);
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenRatioBelowThreshold_DoesNotFire()
    {
        // Baseline is 50.000.000 / 100.000.000 = 0,50, below a 0,70 threshold.
        Assert.Empty(
            Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.7000m), Accounts()));
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenRatioExactlyAtThreshold_DoesNotFire()
    {
        // THE BOUNDARY. The baseline balance is already exactly 0,50. The comparison is strictly
        // greater, so sitting on the configured threshold is not yet an exceedance.
        Assert.Empty(
            Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.5000m), Accounts()));
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenRatioJustAboveThreshold_Fires()
    {
        // The other side of the same boundary: 51.000.000 / 100.000.000 = 0,51
        var accounts = Accounts() with { TotalLiabilities = 51_000_000, TotalAssets = 100_000_000 };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.5000m), accounts));

        Assert.Equal(0.51m, alert.MetricValue);
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenThresholdMissing_DoesNotFire()
    {
        // A threshold rule with no threshold is unevaluable, not "fires by default".
        var accounts = Accounts() with { TotalLiabilities = 75_000_000, TotalAssets = 100_000_000 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: null), accounts));
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenTotalAssetsZero_DoesNotFire()
    {
        // No division by zero, and no alert invented from an undefined ratio.
        var accounts = Accounts() with { TotalLiabilities = 75_000_000, TotalAssets = 0 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.5000m), accounts));
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenTotalAssetsMissing_DoesNotFire()
    {
        // An absent divisor is unknown, not "assume the ratio is high".
        var accounts = Accounts() with { TotalLiabilities = 75_000_000, TotalAssets = null };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.5000m), accounts));
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_WhenTotalLiabilitiesMissing_DoesNotFire()
    {
        // Likewise for the numerator: absent is not zero and not high.
        var accounts = Accounts() with { TotalLiabilities = null, TotalAssets = 100_000_000 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.EndeudamientoElevado, threshold: 0.5000m), accounts));
    }

    [Fact]
    public void Evaluate_EndeudamientoElevado_SubstitutesTokensInEsArFormat()
    {
        var accounts = Accounts() with { TotalLiabilities = 75_000_000, TotalAssets = 100_000_000 };
        var rule = Rule(
            AlertRuleEngine.EndeudamientoElevado,
            threshold: 0.5000m,
            template: "El endeudamiento sobre el activo es del {valor} %, " +
                      "por encima del umbral del {umbral} %.");

        var alert = Assert.Single(Evaluate(rule, accounts));

        // es-AR uses a decimal comma.
        Assert.Equal(
            "El endeudamiento sobre el activo es del 75,0 %, por encima del umbral del 50,0 %.",
            alert.Description);
    }

    // ── COBERTURA_INTERESES_INDETERMINABLE ────────────────────────────────────

    [Fact]
    public void Evaluate_CoberturaIntereses_WhenFinancialExpensesNull_Fires()
    {
        var accounts = Accounts() with { FinancialExpenses = null };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.CoberturaInteresesIndeterminable), accounts));

        Assert.Equal(AlertRuleEngine.CoberturaInteresesIndeterminable, alert.Code);
    }

    [Fact]
    public void Evaluate_CoberturaIntereses_WhenFinancialExpensesStated_DoesNotFire()
    {
        Assert.Empty(Evaluate(Rule(AlertRuleEngine.CoberturaInteresesIndeterminable), Accounts()));
    }

    [Fact]
    public void Evaluate_CoberturaIntereses_WhenFinancialExpensesZero_DoesNotFire()
    {
        // A reported zero is DETERMINABLE (the company had no financial expenses); only an absent
        // figure makes coverage impossible to compute.
        var accounts = Accounts() with { FinancialExpenses = 0 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.CoberturaInteresesIndeterminable), accounts));
    }

    // ── FLUJO_NO_CONCILIA ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_FlujoNoConcilia_WhenSectionsDoNotSumToNetChange_Fires()
    {
        // Sections sum to 30.000; the statement reports 60.000.
        var cashFlow = CashFlow() with { NetChange = 60_000, CashEnd = 260_000 };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.FlujoNoConcilia), Accounts(), cashFlow));

        Assert.Equal(AlertRuleEngine.FlujoNoConcilia, alert.Code);
    }

    [Fact]
    public void Evaluate_FlujoNoConcilia_WhenItReconciles_DoesNotFire()
    {
        Assert.Empty(Evaluate(Rule(AlertRuleEngine.FlujoNoConcilia), Accounts(), CashFlow()));
    }

    [Fact]
    public void Evaluate_FlujoNoConcilia_WhenWithinTolerance_DoesNotFire()
    {
        // BOUNDARY, delegated: 90 / 30.000 = 0,3 % relative — under the service's 0,5 % gate, so
        // the reconciliation passes and no alert is raised. This asserts the rule honours the
        // service's tolerance rather than carrying its own copy of it.
        var cashFlow = CashFlow() with { NetChange = 30_090, CashEnd = 230_090 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.FlujoNoConcilia), Accounts(), cashFlow));
    }

    [Fact]
    public void Evaluate_FlujoNoConcilia_WhenNotPresent_DoesNotFire()
    {
        Assert.Empty(Evaluate(
            Rule(AlertRuleEngine.FlujoNoConcilia), Accounts(), new CashFlowData { Present = false }));
    }

    [Fact]
    public void Evaluate_FlujoNoConcilia_WhenCheckNotEvaluable_DoesNotFire()
    {
        // Operating absent → the sections check is NotEvaluated, which is not a failure.
        // This is why the rule reads Reconcile's tri-state rather than Check's failures-only list.
        var cashFlow = CashFlow() with { Operating = null };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.FlujoNoConcilia), Accounts(), cashFlow));
    }

    [Fact]
    public void Evaluate_FlujoNoConcilia_WhenCashFlowSectionAbsentEntirely_DoesNotFire()
    {
        // Analyses predating the cash-flow feature have no cash_flow key at all → null model.
        Assert.Empty(Evaluate(Rule(AlertRuleEngine.FlujoNoConcilia), Accounts(), cashFlow: null));
    }

    // ── FLUJO_FINANCIACION_CERO ───────────────────────────────────────────────

    [Fact]
    public void Evaluate_FlujoFinanciacionCero_WhenFinancingIsZero_Fires()
    {
        var cashFlow = CashFlow() with { Financing = 0, NetChange = 60_000, CashEnd = 260_000 };

        var alert = Assert.Single(
            Evaluate(Rule(AlertRuleEngine.FlujoFinanciacionCero), Accounts(), cashFlow));

        Assert.Equal(AlertRuleEngine.FlujoFinanciacionCero, alert.Code);
    }

    [Fact]
    public void Evaluate_FlujoFinanciacionCero_WhenFinancingNonZero_DoesNotFire()
    {
        Assert.Empty(Evaluate(Rule(AlertRuleEngine.FlujoFinanciacionCero), Accounts(), CashFlow()));
    }

    [Fact]
    public void Evaluate_FlujoFinanciacionCero_WhenFinancingAbsent_DoesNotFire()
    {
        // Absent is unknown, not zero.
        var cashFlow = CashFlow() with { Financing = null };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.FlujoFinanciacionCero), Accounts(), cashFlow));
    }

    [Fact]
    public void Evaluate_FlujoFinanciacionCero_WhenNotPresent_DoesNotFire()
    {
        // present:false means every figure is null by contract — Financing == 0 must not be
        // inferred from the absence of a statement.
        Assert.Empty(Evaluate(
            Rule(AlertRuleEngine.FlujoFinanciacionCero), Accounts(),
            new CashFlowData { Present = false, Financing = 0 }));
    }

    // ── Catalogue semantics ───────────────────────────────────────────────────

    [Fact]
    public void Evaluate_DisabledRule_NeverFires()
    {
        // The condition holds exactly as in the fires-test above; only Enabled differs.
        var accounts = Accounts() with { NonCurrentLiabilities = 0, TotalLiabilities = 20_000_000 };

        Assert.Empty(Evaluate(Rule(AlertRuleEngine.PasivoCienCorriente, enabled: false), accounts));
    }

    [Fact]
    public void Evaluate_DisabledRule_DoesNotSuppressAnEnabledOne()
    {
        var accounts = Accounts() with
        {
            NonCurrentLiabilities = 0,
            TotalLiabilities      = 20_000_000,
            FinancialExpenses     = null,
        };

        var alerts = _sut.Evaluate(
            [
                Rule(AlertRuleEngine.PasivoCienCorriente, enabled: false),
                Rule(AlertRuleEngine.CoberturaInteresesIndeterminable, enabled: true),
            ],
            accounts, null);

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertRuleEngine.CoberturaInteresesIndeterminable, alert.Code);
    }

    [Fact]
    public void Evaluate_UnimplementedCode_IsSkippedNotGuessed()
    {
        // A catalogue row the engine has no rule for must produce nothing — never a default alert.
        Assert.Empty(Evaluate(Rule("REGLA_QUE_NO_EXISTE"), Accounts(), CashFlow()));
    }

    [Fact]
    public void Evaluate_CarriesSeverityAndTitleFromTheCatalogueRow()
    {
        // The binding the model channel never had: severity comes from the catalogue, not the model.
        var accounts = Accounts() with { FinancialExpenses = null };
        var rule = Rule(AlertRuleEngine.CoberturaInteresesIndeterminable, severity: "critical");

        var alert = Assert.Single(Evaluate(rule, accounts));

        Assert.Equal("critical", alert.Level);
        Assert.Equal($"Título de {AlertRuleEngine.CoberturaInteresesIndeterminable}", alert.Title);
    }

    [Fact]
    public void Evaluate_OrdersOutputByCodeOrdinal()
    {
        var accounts = Accounts() with
        {
            NonCurrentLiabilities = 0,
            TotalLiabilities      = 20_000_000,
            FinancialExpenses     = null,
        };
        var cashFlow = CashFlow() with { Financing = 0, NetChange = 60_000, CashEnd = 260_000 };

        // Supplied deliberately out of order.
        var alerts = _sut.Evaluate(
            [
                Rule(AlertRuleEngine.PasivoCienCorriente),
                Rule(AlertRuleEngine.FlujoFinanciacionCero),
                Rule(AlertRuleEngine.CoberturaInteresesIndeterminable),
            ],
            accounts, cashFlow);

        Assert.Equal(
            [
                AlertRuleEngine.CoberturaInteresesIndeterminable,
                AlertRuleEngine.FlujoFinanciacionCero,
                AlertRuleEngine.PasivoCienCorriente,
            ],
            alerts.Select(a => a.Code));
    }

    [Fact]
    public void Evaluate_SameInputTwice_ProducesIdenticalOutput()
    {
        // The property the whole change exists for.
        var accounts = Accounts() with
        {
            NonCurrentLiabilities = 0,
            TotalLiabilities      = 20_000_000,
            Inventory             = 30_000_000,
            FinancialExpenses     = null,
        };
        var cashFlow = CashFlow() with { NetChange = 60_000, CashEnd = 260_000 };
        AlertRuleDefinition[] rules =
        [
            Rule(AlertRuleEngine.PasivoCienCorriente),
            Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m),
            Rule(AlertRuleEngine.CoberturaInteresesIndeterminable),
            Rule(AlertRuleEngine.FlujoNoConcilia),
            Rule(AlertRuleEngine.FlujoFinanciacionCero),
        ];

        var first  = _sut.Evaluate(rules, accounts, cashFlow);
        var second = _sut.Evaluate(rules, accounts, cashFlow);

        Assert.Equal(first, second);
    }

    // ── Degenerate inputs ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_WithNoBalanceAtAll_ReturnsEmptyAndDoesNotThrow()
    {
        // BalanceJson absent or malformed → accounts null. No rule may invent a finding from it.
        var alerts = _sut.Evaluate(
            [
                Rule(AlertRuleEngine.PasivoCienCorriente),
                Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m),
                Rule(AlertRuleEngine.CoberturaInteresesIndeterminable),
                Rule(AlertRuleEngine.FlujoNoConcilia),
                Rule(AlertRuleEngine.FlujoFinanciacionCero),
            ],
            accounts: null, cashFlow: null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_WithEmptyCatalogue_ReturnsEmpty()
    {
        // A tenant with no catalogue rows gets no alerts — absence is never a hardcoded fallback.
        Assert.Empty(_sut.Evaluate([], Accounts(), CashFlow()));
    }

    [Fact]
    public void Evaluate_WithAllFiguresNull_ReturnsEmptyAndDoesNotThrow()
    {
        var alerts = _sut.Evaluate(
            [
                Rule(AlertRuleEngine.PasivoCienCorriente),
                Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m),
                Rule(AlertRuleEngine.FlujoNoConcilia),
                Rule(AlertRuleEngine.FlujoFinanciacionCero),
            ],
            new BalanceAccounts(), new CashFlowData { Present = true });

        Assert.Empty(alerts);
    }

    // ── ImplementedCodes ────────────────────────────────────────────────────────

    /// <summary>
    /// The configuration API refuses to enable a code outside this set, so the set is a gate, not a
    /// convenience. It is built by reflection over this class's public string constants; pinning it
    /// here means a future public constant that is not a rule code cannot silently widen that gate.
    /// </summary>
    [Fact]
    public void ImplementedCodes_IsExactlyTheSixDispatchedCodes()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                AlertRuleEngine.PasivoCienCorriente,
                AlertRuleEngine.InventarioElevado,
                AlertRuleEngine.EndeudamientoElevado,
                AlertRuleEngine.CoberturaInteresesIndeterminable,
                AlertRuleEngine.FlujoNoConcilia,
                AlertRuleEngine.FlujoFinanciacionCero,
            },
            AlertRuleEngine.ImplementedCodes.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Membership must mean "actually dispatched": a code in the set that the switch does not
    /// handle would be enabled by the API and then skipped by Evaluate on every analysis — exactly
    /// the silent no-op the guard exists to prevent. Asserted behaviourally rather than by
    /// inspecting the switch.
    /// </summary>
    [Fact]
    public void ImplementedCodes_AreAllDispatchedByEvaluate()
    {
        // Inputs chosen so every implemented rule fires: zero non-current liabilities against
        // positive total liabilities, inventory above the threshold, liabilities above the same
        // share of total assets, absent financial expenses, and a cash flow that neither
        // reconciles nor reports financing.
        var accounts = new BalanceAccounts
        {
            NonCurrentLiabilities = 0d,
            TotalLiabilities      = 1_000d,
            TotalAssets           = 1_000d,
            Inventory             = 900d,
            CurrentAssets         = 1_000d,
            FinancialExpenses     = null,
        };
        var cashFlow = new CashFlowData
        {
            Present   = true,
            Operating = 100d,
            Investing = 0d,
            Financing = 0d,
            NetChange = 999d,
        };

        var rules = AlertRuleEngine.ImplementedCodes
            .Select(code => Rule(code, threshold: 0.5000m))
            .ToList();

        var fired = _sut.Evaluate(rules, accounts, cashFlow);

        Assert.Equal(
            AlertRuleEngine.ImplementedCodes.ToHashSet(StringComparer.Ordinal),
            fired.Select(a => a.Code).ToHashSet(StringComparer.Ordinal));
    }

    // ── ThresholdUnits ──────────────────────────────────────────────────────────

    /// <summary>
    /// INVENTARIO_ELEVADO and ENDEUDAMIENTO_ELEVADO are the codes that read a ThresholdValue
    /// today, and both read it as a proportion. Configuration rejects a threshold on any code
    /// absent from this map, so a rule that gains one must declare it here — marking its constant
    /// — rather than by editing validation.
    /// </summary>
    [Fact]
    public void ThresholdUnits_ContainsExactlyTheRulesThatReadAThreshold()
    {
        Assert.Equal(
            new Dictionary<string, RuleThresholdUnit>(StringComparer.Ordinal)
            {
                [AlertRuleEngine.InventarioElevado]    = RuleThresholdUnit.Proportion,
                [AlertRuleEngine.EndeudamientoElevado] = RuleThresholdUnit.Proportion,
            },
            AlertRuleEngine.ThresholdUnits.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
    }

    /// <summary>A code cannot declare a threshold unit without the engine implementing it.</summary>
    [Fact]
    public void ThresholdUnits_OnlyCoversImplementedCodes()
    {
        Assert.All(
            AlertRuleUnitCodes(),
            code => Assert.Contains(code, AlertRuleEngine.ImplementedCodes));
    }

    /// <summary>
    /// Behavioural counterpart to the map: a rule marked as thresholded must actually change its
    /// verdict when the threshold moves, and one that is not must be indifferent to it — otherwise
    /// the map would be a comment rather than a fact.
    /// </summary>
    [Fact]
    public void ThresholdedRule_VerdictFollowsTheThreshold()
    {
        // Inventory is 60 % of current assets.
        var accounts = new BalanceAccounts { Inventory = 600d, CurrentAssets = 1_000d };

        var below = _sut.Evaluate(
            [Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m)], accounts, cashFlow: null);
        var above = _sut.Evaluate(
            [Rule(AlertRuleEngine.InventarioElevado, threshold: 0.7000m)], accounts, cashFlow: null);

        Assert.Single(below);
        Assert.Empty(above);
    }

    private static IEnumerable<string> AlertRuleUnitCodes() => AlertRuleEngine.ThresholdUnits.Keys;

    // ── metric_unit on the wire ─────────────────────────────────────────────────

    /// <summary>
    /// A fired alert states the unit its metric and threshold share, so a consumer knows whether
    /// they may be rendered as a percentage without keeping its own metric-key → unit registry.
    /// </summary>
    [Fact]
    public void FiredAlert_CarriesTheMetricUnit()
    {
        var alerts = _sut.Evaluate(
            [Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m)],
            new BalanceAccounts { Inventory = 600d, CurrentAssets = 1_000d },
            cashFlow: null);

        var alert = Assert.Single(alerts);
        Assert.Equal(RuleThresholdUnitWire.Proportion, alert.MetricUnit);
    }

    /// <summary>A rule that measures nothing numeric states no unit, rather than a misleading default.</summary>
    [Fact]
    public void FiredAlert_WithoutAMetric_HasNullMetricUnit()
    {
        var alerts = _sut.Evaluate(
            [Rule(AlertRuleEngine.PasivoCienCorriente)],
            new BalanceAccounts { NonCurrentLiabilities = 0d, TotalLiabilities = 1_000d },
            cashFlow: null);

        var alert = Assert.Single(alerts);
        Assert.Null(alert.MetricKey);
        Assert.Null(alert.MetricUnit);
    }

    /// <summary>
    /// Pins the wire contract: the key is metric_unit alongside metric_key/metric_value, and the
    /// value is the stable lowercase token — not the enum member name, and never its ordinal.
    /// </summary>
    [Fact]
    public void FiredAlert_MetricUnit_SerializesAsStableLowercaseString()
    {
        var alerts = _sut.Evaluate(
            [Rule(AlertRuleEngine.InventarioElevado, threshold: 0.5000m)],
            new BalanceAccounts { Inventory = 600d, CurrentAssets = 1_000d },
            cashFlow: null);

        var json = JsonSerializer.Serialize(alerts);

        Assert.Contains("\"metric_unit\":\"proportion\"", json);
    }

    /// <summary>
    /// Every enum member must declare a wire value, so adding one is a deliberate contract decision
    /// rather than something a consumer receives and cannot interpret.
    /// </summary>
    [Fact]
    public void EveryThresholdUnit_HasADeclaredWireValue()
    {
        Assert.All(
            Enum.GetValues<RuleThresholdUnit>(),
            unit => Assert.False(string.IsNullOrWhiteSpace(unit.ToWire())));
    }

    // ── Backward compatibility of the persisted shape ───────────────────────────

    /// <summary>
    /// [dbo].[AnalysisResults].[CatalogueAlerts] rows written before metric_unit existed must still
    /// parse, with the new field null. The column is persisted and read back as an opaque string by
    /// AnalysisController, so nothing in this codebase deserializes an old row — but the Portal
    /// does, and an added optional key must stay additive.
    /// </summary>
    [Fact]
    public void CatalogueAlert_PayloadWrittenBeforeMetricUnit_StillParsesWithNullUnit()
    {
        const string persistedBeforeThisChange = """
        [{"level":"medium","code":"INVENTARIO_ELEVADO","title":"Participación elevada",
          "description":"...","metric_key":"inventory_over_current_assets",
          "metric_value":0.6000,"threshold_value":0.5000}]
        """;

        var alerts = JsonSerializer.Deserialize<List<CatalogueAlert>>(persistedBeforeThisChange);

        var alert = Assert.Single(alerts!);
        Assert.Null(alert.MetricUnit);
        Assert.Equal("inventory_over_current_assets", alert.MetricKey);
        Assert.Equal(0.6000m, alert.MetricValue);
        Assert.Equal(0.5000m, alert.ThresholdValue);
    }

    // ── DML 086: the seeded ENDEUDAMIENTO_ELEVADO row ─────────────────────────

    // The values docs/database/086_alert_rule_catalogue_seed_endeudamiento_elevado.sql writes.
    private const decimal Seed086Threshold = 0.8500m;
    private const string  Seed086Severity  = "high";
    private const string  Seed086Name      = "Endeudamiento elevado";
    private const string  Seed086Template  =
        "El pasivo representa el {valor} % del activo total, por encima del umbral configurado de {umbral} %.";

    private static AlertRuleDefinition Seed086Row(bool enabled) => new()
    {
        RuleId                = Guid.NewGuid(),
        TenantId              = Tenant,
        Code                  = AlertRuleEngine.EndeudamientoElevado,
        Enabled               = enabled,
        Severity              = Seed086Severity,
        ThresholdValue        = Seed086Threshold,
        DisplayNameEs         = Seed086Name,
        DescriptionTemplateEs = Seed086Template,
        CreatedAt             = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt             = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// 086 seeds the row DISABLED: on a balance at 95 % leverage the engine's output is identical with the row
    /// and without it, next to an enabled rule that does fire.
    /// </summary>
    [Fact]
    public void Seed086_Disabled_ChangesNothing()
    {
        var accounts = Accounts() with { TotalLiabilities = 95_000_000, NonCurrentLiabilities = 75_000_000, Equity = 5_000_000 };
        var others   = new[] { Rule(AlertRuleEngine.InventarioElevado, threshold: 0.2000m) };

        var without = _sut.Evaluate(others, accounts, null);
        var with    = _sut.Evaluate([.. others, Seed086Row(enabled: false)], accounts, null);

        Assert.Single(without);
        Assert.Equal(
            JsonSerializer.Serialize(without),
            JsonSerializer.Serialize(with));
    }

    [Fact]
    public void Seed086_OnceEnabled_FiresAboveItsThreshold_WithTheTemplateRendered()
    {
        var accounts = Accounts() with { TotalLiabilities = 90_000_000, NonCurrentLiabilities = 70_000_000, Equity = 10_000_000 };

        var alert = Assert.Single(_sut.Evaluate([Seed086Row(enabled: true)], accounts, null));

        Assert.Equal(AlertRuleEngine.EndeudamientoElevado, alert.Code);
        Assert.Equal(
            "El pasivo representa el 90,0 % del activo total, por encima del umbral configurado de 85,0 %.",
            alert.Description);
        Assert.DoesNotContain("{", alert.Description);
    }

    [Fact]
    public void Seed086_OnceEnabled_DoesNotFireExactlyAtItsThreshold()
    {
        var accounts = Accounts() with { TotalLiabilities = 85_000_000, NonCurrentLiabilities = 65_000_000, Equity = 15_000_000 };

        Assert.Empty(_sut.Evaluate([Seed086Row(enabled: true)], accounts, null));
    }

    /// <summary>
    /// What makes the seeded row editable on the configuration screen: the code is implemented (so it can be
    /// enabled), it reads a PROPORTION threshold, and 0.8500 is one the PATCH accepts — in (0, 1], 4 decimals.
    /// </summary>
    [Fact]
    public void Seed086_IsAnImplementedProportionRule_AndItsPlaceholderIsAValidThreshold()
    {
        Assert.Contains(AlertRuleEngine.EndeudamientoElevado, AlertRuleEngine.ImplementedCodes);
        Assert.Equal(RuleThresholdUnit.Proportion, AlertRuleEngine.ThresholdUnits[AlertRuleEngine.EndeudamientoElevado]);
        Assert.InRange(Seed086Threshold, 0.0001m, 1m);
        Assert.Equal(Seed086Threshold, decimal.Round(Seed086Threshold, 4));
    }
}
