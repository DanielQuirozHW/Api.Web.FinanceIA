// Project : Api.Web.FinanceIA
// Created : 2026-09-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[PersonLegajos].
/// The legajo of a persona humana: ONE row per natural person per tenant, keyed by
/// (TenantId, CUIT) — the person's CUIL, stored in the same hyphenated <c>DD-DDDDDDDD-D</c> form as
/// [dbo].[Entities].CUIT. An entity whose <see cref="Entity.PersonType"/> is
/// <see cref="PersonTypes.Humana"/> links to it through <see cref="Entity.PersonLegajoId"/>; a
/// later phase will let a fiador link to the same person through <c>Fiadores.PersonLegajoId</c>,
/// which is why the person lives in its own row rather than on the entity.
///
/// PII: this entity is identifying data by construction. <see cref="CUIT"/> and
/// <see cref="FullName"/> name a natural person, <see cref="ArcaSnapshotJson"/> is that person's
/// ARCA constancia and <see cref="BcraDataJson"/> their Central de Deudores record. The rules
/// (M10 Phase 2a):
/// <list type="bullet">
/// <item>The DOCUMENTS uploaded into this legajo (their PDF bytes) may be sent to the model for
/// classification and extraction ONLY while consent is recorded (<see cref="ConsentRecordedAt"/> not
/// null), and the worker re-checks that immediately before every call. The person's own paperwork
/// naturally carries their name and CUIT; that is what consent authorises.</item>
/// <item>OUR CODE never places the person's identity — <see cref="FullName"/>, <see cref="CUIT"/> —
/// into any prompt: not the classification request, not a narrative, chat or scoring prompt. The
/// holder CUIT is compared with <see cref="CUIT"/> in C#, AFTER the model has answered.</item>
/// <item>Nothing serializes this type (or <see cref="Fiador"/>) wholesale — into a prompt, a
/// response, an audit payload or a log.</item>
/// <item>No CUIT and no name in logs: log the <see cref="PersonLegajoId"/> (the #237 convention).</item>
/// </list>
/// With a natural person's CUIL and credit history in the row, a breach of any of these is a
/// reportable data incident, not a style slip.
///
/// Database First — schema changes via DDL scripts only
/// (see docs/database/069_person_legajos_and_entity_person_type.sql and 070_legajo_documents_and_extractions.sql).
/// </summary>
public sealed class PersonLegajo
{
    /// <summary>
    /// Primary key. Supplied by the application (<c>Guid.NewGuid()</c>) so the entity's
    /// <see cref="Entity.PersonLegajoId"/> is known before the single SaveChanges that inserts both.
    /// The column's NEWSEQUENTIALID() default serves the DDL 069 backfill and hand-written rows.
    /// </summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>Tenant that owns this legajo. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The person's CUIL/CUIT, hyphenated exactly like [dbo].[Entities].CUIT. Unique per tenant.</summary>
    public string CUIT { get; set; } = string.Empty;

    /// <summary>The person's name as captured at creation (the entity's BusinessName).</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="FiscalRegimes.Known"/>. Defaults to <see cref="FiscalRegimes.Desconocido"/>
    /// rather than an empty string: EF only omits a value equal to the CLR default (null), so an
    /// empty string would be sent and fail CK_PersonLegajos_FiscalRegime.
    /// </summary>
    public string FiscalRegime { get; set; } = FiscalRegimes.Desconocido;

    /// <summary>Monotributo category, a single letter A–K. Null unless the regime is Monotributo.</summary>
    public string? MonotributoCategory { get; set; }

    /// <summary>
    /// Start of activity APPROXIMATED from ARCA: the earliest registration period it reports (first day of
    /// that month, see <c>ArcaActivityStart</c>). ARCA has no inscription date and re-dates periods, so the
    /// real start may be earlier; the evaluation never rejects on it alone. Written on every legajo ARCA write
    /// (create with a snapshot, refresh-arca) and by nothing else; ARCA reporting none never clears it.
    /// </summary>
    public DateTime? ActivityStartDate { get; set; }

    /// <summary>Reserved: marital status. Not written in this phase.</summary>
    public string? MaritalStatus { get; set; }

    /// <summary>Reserved: patrimonial regime. Not written in this phase.</summary>
    public string? PatrimonialRegime { get; set; }

    /// <summary>The person's ARCA fiscal snapshot (serialized <c>ArcaFiscalData</c>).</summary>
    public string? ArcaSnapshotJson { get; set; }

    /// <summary>UTC timestamp of the snapshot in <see cref="ArcaSnapshotJson"/>.</summary>
    public DateTime? ArcaDataUpdatedAt { get; set; }

    /// <summary>The person's BCRA snapshot (serialized <c>BcraData</c>), queried by CUIL.</summary>
    public string? BcraDataJson { get; set; }

    /// <summary>UTC timestamp of the snapshot in <see cref="BcraDataJson"/>.</summary>
    public DateTime? BcraDataUpdatedAt { get; set; }

    /// <summary>The person's NOSIS snapshot. NULL, and stays NULL, in this phase.</summary>
    public string? NosisDataJson { get; set; }

    /// <summary>UTC timestamp of <see cref="NosisDataJson"/>. NULL in this phase.</summary>
    public DateTime? NosisDataUpdatedAt { get; set; }

    /// <summary>UserId of the author. Null for legajos written by the DDL 069 backfill.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>UTC. Set explicitly by the application (the in-memory provider ignores SQL defaults).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC. Set explicitly by the application on every write.</summary>
    public DateTime UpdatedAt { get; set; }

    // ── Data-processing authorization (DDL 070) ──────────────────────────────────
    // The three move together: all set by POST .../consent, all cleared by DELETE .../consent.

    /// <summary>
    /// UTC time the person's data-processing authorization was recorded, or null when there is none.
    /// Null means NO model call may be made for any document of this legajo.
    /// </summary>
    public DateTime? ConsentRecordedAt { get; set; }

    /// <summary>
    /// The AutorizacionDatos document the consent was recorded against. FK_PersonLegajos_ConsentDocument
    /// exists in the database but is not modelled — see <c>PersonLegajoConfiguration</c>.
    /// </summary>
    public Guid? ConsentDocumentId { get; set; }

    /// <summary>UserId of the analyst who recorded the consent. No FK, per 030/069.</summary>
    public Guid? ConsentRecordedBy { get; set; }
}
