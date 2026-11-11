// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Whether an analysis may be re-scored (a balance analysis) or re-evaluated (a persona humana one) — the
/// rules shared by the endpoint, the detail's <c>canRescore</c> and the dashboard, so the button the Portal
/// shows and the answer the endpoint gives cannot drift. Pure: the caller reads the inputs; the cooldown is
/// the endpoint's alone.
/// </summary>
public static class RescoreEligibility
{
    private const string ProcessingStatus = "Processing";

    /// <summary>
    /// The first rule <paramref name="analysis"/> breaks, as an errorCode, or null when it may be
    /// re-scored. A balance analysis is checked in this order: balance, status, decision record, document.
    /// A persona humana analysis has no balance and no document, so only the decision half applies: status,
    /// then decision record. Whether its legajo is still evaluable is the endpoint's to answer, with the
    /// refusals of POST /api/person-analyses.
    /// </summary>
    /// <param name="analysis">The analysis row (decision columns included).</param>
    /// <param name="hasBalance">The result carries a non-empty BalanceJson.</param>
    /// <param name="hasVotes">Any [AnalysisApprovalVotes] row exists for the analysis.</param>
    /// <param name="documentProcessingStatus">The analysis document's ProcessingStatus, null when unknown.</param>
    public static string? Evaluate(
        Analysis analysis, bool hasBalance, bool hasVotes, string? documentProcessingStatus)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (string.Equals(analysis.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            return DecisionGate(analysis.Status, HasDecisionRecord(analysis), hasVotes);

        if (!hasBalance)
            return RescoreErrorCodes.ResultMissing;

        return DecisionGate(analysis.Status, HasDecisionRecord(analysis), hasVotes)
               ?? (string.Equals(documentProcessingStatus, ProcessingStatus, StringComparison.Ordinal)
                   ? RescoreErrorCodes.Processing
                   : null);
    }

    /// <summary>
    /// The decision half of the rules, re-checked inside the write transaction: the analysis must
    /// still be PendingReview, with no vote and no pre-approval or decision on record.
    /// </summary>
    public static string? DecisionGate(string? status, bool hasDecisionRecord, bool hasVotes)
    {
        if (!string.Equals(status, AnalysisStatuses.PendingReview, StringComparison.Ordinal))
            return RescoreErrorCodes.NotPending;

        return hasDecisionRecord || hasVotes ? RescoreErrorCodes.HasVotes : null;
    }

    /// <summary>
    /// True when someone already signed this analysis. A return-to-review puts it back to
    /// PendingReview but deliberately keeps PreApprovedBy and ReviewedBy (see DecideAsync); a
    /// signature given against the old score (or the old evaluation) must never end up pointing at a
    /// different one.
    /// </summary>
    public static bool HasDecisionRecord(Analysis analysis)
        => analysis.PreApprovedBy is not null || analysis.PreApprovedAt is not null || analysis.ReviewedBy is not null;

    /// <summary>The Spanish detail the endpoint answers with for <paramref name="errorCode"/>.</summary>
    public static string Detail(string errorCode) => errorCode switch
    {
        RescoreErrorCodes.PersonAnalysis  => "El análisis de persona humana ya no se puede reevaluar en el lugar: su solicitante cambió de legajo o no tiene evaluación. Creá un análisis nuevo.",
        RescoreErrorCodes.ResultMissing   => "El análisis no tiene un balance extraído sobre el cual recalcular el score.",
        RescoreErrorCodes.NotPending      => "Solo se puede reanalizar un análisis pendiente de revisión.",
        RescoreErrorCodes.HasVotes        => "El análisis ya tiene una preaprobación, una decisión o votos registrados, por lo que no se puede reanalizar.",
        RescoreErrorCodes.Processing      => "El documento del análisis se está procesando. Probá de nuevo cuando termine.",
        RescoreErrorCodes.InProgress      => "Otro reanálisis de este análisis terminó primero. Recargá el análisis para ver el resultado.",
        RescoreErrorCodes.BcraUnavailable => "El BCRA sigue sin responder. El análisis no se modificó; probá de nuevo en unos minutos.",
        RescoreErrorCodes.Failed          => "No se pudo recalcular el score. El análisis no se modificó.",
        RescoreErrorCodes.PersonAnalysisNotCorrectable => "Un análisis de persona humana no tiene un balance extraído: sus cifras no se pueden corregir.",
        _                                 => "No se puede reanalizar el análisis."
    };
}
