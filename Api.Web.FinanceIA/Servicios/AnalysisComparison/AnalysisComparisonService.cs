// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisComparison;

/// <summary>
/// Default <see cref="IAnalysisComparisonService"/>: reads the two analyses and hands them to
/// <see cref="AnalysisComparisonBuilder"/>. Logs counts and keys only — never a figure, never a name.
/// </summary>
/// <remarks>
/// Company (Balance) and persona humana analyses; any other kind answers <c>unsupported_kind</c>. A company
/// analysis needs a score; a persona humana analysis needs its stored evaluation (the solicitante row).
/// The previous analysis is resolved at read time — <c>Analyses.ComparativeAnalysisId</c> is neither read
/// nor written. Distinct from the re-score "Qué cambió", which compares a re-score with its own earlier run.
/// </remarks>
public sealed class AnalysisComparisonService : IAnalysisComparisonService
{
    private readonly IAnalysisComparisonRepository _repository;
    private readonly ILogger<AnalysisComparisonService> _logger;

    public AnalysisComparisonService(
        IAnalysisComparisonRepository repository,
        ILogger<AnalysisComparisonService> logger)
    {
        _repository = repository;
        _logger     = logger;
    }

    /// <inheritdoc />
    public async Task<AnalysisComparisonResponse?> CompareWithPreviousAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var current = await _repository
            .GetSnapshotByResultIdAsync(resultId, tenantId, ct)
            .ConfigureAwait(false);

        if (current is null)
            return null;

        AnalysisComparisonSnapshot? previous;
        AnalysisComparisonBuilder.Outcome outcome;

        if (string.Equals(current.AnalysisKind, AnalysisKinds.Balance, StringComparison.Ordinal))
        {
            if (current.AiSuggestedScore is null)
                return Unavailable(AnalysisComparisonCodes.Reasons.NoResult, resultId, tenantId);

            previous = await PreviousAsync(current, tenantId, ct).ConfigureAwait(false);
            if (previous is null)
                return Unavailable(AnalysisComparisonCodes.Reasons.NoPrevious, resultId, tenantId);

            outcome = AnalysisComparisonBuilder.Build(current, previous);
        }
        else if (string.Equals(current.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
        {
            current.Person = await _repository
                .GetPersonFactsAsync(current.AnalysisId, tenantId, ct)
                .ConfigureAwait(false);
            if (current.Person is null)
                return Unavailable(AnalysisComparisonCodes.Reasons.NoResult, resultId, tenantId);

            previous = await PreviousAsync(current, tenantId, ct).ConfigureAwait(false);
            if (previous is null)
                return Unavailable(AnalysisComparisonCodes.Reasons.NoPrevious, resultId, tenantId);

            previous.Person = await _repository
                .GetPersonFactsAsync(previous.AnalysisId, tenantId, ct)
                .ConfigureAwait(false);

            // The lookup only returns an analysis with a solicitante row; a row deleted in between reads as none.
            if (previous.Person is null)
                return Unavailable(AnalysisComparisonCodes.Reasons.NoPrevious, resultId, tenantId);

            outcome = AnalysisComparisonBuilder.BuildPersona(current, previous);
        }
        else
        {
            return Unavailable(AnalysisComparisonCodes.Reasons.UnsupportedKind, resultId, tenantId);
        }

        foreach (var key in outcome.UnreadableKeys)
        {
            _logger.LogWarning(
                "Previous-analysis comparison: {Key} could not be read and was omitted. Tenant {TenantId} result {ResultId} previous {PreviousResultId}",
                key, tenantId, resultId, previous.ResultId);
        }

        var groups = outcome.Response.Groups ?? [];
        _logger.LogInformation(
            "Previous-analysis comparison built. Tenant {TenantId} result {ResultId} previous {PreviousResultId}: " +
            "{GroupCount} groups, {RowCount} rows, {NoteCount} notes, {UnreadableCount} unreadable",
            tenantId, resultId, previous.ResultId,
            groups.Count, groups.Sum(g => g.Rows.Count), outcome.Response.Notes?.Count ?? 0, outcome.UnreadableKeys.Count);

        return outcome.Response;
    }

    private Task<AnalysisComparisonSnapshot?> PreviousAsync(
        AnalysisComparisonSnapshot current, string tenantId, CancellationToken ct)
        => _repository.GetPreviousSnapshotAsync(
            tenantId, current.EntityId, current.AnalysisKind, current.AnalysisId, current.CreatedAt, ct);

    private AnalysisComparisonResponse Unavailable(string reason, Guid resultId, string tenantId)
    {
        _logger.LogInformation(
            "Previous-analysis comparison not available ({Reason}). Tenant {TenantId} result {ResultId}",
            reason, tenantId, resultId);

        return AnalysisComparisonResponse.Unavailable(reason);
    }
}
