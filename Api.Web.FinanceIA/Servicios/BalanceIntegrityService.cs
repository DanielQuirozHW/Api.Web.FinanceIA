// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Implementation ──────────────────────────────────────────────────────────────

/// <summary>
/// Stateless, deterministic integrity checks over an extracted balance. No DI dependencies — can
/// be instantiated directly in tests. Every check runs only when all of its operands were
/// extracted; otherwise its name is listed under <c>skipped</c>, so an older model output (no
/// rubros, no prior year) simply runs fewer checks.
///
/// The anomaly texts deliberately contain no "%": AlertRatioReconciliationService rewrites
/// "X% del &lt;denominador&gt;" clauses in the anomalies list, and these must never be touched by it.
/// </summary>
public sealed class BalanceIntegrityService : IBalanceIntegrityService
{
    /// <summary>Ceiling applied to the extraction confidence when any check fails.</summary>
    public const decimal ConfidenceCap = 0.69m;

    // ── Check names (persisted in integrity.checks[].name / integrity.skipped) ────
    public const string AssetsComponentsCheck           = "assets_components";
    public const string LiabilitiesComponentsCheck      = "liabilities_components";
    public const string AccountingEquationCheck         = "accounting_equation";
    public const string CurrentAssetsItemsCheck         = "current_assets_items";
    public const string CurrentLiabilitiesItemsCheck    = "current_liabilities_items";
    public const string NonCurrentLiabilitiesItemsCheck = "non_current_liabilities_items";
    public const string NetProfitReconciliationCheck    = "net_profit_reconciliation";
    public const string SignAssetsCheck                 = "sign_assets";
    public const string SignCurrentAssetsCheck          = "sign_current_assets";
    public const string SignLiabilitiesCheck            = "sign_liabilities";
    public const string SignInventoryCheck              = "sign_inventory";
    public const string SignNetRevenueCheck             = "sign_net_revenue";
    public const string ColumnSwapCheck                 = "column_swap";

    /// <summary>
    /// The totals/structure checks: arithmetic between printed lines, or a figure read from the other
    /// column. A failure here means the figures were MISREAD (two sides swapped, a rotated table read
    /// across), so ClaudeService runs a second, differently-read extraction pass when the first one
    /// fails any of them, and the pass selection counts them. The remaining checks
    /// (net_profit_reconciliation, the sign checks) are softer — a tax-sign convention, a legitimately
    /// odd figure — and never trigger a second pass on their own.
    /// </summary>
    public static readonly IReadOnlySet<string> StructureChecks = new HashSet<string>(StringComparer.Ordinal)
    {
        AssetsComponentsCheck,
        LiabilitiesComponentsCheck,
        AccountingEquationCheck,
        CurrentAssetsItemsCheck,
        CurrentLiabilitiesItemsCheck,
        NonCurrentLiabilitiesItemsCheck,
        ColumnSwapCheck,
    };

    /// <summary>
    /// The checks that must have RUN (passed or failed, not skipped) for an extraction to count as
    /// verified: the three that hold the balance sheet's own totals together. A report in which any
    /// of them was skipped still has <c>passed</c> = true when nothing that ran failed, so
    /// <c>passed</c> alone proves little. Read by <c>ExtractionIntegrityReader</c>; never by the
    /// score, the pass selection or <see cref="BalanceIntegrityReport.Passed"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> VerificationChecks =
    [
        AssetsComponentsCheck,
        LiabilitiesComponentsCheck,
        AccountingEquationCheck,
    ];

    /// <summary>
    /// The checks whose failure CAN describe how the statement is PRESENTED, not figures that do not add up:
    /// <see cref="NetProfitReconciliationCheck"/>, which fails on a correct reading when the income statement
    /// puts RECPAM after the income tax, prints no pre-tax subtotal, or prints one that leaves financial results
    /// out (FINEX, HSE, INTACTO). It fails on a misread too — a sign lost (<see cref="BalanceIntegrityCheck.SignFlip"/>)
    /// or the pre-tax result and the tax taken from the comparative column (<see cref="BalanceIntegrityCheck.ColumnMix"/>)
    /// — which <see cref="FailsOnlyOnPresentation"/> tells apart. A report whose ONLY failures are presentations is a
    /// caveat for the score reliability, the decision gate and ValidationStatus — never for
    /// <see cref="BalanceIntegrityReport.Passed"/>, which stays the truthful report, nor for the confidence cap.
    /// </summary>
    public static readonly IReadOnlySet<string> PresentationChecks = new HashSet<string>(StringComparer.Ordinal)
    {
        NetProfitReconciliationCheck,
    };

    /// <summary>
    /// True when <paramref name="failedChecks"/> names at least one check, every one is a
    /// <see cref="PresentationChecks"/> member, and — when <see cref="NetProfitReconciliationCheck"/> is among
    /// them — its failure is KNOWN not to be a sign flip (<paramref name="netProfitSignFlip"/> == false) and is
    /// NOT known to be a column mix (<paramref name="netProfitColumnMix"/> != true). A sign flip (true) is a
    /// misread net profit, and an unknown flip (null) is not assumed away. A column mix (true) is a misread pre-tax
    /// result and tax; an unknown one (null: no comparative net profit, or a blob recorded before the verdict
    /// existed) keeps the verdict it always had. Null or empty names are false: nothing known failed, or the names
    /// are unknown (a pre-names block). The ONE place the rule lives.
    /// </summary>
    public static bool FailsOnlyOnPresentation(
        IEnumerable<string>? failedChecks, bool? netProfitSignFlip, bool? netProfitColumnMix)
    {
        if (failedChecks is null)
            return false;

        var any       = false;
        var netProfit = false;
        foreach (var name in failedChecks)
        {
            if (name is null || !PresentationChecks.Contains(name))
                return false;
            any = true;
            netProfit |= string.Equals(name, NetProfitReconciliationCheck, StringComparison.Ordinal);
        }

        return any && (!netProfit || (netProfitSignFlip == false && netProfitColumnMix != true));
    }

    /// <summary>
    /// True when <paramref name="netProfit"/> is, within the check's tolerance, the NEGATIVE of either reading
    /// <c>net_profit_reconciliation</c> accepts — signed (result before tax + income tax) or magnitude (result
    /// before tax − |income tax|): the net profit was read with the wrong sign (parentheses lost). The ONE
    /// sign-flip rule: the check records it and a stored blob without that record is re-judged by it.
    /// </summary>
    public static bool IsNetProfitSignFlip(double netProfit, double resultBeforeTax, double incomeTax)
    {
        var signed    = resultBeforeTax + incomeTax;
        var magnitude = resultBeforeTax - Math.Abs(incomeTax);

        return Within(-signed, netProfit, Tolerance).Passed || Within(-magnitude, netProfit, Tolerance).Passed;
    }

    /// <summary>
    /// The sign-flip verdict of a FRESHLY evaluated report: null when net_profit_reconciliation did not fail,
    /// otherwise whether its check recorded <see cref="BalanceIntegrityCheck.SignFlip"/> (absent there means
    /// "checked, not a flip").
    /// </summary>
    public static bool? NetProfitSignFlipOf(IEnumerable<BalanceIntegrityCheck>? checks)
        => checks?.FirstOrDefault(c => !c.Passed && string.Equals(c.Name, NetProfitReconciliationCheck, StringComparison.Ordinal))
               is { } failed
            ? failed.SignFlip == true
            : null;

    /// <summary>
    /// Whether result before tax and income tax, as read, are the COMPARATIVE column's: either reading
    /// <c>net_profit_reconciliation</c> accepts — signed (result before tax + income tax) or magnitude (result before
    /// tax − |income tax|) — equals <paramref name="priorNetProfit"/> within the column-swap tolerance (the bar for
    /// "a figure read from the other column"; a comparative column that reconciles as printed matches it almost
    /// exactly). Null when it cannot be checked: no comparative net profit, or a zero one — two zeros are not
    /// evidence of anything. The ONE column-mix rule: the check records it; a stored blob without that record is
    /// NOT re-judged (it stays unknown, so its verdict stays the one it always had).
    /// </summary>
    public static bool? NetProfitColumnMix(double resultBeforeTax, double incomeTax, double? priorNetProfit)
    {
        if (priorNetProfit is not { } prior || prior == 0d)
            return null;

        var signed    = resultBeforeTax + incomeTax;
        var magnitude = resultBeforeTax - Math.Abs(incomeTax);

        return Within(prior, signed, ColumnSwapTolerance).Passed || Within(prior, magnitude, ColumnSwapTolerance).Passed;
    }

    /// <summary>
    /// The column-mix verdict of a report: null when net_profit_reconciliation did not fail, otherwise what its check
    /// recorded in <see cref="BalanceIntegrityCheck.ColumnMix"/> — absent stays null ("could not be checked"), unlike
    /// <see cref="NetProfitSignFlipOf"/>, whose absent record means "checked, not a flip".
    /// </summary>
    public static bool? NetProfitColumnMixOf(IEnumerable<BalanceIntegrityCheck>? checks)
        => checks?.FirstOrDefault(c => !c.Passed && string.Equals(c.Name, NetProfitReconciliationCheck, StringComparison.Ordinal))
               is { } failed
            ? failed.ColumnMix
            : null;

    // Arithmetic tolerance: a printed statement rounds every line, so an exact match is not the bar.
    // internal: TrialBalanceNormalizer confirms a trial balance's reading with the SAME bar the
    // accounting_equation check applies afterwards.
    internal const double Tolerance = 0.01;

    /// <summary>
    /// The sentence a column mix appends to the net_profit_reconciliation anomaly (no figure: the first sentence
    /// already quotes them). No "%", like every anomaly text here.
    /// </summary>
    internal const string ColumnMixAnomalySentence =
        "El resultado antes de impuestos y el impuesto leídos coinciden con el resultado neto del ejercicio anterior: " +
        "es posible que se hayan tomado de la columna comparativa.";

    // A current-year figure "equal" to a prior-year one: tight enough that only a figure read from
    // the other column matches, loose enough to absorb a rounded decimal.
    private const double ColumnSwapTolerance = 0.0001;

    // "The whole column is the prior year's" needs enough evidence to rule out a static balance.
    private const int MinTotalsForWholeColumnSwap = 3;

    // ── BalanceJson line keys ─────────────────────────────────────────────────────
    private const string Assets                = "assets";
    private const string CurrentAssets         = "current_assets";
    private const string NonCurrentAssets      = "non_current_assets";
    private const string Liabilities           = "liabilities";
    private const string CurrentLiabilities    = "current_liabilities";
    private const string NonCurrentLiabilities = "non_current_liabilities";
    private const string Equity                = "equity";
    private const string NetRevenue            = "net_revenue";
    private const string NetProfit             = "net_profit";
    private const string Inventory             = "inventory";

    // Which scoring ratios each line feeds — mirrors RatioCalculationService's formulas.
    private static readonly IReadOnlyDictionary<string, string[]> RatiosByLine =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [CurrentAssets]      = ["liquidez_corriente", "prueba_acida"],
            [CurrentLiabilities] = ["liquidez_corriente", "prueba_acida"],
            [Inventory]          = ["prueba_acida"],
            ["prepaid_expenses"] = ["prueba_acida"],
            [Assets]             = ["endeudamiento", "roa"],
            [Liabilities]        = ["endeudamiento", "leverage"],
            [Equity]             = ["leverage", "roe"],
            [NetProfit]          = ["roe", "roa", "margen_neto"],
            [NetRevenue]         = ["margen_neto"],
            ["operating_result"]   = ["cobertura_intereses"],
            ["financial_expenses"] = ["cobertura_intereses"],
        };

    // es-AR noun phrase + agreeing participle, for the column-swap anomaly.
    private static readonly IReadOnlyDictionary<string, (string Noun, string Informed)> LineText =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [Assets]                = ("el activo total",         "informado"),
            [CurrentAssets]         = ("el activo corriente",     "informado"),
            [NonCurrentAssets]      = ("el activo no corriente",  "informado"),
            [Liabilities]           = ("el pasivo total",         "informado"),
            [CurrentLiabilities]    = ("el pasivo corriente",     "informado"),
            [NonCurrentLiabilities] = ("el pasivo no corriente",  "informado"),
            [Equity]                = ("el patrimonio neto",      "informado"),
            [NetRevenue]            = ("las ventas netas",        "informadas"),
            [NetProfit]             = ("el resultado neto",       "informado"),
        };

    // Totals compared across the two columns by the column-swap check.
    private static readonly string[] SwapLines =
    [
        Assets, CurrentAssets, NonCurrentAssets, Liabilities, CurrentLiabilities,
        NonCurrentLiabilities, Equity, NetRevenue, NetProfit
    ];

    // ── IBalanceIntegrityService ──────────────────────────────────────────────────

    /// <inheritdoc />
    public BalanceIntegrityOutcome Evaluate(ExtractedBalance balance)
    {
        ArgumentNullException.ThrowIfNull(balance);

        var acc = new Accumulator(balance.AppliedScale);
        var c   = balance.Current;

        // ── Subtotals vs totals ──────────────────────────────────────────────────
        // The schema has no non_current_assets_items, so an unprinted activo no corriente never has rubros.
        var assetsSum = ComponentsSum(c.TotalAssets,
            Component(CurrentAssets, c.CurrentAssets, balance.CurrentAssetsItems),
            Component(NonCurrentAssets, c.NonCurrentAssets, null));
        Compare(acc, AssetsComponentsCheck, c.TotalAssets, assetsSum.Expected,
            [Assets, CurrentAssets, NonCurrentAssets],
            (e, a) => $"El activo total informado ({acc.Money(a)}) no coincide con la suma " +
                      $"{ComponentsText(assetsSum.InferredFromItems, CurrentAssets, "activo corriente", "activo no corriente")} ({acc.Money(e)}).",
            assetsSum.InferredZero, assetsSum.InferredFromItems);

        var liabilitiesSum = ComponentsSum(c.TotalLiabilities,
            Component(CurrentLiabilities, c.CurrentLiabilities, balance.CurrentLiabilitiesItems),
            Component(NonCurrentLiabilities, c.NonCurrentLiabilities, balance.NonCurrentLiabilitiesItems));
        Compare(acc, LiabilitiesComponentsCheck, c.TotalLiabilities, liabilitiesSum.Expected,
            [Liabilities, CurrentLiabilities, NonCurrentLiabilities],
            (e, a) => $"El pasivo total informado ({acc.Money(a)}) no coincide con la suma " +
                      $"{ComponentsText(liabilitiesSum.InferredFromItems, CurrentLiabilities, "pasivo corriente", "pasivo no corriente")} ({acc.Money(e)}).",
            liabilitiesSum.InferredZero, liabilitiesSum.InferredFromItems);

        Compare(acc, AccountingEquationCheck, c.TotalAssets, Sum(c.TotalLiabilities, c.Equity),
            [Assets, Liabilities, Equity],
            (e, a) => $"El activo total informado ({acc.Money(a)}) no coincide con la suma del pasivo total y el patrimonio neto ({acc.Money(e)}).");

        // ── Rubro lines vs their subtotal ────────────────────────────────────────
        Compare(acc, CurrentAssetsItemsCheck, c.CurrentAssets, SumItems(balance.CurrentAssetsItems),
            [CurrentAssets],
            (e, a) => $"La suma de los rubros del activo corriente ({acc.Money(e)}) no coincide con el activo corriente informado ({acc.Money(a)}).");

        Compare(acc, CurrentLiabilitiesItemsCheck, c.CurrentLiabilities, SumItems(balance.CurrentLiabilitiesItems),
            [CurrentLiabilities],
            (e, a) => $"La suma de los rubros del pasivo corriente ({acc.Money(e)}) no coincide con el pasivo corriente informado ({acc.Money(a)}).");

        Compare(acc, NonCurrentLiabilitiesItemsCheck, c.NonCurrentLiabilities, SumItems(balance.NonCurrentLiabilitiesItems),
            [NonCurrentLiabilities],
            (e, a) => $"La suma de los rubros del pasivo no corriente ({acc.Money(e)}) no coincide con el pasivo no corriente informado ({acc.Money(a)}).");

        // ── Income statement ─────────────────────────────────────────────────────
        NetProfitCheck(acc, c.NetProfit, balance.ResultBeforeTax, balance.IncomeTax, balance.PriorYear?.NetProfit);

        // ── Signs ────────────────────────────────────────────────────────────────
        Sign(acc, SignAssetsCheck, Assets, c.TotalAssets, strictlyPositive: true,
            v => $"El activo total informado ({acc.Money(v)}) debería ser mayor que cero.");
        // Zero current assets is legitimate beside a positive total (a holding whose assets are all
        // non-current): only a negative figure fails then. Without a positive total a zero is still suspect.
        Sign(acc, SignCurrentAssetsCheck, CurrentAssets, c.CurrentAssets, strictlyPositive: c.TotalAssets is not > 0,
            v => v < 0
                ? $"El activo corriente informado ({acc.Money(v)}) no puede ser negativo."
                : $"El activo corriente informado ({acc.Money(v)}) debería ser mayor que cero.");
        // Zero total liabilities is legitimate (a company with no debts, e.g. in its first fiscal
        // year, printing "–"): only a negative figure is an extraction problem.
        Sign(acc, SignLiabilitiesCheck, Liabilities, c.TotalLiabilities, strictlyPositive: false,
            v => $"El pasivo total informado ({acc.Money(v)}) no puede ser negativo.");
        Sign(acc, SignInventoryCheck, Inventory, c.Inventory, strictlyPositive: false,
            v => $"Los bienes de cambio informados ({acc.Money(v)}) no pueden ser negativos.");
        // Zero sales is legitimate (a holding, a pre-operating company, a year without sales): only
        // a negative figure is an extraction problem.
        Sign(acc, SignNetRevenueCheck, NetRevenue, c.NetRevenue, strictlyPositive: false,
            v => $"Las ventas netas informadas ({acc.Money(v)}) no pueden ser negativas.");

        // ── Prior-year column ────────────────────────────────────────────────────
        ColumnSwap(acc, c, balance.PriorYear);

        var affected = ScoringDefaults.ThresholdRatios
            .Where(r => acc.FailedLines.Any(l => RatiosByLine.TryGetValue(l, out var rs) && rs.Contains(r)))
            .ToList();

        var report = new BalanceIntegrityReport
        {
            Passed         = acc.Checks.All(ch => ch.Passed),
            Checks         = acc.Checks,
            Skipped        = acc.Skipped,
            AffectedRatios = affected,
        };

        return new BalanceIntegrityOutcome(report, acc.Anomalies);
    }

    // ── Private methods ───────────────────────────────────────────────────────────

    private static void Compare(
        Accumulator acc, string name, double? actual, double? expected, string[] lines,
        Func<double, double, string> anomaly, IReadOnlyList<string>? inferredZero = null,
        IReadOnlyList<string>? inferredFromItems = null)
    {
        if (actual is not { } a || expected is not { } e)
        {
            acc.Skipped.Add(name);
            return;
        }

        var (passed, diffPct) = Within(e, a, Tolerance);
        acc.Checks.Add(new BalanceIntegrityCheck
        {
            Name = name, Passed = passed, Expected = e, Actual = a, DiffPct = diffPct, Lines = lines,
            InferredZero = inferredZero, InferredFromItems = inferredFromItems
        });

        if (!passed)
            acc.Fail(lines, anomaly(e, a));
    }

    // net_profit ≈ result_before_tax + income_tax when the tax is SIGNED as printed (a charge is
    // negative, a benefit positive), or ≈ result_before_tax − |income_tax| when the tax was
    // reported as a magnitude. Either reading reconciling passes. A failure records whether the net profit's
    // sign was lost (SignFlip) and whether the pre-tax result and the tax are the comparative column's (ColumnMix):
    // both misreads, never presentations. Independent verdicts — both can hold at once.
    private static void NetProfitCheck(
        Accumulator acc, double? netProfit, double? resultBeforeTax, double? incomeTax, double? priorNetProfit)
    {
        if (netProfit is not { } np || resultBeforeTax is not { } rbt || incomeTax is not { } tax)
        {
            acc.Skipped.Add(NetProfitReconciliationCheck);
            return;
        }

        var signed    = rbt + tax;
        var magnitude = rbt - Math.Abs(tax);
        var (signedOk, signedDiff)       = Within(signed, np, Tolerance);
        var (magnitudeOk, magnitudeDiff) = Within(magnitude, np, Tolerance);

        var passed    = signedOk || magnitudeOk;
        var expected  = signedDiff <= magnitudeDiff ? signed : magnitude;
        var columnMix = passed ? null : NetProfitColumnMix(rbt, tax, priorNetProfit);
        string[] lines = [NetProfit];

        acc.Checks.Add(new BalanceIntegrityCheck
        {
            Name     = NetProfitReconciliationCheck,
            Passed   = passed,
            Expected = expected,
            Actual   = np,
            DiffPct  = Math.Min(signedDiff, magnitudeDiff),
            Lines    = lines,
            // A misread, not a presentation: recorded so every reader applies FailsOnlyOnPresentation alike.
            SignFlip  = !passed && IsNetProfitSignFlip(np, rbt, tax) ? true : null,
            ColumnMix = columnMix,
        });

        if (!passed)
        {
            acc.Fail(lines,
                $"El resultado neto informado ({acc.Money(np)}) no coincide con el resultado antes del impuesto a " +
                $"las ganancias neto de ese impuesto ({acc.Money(expected)})." +
                (columnMix == true ? " " + ColumnMixAnomalySentence : string.Empty));
        }
    }

    private static void Sign(
        Accumulator acc, string name, string line, double? value, bool strictlyPositive,
        Func<double, string> anomaly)
    {
        if (value is not { } v)
        {
            acc.Skipped.Add(name);
            return;
        }

        var passed = strictlyPositive ? v > 0 : v >= 0;
        string[] lines = [line];
        acc.Checks.Add(new BalanceIntegrityCheck { Name = name, Passed = passed, Actual = v, Lines = lines });

        if (!passed)
            acc.Fail(lines, anomaly(v));
    }

    // Two ways a figure betrays the comparative column:
    //   1. every comparable current-year total equals the prior year's own line → the same column
    //      was read twice;
    //   2. a current-year total equals a DIFFERENT prior-year line (the pasivo corriente reading as
    //      last year's "Total del pasivo y patrimonio neto") → one figure came from the other column.
    // Zero figures never count: two zeros are not evidence of anything. A figure that also equals
    // its OWN prior-year line is explained by an unchanged line, not by a swap.
    private static void ColumnSwap(Accumulator acc, BalanceAccounts current, BalanceAccounts? prior)
    {
        if (prior is null)
        {
            acc.Skipped.Add(ColumnSwapCheck);
            return;
        }

        var cur = Figures(current);
        var pri = Figures(prior);

        var comparable = SwapLines.Where(l => IsNonZero(cur[l]) && IsNonZero(pri[l])).ToList();
        if (!SwapLines.Any(l => IsNonZero(cur[l])) || !SwapLines.Any(l => IsNonZero(pri[l])))
        {
            acc.Skipped.Add(ColumnSwapCheck);
            return;
        }

        if (comparable.Count >= MinTotalsForWholeColumnSwap
            && comparable.All(l => Within(pri[l]!.Value, cur[l]!.Value, ColumnSwapTolerance).Passed))
        {
            acc.Checks.Add(new BalanceIntegrityCheck
            {
                Name = ColumnSwapCheck, Passed = false, Lines = comparable, ComparedWith = "prior_year"
            });
            acc.Fail(comparable,
                "Los totales informados para el ejercicio coinciden con los del ejercicio anterior; es posible " +
                "que se haya leído la misma columna para ambos ejercicios.");
            return;
        }

        var hits = 0;
        foreach (var line in SwapLines)
        {
            if (cur[line] is not { } value || value == 0d)
                continue;

            if (pri[line] is { } own && Within(own, value, ColumnSwapTolerance).Passed)
                continue;

            foreach (var other in SwapLines)
            {
                if (other == line || pri[other] is not { } candidate || candidate == 0d)
                    continue;

                var (same, diffPct) = Within(candidate, value, ColumnSwapTolerance);
                if (!same)
                    continue;

                string[] lines = [line];
                acc.Checks.Add(new BalanceIntegrityCheck
                {
                    Name         = ColumnSwapCheck,
                    Passed       = false,
                    Expected     = candidate,
                    Actual       = value,
                    DiffPct      = diffPct,
                    Lines        = lines,
                    ComparedWith = $"prior_year.{other}"
                });

                var (noun, informed) = LineText[line];
                acc.Fail(lines,
                    $"{Capitalize(noun)} {informed} ({acc.Money(value)}) coincide con {LineText[other].Noun} del " +
                    "ejercicio anterior; es posible que se haya leído la columna comparativa.");
                hits++;
                break;
            }
        }

        if (hits == 0)
            acc.Checks.Add(new BalanceIntegrityCheck { Name = ColumnSwapCheck, Passed = true, Lines = comparable });
    }

    private static Dictionary<string, double?> Figures(BalanceAccounts a) => new(StringComparer.Ordinal)
    {
        [Assets]                = a.TotalAssets,
        [CurrentAssets]         = a.CurrentAssets,
        [NonCurrentAssets]      = a.NonCurrentAssets,
        [Liabilities]           = a.TotalLiabilities,
        [CurrentLiabilities]    = a.CurrentLiabilities,
        [NonCurrentLiabilities] = a.NonCurrentLiabilities,
        [Equity]                = a.Equity,
        [NetRevenue]            = a.NetRevenue,
        [NetProfit]             = a.NetProfit,
    };

    private static bool IsNonZero(double? value) => value is { } v && v != 0d;

    // The sum a *_components check compares with its total, in order:
    //   1. both components printed: their sum;
    //   2. no total: null (the check is skipped);
    //   3. exactly one null, with NO rubros of its own, while the other alone equals the total within the
    //      tolerance: a section the statement does not print, evaluated as zero (InferredZero);
    //   4. exactly one null WITH rubros, every amount read and summing to ≥ 0: the other component plus those
    //      rubros (InferredFromItems) — the rubros are printed lines, so they must explain the total; a rubro
    //      that contradicts it (a figure read from the other half of a rotated sheet) FAILS the check instead of
    //      hiding behind a skip. A negative sum proves nothing: a trial balance's credit-signed rubros under a
    //      null subtotal are never sign-normalized (TrialBalanceNormalizer flips rubros against a printed
    //      subtotal only), so it keeps the skip;
    //   5. anything else (both null, a rubro without an amount): null, so the check is skipped — a null
    //      subtotal may also be an illegible one, which must stay unverified.
    // Every inference lives inside the check only: the extracted figures stay as they were read.
    private static ComponentsSumResult ComponentsSum(double? total, ComponentFigures first, ComponentFigures second)
    {
        if (first.Value is { } a && second.Value is { } b)
            return new ComponentsSumResult(a + b, null, null);

        if (total is not { } t)
            return ComponentsSumResult.Skip;

        if (first.Value is null && second.Value is { } onlySecond)
            return FromOne(t, onlySecond, first);

        if (second.Value is null && first.Value is { } onlyFirst)
            return FromOne(t, onlyFirst, second);

        return ComponentsSumResult.Skip;
    }

    // Exactly one component printed (present); missing is the null one.
    private static ComponentsSumResult FromOne(double total, double present, ComponentFigures missing)
    {
        if (!missing.HasItems)
            return Within(total, present, Tolerance).Passed
                ? new ComponentsSumResult(present, [missing.Key], null)
                : ComponentsSumResult.Skip;

        return missing.ItemsSum is { } items && items >= 0d
            ? new ComponentsSumResult(present + items, null, [missing.Key])
            : ComponentsSumResult.Skip;
    }

    private static ComponentFigures Component(string key, double? value, IReadOnlyList<BalanceLineItem>? items)
        => new(key, value, HasItems(items), SumItems(items));

    // The tail of a *_components anomaly after "la suma ": byte-identical to the original text unless a
    // component was evaluated from its rubros, which it then names.
    private static string ComponentsText(IReadOnlyList<string>? inferredFromItems, string firstKey, string first, string second)
    {
        if (inferredFromItems is not [var key])
            return $"del {first} y el {second}";

        return string.Equals(key, firstKey, StringComparison.Ordinal)
            ? $"de los rubros del {first} y el {second}"
            : $"del {first} y los rubros del {second}";
    }

    private static bool HasItems(IReadOnlyList<BalanceLineItem>? items) => items is { Count: > 0 };

    private static double? Sum(double? x, double? y) => x is { } a && y is { } b ? a + b : null;

    // Null when there are no lines or any line's amount was not extracted: a partial sum proves nothing.
    private static double? SumItems(IReadOnlyList<BalanceLineItem>? items)
    {
        if (items is null || items.Count == 0 || items.Any(i => i.Amount is null))
            return null;

        return items.Sum(i => i.Amount!.Value);
    }

    internal static (bool Passed, double DiffPct) Within(double expected, double actual, double tolerance)
    {
        var scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        if (scale == 0d)
            return (true, 0d);

        var diff = Math.Abs(expected - actual);
        return (diff <= tolerance * scale, Math.Round(diff / scale * 100d, 2, MidpointRounding.AwayFromZero));
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ── Nested types ──────────────────────────────────────────────────────────────

    // One component of a *_components check: its subtotal, whether it has rubros, and their sum (null when
    // there are none or any amount is unread).
    private readonly record struct ComponentFigures(string Key, double? Value, bool HasItems, double? ItemsSum);

    private readonly record struct ComponentsSumResult(
        double? Expected, IReadOnlyList<string>? InferredZero, IReadOnlyList<string>? InferredFromItems)
    {
        public static ComponentsSumResult Skip => new(null, null, null);
    }

    private sealed class Accumulator(double appliedScale)
    {
        public List<BalanceIntegrityCheck> Checks      { get; } = [];
        public List<string>                Skipped     { get; } = [];
        public List<string>                Anomalies   { get; } = [];
        public HashSet<string>             FailedLines { get; } = new(StringComparer.Ordinal);

        public void Fail(IEnumerable<string> lines, string anomaly)
        {
            FailedLines.UnionWith(lines);
            Anomalies.Add(anomaly);
        }

        // Quotes a figure in the unit the analyst reads on the document. The checks run in pesos,
        // but the model's own narrative and anomalies quote a balance "expresado en miles" as
        // printed, so "$43.247.190.286" beside the model's "$43.247.190" would read as two different
        // figures. Whole units, es-AR grouping, built culture-independently so the text is identical
        // whatever ICU data the host carries: "$43.247.190.286", "-$1.234", "$43.247.190, en miles".
        public string Money(double pesos) => FormatMoney(pesos, appliedScale);
    }

    /// <summary>
    /// A figure in pesos quoted in the unit the analyst reads on the document (see <c>Accumulator.Money</c>):
    /// whole units, es-AR grouping, culture-independent — "$43.247.190.286", "-$1.234", "$43.247.190, en miles".
    /// </summary>
    internal static string FormatMoney(double pesos, double appliedScale)
    {
        var (divisor, suffix) = appliedScale switch
        {
            1_000d     => (1_000d, ", en miles"),
            1_000_000d => (1_000_000d, ", en millones"),
            _          => (1d, string.Empty),
        };

        var rounded = Math.Round(pesos / divisor, MidpointRounding.AwayFromZero);
        var body    = Math.Abs(rounded).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
        return (rounded < 0 ? $"-${body}" : $"${body}") + suffix;
    }
}
