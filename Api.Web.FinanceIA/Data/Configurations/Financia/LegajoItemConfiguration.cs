// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="LegajoItem"/>.
/// Maps to [dbo].[LegajoItems], created by docs/database/071_legajo_items_and_incomes.sql (InvestmentClass: 080).
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class LegajoItemConfiguration : IEntityTypeConfiguration<LegajoItem>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegajoItem> builder)
    {
        builder.ToTable("LegajoItems", "dbo");

        builder.HasKey(x => x.ItemId);

        builder.Property(x => x.ItemId)
            .HasColumnName("ItemId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId")
            .IsRequired();

        builder.Property(x => x.ItemKey)
            .HasColumnName("ItemKey")
            .HasMaxLength(300)
            .IsRequired();

        builder.Property(x => x.Side)
            .HasColumnName("Side")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(x => x.Kind)
            .HasColumnName("Kind")
            .HasMaxLength(20)
            .IsRequired();

        // DDL 080. CK_LegajoItems_InvestmentClass (one of LegajoInvestmentClasses.Known, only on an Inversion row)
        // is not modelled: LegajoInvestmentClassMappingTests pins it to the constants.
        builder.Property(x => x.InvestmentClass)
            .HasColumnName("InvestmentClass")
            .HasMaxLength(30);

        builder.Property(x => x.MatchKeyKind)
            .HasColumnName("MatchKeyKind")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(x => x.MatchKey)
            .HasColumnName("MatchKey")
            .HasMaxLength(60);

        builder.Property(x => x.Description)
            .HasColumnName("Description")
            .HasMaxLength(200);

        builder.Property(x => x.OwnershipPct)
            .HasColumnName("OwnershipPct")
            .HasColumnType("decimal(9,4)");

        builder.Property(x => x.MarketValue)
            .HasColumnName("MarketValue")
            .HasColumnType("decimal(19,2)");

        builder.Property(x => x.MarketValueDocumentId)
            .HasColumnName("MarketValueDocumentId");

        builder.Property(x => x.FiscalValue)
            .HasColumnName("FiscalValue")
            .HasColumnType("decimal(19,2)");

        builder.Property(x => x.FiscalValueDocumentId)
            .HasColumnName("FiscalValueDocumentId");

        builder.Property(x => x.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(10);

        builder.Property(x => x.IsActivityAsset)
            .HasColumnName("IsActivityAsset")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.IsHousing)
            .HasColumnName("IsHousing")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.LinkedCuit)
            .HasColumnName("LinkedCuit")
            .HasColumnType("nvarchar(13)");

        builder.Property(x => x.ReconciliationStatus)
            .HasColumnName("ReconciliationStatus")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.ConflictReasons)
            .HasColumnName("ConflictReasons")
            .HasMaxLength(200);

        builder.Property(x => x.EditedFields)
            .HasColumnName("EditedFields")
            .HasMaxLength(100);

        builder.Property(x => x.AnalystNote)
            .HasColumnName("AnalystNote")
            .HasMaxLength(1000);

        builder.Property(x => x.ConfirmedFingerprint)
            .HasColumnName("ConfirmedFingerprint")
            .HasMaxLength(64);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // Set by the application on every write; the optimistic-concurrency token of the row, so a
        // rebuild and an analyst's PATCH racing on the same row never silently overwrite each other.
        builder.Property(x => x.UpdatedAt)
            .HasColumnName("UpdatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(x => x.UpdatedBy)
            .HasColumnName("UpdatedBy");

        builder.HasIndex(x => new { x.TenantId, x.PersonLegajoId })
            .HasDatabaseName("IX_LegajoItems_TenantId_PersonLegajoId");

        // ONE row per rebuild identity in a legajo. The name is load-bearing: a rebuild that loses a race
        // to another one fails on it and retries.
        builder.HasIndex(x => new { x.TenantId, x.PersonLegajoId, x.ItemKey })
            .IsUnique()
            .HasDatabaseName("UX_LegajoItems_Key");

        // FK_LegajoItems_PersonLegajos and the two document FKs exist in the database but are deliberately
        // NOT modelled: an item is always written after its legajo and documents were saved by an EARLIER
        // SaveChanges (the #237 lesson recorded on EntityConfiguration).
    }
}
