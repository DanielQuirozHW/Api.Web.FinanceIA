// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One of the solicitante's headline indicators before and after a re-evaluation: status, value and band.
/// A side the stored evaluation does not carry reads null throughout.
/// </summary>
public sealed record PersonRescoreIndicatorChange(
    string   Id,
    string?  PreviousStatus,
    decimal? PreviousValue,
    string?  PreviousBand,
    string?  NewStatus,
    decimal? NewValue,
    string?  NewBand);
