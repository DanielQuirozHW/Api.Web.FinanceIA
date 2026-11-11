// Project : Api.Web.FinanceIA
// Created : 2026-06-07

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Internal aggregate returned by the usage repository.
/// </summary>
public sealed class UsageAggregate
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public int TotalCalls { get; set; }
    public IReadOnlyList<OperationAggregate> ByOperation { get; set; } = [];
    public IReadOnlyList<DayAggregate> ByDay { get; set; } = [];
}

public sealed class OperationAggregate
{
    public string Operation { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }
}

public sealed class DayAggregate
{
    public DateTime Day { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }
}

// ── Chat-token metering (billing v1) ─────────────────────────────────────────

/// <summary>
/// Where a chat-token figure was measured. Surfaced to the caller because a zero read means
/// two very different things depending on the source, and the Portal must be able to tell them
/// apart before it shows an analyst a quota number.
/// </summary>
public enum ChatTokenSource
{
    /// <summary>Measured from [dbo].[ApiCallLogs] rows with Service = 'ClaudeChat'.</summary>
    ApiCallLogs,

    /// <summary>
    /// Measured from assistant turns in [dbo].[ChatMessages], because ApiCallLogs held no rows
    /// for the period. See <c>UsageRepository</c> for why that happens.
    /// </summary>
    ChatMessages
}

/// <summary>
/// Chat token consumption for one tenant over one calendar month.
/// <paramref name="Calls"/> is the row count behind the figure, not a token number: it is what
/// distinguishes "this tenant chatted nothing this month" from "nothing was recorded at all".
/// </summary>
public sealed record ChatTokenUsage(long InputTokens, long OutputTokens, int Calls)
{
    /// <summary>Input plus output — the figure billing meters against the plan limit.</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>An empty period: no rows at all behind this figure.</summary>
    public static readonly ChatTokenUsage Empty = new(0L, 0L, 0);
}

// ── API response DTOs ────────────────────────────────────────────────────────

public sealed class UsageSummaryResponse
{
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public int TotalCalls { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public IReadOnlyList<UsageByOperation> ByOperation { get; set; } = [];
    public IReadOnlyList<UsageByDay> ByDay { get; set; } = [];
}

public sealed class UsageByOperation
{
    public string Operation { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }
}

public sealed class UsageByDay
{
    public string Date { get; set; } = string.Empty;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }
}
