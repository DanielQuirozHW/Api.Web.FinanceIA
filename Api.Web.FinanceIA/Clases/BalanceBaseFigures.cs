// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Whether a stored balance read NONE of the seven base figures the scoring is built on — total assets,
/// current assets, current liabilities, total liabilities, equity, net revenue and net profit. Pure.
///
/// <para>
/// The same rule the extraction applies before it creates an analysis
/// (<c>ClaudeService.AnyCoreTotal</c>, the <c>claude_no_financial_statements</c> gate): a zero counts, as a
/// printed zero is a figure that was read. Analyses persisted before that gate (#335) can hold a balance with
/// none of them; <see cref="ReanalysisPolicy"/> lets such a document be analysed again without rejecting
/// the empty analysis first.
/// </para>
/// </summary>
public static class BalanceBaseFigures
{
    /// <summary>
    /// True only when <paramref name="figures"/> describes a stored balance (<c>HasBalance</c>) in which all
    /// seven base figures are absent or null. No row, or no stored balance at all, is false: nothing proves
    /// that analysis read nothing, so the document keeps today's rule.
    /// </summary>
    public static bool ReadNone(BalanceBaseFiguresProjection? figures)
        => figures is { HasBalance: true }
           && figures.Assets             is null
           && figures.CurrentAssets      is null
           && figures.CurrentLiabilities is null
           && figures.Liabilities        is null
           && figures.Equity             is null
           && figures.NetRevenue         is null
           && figures.NetProfit          is null;
}
