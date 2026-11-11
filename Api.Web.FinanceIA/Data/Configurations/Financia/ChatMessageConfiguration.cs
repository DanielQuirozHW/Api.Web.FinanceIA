// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="ChatMessage"/>.
/// Maps to [dbo].[ChatMessages] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("ChatMessages", "dbo");

        builder.HasKey(x => x.ChatMessageId);

        builder.Property(x => x.ChatMessageId)
            .HasColumnName("ChatMessageId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.ConversationId)
            .HasColumnName("ConversationId")
            .IsRequired();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Role)
            .HasColumnName("Role")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(x => x.Content)
            .HasColumnName("Content")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.SenderUserId)
            .HasColumnName("SenderUserId");

        builder.Property(x => x.InputTokens)
            .HasColumnName("InputTokens");

        builder.Property(x => x.OutputTokens)
            .HasColumnName("OutputTokens");

        builder.Property(x => x.Model)
            .HasColumnName("Model")
            .HasMaxLength(100);

        // Script 053. The column's DEFAULT (0) is deliberately NOT mapped: AnalysisController always
        // sets this explicitly on the assistant turn, and every other write leaves it false, so the
        // store default only ever applies to the rows the ALTER backfilled.
        builder.Property(x => x.HadEphemeralAttachment)
            .HasColumnName("HadEphemeralAttachment");

        // Script 055. NULLABLE with no default, unlike the flag above: NULL is the honest "this turn
        // carried no attachment", which is almost every turn, and there was never any text to
        // backfill for the rows written before the column existed. Note the two columns sit on
        // DIFFERENT turns — this one on the analyst's, the flag above on the assistant's.
        builder.Property(x => x.AttachmentMarkdown)
            .HasColumnName("AttachmentMarkdown")
            .HasColumnType("nvarchar(max)");

        // Script 056. The label for the column above, on the same USER turn, and NULL under exactly
        // the same condition — AnalysisController derives it from the capped Markdown, so neither can
        // exist without the other. NVARCHAR(255) rather than max: this is a file name, not content.
        builder.Property(x => x.AttachmentFileName)
            .HasColumnName("AttachmentFileName")
            .HasMaxLength(255);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => new { x.ConversationId, x.ChatMessageId })
            .HasDatabaseName("IX_ChatMessages_ConversationId_ChatMessageId");
    }
}
