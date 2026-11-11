// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One Active cupo with its utilizado and the two BCRA scalars of its entity's CURRENT snapshot — read with
/// JSON_VALUE, so the blob is never selected.
/// </summary>
/// <param name="EntityId">The cupo's entity.</param>
/// <param name="CreditLineId">The cupo.</param>
/// <param name="Currency">The cupo's currency.</param>
/// <param name="Amount">The cupo's Amount.</param>
/// <param name="Utilizado">Its operations that occupy cupo — every status except Cancelada.</param>
/// <param name="NowEstadoRaw">JSON_VALUE(Entities.BcraDataJson, '$.deuda.estado'); null when the entity does not resolve.</param>
/// <param name="NowSituacionMaximaRaw">JSON_VALUE(Entities.BcraDataJson, '$.deuda.situacionMaxima').</param>
/// <param name="BcraDataUpdatedAt">Entities.BcraDataUpdatedAt (UTC); null when never queried.</param>
public sealed record DashboardBcraCurrentRow(
    Guid EntityId, Guid CreditLineId, string Currency, decimal Amount, decimal Utilizado,
    string? NowEstadoRaw, string? NowSituacionMaximaRaw, DateTime? BcraDataUpdatedAt);
