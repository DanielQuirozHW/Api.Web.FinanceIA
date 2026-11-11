// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Text.Json;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The closed vocabulary GET /api/analyses/runs reports as a failed run's <c>failureCode</c>, and the
/// mapping from what the run left behind. Pure.
///
/// <para>
/// WHAT IS STORED IS NOT WHAT IS SHOWN. <c>Documents.LastExtractionError</c> holds a short code this
/// project writes (<see cref="StoredClaudeApiError"/>, <see cref="StoredClaudeRateLimited"/>) or, from the
/// pipeline's catch-all, an exception TYPE NAME; the <c>Analysis.Failed</c> audit row carries a
/// <c>reason</c> code. Neither ever leaves the API: <see cref="FromStoredReason"/> is an allowlist and
/// everything it does not recognise is <see cref="Other"/>.
/// </para>
///
/// <para>
/// Deliberately absent: <c>not_balance</c> (POST /api/analyses refuses a non-balance document with 400
/// before it ever reaches Processing, so no run can fail that way).
/// </para>
///
/// <para>
/// TWO VOCABULARIES. <see cref="FromStoredReason"/> / <see cref="All"/> are GET /api/analyses/runs'
/// <c>failureCode</c> and do not change: every <c>claude_*</c> code is <see cref="ModelError"/> there.
/// <see cref="ForBudgetRefusal"/> / <see cref="BudgetRefusalCodes"/> are the <c>lastFailureCode</c> of the
/// trigger's 409 <c>extraction_budget_exhausted</c>: the same mapping plus <see cref="Unreadable"/> for a
/// failure the DOCUMENT caused (the model declined it, or answered with JSON that is not a balance).
/// A third, finer one sits beside them without changing either: <see cref="AnalysisRunFailureReasons"/>
/// (<c>failureReason</c> / <c>causeGroup</c> on the same failed items and on the trigger's synchronous 502).
/// </para>
/// </summary>
public static class AnalysisRunFailureCodes
{
    /// <summary>
    /// AuditLogs.Action of a failed analysis run — the pipeline (model failure, abandoned queued run) and
    /// the stale-Processing sweep write it; GET /api/analyses/runs reads it.
    /// </summary>
    public const string FailedAuditAction = "Analysis.Failed";

    // ── Codes written by this project (Documents.LastExtractionError / audit reason) ──────────────

    /// <summary>
    /// Stored by the pipeline for a <c>ClaudeApiException</c> no finer code describes: another 4xx, an
    /// answer with no text block or an envelope that does not parse. Also every model failure recorded
    /// before the finer codes existed. Spends an attempt.
    /// </summary>
    public const string StoredClaudeApiError = "claude_api_error";

    /// <summary>Stored by the pipeline when the Anthropic API answered 429 (rate or usage limit). Does not spend an attempt.</summary>
    public const string StoredClaudeRateLimited = "claude_rate_limited";

    /// <summary>
    /// Stored when every attempt of the pass timed out. Spends an attempt: a timed-out generation is still
    /// billed, and a file that always times out must not be retried forever.
    /// </summary>
    public const string StoredClaudeTimeout = "claude_timeout";

    /// <summary>Stored when every attempt of the pass failed at the network. Ours: does not spend an attempt.</summary>
    public const string StoredClaudeNetworkError = "claude_network_error";

    /// <summary>Stored when the API answered 5xx on every attempt of the pass. Ours: does not spend an attempt.</summary>
    public const string StoredClaudeServerError = "claude_server_error";

    /// <summary>Stored when the API answered 401 or 403 (this deployment's key). Ours: does not spend an attempt.</summary>
    public const string StoredClaudeAuthError = "claude_auth_error";

    /// <summary>Stored when the model declined the request (<c>stop_reason = refusal</c>). Spends an attempt.</summary>
    public const string StoredClaudeRefusal = "claude_refusal";

    /// <summary>
    /// Stored when the model answered in full but its JSON did not fit the balance schema, or was empty.
    /// Spends an attempt.
    /// </summary>
    public const string StoredClaudeUnreadableAnswer = "claude_unreadable_answer";

    /// <summary>
    /// Stored when the model answered but no complete JSON value could be read out of the answer at all:
    /// no <c>{</c> anywhere, or the one found does not open a valid value. Recorded WITHOUT spending the
    /// file's extraction budget — reading the answer is this project's job, not the document's. Distinct
    /// from <see cref="StoredClaudeUnreadableAnswer"/> (valid JSON that is not a balance, which spends one).
    /// Reported as <see cref="ModelError"/> by the <c>claude_</c> prefix.
    /// </summary>
    public const string StoredClaudeUnparseableAnswer = "claude_unparseable_answer";

    /// <summary>
    /// Stored by the pipeline when the extraction's answer stopped at <c>max_tokens</c> (for Sonnet 4.6,
    /// after its one escalation). Recorded WITHOUT spending the file's extraction budget: the cap is this
    /// project's, not the document's. Reported as <see cref="ModelError"/> by the <c>claude_</c> prefix.
    /// </summary>
    public const string StoredClaudeMaxTokens = "claude_max_tokens";

    /// <summary>
    /// Stored when every pass answered with a balance but the kept one holds none of the seven core totals
    /// (total assets, current assets, current liabilities, total liabilities, equity, net revenue, net
    /// profit): the file carries no readable financial statements — a blank page, the CPCE legalization
    /// cover alone. No analysis and no score are created. Recorded WITHOUT spending the file's extraction
    /// budget (see <c>AnalysisPipeline.ExtractionFailureCountsAsAttempt</c>). Reported as
    /// <see cref="ModelError"/> by the <c>claude_</c> prefix; the budget refusal reports it as
    /// <see cref="Unreadable"/>.
    /// </summary>
    public const string StoredClaudeNoFinancialStatements = "claude_no_financial_statements";

    /// <summary>Audit reason of a run the stale-Processing sweep released (AnalysisJobWorker).</summary>
    public const string StoredStaleProcessing = "stale_processing";

    /// <summary>Audit reason of a queued run stopped because its entity's consent is no longer active.</summary>
    public const string StoredConsentRequired = "consent_required";

    // ── The vocabulary the endpoint returns ───────────────────────────────────────────────────────

    /// <summary>The run was cut off (process restart, shutdown) and released without finishing.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>The entity is a persona humana whose data-processing consent is not active.</summary>
    public const string ConsentRequired = "consent_required";

    /// <summary>The extraction model failed (timeout, network, API error, unusable answer).</summary>
    public const string ModelError = "model_error";

    /// <summary>The model provider refused the call for rate or usage limits (HTTP 429).</summary>
    public const string Quota = "quota";

    /// <summary>Anything else, including every reason this build does not recognise.</summary>
    public const string Other = "other";

    /// <summary>
    /// The document could not be read: the model declined it, answered with JSON that is not a balance, or
    /// found no financial statements in it.
    /// Only the extraction-budget refusal reports it (<see cref="ForBudgetRefusal"/>); GET /api/analyses/runs
    /// keeps reporting those failures as <see cref="ModelError"/>.
    /// </summary>
    public const string Unreadable = "unreadable";

    /// <summary>Every value <see cref="FromStoredReason"/> can return.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Interrupted, ConsentRequired, ModelError, Quota, Other };

    /// <summary>Every value <see cref="ForBudgetRefusal"/> can return: <see cref="All"/> plus <see cref="Unreadable"/>.</summary>
    public static IReadOnlySet<string> BudgetRefusalCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Interrupted, ConsentRequired, ModelError, Quota, Other, Unreadable };

    // Exception type names the pipeline's catch-all may store that mean "the run was cancelled".
    private static readonly HashSet<string> CancellationTypeNames =
        new(StringComparer.Ordinal) { nameof(TaskCanceledException), nameof(OperationCanceledException) };

    // Prefix of every model-call code this project writes; any future one is still a model error.
    private const string ClaudeCodePrefix = "claude_";

    // Longest audit reason worth reading. Every real one is a short code; anything longer is not ours.
    private const int MaxReasonLength = 100;

    /// <summary>Maps one stored reason (document code, exception type name or audit reason) to the vocabulary.</summary>
    public static string FromStoredReason(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return Other;

        if (string.Equals(stored, StoredStaleProcessing, StringComparison.Ordinal)
            || CancellationTypeNames.Contains(stored))
            return Interrupted;

        if (string.Equals(stored, StoredConsentRequired, StringComparison.Ordinal))
            return ConsentRequired;

        if (string.Equals(stored, StoredClaudeRateLimited, StringComparison.Ordinal))
            return Quota;

        if (stored.StartsWith(ClaudeCodePrefix, StringComparison.Ordinal))
            return ModelError;

        return Other;
    }

    /// <summary>
    /// Maps the stored reason of the failure that spent a file's extraction budget to the
    /// <c>lastFailureCode</c> of the trigger's 409: <see cref="Unreadable"/> for a failure the document
    /// caused (<see cref="StoredClaudeRefusal"/>, <see cref="StoredClaudeUnreadableAnswer"/>,
    /// <see cref="StoredClaudeNoFinancialStatements"/> — the right ask is another file), else
    /// <see cref="FromStoredReason"/>. An allowlist like that one: the stored text never leaves the API.
    /// </summary>
    public static string ForBudgetRefusal(string? stored)
    {
        if (string.Equals(stored, StoredClaudeRefusal, StringComparison.Ordinal)
            || string.Equals(stored, StoredClaudeUnreadableAnswer, StringComparison.Ordinal)
            || string.Equals(stored, StoredClaudeNoFinancialStatements, StringComparison.Ordinal))
            return Unreadable;

        return FromStoredReason(stored);
    }

    /// <summary>
    /// The <c>reason</c> string of an <c>Analysis.Failed</c> audit row's NewValueJson, or null when it
    /// has none, is not an object, carries a non-string reason, or does not parse.
    /// </summary>
    public static string? ReadAuditReason(string? newValueJson)
    {
        if (string.IsNullOrWhiteSpace(newValueJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(newValueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("reason", out var reason)
                || reason.ValueKind != JsonValueKind.String)
                return null;

            var value = reason.GetString();
            return value is { Length: > 0 and <= MaxReasonLength } ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
