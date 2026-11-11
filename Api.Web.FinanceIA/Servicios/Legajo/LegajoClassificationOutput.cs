// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// What the classification model returned through its tool call, after C# parsing and range checks.
/// PII: <see cref="HolderCuit"/> and its <see cref="HolderCuitQuote"/> are read from the person's
/// paperwork (or a third party's). Never logged; never returned wholesale by an endpoint.
/// </summary>
public sealed record LegajoClassificationOutput
{
    /// <summary>One of the legajo types, or <see cref="LegajoClassificationPrompt.UnknownType"/>.</summary>
    [JsonPropertyName("documentType")]
    public required string DocumentType { get; init; }

    /// <summary>The fiscal year the document refers to, when it states one.</summary>
    [JsonPropertyName("fiscalYear")]
    public int? FiscalYear { get; init; }

    /// <summary>The close of the period the document covers.</summary>
    [JsonPropertyName("periodEnd")]
    public DateOnly? PeriodEnd { get; init; }

    /// <summary>The date a statement of assets is measured at (manifestación de bienes).</summary>
    [JsonPropertyName("measurementDate")]
    public DateOnly? MeasurementDate { get; init; }

    /// <summary>The holder CUIT as 11 digits, or null when the document does not show one.</summary>
    [JsonPropertyName("holderCuit")]
    public string? HolderCuit { get; init; }

    /// <summary>1-based page the holder CUIT was read on.</summary>
    [JsonPropertyName("holderCuitPage")]
    public int? HolderCuitPage { get; init; }

    /// <summary>The literal text the holder CUIT was read from.</summary>
    [JsonPropertyName("holderCuitQuote")]
    public string? HolderCuitQuote { get; init; }

    /// <summary>One of <c>Models.Financia.EngagementTypes</c>, or null when undetermined.</summary>
    [JsonPropertyName("engagementType")]
    public string? EngagementType { get; init; }

    /// <summary>True when the PDF looks like several documents bound together.</summary>
    [JsonPropertyName("isComposite")]
    public bool IsComposite { get; init; }

    /// <summary>The parts of a composite PDF. Empty otherwise.</summary>
    [JsonPropertyName("segments")]
    public IReadOnlyList<LegajoClassificationSegment> Segments { get; init; } = [];

    /// <summary>The model's self-reported confidence, clamped to 0..1.</summary>
    [JsonPropertyName("confidence")]
    public decimal Confidence { get; init; }

    /// <summary>A short es-AR note for the analyst.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}
