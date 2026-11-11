// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Sends a financial document to the Anthropic Claude API and returns
/// the structured analysis result.
/// </summary>
public interface IClaudeService
{
    /// <summary>
    /// Sends a conversational message to Claude with a pre-built system prompt
    /// and returns the assistant's reply text plus token counts for billing.
    /// </summary>
    /// <param name="model">
    /// Id of the Claude model to call, which MUST name an entry in
    /// <see cref="Models.Financia.ChatModelCatalog"/> — an unknown id throws rather than reaching the
    /// API, because the catalogue is what says how a request for that model has to be shaped (Claude
    /// Sonnet 5 rejects <c>temperature</c>, and needs a larger <c>max_tokens</c> because it thinks
    /// before answering).
    /// <para>
    /// Whether the TENANT may use it is the caller's decision, not this method's: the allow-list
    /// lives on <c>TenantBillingConfig.ChatEnabledModels</c> and is resolved in
    /// <c>AnalysisController</c>, which holds the tenant context. The check here is only that the
    /// model exists.
    /// </para>
    /// </param>
    /// <param name="systemPrompt">
    /// System context that grounds the conversation in a specific analysis.
    /// </param>
    /// <param name="history">
    /// Prior conversation turns in chronological order (oldest first).
    /// Must alternate user / assistant.
    /// </param>
    /// <param name="userMessage">The analyst's latest question or message.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <param name="attachmentMarkdown">
    /// Optional Markdown of a spreadsheet the analyst attached to THIS turn. When present it rides
    /// the final user turn as a SECOND text block, framed as data; when null or whitespace the
    /// request is byte-identical to one built before this parameter existed. Never persisted and
    /// never replayed in <paramref name="history"/> — it lives only for the duration of this call.
    /// </param>
    /// <returns>
    /// A <see cref="ClaudeChatResult"/> containing the assistant's reply and usage metrics.
    /// </returns>
    /// <exception cref="Clases.Exceptions.ClaudeApiException">
    /// Thrown on API error, timeout or rate-limit, and when <paramref name="model"/> is not in the
    /// catalogue. Never swallowed — callers must handle it.
    /// </exception>
    Task<ClaudeChatResult> ChatAsync(
        string                             model,
        string                             systemPrompt,
        IReadOnlyList<ConversationMessage> history,
        string                             userMessage,
        CancellationToken                  ct,
        string?                            attachmentMarkdown = null);

    /// <summary>
    /// Analyses the PDF document and returns a structured financial result.
    /// </summary>
    /// <param name="pdfBytes">Raw PDF file bytes.</param>
    /// <param name="documentType">Document type label (e.g. Balance, Complemento).</param>
    /// <param name="fiscalYear">Optional fiscal year for context.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>Structured result with ratios, alerts, balance check and audit hashes.</returns>
    /// <exception cref="Clases.Exceptions.ClaudeApiException">
    /// Thrown on 5xx errors after retries, 429 rate-limit, timeout, or unparseable response.
    /// </exception>
    Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(
        byte[]            pdfBytes,
        string            documentType,
        int?              fiscalYear,
        CancellationToken ct);

    /// <summary>
    /// Generates a professional narrative (in Spanish) that justifies the credit-risk
    /// scoring result. Maximum 4 paragraphs, tone suitable for Argentine SGR credit analysts.
    /// </summary>
    /// <param name="scoring">Scoring result produced by the deterministic engine.</param>
    /// <param name="ratios">Financial ratios used in the scoring.</param>
    /// <param name="bcraData">BCRA enrichment data, or <c>null</c> if unavailable.</param>
    /// <param name="macroData">Macroeconomic variables, or <c>null</c> if unavailable.</param>
    /// <param name="reliability">
    /// The score-reliability verdict of THIS score (score credibility 1b-3), or <c>null</c> when it could not
    /// be evaluated. Restated in the prompt through fixed phrases only — never its messages. Required, never
    /// defaulted: a caller that forgot it would ship a narrative presenting a provisional score as definitive.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>
    /// A <see cref="ClaudeNarrativeResult"/> with the narrative text and usage metrics.
    /// Never throws — all errors are caught and logged; returns empty text on failure.
    /// </returns>
    Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(
        ScoringResult     scoring,
        RatiosData        ratios,
        BcraData?         bcraData,
        BcraMacroData?    macroData,
        ScoreReliabilityResponse? reliability,
        CancellationToken ct);

    /// <summary>
    /// Asks the extraction model, once, for the figures of the failed integrity checks as printed on the
    /// document (Phase 10-F4a "Sugerir corrección"), over the page images the second extraction pass
    /// builds. A SUGGESTION only: the caller verifies it with the integrity checks, and nothing is applied.
    /// </summary>
    /// <param name="pdfBytes">The analysis' balance PDF, already unwrapped like the extraction's.</param>
    /// <param name="question">The figures asked about, numbers and fixed label hints only.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <returns>
    /// The answer with the images it refers to, or a failure code. Never throws but on cancellation.
    /// </returns>
    Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
        byte[]                    pdfBytes,
        BalanceSuggestionQuestion question,
        CancellationToken         ct);
}
