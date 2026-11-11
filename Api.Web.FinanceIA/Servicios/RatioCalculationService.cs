// Project : Api.Web.FinanceIA
// Created : 2026-06-10

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless, deterministic implementation of <see cref="IRatioCalculationService"/>.
/// No DI dependencies — can be instantiated directly in tests.
/// </summary>
public sealed class RatioCalculationService : IRatioCalculationService
{
    // ── IRatioCalculationService ──────────────────────────────────────────────

    /// <inheritdoc />
    public RatiosData Calculate(BalanceAccounts a)
    {
        // ── Liquidity ─────────────────────────────────────────────────────────

        var currentRatio = R(a.CurrentAssets, a.CurrentLiabilities);

        // acid_test: computable whenever inventory is known (including explicit zero).
        // Zero inventory means acid_test equals current_ratio — valid for service companies.
        // Null means the inventory line is absent from the document (data not available).
        // Negative inventory is invalid data → treat as absent.
        // PrepaidExpenses null → treat as 0.
        double? acidTest = (a.Inventory is null || a.Inventory < 0)
            ? null
            : R(a.CurrentAssets - a.Inventory - (a.PrepaidExpenses ?? 0), a.CurrentLiabilities);

        // ── Leverage ──────────────────────────────────────────────────────────

        var debtRatio = R(a.TotalLiabilities, a.TotalAssets);

        // Non-positive equity makes leverage meaningless.
        double? leverage = (a.Equity is null || a.Equity <= 0)
            ? null
            : R(a.TotalLiabilities, a.Equity);

        // ── Coverage ──────────────────────────────────────────────────────────

        // FinancialExpenses may be stored negative (expense sign convention).
        // Take Abs() so the sign convention doesn't affect the ratio.
        double? interestCoverage = (a.FinancialExpenses is null || a.FinancialExpenses == 0)
            ? null
            : R(a.OperatingResult, Math.Abs(a.FinancialExpenses.Value));

        // ── Profitability ─────────────────────────────────────────────────────

        // Non-positive equity makes ROE meaningless.
        double? roe = (a.Equity is null || a.Equity <= 0)
            ? null
            : R(a.NetProfit * 100, a.Equity);

        var roa       = R(a.NetProfit * 100, a.TotalAssets);
        var netMargin = R(a.NetProfit * 100, a.NetRevenue);

        return new RatiosData
        {
            CurrentRatio     = currentRatio,
            AcidTest         = acidTest,
            DebtRatio        = debtRatio,
            Leverage         = leverage,
            InterestCoverage = interestCoverage,
            Roe              = roe,
            Roa              = roa,
            NetMargin        = netMargin,
            // The two income-statement operands the profitability floor reads (scoring algorithm 10).
            // Carried, never serialised: RatiosJson keeps its bytes.
            OperatingResult   = a.OperatingResult,
            FinancialExpenses = a.FinancialExpenses,
            ExclusionReasons = ExclusionReasonsFor(a, currentRatio, acidTest, debtRatio, leverage,
                                                   interestCoverage, roe, roa, netMargin),
        };
    }

    // ── Exclusion reasons (reporting only) ────────────────────────────────────

    /// <summary>
    /// Why each null ratio is null, keyed by its scoring key. "unread" whenever an operand is null —
    /// it wins over every other reason; "not_meaningful" when every operand was read but the formula
    /// has no meaning: a zero denominator, non-positive equity (leverage, ROE), negative inventory
    /// (acid test: read, but not a valid stock figure). Mirrors the formulas above exactly; a ratio
    /// that was computed gets no entry.
    /// <para>
    /// One exception to "not_meaningful" (scoring algorithm 10): a current liabilities figure READ as
    /// zero against a POSITIVE numerator — current assets for liquidez corriente, current assets minus
    /// inventory and prepaid expenses for prueba ácida — is <c>no_current_liabilities</c>. The ratio is
    /// still null (no infinite number is ever produced), but the company owes nothing short-term and
    /// holds something to pay it with, which is the best liquidity there is; the engine scores it at
    /// the best band. A null current liabilities figure is still "unread", and a numerator at or
    /// below zero is still "not_meaningful".
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ExclusionReasonsFor(
        BalanceAccounts a,
        double? currentRatio, double? acidTest, double? debtRatio, double? leverage,
        double? interestCoverage, double? roe, double? roa, double? netMargin)
    {
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string key, double? value, params double?[] operands)
        {
            if (value is not null)
                return;

            reasons[key] = operands.Any(o => o is null)
                ? RatioExclusionReasons.Unread
                : RatioExclusionReasons.NotMeaningful;
        }

        Add("liquidez_corriente",  currentRatio,     a.CurrentAssets, a.CurrentLiabilities);
        Add("prueba_acida",        acidTest,         a.CurrentAssets, a.CurrentLiabilities, a.Inventory);

        // Zero current liabilities against a positive numerator: a marker, not an exclusion (scoring
        // algorithm 10). Only overrides a "not_meaningful" the zero denominator produced — "unread"
        // (an operand missing) is never touched, and negative inventory keeps acid test meaningless.
        if (a.CurrentLiabilities == 0 && a.CurrentAssets is { } currentAssets)
        {
            if (currentRatio is null && currentAssets > 0)
                reasons["liquidez_corriente"] = RatioExclusionReasons.NoCurrentLiabilities;

            if (acidTest is null && a.Inventory is { } inventory && inventory >= 0
                && currentAssets - inventory - (a.PrepaidExpenses ?? 0) > 0)
            {
                reasons["prueba_acida"] = RatioExclusionReasons.NoCurrentLiabilities;
            }
        }
        Add("endeudamiento",       debtRatio,        a.TotalLiabilities, a.TotalAssets);
        Add("leverage",            leverage,         a.TotalLiabilities, a.Equity);
        Add("cobertura_intereses", interestCoverage, a.OperatingResult, a.FinancialExpenses);
        Add("roe",                 roe,              a.NetProfit, a.Equity);
        Add("roa",                 roa,              a.NetProfit, a.TotalAssets);
        Add("margen_neto",         netMargin,        a.NetProfit, a.NetRevenue);

        return reasons;
    }

    // ── Private helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Safe division rounded to 2 decimal places (AwayFromZero).
    /// Returns <c>null</c> when either operand is null or the divisor is zero.
    /// </summary>
    private static double? R(double? numerator, double? denominator)
    {
        if (numerator is null || denominator is null || denominator == 0)
            return null;

        return Math.Round(numerator.Value / denominator.Value, 2, MidpointRounding.AwayFromZero);
    }
}
