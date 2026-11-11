// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The figures behind an OPEN event alert, as the reconcile pass that just ran computed them — the same
/// values its Description is written from, so the Portal never has to parse the text. One flat shape for
/// every event type; a field that does not apply to the type is null.
/// </summary>
/// <remarks>
/// Dates are Argentine calendar dates, serialised "yyyy-MM-dd". An operation's ValidUntil is already a
/// calendar date (stored as a UTC midnight) and is emitted as that same date.
/// </remarks>
public sealed record AlertFactsDto
{
    /// <summary>The cupo's currency (CUPO_* types).</summary>
    public string? Currency { get; init; }

    /// <summary>
    /// EXPIRED: occupied by the expired operations. EXPIRING: the sum of the operations in the window.
    /// UTILIZATION_HIGH: Utilizado.
    /// </summary>
    public decimal? Amount { get; init; }

    /// <summary>The cupo's Amount (CUPO_* types). Disponible is CupoAmount − Amount.</summary>
    public decimal? CupoAmount { get; init; }

    /// <summary>How many operations (EXPIRED, EXPIRING).</summary>
    public int? Count { get; init; }

    /// <summary>
    /// EXPIRED: the oldest ValidUntil. EXPIRING: the nearest ValidUntil. REVIEW_DUE: the last approval.
    /// BCRA_WORSENED: when the BCRA was last queried (null if never).
    /// </summary>
    public DateOnly? Date { get; init; }

    /// <summary>REVIEW_DUE: the due date, <see cref="Date"/> + 365 days.</summary>
    public DateOnly? SecondaryDate { get; init; }

    /// <summary>BCRA_WORSENED: the situación at the analysis behind the cupo.</summary>
    public int? SituacionFrom { get; init; }

    /// <summary>BCRA_WORSENED: the situación now.</summary>
    public int? SituacionTo { get; init; }

    /// <summary>REVIEW_DUE: days overdue (positive), or days left (negative); 0 is due today.</summary>
    public int? DaysDelta { get; init; }
}
