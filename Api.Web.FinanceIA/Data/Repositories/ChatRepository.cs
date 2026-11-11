// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IChatRepository"/>.
/// All reads use AsNoTracking; all writes call DetectChanges before SaveChangesAsync.
/// </summary>
public sealed class ChatRepository : IChatRepository
{
    private readonly FinanciaDbContext          _dbContext;
    private readonly ILogger<ChatRepository>   _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ChatRepository"/>.
    /// </summary>
    public ChatRepository(FinanciaDbContext dbContext, ILogger<ChatRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatConversation>> GetConversationsByResultAsync(
        Guid resultId, string tenantId, Guid userId, CancellationToken ct)
    {
        return await _dbContext.ChatConversations
            .AsNoTracking()
            .Where(c => c.ResultId == resultId && c.TenantId == tenantId && c.CreatedByUserId == userId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatConversation>> GetConversationsByResultForTenantAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.ChatConversations
            .AsNoTracking()
            .Where(c => c.ResultId == resultId && c.TenantId == tenantId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChatConversation?> GetConversationByIdAsync(
        Guid conversationId, string tenantId, Guid userId, CancellationToken ct)
    {
        return await _dbContext.ChatConversations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.ConversationId == conversationId && c.TenantId == tenantId && c.CreatedByUserId == userId,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChatConversation?> GetConversationByIdForTenantAsync(
        Guid conversationId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.ChatConversations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.ConversationId == conversationId && c.TenantId == tenantId,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RenameConversationAsync(
        Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct)
    {
        var conversation = await _dbContext.ChatConversations
            .FirstOrDefaultAsync(
                c => c.ConversationId == conversationId && c.TenantId == tenantId && c.CreatedByUserId == userId,
                ct)
            .ConfigureAwait(false);

        if (conversation is null)
            return false;

        conversation.Title = newTitle;
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Renamed conversation {ConversationId} tenant {TenantId}",
            conversationId, tenantId);

        return true;
    }

    /// <inheritdoc />
    public async Task<ChatConversation> CreateConversationAsync(
        ChatConversation conversation, CancellationToken ct)
    {
        _dbContext.ChatConversations.Add(conversation);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created chat conversation {ConversationId} for result {ResultId} tenant {TenantId}",
            conversation.ConversationId, conversation.ResultId, conversation.TenantId);

        return conversation;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        Guid conversationId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.TenantId == tenantId)
            .OrderBy(m => m.ChatMessageId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChatMessage?> GetMessageByIdAsync(
        long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.ChatMessageId == chatMessageId
                     && m.ConversationId == conversationId
                     && m.TenantId == tenantId,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct)
    {
        _dbContext.ChatMessages.Add(message);

        // Bump parent conversation counters in the same SaveChanges call
        var conversation = await _dbContext.ChatConversations
            .FirstAsync(c => c.ConversationId == message.ConversationId, ct)
            .ConfigureAwait(false);

        conversation.MessageCount++;
        conversation.LastMessageAt = DateTime.UtcNow;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Added {Role} message {ChatMessageId} to conversation {ConversationId} tenant {TenantId}",
            message.Role, message.ChatMessageId, message.ConversationId, message.TenantId);

        return message;
    }
}
