// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAnalysisRescoreRepository"/> (balance) and
/// <see cref="IPersonAnalysisRescoreRepository"/> (persona humana) against FinanciaDbContext. Both writes go
/// through the same decision gate, transaction and concurrency token.
/// </summary>
public sealed class AnalysisRescoreRepository : IAnalysisRescoreRepository, IPersonAnalysisRescoreRepository
{
    private readonly FinanciaDbContext                  _dbContext;
    private readonly IAnalysisEditRepository            _editRepository;
    private readonly ILogger<AnalysisRescoreRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisRescoreRepository"/>, writing a correction's
    /// [AnalysisEdits] rows through an <see cref="AnalysisEditRepository"/> on the SAME context.
    /// </summary>
    public AnalysisRescoreRepository(FinanciaDbContext dbContext, ILogger<AnalysisRescoreRepository> logger)
        : this(dbContext, new AnalysisEditRepository(dbContext), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisRescoreRepository"/>. <paramref name="editRepository"/>
    /// must work on <paramref name="dbContext"/> — the scoped one DI hands both — for its insert to join the
    /// write transaction.
    /// </summary>
    public AnalysisRescoreRepository(
        FinanciaDbContext dbContext, IAnalysisEditRepository editRepository, ILogger<AnalysisRescoreRepository> logger)
    {
        _dbContext      = dbContext;
        _editRepository = editRepository;
        _logger         = logger;
    }

    /// <inheritdoc />
    public async Task<bool> HasAnyVoteAsync(Guid analysisId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.AnalysisApprovalVotes
            .AsNoTracking()
            .AnyAsync(v => v.TenantId == tenantId && v.AnalysisId == analysisId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetAnalysisIdAsync(Guid resultId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ResultId == resultId)
            .Select(r => (Guid?)r.AnalysisId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(Guid AnalysisId, string? BalanceJson)?> GetCorrectionSourceAsync(
        Guid resultId, string tenantId, CancellationToken ct)
    {
        var row = await _dbContext.AnalysisResults
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ResultId == resultId)
            .Select(r => new { r.AnalysisId, r.BalanceJson })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : (row.AnalysisId, row.BalanceJson);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRescoreHistoryItem>> GetHistoryAsync(
        Guid analysisId, string tenantId, CancellationToken ct)
    {
        return await _dbContext.AnalysisRescores
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.AnalysisId == analysisId)
            .OrderByDescending(h => h.RescoredAt)
            .Select(h => new AnalysisRescoreHistoryItem(
                h.RescoreId,
                h.RescoredAt,
                h.RescoredBy,
                h.PreviousScore,
                h.NewScore,
                h.PreviousLevel,
                h.NewLevel,
                h.PreviousEnrichmentStatus,
                h.NewEnrichmentStatus,
                // Persona humana only: the suggestions index of PreviousValuesJson, read server-side so the
                // blob is never selected. A balance row's PreviousValuesJson has no such node: null.
                SqlJsonFunctions.JsonValue(h.PreviousValuesJson, PersonRescorePreviousValues.PreviousSuggestionPath),
                SqlJsonFunctions.JsonValue(h.PreviousValuesJson, PersonRescorePreviousValues.NewSuggestionPath),
                // "balance_correction" for a re-score run by a balance correction; null for a plain one.
                SqlJsonFunctions.JsonValue(h.PreviousValuesJson, RescorePreviousValues.TriggerPath)))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// REPEATABLE READ, and the order of the reads, are what close the race with a decision. The gate
    /// reads the [Analyses] row first and holds its shared lock until commit, so a PATCH decision
    /// that arrives while this transaction is open waits for it instead of slipping in between the
    /// check and the write; one that committed before the check is seen by it and aborts the
    /// re-score. The [AnalysisResults] row is NOT read here — the update goes through an attached
    /// stub — so two concurrent re-scores never both hold a shared lock on it and cannot deadlock:
    /// the second UPDATE waits for the first, then matches no row and becomes a lost race.
    /// </para>
    /// <para>
    /// The UPDATE's WHERE is the primary key plus the concurrency token. The caller resolved the
    /// result by (ResultId, TenantId) before computing, and the gate below is tenant-scoped on the
    /// analysis that owns it, so a ResultId from another tenant never reaches this point.
    /// </para>
    /// <para>
    /// InMemory (the behavioural tests) has no transactions; there the same body simply runs — gate,
    /// guarded UPDATE, history INSERT — exactly the PersonAnalysisRepository arrangement.
    /// </para>
    /// </remarks>
    public async Task<AnalysisRescoreWriteOutcome> TryApplyAsync(AnalysisRescoreWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (!_dbContext.Database.IsRelational())
            return await ApplyAsync(write, ct).ConfigureAwait(false);

        // EnableRetryOnFailure refuses a user transaction outside the execution strategy.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                .ConfigureAwait(false);

            var outcome = await ApplyAsync(write, ct).ConfigureAwait(false);

            if (outcome == AnalysisRescoreWriteOutcome.Applied)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            else
                await transaction.RollbackAsync(ct).ConfigureAwait(false);

            return outcome;
        }).ConfigureAwait(false);
    }

    private async Task<AnalysisRescoreWriteOutcome> ApplyAsync(AnalysisRescoreWrite write, CancellationToken ct)
    {
        // ── Decision gate, re-checked now that the score is computed ──────────
        if (await GateAsync(write.AnalysisId, write.TenantId, ct).ConfigureAwait(false) is { } refused)
            return refused;

        // ── The update (only the re-scored columns), then the history insert ──────
        // An execution-strategy retry re-runs this body after a transient failure, possibly with the
        // previous attempt's stub or history row still tracked; attaching a second instance of the
        // same key would throw, so forget them first.
        DetachPrevious(write);

        var stub = new AnalysisResult
        {
            ResultId     = write.ResultId,
            AnalysisId   = write.AnalysisId,
            TenantId     = write.TenantId,
            RescoreCount = write.ExpectedRescoreCount
        };

        var entry = _dbContext.AnalysisResults.Attach(stub);

        SetModified(entry, x => x.RatiosJson,         write.RatiosJson);
        SetModified(entry, x => x.BcraDataJson,       write.BcraDataJson);
        SetModified(entry, x => x.MacroDataJson,      write.MacroDataJson);
        SetModified(entry, x => x.AiSuggestedScore,   write.AiSuggestedScore);
        SetModified(entry, x => x.AiSuggestedLevel,   write.AiSuggestedLevel);
        SetModified(entry, x => x.ScoreBreakdownJson, write.ScoreBreakdownJson);
        SetModified(entry, x => x.ScoringNarrative,   write.ScoringNarrative);
        SetModified(entry, x => x.LufeDataJson,       write.LufeDataJson);
        SetModified(entry, x => x.FiscalDataJson,     write.FiscalDataJson);
        SetModified(entry, x => x.NosisDataJson,      write.NosisDataJson);
        SetModified(entry, x => x.CasfogDataJson,     write.CasfogDataJson);
        SetModified(entry, x => x.EnrichmentStatus,   write.EnrichmentStatus);
        SetModified(entry, x => x.EnrichmentNotes,    write.EnrichmentNotes);
        SetModified(entry, x => x.RescoredAt,         write.RescoredAt);
        SetModified(entry, x => x.RescoredBy,         write.RescoredBy);

        // A balance correction, and only a correction, also rewrites the extracted balance and the
        // ValidationStatus recomputed from its re-evaluated integrity — in this same guarded UPDATE.
        if (write.Correction is { } correction)
        {
            SetModified(entry, x => x.BalanceJson,      correction.BalanceJson);
            SetModified(entry, x => x.ValidationStatus, correction.ValidationStatus);
        }

        // The concurrency token: ORIGINAL = what was read (the WHERE), CURRENT = the increment (the SET).
        var count = entry.Property(x => x.RescoreCount);
        count.OriginalValue = write.ExpectedRescoreCount;
        count.CurrentValue  = write.ExpectedRescoreCount + 1;
        count.IsModified    = true;

        // The guarded UPDATE goes first, in its own SaveChanges, and the history INSERT only after it
        // matched a row. Inside the relational transaction the two are one unit either way; ordering
        // them is what keeps a lost race from inserting a history row on a provider without
        // transactions too (InMemory applies a batch entry by entry, with no rollback).
        try
        {
            _dbContext.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Zero rows matched ResultId + the expected RescoreCount: another re-score landed first.
            // Nothing was written. Detach the stub so a retry or a later write on this scoped context
            // starts clean.
            entry.State = EntityState.Detached;

            _logger.LogWarning(
                "Re-score of analysis {AnalysisId} tenant {TenantId} lost the race: RescoreCount was no longer {Expected}",
                write.AnalysisId, write.TenantId, write.ExpectedRescoreCount);

            return AnalysisRescoreWriteOutcome.LostRace;
        }

        entry.State = EntityState.Detached;

        var history = _dbContext.AnalysisRescores.Add(write.History);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        history.State = EntityState.Detached;

        // A correction's audit rows, last, inside the same transaction: a failure here rolls back the
        // corrected balance, the re-score and the history row with them.
        if (write.Correction is { } edits)
            await _editRepository.AddRangeAsync(edits.Edits, ct).ConfigureAwait(false);

        return AnalysisRescoreWriteOutcome.Applied;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same arrangement as the balance write above — REPEATABLE READ, the gate first, the guarded
    /// AnalysisResults UPDATE through an attached stub as the race detector — followed by the participant
    /// rows, each overwritten in place through a stub keyed on its ParticipantId (no delete + insert: the ids
    /// the decision, the PDF and the fiador contributions point at stay valid), then the history INSERT.
    /// Every participant id came from a tenant-scoped read of this analysis; a row that no longer exists
    /// makes its UPDATE match nothing, which throws and rolls the whole transaction back.
    /// </remarks>
    public async Task<AnalysisRescoreWriteOutcome> TryApplyAsync(PersonAnalysisRescoreWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (!_dbContext.Database.IsRelational())
            return await ApplyAsync(write, ct).ConfigureAwait(false);

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
                .ConfigureAwait(false);

            var outcome = await ApplyAsync(write, ct).ConfigureAwait(false);

            if (outcome == AnalysisRescoreWriteOutcome.Applied)
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            else
                await transaction.RollbackAsync(ct).ConfigureAwait(false);

            return outcome;
        }).ConfigureAwait(false);
    }

    private async Task<AnalysisRescoreWriteOutcome> ApplyAsync(PersonAnalysisRescoreWrite write, CancellationToken ct)
    {
        // ── Decision gate, re-checked now that the evaluation is computed ─────
        if (await GateAsync(write.AnalysisId, write.TenantId, ct).ConfigureAwait(false) is { } refused)
            return refused;

        DetachPrevious(write);

        // ── The result: the derived columns, guarded by RescoreCount ─────────
        var stub = new AnalysisResult
        {
            ResultId     = write.ResultId,
            AnalysisId   = write.AnalysisId,
            TenantId     = write.TenantId,
            RescoreCount = write.ExpectedRescoreCount
        };

        var entry = _dbContext.AnalysisResults.Attach(stub);

        SetModified(entry, x => x.BcraDataJson,     write.BcraDataJson);
        SetModified(entry, x => x.MacroDataJson,    write.MacroDataJson);
        SetModified(entry, x => x.FiscalDataJson,   write.FiscalDataJson);
        SetModified(entry, x => x.LufeDataJson,     write.LufeDataJson);
        SetModified(entry, x => x.AiInputHash,      write.AiInputHash);
        SetModified(entry, x => x.AiOutputHash,     write.AiOutputHash);
        SetModified(entry, x => x.EnrichmentStatus, write.EnrichmentStatus);
        SetModified(entry, x => x.EnrichmentNotes,  write.EnrichmentNotes);
        SetModified(entry, x => x.RescoredAt,       write.RescoredAt);
        SetModified(entry, x => x.RescoredBy,       write.RescoredBy);

        var count = entry.Property(x => x.RescoreCount);
        count.OriginalValue = write.ExpectedRescoreCount;
        count.CurrentValue  = write.ExpectedRescoreCount + 1;
        count.IsModified    = true;

        try
        {
            _dbContext.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            entry.State = EntityState.Detached;

            _logger.LogWarning(
                "Re-evaluation of analysis {AnalysisId} tenant {TenantId} lost the race: RescoreCount was no longer {Expected}",
                write.AnalysisId, write.TenantId, write.ExpectedRescoreCount);

            return AnalysisRescoreWriteOutcome.LostRace;
        }

        entry.State = EntityState.Detached;

        // ── The participant rows, in place ───────────────────────────────────
        var participantEntries = new List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<AnalysisParticipant>>();
        foreach (var row in write.Participants)
        {
            var participant = _dbContext.AnalysisParticipants.Attach(new AnalysisParticipant
            {
                ParticipantId = row.ParticipantId,
                TenantId      = write.TenantId,
                AnalysisId    = write.AnalysisId
            });

            SetModified(participant, x => x.DocumentVersionsJson, row.DocumentVersionsJson);
            SetModified(participant, x => x.FiltersJson,          row.FiltersJson);
            SetModified(participant, x => x.IndicatorsJson,       row.IndicatorsJson);
            SetModified(participant, x => x.CoverageJson,         row.CoverageJson);
            SetModified(participant, x => x.EvaluationVersion,    row.EvaluationVersion);
            SetModified(participant, x => x.CreatedAt,            row.CreatedAt);
            participantEntries.Add(participant);
        }

        try
        {
            _dbContext.ChangeTracker.DetectChanges();
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var participant in participantEntries)
                participant.State = EntityState.Detached;
        }

        var history = _dbContext.AnalysisRescores.Add(write.History);
        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        history.State = EntityState.Detached;

        return AnalysisRescoreWriteOutcome.Applied;
    }

    /// <summary>
    /// The decision half of the eligibility, re-read inside the write transaction: null when the analysis is
    /// still PendingReview with no vote and no decision record, otherwise the outcome that refuses the write.
    /// Under REPEATABLE READ the [Analyses] row's shared lock is held until commit, so a decision cannot slip
    /// in between this check and the write.
    /// </summary>
    private async Task<AnalysisRescoreWriteOutcome?> GateAsync(Guid analysisId, string tenantId, CancellationToken ct)
    {
        var gate = await _dbContext.Analyses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AnalysisId == analysisId)
            .Select(a => new { a.Status, a.PreApprovedBy, a.PreApprovedAt, a.ReviewedBy })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (gate is null)
            return AnalysisRescoreWriteOutcome.NotPending;

        var hasVotes = await HasAnyVoteAsync(analysisId, tenantId, ct).ConfigureAwait(false);
        var hasDecisionRecord = gate.PreApprovedBy is not null || gate.PreApprovedAt is not null || gate.ReviewedBy is not null;

        var blocked = RescoreEligibility.DecisionGate(gate.Status, hasDecisionRecord, hasVotes);
        if (blocked is null)
            return null;

        _logger.LogWarning(
            "Re-score of analysis {AnalysisId} tenant {TenantId} not applied: {Reason} changed while it was computing",
            analysisId, tenantId, blocked);

        return blocked == RescoreErrorCodes.NotPending
            ? AnalysisRescoreWriteOutcome.NotPending
            : AnalysisRescoreWriteOutcome.HasVotes;
    }

    private void DetachPrevious(PersonAnalysisRescoreWrite write)
    {
        foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisResult>()
                     .Where(e => e.Entity.ResultId == write.ResultId).ToList())
            tracked.State = EntityState.Detached;

        var participantIds = write.Participants.Select(p => p.ParticipantId).ToHashSet();
        foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisParticipant>()
                     .Where(e => participantIds.Contains(e.Entity.ParticipantId)).ToList())
            tracked.State = EntityState.Detached;

        foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisRescore>()
                     .Where(e => e.Entity.RescoreId == write.History.RescoreId).ToList())
            tracked.State = EntityState.Detached;
    }

    private void DetachPrevious(AnalysisRescoreWrite write)
    {
        foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisResult>()
                     .Where(e => e.Entity.ResultId == write.ResultId).ToList())
            tracked.State = EntityState.Detached;

        foreach (var tracked in _dbContext.ChangeTracker.Entries<AnalysisRescore>()
                     .Where(e => e.Entity.RescoreId == write.History.RescoreId).ToList())
            tracked.State = EntityState.Detached;
    }

    private static void SetModified<TEntity, TValue>(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry,
        System.Linq.Expressions.Expression<Func<TEntity, TValue>> property,
        TValue value)
        where TEntity : class
    {
        var p = entry.Property(property);
        p.CurrentValue = value;
        p.IsModified   = true;
    }
}
