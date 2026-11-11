// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoExtractions].[Kind] — CK_LegajoExtractions_Kind (DDL 070).</summary>
public static class LegajoExtractionKinds
{
    /// <summary>The Phase 2a pass: what the document is, whose it is, which period.</summary>
    public const string Classification = "Classification";

    /// <summary>The Phase 2b pass: the figures.</summary>
    public const string Extraction = "Extraction";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Classification, Extraction];
}
