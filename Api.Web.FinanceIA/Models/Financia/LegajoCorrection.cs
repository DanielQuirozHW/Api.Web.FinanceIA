// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[LegajoCorrections] (DDL 070): one analyst correction to one
/// field of a <see cref="LegajoExtraction"/>. APPEND-ONLY — rows are never updated or deleted; the
/// effective value of a field is the run's value overlaid with its corrections in order.
///
/// <para>
/// Written by the extraction review (POST /api/documents/{id}/extraction/corrections, Phase 2b); read
/// wherever a run is evaluated on its effective values, including the legajo reconciliation (Phase 3b).
/// </para>
/// </summary>
public sealed class LegajoCorrection
{
    /// <summary>Primary key. Supplied by the application.</summary>
    public Guid CorrectionId { get; set; }

    /// <summary>Tenant that owns the correction. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The run whose field is corrected.</summary>
    public Guid ExtractionId { get; set; }

    /// <summary>JSON path of the field within the run's ResultJson.</summary>
    public string FieldPath { get; set; } = string.Empty;

    /// <summary>The value before the correction, as JSON, or null.</summary>
    public string? OriginalValueJson { get; set; }

    /// <summary>The corrected value, as JSON, or null (a field cleared).</summary>
    public string? CorrectedValueJson { get; set; }

    /// <summary>Why — at least five non-blank characters (CK_LegajoCorrections_Reason).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>UserId of the analyst.</summary>
    public Guid CorrectedBy { get; set; }

    /// <summary>UTC. Set explicitly by the application.</summary>
    public DateTime CorrectedAt { get; set; }
}
