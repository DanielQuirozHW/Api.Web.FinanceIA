// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The columns of <see cref="Entity"/> that GET /api/entities/{id} actually renders — and nothing
/// else. A REPOSITORY projection, not a wire contract: it is the target of the <c>.Select(...)</c>
/// in <c>EntityRepository.BuildDetail</c>, so EF emits a SELECT naming exactly these columns. The
/// response DTO clients see is still <see cref="EntityResponse"/>.
///
/// <para>
/// This finishes on the DETAIL path what #138 and #140 did on the LIST path. The Portal's entity
/// detail opens on the "Resumen" tab, which reads FISCAL and LUFE only; BCRA, CASFOG and NOSIS
/// render exclusively when the user opens their respective tabs, and NOSIS and CASFOG are the two
/// heaviest snapshots. Loading all three up front cost the detail request both the disk read and
/// the parse for data most requests never display.
/// </para>
///
/// <para>
/// So <c>BcraDataJson</c>, <c>CasfogDataJson</c> and <c>NosisDataJson</c> are deliberately ABSENT.
/// Their <c>*DataUpdatedAt</c> timestamps are NOT: they are plain <c>datetime2</c> columns and cost
/// nothing to read, so the detail can still render "last refreshed" without fetching a payload. The
/// snapshots themselves are served on demand by GET /api/entities/{id}/bcra, /casfog and /nosis,
/// which read the stored column and never call the external gateway.
/// </para>
///
/// <para>
/// A timestamp is NOT a reliable "this source has data" flag, and must not be used as one. Scripts
/// 025, 033 and 042 added these columns with no backfill by design — "the capture time of existing
/// snapshots is not recoverable, and NULL correctly means 'never refreshed since this column
/// existed'" — so rows predating them, and rows created before the create path started stamping
/// BCRA and CASFOG, carry a populated blob with a NULL timestamp. The authoritative answer to "does
/// this source have data" is the per-source endpoint's response: a null payload means no data.
/// </para>
///
/// <para>
/// Adding any of those three <c>*DataJson</c> properties here puts the column straight back into
/// the SELECT and undoes the change; <c>DetailQuery_SqlNeverNamesTheThreeLazySnapshots</c> and the
/// shape-lock test exist to stop that.
/// </para>
/// </summary>
public sealed class EntityDetailProjection
{
    /// <summary>Primary key. Also the key <c>EnrichProjectionAsync</c> joins the three extras on.</summary>
    public Guid EntityId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string CUIT { get; set; } = string.Empty;

    public string BusinessName { get; set; } = string.Empty;

    public string? TradeName { get; set; }

    public string? Sector { get; set; }

    public string? MipymeCategory { get; set; }

    public string? SgrOneClickEntityId { get; set; }

    public decimal? RiskScore { get; set; }

    public DateTime? RiskScoreUpdatedAt { get; set; }

    public bool IsMonitored { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Author. Also the key EnrichProjectionAsync resolves the display name from.</summary>
    public Guid? CreatedBy { get; set; }

    // ── The two snapshots the Resumen tab renders ─────────────────────────────────
    // Read and deserialised on every detail load, because the default tab needs them.

    public string? FiscalDataJson { get; set; }

    public DateTime? FiscalDataUpdatedAt { get; set; }

    public string? LufeDataJson { get; set; }

    public DateTime? LufeDataUpdatedAt { get; set; }

    // ── The three lazy sources: timestamp only, never the payload ─────────────────
    // The Portal reads these to render "last updated" and to know the tab has something behind it.
    // The payloads come from the per-source endpoints when the tab is actually opened.

    public DateTime? BcraDataUpdatedAt { get; set; }

    public DateTime? CasfogDataUpdatedAt { get; set; }

    public DateTime? NosisDataUpdatedAt { get; set; }

    // ── Persona humana (DDL 069) ──────────────────────────────────────────────────
    // Three scalars off the entity, plus ONE column of the linked legajo, read by a tenant-scoped
    // correlated subquery in BuildDetail. None of the legajo's snapshots (ArcaSnapshotJson,
    // BcraDataJson, NosisDataJson) is ever selected for the detail.

    /// <summary>Juridica or Humana.</summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;

    /// <summary>One of <see cref="FiscalRegimes.Known"/>, or null when not determined.</summary>
    public string? FiscalRegime { get; set; }

    /// <summary>The linked [dbo].[PersonLegajos] row. Null for every Juridica.</summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>The legajo's monotributo category (A–K). Null without a legajo or a category.</summary>
    public string? MonotributoCategory { get; set; }
}
