// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="Document"/>.
/// Maps to [dbo].[Documents] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents", "dbo");

        builder.HasKey(x => x.DocumentId);

        builder.Property(x => x.DocumentId)
            .HasColumnName("DocumentId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EntityId)
            .HasColumnName("EntityId")
            .IsRequired();

        builder.Property(x => x.UploadedBy)
            .HasColumnName("UploadedBy")
            .IsRequired();

        builder.Property(x => x.OriginalFileName)
            .HasColumnName("OriginalFileName")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.StoredFileName)
            .HasColumnName("StoredFileName")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.StoragePath)
            .HasColumnName("StoragePath")
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(x => x.FileSizeBytes)
            .HasColumnName("FileSizeBytes")
            .IsRequired();

        builder.Property(x => x.PageCount)
            .HasColumnName("PageCount");

        builder.Property(x => x.MimeType)
            .HasColumnName("MimeType")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.FileHashSHA256)
            .HasColumnName("FileHashSHA256")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.DocumentType)
            .HasColumnName("DocumentType")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.FiscalYear)
            .HasColumnName("FiscalYear");

        builder.Property(x => x.PdfType)
            .HasColumnName("PdfType")
            .HasMaxLength(20);

        builder.Property(x => x.ProcessingStatus)
            .HasColumnName("ProcessingStatus")
            .HasMaxLength(30)
            .HasDefaultValue("Pending")
            .IsRequired();

        // Added by DDL 050. NOT NULL with a database default of 0, so a row inserted without it
        // (and every row that predates the script) reads back as "no failed attempts".
        builder.Property(x => x.ExtractionAttempts)
            .HasColumnName("ExtractionAttempts")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(x => x.LastExtractionFailedAt)
            .HasColumnName("LastExtractionFailedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.LastExtractionError)
            .HasColumnName("LastExtractionError")
            .HasMaxLength(200);

        builder.Property(x => x.UploadedAt)
            .HasColumnName("UploadedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasColumnName("ProcessedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.RetentionExpiresAt)
            .HasColumnName("RetentionExpiresAt")
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.IsDeleted)
            .HasColumnName("IsDeleted")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.DeletedAt)
            .HasColumnName("DeletedAt")
            .HasColumnType("datetime2");

        builder.Property(d => d.Source)
               .HasColumnName("Source")
               .HasColumnType("nvarchar(50)")
               .IsRequired(false);

        builder.Property(d => d.SourceRef)
               .HasColumnName("SourceRef")
               .HasColumnType("nvarchar(2048)")
               .IsRequired(false);

        // ── DDL 070: persona humana legajo columns ──────────────────────────────────
        // FK_Documents_PersonLegajos and FK_Documents_SupersedesDocument exist in the database but are
        // deliberately NOT modelled, like FK_Documents_EntityId before them. Every write in Phase 2a
        // (and the planned 2b) inserts a document whose legajo already exists from an EARLIER
        // SaveChanges, so EF never has to order two INSERTs. Modelling this one would also close a
        // cycle with FK_PersonLegajos_ConsentDocument. ⚠ A FUTURE write that inserts a legajo and its
        // document (or a document and the one it supersedes) in the SAME SaveChanges must model that
        // FK first: EF orders the commands of one SaveChanges by modelled FKs only, and otherwise
        // falls back to table-name order — the #237 lesson recorded on EntityConfiguration.
        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId");

        builder.Property(x => x.PeriodEnd)
            .HasColumnName("PeriodEnd")
            .HasColumnType("date");

        builder.Property(x => x.EngagementType)
            .HasColumnName("EngagementType")
            .HasMaxLength(20);

        builder.Property(x => x.SupersedesDocumentId)
            .HasColumnName("SupersedesDocumentId");

        builder.Property(x => x.HolderCuit)
            .HasColumnName("HolderCuit")
            .HasColumnType("nvarchar(13)")
            .HasMaxLength(13);

        builder.Property(x => x.LegajoStatus)
            .HasColumnName("LegajoStatus")
            .HasMaxLength(30);

        builder.Property(x => x.LegajoStatusUpdatedAt)
            .HasColumnName("LegajoStatusUpdatedAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.PageQualityJson)
            .HasColumnName("PageQualityJson")
            .HasColumnType("nvarchar(max)");

        builder.HasIndex(x => x.TenantId)
            .HasDatabaseName("IX_Documents_TenantId");

        builder.HasIndex(new[] { "TenantId", "EntityId" })
            .HasDatabaseName("IX_Documents_TenantId_EntityId");

        // DDL 070. Serves "every document of this legajo". Filtered, because every company document
        // holds NULL.
        builder.HasIndex(x => new { x.TenantId, x.PersonLegajoId })
            .HasFilter("[PersonLegajoId] IS NOT NULL")
            .HasDatabaseName("IX_Documents_TenantId_PersonLegajoId");

        // DDL 070. The same file at most ONCE per legajo among live rows: the database side of the
        // upload's 409, so a double-click that passes the pre-check still cannot store it twice. The
        // name is load-bearing — LegajoDocumentRepository recognises the violation by it (#237's
        // pattern) and turns it into the same 409 duplicate_document.
        builder.HasIndex(x => new { x.PersonLegajoId, x.FileHashSHA256 })
            .IsUnique()
            .HasFilter("[PersonLegajoId] IS NOT NULL AND [IsDeleted] = 0")
            .HasDatabaseName("UX_Documents_Legajo_Hash");
    }
}
