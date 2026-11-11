// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>What a legajo-scoped BCRA/ARCA refresh did. The controller maps each to its status code.</summary>
public enum LegajoSnapshotRefreshStatus
{
    /// <summary>The snapshot was fetched and stored.</summary>
    Refreshed,

    /// <summary>ARCA sent a key status but no fiscal section: only the key status was merged in.</summary>
    KeyStatusOnly,

    /// <summary>
    /// No such legajo in the tenant, or a legajo that is neither an entity's nor an ACTIVE fiador's.
    /// Nothing was called.
    /// </summary>
    LegajoNotFound,

    /// <summary>The tenant has no ARCA CUIT configured (Tenants:{tenantId}:Cuit). Nothing was called.</summary>
    ArcaNotConfigured,

    /// <summary>BCRA answered nothing usable, or its Central de Deudores part failed. Nothing was written.</summary>
    BcraUnavailable,

    /// <summary>ARCA reports no person with the legajo's CUIT. Nothing was written.</summary>
    ArcaPersonNotFound,

    /// <summary>ARCA answered with neither a fiscal section nor a key status. Nothing was written.</summary>
    ArcaNoFiscalData,

    /// <summary>ARCA could not be reached or answered a fault. Nothing was written.</summary>
    ArcaUnavailable,

    /// <summary>The stored BCRA snapshot is usable: nothing was called (RefreshUnusableBcraAsync only).</summary>
    NotNeeded,
}

/// <summary>
/// The outcome of a legajo-scoped refresh, with the legajo as stored when something was written
/// (<see cref="LegajoSnapshotRefreshStatus.Refreshed"/> / <see cref="LegajoSnapshotRefreshStatus.KeyStatusOnly"/>)
/// and the entity it belongs to, when it is an entity's.
/// </summary>
public sealed record LegajoSnapshotRefreshOutcome(
    LegajoSnapshotRefreshStatus Status,
    PersonLegajo? Legajo = null,
    Guid? EntityId = null);
