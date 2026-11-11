// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="LegajoExtraction"/>.
/// Maps to [dbo].[LegajoExtractions], created by docs/database/070_legajo_documents_and_extractions.sql.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class LegajoExtractionConfiguration : IEntityTypeConfiguration<LegajoExtraction>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegajoExtraction> builder)
    {
        builder.ToTable("LegajoExtractions", "dbo");

        builder.HasKey(x => x.ExtractionId);

        // Application-supplied, like PersonLegajoConfiguration: the NEWSEQUENTIALID() default in the
        // DDL serves rows written by hand.
        builder.Property(x => x.ExtractionId)
            .HasColumnName("ExtractionId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId")
            .IsRequired();

        builder.Property(x => x.DocumentId)
            .HasColumnName("DocumentId")
            .IsRequired();

        builder.Property(x => x.Kind)
            .HasColumnName("Kind")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.DocumentType)
            .HasColumnName("DocumentType")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.SchemaVersion)
            .HasColumnName("SchemaVersion")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.PromptVersion)
            .HasColumnName("PromptVersion")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Model)
            .HasColumnName("Model")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ReadMode)
            .HasColumnName("ReadMode")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("Status")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.ResultJson)
            .HasColumnName("ResultJson")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.ChecksJson)
            .HasColumnName("ChecksJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.Confidence)
            .HasColumnName("Confidence")
            .HasColumnType("decimal(5,4)");

        builder.Property(x => x.InputTokens)
            .HasColumnName("InputTokens")
            .IsRequired();

        builder.Property(x => x.OutputTokens)
            .HasColumnName("OutputTokens")
            .IsRequired();

        builder.Property(x => x.CostUsd)
            .HasColumnName("CostUsd")
            .HasColumnType("decimal(12,6)");

        builder.Property(x => x.InputHash)
            .HasColumnName("InputHash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.OutputHash)
            .HasColumnName("OutputHash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.IsCurrent)
            .HasColumnName("IsCurrent")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.CreatedBy)
            .HasColumnName("CreatedBy");

        builder.Property(x => x.ReviewedBy)
            .HasColumnName("ReviewedBy");

        builder.Property(x => x.ReviewedAt)
            .HasColumnName("ReviewedAt")
            .HasColumnType("datetime2");

        // DDL 071.
        builder.Property(x => x.ReviewOverride)
            .HasColumnName("ReviewOverride");

        builder.Property(x => x.ReviewReason)
            .HasColumnName("ReviewReason")
            .HasMaxLength(1000);

        // ONE current run per (document, kind). A new run is inserted and the previous current run is
        // flipped to IsCurrent = 0 in the SAME SaveChanges, so the UPDATE that releases the slot must
        // reach SQL Server before the INSERT that takes it. Modelled so EF knows the index exists; a
        // test pins columns, uniqueness, filter and name.
        //
        // ⚠ What actually orders the two commands, verified against EF Core 9's CommandBatchPreparer:
        // it adds a unique-index dependency only when one of the index's KEY columns changes, and the
        // swap changes the FILTER column (IsCurrent) — so no index edge is created. The order then
        // comes from ModificationCommandComparer, which sorts commands of the same table by
        // EntityState, Modified (3) before Added (4). That rule is deterministic, and
        // LegajoDocumentRepository relies on it; do not split the swap across tables or put a
        // Deleted command beside it without re-checking the order.
        builder.HasIndex(x => new { x.DocumentId, x.Kind })
            .IsUnique()
            .HasFilter("[IsCurrent] = 1")
            .HasDatabaseName("UX_LegajoExtractions_Current");

        builder.HasIndex(x => new { x.TenantId, x.PersonLegajoId })
            .HasDatabaseName("IX_LegajoExtractions_TenantId_PersonLegajoId");

        // FK_LegajoExtractions_PersonLegajos and FK_LegajoExtractions_Documents exist in the database
        // but are deliberately NOT modelled: a run is always inserted after its document and legajo
        // were saved by an EARLIER SaveChanges, so EF never has to order those INSERTs. ⚠ A future
        // write that inserts a document (or a legajo) together with a run over it in the SAME
        // SaveChanges must model that FK first, or EF falls back to table-name order and the
        // "LegajoExtractions" INSERT can precede "PersonLegajos" — the #237 lesson recorded on
        // EntityConfiguration (and on Fiador's).
    }
}
