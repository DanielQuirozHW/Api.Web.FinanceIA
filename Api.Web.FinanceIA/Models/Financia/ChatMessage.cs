// Project : Api.Web.FinanceIA
// Created : 2026-06-07

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[ChatMessages].
/// Append-only chat turn storage for analyst ↔ AI conversations.
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class ChatMessage
{
    /// <summary>Primary key (auto-increment).</summary>
    public long ChatMessageId { get; set; }

    /// <summary>Foreign key to [dbo].[ChatConversations].ConversationId.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>Tenant that owns this message.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Message author: "user" or "assistant".</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Message text content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>User that sent the message (null for assistant turns).</summary>
    public Guid? SenderUserId { get; set; }

    /// <summary>Input token count (assistant turns only).</summary>
    public int? InputTokens { get; set; }

    /// <summary>Output token count (assistant turns only).</summary>
    public int? OutputTokens { get; set; }

    /// <summary>AI model used (assistant turns only).</summary>
    public string? Model { get; set; }

    /// <summary>True when this turn carried an ephemeral chat attachment whose tokens inflate
    /// InputTokens but are never replayed. The context gate skips such turns. (assistant turns only)</summary>
    public bool HadEphemeralAttachment { get; set; }

    /// <summary>
    /// The Markdown of the spreadsheet the analyst attached to THIS turn, already capped by
    /// AnalysisController.CapAttachmentMarkdown, or null when the turn carried none (script 055).
    /// USER turns only — it records what the analyst uploaded, not what was billed, which is what
    /// <see cref="HadEphemeralAttachment"/> on the assistant turn is for.
    /// <para>
    /// Persisted so the analyst can re-read the attachment when they reopen the conversation. It is
    /// NOT history: <see cref="Content"/> is what gets replayed to the model on every later turn,
    /// and this column is deliberately not that. Nothing re-injects it into a prompt.
    /// </para>
    /// </summary>
    public string? AttachmentMarkdown { get; set; }

    /// <summary>
    /// The original file name of the spreadsheet the analyst attached to THIS turn, trimmed to 255
    /// characters, or null when the turn carried none (script 056). USER turns only, beside
    /// <see cref="AttachmentMarkdown"/>.
    /// <para>
    /// It is NULL exactly when <see cref="AttachmentMarkdown"/> is null: both are written from the
    /// same capped local, so a label can never outlive the content it labels. Stored only so the
    /// analyst can tell WHICH file a turn carried when they reopen the conversation — nothing reads
    /// it into a prompt and it never reaches ClaudeService.
    /// </para>
    /// </summary>
    public string? AttachmentFileName { get; set; }

    /// <summary>UTC timestamp when the message was created.</summary>
    public DateTime CreatedAt { get; set; }
}
