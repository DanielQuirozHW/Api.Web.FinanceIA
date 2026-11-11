// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The two blobs the ratios panel renders, and nothing else. A REPOSITORY projection, not a wire
/// contract: it is the target of the <c>.Select(...)</c> behind
/// <c>AnalysisResultRepository.GetRatiosByIdAsync</c>, so that query names exactly three columns —
/// the key and two <c>nvarchar(max)</c> blobs. None of the other twelve blobs on the row is read.
/// The response DTO clients see is <see cref="AnalysisRatiosResponse"/>.
///
/// <para>
/// This applies to the PyME (entity) detail page what #143 applied to the analysis detail. That page
/// shows a "Ratios calculados por FinancIA" panel for the entity's LATEST analysis and, to fill it,
/// called GET /api/analyses/{id} — the whole detail — purely to read <c>RatiosJson</c> and
/// <c>ScoreBreakdownJson</c>. Even after #143 the detail still named nine blobs, ~73KB on a
/// representative row, so a small table was paying for BalanceJson, BcraDataJson, RiskAlerts,
/// CatalogueAlerts, Anomalies, ExecutiveSummary and ScoringNarrative it never renders. On warm
/// serverless Azure SQL that read is also the tail the PyME page's credit-lines and notes queue
/// behind under contention. (The BCRA split has since taken BcraDataJson out of the detail read
/// too, but that only narrows the gap — the detail still reads seven blobs this panel does not
/// render, so the argument for a separate query is unchanged.)
/// </para>
///
/// <para>
/// Two columns rather than one query per blob: the panel renders the ratio table and the per-ratio
/// score badges together, so this is ONE query rather than two round-trips for the same panel. The
/// same argument that made <see cref="AnalysisContextProjection"/> a single four-blob read.
/// </para>
///
/// <para>
/// A null return from the repository means NO SUCH RESULT in this tenant — a 404. A non-null
/// instance whose two blobs are null means the analysis exists but was never scored — a 200 carrying
/// nulls, which is the normal state for analyses that predate the scoring engine. Projecting into a
/// class rather than a value tuple is what keeps those two cases distinguishable:
/// <c>SingleOrDefaultAsync</c> returns null only when no row matched, and a matched row always
/// materialises an instance.
/// </para>
/// </summary>
public sealed class AnalysisRatiosProjection
{
    /// <summary>Primary key of the result the ratios belong to.</summary>
    public Guid ResultId { get; set; }

    /// <summary>
    /// Raw stored ratios JSON, echoed to the client verbatim and never parsed here — the Portal
    /// parses it itself, exactly as it does off the detail payload. Null when the analysis produced
    /// no ratios.
    /// </summary>
    public string? RatiosJson { get; set; }

    /// <summary>
    /// Raw stored per-component and per-ratio score breakdown JSON. Null for analyses that predate
    /// the scoring engine.
    /// </summary>
    public string? ScoreBreakdownJson { get; set; }
}
