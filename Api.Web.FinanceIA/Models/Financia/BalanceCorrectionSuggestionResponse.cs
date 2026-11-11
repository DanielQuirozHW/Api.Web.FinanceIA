// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// 200 body of POST /api/analyses/{id}/balance-corrections/suggestions: a PROPOSAL, verified by the
/// integrity checks, that nothing applies. Accepting it is the analyst's POST /balance-corrections with
/// <c>origin: "ai_suggestion"</c>.
/// </summary>
public sealed record BalanceCorrectionSuggestionResponse
{
    /// <summary>One of <see cref="BalanceSuggestionOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>es-AR text ready to show.</summary>
    public required string Message { get; init; }

    /// <summary>The verified proposals; empty unless <see cref="Outcome"/> is fixes or reduces.</summary>
    public IReadOnlyList<BalanceCorrectionSuggestionItem> Suggestions { get; init; } = [];

    /// <summary>
    /// The failed checks a suggestion targets (the structure checks and net_profit_reconciliation) over the
    /// stored figures.
    /// </summary>
    public IReadOnlyList<string> FailedChecksBefore { get; init; } = [];

    /// <summary>
    /// The failed targeted checks with the proposals applied (the stored ones when nothing was proposed).
    /// </summary>
    public IReadOnlyList<string> FailedChecksAfter { get; init; } = [];

    /// <summary>The resolved extraction model that answered.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// The upright full page of every page a suggestion refers to, as sent to the model (downscaled), one
    /// entry per distinct page in ascending order — for the evidence to survive an imprecise row band.
    /// </summary>
    public IReadOnlyList<BalanceSuggestionPageResponse> Pages { get; init; } = [];

    /// <summary>
    /// When this response was computed and cached (UTC), on a response served from the cache; null when it
    /// was computed for this request.
    /// </summary>
    public DateTimeOffset? CachedAt { get; init; }
}

/// <summary>
/// One proposed figure. <see cref="Path"/> is EXACTLY a BalanceCorrectionPath (a top-level key or
/// <c>&lt;array&gt;[i].amount</c>); values are pesos, as stored (amounts_scale applied).
/// </summary>
public sealed record BalanceCorrectionSuggestionItem
{
    public required string Path { get; init; }

    /// <summary>The stored value; null when it was not extracted.</summary>
    public double? CurrentValue { get; init; }

    public required double SuggestedValue { get; init; }

    /// <summary>The label as printed on the document (≤ 80 characters); null when not reported.</summary>
    public string? PrintedLabel { get; init; }

    /// <summary>1-based page where it is printed; null when not reported.</summary>
    public int? Page { get; init; }

    /// <summary>A crop of the page around the printed row; null when the location is missing or invalid.</summary>
    public BalanceSuggestionEvidenceResponse? Evidence { get; init; }

    /// <summary>
    /// Where the row is on its page's entry in <see cref="BalanceCorrectionSuggestionResponse.Pages"/>, in
    /// fractions of that full page; null when the location is missing or invalid.
    /// </summary>
    public BalanceSuggestionHighlight? Highlight { get; init; }

    /// <summary>
    /// One of <see cref="BalanceSuggestionBases"/>: <c>printed</c> (the figure as printed on the document)
    /// or <c>derived</c> (calculated from other printed figures). Absent reads as printed.
    /// </summary>
    public string Basis { get; init; } = BalanceSuggestionBases.Printed;

    /// <summary>For a derived figure, the es-AR sentence that says how it was calculated; null otherwise.</summary>
    public string? Derivation { get; init; }

    /// <summary>
    /// For a derived figure, a crop of the second line it was calculated from (the income tax line; the
    /// first one, net profit, is <see cref="Evidence"/>); null otherwise or when that location is missing.
    /// </summary>
    public BalanceSuggestionEvidenceResponse? EvidenceSecondary { get; init; }

    /// <summary>
    /// The page of <see cref="EvidenceSecondary"/>, so that page is also among the response's pages. Not
    /// part of the contract.
    /// </summary>
    [JsonIgnore]
    public int? SecondaryPage { get; init; }

    /// <summary>
    /// Where the figure is PRINTED, found in the page's text layer (not the model's band): for a printed figure
    /// the figure itself, for a derived one the net result it was calculated from. Null on a scan, an unreadable
    /// text layer, or when the figure is not printed on that page unambiguously.
    /// </summary>
    public BalanceSuggestionTextMatch? TextMatch { get; init; }

    /// <summary>
    /// For a derived figure, where the second figure it was calculated from (the income tax) is PRINTED; null
    /// otherwise, or when it was not found unambiguously.
    /// </summary>
    public BalanceSuggestionTextMatch? TextMatchSecondary { get; init; }
}

/// <summary>An image crop, base64. Never logged, never persisted.</summary>
public sealed record BalanceSuggestionEvidenceResponse(string MediaType, string Data);

/// <summary>One upright full page as the model read it, downscaled. Never logged, never persisted.</summary>
/// <param name="Page">1-based page number.</param>
/// <param name="Image">The page JPEG, base64.</param>
public sealed record BalanceSuggestionPageResponse(int Page, BalanceSuggestionEvidenceResponse Image);

/// <summary>
/// A rectangle on a full page, every bound a fraction (0..1) of that page: the row band as the model reported
/// it, across the x-range of the image it read it from (the whole width, or one half's).
/// </summary>
public sealed record BalanceSuggestionHighlight(int Page, double Top, double Bottom, double Left, double Right);

/// <summary>
/// Where a suggested figure is PRINTED, found in the page's text layer (not the model's band): a rectangle on the
/// full upright page of <see cref="Page"/> — the <see cref="BalanceCorrectionSuggestionResponse.Pages"/> entry of
/// that page, the same frame as <see cref="BalanceSuggestionHighlight"/> — every bound a fraction (0..1) of that
/// page, top-left origin. The glyphs' own box, no padding.
/// </summary>
public sealed record BalanceSuggestionTextMatch(int Page, double Top, double Bottom, double Left, double Right);
