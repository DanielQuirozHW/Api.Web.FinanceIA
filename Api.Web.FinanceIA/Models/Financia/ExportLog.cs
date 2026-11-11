// Project : Api.Web.FinanceIA
// Created : 2026-05-09

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[ExportLogs].
/// Append-only audit log of every report exported from the system.
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class ExportLog
{
    /// <summary>Primary key (auto-increment).</summary>
    public long ExportLogId { get; set; }

    /// <summary>The AnalysisResult that was exported.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>Tenant that owns the result. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>User who triggered the export.</summary>
    public Guid ExportedBy { get; set; }

    /// <summary>Export format. Allowed values: PDF | Excel | CSV.</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>JSON array of section keys included in the export (e.g. ["balance","ratios"]).</summary>
    public string SectionsIncluded { get; set; } = string.Empty;

    /// <summary>SHA-256 hex hash of the generated file bytes.</summary>
    public string FileHashSha256 { get; set; } = string.Empty;

    /// <summary>Client IP address. Null when not determinable.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Template key used to generate the export (e.g. "ejecutivo", "completo"). Null when sections were specified explicitly.</summary>
    public string? Template { get; set; }

    /// <summary>UTC timestamp when the export was generated.</summary>
    public DateTime ExportedAt { get; set; }
}
