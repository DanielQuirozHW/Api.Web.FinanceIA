// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations.Financia;

/// <summary>
/// Fluent API configuration for <see cref="ApiCallLog"/>.
/// Maps to [dbo].[ApiCallLogs] in the per-tenant business database.
/// Database First — schema changes are applied via DDL scripts, not migrations.
/// </summary>
public sealed class ApiCallLogConfiguration : IEntityTypeConfiguration<ApiCallLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApiCallLog> builder)
    {
        builder.ToTable("ApiCallLogs", "dbo");

        builder.HasKey(x => x.ApiCallLogId);

        builder.Property(x => x.ApiCallLogId)
            .HasColumnName("ApiCallLogId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TenantId)
            .HasColumnName("TenantId")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Service)
            .HasColumnName("Service")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.Endpoint)
            .HasColumnName("Endpoint")
            .HasMaxLength(500)
            .IsRequired();

        builder.Property(x => x.RequestBodyHash)
            .HasColumnName("RequestBodyHash")
            .HasMaxLength(64);

        builder.Property(x => x.ResponseStatus)
            .HasColumnName("ResponseStatus");

        builder.Property(x => x.ResponseBodyHash)
            .HasColumnName("ResponseBodyHash")
            .HasMaxLength(64);

        builder.Property(x => x.DurationMs)
            .HasColumnName("DurationMs");

        builder.Property(x => x.InputTokens)
            .HasColumnName("InputTokens");

        builder.Property(x => x.OutputTokens)
            .HasColumnName("OutputTokens");

        builder.Property(x => x.EstimatedCostUsd)
            .HasColumnName("EstimatedCostUsd")
            .HasColumnType("decimal(10,6)");

        builder.Property(x => x.IsSuccess)
            .HasColumnName("IsSuccess")
            .IsRequired();

        builder.Property(x => x.ErrorMessage)
            .HasColumnName("ErrorMessage")
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.CalledAt)
            .HasColumnName("CalledAt")
            .HasColumnType("datetime2")
            .HasDefaultValueSql("GETUTCDATE()")
            .IsRequired();

        builder.Property(x => x.Operation)
            .HasColumnName("Operation")
            .HasMaxLength(30);

        builder.Property(x => x.Model)
            .HasColumnName("Model")
            .HasMaxLength(100);

        builder.Property(x => x.UserId)
            .HasColumnName("UserId");

        builder.Property(x => x.AnalysisId)
            .HasColumnName("AnalysisId");

        builder.Property(x => x.ChatMessageId)
            .HasColumnName("ChatMessageId");

        // DDL 070. No FK, like AnalysisId.
        builder.Property(x => x.DocumentId)
            .HasColumnName("DocumentId");

        builder.HasIndex(x => new { x.TenantId, x.CalledAt })
            .HasDatabaseName("IX_ApiCallLogs_TenantId_CalledAt");
    }
}
