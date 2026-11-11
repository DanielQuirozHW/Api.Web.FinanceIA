// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoExtractions].[ReadMode] — CK_LegajoExtractions_ReadMode (DDL 070).</summary>
public static class LegajoReadModes
{
    /// <summary>The PDF went to the model as one document block.</summary>
    public const string Document = "Document";

    /// <summary>The pages went as rendered JPEG images.</summary>
    public const string Images = "Images";

    /// <summary>Reserved for Phase 2b (text pages as document, the rest as images).</summary>
    public const string Mixed = "Mixed";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Document, Images, Mixed];
}
