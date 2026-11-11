// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[Documents].[LegajoStatus] values — the vocabulary of CK_Documents_LegajoStatus (DDL 070).
/// NULL for every document that does not belong to a legajo (CK_Documents_LegajoStatus_Owner).
/// </summary>
/// <remarks>
/// Named in ONE place for the reason <see cref="AnalysisStatuses"/> is: the upload, the consent
/// endpoints, the worker's conditional updates and the confirm endpoint all route on these, and a
/// typo inside a string literal is invisible to the compiler. A test pins this list to the 070 CHECK.
///
/// <para>
/// The pipeline: Uploaded → (consent) Queued → Classifying → Blocked | NeedsReview |
/// ReadyForExtraction, or Failed. NeedsReview → ReadyForExtraction by an analyst's confirmation.
/// AutorizacionDatos goes Uploaded-free straight to Ready. Phase 2b: ReadyForExtraction → Extracting →
/// Ready | NeedsReview | Blocked, or Failed, for the types with an extractor (the rest wait in
/// ReadyForExtraction); the extraction's NeedsReview → Ready by the analyst's review, and a correction
/// sends a Ready document back to NeedsReview.
/// </para>
/// </remarks>
public static class LegajoStatuses
{
    /// <summary>Stored and measured; waiting for consent before any model call.</summary>
    public const string Uploaded = "Uploaded";

    /// <summary>Consent is recorded; waiting for the background worker.</summary>
    public const string Queued = "Queued";

    /// <summary>Claimed by a worker; the classification call is in flight.</summary>
    public const string Classifying = "Classifying";

    /// <summary>The holder CUIT the model read is a different person's. Never extracted; can only be deleted.</summary>
    public const string Blocked = "Blocked";

    /// <summary>Classified cleanly (or confirmed); extraction claims it when its type has an extractor.</summary>
    public const string ReadyForExtraction = "ReadyForExtraction";

    /// <summary>Claimed by a worker; the extraction call(s) are in flight.</summary>
    public const string Extracting = "Extracting";

    /// <summary>
    /// Classification or extraction needs an analyst to look at it. Which one: an extraction review
    /// when the document has a current extraction run.
    /// </summary>
    public const string NeedsReview = "NeedsReview";

    /// <summary>Done: an AutorizacionDatos document, or an extraction with nothing left to review (or approved).</summary>
    public const string Ready = "Ready";

    /// <summary>
    /// The pipeline failed; see Documents.LastExtractionError. Reprocess moves it back to the stage that
    /// failed: ReadyForExtraction for an extraction_ code, Queued otherwise.
    /// </summary>
    public const string Failed = "Failed";

    /// <summary>Every value CK_Documents_LegajoStatus admits, in the order the DDL lists them.</summary>
    public static readonly IReadOnlyList<string> Known =
    [
        Uploaded, Queued, Classifying, Blocked, ReadyForExtraction, Extracting, NeedsReview, Ready, Failed
    ];
}
