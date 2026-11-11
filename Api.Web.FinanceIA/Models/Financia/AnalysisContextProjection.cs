// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The four frozen source snapshots of one analysis, and nothing else. A REPOSITORY projection, not
/// a wire contract: it is the target of the <c>.Select(...)</c> behind
/// <c>AnalysisResultRepository.GetContextSnapshotByIdAsync</c>, so that query names exactly five
/// columns — the key and four <c>nvarchar(max)</c> blobs. None of the other ten blobs on the row is
/// read. The response DTO clients see is <see cref="AnalysisContextResponse"/>.
///
/// <para>
/// Four named blobs rather than the source-agnostic shape of
/// <see cref="EntitySnapshotProjection"/>: the entity detail opens one tab at a time, so there it is
/// right that each source has its own single-column read. The analysis "Contexto" section renders
/// all four together, so this is ONE query rather than four round-trips for the same page.
/// </para>
///
/// <para>
/// These are point-in-time copies taken at analysis run time, not the live entity values — the whole
/// reason they were persisted onto the result row. They are READ-ONLY: nothing refreshes them, which
/// is also why, unlike the entity snapshot reads, there are no <c>*DataUpdatedAt</c> timestamps to
/// carry. The capture time is <see cref="AnalysisResult.GeneratedAt"/>, which the detail already
/// ships.
/// </para>
///
/// <para>
/// A null return from the repository means NO SUCH RESULT in this tenant — a 404. A non-null
/// instance whose four blobs are all null means the analysis exists but froze no snapshot — a 200
/// carrying nulls. Projecting into a class rather than a value tuple is what keeps those two cases
/// distinguishable: <c>SingleOrDefaultAsync</c> returns null only when no row matched, and a matched
/// row always materialises an instance.
/// </para>
/// </summary>
public sealed class AnalysisContextProjection
{
    /// <summary>Primary key of the result the snapshots belong to.</summary>
    public Guid ResultId { get; set; }

    /// <summary>Raw stored LUFE JSON, or null when no LUFE snapshot was frozen for this analysis.</summary>
    public string? LufeDataJson { get; set; }

    /// <summary>Raw stored Fiscal/ARCA JSON, or null when none was frozen.</summary>
    public string? FiscalDataJson { get; set; }

    /// <summary>Raw stored CASFOG JSON, or null when none was frozen.</summary>
    public string? CasfogDataJson { get; set; }

    /// <summary>
    /// Raw stored NOSIS JSON. Always null today — no integration writes this column yet — and it is
    /// write-once, never back-filled from the live entity.
    /// </summary>
    public string? NosisDataJson { get; set; }

    /// <summary>
    /// Raw stored BCRA JSON — Central de Deudores, macro, cambiario and rejected cheques as fetched
    /// when the analysis ran. The heaviest blob on the row for an entity with BCRA history: #149
    /// measured 859.295 characters on a 2930-cheque profile, dominated by <c>cheques.detalle</c>.
    ///
    /// <para>
    /// The odd one out in this set. The other four are frozen copies of entity snapshots that the
    /// detail never needs; this one the detail still needs TWO SCALARS from — <c>deuda.estado</c>
    /// and <c>deuda.situacionMaxima</c>, for the Score section. It takes them with <c>JSON_VALUE</c>
    /// rather than by reading the column. See
    /// <see cref="AnalysisResultDetailProjection.BcraDeudaEstadoRaw"/>.
    /// </para>
    ///
    /// <para>
    /// No longer the ONLY read path that ships this column: <see cref="AnalysisBcraProjection"/>
    /// reads it too, for GET /api/analyses/{id}/bcra/cheques. The two are not redundant — this one
    /// reads it alongside the four snapshots because the Contexto section renders them together,
    /// that one reads it alone because the cheque table is opened on its own. The controller drops
    /// <c>cheques.detalle</c> from what it serves off THIS projection; the array is what the other
    /// endpoint exists to return.
    /// </para>
    ///
    /// <para>
    /// Null when BCRA was unavailable at analysis time or the entity had no CUIT. Note the
    /// all-or-nothing write in <c>BcraService.GetBcraDataForAnalysisAsync</c>: when every BCRA call
    /// failed the column is left NULL rather than storing a document with a failed <c>estado</c>.
    /// </para>
    /// </summary>
    public string? BcraDataJson { get; set; }
}
