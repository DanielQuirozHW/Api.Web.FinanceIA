// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/entities/{entityId}/fiadores. Two shapes, told apart by <see cref="Origen"/>:
/// <list type="bullet">
/// <item><b>LUFE</b> (<see cref="Origen"/> absent or "Lufe", which is what the Portal has always sent):
/// adds one of the PyME's LUFE socios/autoridades. The CUIT must be among them, and the name, cargo and
/// percentage are copied from LUFE — the request's values are advisory and ignored.</item>
/// <item><b>Manual</b> (<see cref="Origen"/> = "Manual", DDL 074): the analyst's own CUIT and data, which
/// are stored as sent. The CUIT needs a valid check digit and may or may not appear in LUFE;
/// <see cref="Denominacion"/> is required.</item>
/// </list>
/// </summary>
public sealed class AddFiadorRequest
{
    /// <summary>
    /// The CUIT to add, with or without hyphens — it is normalised to the stored XX-XXXXXXXX-X form before
    /// anything is looked up. On the LUFE path it must belong to one of the entity's
    /// <c>LufeData.Autoridades[]</c> (otherwise 400: the row would be stamped Origen='Lufe' while naming
    /// someone LUFE never reported). On the manual path it must carry a valid modulo-11 check digit.
    /// </summary>
    [Required]
    public string Cuit { get; set; } = string.Empty;

    /// <summary>
    /// LUFE path: ACCEPTED BUT NOT AUTHORITATIVE — the stored value is LUFE's, so the persisted row always
    /// agrees with the provenance Origen='Lufe' claims. Manual path: REQUIRED, trimmed, 2–300 characters.
    /// </summary>
    public string? Denominacion { get; set; }

    /// <summary>
    /// LUFE path: ignored (see <see cref="Denominacion"/>). Manual path: optional free text (socio,
    /// cónyuge, tercero, …), trimmed, at most 200 characters.
    /// </summary>
    public string? Cargo { get; set; }

    /// <summary>
    /// LUFE path: ignored (see <see cref="Denominacion"/>). Manual path: optional, 0–100 with at most
    /// 2 decimals.
    /// </summary>
    public decimal? PorcAccionista { get; set; }

    /// <summary>
    /// "Lufe" (or absent) for the LUFE path, "Manual" for the manual path; case-insensitive. Any other
    /// value is a 400.
    /// </summary>
    public string? Origen { get; set; }
}
