// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>A legajo's recorded data-processing authorization.</summary>
public sealed record LegajoConsentResponse
{
    /// <summary>When it was recorded (UTC).</summary>
    public required DateTime RecordedAt { get; init; }

    /// <summary>The AutorizacionDatos document it was recorded against.</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>Display name of the analyst who recorded it, or null when unknown.</summary>
    public string? RecordedByName { get; init; }

    /// <summary>
    /// True when the consent allows model calls: recorded AND its AutorizacionDatos document still
    /// exists in this legajo and is not deleted. False flags a consent recorded against a document
    /// that is gone (only possible by hand in the database) — record it again.
    /// </summary>
    public required bool IsActive { get; init; }
}
