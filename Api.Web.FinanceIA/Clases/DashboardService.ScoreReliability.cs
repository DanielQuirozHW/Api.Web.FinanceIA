// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.ScoreReliability;

namespace Api.Web.FinanceIA.Clases;

public sealed partial class DashboardService
{
    /// <summary>
    /// The score-reliability verdict (score credibility 1b-2b) of each row a section SHOWS, keyed by
    /// ResultId — through <see cref="ScoreReliabilityAdapter"/>, the adapter GET /api/analyses/{id} and the
    /// list use, from the same columns, so the dashboard never tells an analyst something the detail
    /// contradicts. One page-sized query, after the section's cap; persona humana rows (verdict null by rule)
    /// are not read. Never throws on a bad breakdown: it becomes facts_unavailable, logged once per section.
    /// <para>
    /// Fault-isolated from the section it serves: a failure of the lookup itself costs the verdicts, never
    /// the section — see <see cref="ReadSourcesAsync"/>.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, ScoreReliabilityResponse?>> ScoreReliabilityAsync(
        string tenantId, IReadOnlyList<DashboardAnalysisRow> rows, string section, CancellationToken ct)
    {
        var verdicts = new Dictionary<Guid, ScoreReliabilityResponse?>();
        if (rows.Count == 0)
            return verdicts;

        var resultIds = rows
            .Where(r => !string.Equals(r.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            .Select(r => r.ResultId)
            .Distinct()
            .ToList();

        var sources = await ReadSourcesAsync(tenantId, resultIds, section, ct).ConfigureAwait(false);

        var unreadable = new List<Guid>();
        foreach (var row in rows)
        {
            sources.TryGetValue(row.ResultId, out var source);

            var verdict = ScoreReliabilityAdapter.Evaluate(
                new ScoreReliabilityRow(
                    AiSuggestedScore: row.AiSuggestedScore,
                    EnrichmentStatus: row.EnrichmentStatus,
                    AnalysisKind:     row.AnalysisKind,
                    FiscalYear:       source?.FiscalYear,
                    GeneratedAt:      row.GeneratedAt,
                    RescoredAt:       source?.RescoredAt),
                source?.ScoreBreakdownJson);

            verdicts[row.ResultId] = verdict.Reliability;
            if (verdict.BreakdownUnreadable)
                unreadable.Add(row.ResultId);
        }

        // Never per row: one line per section, and only when a breakdown in it could not be read.
        if (unreadable.Count > 0)
        {
            _logger.LogWarning(
                "{Count} score breakdown(s) in dashboard section {Section} could not be read for tenant {TenantId}; their score reliability is reported as facts_unavailable. ResultIds: {ResultIds}",
                unreadable.Count, section, tenantId, string.Join(", ", unreadable.Take(10)));
        }

        return verdicts;
    }

    /// <summary>
    /// The verdict's columns for the page, or an EMPTY map when the lookup fails. A reliability problem must
    /// never hide the section it decorates — above all the confirmation queue — so the failure is logged once
    /// and every non-persona row is evaluated with no breakdown: facts_unavailable (with_caveats), never
    /// complete. The section renders and is not listed in degradedSections. Cancellation still propagates,
    /// exactly as RunSectionAsync treats it.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, DashboardScoreReliabilitySource>> ReadSourcesAsync(
        string tenantId, IReadOnlyCollection<Guid> resultIds, string section, CancellationToken ct)
    {
        try
        {
            return await _dashboardRepository
                .GetScoreReliabilitySourcesAsync(tenantId, resultIds, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Score reliability lookup failed in dashboard section {Section} for tenant {TenantId}; the section is returned with its rows read as facts_unavailable.",
                section, tenantId);

            return new Dictionary<Guid, DashboardScoreReliabilitySource>();
        }
    }
}
