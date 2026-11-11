// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="ChatConversation"/>.
/// Maps to [dbo].[ChatConversations] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class ChatConversationConfiguration : IEntityTypeConfiguration<ChatConversation>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatConversation> builder)
    {
        builder.ToTable("ChatConversations", "dbo");

        builder.HasKey(x => x.ConversationId);

        builder.Property(x => x.ConversationId)
            .HasColumnName("ConversationId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.ResultId)
            .HasColumnName("ResultId")
            .IsRequired();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.MessageCount)
            .HasColumnName("MessageCount")
            .HasDefaultValueSql("0")
            .IsRequired();

        builder.Property(x => x.LastMessageAt)
            .HasColumnName("LastMessageAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.Title)
            .HasColumnName("Title")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.CreatedByUserId)
            .HasColumnName("CreatedByUserId")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => new { x.ResultId, x.CreatedByUserId, x.LastMessageAt })
            .HasDatabaseName("IX_ChatConversations_Result_User_LastMsg");
    }
}
