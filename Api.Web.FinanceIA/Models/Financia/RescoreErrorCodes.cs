// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The <c>errorCode</c> values of POST /api/analyses/{id}/rescore and of the detail's
/// <c>rescoreBlockedReason</c>. Part of the Portal contract: renaming one is a breaking change.
/// </summary>
public static class RescoreErrorCodes
{
    public const string PersonAnalysis   = "person_analysis_not_rescorable";
    public const string ResultMissing    = "analysis_result_missing";
    public const string NotPending       = "analysis_not_pending";
    public const string HasVotes         = "analysis_has_votes";
    public const string Processing       = "analysis_processing";
    public const string InProgress       = "analysis_rescore_in_progress";
    public const string TooManyRequests  = "too_many_requests";
    public const string BcraUnavailable  = "bcra_unavailable";
    public const string Failed           = "rescore_failed";

    /// <summary>A balance correction asked of a persona humana analysis, which has no extracted balance.</summary>
    public const string PersonAnalysisNotCorrectable = "person_analysis_not_correctable";

    /// <summary>A balance correction whose request or figures fail validation (400, with field paths).</summary>
    public const string ValidationError = "validation_error";

    /// <summary>A correction suggestion asked of a balance whose stored integrity has no failed structure check.</summary>
    public const string SuggestionNotApplicable = "suggestion_not_applicable";

    /// <summary>A correction suggestion whose analysis document cannot be read from storage.</summary>
    public const string DocumentUnavailable = "document_unavailable";

    /// <summary>The model call of a correction suggestion failed (502).</summary>
    public const string SuggestionUnavailable = "suggestion_unavailable";
}
