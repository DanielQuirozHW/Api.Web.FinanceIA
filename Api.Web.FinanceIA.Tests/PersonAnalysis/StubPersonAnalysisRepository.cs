// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// An in-memory <see cref="IPersonAnalysisRepository"/>. By default every analysis reads as a balance one
/// (the kind of every analysis before DDL 072), so the existing AnalysisController suites see the
/// behaviour they always saw; a test adds kinds and participants to exercise the persona branches.
/// </summary>
public sealed class StubPersonAnalysisRepository : IPersonAnalysisRepository
{
    /// <summary>Kinds by AnalysisId. Missing = <see cref="AnalysisKinds.Balance"/>.</summary>
    public Dictionary<Guid, string> Kinds { get; } = [];

    /// <summary>Stored participants.</summary>
    public List<AnalysisParticipant> Participants { get; } = [];

    /// <summary>Stored analyses (CreateAsync).</summary>
    public List<Analysis> Analyses { get; } = [];

    /// <summary>Stored results (CreateAsync).</summary>
    public List<AnalysisResult> Results { get; } = [];

    /// <summary>pf_ rows returned by GetEvaluationConfigAsync.</summary>
    public Dictionary<string, decimal> Config { get; } = [];

    /// <summary>The fiscal year GetKindAndFiscalYearAsync answers for an analysis not in <see cref="FiscalYears"/>.</summary>
    public const int DefaultFiscalYear = 2025;

    /// <summary>Analyses.FiscalYear by AnalysisId, answered by GetKindAndFiscalYearAsync.</summary>
    public Dictionary<Guid, int> FiscalYears { get; } = [];

    /// <summary>The entity GetKindAndFiscalYearAsync answers for an analysis not in <see cref="EntityIds"/>.</summary>
    public Guid DefaultEntityId { get; set; } = Guid.NewGuid();

    /// <summary>Analyses.EntityId by AnalysisId, answered by GetKindAndFiscalYearAsync.</summary>
    public Dictionary<Guid, Guid> EntityIds { get; } = [];

    /// <summary>Analyses GetKindAndFiscalYearAsync answers null for — no such row in this tenant.</summary>
    public HashSet<Guid> MissingAnalyses { get; } = [];

    /// <summary>Calls to GetKindAndFiscalYearAsync.</summary>
    public int KindReads { get; private set; }

    /// <summary>Calls to SetConfirmationChecksAsync, in order.</summary>
    public List<(Guid AnalysisId, string Json)> ConfirmationChecksWrites { get; } = [];

    /// <summary>Fiadores.Denominacion by FiadorId, returned by GetFiadorIdentitiesAsync.</summary>
    public Dictionary<Guid, string?> FiadorNames { get; } = [];

    /// <summary>Fiadores.CUIT by FiadorId, returned by GetFiadorIdentitiesAsync beside the name.</summary>
    public Dictionary<Guid, string?> FiadorCuits { get; } = [];

    /// <inheritdoc />
    public Task CreateAsync(
        Analysis analysis, AnalysisResult result, AnalysisParticipant participant,
        IReadOnlyList<AnalysisParticipant> fiadores, CancellationToken ct)
    {
        Analyses.Add(analysis);
        Results.Add(result);
        Participants.Add(participant);
        Participants.AddRange(fiadores);
        Kinds[analysis.AnalysisId] = analysis.AnalysisKind;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AnalysisParticipant>> GetParticipantsAsync(Guid analysisId, string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AnalysisParticipant>>(
            Participants.Where(p => p.AnalysisId == analysisId && p.TenantId == tenantId).ToList());

    /// <inheritdoc />
    public Task<bool> HasOpenPersonAnalysisAsync(Guid entityId, string tenantId, CancellationToken ct)
        => Task.FromResult(Analyses.Any(a => a.EntityId == entityId && a.TenantId == tenantId
            && a.AnalysisKind == AnalysisKinds.PersonaHumana
            && (a.Status == AnalysisStatuses.PendingReview || a.Status == AnalysisStatuses.PreApproved)));

    /// <inheritdoc />
    public Task<bool> SetConfirmationChecksAsync(Guid analysisId, string tenantId, string checksJson, CancellationToken ct)
    {
        ConfirmationChecksWrites.Add((analysisId, checksJson));
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<AnalysisKindAndFiscalYear?> GetKindAndFiscalYearAsync(Guid analysisId, string tenantId, CancellationToken ct)
    {
        KindReads++;
        if (MissingAnalyses.Contains(analysisId))
            return Task.FromResult<AnalysisKindAndFiscalYear?>(null);

        return Task.FromResult<AnalysisKindAndFiscalYear?>(new AnalysisKindAndFiscalYear(
            Kinds.TryGetValue(analysisId, out var kind) ? kind : AnalysisKinds.Balance,
            FiscalYears.TryGetValue(analysisId, out var year) ? year : DefaultFiscalYear,
            EntityIds.TryGetValue(analysisId, out var entityId) ? entityId : DefaultEntityId));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, FiadorIdentity>> GetFiadorIdentitiesAsync(
        IReadOnlyCollection<Guid> fiadorIds, string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<Guid, FiadorIdentity>>(
            fiadorIds
                .Where(id => FiadorNames.ContainsKey(id) || FiadorCuits.ContainsKey(id))
                .Distinct()
                .ToDictionary(id => id, id => new FiadorIdentity(FiadorNames.GetValueOrDefault(id), FiadorCuits.GetValueOrDefault(id))));

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, decimal>> GetEvaluationConfigAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>(Config));
}
