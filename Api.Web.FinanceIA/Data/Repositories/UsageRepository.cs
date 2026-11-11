// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Aggregates AI token usage from ApiCallLogs using in-memory grouping.
/// A single tenant's monthly AI calls are bounded (hundreds to low thousands),
/// so materializing the filtered rows is safe and avoids EF Core GroupBy translation issues.
/// </summary>
public sealed class UsageRepository : IUsageRepository
{
    // The [dbo].[ApiCallLogs].[Service] discriminator the chat endpoint writes. The same literal
    // is written at AnalysisController.cs:2288 (failure path) and :2330 (success path); it is
    // constrained in the database by CK_ApiCallLogs_Service.
    private const string ClaudeChatService = "ClaudeChat";

    private readonly FinanciaDbContext _db;
    private readonly ILogger<UsageRepository> _logger;

    public UsageRepository(FinanciaDbContext db, ILogger<UsageRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<UsageAggregate> GetUsageInPeriodAsync(
        string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
    {
        _logger.LogDebug(
            "Fetching usage for tenant {TenantId}, period [{PeriodStartUtc:O}, {PeriodEndUtc:O})",
            tenantId, periodStartUtc, periodEndUtc);

        var rows = await _db.ApiCallLogs
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                        && r.Operation != null
                        && r.CalledAt >= periodStartUtc
                        && r.CalledAt < periodEndUtc)
            .Select(r => new
            {
                r.Operation,
                r.InputTokens,
                r.OutputTokens,
                r.CalledAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        long totalInput = 0;
        long totalOutput = 0;
        int totalCalls = rows.Count;

        foreach (var r in rows)
        {
            totalInput += (long)(r.InputTokens ?? 0);
            totalOutput += (long)(r.OutputTokens ?? 0);
        }

        var byOperation = rows
            .GroupBy(r => r.Operation!)
            .Select(g => new OperationAggregate
            {
                Operation = g.Key,
                InputTokens = g.Sum(r => (long)(r.InputTokens ?? 0)),
                OutputTokens = g.Sum(r => (long)(r.OutputTokens ?? 0)),
                Calls = g.Count()
            })
            .OrderByDescending(o => o.Calls)
            .ToList();

        // Argentine calendar days, not UTC ones, so the buckets use the same calendar as the
        // window that selected them. Grouping on the raw UTC date would file everything a tenant
        // did between 21:00 and midnight ART under the following day. Done in memory, after
        // materialization, so there is no EF translation of the conversion.
        var byDay = rows
            .GroupBy(r => ArgentineTime.ToLocal(r.CalledAt).Date)
            .Select(g => new DayAggregate
            {
                Day = g.Key,
                InputTokens = g.Sum(r => (long)(r.InputTokens ?? 0)),
                OutputTokens = g.Sum(r => (long)(r.OutputTokens ?? 0)),
                Calls = g.Count()
            })
            .OrderBy(d => d.Day)
            .ToList();

        return new UsageAggregate
        {
            TotalInputTokens = totalInput,
            TotalOutputTokens = totalOutput,
            TotalCalls = totalCalls,
            ByOperation = byOperation,
            ByDay = byDay
        };
    }

    /// <inheritdoc />
    public async Task<ChatTokenUsage> GetChatTokensInPeriodAsync(
        string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
    {
        // Service, not Operation: Operation is nullable free text, while Service is the
        // constrained discriminator, so it is the column that actually isolates chat.
        // The seek rides IX_ApiCallLogs_TenantId_CalledAt and residual-filters Service.
        var rows = await _db.ApiCallLogs
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                        && r.Service == ClaudeChatService
                        && r.CalledAt >= periodStartUtc
                        && r.CalledAt < periodEndUtc)
            .Select(r => new { r.InputTokens, r.OutputTokens })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Materialize then fold, matching GetUsageInPeriodAsync above: a single tenant's monthly
        // chat calls are bounded, and it avoids EF's nullable-Sum translation.
        long totalInput = 0;
        long totalOutput = 0;

        foreach (var r in rows)
        {
            totalInput += r.InputTokens ?? 0;
            totalOutput += r.OutputTokens ?? 0;
        }

        _logger.LogDebug(
            "Chat tokens from ApiCallLogs for tenant {TenantId}, period [{PeriodStartUtc:O}, {PeriodEndUtc:O}): {Calls} calls",
            tenantId, periodStartUtc, periodEndUtc, rows.Count);

        return new ChatTokenUsage(totalInput, totalOutput, rows.Count);
    }

    /// <inheritdoc />
    public async Task<ChatTokenUsage> GetChatTokensFromMessagesInPeriodAsync(
        string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
    {
        // Only turns that actually carry a token count. That is the assistant turns: the user
        // turn is persisted with both token columns null (AnalysisController.cs:2240-2247),
        // so this predicate selects the billable half without depending on the Role string.
        var rows = await _db.ChatMessages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.CreatedAt >= periodStartUtc
                        && m.CreatedAt < periodEndUtc
                        && (m.InputTokens != null || m.OutputTokens != null))
            .Select(m => new { m.InputTokens, m.OutputTokens })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        long totalInput = 0;
        long totalOutput = 0;

        foreach (var m in rows)
        {
            totalInput += m.InputTokens ?? 0;
            totalOutput += m.OutputTokens ?? 0;
        }

        _logger.LogDebug(
            "Chat tokens from ChatMessages for tenant {TenantId}, period [{PeriodStartUtc:O}, {PeriodEndUtc:O}): {Turns} turns",
            tenantId, periodStartUtc, periodEndUtc, rows.Count);

        return new ChatTokenUsage(totalInput, totalOutput, rows.Count);
    }
}
