// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for chat conversations and messages.
/// All reads are tenant-isolated; user-scoping applies to writes only (a conversation is readable
/// by any user of the owning tenant, but only its creator may append to or rename it).
/// All writes bump conversation counters atomically.
/// </summary>
public interface IChatRepository
{
    /// <summary>
    /// Retrieves all conversations for a given result, tenant and user, ordered by most recent first.
    /// </summary>
    Task<IReadOnlyList<ChatConversation>> GetConversationsByResultAsync(
        Guid resultId, string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Retrieves all conversations for a given result within a tenant regardless of author,
    /// ordered by most recent first.
    /// </summary>
    Task<IReadOnlyList<ChatConversation>> GetConversationsByResultForTenantAsync(
        Guid resultId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves a single conversation by ID, scoped to tenant and user. Returns null if not found.
    /// </summary>
    Task<ChatConversation?> GetConversationByIdAsync(
        Guid conversationId, string tenantId, Guid userId, CancellationToken ct);

    /// <summary>
    /// Retrieves a single conversation by ID scoped to tenant only, regardless of author.
    /// Returns null if not found.
    /// </summary>
    Task<ChatConversation?> GetConversationByIdForTenantAsync(
        Guid conversationId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Renames a conversation. Returns false if the conversation was not found for the given tenant/user.
    /// </summary>
    Task<bool> RenameConversationAsync(
        Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct);

    /// <summary>
    /// Creates a new conversation header.
    /// </summary>
    Task<ChatConversation> CreateConversationAsync(
        ChatConversation conversation, CancellationToken ct);

    /// <summary>
    /// Retrieves all messages for a conversation ordered by ChatMessageId ASC.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        Guid conversationId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves a single message by ID, scoped to its conversation and tenant. Returns null if not
    /// found, including when the ID belongs to another conversation or another tenant.
    /// </summary>
    /// <remarks>
    /// A single-row read rather than a filter over <see cref="GetMessagesAsync"/>: a conversation can
    /// hold several stored attachments of up to 200.000 characters each, and materialising all of
    /// them to return one is exactly the unbounded read the history payload was shaped to avoid.
    /// Ownership is in the predicate, not checked after the fact.
    /// </remarks>
    Task<ChatMessage?> GetMessageByIdAsync(
        long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Appends a message and bumps the parent conversation's MessageCount and LastMessageAt.
    /// </summary>
    Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct);
}
