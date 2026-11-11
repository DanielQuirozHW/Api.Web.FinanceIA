// Project : Api.Web.FinanceIA
// Created : 2026-06-07

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[ChatConversations].
/// Conversation header linking a chat session to an AnalysisResult.
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class ChatConversation
{
    /// <summary>Primary key (auto-generated GUID).</summary>
    public Guid ConversationId { get; set; }

    /// <summary>Foreign key to [dbo].[AnalysisResults].ResultId.</summary>
    public Guid ResultId { get; set; }

    /// <summary>Foreign key to [dbo].[Analyses].AnalysisId.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>Tenant that owns this conversation.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Number of messages in this conversation.</summary>
    public int MessageCount { get; set; }

    /// <summary>UTC timestamp of the last message, or null if no messages yet.</summary>
    public DateTime? LastMessageAt { get; set; }

    /// <summary>User-visible title for the conversation (first message truncated).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The UserId of the user who created this conversation.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>UTC timestamp when the conversation was created.</summary>
    public DateTime CreatedAt { get; set; }
}
