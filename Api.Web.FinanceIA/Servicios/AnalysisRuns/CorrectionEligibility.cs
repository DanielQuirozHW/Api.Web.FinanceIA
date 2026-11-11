// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The eligibility reads of a re-score, a balance correction and a correction suggestion — ONE copy, so
/// the three cannot disagree on who may change an analysis. Returns the refusal code
/// (<see cref="RescoreEligibility"/>) or null, and the analysis document when it was read.
/// </summary>
internal static class CorrectionEligibility
{
    /// <summary>
    /// Cheap row-only rules first: a missing balance or a decided analysis never costs the vote or document
    /// reads. A persona analysis has neither a balance nor a document: only its decision rules apply, here
    /// and after the vote read.
    /// </summary>
    public static async Task<(string? Blocked, Document? Document)> EvaluateAsync(
        Analysis                   analysis,
        AnalysisResult             result,
        string                     tenantId,
        IAnalysisRescoreRepository rescoreRepository,
        IDocumentRepository        documentRepository,
        CancellationToken          ct)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(result);

        var rowOnly = RescoreEligibility.Evaluate(
            analysis, !string.IsNullOrWhiteSpace(result.BalanceJson), hasVotes: false, documentProcessingStatus: null);
        if (rowOnly is not null)
            return (rowOnly, null);

        var hasVotes = await rescoreRepository
            .HasAnyVoteAsync(analysis.AnalysisId, tenantId, ct)
            .ConfigureAwait(false);

        Document? document = null;
        if (analysis.DocumentId is { } documentId)
        {
            document = await documentRepository
                .GetByIdAsync(documentId, tenantId, ct)
                .ConfigureAwait(false);
        }

        return (RescoreEligibility.Evaluate(analysis, hasBalance: true, hasVotes, document?.ProcessingStatus), document);
    }
}
