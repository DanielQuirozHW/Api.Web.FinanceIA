// Project : Api.Web.FinanceIA
// Created : 2026-08-27
// Modified: 2026-08-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The two identifying strings of one entity, as returned by GET /api/entities/{id}/label — the
/// breadcrumb label on the PyME (entity) detail page. Read-only: three narrow columns, no blob, no
/// external call, no write.
///
/// <para>
/// Split out of GET /api/entities/{id} for the same reason /ratios was split out of the analysis
/// detail in #145. The Portal's breadcrumb hook runs in <c>PortalShell</c>, OUTSIDE the page
/// transition, so it is a SECOND read on every navigation to an entity detail — and it was fetching
/// the whole detail to display <c>businessName</c>. That detail still names FiscalDataJson and
/// LufeDataJson, and LUFE alone is ~26KB on a representative PyME, so a breadcrumb was paying for
/// two <c>nvarchar(max)</c> columns and eleven scalars it never renders.
/// </para>
///
/// <para>
/// Property names and types match <see cref="EntityResponse.BusinessName"/> and
/// <see cref="EntityResponse.CUIT"/> EXACTLY, so the JSON keys are <c>businessName</c> and
/// <c>cuit</c> and the Portal swaps the endpoint URL while the field it reads stays
/// <c>businessName</c>. That is the whole point of this DTO; it is not a place to improve the shape.
/// <c>CUIT</c> keeps its all-caps spelling for the same reason — the camelCase policy lowercases the
/// leading run, so the wire key is <c>cuit</c> here exactly as it is on the detail and the list.
/// </para>
///
/// <para>
/// Both fields are NOT NULL in the database, so neither is ever null on a 200. An entity that does
/// not exist in this tenant is a 404, not a 200 carrying nulls — there is no "exists but has no
/// name" state to represent, unlike the snapshot and ratios responses.
/// </para>
///
/// <para>
/// The CUIT is included because it costs nothing — <c>nvarchar(13)</c>, on the same row, already in
/// the shared <c>EntityNameProjection</c> the query fills — and it is what lets a caller render the
/// crumb with a disambiguating subtitle without a second request. A client that only needs the name
/// ignores it.
/// </para>
/// </summary>
public sealed class EntityLabelResponse
{
    /// <summary>Legal business name (razón social). Never null — NOT NULL in the database.</summary>
    public string BusinessName { get; set; } = string.Empty;

    /// <summary>CUIT of the entity. Never null — NOT NULL and uniquely indexed in the database.</summary>
    public string CUIT { get; set; } = string.Empty;
}
