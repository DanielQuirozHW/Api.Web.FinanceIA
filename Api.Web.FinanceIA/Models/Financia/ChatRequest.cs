// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// A single turn in the conversation between the analyst and the AI.
/// </summary>
public sealed class ConversationMessage
{
    /// <summary>Message author: "user" or "assistant".</summary>
    [Required]
    public string Role { get; set; } = string.Empty;

    /// <summary>Message text.</summary>
    [Required]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Request body for POST /api/analyses/{resultId}/chat.
/// </summary>
public sealed class ChatRequest
{
    /// <summary>The analyst's latest message.</summary>
    [Required]
    [MinLength(1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional existing conversation to continue. If null, a new conversation is created.
    /// </summary>
    public Guid? ConversationId { get; set; }

    /// <summary>
    /// Optional id of the Claude model to answer this turn with, from the list
    /// GET /api/billing/usage reports as <c>chatEnabledModels</c>.
    ///
    /// <para>
    /// EPHEMERAL, exactly like <see cref="AttachmentMarkdown"/> below: it applies to this ONE turn
    /// and is not stored on the conversation, so the next turn of the same conversation starts from
    /// the default again unless it names one too. A conversation is therefore free to mix models —
    /// a cheap one for a quick follow-up, a capable one for the hard question — and
    /// <c>ChatMessages.Model</c> records, per turn, which one actually answered.
    /// </para>
    ///
    /// <para>
    /// NEVER A 400. An id this tenant is not entitled to, an id that does not exist, and no id at all
    /// are all handled the same way: the turn is answered by the first model on the tenant's
    /// allow-list. A model the analyst cannot have is not worth failing an otherwise valid question
    /// over, and the allow-list can change under a Portal that cached it.
    /// </para>
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Optional Markdown of an .xlsx the analyst attached to THIS turn, as returned by
    /// POST /api/analyses/{resultId}/chat/attachment. It rides the request and reaches Claude as a
    /// second text block on the final user turn -- once. It is never written to ChatMessages.Content,
    /// which is what history is rebuilt from, so it can never come back as history on a later turn.
    ///
    /// <para>
    /// The capped value IS stored, in ChatMessages.AttachmentMarkdown on the user turn (script 055),
    /// so the analyst can read back what they attached when they reopen the conversation. That column
    /// is storage, not history: nothing re-injects it into a prompt.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Deliberately carries NO length attribute. The endpoint TRUNCATES an oversized attachment
    /// rather than refusing the turn (see AnalysisController.AttachmentMarkdownMaxChars): the parser
    /// applies no per-cell length cap, so /chat/attachment can legitimately return Markdown above
    /// that ceiling, and a reject here would 400 a file the server itself had just accepted.
    /// </remarks>
    public string? AttachmentMarkdown { get; set; }

    /// <summary>
    /// Optional original file name of the .xlsx whose Markdown rides <see cref="AttachmentMarkdown"/>
    /// on this turn, so the analyst can tell WHICH spreadsheet a turn carried when they reopen the
    /// conversation. Persisted on the user turn as ChatMessages.AttachmentFileName (script 056).
    ///
    /// <para>
    /// It is a LABEL AND NOTHING ELSE. It is never sent to Claude, never written to
    /// ChatMessages.Content, and never reaches the system prompt — the model is told that a
    /// spreadsheet arrived, not what it was called.
    /// </para>
    ///
    /// <para>
    /// Stored only when <see cref="AttachmentMarkdown"/> is non-blank, so a name can never be kept
    /// for content that was not. Sending one without the other stores NULL for both.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Deliberately carries NO length attribute, for the same reason
    /// <see cref="AttachmentMarkdown"/> does not: the endpoint TRIMS an over-long name to 255
    /// characters rather than refusing the turn. Rejecting a question over the length of a file name
    /// would dead-end an analyst whose file the server had already accepted and parsed. Any directory
    /// component is stripped — what is stored is the leaf name.
    /// </remarks>
    public string? AttachmentFileName { get; set; }
}
