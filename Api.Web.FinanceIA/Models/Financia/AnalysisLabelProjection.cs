// Project : Api.Web.FinanceIA
// Created : 2026-08-27
// Modified: 2026-08-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The analysed entity's name and id for one result, and nothing else. A REPOSITORY projection, not
/// a wire contract: it is the target of the <c>.Select(...)</c> behind
/// <c>AnalysisResultRepository.GetLabelByIdAsync</c>, so that query names exactly five columns —
/// three of them keys — across three tables and not one <c>nvarchar(max)</c> on any of them. The
/// response DTO clients see is <see cref="AnalysisLabelResponse"/>.
///
/// <para>
/// The name is not on the result row. It is reached ResultId → AnalysisId → EntityId →
/// <c>Entities.BusinessName</c>, and this is the only projection in the repository that leaves
/// AnalysisResults at all. It does so in ONE statement rather than the three round-trips the detail
/// path runs, two of which would name a blob: <c>IAnalysisRepository.GetByIdAsync</c> loads the
/// Analyses row unprojected — <c>ReviewNotes</c> is <c>nvarchar(max)</c> there — and
/// <c>IEntityRepository.GetByIdAsync</c> loads the Entities row with all five source snapshots on
/// it.
/// </para>
///
/// <para>
/// This applies to the breadcrumb what #145 applied to the ratios panel. The Portal's
/// <c>useBreadcrumbRecord</c> runs in <c>PortalShell</c>, OUTSIDE the page transition, so it is a
/// SECOND read on every navigation to an analysis detail — and it was calling GET
/// /api/analyses/{id}, the heaviest read in the app even after the BCRA split, to take
/// <c>entityName</c> off it.
/// </para>
///
/// <para>
/// <see cref="EntityName"/> is non-nullable because it cannot be null on a materialised instance:
/// <c>Entities.BusinessName</c> is NOT NULL, and both joins are INNER over NOT NULL foreign keys
/// (<c>FK_AnalysisResults_AnalysisId</c>, <c>FK_Analyses_EntityId</c>), so they cannot drop the row
/// either. A null return from the repository therefore means NO SUCH RESULT in this tenant — a 404 —
/// and never "the analysis exists but has no name". Projecting into a class rather than a value
/// tuple is what keeps that distinction available to the caller.
/// </para>
/// </summary>
public sealed class AnalysisLabelProjection
{
    /// <summary>Primary key of the result the label belongs to.</summary>
    public Guid ResultId { get; set; }

    /// <summary>
    /// Primary key of the analysed entity, taken off the Analyses row. It is the join key, so naming
    /// it in the SELECT costs nothing, and it is what lets the breadcrumb link its crumb to the PyME
    /// page without a second request.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// <c>Entities.BusinessName</c> — the legal business name the breadcrumb displays. Never null on
    /// a materialised instance; see the type summary.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;
}
