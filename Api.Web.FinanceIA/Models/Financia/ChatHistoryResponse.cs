using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

public sealed class ChatHistoryResponse
{
    public Guid? ConversationId { get; set; }
    public IReadOnlyList<ChatHistoryMessage> Messages { get; set; } = [];
}

public sealed class ChatHistoryMessage
{
    /// <summary>
    /// The message's ChatMessages.ChatMessageId — a BIGINT identity, not a GUID. It is the handle the
    /// Portal needs to call
    /// GET /api/analyses/{resultId}/chat/{conversationId}/messages/{messageId}/attachment, which is
    /// where the stored Markdown is served from (script 056).
    /// </summary>
    public long MessageId { get; set; }

    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Input token count for this turn, or null (assistant turns only).</summary>
    public int? InputTokens { get; set; }

    /// <summary>Output token count for this turn, or null (assistant turns only).</summary>
    public int? OutputTokens { get; set; }

    /// <summary>
    /// True when the analyst attached a spreadsheet to this turn and its Markdown is stored
    /// (script 055). User turns only.
    /// <para>
    /// A marker, never the content: one attachment can reach 200.000 characters, so shipping it in
    /// a list that returns every turn of the conversation would make the history payload unbounded.
    /// The Markdown itself is fetched on demand.
    /// </para>
    /// </summary>
    public bool HasAttachment { get; set; }

    /// <summary>
    /// The name of the file behind <see cref="HasAttachment"/>, or null when the turn carried none
    /// (script 056). User turns only.
    /// <para>
    /// This is what makes the marker legible — "un adjunto" is not an answer when three versions of
    /// the same cash flow were tried on one analysis. It is tens of bytes, unlike the Markdown, which
    /// stays off this payload for the reason above. Null on a turn taken between the 055 and 056
    /// deploys: those carry Markdown that was never labelled.
    /// </para>
    /// </summary>
    public string? AttachmentFileName { get; set; }
}

public sealed class ChatConversationListResponse
{
    public IReadOnlyList<ChatConversationSummary> Conversations { get; set; } = [];
}

public sealed class ChatConversationSummary
{
    public Guid ConversationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>UserId of the analyst who created the conversation.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Display name of the analyst who created the conversation.</summary>
    public string CreatedByName { get; set; } = string.Empty;
}

public sealed class RenameConversationRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
}
