// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="AnalysisResult"/>.
/// Maps to [dbo].[AnalysisResults] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class AnalysisResultConfiguration : IEntityTypeConfiguration<AnalysisResult>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AnalysisResult> builder)
    {
        builder.ToTable("AnalysisResults", "dbo");

        builder.HasKey(x => x.ResultId);

        builder.Property(x => x.ResultId)
            .HasColumnName("ResultId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId")
            .IsRequired();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.BalanceJson)
            .HasColumnName("BalanceJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.RatiosJson)
            .HasColumnName("RatiosJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.BcraDataJson)
            .HasColumnName("BcraDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.MacroDataJson)
            .HasColumnName("MacroDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.AiSuggestedScore)
            .HasColumnName("AiSuggestedScore")
            .HasColumnType("decimal(5,2)");

        builder.Property(x => x.AiSuggestedLevel)
            .HasColumnName("AiSuggestedLevel")
            .HasMaxLength(20);

        builder.Property(x => x.ExecutiveSummary)
            .HasColumnName("ExecutiveSummary")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.ScoringNarrative)
            .HasColumnName("ScoringNarrative")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.RiskAlerts)
            .HasColumnName("RiskAlerts")
            .HasColumnType("nvarchar(max)");

        // Added by DDL 038. Mapping it makes EF emit the column in every SELECT and INSERT for
        // AnalysisResult, so 038 MUST be applied before this code is deployed.
        builder.Property(x => x.CatalogueAlerts)
            .HasColumnName("CatalogueAlerts")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.Anomalies)
            .HasColumnName("Anomalies")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.AiInputHash)
            .HasColumnName("AiInputHash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.AiOutputHash)
            .HasColumnName("AiOutputHash")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.AiInputTokens)
            .HasColumnName("AiInputTokens");

        builder.Property(x => x.AiOutputTokens)
            .HasColumnName("AiOutputTokens");

        builder.Property(x => x.ValidationStatus)
            .HasColumnName("ValidationStatus")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.ValidationNotes)
            .HasColumnName("ValidationNotes")
            .HasColumnType("nvarchar(max)");

        // Added by DDL 039. EnrichmentStatus is NOT NULL, so mapping it makes EF emit it in every
        // SELECT and INSERT for AnalysisResult — 039 MUST be applied before this code is deployed.
        builder.Property(x => x.EnrichmentStatus)
            .HasColumnName("EnrichmentStatus")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.EnrichmentNotes)
            .HasColumnName("EnrichmentNotes")
            .HasMaxLength(500);

        builder.Property(x => x.ScoreBreakdownJson)
            .HasColumnName("ScoreBreakdownJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.LufeDataJson)
            .HasColumnName("LufeDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.FiscalDataJson)
            .HasColumnName("FiscalDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.NosisDataJson)
            .HasColumnName("NosisDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.CasfogDataJson)
            .HasColumnName("CasfogDataJson")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.GeneratedAt)
            .HasColumnName("GeneratedAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        // ── Re-score (DDL 073) ──────────────────────────────────────────────────
        builder.Property(x => x.RescoredAt)
            .HasColumnName("RescoredAt")
            .HasColumnType("datetime2");

        builder.Property(x => x.RescoredBy)
            .HasColumnName("RescoredBy");

        // NOT NULL DEFAULT 0 in the database. The concurrency token is what turns the re-score write into
        // UPDATE … WHERE ResultId = @id AND RescoreCount = @expected. Safe to declare here because nothing
        // else updates an AnalysisResults row: the pipeline and the persona analysis only INSERT, and
        // IAnalysisResultRepository.UpdateAsync has no production caller.
        builder.Property(x => x.RescoreCount)
            .HasColumnName("RescoreCount")
            .IsRequired()
            .IsConcurrencyToken();

        builder.HasIndex(x => x.AnalysisId)
            .IsUnique()
            .HasDatabaseName("UQ_AnalysisResults_AnalysisId");

        // Serves the list page: WHERE TenantId = @t ORDER BY GeneratedAt DESC, ResultId
        // OFFSET/FETCH. Declared here for the same reason IX_Documents_TenantId is declared in
        // DocumentConfiguration — this is Database First, migrations are never run, so the mapping
        // is documentation that keeps the model honest about what the DBA actually created.
        // Created by docs/database/044_analysisresults_add_tenant_generatedat_index.sql; until that
        // script runs the query is correct but sorts instead of seeking.
        builder.HasIndex(x => new { x.TenantId, x.GeneratedAt, x.ResultId })
            .IsDescending(false, true, false)
            .HasDatabaseName("IX_AnalysisResults_TenantId_GeneratedAt");
    }
}
