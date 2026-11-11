// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// A legajo without its JSON snapshots and without the person's name: what the legajo endpoints and
/// the consent write need. <see cref="CUIT"/> is carried for the upload's and the worker's checks only;
/// the read endpoints do not return it.
/// </summary>
public sealed record LegajoSummary
{
    /// <summary>The legajo.</summary>
    public required Guid PersonLegajoId { get; init; }

    /// <summary>Tenant slug.</summary>
    public required string TenantId { get; init; }

    /// <summary>The entity linked to this legajo (UX_Entities_PersonLegajoId), or null when none.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>The person's CUIT (hyphenated). PII — never returned by a read endpoint, never logged.</summary>
    public required string CUIT { get; init; }

    /// <summary>One of <see cref="FiscalRegimes.Known"/>.</summary>
    public required string FiscalRegime { get; init; }

    /// <summary>A–K, or null.</summary>
    public string? MonotributoCategory { get; init; }

    /// <summary>When consent was recorded, or null.</summary>
    public DateTime? ConsentRecordedAt { get; init; }

    /// <summary>The AutorizacionDatos document the consent points at.</summary>
    public Guid? ConsentDocumentId { get; init; }

    /// <summary>Who recorded it.</summary>
    public Guid? ConsentRecordedBy { get; init; }

    /// <summary>
    /// Consent is recorded AND its AutorizacionDatos document still exists, belongs to this legajo
    /// and is not deleted. The only consent that allows a model call.
    /// </summary>
    public bool HasActiveConsent { get; init; }
}
