// Project : Api.Web.FinanceIA
// Created : 2026-04-15
// Modified: 2026-04-15

using Api.Web.FinanceIA.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Web.FinanceIA.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="ErrorLog"/>.
/// Maps to [dbo].[ErrorLogs] — persistent error log for operational review.
/// Used alongside Application Insights telemetry.
/// </summary>
public sealed class ErrorLogConfiguration
    : IEntityTypeConfiguration<ErrorLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ErrorLog> builder)
    {
        builder.ToTable("ErrorLogs", "dbo");

        builder.HasKey(x => x.ErrorId);

        builder.Property(x => x.ErrorId)
            .HasColumnName("ErrorId")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.Timestamp)
            .HasColumnName("Timestamp")
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.Level)
            .HasColumnName("Level")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Message)
            .HasColumnName("Message")
            .IsRequired();

        builder.Property(x => x.ExceptionDetails)
            .HasColumnName("ExceptionDetails");

        builder.Property(x => x.Source)
            .HasColumnName("Source")
            .HasMaxLength(255);

        builder.Property(x => x.Url)
            .HasColumnName("Url")
            .HasMaxLength(2048);

        builder.Property(x => x.UserId)
            .HasColumnName("UserId")
            .HasMaxLength(255);

        builder.Property(x => x.IpAddress)
            .HasColumnName("IPAddress")
            .HasMaxLength(45);

        builder.Property(x => x.StackTrace)
            .HasColumnName("StackTrace");

        builder.Property(x => x.TraceId)
            .HasColumnName("TraceId")
            .HasMaxLength(100);

        // Non-clustered index on TraceId for correlation lookups,
        // filtered to rows where TraceId is not null.
        builder.HasIndex(x => x.TraceId)
            .HasDatabaseName("IX_ErrorLogs_TraceId")
            .HasFilter("[TraceId] IS NOT NULL");
    }
}
