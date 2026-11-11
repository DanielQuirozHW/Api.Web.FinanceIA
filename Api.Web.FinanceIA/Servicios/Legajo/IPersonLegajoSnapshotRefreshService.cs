// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The legajo-scoped BCRA and ARCA refreshes (POST api/person-legajos/{legajoId}/refresh-bcra|arca):
/// the same vendor services and the same snapshot write (<see cref="PersonSnapshotWriter"/>) as the
/// entity refreshes, keyed by the legajo instead of an entity, so a fiador's legajo — linked to no
/// entity — can be refreshed too.
/// </summary>
/// <remarks>
/// REACH: the legajo must belong to the tenant and be either an entity's legajo or the legajo of an
/// ACTIVE fiador; anything else is <see cref="LegajoSnapshotRefreshStatus.LegajoNotFound"/>. An entity's
/// legajo is refreshed WITH its entity, in one save, exactly as the entity route does, so the two never
/// disagree; a fiador's legajo alone. Nothing here logs a CUIT, a name or a snapshot.
/// </remarks>
public interface IPersonLegajoSnapshotRefreshService
{
    /// <summary>
    /// Re-queries BCRA (deuda, cheques, macro, cambiario) for the legajo's CUIT and stores it. An answer
    /// whose Central de Deudores part failed (or no answer at all) is
    /// <see cref="LegajoSnapshotRefreshStatus.BcraUnavailable"/> and writes nothing; a failed cheques
    /// lookup next to a good deuda keeps the stored cheques block when it holds a real answer
    /// (<see cref="PersonSnapshotWriter.KeepStoredChequesOnFailure"/>), exactly as the entity route does.
    /// </summary>
    Task<LegajoSnapshotRefreshOutcome> RefreshBcraAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The re-query a persona humana evaluation gives a legajo before reading it (M10 Phase 5a-2, the fiador's
    /// counterpart of the solicitante's): only when the stored snapshot is missing or holds a failed query
    /// (<see cref="PersonSnapshotWriter.IsBcraSnapshotUsable"/>; otherwise
    /// <see cref="LegajoSnapshotRefreshStatus.NotNeeded"/>, nothing called), and the answer is kept only when
    /// its Central de Deudores part is real. FAULT-ISOLATED: a BCRA exception is
    /// <see cref="LegajoSnapshotRefreshStatus.BcraUnavailable"/> and leaves the snapshot as it was; only
    /// cancellation propagates. The save is not isolated — it shares the request's DbContext.
    /// </summary>
    Task<LegajoSnapshotRefreshOutcome> RefreshUnusableBcraAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Re-queries ARCA's constancia de inscripción for the legajo's CUIT, on behalf of
    /// <paramref name="cuitRepresentada"/> (the tenant's configured CUIT, null when it has none), and
    /// stores the snapshot, regime, monotributo category and activity start.
    /// </summary>
    Task<LegajoSnapshotRefreshOutcome> RefreshArcaAsync(
        Guid personLegajoId, string tenantId, string? cuitRepresentada, CancellationToken ct);
}
