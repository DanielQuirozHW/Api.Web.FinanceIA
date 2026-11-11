// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[Documents].[PdfType] values — the vocabulary of CK_Documents_PdfType. 001 admitted
/// Scanned and Native; DDL 070 added Mixed. Written only by the legajo upload, from the page-quality
/// verdicts (every page text → Native, no page text → Scanned, both → Mixed).
/// </summary>
public static class PdfTypes
{
    /// <summary>Every page carries an extractable text layer.</summary>
    public const string Native = "Native";

    /// <summary>No page carries usable text (an image-only scan).</summary>
    public const string Scanned = "Scanned";

    /// <summary>Some pages have text and some do not.</summary>
    public const string Mixed = "Mixed";

    /// <summary>Every value the CHECK admits (besides NULL), in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Scanned, Native, Mixed];
}
