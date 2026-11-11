// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Read model for one fiador (guarantor) of a PyME, as returned by
/// <c>GET /api/entities/{entityId}/fiadores</c>, the add endpoint's 201, the PATCH and refresh-nosis 200s.
///
/// <para>
/// PII — READ THIS BEFORE REUSING THE TYPE. A <see cref="FiadorResponse"/> carries natural-person
/// identity: <see cref="CUIT"/> and <see cref="Denominacion"/> name an individual, and once
/// <see cref="Nosis"/> is populated it additionally carries the NOSIS green set (historia laboral,
/// the employer's CUIT and razón social — a third party — plus the PEP and SO compliance flags) on
/// top of the identity fields already inside a <c>NosisData</c> blob. Per the project rule it must NEVER be serialized wholesale into a Claude
/// prompt: the model analyses figures, not identities. No prompt builder reads this type today —
/// <c>ClaudeService</c> and <c>AnalysisController</c> are the only prompt builders and neither
/// mentions a fiador. Keep it that way. With a natural person's identity and compliance flags in
/// the blob, an accidental wholesale serialization is a reportable data incident, not a style slip.
/// </para>
/// </summary>
public sealed class FiadorResponse
{
    /// <summary>Fiador primary key.</summary>
    public Guid FiadorId { get; set; }

    /// <summary>Entity (PyME) this fiador guarantees.</summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// The fiador's own legajo (persona humana), once POST …/fiadores/{fiadorId}/legajo linked it; null
    /// until then. Its documents, consent and snapshots live under api/person-legajos/{personLegajoId}.
    /// </summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>The fiador's OWN CUIT, not the PyME's, hyphenated as XX-XXXXXXXX-X.</summary>
    public string CUIT { get; set; } = string.Empty;

    /// <summary>Stored name: LUFE's at add time, the analyst's for a manual or edited fiador. May be null for a LUFE add.</summary>
    public string? Denominacion { get; set; }

    /// <summary>Role (socio, presidente, cónyuge, tercero, …), or null when not informed.</summary>
    public string? Cargo { get; set; }

    /// <summary>Shareholding percentage, or null when not informed. 0 is a real value.</summary>
    public decimal? PorcAccionista { get; set; }

    /// <summary>
    /// Provenance of the CUIT: 'Lufe' when picked from the socios/autoridades list, 'Manual' when the
    /// analyst typed it. Unchanged by an edit.
    /// </summary>
    public string Origen { get; set; } = string.Empty;

    /// <summary>
    /// True when the fiador was edited (<see cref="UpdatedAt"/> set), or when it is a 'Lufe' fiador whose
    /// stored name, cargo or percentage differs from what LUFE currently reports for its CUIT (see
    /// <see cref="LufeOriginal"/>). Derived per read — never stored — so a LUFE refresh that changes a
    /// socio's data can turn it true without anyone editing the fiador.
    /// </summary>
    public bool Editado { get; set; }

    /// <summary>
    /// What LUFE currently reports for this CUIT, from the entity's persisted LUFE snapshot. Set only for
    /// a 'Lufe' fiador while LUFE still lists the CUIT; null for a 'Manual' one and when LUFE dropped it.
    /// </summary>
    public FiadorLufeOriginalResponse? LufeOriginal { get; set; }

    /// <summary>UTC timestamp of the latest edit, or null while never edited.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Display name of the latest editor; "Usuario" when that user no longer resolves; null while never
    /// edited.
    /// </summary>
    public string? UpdatedByName { get; set; }

    /// <summary>Lifecycle status. Always 'Active' on these endpoints — 'Removed' rows are not listed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the fiador was added.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The fiador's OWN NOSIS snapshot, queried by THIS fiador's CUIT rather than the PyME's and
    /// against the FIADOR variable group. Null until an analyst calls
    /// <c>POST /api/entities/{entityId}/fiadores/{fiadorId}/refresh-nosis</c> — NOSIS is billed per
    /// query, so listing fiadores never fetches it. Null therefore means "never asked", NOT
    /// "nothing on file": that answer is <c>NosisData.Estado == NoDataReported</c> on a non-null
    /// snapshot. Named <c>Nosis</c> to match <see cref="EntityResponse.Nosis"/>.
    /// </summary>
    public NosisData? Nosis { get; set; }

    /// <summary>
    /// UTC timestamp of when <see cref="Nosis"/> was fetched, or null while it never has been.
    /// Deliberately not named NosisDataUpdatedAt like the field on <see cref="EntityResponse"/>:
    /// that one tracks a snapshot the application keeps current, this one records the last explicit
    /// analyst-demanded fetch. Not a typo — see docs/database/046_fiadores_table.sql.
    /// </summary>
    public DateTime? NosisFetchedAt { get; set; }
}
