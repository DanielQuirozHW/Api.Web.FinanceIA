// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-10-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The identifying fields of an <see cref="Entity"/> that a list row displays, keyed by id: its name, its
/// CUIT, and whether it is a persona humana (the alerts list badges it).
/// A REPOSITORY projection, not a wire contract.
///
/// <para>
/// Exists because the analyses list used to resolve them by calling
/// <c>IEntityRepository.GetAllByTenantAsync</c> — loading EVERY entity of the tenant, all five
/// <c>nvarchar(max)</c> source snapshots included, then filtering in memory down to the ≤pageSize
/// ids it actually needed. That was a second full-tenant blob scan sitting inside an endpoint whose
/// whole purpose was to stop doing full-tenant blob scans, and it would have quietly cancelled out
/// the projection on AnalysisResults.
/// </para>
/// </summary>
public sealed class EntityNameProjection
{
    public Guid EntityId { get; set; }

    public string BusinessName { get; set; } = string.Empty;

    public string CUIT { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="PersonTypes.Juridica"/> or <see cref="PersonTypes.Humana"/> (CK_Entities_PersonType,
    /// DDL 069). A short constrained column, not a snapshot.
    /// </summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;
}
