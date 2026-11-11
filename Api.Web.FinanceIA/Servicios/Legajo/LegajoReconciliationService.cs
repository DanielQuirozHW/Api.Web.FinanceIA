// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// <see cref="ILegajoReconciliationService"/>: loads the legajo's approved documents on their effective
/// values, runs <see cref="LegajoReconciliation.Build"/>, and lets the repository merge and save the
/// result in one SaveChanges. Logs ids and counts only — never a value, a description or a note.
/// </summary>
public sealed class LegajoReconciliationService : ILegajoReconciliationService
{
    private readonly ILegajoDocumentRepository       _legajoRepository;
    private readonly ILegajoReconciliationRepository _reconciliationRepository;
    private readonly ILegajoIntegrityService         _integrity;
    private readonly LegajoOptions                   _legajoOptions;
    private readonly ILogger<LegajoReconciliationService> _logger;

    /// <summary>Initializes a new instance of <see cref="LegajoReconciliationService"/>.</summary>
    public LegajoReconciliationService(
        ILegajoDocumentRepository       legajoRepository,
        ILegajoReconciliationRepository reconciliationRepository,
        ILegajoIntegrityService         integrity,
        IOptions<LegajoOptions>         legajoOptions,
        ILogger<LegajoReconciliationService> logger)
    {
        _legajoRepository         = legajoRepository;
        _reconciliationRepository = reconciliationRepository;
        _integrity                = integrity;
        _legajoOptions            = legajoOptions.Value;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task<LegajoRebuildOutcome> RebuildAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        for (var attempt = 1; ; attempt++)
        {
            var legajo = await _legajoRepository.GetLegajoSummaryAsync(personLegajoId, tenantId, ct).ConfigureAwait(false);
            if (legajo is null)
                return new LegajoRebuildOutcome(null, LegajoFound: false, LostRace: false);

            var inputs = await LoadInputsAsync(personLegajoId, tenantId, legajo.CUIT, ct).ConfigureAwait(false);
            var build  = LegajoReconciliation.Build(tenantId, personLegajoId, inputs);

            try
            {
                var merge = await _reconciliationRepository
                    .ApplyRebuildAsync(personLegajoId, tenantId, build, DateTime.UtcNow, ct)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Legajo {PersonLegajoId} tenant {TenantId} reconciled: {ItemsCount} item(s) (+{Added} ~{Updated} -{Removed}, {Lapsed} confirmation(s) lapsed), {IncomesCount} income(s), {Used} document(s) used, {Skipped} skipped",
                    personLegajoId, tenantId, build.Items.Count, merge.ItemsAdded.Count, merge.ItemsUpdated,
                    merge.ItemsRemoved.Count, merge.ConfirmationsLapsed, build.Incomes.Count, build.DocumentsUsed.Count,
                    build.Skipped.Count);

                return new LegajoRebuildOutcome(new LegajoReconciliationResultResponse
                {
                    PersonLegajoId      = personLegajoId,
                    ItemsCount          = build.Items.Count,
                    IncomesCount        = build.Incomes.Count,
                    ItemsAdded          = merge.ItemsAdded.Count,
                    ItemsUpdated        = merge.ItemsUpdated,
                    ItemsRemoved        = merge.ItemsRemoved.Count,
                    ConfirmationsLapsed = merge.ConfirmationsLapsed,
                    DocumentsUsed       = build.DocumentsUsed,
                    DocumentsSkipped    = build.Skipped
                        .Select(s => new LegajoReconciliationSkippedResponse { DocumentId = s.DocumentId, Reason = s.Reason })
                        .ToList()
                }, LegajoFound: true, LostRace: false);
            }
            catch (DbUpdateException ex) when (attempt < 2)
            {
                _logger.LogWarning(
                    "Legajo {PersonLegajoId} tenant {TenantId}: reconciliation lost a race ({ExceptionType}); retrying once",
                    personLegajoId, tenantId, ex.GetType().Name);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(
                    "Legajo {PersonLegajoId} tenant {TenantId}: reconciliation lost a race twice ({ExceptionType}); nothing saved",
                    personLegajoId, tenantId, ex.GetType().Name);
                return new LegajoRebuildOutcome(null, LegajoFound: true, LostRace: true);
            }
        }
    }

    /// <inheritdoc />
    public async Task TryRebuildAsync(Guid personLegajoId, string tenantId, string trigger, CancellationToken ct)
    {
        try
        {
            var outcome = await RebuildAsync(personLegajoId, tenantId, ct).ConfigureAwait(false);
            if (outcome.LostRace)
            {
                _logger.LogWarning(
                    "Legajo {PersonLegajoId} tenant {TenantId}: reconciliation after {Trigger} not saved (lost a race); POST .../reconciliation/rebuild repairs it",
                    personLegajoId, tenantId, trigger);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed rebuild must never fail the flow that triggered it: the rows stay as they were and the
            // explicit rebuild endpoint repairs them.
            _logger.LogWarning(ex,
                "Legajo {PersonLegajoId} tenant {TenantId}: reconciliation after {Trigger} failed; POST .../reconciliation/rebuild repairs it",
                personLegajoId, tenantId, trigger);
        }
    }

    /// <summary>The legajo's approved documents: live, Ready, with a succeeded current extraction run, on their effective values.</summary>
    private async Task<IReadOnlyList<LegajoReconciliationInput>> LoadInputsAsync(
        Guid personLegajoId, string tenantId, string legajoCuit, CancellationToken ct)
    {
        var documents = (await _legajoRepository.GetLegajoDocumentsAsync(personLegajoId, tenantId, ct).ConfigureAwait(false))
            .Where(d => !d.IsDeleted && d.LegajoStatus == LegajoStatuses.Ready && LegajoExtractionPrompts.For(d.DocumentType) is not null)
            .ToList();
        if (documents.Count == 0)
            return [];

        var runs = await _legajoRepository
            .GetCurrentExtractionsAsync(documents.Select(d => d.DocumentId).ToList(), tenantId, ct)
            .ConfigureAwait(false);
        if (runs.Count == 0)
            return [];

        var corrections = await _legajoRepository
            .GetCorrectionsAsync(runs.Values.Select(r => r.ExtractionId).ToList(), tenantId, ct)
            .ConfigureAwait(false);

        var inputs = new List<LegajoReconciliationInput>();
        foreach (var document in documents)
        {
            if (!runs.TryGetValue(document.DocumentId, out var run))
                continue;

            var effective = LegajoExtractionReview.Evaluate(
                run, corrections, _integrity,
                document.PeriodEnd is { } end ? DateOnly.FromDateTime(end) : null,
                legajoCuit, _legajoOptions.FieldConfidenceThreshold);
            if (effective is null)
                continue;

            inputs.Add(new LegajoReconciliationInput(document.DocumentId, document.UploadedAt, document.SupersedesDocumentId, effective));
        }

        return inputs;
    }
}
