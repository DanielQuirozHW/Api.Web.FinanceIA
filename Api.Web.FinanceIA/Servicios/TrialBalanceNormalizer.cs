// Project : Api.Web.FinanceIA
// Created : 2026-11-04

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>What <see cref="TrialBalanceNormalizer.Normalize"/> returns: the figures to check and persist, the audit, the anomalies.</summary>
public sealed record TrialBalanceOutcome(ExtractedBalance Balance, TrialBalanceAudit Audit, IReadOnlyList<string> Anomalies);

/// <summary>
/// Brings a TRIAL BALANCE ("Balance de Presentación", balance de sumas y saldos printed from an accounting
/// system; extraction prompt 2.19 <c>document_kind = "trial_balance"</c>) to the schema the rest of the
/// pipeline reads. Pure and static; every change is arithmetic, and the ones that move equity or the year's
/// result happen ONLY when the accounting equation proves the reading:
/// <list type="number">
/// <item><b>Signs.</b> A ledger prints credit balances negative; the schema wants positive liabilities and
/// sales. A negative liabilities total or net revenue becomes its magnitude, and a liabilities rubro list
/// whose sum is the NEGATIVE of its (now positive) subtotal is negated line by line.</item>
/// <item><b>Equity.</b> With R the sum of the printed results accounts and Ep the printed equity, the ledger
/// sign c (−1: credits printed negative) is the one for which assets = liabilities + c·(Ep + R) within the
/// accounting_equation's own tolerance — a pre-closing balance, whose equity then INCLUDES the year's result
/// c·R — or, failing that, assets = liabilities + c·Ep (the result is already in equity). The model's own
/// convention flag is tried first. When neither closes nothing moves: a double-listed group or a misread ledger
/// fails safe, and the integrity checks report it as they would have.</item>
/// <item><b>Results.</b> With c known: net_profit = c·R; income_tax and result_before_tax when an income-tax
/// account is listed; financial_expenses from the interest accounts; operating_result when every account is
/// classified and one is revenue.</item>
/// </list>
/// Never logs, never throws on data (ClaudeService fault-isolates it anyway). Anomaly text carries no "%",
/// which AlertRatioReconciliationService would rewrite.
/// </summary>
public static class TrialBalanceNormalizer
{
    /// <summary>The <c>document_kind</c> value of a trial balance (prompt 2.19).</summary>
    public const string DocumentKind = "trial_balance";

    // ── Adjustment codes (persisted in trial_balance.adjustment) ──────────────
    public const string ResultAdded     = "result_added";
    public const string AlreadyIncluded = "already_included";
    public const string NotReconciled   = "not_reconciled";
    public const string NotComputable   = "not_computable";

    // ── Results-account classes (the prompt's enum) ──────────────────────────
    public const string Revenue          = "revenue";
    public const string CostOfSales      = "cost_of_sales";
    public const string OperatingExpense = "operating_expense";
    public const string Interest         = "interest";
    public const string FinancialOther   = "financial_other";
    public const string Recpam           = "recpam";
    public const string Other            = "other";
    public const string IncomeTax        = "income_tax";

    private static readonly IReadOnlySet<string> KnownClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        Revenue, CostOfSales, OperatingExpense, Interest, FinancialOther, Recpam, Other, IncomeTax,
    };

    private static readonly string[] OperatingClasses = [Revenue, CostOfSales, OperatingExpense];

    // ── BalanceJson keys (audit only) ─────────────────────────────────────────
    private const string Liabilities                = "liabilities";
    private const string CurrentLiabilities         = "current_liabilities";
    private const string NonCurrentLiabilities      = "non_current_liabilities";
    private const string CurrentLiabilitiesItems    = "current_liabilities_items";
    private const string NonCurrentLiabilitiesItems = "non_current_liabilities_items";
    private const string NetRevenue                 = "net_revenue";
    private const string Equity                     = "equity";
    private const string NetProfit                  = "net_profit";
    private const string ResultBeforeTax            = "result_before_tax";
    private const string IncomeTaxKey               = "income_tax";
    private const string OperatingResult            = "operating_result";
    private const string FinancialExpenses          = "financial_expenses";

    /// <summary>True when <paramref name="documentKind"/> names a trial balance (case- and blank-insensitive).</summary>
    public static bool IsTrialBalance(string? documentKind)
        => string.Equals(documentKind?.Trim(), DocumentKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalises <paramref name="balance"/> (already in pesos) read from a trial balance.</summary>
    /// <param name="balance">The pass's figures, scaled to pesos.</param>
    /// <param name="block">The pass's <c>trial_balance</c> block, scaled alike; null when the model returned none.</param>
    public static TrialBalanceOutcome Normalize(ExtractedBalance balance, TrialBalanceExtraction? block)
    {
        ArgumentNullException.ThrowIfNull(balance);

        var signs = new List<string>();
        var normalized = NormalizeSigns(balance, signs);
        var current    = normalized.Current;

        if (current.TotalAssets is not { } assets || current.TotalLiabilities is not { } liabilities)
            return Unchanged(normalized, NotComputable, signs);

        var results     = ResultsSum(block?.ResultsAccounts);
        var resolution  = Resolve(assets, liabilities, block?.EquityPrinted, current.Equity, results, block?.CreditBalancesNegative);
        if (resolution is null)
            return Unchanged(normalized, results is null && block?.EquityPrinted is null ? NotComputable : NotReconciled, signs);

        var (adjustment, sign, equityPrinted, equity) = resolution.Value;
        var replaced = new Dictionary<string, double?>(StringComparer.Ordinal);
        var updated  = current;
        double? yearResult = null;
        Dictionary<string, double>? byClass = null;

        if (equity != current.Equity)
        {
            replaced[Equity] = current.Equity;
            updated = updated with { Equity = equity };
        }

        double? resultBeforeTax = normalized.ResultBeforeTax;
        double? incomeTax       = normalized.IncomeTax;

        if (sign is { } c && results is { } r && r != 0d)
        {
            var accounts = block!.ResultsAccounts!;
            yearResult = c * r;
            byClass    = ByClass(accounts, c);

            updated = Set(updated, NetProfit, updated.NetProfit, yearResult, replaced, (a, v) => a with { NetProfit = v });

            if (byClass.TryGetValue(IncomeTax, out var tax))
            {
                Track(replaced, IncomeTaxKey, incomeTax, tax);
                Track(replaced, ResultBeforeTax, resultBeforeTax, yearResult - tax);
                incomeTax       = tax;
                resultBeforeTax = yearResult - tax;
            }

            if (byClass.TryGetValue(Interest, out var interest))
                updated = Set(updated, FinancialExpenses, updated.FinancialExpenses, interest, replaced, (a, v) => a with { FinancialExpenses = v });

            var allClassified = accounts.All(a => a.Class is { } k && KnownClasses.Contains(k));
            if (allClassified && byClass.ContainsKey(Revenue))
            {
                var operating = OperatingClasses.Sum(k => byClass.GetValueOrDefault(k));
                updated = Set(updated, OperatingResult, updated.OperatingResult, operating, replaced, (a, v) => a with { OperatingResult = v });
            }

            if (updated.NetRevenue is null && byClass.TryGetValue(Revenue, out var revenue) && revenue >= 0d)
                updated = Set(updated, NetRevenue, updated.NetRevenue, revenue, replaced, (a, v) => a with { NetRevenue = v });
        }

        var audit = new TrialBalanceAudit
        {
            Adjustment             = adjustment,
            CreditBalancesNegative = sign is { } s ? s < 0 : null,
            EquityPrinted          = equityPrinted,
            YearResult             = yearResult,
            ResultsByClass         = byClass,
            Replaced               = replaced.Count > 0 ? replaced : null,
            SignsNormalized        = signs,
        };

        IReadOnlyList<string> anomalies = adjustment == ResultAdded && yearResult is { } added && equity is { } total
            ? [ResultAddedAnomaly(equityPrinted, added, total, balance.AppliedScale)]
            : [];

        var final = normalized with
        {
            Current         = updated,
            ResultBeforeTax = resultBeforeTax,
            IncomeTax       = incomeTax,
        };

        return new TrialBalanceOutcome(final, audit, anomalies);
    }

    /// <summary>The es-AR anomaly for a pre-closing equity completed with the year's result.</summary>
    public static string ResultAddedAnomaly(double? equityPrinted, double yearResult, double equity, double appliedScale)
    {
        var printed = equityPrinted is { } p ? $" ({BalanceIntegrityService.FormatMoney(p, appliedScale)})" : string.Empty;
        return "El documento es un balance de sumas y saldos previo al cierre: el patrimonio neto impreso" + printed +
               " no incluye el resultado del ejercicio. Se le sumó el resultado del ejercicio calculado a partir de " +
               $"las cuentas de resultado ({BalanceIntegrityService.FormatMoney(yearResult, appliedScale)}), con lo que " +
               $"el patrimonio neto asciende a {BalanceIntegrityService.FormatMoney(equity, appliedScale)}.";
    }

    // ── Private methods ───────────────────────────────────────────────────────────

    private static TrialBalanceOutcome Unchanged(ExtractedBalance balance, string adjustment, List<string> signs)
        => new(balance, new TrialBalanceAudit { Adjustment = adjustment, SignsNormalized = signs }, []);

    private static ExtractedBalance NormalizeSigns(ExtractedBalance balance, List<string> signs)
    {
        var c = balance.Current;

        double? Magnitude(string key, double? value)
        {
            if (value is not { } v || v >= 0d)
                return value;
            signs.Add(key);
            return -v;
        }

        var current = c with
        {
            TotalLiabilities      = Magnitude(Liabilities, c.TotalLiabilities),
            CurrentLiabilities    = Magnitude(CurrentLiabilities, c.CurrentLiabilities),
            NonCurrentLiabilities = Magnitude(NonCurrentLiabilities, c.NonCurrentLiabilities),
            NetRevenue            = Magnitude(NetRevenue, c.NetRevenue),
        };

        return balance with
        {
            Current                    = current,
            CurrentLiabilitiesItems    = NormalizeItems(CurrentLiabilitiesItems, balance.CurrentLiabilitiesItems, current.CurrentLiabilities, signs),
            NonCurrentLiabilitiesItems = NormalizeItems(NonCurrentLiabilitiesItems, balance.NonCurrentLiabilitiesItems, current.NonCurrentLiabilities, signs),
        };
    }

    // A rubro list carrying the ledger's credit sign sums to the NEGATIVE of its positive subtotal: every line
    // is negated, so a contra line (opposite sign) keeps its relation to the others. Anything else is left as
    // read — the items check then judges it.
    private static IReadOnlyList<BalanceLineItem>? NormalizeItems(
        string key, IReadOnlyList<BalanceLineItem>? items, double? subtotal, List<string> signs)
    {
        if (items is not { Count: > 0 } || subtotal is not { } total || total <= 0d || items.Any(i => i.Amount is null))
            return items;

        var sum = items.Sum(i => i.Amount!.Value);
        if (!BalanceIntegrityService.Within(-total, sum, BalanceIntegrityService.Tolerance).Passed)
            return items;

        signs.Add(key);
        return items.Select(i => i with { Amount = -i.Amount }).ToList();
    }

    // Null when no account was listed or any amount is unreadable: a partial sum proves nothing.
    private static double? ResultsSum(IReadOnlyList<TrialBalanceAccount>? accounts)
        => accounts is { Count: > 0 } && accounts.All(a => a.Amount is not null)
            ? accounts.Sum(a => a.Amount!.Value)
            : null;

    // (adjustment, ledger sign, printed equity in schema sign, equity to persist), or null when the equation
    // confirms no reading. The printed equity is preferred; the model's schema equity is the fallback.
    private static (string Adjustment, int? Sign, double? EquityPrinted, double? Equity)? Resolve(
        double assets, double liabilities, double? equityPrinted, double? schemaEquity, double? results, bool? creditNegative)
    {
        int[] order = creditNegative == false ? [1, -1] : [-1, 1];

        if (equityPrinted is { } ep)
        {
            if (results is { } r && r != 0d)
            {
                foreach (var c in order)
                {
                    if (Closes(assets, liabilities + c * (ep + r)))
                        return (ResultAdded, c, c * ep, c * (ep + r));
                }
            }

            foreach (var c in order)
            {
                if (ep != 0d && Closes(assets, liabilities + c * ep))
                    return (AlreadyIncluded, c, c * ep, c * ep);
            }

            return null;
        }

        if (schemaEquity is not { } e)
            return null;

        if (results is { } rr && rr != 0d)
        {
            foreach (var c in order)
            {
                if (Closes(assets, liabilities + e + c * rr))
                    return (ResultAdded, c, e, e + c * rr);
            }
        }

        // The result is already in the model's equity, but nothing here confirms the ledger's sign: only the
        // model's own flag can, and without it no result is computed.
        return Closes(assets, liabilities + e)
            ? (AlreadyIncluded, creditNegative is { } flag ? (flag ? -1 : 1) : null, e, e)
            : null;
    }

    private static bool Closes(double assets, double expected)
        => assets > 0d && BalanceIntegrityService.Within(expected, assets, BalanceIntegrityService.Tolerance).Passed;

    private static Dictionary<string, double> ByClass(IReadOnlyList<TrialBalanceAccount> accounts, int sign)
    {
        var byClass = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            var key = account.Class is { } k && KnownClasses.Contains(k) ? k : "unclassified";
            byClass[key] = byClass.GetValueOrDefault(key) + sign * account.Amount!.Value;
        }

        return byClass;
    }

    private static BalanceAccounts Set(
        BalanceAccounts accounts, string key, double? original, double? value,
        Dictionary<string, double?> replaced, Func<BalanceAccounts, double?, BalanceAccounts> with)
    {
        Track(replaced, key, original, value);
        return with(accounts, value);
    }

    private static void Track(Dictionary<string, double?> replaced, string key, double? original, double? value)
    {
        if (original != value)
            replaced[key] = original;
    }
}
