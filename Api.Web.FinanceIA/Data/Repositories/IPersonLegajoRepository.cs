// Project : Api.Web.FinanceIA
// Created : 2026-09-27

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Data access for [dbo].[PersonLegajos] (DDL 069) — the legajo of a persona humana, one per
/// (TenantId, CUIT). Every read is scoped to the tenant: there is no EF global query filter.
/// </summary>
/// <remarks>
/// The legajo is part of the entity, not an enrichment: whenever an entity and its legajo change
/// together they are written by <see cref="SaveEntityWithLegajoAsync"/>, in ONE SaveChanges, which
/// SQL Server runs as one transaction. The CUIT argument is the hyphenated form stored in
/// [dbo].[Entities].CUIT, and it comes first, in the same order as
/// <see cref="IEntityRepository.GetByCuitAsync"/>.
/// </remarks>
public interface IPersonLegajoRepository
{
    /// <summary>The tenant's legajo for <paramref name="cuit"/>, or null. Read-only (AsNoTracking).</summary>
    Task<PersonLegajo?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct);

    /// <summary>The tenant's legajo with this id, or null — also null for another tenant's legajo.</summary>
    Task<PersonLegajo?> GetByIdAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Only the legajo's monotributo category — a one-column read for response paths that hold an
    /// <see cref="Entity"/> but not its legajo. Never reads the legajo's JSON snapshots. Null when
    /// the legajo has no category, does not exist, or belongs to another tenant.
    /// </summary>
    Task<string?> GetMonotributoCategoryAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="entity"/> and <paramref name="legajo"/> in one SaveChanges: each is
    /// inserted when its <c>…IsNew</c> flag is set and updated otherwise. The entity must already
    /// point at the legajo and both must belong to the same tenant.
    /// </summary>
    /// <exception cref="DuplicateCuitException">
    /// The write lost a race against a per-tenant CUIT index. Both entries are detached first, so the
    /// caller can re-read and retry on the same request.
    /// </exception>
    Task SaveEntityWithLegajoAsync(
        Entity entity,
        bool entityIsNew,
        PersonLegajo legajo,
        bool legajoIsNew,
        CancellationToken ct);

    /// <summary>
    /// Links <paramref name="fiador"/> to <paramref name="legajo"/> in one SaveChanges: the legajo is
    /// inserted when <paramref name="legajoIsNew"/> is set (and left untouched otherwise), and the
    /// fiador row gets ONLY its <see cref="Fiador.PersonLegajoId"/> written — never its name, cargo,
    /// status or the <see cref="Fiador.UpdatedAt"/> stamp of an analyst's edit. The fiador must already
    /// point at the legajo and both must belong to the same tenant. FK_Fiadores_PersonLegajos, modelled
    /// for this, orders the legajo INSERT before the fiador UPDATE.
    /// </summary>
    /// <exception cref="DuplicateCuitException">
    /// The legajo insert lost a race against UX_PersonLegajos_TenantId_CUIT. Both entries are detached
    /// first, so the caller can re-read the winner and link it on the same request.
    /// </exception>
    Task SaveFiadorLegajoLinkAsync(
        Fiador fiador,
        PersonLegajo legajo,
        bool legajoIsNew,
        CancellationToken ct);

    /// <summary>
    /// Writes a legajo-only BCRA/ARCA refresh — a legajo linked to no entity, i.e. a fiador's: the two
    /// snapshots and their stamps, the fiscal regime, the monotributo category, the activity start and
    /// UpdatedAt, and nothing else, so a consent recorded meanwhile is never overwritten. A legajo
    /// linked to an entity is saved with it through <see cref="SaveEntityWithLegajoAsync"/> instead.
    /// </summary>
    Task SaveLegajoSnapshotsAsync(PersonLegajo legajo, CancellationToken ct);
}
