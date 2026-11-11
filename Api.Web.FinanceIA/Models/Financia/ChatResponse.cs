// Project : Api.Web.FinanceIA
// Created : 2026-05-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response body for POST /api/analyses/{resultId}/chat.
/// </summary>
public sealed class ChatResponse
{
    /// <summary>The conversation this message belongs to.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>The assistant's reply in Spanish.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Always "assistant".</summary>
    public string Role { get; set; } = "assistant";

    /// <summary>Input token count for this turn, or null.</summary>
    public int? InputTokens { get; set; }

    /// <summary>Output token count for this turn, or null.</summary>
    public int? OutputTokens { get; set; }
}
