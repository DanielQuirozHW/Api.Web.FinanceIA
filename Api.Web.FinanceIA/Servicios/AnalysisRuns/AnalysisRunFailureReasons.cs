// Project : Api.Web.FinanceIA
// Created : 2026-11-07

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The finer, closed vocabulary a failed run reports BESIDE <see cref="AnalysisRunFailureCodes"/>: the
/// <c>failureReason</c> and <c>causeGroup</c> of GET /api/analyses/runs' failed items and of POST
/// /api/analyses' synchronous 502, plus the <c>attemptsRemaining</c> arithmetic. Pure.
///
/// <para>
/// WHY A THIRD VOCABULARY. <c>failureCode</c> folds every <c>claude_*</c> code into
/// <see cref="AnalysisRunFailureCodes.ModelError"/> and keeps doing so (an older Portal reads it). This one
/// tells the analyst what happened and whose move it is: <see cref="Document"/> means the file is the
/// problem (upload another one), <see cref="Ours"/> means it is this service or its provider (retry, or
/// tell support).
/// </para>
///
/// <para>
/// THE MAPPING (ordinal, an allowlist like <see cref="AnalysisRunFailureCodes.FromStoredReason"/>: the
/// stored text never leaves the API):
/// <list type="bullet">
///   <item><c>claude_no_financial_statements</c> → <see cref="NoFinancialStatements"/> (document)</item>
///   <item><c>claude_refusal</c>, <c>claude_unreadable_answer</c> → <see cref="UnreadableDocument"/> (document)</item>
///   <item><c>claude_api_error</c> → <see cref="FileRejected"/> (document)</item>
///   <item><c>claude_timeout</c> → <see cref="Timeout"/> (ours)</item>
///   <item><c>claude_server_error</c>, <c>claude_network_error</c>, <c>claude_rate_limited</c>,
///         <c>claude_auth_error</c> → <see cref="ServiceUnavailable"/> (ours)</item>
///   <item><c>claude_unparseable_answer</c>, <c>claude_max_tokens</c> → <see cref="ReadingFailed"/> (ours)</item>
///   <item><c>stale_processing</c> and a cancellation exception type name → <see cref="Interrupted"/> (ours)</item>
///   <item><c>consent_required</c> → <see cref="ConsentRequired"/> (ours)</item>
///   <item>anything else, a future <c>claude_*</c> code included, or no reason → <see cref="Other"/> (ours)</item>
/// </list>
/// </para>
/// </summary>
public static class AnalysisRunFailureReasons
{
    // ── failureReason ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The file holds no readable financial statements (a blank page, the legalization cover alone).</summary>
    public const string NoFinancialStatements = "no_financial_statements";

    /// <summary>The model declined the file, or read it as something that is not a balance.</summary>
    public const string UnreadableDocument = "unreadable_document";

    /// <summary>The provider rejected the request built from the file (another 4xx, an answer with no text).</summary>
    public const string FileRejected = "file_rejected";

    /// <summary>Every attempt of the reading timed out.</summary>
    public const string Timeout = "timeout";

    /// <summary>The provider was unavailable: 5xx, network failure, rate limit (429) or this deployment's key (401/403).</summary>
    public const string ServiceUnavailable = "service_unavailable";

    /// <summary>The model answered but this service could not read the answer (no JSON in it, or it hit our output cap).</summary>
    public const string ReadingFailed = "reading_failed";

    /// <summary>The run was cut off (restart, shutdown, stale-Processing sweep) and released without finishing.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>The entity is a persona humana whose data-processing consent is not active.</summary>
    public const string ConsentRequired = "consent_required";

    /// <summary>Anything else, including every reason this build does not recognise.</summary>
    public const string Other = "other";

    /// <summary>Every value <see cref="FromStoredReason"/> can return.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        NoFinancialStatements, UnreadableDocument, FileRejected, Timeout, ServiceUnavailable,
        ReadingFailed, Interrupted, ConsentRequired, Other
    };

    // ── causeGroup ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document caused the failure: the right ask is another file.</summary>
    public const string Document = "document";

    /// <summary>This service or its provider caused the failure: the file may well be fine.</summary>
    public const string Ours = "ours";

    /// <summary>Every value <see cref="CauseGroupOf"/> can return.</summary>
    public static IReadOnlySet<string> CauseGroups { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Document, Ours };

    /// <summary>The reasons whose <c>causeGroup</c> is <see cref="Document"/>; every other one is <see cref="Ours"/>.</summary>
    public static IReadOnlySet<string> DocumentCaused { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NoFinancialStatements, UnreadableDocument, FileRejected };

    private static readonly Dictionary<string, string> ByStoredClaudeCode = new(StringComparer.Ordinal)
    {
        [AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements] = NoFinancialStatements,
        [AnalysisRunFailureCodes.StoredClaudeRefusal]               = UnreadableDocument,
        [AnalysisRunFailureCodes.StoredClaudeUnreadableAnswer]      = UnreadableDocument,
        [AnalysisRunFailureCodes.StoredClaudeApiError]              = FileRejected,
        [AnalysisRunFailureCodes.StoredClaudeTimeout]               = Timeout,
        [AnalysisRunFailureCodes.StoredClaudeServerError]           = ServiceUnavailable,
        [AnalysisRunFailureCodes.StoredClaudeNetworkError]          = ServiceUnavailable,
        [AnalysisRunFailureCodes.StoredClaudeRateLimited]           = ServiceUnavailable,
        [AnalysisRunFailureCodes.StoredClaudeAuthError]             = ServiceUnavailable,
        [AnalysisRunFailureCodes.StoredClaudeUnparseableAnswer]     = ReadingFailed,
        [AnalysisRunFailureCodes.StoredClaudeMaxTokens]             = ReadingFailed,
    };

    /// <summary>
    /// Maps one stored reason (document code, exception type name or audit reason) to a
    /// <c>failureReason</c>. See the class remarks for the table.
    /// </summary>
    public static string FromStoredReason(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Other;

        if (ByStoredClaudeCode.TryGetValue(stored, out var reason))
            return reason;

        // stale_processing and the cancellation type names, exactly as failureCode reads them.
        return AnalysisRunFailureCodes.FromStoredReason(stored) switch
        {
            AnalysisRunFailureCodes.Interrupted     => Interrupted,
            AnalysisRunFailureCodes.ConsentRequired => ConsentRequired,
            _                                       => Other
        };
    }

    /// <summary>The <c>causeGroup</c> of a <c>failureReason</c>: <see cref="Document"/> or <see cref="Ours"/>.</summary>
    public static string CauseGroupOf(string? reason)
        => reason is not null && DocumentCaused.Contains(reason) ? Document : Ours;

    /// <summary>
    /// Extraction attempts the file has left: <c>max(0, maxAttempts − attemptsSpent)</c>. Null when the
    /// budget is off (<paramref name="maxAttempts"/> ≤ 0) or the attempts spent are unknown.
    /// </summary>
    public static int? AttemptsRemaining(int maxAttempts, int? attemptsSpent)
    {
        if (maxAttempts <= 0 || attemptsSpent is not { } spent)
            return null;

        return Math.Max(0, maxAttempts - spent);
    }
}
