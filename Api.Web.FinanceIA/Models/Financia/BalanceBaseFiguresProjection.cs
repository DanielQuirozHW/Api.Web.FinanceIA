// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The seven base figures of one analysis result's stored balance, as read by
/// <c>IAnalysisRepository.GetBalanceBaseFiguresAsync</c> through <c>JSON_VALUE</c> — each the figure's
/// TEXT, or null when the key is absent or JSON null — so the <c>BalanceJson</c> blob never leaves the
/// database. A REPOSITORY projection, not a wire contract; <see cref="Api.Web.FinanceIA.Clases.BalanceBaseFigures"/>
/// judges it.
/// </summary>
public sealed record BalanceBaseFiguresProjection
{
    /// <summary>True when the result has a stored balance at all (<c>BalanceJson</c> is not null).</summary>
    public bool HasBalance { get; init; }

    /// <summary><c>$.assets</c> (total assets).</summary>
    public string? Assets { get; init; }

    /// <summary><c>$.current_assets</c>.</summary>
    public string? CurrentAssets { get; init; }

    /// <summary><c>$.current_liabilities</c>.</summary>
    public string? CurrentLiabilities { get; init; }

    /// <summary><c>$.liabilities</c> (total liabilities).</summary>
    public string? Liabilities { get; init; }

    /// <summary><c>$.equity</c>.</summary>
    public string? Equity { get; init; }

    /// <summary><c>$.net_revenue</c>.</summary>
    public string? NetRevenue { get; init; }

    /// <summary><c>$.net_profit</c>.</summary>
    public string? NetProfit { get; init; }
}
