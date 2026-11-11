// Project : Api.Web.FinanceIA
// Created : 2026-11-05

using Api.Web.FinanceIA.Servicios.Privacy;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What the analysis chat hands Claude after the company's identifiers were stripped (K5): redacted copies
/// of the system prompt, of every replayed turn and of the analyst's current message, plus how many
/// occurrences were replaced. Never persisted — ChatMessages keeps the text as written.
/// </summary>
/// <param name="SystemPrompt">The redacted system prompt.</param>
/// <param name="History">Redacted copies of the replayed turns, both roles, in order.</param>
/// <param name="Message">The redacted current message.</param>
/// <param name="Counts">Replacements across all of the above.</param>
public sealed record RedactedChatRequest(
    string                             SystemPrompt,
    IReadOnlyList<ConversationMessage> History,
    string                             Message,
    RedactionCounts                    Counts);
