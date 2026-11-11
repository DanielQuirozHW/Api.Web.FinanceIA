// Project : Api.Web.FinanceIA
// Created : 2026-07-17

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/entities/{id}/notes.
/// </summary>
public sealed class CreateNoteRequest
{
    /// <summary>Free-text note content. Required; max 4000 characters.</summary>
    public string Content { get; set; } = string.Empty;
}
