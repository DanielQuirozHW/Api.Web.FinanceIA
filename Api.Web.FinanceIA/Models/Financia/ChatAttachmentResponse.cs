// Project : Api.Web.FinanceIA
// Created : 2026-08-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response DTO for POST /api/analyses/{resultId}/chat/attachment.
/// Carries the spreadsheet rendered as Markdown so the caller can include it in a chat turn. The
/// .xlsx itself is never stored; only this rendering is, and only once it rides a chat turn, on the
/// user turn that carried it (scripts 055 and 056).
/// </summary>
public sealed class ChatAttachmentResponse
{
    /// <summary>One Markdown table per rendered sheet, plus the truncation note when there is one.</summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>Sheets included in <see cref="Markdown"/>.</summary>
    public int SheetCount { get; set; }

    /// <summary>Rows included across all sheets, header rows counted.</summary>
    public int RowCount { get; set; }

    /// <summary>Cells included across all sheets.</summary>
    public int CellCount { get; set; }

    /// <summary>True when a cap was reached and part of the file was left out.</summary>
    public bool Truncated { get; set; }

    /// <summary>es-AR note naming what was left out; null when nothing was.</summary>
    public string? Note { get; set; }
}
