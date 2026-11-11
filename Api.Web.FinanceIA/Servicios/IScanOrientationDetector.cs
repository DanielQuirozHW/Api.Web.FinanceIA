// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Judges, from the page IMAGE, how far a page must be turned for its text to read upright. Used by the
/// balance extraction's second pass for the pages <see cref="PdfPageOrientation"/> cannot decide —
/// scans with no text layer, where a landscape balance table printed sideways on a portrait page would
/// otherwise be sent sideways again.
/// </summary>
public interface IScanOrientationDetector
{
    /// <summary>
    /// The clockwise turn for each of <paramref name="pageIndexes"/> (0-based), in that order; null
    /// for a page it could not judge. NEVER fails the caller: an exception, a timeout, an API error or
    /// an unreadable answer turns the affected pages unknown (logged as a Warning). Only cancellation of
    /// <paramref name="ct"/> propagates.
    /// </summary>
    /// <param name="pdfBytes">The document the pages belong to.</param>
    /// <param name="pageIndexes">0-based indexes of the pages to judge.</param>
    /// <param name="apiKey">The Anthropic API key (Anthropic:ApiKey).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<ScanOrientationResult> DetectAsync(
        byte[]              pdfBytes,
        IReadOnlyList<int>  pageIndexes,
        string              apiKey,
        CancellationToken   ct);
}
