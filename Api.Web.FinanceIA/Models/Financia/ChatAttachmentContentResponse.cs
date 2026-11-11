// Project : Api.Web.FinanceIA
// Created : 2026-09-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response DTO for
/// GET /api/analyses/{resultId}/chat/{conversationId}/messages/{messageId}/attachment.
/// Serves the spreadsheet stored on one chat turn (scripts 055 and 056) back to the analyst who
/// attached it, on demand.
/// <para>
/// It exists so the conversation-history payload does not have to carry the Markdown. That endpoint
/// returns every turn of the conversation and one stored attachment can be 200.000 characters, so it
/// reports <c>hasAttachment</c> and <c>attachmentFileName</c> and leaves the content to this route,
/// which serves exactly one message.
/// </para>
/// <para>
/// A READ PATH FOR THE ANALYST, not a prompt path. Nothing here is sent to Claude: re-using the
/// spreadsheet on a later turn means the caller posting this Markdown back as an ordinary
/// <c>attachmentMarkdown</c>, the same route any upload takes.
/// </para>
/// </summary>
public sealed class ChatAttachmentContentResponse
{
    /// <summary>The turn this attachment rode in on — ChatMessages.ChatMessageId.</summary>
    public long MessageId { get; set; }

    /// <summary>
    /// The file's name as the analyst sent it (script 056), or null for a turn taken between the 055
    /// and 056 deploys — those carry Markdown that was never labelled.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// The stored Markdown, exactly as it was capped on the turn that carried it — the same string
    /// the model was shown, never the raw upload.
    /// </summary>
    public string Markdown { get; set; } = string.Empty;
}
