// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Read-only repository for aggregating AI token usage from ApiCallLogs.
/// </summary>
/// <remarks>
/// Every method takes the reporting window as a precomputed half-open UTC pair
/// <c>[periodStartUtc, periodEndUtc)</c> rather than a year and a month. A billing period is the
/// ARGENTINE calendar month while <c>CalledAt</c> and <c>CreatedAt</c> are stored in UTC, so the
/// boundary is a timezone conversion — one that <c>ArgentineTime</c> owns, and that every caller
/// must share so the analysis count and the token counts cannot drift onto different periods.
/// This also matches <see cref="IAlertRepository.ExistsByTypeInPeriodAsync"/>, which already takes
/// its window this way.
/// </remarks>
public interface IUsageRepository
{
    /// <summary>
    /// Returns aggregated token usage for a tenant inside the given window.
    /// </summary>
    /// <param name="tenantId">Firebase tenant slug to scope the aggregate to.</param>
    /// <param name="periodStartUtc">Start of the window, UTC, INCLUSIVE.</param>
    /// <param name="periodEndUtc">End of the window, UTC, EXCLUSIVE.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<UsageAggregate> GetUsageInPeriodAsync(
        string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct);

    /// <summary>
    /// Returns chat-only token usage for a tenant inside the given window, measured
    /// from [dbo].[ApiCallLogs] rows carrying Service = 'ClaudeChat'.
    ///
    /// <para>
    /// Deliberately NOT <see cref="GetUsageInPeriodAsync"/>: that method filters on
    /// <c>Operation != null</c> and sums every service together, so its totals mix document
    /// extraction and the scoring narrative into the chat figure. A chat quota must count chat.
    /// </para>
    /// </summary>
    Task<ChatTokenUsage> GetChatTokensInPeriodAsync(
        string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct);

    /// <summary>
    /// The same figure measured from assistant turns in [dbo].[ChatMessages] instead — the
    /// fallback used when <see cref="GetChatTokensInPeriodAsync"/> finds no rows at all.
    ///
    /// <para>
    /// It exists because the two writes have very different reliability. The ApiCallLogs write is
    /// fire-and-forget and <c>ApiCallLogService</c> swallows its own failures, so that table can be
    /// empty without anything surfacing; the ChatMessages write is awaited inside the request that
    /// produced the tokens. Counts successful turns only — a failed Claude call persists no
    /// assistant turn, and records no tokens in ApiCallLogs either.
    /// </para>
    /// </summary>
    Task<ChatTokenUsage> GetChatTokensFromMessagesInPeriodAsync(
        string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct);
}
