// Project : Api.Web.FinanceIA
// Created : 2026-11-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The <c>trial_balance</c> block one extraction pass returned (prompt 2.19+) for a document it read as a
/// trial balance ("Balance de Presentación", sumas y saldos): figures EXACTLY AS PRINTED, with the ledger's
/// own sign. Never persisted as such — <see cref="Api.Web.FinanceIA.Servicios.TrialBalanceNormalizer"/>
/// reads it and persists a <see cref="TrialBalanceAudit"/>, which carries no account label.
/// </summary>
public sealed record TrialBalanceExtraction
{
    /// <summary>True when the ledger prints credit balances negative; null when the model did not say.</summary>
    public bool? CreditBalancesNegative { get; init; }

    /// <summary>The printed total of the equity accounts, ledger sign.</summary>
    public double? EquityPrinted { get; init; }

    /// <summary>Every results account once, printed amount and class; null when none was listed.</summary>
    public IReadOnlyList<TrialBalanceAccount>? ResultsAccounts { get; init; }

    /// <summary>Returns a copy with every amount multiplied by <paramref name="factor"/>.</summary>
    public TrialBalanceExtraction Scale(double factor) => factor == 1d ? this : this with
    {
        EquityPrinted   = EquityPrinted * factor,
        ResultsAccounts = ResultsAccounts?.Select(a => a with { Amount = a.Amount * factor }).ToList(),
    };
}

/// <summary>One results account of a trial balance: its printed amount (ledger sign) and its class.</summary>
/// <param name="Amount">The printed amount; null when it could not be read.</param>
/// <param name="Class">One of the <c>TrialBalanceNormalizer</c> class names, lower case; anything else is unclassified.</param>
public sealed record TrialBalanceAccount(double? Amount, string? Class);
