// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The columns of <see cref="Entity"/> that GET /api/entities actually consumes — and nothing else.
/// A REPOSITORY projection, not a wire contract: it is the target of the <c>.Select(...)</c> in
/// <c>EntityRepository.BuildListPage</c>, so EF emits a SELECT naming exactly these columns. The
/// response DTO clients see is <see cref="EntityListItemResponse"/>.
///
/// <para>
/// This is the other half of the fix #138 started. #138 stopped the list DESERIALISING the five
/// source snapshots — FiscalDataJson, LufeDataJson, BcraDataJson, CasfogDataJson, NosisDataJson —
/// and shipping them, which took a page from 375 KB to about 2.4 KB. The query still SELECTed them:
/// <c>.Where(tenant).ToListAsync()</c> read all five <c>nvarchar(max)</c> columns off disk for every
/// row in the tenant, and the controller then paged that list in memory. Not parsing a blob you
/// already paid to read saves the CPU and none of the I/O.
/// </para>
///
/// <para>
/// Adding any <c>*DataJson</c> property here puts that column straight back into the SELECT;
/// <c>ListPageQuery_SqlNeverNamesASourceSnapshot</c> and the shape-lock test exist to stop that.
/// The snapshots are still served in full by GET /api/entities/{id}.
/// </para>
/// </summary>
public sealed class EntityListProjection
{
    /// <summary>Primary key. Also the ORDER BY tie-break that makes paging stable.</summary>
    public Guid EntityId { get; set; }

    public string CUIT { get; set; } = string.Empty;

    public string BusinessName { get; set; } = string.Empty;

    public string? TradeName { get; set; }

    public string? Sector { get; set; }

    public string? MipymeCategory { get; set; }

    public bool IsMonitored { get; set; }

    public decimal? RiskScore { get; set; }

    public DateTime? RiskScoreUpdatedAt { get; set; }

    /// <summary>UTC. The sort key: the list is newest-first on this column.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Author. Also the key EnrichProjectionAsync resolves the display name from.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>Juridica or Humana (DDL 069). A short nvarchar, not a snapshot.</summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;

    /// <summary>One of <see cref="FiscalRegimes.Known"/>, or null when not determined (DDL 069).</summary>
    public string? FiscalRegime { get; set; }
}
