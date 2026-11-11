// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Which palette an export is printed in — the <c>theme</c> query parameter of GET
/// /api/analyses/{analysisId}/export/pdf. <see cref="Light"/> is the default and the paper document.
/// </summary>
public enum PdfTheme
{
    /// <summary>The Financia light variant: ink on white paper.</summary>
    Light = 0,

    /// <summary>The Financia dark variant: white ink on navy paper, for reading on screen.</summary>
    Dark = 1
}
