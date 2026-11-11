// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// What C# decides from a successful classification. Computed AFTER the model answered, from the
/// model's reading and the legajo's own CUIT — which never went into the request.
/// </summary>
/// <param name="Status">Blocked, NeedsReview or ReadyForExtraction.</param>
/// <param name="ReviewReasons">Why NeedsReview (or Blocked); empty for ReadyForExtraction.</param>
/// <param name="HolderCuit">The holder CUIT to store (XX-XXXXXXXX-X), or null when none valid was read.</param>
/// <param name="HolderCuitMatches">True/false when a valid holder CUIT was read; null otherwise.</param>
public sealed record LegajoClassificationOutcome(
    string Status,
    IReadOnlyList<string> ReviewReasons,
    string? HolderCuit,
    bool? HolderCuitMatches)
{
    /// <summary>A valid holder CUIT was read and it is NOT the legajo's person. Blocks the document.</summary>
    public const string ReasonCuitMismatch = "cuit_mismatch";

    /// <summary>The document shows no holder CUIT (or the model could not tell which).</summary>
    public const string ReasonCuitMissing = "cuit_missing";

    /// <summary>The model read 11 digits whose check digit is wrong — a misread, not a person.</summary>
    public const string ReasonCuitInvalid = "cuit_invalid";

    /// <summary>The detected type differs from the one the analyst declared.</summary>
    public const string ReasonTypeMismatch = "type_mismatch";

    /// <summary>The PDF looks like several documents.</summary>
    public const string ReasonComposite = "composite";

    /// <summary>The model's confidence is below Legajo:ClassificationConfidenceThreshold.</summary>
    public const string ReasonLowConfidence = "low_confidence";

    /// <summary>
    /// Decides the document's status. Pure.
    /// <list type="number">
    /// <item>A VALID holder CUIT that differs (by digits) from the legajo's → <see cref="LegajoStatuses.Blocked"/>.
    /// A blocked document is never extracted and can only be deleted.</item>
    /// <item>Otherwise <see cref="LegajoStatuses.NeedsReview"/> when the CUIT is missing or invalid,
    /// the detected type differs from the declared one, the PDF is composite, or confidence is below
    /// <paramref name="confidenceThreshold"/> — every reason that applies is listed.</item>
    /// <item>Otherwise <see cref="LegajoStatuses.ReadyForExtraction"/>.</item>
    /// </list>
    /// </summary>
    public static LegajoClassificationOutcome Decide(
        LegajoClassificationOutput output,
        string declaredDocumentType,
        string legajoCuit,
        decimal confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(output);

        var legajoDigits = LegajoCuit.Digits(legajoCuit);
        var readDigits   = LegajoCuit.Digits(output.HolderCuit);

        string? holderCuit = null;
        bool?   matches    = null;
        var reasons = new List<string>();

        if (readDigits is null)
        {
            reasons.Add(ReasonCuitMissing);
        }
        else if (!LegajoCuit.HasValidCheckDigit(readDigits))
        {
            reasons.Add(ReasonCuitInvalid);
        }
        else
        {
            holderCuit = LegajoCuit.Format(readDigits);
            matches    = string.Equals(readDigits, legajoDigits, StringComparison.Ordinal);

            if (matches == false)
                return new LegajoClassificationOutcome(LegajoStatuses.Blocked, [ReasonCuitMismatch], holderCuit, false);
        }

        if (!string.Equals(output.DocumentType, declaredDocumentType, StringComparison.Ordinal))
            reasons.Add(ReasonTypeMismatch);

        if (output.IsComposite)
            reasons.Add(ReasonComposite);

        if (output.Confidence < confidenceThreshold)
            reasons.Add(ReasonLowConfidence);

        return new LegajoClassificationOutcome(
            reasons.Count == 0 ? LegajoStatuses.ReadyForExtraction : LegajoStatuses.NeedsReview,
            reasons,
            holderCuit,
            matches);
    }
}
