// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// ONE stored source snapshot and its refresh timestamp. A REPOSITORY projection, not a wire
/// contract: it is the target of the single-column <c>.Select(...)</c> behind
/// <c>EntityRepository.GetBcraSnapshotAsync</c>, <c>GetCasfogSnapshotAsync</c> and
/// <c>GetNosisSnapshotAsync</c>, so each of those queries names exactly three columns — the key,
/// one <c>nvarchar(max)</c> blob, and its <c>datetime2</c>. The blobs of the other four sources
/// are never read.
///
/// <para>
/// Deliberately source-agnostic (<see cref="Json"/>, not <c>BcraDataJson</c>) so one type serves
/// all three reads. The controller knows which source it asked for and deserialises accordingly.
/// </para>
///
/// <para>
/// A null return from the repository means NO SUCH ENTITY in this tenant — a 404. A non-null
/// instance whose <see cref="Json"/> is null means the entity exists but that source has never
/// been fetched — a 200 carrying a null payload. Projecting into a class rather than a value tuple
/// is what keeps those two cases distinguishable: <c>SingleOrDefaultAsync</c> returns null only
/// when no row matched, and a matched row always materialises an instance.
/// </para>
/// </summary>
public sealed class EntitySnapshotProjection
{
    /// <summary>Primary key of the entity the snapshot belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The raw stored JSON, or null when this source has never been fetched.</summary>
    public string? Json { get; set; }

    /// <summary>UTC timestamp of the most recent refresh of this source. Null when never fetched.</summary>
    public DateTime? UpdatedAt { get; set; }
}
