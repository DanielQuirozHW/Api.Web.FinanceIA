// Project : Api.Web.FinanceIA
// Created : 2026-07-17

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Note"/>.
/// Maps to [dbo].[Notes] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("Notes", "dbo");

        builder.HasKey(x => x.NoteId);

        builder.Property(x => x.NoteId)
            .HasColumnName("NoteId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId")
            .IsRequired();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId");

        builder.Property(x => x.Content)
            .HasColumnName("Content")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.AuthorUserId)
            .HasColumnName("AuthorUserId")
            .IsRequired();

        builder.Property(x => x.AuthorName)
            .HasColumnName("AuthorName")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasDefaultValueSql("(0)")
            .IsRequired();

        builder.Property(x => x.DeletedAt)
            .HasColumnName("DeletedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.DeletedBy)
            .HasColumnName("DeletedBy");

        builder.HasIndex(x => new { x.TenantId, x.EntityId, x.NoteId })
            .HasDatabaseName("IX_Notes_TenantId_EntityId_NoteId");
    }
}
