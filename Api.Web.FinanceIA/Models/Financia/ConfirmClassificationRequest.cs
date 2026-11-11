// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Body of POST /api/documents/{id}/classification/confirm: an analyst accepts a NeedsReview
/// classification, stating the document type (and optionally the period end) and why.
/// </summary>
public sealed record ConfirmClassificationRequest
{
    /// <summary>The confirmed type: one of the legajo types except AutorizacionDatos.</summary>
    public string? DocumentType { get; init; }

    /// <summary>The confirmed period end. Null keeps the classified one.</summary>
    public DateOnly? PeriodEnd { get; init; }

    /// <summary>Why — at least five non-blank characters. Audited.</summary>
    public string? Reason { get; init; }
}
