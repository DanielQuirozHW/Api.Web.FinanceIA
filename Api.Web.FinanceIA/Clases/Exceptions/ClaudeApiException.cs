// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-11-06

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases.Exceptions;

/// <summary>
/// Thrown when the Anthropic Claude API returns an error,
/// times out, or the response cannot be parsed.
/// The <see cref="AppException.ClientMessage"/> is always safe to surface to callers.
/// </summary>
public sealed class ClaudeApiException : ExternalServiceException
{
    /// <summary>HTTP status code returned by the API, or null for network/timeout errors.</summary>
    public int? ApiStatusCode { get; }

    /// <summary>
    /// The internal reason this was thrown (e.g. "Claude extraction hit max_tokens …"). Written by
    /// this project's own code — never the API's raw response body, never a figure — and NEVER put in
    /// an API response: <see cref="Exception.Message"/> stays the generic client-safe text. For an HTTP
    /// error it carries what the API's error envelope said, parsed and bounded by
    /// <c>AnthropicErrorDiagnostics</c>: <c>HTTP 400 invalid_request_error: &lt;message ≤300&gt; [req_…]</c>.
    /// Written to ApiCallLogs.ErrorMessage. Exposed so tests, logs and the model-comparison harness can
    /// tell failure kinds apart.
    /// </summary>
    public string DiagnosticMessage { get; }

    /// <summary>
    /// Input tokens the failed call consumed, when the API answered with usage before the failure
    /// (max_tokens, refusal, an unreadable answer). Null when unknown (timeout, network, HTTP error).
    /// Recorded in the failed call's ApiCallLogs row: the call was billed.
    /// </summary>
    public int? InputTokens { get; init; }

    /// <summary>Output tokens the failed call consumed; null when unknown (see <see cref="InputTokens"/>).</summary>
    public int? OutputTokens { get; init; }

    /// <summary>
    /// What kind of unusable ANSWER the API sent, when the call itself succeeded: the ClaudeService
    /// reader's code — <c>max_tokens</c>, <c>refusal</c>, <c>no_text</c> or <c>envelope_invalid</c> — or,
    /// for the balance extraction, <c>schema_invalid</c> / <c>empty</c> when the answer's JSON did not fit
    /// the schema or was empty, and <c>unparseable</c> when the answer held no readable JSON object at all
    /// (no <c>{</c>, or one that does not open a complete JSON value), and <c>no_financial_statements</c>
    /// when every pass answered with a balance but the kept one holds none of the core totals (a blank
    /// page, a legalization cover). Null for a transport failure
    /// (timeout, network, HTTP status). The pipeline reads it to tell a failure caused by this project's own output cap (<c>max_tokens</c>) from one
    /// caused by the document (<c>refusal</c>, <c>schema_invalid</c>, <c>empty</c>, <c>no_financial_statements</c>),
    /// by our reading of the answer (<c>unparseable</c>) or by the service.
    /// </summary>
    public string? FailureCode { get; init; }

    /// <summary>
    /// The model id the failed call was sent to, when the thrower knows it (the balance extraction
    /// stamps it). Null otherwise.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// How many extraction passes ran before the failure (1 or 2). Set only by the no-financial-statements
    /// gate, for the pipeline's Warning; null elsewhere.
    /// </summary>
    public int? ExtractionPassesRun { get; init; }

    /// <summary>
    /// The effective confidence of the pass the gate judged. Set only by the no-financial-statements gate,
    /// for the pipeline's Warning; null elsewhere.
    /// </summary>
    public decimal? KeptConfidence { get; init; }

    /// <summary>
    /// The second pass's scan-orientation call, when one ran before the failure: a billed call priced by
    /// its own model, which the pipeline logs as its own ApiCallLogs row. Set only by the
    /// no-financial-statements gate (the only failure that can follow it); null elsewhere.
    /// </summary>
    public ScanOrientationUsage? OrientationDetection { get; init; }

    /// <summary>
    /// The seconds the provider asked us to wait before calling again (its <c>retry-after</c> on an HTTP 429),
    /// when the thrower read it; null otherwise. Read by <c>AnalysisAutoRetryPolicy.DelayFor</c> to time the
    /// one automatic retry of a background analysis (capped at 120 s). Nothing sets it yet, so a 429 retry
    /// waits the default 60 s.
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>The client-facing text of every failure but the ones that name their own.</summary>
    public const string DefaultClientMessage = "El servicio de análisis de IA no está disponible temporalmente.";

    /// <summary>
    /// The client-facing text of an extraction whose answer holds no readable JSON object
    /// (<c>claude_unparseable_answer</c>): the service answered, we could not read it — not an outage.
    /// </summary>
    public const string UnparseableAnswerClientMessage =
        "No pudimos interpretar la respuesta del modelo para este balance. Volvé a intentarlo; si se repite, avisá a soporte.";

    /// <summary>
    /// The client-facing text of an extraction that found no financial statements in the file
    /// (<c>claude_no_financial_statements</c>): the model read the file, there was no balance in it.
    /// Neutral — it never blames the analyst.
    /// </summary>
    public const string NoFinancialStatementsClientMessage =
        "No encontramos estados contables legibles en el archivo (por ejemplo, una página en blanco o solo " +
        "la carátula de la legalización). Subí el PDF con el balance completo.";

    /// <summary>
    /// Initializes a new <see cref="ClaudeApiException"/>.
    /// </summary>
    /// <param name="diagnosticMessage">Internal message — never shown to end users.</param>
    /// <param name="apiStatusCode">HTTP status code from the Anthropic API, if available.</param>
    /// <param name="innerException">Original exception if applicable.</param>
    /// <param name="clientMessage">
    /// The client-safe <see cref="Exception.Message"/>; null for <see cref="DefaultClientMessage"/>. Fixed
    /// text this project writes — never a diagnostic, never any part of a response.
    /// </param>
    public ClaudeApiException(
        string diagnosticMessage,
        int? apiStatusCode = null,
        Exception? innerException = null,
        string? clientMessage = null)
        : base(
            serviceName:   "ClaudeApi",
            errorCode:     "claude_api_error",
            httpStatusCode: 502,
            message:       clientMessage ?? DefaultClientMessage,
            innerException: innerException)
    {
        ApiStatusCode     = apiStatusCode;
        DiagnosticMessage = diagnosticMessage ?? string.Empty;
    }
}
