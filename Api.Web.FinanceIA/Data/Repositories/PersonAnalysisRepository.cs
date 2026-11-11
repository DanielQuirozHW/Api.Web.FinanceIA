// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>EF Core implementation of <see cref="IPersonAnalysisRepository"/> against FinanciaDbContext.</summary>
public sealed class PersonAnalysisRepository : IPersonAnalysisRepository
{
    private static readonly string[] OpenStatuses = [AnalysisStatuses.PendingReview, AnalysisStatuses.PreApproved];

    private readonly FinanciaDbContext _dbContext;
    private readonly ILogger<PersonAnalysisRepository> _logger;

    /// <summary>Initializes a new instance of <see cref="PersonAnalysisRepository"/>.</summary>
    public PersonAnalysisRepository(FinanciaDbContext dbContext, ILogger<PersonAnalysisRepository> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task CreateAsync(
        Analysis analysis, AnalysisResult result, AnalysisParticipant participant,
        IReadOnlyList<AnalysisParticipant> fiadores, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(fiadores);

        if (analysis.AnalysisKind != AnalysisKinds.PersonaHumana)
            throw new ArgumentException("Only a persona humana analysis is created here.", nameof(analysis));

        if (result.AnalysisId != analysis.AnalysisId || participant.AnalysisId != analysis.AnalysisId
            || !string.Equals(result.TenantId, analysis.TenantId, StringComparison.Ordinal)
            || !string.Equals(participant.TenantId, analysis.TenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The result and the participant must belong to the analysis and its tenant.");
        }

        if (participant.Role != ParticipantRoles.Solicitante || participant.FiadorId is not null)
            throw new ArgumentException("The participant must be the solicitante.", nameof(participant));

        // CK_AnalysisParticipants_FiadorId_Role would refuse the INSERT anyway; failing here names the cause.
        if (fiadores.Any(f => f is null
                           || f.Role != ParticipantRoles.Fiador
                           || f.FiadorId is null
                           || f.AnalysisId != analysis.AnalysisId
                           || !string.Equals(f.TenantId, analysis.TenantId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Every fiador row must be a Fiador participant naming its fiador, of the analysis and its tenant.",
                nameof(fiadores));
        }

        // The Analyses row goes first, in its own SaveChanges: no FK is modelled between the three tables,
        // so nothing else would make EF order the INSERTs. The transaction makes the three one unit, and
        // EnableRetryOnFailure requires it to run inside the execution strategy. InMemory (the behavioural
        // tests) has no transactions; there the three saves simply run in order.
        if (_dbContext.Database.IsRelational())
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await SaveInOrderAsync(analysis, result, participant, fiadores, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        else
        {
            await SaveInOrderAsync(analysis, result, participant, fiadores, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Persona humana analysis {AnalysisId} created with result {ResultId}, participant {ParticipantId} and " +
            "{FiadorCount} fiador row(s) for tenant {TenantId}",
            analysis.AnalysisId, result.ResultId, participant.ParticipantId, fiadores.Count, analysis.TenantId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisParticipant>> GetParticipantsAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        var rows = await _dbContext.AnalysisParticipants
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.AnalysisId == analysisId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .OrderBy(p => p.Role == ParticipantRoles.Solicitante ? 0 : 1)
            .ThenBy(p => p.CreatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> HasOpenPersonAnalysisAsync(Guid entityId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Analyses
            .AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId
                        && a.EntityId == entityId
                        && a.AnalysisKind == AnalysisKinds.PersonaHumana
                        && OpenStatuses.Contains(a.Status), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetConfirmationChecksAsync(Guid analysisId, string tenantId, string checksJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksJson);

        var analysis = await _dbContext.Analyses
            .FirstOrDefaultAsync(a => a.TenantId == tenantId
                                   && a.AnalysisId == analysisId
                                   && a.AnalysisKind == AnalysisKinds.PersonaHumana, ct)
            .ConfigureAwait(false);
        if (analysis is null)
            return false;

        analysis.ConfirmationChecksJson = checksJson;
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Confirmation checks recorded on persona humana analysis {AnalysisId} for tenant {TenantId}",
            analysisId, tenantId);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, FiadorIdentity>> GetFiadorIdentitiesAsync(
        IReadOnlyCollection<Guid> fiadorIds, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fiadorIds);

        if (fiadorIds.Count == 0)
            return new Dictionary<Guid, FiadorIdentity>();

        var ids = fiadorIds.Distinct().ToList();
        var rows = await _dbContext.Fiadores
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && ids.Contains(f.FiadorId))
            .Select(f => new { f.FiadorId, f.Denominacion, f.CUIT })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.FiadorId, r => new FiadorIdentity(r.Denominacion, r.CUIT));
    }

    /// <inheritdoc />
    public async Task<AnalysisKindAndFiscalYear?> GetKindAndFiscalYearAsync(Guid analysisId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AnalysisId == analysisId)
            .Select(a => new AnalysisKindAndFiscalYear(a.AnalysisKind, a.FiscalYear, a.EntityId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, decimal>> GetEvaluationConfigAsync(string tenantId, CancellationToken ct)
    {
        var rows = await _dbContext.ScoringConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId
                     && c.ConfigKey.StartsWith(PersonEvaluationDefaults.KeyPrefix)
                     && !c.ConfigKey.Contains("__sector__"))
            .Select(c => new { c.ConfigKey, c.ConfigValue })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Where(r => PersonEvaluationDefaults.IsKey(r.ConfigKey))
            .GroupBy(r => r.ConfigKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ConfigValue, StringComparer.Ordinal);
    }

    private async Task SaveInOrderAsync(
        Analysis analysis, AnalysisResult result, AnalysisParticipant participant,
        IReadOnlyList<AnalysisParticipant> fiadores, CancellationToken ct)
    {
        _dbContext.Analyses.Add(analysis);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _dbContext.AnalysisResults.Add(result);
        _dbContext.AnalysisParticipants.Add(participant);
        _dbContext.AnalysisParticipants.AddRange(fiadores);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
