// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[AnalysisRescores] (DDL 073). One row per re-score of a balance
/// analysis — what the score was, what it became, and the previous value of every column the re-score
/// overwrote on [dbo].[AnalysisResults] — or per re-evaluation of a persona humana one, whose score and
/// level stay null and whose <see cref="PreviousValuesJson"/> also holds the overwritten
/// [dbo].[AnalysisParticipants] columns (see <c>PersonRescorePreviousValues</c>).
/// </summary>
/// <remarks>
/// <para>
/// APPEND-ONLY, like the audit tables: written once, in the same transaction as the result update it
/// records, and never updated or deleted. <see cref="PreviousValuesJson"/> is what makes a re-score
/// reversible by hand — it holds the exact strings that were on the row before.
/// </para>
/// <para>
/// Keyed on <see cref="AnalysisId"/> for the history read (the index leads with TenantId, AnalysisId);
/// <see cref="ResultId"/> is carried beside it because every route addresses the result.
/// </para>
/// </remarks>
public sealed class AnalysisRescore
{
    /// <summary>Primary key. Supplied by the application.</summary>
    public Guid RescoreId { get; set; }

    /// <summary>Firebase tenant slug (NVARCHAR(100)) — the row-level tenancy column.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>[dbo].[Analyses].AnalysisId (FK).</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>[dbo].[AnalysisResults].ResultId of the re-scored result.</summary>
    public Guid ResultId { get; set; }

    /// <summary>UTC instant of the re-score; equals AnalysisResults.RescoredAt when it is the latest.</summary>
    public DateTime RescoredAt { get; set; }

    /// <summary>Users.UserId of the analyst who ran it.</summary>
    public Guid RescoredBy { get; set; }

    /// <summary>AiSuggestedScore before the re-score (DECIMAL(5,2)); null when the result had none.</summary>
    public decimal? PreviousScore { get; set; }

    /// <summary>AiSuggestedScore after the re-score.</summary>
    public decimal? NewScore { get; set; }

    /// <summary>AiSuggestedLevel before the re-score.</summary>
    public string? PreviousLevel { get; set; }

    /// <summary>AiSuggestedLevel after the re-score.</summary>
    public string? NewLevel { get; set; }

    /// <summary>EnrichmentStatus before the re-score (Complete | NotApplicable | Degraded).</summary>
    public string PreviousEnrichmentStatus { get; set; } = string.Empty;

    /// <summary>EnrichmentStatus after the re-score.</summary>
    public string NewEnrichmentStatus { get; set; } = string.Empty;

    /// <summary>
    /// JSON object with the previous value of every AnalysisResults column the re-score overwrote,
    /// each as the raw string that was stored (see <c>RescorePreviousValues</c>). Never read by any
    /// endpoint: it is the record that makes the change reversible, not a payload.
    /// </summary>
    public string PreviousValuesJson { get; set; } = string.Empty;

    /// <summary>
    /// JSON array of the sources the new score was computed from:
    /// <c>[{ "source": "BCRA", "asOf": "…", "live": true }, …]</c>. Carries dates only — no payload.
    /// </summary>
    public string SourcesJson { get; set; } = string.Empty;
}
