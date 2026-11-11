// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One cupo-occupying operation of an Active cupo, projected for the cupo alerts so no Notes or audit
/// columns travel. Same idiom as <see cref="DashboardUpcomingOperationRow"/>.
/// </summary>
public sealed record CupoAlertOperationRow
{
    /// <summary>The operation.</summary>
    public Guid OperationId { get; init; }

    /// <summary>Its cupo.</summary>
    public Guid CreditLineId { get; init; }

    /// <summary>The operation's Amount.</summary>
    public decimal Amount { get; init; }

    /// <summary>The operation's currency (always the cupo's).</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Stored status.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>ValidUntil.</summary>
    public DateTime? ValidUntil { get; init; }

    /// <summary>
    /// When the operation was created. With <see cref="VigenteConfirmadaEl"/>, what CUPO_OPERATIONS_EXPIRING
    /// needs to tell whether an operation was already 'Vigente' when a user dismissed the alert.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When it was confirmed 'Vigente'; null while 'Aprobada' and on operations predating script 040.</summary>
    public DateTime? VigenteConfirmadaEl { get; init; }

    /// <summary>The row as the operation shape <c>CreditLineRules.EffectiveStatus</c> reads.</summary>
    public CreditLineOperation ToOperation() => new()
    {
        OperationId         = OperationId,
        CreditLineId        = CreditLineId,
        Amount              = Amount,
        Currency            = Currency,
        Status              = Status,
        ValidUntil          = ValidUntil,
        CreatedAt           = CreatedAt,
        VigenteConfirmadaEl = VigenteConfirmadaEl
    };
}
