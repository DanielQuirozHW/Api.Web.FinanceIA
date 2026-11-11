// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// 202 body of POST /api/analyses when the caller asked for <c>Prefer: respond-async</c>.
/// </summary>
/// <remarks>
/// It carries no analysis or result id because neither exists yet: the [dbo].[Analyses] row is
/// written only when the run completes. The caller follows the Location header
/// (GET /api/documents/{documentId}) until ProcessingStatus leaves "Processing"; the result id then
/// arrives in the ANALYSIS_COMPLETED notification (its SourceId) and in GET /api/analyses?entityId=.
/// </remarks>
public sealed class AnalysisAcceptedResponse
{
    /// <summary>The document being analysed.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The entity the analysis belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Always "Processing" — the document's status at the moment of acceptance.</summary>
    public string Status { get; set; } = string.Empty;
}
