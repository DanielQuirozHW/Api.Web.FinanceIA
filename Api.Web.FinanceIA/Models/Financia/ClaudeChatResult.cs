// Project : Api.Web.FinanceIA
// Created : 2026-05-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Result returned by <see cref="Api.Web.FinanceIA.Servicios.IClaudeService.ChatAsync"/>.
/// </summary>
public sealed record ClaudeChatResult(
    /// <summary>The assistant's reply text.</summary>
    string Text,
    /// <summary>Number of input tokens consumed.</summary>
    int InputTokens,
    /// <summary>Number of output tokens generated.</summary>
    int OutputTokens
);
