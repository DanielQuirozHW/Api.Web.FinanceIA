// Project : Api.Web.FinanceIA
// Created : 2026-09-27

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>Which per-tenant CUIT uniqueness rule a write lost a race against.</summary>
public enum DuplicateCuitScope
{
    /// <summary>
    /// [dbo].[Entities]: UQ_Entities_TenantId_CUIT (the CUIT already has an entity in this tenant)
    /// or UX_Entities_PersonLegajoId (the legajo is already linked to an entity — which, since a
    /// legajo and its entity share the CUIT, is the same conflict seen from the other index).
    /// </summary>
    Entity,

    /// <summary>
    /// [dbo].[PersonLegajos]: UX_PersonLegajos_TenantId_CUIT (another request created the person's
    /// legajo first). Recoverable — re-read the legajo and link to it.
    /// </summary>
    PersonLegajo
}
