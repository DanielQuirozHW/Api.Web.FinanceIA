// Project : Api.Web.FinanceIA
// Created : 2026-08-27
// Modified: 2026-08-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The stored BCRA blob of one analysis, and nothing else. A REPOSITORY projection, not a wire
/// contract: it is the target of the <c>.Select(...)</c> behind
/// <c>AnalysisResultRepository.GetBcraSnapshotByIdAsync</c>, so that query names exactly two
/// columns — the key and one <c>nvarchar(max)</c> blob. None of the other thirteen blobs on the row
/// is read. The response DTO clients see is <see cref="AnalysisBcraChequesResponse"/>.
///
/// <para>
/// This is the one-blob-per-read shape the ENTITY snapshots use
/// (<see cref="EntitySnapshotProjection"/>), and the reason
/// <see cref="AnalysisContextProjection"/> gives for NOT using it does not apply here. That one
/// reads five blobs at once because the Contexto section renders them together; this one exists for
/// GET /api/analyses/{id}/bcra/cheques, which serves a single table the analyst opens on its own.
/// Reading it through <c>GetContextSnapshotByIdAsync</c> would pull LufeDataJson —26KB on a
/// representative row— plus the Fiscal, CASFOG and NOSIS snapshots for a request that renders none
/// of them.
/// </para>
///
/// <para>
/// There is no <c>*DataUpdatedAt</c> counterpart, unlike <see cref="EntitySnapshotProjection"/>:
/// this is a point-in-time copy taken when the analysis ran and never refreshed, so its capture
/// time is the result's own <c>GeneratedAt</c>.
/// </para>
///
/// <para>
/// A null return from the repository means NO SUCH RESULT in this tenant — a 404. A non-null
/// instance whose <see cref="BcraDataJson"/> is null means the analysis exists but froze no BCRA
/// snapshot — a 200 carrying an empty list. Projecting into a class rather than a bare
/// <c>string?</c> is what keeps those two cases distinguishable: <c>SingleOrDefaultAsync</c>
/// returns null only when no row matched, and a matched row always materialises an instance.
/// </para>
/// </summary>
public sealed class AnalysisBcraProjection
{
    /// <summary>Primary key of the result the snapshot belongs to.</summary>
    public Guid ResultId { get; set; }

    /// <summary>
    /// Raw stored BCRA JSON — Central de Deudores, macro, cambiario and rejected cheques as fetched
    /// when the analysis ran. The heaviest blob on the row for an entity with BCRA history: #149
    /// measured 859.295 characters on a 2930-cheque profile, of which <c>cheques.detalle</c> is
    /// very nearly all.
    ///
    /// <para>
    /// Null when BCRA was unavailable at analysis time or the entity had no CUIT. Note the
    /// all-or-nothing write in <c>BcraService.GetBcraDataForAnalysisAsync</c>: when every BCRA call
    /// failed the column is left NULL rather than storing a document with a failed <c>estado</c>.
    /// </para>
    /// </summary>
    public string? BcraDataJson { get; set; }
}
