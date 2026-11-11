// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="LegajoIncome"/>.
/// Maps to [dbo].[LegajoIncomes], created by docs/database/071_legajo_items_and_incomes.sql.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class LegajoIncomeConfiguration : IEntityTypeConfiguration<LegajoIncome>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LegajoIncome> builder)
    {
        builder.ToTable("LegajoIncomes", "dbo");

        builder.HasKey(x => x.IncomeId);

        builder.Property(x => x.IncomeId)
            .HasColumnName("IncomeId")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PersonLegajoId)
            .HasColumnName("PersonLegajoId")
            .IsRequired();

        builder.Property(x => x.Period)
            .HasColumnName("Period")
            .HasMaxLength(7);

        builder.Property(x => x.PeriodFrom)
            .HasColumnName("PeriodFrom")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.PeriodTo)
            .HasColumnName("PeriodTo")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.Source)
            .HasColumnName("Source")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Concept)
            .HasColumnName("Concept")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasColumnName("Amount")
            .HasColumnType("decimal(19,2)")
            .IsRequired();

        builder.Property(x => x.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(10)
            .HasDefaultValue("ARS")
            .IsRequired();

        builder.Property(x => x.DocumentId)
            .HasColumnName("DocumentId")
            .IsRequired();

        builder.Property(x => x.ExtractionId)
            .HasColumnName("ExtractionId")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("CreatedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.PersonLegajoId })
            .HasDatabaseName("IX_LegajoIncomes_TenantId_PersonLegajoId");

        // ONE row per source, concept and range in a legajo. A rebuild diffs on this key, updating a row in
        // place rather than deleting and re-inserting it.
        builder.HasIndex(x => new { x.TenantId, x.PersonLegajoId, x.Source, x.Concept, x.PeriodFrom, x.PeriodTo })
            .IsUnique()
            .HasDatabaseName("UX_LegajoIncomes_Key");

        // FK_LegajoIncomes_PersonLegajos, FK_LegajoIncomes_Documents and FK_LegajoIncomes_LegajoExtractions
        // exist in the database but are deliberately NOT modelled: an income is always written after its
        // legajo, document and run were saved by an EARLIER SaveChanges.
    }
}
