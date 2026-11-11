// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for PATCH /api/entities/{entityId}/fiadores/{fiadorId}: corrects the editable fields of
/// an ACTIVE fiador of any origen (DDL 074).
///
/// <para>
/// MERGE-PATCH SEMANTICS. A property ABSENT from the JSON leaves the stored value unchanged; a property
/// PRESENT with <c>null</c> clears it. System.Text.Json calls a setter only for a property that appears in
/// the payload, so each setter flips its <c>…Specified</c> flag and the controller can tell "not sent"
/// from "sent as null". <see cref="Denominacion"/> cannot be cleared — a null or blank one is a 400.
/// </para>
///
/// <para>
/// PII: every field but the flags names a natural person. Never logged, never audited as a value.
/// </para>
/// </summary>
public sealed class PatchFiadorRequest
{
    private string?  _denominacion;
    private string?  _cargo;
    private decimal? _porcAccionista;

    /// <summary>
    /// The fiador's CUIT, OPTIONAL and NOT EDITABLE. Accepted only so a client can post the whole row
    /// back: when present it must normalise to the stored CUIT, otherwise the request is a 400.
    /// </summary>
    public string? Cuit { get; set; }

    /// <summary>Name (trimmed, 2–300 characters). Present-and-null or blank is a 400.</summary>
    public string? Denominacion
    {
        get => _denominacion;
        set { _denominacion = value; DenominacionSpecified = true; }
    }

    /// <summary>Free-text role (socio, cónyuge, tercero, …), at most 200 characters. Null clears it.</summary>
    public string? Cargo
    {
        get => _cargo;
        set { _cargo = value; CargoSpecified = true; }
    }

    /// <summary>Shareholding percentage, 0–100 with at most 2 decimals. Null clears it.</summary>
    public decimal? PorcAccionista
    {
        get => _porcAccionista;
        set { _porcAccionista = value; PorcAccionistaSpecified = true; }
    }

    /// <summary>True when <see cref="Denominacion"/> appeared in the payload.</summary>
    [JsonIgnore]
    public bool DenominacionSpecified { get; private set; }

    /// <summary>True when <see cref="Cargo"/> appeared in the payload.</summary>
    [JsonIgnore]
    public bool CargoSpecified { get; private set; }

    /// <summary>True when <see cref="PorcAccionista"/> appeared in the payload.</summary>
    [JsonIgnore]
    public bool PorcAccionistaSpecified { get; private set; }
}
