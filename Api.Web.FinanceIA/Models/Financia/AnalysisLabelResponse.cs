// Project : Api.Web.FinanceIA
// Created : 2026-08-27
// Modified: 2026-08-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The analysed entity's name and id for one analysis, as returned by
/// GET /api/analyses/{id}/label — the breadcrumb label on the analysis detail page. Read-only: five
/// columns across three tables, no blob, no external call, no write.
///
/// <para>
/// Split out of GET /api/analyses/{id} for the same reason /ratios was in #145, but for a heavier
/// caller. The Portal's <c>useBreadcrumbRecord</c> runs in <c>PortalShell</c>, OUTSIDE the page
/// transition, so it is a SECOND full read on every navigation to an analysis detail — and the
/// detail is the heaviest read in the app: even after #143 moved four frozen snapshots to /context
/// and the BCRA split moved a fifth, it still names seven <c>nvarchar(max)</c> columns. All of that
/// came off disk so a breadcrumb could print one string.
/// </para>
///
/// <para>
/// Property names and types match <see cref="AnalysisResultResponse.EntityName"/> and
/// <see cref="AnalysisResultResponse.EntityId"/> EXACTLY, so the JSON keys are <c>entityName</c> and
/// <c>entityId</c> and the Portal swaps the endpoint URL and changes nothing about the field it
/// reads. That is the whole point of this DTO; it is not a place to improve the shape.
/// </para>
///
/// <para>
/// <see cref="EntityName"/> is <c>string?</c> to match the detail response's declaration, not
/// because it can be null here. On the detail it is nullable because the name is looked up
/// separately and that lookup can come back empty; on this endpoint the name arrives through an
/// INNER JOIN over NOT NULL foreign keys onto a NOT NULL column, so a 200 always carries it. There
/// is no "exists but has no name" state: a result that does not resolve in this tenant is a 404.
/// </para>
///
/// <para>
/// <see cref="EntityId"/> is here because it is already the join key — naming it in the SELECT costs
/// nothing and reads no extra page — and it is what lets the breadcrumb render
/// "PyME › {name} › Análisis" with the middle crumb linking to the entity page, without a second
/// request. A client that only needs the label ignores it.
/// </para>
/// </summary>
public sealed class AnalysisLabelResponse
{
    /// <summary>
    /// Legal business name (razón social) of the analysed entity. Declared nullable to match
    /// <see cref="AnalysisResultResponse.EntityName"/>; never null in practice on a 200.
    /// </summary>
    public string? EntityName { get; set; }

    /// <summary>Identifier of the analysed entity, for linking the crumb to the PyME page.</summary>
    public Guid EntityId { get; set; }
}
