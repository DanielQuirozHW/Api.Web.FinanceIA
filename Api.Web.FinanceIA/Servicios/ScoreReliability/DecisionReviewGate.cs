// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The decision review gate (score credibility 1b-5): a hard rule must never APPROVE alone on unverified inputs.
/// Approving a score the reliability verdict calls <c>provisional</c> or <c>none</c> takes an explicit, audited
/// acknowledgement; <c>with_caveats</c> and <c>complete</c> take none. Deciding on figures that do not add up takes a
/// written note of 20+ characters: an approval needs a <c>figuresReviewNote</c>, a rejection may give it in the
/// <c>figuresReviewNote</c> or, when that is empty, in its <c>reviewNotes</c>. A rejection never needs the
/// acknowledgement — it is the safe direction. The ONE place the rule lives: GET /api/analyses/{id} exposes it as
/// <c>decisionRequirements</c> and DecideAsync enforces it, both from the same verdict, so the Portal can never ask
/// for less (or more) than the BFF checks. "Figures that do not add up" is exactly the <c>integrity_failed</c> reason:
/// a failure of only presentation checks (policy 5, <c>integrity_presentation</c>) does not raise it, so it asks for
/// no note.
/// </summary>
// PROVISORIO — pendiente de decisión de la SGR (score credibility 1b-5)
internal static class DecisionReviewGate
{
    /// <summary>The shortest figures-review note (trimmed) that satisfies the gate.</summary>
    public const int MinNoteLength = 20;

    /// <summary>The longest figures-review note (trimmed) the endpoint accepts.</summary>
    public const int MaxNoteLength = 1000;

    /// <summary>409 errorCode: an approval of a provisional/none score whose caveats were not acknowledged.</summary>
    public const string CaveatsNotAcknowledged = "caveats_not_acknowledged";

    /// <summary>409 errorCode: the figures failed integrity and no review note was given.</summary>
    public const string FiguresReviewRequired = "figures_review_required";

    /// <summary>The request field the note came from: the dedicated one.</summary>
    public const string FiguresReviewNoteField = "figuresReviewNote";

    /// <summary>The request field the note came from: a rejection's own reason.</summary>
    public const string ReviewNotesField = "reviewNotes";

    /// <summary>es-AR detail of a <see cref="CaveatsNotAcknowledged"/> 409.</summary>
    public const string CaveatsNotAcknowledgedDetail =
        "Para decidir sobre un análisis con advertencias en la confiabilidad del score, confirmá que las revisaste.";

    /// <summary>es-AR detail of a <see cref="FiguresReviewRequired"/> 409 on an approval.</summary>
    public const string FiguresReviewRequiredDetail =
        "Las cifras del balance no cierran. Para decidir, corregilas o dejá constancia de que las verificaste contra el documento.";

    /// <summary>es-AR detail of a <see cref="FiguresReviewRequired"/> 409 on a rejection.</summary>
    public const string RejectionReasonRequiredDetail =
        "Las cifras del balance no cierran. Para rechazar, contá en el motivo por qué rechazás y cómo verificaste las cifras (mínimo 20 caracteres).";

    /// <summary>The requirements for <paramref name="reliability"/>; null when there is no verdict (persona humana).</summary>
    public static DecisionRequirementsResponse? RequirementsFor(ScoreReliabilityResponse? reliability)
    {
        if (reliability is null)
            return null;

        var figuresFailed = ScoreReliabilityPromptBlock.Has(reliability, ScoreReliabilityCodes.IntegrityFailed);

        return new DecisionRequirementsResponse
        {
            AcknowledgeCaveats      = RequiresAcknowledgement(reliability.Level),
            FiguresReviewNote       = figuresFailed,
            RejectionReasonRequired = figuresFailed,
        };
    }

    /// <summary>True when approving at <paramref name="level"/> needs the acknowledgement: provisional or none.</summary>
    public static bool RequiresAcknowledgement(string? level)
        => string.Equals(level, ScoreReliabilityLevels.Provisional, StringComparison.Ordinal)
        || string.Equals(level, ScoreReliabilityLevels.None, StringComparison.Ordinal);

    /// <summary>The note as the gate measures and the audit stores it: trimmed, or null when blank.</summary>
    public static string? NormaliseNote(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>
    /// The note that answers the figures requirement, and the request field it came from: the figures-review note,
    /// or — on a rejection whose figures-review note is empty — its <c>reviewNotes</c>. Both normalised.
    /// </summary>
    public static (string? Note, string Field) ResolveNote(bool isRejection, string? figuresReviewNote, string? reviewNotes)
    {
        var figures = NormaliseNote(figuresReviewNote);
        if (figures is not null || !isRejection)
            return (figures, FiguresReviewNoteField);

        return (NormaliseNote(reviewNotes), ReviewNotesField);
    }
}
