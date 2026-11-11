// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// What of Documents.LastExtractionError a legajo document may show the Portal (LegajoDocumentResponse
/// .LastErrorCode). Pure.
///
/// <para>
/// The legajo pipeline writes only <see cref="LegajoClassificationErrors"/> and
/// <see cref="LegajoExtractionErrors"/> codes there, but the column is shared with the balance
/// extraction, which may write an exception type name. So the response passes it through this
/// allowlist and falls back to <see cref="LegajoClassificationErrors.Unexpected"/>: whatever the column
/// holds, only a vocabulary code leaves the API.
/// </para>
/// </summary>
public static class LegajoFailureCodes
{
    private static readonly string[] StageCodes =
    [
        LegajoClassificationErrors.NotConfigured,
        LegajoClassificationErrors.PdfUnreadable,
        LegajoClassificationErrors.ConsentRevoked,
        LegajoClassificationErrors.Timeout,
        LegajoClassificationErrors.NetworkError,
        LegajoClassificationErrors.RateLimited,
        LegajoClassificationErrors.ApiError,
        LegajoClassificationErrors.InvalidResponse,
        LegajoClassificationErrors.Truncated,
        LegajoClassificationErrors.Refused,
        LegajoClassificationErrors.AttemptsExhausted,
        LegajoClassificationErrors.StorageUnavailable,
        LegajoClassificationErrors.Unexpected,
        LegajoExtractionErrors.TooManyPages,
        LegajoExtractionErrors.RequestTooLarge,
        LegajoExtractionErrors.Timeout,
        LegajoExtractionErrors.InvalidOutput
    ];

    /// <summary>Every code either stage writes to the document: each code as is and in its extraction-stage form.</summary>
    public static IReadOnlySet<string> Known { get; } =
        StageCodes.Concat(StageCodes.Select(LegajoExtractionErrors.ForDocument)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The failure code to show: null unless the document is <see cref="LegajoStatuses.Failed"/> (the
    /// column is not cleared on reprocess, so a recovered document still carries its old code); a known
    /// code as stored; <see cref="LegajoClassificationErrors.Unexpected"/> for anything else.
    /// </summary>
    public static string? ForResponse(string? legajoStatus, string? lastError)
    {
        if (legajoStatus != LegajoStatuses.Failed)
            return null;

        return lastError is not null && Known.Contains(lastError) ? lastError : LegajoClassificationErrors.Unexpected;
    }
}
