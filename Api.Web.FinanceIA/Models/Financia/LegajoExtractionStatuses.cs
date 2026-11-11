// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoExtractions].[Status] — CK_LegajoExtractions_Status (DDL 070).</summary>
public static class LegajoExtractionStatuses
{
    /// <summary>The model answered and the answer parsed.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The call or the parse failed. Stored too: a run is a record of what was spent.</summary>
    public const string Failed = "Failed";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Succeeded, Failed];
}
