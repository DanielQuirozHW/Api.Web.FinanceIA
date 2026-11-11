// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One operation of an Active cupo — stored-'Vigente' for the maturities, any cupo-occupying status for the
/// sgr section — projected with the cupo's entity and currency. Member-initialised so the query can keep
/// composing after it.
/// </summary>
public sealed record DashboardUpcomingOperationRow
{
    /// <summary>The operation.</summary>
    public Guid OperationId { get; init; }

    /// <summary>Its cupo.</summary>
    public Guid CreditLineId { get; init; }

    /// <summary>The cupo's entity.</summary>
    public Guid EntityId { get; init; }

    /// <summary>SGR One Click catalogue code.</summary>
    public int Type { get; init; }

    /// <summary>The cupo's currency.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>The operation's Amount.</summary>
    public decimal Amount { get; init; }

    /// <summary>Stored status.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>ValidUntil.</summary>
    public DateTime? ValidUntil { get; init; }

    /// <summary>The row as the operation shape <c>CreditLineRules.EffectiveStatus</c> reads.</summary>
    public CreditLineOperation ToOperation() => new()
    {
        OperationId  = OperationId,
        CreditLineId = CreditLineId,
        Type         = Type,
        Currency     = Currency,
        Amount       = Amount,
        Status       = Status,
        ValidUntil   = ValidUntil
    };
}
