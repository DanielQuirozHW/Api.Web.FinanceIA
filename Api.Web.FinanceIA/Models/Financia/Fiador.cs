// Project : Api.Web.FinanceIA
// Created : 2026-08-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Fiadores].
/// A guarantor (fiador/garante) of a PyME: a natural person — or entity — identified by
/// their OWN CUIT, selected by the analyst from the PyME's LUFE socios/autoridades list
/// (<c>LufeData.Autoridades[]</c>). The relationship hangs off [dbo].[Entities], not off an
/// analysis, so it persists across analyses; a PyME may have N fiadores. Exactly one row is
/// Active per (TenantId, EntityId, CUIT). Removal is soft (Status flips Active -&gt; Removed),
/// never a delete, so the analyst's selection history survives.
///
/// PII: this entity is identifying data by construction. <see cref="CUIT"/> and
/// <see cref="Denominacion"/> name a natural person, and once <see cref="NosisDataJson"/> is
/// populated it carries the NOSIS green set — historia laboral, the CUIT and razón social of the
/// person's EMPLOYER (a third party), and the UIF sujeto-obligado and PEP compliance flags — on
/// top of the identity fields already inside a <c>NosisData</c> blob.
/// The rules, shared with <see cref="PersonLegajo"/> (M10 Phase 2a):
/// <list type="bullet">
/// <item>A person's documents reach the model for classification or extraction ONLY through a
/// legajo whose consent is recorded (<see cref="PersonLegajo.ConsentRecordedAt"/>). Nothing sends a
/// fiador's documents or data to the model today.</item>
/// <item>OUR CODE never places the person's identity — <see cref="Denominacion"/>,
/// <see cref="CUIT"/> — into any prompt: classification, narrative, chat or scoring.</item>
/// <item>Nothing serializes this type wholesale — into a prompt, a response, an audit payload or a
/// log.</item>
/// <item>No CUIT and no name in logs: log the row's id (the #237 convention).</item>
/// </list>
/// With a natural person's documento and compliance flags in the blob, a breach of any of these is
/// a reportable data incident, not a style slip.
///
/// Database First — schema changes via DDL scripts only (see docs/database/046_fiadores_table.sql).
/// </summary>
public sealed class Fiador
{
    /// <summary>Primary key (database-assigned via NEWSEQUENTIALID()).</summary>
    public Guid FiadorId { get; set; }

    /// <summary>Tenant that owns this fiador record.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Foreign key to [dbo].[Entities].EntityId — the PyME this fiador guarantees.</summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// The fiador's OWN Argentine tax identification number, not the PyME's. Stored as a string
    /// to match [dbo].[Entities].CUIT; the LUFE source field arrives as a JSON number, and
    /// converting it is the ingesting code's job.
    /// </summary>
    public string CUIT { get; set; } = string.Empty;

    /// <summary>
    /// Name as it came from <c>Autoridades[].Denominacion</c>. Null when LUFE did not inform it.
    /// </summary>
    public string? Denominacion { get; set; }

    /// <summary>
    /// Role within the PyME, from <c>Autoridades[].Cargo</c> (socio, presidente, etc.).
    /// Null when LUFE did not inform it.
    /// </summary>
    public string? Cargo { get; set; }

    /// <summary>
    /// Shareholding percentage, from <c>Autoridades[].PorcAccionista</c>. Null when LUFE did not
    /// inform it; 0 is a real value (an authority who holds no shares).
    /// </summary>
    public decimal? PorcAccionista { get; set; }

    /// <summary>
    /// Provenance of this CUIT: 'Lufe' when picked from the socios/autoridades list, 'Manual' when the
    /// analyst typed it (DDL 074 — the CUIT may or may not appear in LUFE). Never changes after the add,
    /// not even when the fiador is edited: an edited LUFE fiador stays 'Lufe'. Validated in the API —
    /// there is no CHECK constraint.
    /// </summary>
    public string Origen { get; set; } = string.Empty;

    /// <summary>
    /// The fiador's own NOSIS snapshot as raw JSON, in the same <c>NosisData</c> shape stored in
    /// [dbo].[Entities].NosisDataJson but queried by THIS fiador's CUIT and against the FIADOR
    /// variable group (<c>Nosis:VrFiador</c>). Deserialized at the service boundary with
    /// JsonSerializer, never by an EF value conversion.
    /// Null until an analyst refreshes the fiador — NOSIS is billed per query, so adding a fiador
    /// does not fetch it. A null here therefore means "never asked", NOT "nothing on file"; that
    /// distinction lives inside the blob, in <c>NosisData.Estado</c>. See the PII note on the class.
    /// </summary>
    public string? NosisDataJson { get; set; }

    /// <summary>
    /// UTC timestamp of when <see cref="NosisDataJson"/> was fetched, or null while it has never
    /// been queried. Deliberately not named NosisDataUpdatedAt like the column on [dbo].[Entities]:
    /// that one moves on any refresh of a snapshot the application keeps current, while this one
    /// records when the fiador's snapshot was last taken on explicit analyst demand. Both are
    /// re-fetchable — the name records intent, not an immutability guarantee.
    /// </summary>
    public DateTime? NosisFetchedAt { get; set; }

    /// <summary>
    /// Lifecycle status: 'Active' or 'Removed'. Removal is soft, so a fiador the analyst added is
    /// never hard-deleted. Only Active rows take part in the per-(PyME, CUIT) uniqueness rule.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>[dbo].[Users].UserId that added this fiador.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>UTC timestamp when the fiador was added.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The fiador's legajo in [dbo].[PersonLegajos] (DDL 069), when the fiador is a persona humana.
    /// Written ONLY by POST api/entities/{entityId}/fiadores/{fiadorId}/legajo, which creates the
    /// legajo for (TenantId, CUIT) or links the one the tenant already has — a client, or a fiador of
    /// another PyME — so one legajo may back many fiadores. Null until that call.
    /// Deliberately a bare foreign key with NO navigation property, like Entity.PersonLegajoId:
    /// FiadorConfiguration models FK_Fiadores_PersonLegajos for INSERT ORDER only, so the save that
    /// creates a legajo and links it in one SaveChanges inserts the legajo first ("Fiadores" sorts
    /// before "PersonLegajos", which is the order EF would otherwise fall back to).
    /// </summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>
    /// UTC timestamp of the latest edit of <see cref="Denominacion"/>, <see cref="Cargo"/> or
    /// <see cref="PorcAccionista"/> (PATCH, DDL 074). Null while the fiador has never been edited.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>[dbo].[Users].UserId of the latest editor, or null while never edited. No FK (DDL 074).</summary>
    public Guid? UpdatedByUserId { get; set; }
}
