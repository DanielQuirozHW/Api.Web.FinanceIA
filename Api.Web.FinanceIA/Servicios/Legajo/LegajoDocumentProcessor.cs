// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// <see cref="ILegajoDocumentProcessor"/>. Scoped: one instance per job, with its own DbContext.
///
/// <para>
/// Two stages, dispatched by status, each in the same order — the order is the safety argument:
/// <list type="number">
/// <item>CLAIM, in one conditional UPDATE: Queued → Classifying, or ReadyForExtraction → Extracting
/// for a type with an extractor (Phase 2b). A second instance, a duplicate queue item or a re-enqueue
/// loses here and does nothing; a type without an extractor is never claimed.</item>
/// <item>CONSENT: without it no request is built — a Classifying document returns to Uploaded, an
/// Extracting one to ReadyForExtraction (its classification, possibly an analyst's confirmation, is
/// kept). It is checked again immediately before every request and before completing.</item>
/// <item>BUDGET: Billing:MaxExtractionAttemptsPerDocument, per FILE, as for balances.</item>
/// <item>CALL, then persist: the ApiCallLogs row (written by <see cref="LegajoMessagesClient"/>) and the
/// LegajoExtractions run for every request that reached the model, success or failure; then the
/// document's status, conditional on it still being in the claimed status.</item>
/// </list>
/// </para>
///
/// <para>
/// The extraction stage runs pass 1 in the chosen read mode, checks it in C#, and runs pass 2 ONCE,
/// reading the pages as images, when the output was unusable or — only if pass 1 read the document
/// block — a check failed. Both runs are stored; the better one is current. C# then decides: Blocked
/// on another person's CUIT, Ready when nothing is missing or flagged, NeedsReview otherwise. A failure
/// writes an <see cref="LegajoExtractionErrors.StagePrefix"/> code so Reprocess returns it to
/// ReadyForExtraction, never to classification.
/// </para>
///
/// <para>
/// Logs carry ids, statuses, codes and token counts only — never the legajo's CUIT or name, never
/// what the model read (#237's convention).
/// </para>
/// </summary>
public sealed class LegajoDocumentProcessor : ILegajoDocumentProcessor
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { WriteIndented = false };

    private readonly ILegajoDocumentRepository _legajoRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentStorageService _storage;
    private readonly ILegajoClassificationService _classifier;
    private readonly ILegajoExtractionService _extractor;
    private readonly ILegajoIntegrityService _integrity;
    private readonly ILegajoPipelineQueue _queue;
    private readonly ILegajoReconciliationService _reconciliation;
    private readonly LegajoOptions _legajoOptions;
    private readonly BillingOptions _billingOptions;
    private readonly ILogger<LegajoDocumentProcessor> _logger;

    /// <summary>Initializes a new instance of <see cref="LegajoDocumentProcessor"/>.</summary>
    public LegajoDocumentProcessor(
        ILegajoDocumentRepository legajoRepository,
        IDocumentRepository documentRepository,
        IDocumentStorageService storage,
        ILegajoClassificationService classifier,
        ILegajoExtractionService extractor,
        ILegajoIntegrityService integrity,
        ILegajoPipelineQueue queue,
        ILegajoReconciliationService reconciliation,
        IOptions<LegajoOptions> legajoOptions,
        IOptions<BillingOptions> billingOptions,
        ILogger<LegajoDocumentProcessor> logger)
    {
        _legajoRepository   = legajoRepository;
        _documentRepository = documentRepository;
        _storage            = storage;
        _classifier         = classifier;
        _extractor          = extractor;
        _integrity          = integrity;
        _queue              = queue;
        _reconciliation     = reconciliation;
        _legajoOptions      = legajoOptions.Value;
        _billingOptions     = billingOptions.Value;
        _logger             = logger;
    }

    /// <inheritdoc />
    public async Task ProcessAsync(LegajoWorkItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var documentId = item.DocumentId;
        var tenantId   = item.TenantId;

        // ── 1. Claim: classification first, then extraction ─────────────────────
        string claimed;
        if (await _legajoRepository
                .TryTransitionAsync(documentId, tenantId, LegajoStatuses.Queued, LegajoStatuses.Classifying,
                                    DateTime.UtcNow, ct)
                .ConfigureAwait(false))
        {
            claimed = LegajoStatuses.Classifying;
        }
        else if (await _legajoRepository
                     .TryClaimForExtractionAsync(documentId, tenantId, LegajoExtractionPrompts.SupportedTypes,
                                                 DateTime.UtcNow, ct)
                     .ConfigureAwait(false))
        {
            claimed = LegajoStatuses.Extracting;
        }
        else
        {
            _logger.LogInformation(
                "Legajo document {DocumentId} tenant {TenantId} not claimable (not Queued, nor ReadyForExtraction with an extractor); skipped",
                documentId, tenantId);
            return;
        }

        // From here on the row is in the claimed status and belongs to this job. Whatever happens, it
        // must not stay there: an unexpected exception ends in Failed (which Reprocess can recover),
        // never in a row only a sweep could find.
        var state = new JobState();
        try
        {
            if (claimed == LegajoStatuses.Classifying)
                await ProcessClaimedAsync(documentId, tenantId, state, ct).ConfigureAwait(false);
            else
                await ExtractClaimedAsync(documentId, tenantId, state, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-job: the row stays claimed and the next startup or sweep requeues it.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Legajo document {DocumentId} tenant {TenantId}: unexpected {ExceptionType} after the claim ({LegajoStatus})",
                documentId, tenantId, ex.GetType().Name, claimed);

            var code = claimed == LegajoStatuses.Extracting
                ? LegajoExtractionErrors.ForDocument(LegajoClassificationErrors.Unexpected)
                : LegajoClassificationErrors.Unexpected;

            // CancellationToken.None: this write is the cleanup and must happen.
            await _legajoRepository
                .FailAsync(documentId, tenantId, claimed, code,
                           countsAsAttempt: state.BillableRequestSent, DateTime.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LegajoWorkItem>> RecoverAsync(
        bool requeueEveryQueued, IReadOnlySet<Guid> inFlight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inFlight);

        var staleMinutes = Math.Max(1, _legajoOptions.StaleMinutes);
        var now    = DateTime.UtcNow;
        var cutoff = now.AddMinutes(-staleMinutes);

        var rows = await _legajoRepository
            .GetRecoverableAsync(cutoff, requeueEveryQueued ? null : cutoff, LegajoExtractionPrompts.SupportedTypes, ct)
            .ConfigureAwait(false);

        var items = new List<LegajoWorkItem>(rows.Count);
        var requeuedClassifying = 0;
        var requeuedExtracting = 0;

        foreach (var (documentId, tenantId, status) in rows)
        {
            // Never pull a document out from under a job this instance is running right now.
            if (inFlight.Contains(documentId))
                continue;

            // A Classifying row was claimed by a job that stopped mid-way (a restart, a crash): hand it
            // back to Queued first, conditionally, so the claim can be taken again.
            if (status == LegajoStatuses.Classifying)
            {
                if (!await _legajoRepository
                        .TryTransitionAsync(documentId, tenantId, LegajoStatuses.Classifying, LegajoStatuses.Queued, now, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                requeuedClassifying++;
            }

            // Likewise an Extracting row goes back to ReadyForExtraction — never to classification.
            if (status == LegajoStatuses.Extracting)
            {
                if (!await _legajoRepository
                        .TryTransitionAsync(documentId, tenantId, LegajoStatuses.Extracting, LegajoStatuses.ReadyForExtraction, now, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                requeuedExtracting++;
            }

            // A Queued or ReadyForExtraction row is simply enqueued again. If its original item is still
            // in the channel, the duplicate loses the claim and does nothing.
            items.Add(new LegajoWorkItem(documentId, tenantId));
        }

        if (items.Count > 0)
        {
            _logger.LogInformation(
                "Legajo pipeline recovery ({RecoveryKind}): {Count} document(s) enqueued, {ClassifyingCount} of them back from Classifying, {ExtractingCount} back from Extracting",
                requeueEveryQueued ? "startup" : "sweep", items.Count, requeuedClassifying, requeuedExtracting);
        }

        return items;
    }

    private async Task ProcessClaimedAsync(Guid documentId, string tenantId, JobState state, CancellationToken ct)
    {
        var work = await _legajoRepository.GetWorkAsync(documentId, tenantId, ct).ConfigureAwait(false);
        if (work is null)
        {
            _logger.LogWarning("Legajo document {DocumentId} tenant {TenantId} vanished after its claim",
                documentId, tenantId);
            return;
        }

        // ── 2. Consent ──────────────────────────────────────────────────────────
        if (!work.HasActiveConsent)
        {
            await BackToUploadedAsync(work, "no active consent", ct).ConfigureAwait(false);
            return;
        }

        // ── 3. Attempt budget (per file) ────────────────────────────────────────
        var maxAttempts = _billingOptions.MaxExtractionAttemptsPerDocument;
        if (maxAttempts > 0)
        {
            var spent = await _documentRepository
                .SumExtractionAttemptsByHashAsync(work.FileHashSha256, tenantId, ct)
                .ConfigureAwait(false);

            if (spent >= maxAttempts)
            {
                await FailAsync(work, LegajoClassificationErrors.AttemptsExhausted, countsAsAttempt: false, ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        // ── 4. Bytes and page quality (local, no model) ─────────────────────────
        byte[] bytes;
        try
        {
            bytes = await _storage.ReadAsync(work.StoragePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Legajo document {DocumentId} tenant {TenantId}: storage read failed ({ExceptionType})",
                documentId, tenantId, ex.GetType().Name);
            await FailAsync(work, LegajoClassificationErrors.StorageUnavailable, countsAsAttempt: false, ct)
                .ConfigureAwait(false);
            return;
        }

        var quality = PageTextQualityAnalyzer.Analyze(bytes);

        // ── 5. Classify (consent re-checked before every request) ───────────────
        var result = await _classifier
            .ClassifyAsync(
                new LegajoCallContext(tenantId, documentId),
                bytes,
                quality,
                token => _legajoRepository.HasConsentAsync(work.PersonLegajoId, tenantId, token),
                ct)
            .ConfigureAwait(false);

        // A 429 bills no tokens; everything else that reached the API may have.
        state.BillableRequestSent = result.RequestSent && result.ErrorCode != LegajoClassificationErrors.RateLimited;

        // The cost row is already written: LegajoMessagesClient logs every call that reached the API
        // before returning, ahead of anything here that can throw.

        // One run per request that reached the model. A failed one is stored here; a successful one
        // below, together with the outcome C# decides.
        if (result.RequestSent && !result.Succeeded)
            await StoreRunAsync(work, result, outcome: null, ct).ConfigureAwait(false);

        if (result.ErrorCode == LegajoClassificationErrors.ConsentRevoked)
        {
            await BackToUploadedAsync(work, "consent revoked before the call", ct).ConfigureAwait(false);
            return;
        }

        if (!result.Succeeded || result.Output is null)
        {
            await FailAsync(work, result.ErrorCode ?? LegajoClassificationErrors.Unexpected,
                            countsAsAttempt: state.BillableRequestSent, ct)
                .ConfigureAwait(false);
            return;
        }

        // ── 6. Decide in C#, store the run, then move the document ──────────────
        var outcome = LegajoClassificationOutcome.Decide(
            result.Output, work.DocumentType, work.LegajoCuit, _legajoOptions.ClassificationConfidenceThreshold);

        await StoreRunAsync(work, result, outcome, ct).ConfigureAwait(false);

        // Consent may have been revoked while the call was in flight. The run is kept (it was paid
        // for), but the document does not advance on a reading made without standing consent.
        if (!await _legajoRepository.HasConsentAsync(work.PersonLegajoId, tenantId, ct).ConfigureAwait(false))
        {
            await BackToUploadedAsync(work, "consent revoked while the call was in flight", ct).ConfigureAwait(false);
            return;
        }

        var periodEnd = result.Output.PeriodEnd ?? result.Output.MeasurementDate;

        var moved = await _legajoRepository
            .CompleteClassificationAsync(
                work.DocumentId,
                tenantId,
                outcome.Status,
                periodEnd?.ToDateTime(TimeOnly.MinValue),
                result.Output.EngagementType,
                outcome.HolderCuit,
                DateTime.UtcNow,
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Legajo document {DocumentId} tenant {TenantId} classified: {LegajoStatus} ({ReviewReasonCount} review reason(s), confidence {Confidence}, {InputTokens}/{OutputTokens} tokens, applied: {Applied})",
            work.DocumentId, tenantId, outcome.Status, outcome.ReviewReasons.Count, result.Output.Confidence,
            result.InputTokens, result.OutputTokens, moved);

        // Classified cleanly: straight on to extraction. A type without an extractor loses that claim
        // and waits in ReadyForExtraction for Phase 3.
        if (moved && outcome.Status == LegajoStatuses.ReadyForExtraction)
            _queue.Enqueue(new LegajoWorkItem(work.DocumentId, tenantId));
    }

    private async Task BackToUploadedAsync(LegajoDocumentWork work, string reason, CancellationToken ct)
    {
        await _legajoRepository
            .TryTransitionAsync(work.DocumentId, work.TenantId, LegajoStatuses.Classifying, LegajoStatuses.Uploaded,
                                DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Legajo document {DocumentId} tenant {TenantId} returned to Uploaded: {Reason}",
            work.DocumentId, work.TenantId, reason);
    }

    private async Task FailAsync(LegajoDocumentWork work, string errorCode, bool countsAsAttempt, CancellationToken ct)
    {
        await _legajoRepository
            .FailAsync(work.DocumentId, work.TenantId, LegajoStatuses.Classifying, errorCode, countsAsAttempt,
                       DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Legajo document {DocumentId} tenant {TenantId} failed classification: {ErrorCode} (counts as attempt: {CountsAsAttempt})",
            work.DocumentId, work.TenantId, errorCode, countsAsAttempt);
    }

    private async Task StoreRunAsync(
        LegajoDocumentWork work, LegajoClassificationResult result, LegajoClassificationOutcome? outcome,
        CancellationToken ct)
    {
        var run = new LegajoExtraction
        {
            ExtractionId   = Guid.NewGuid(),
            TenantId       = work.TenantId,
            PersonLegajoId = work.PersonLegajoId,
            DocumentId     = work.DocumentId,
            Kind           = LegajoExtractionKinds.Classification,
            DocumentType   = result.Output?.DocumentType ?? work.DocumentType,
            SchemaVersion  = LegajoClassificationPrompt.SchemaVersion,
            PromptVersion  = LegajoClassificationPrompt.PromptVersion,
            Model          = result.Model,
            ReadMode       = result.ReadMode,
            Status         = result.Succeeded ? LegajoExtractionStatuses.Succeeded : LegajoExtractionStatuses.Failed,
            ResultJson     = BuildResultJson(work, result, outcome),
            Confidence     = result.Output?.Confidence,
            InputTokens    = result.InputTokens,
            OutputTokens   = result.OutputTokens,
            CostUsd        = ClaudeModelPricing.EstimateCostUsd(result.Model, result.InputTokens, result.OutputTokens),
            InputHash      = result.InputHash,
            OutputHash     = result.OutputHash,
            // Only a successful run becomes the document's current classification; a failed one is
            // kept as a record of what was spent.
            IsCurrent      = result.Succeeded,
            CreatedAt      = DateTime.UtcNow,
            CreatedBy      = null
        };

        await _legajoRepository.InsertRunAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The run's ResultJson: the model's reading with provenance, and the C# outcome. PII (the holder
    /// CUIT and its quote) — stored only here and in Documents.HolderCuit.
    /// </summary>
    internal static string BuildResultJson(
        LegajoDocumentWork work, LegajoClassificationResult result, LegajoClassificationOutcome? outcome)
    {
        if (!result.Succeeded || result.Output is null)
            return JsonSerializer.Serialize(new { error = result.ErrorCode }, ResultJsonOptions);

        var output = result.Output;
        return JsonSerializer.Serialize(new
        {
            declaredType      = work.DocumentType,
            detectedType      = output.DocumentType,
            fiscalYear        = output.FiscalYear,
            periodEnd         = output.PeriodEnd?.ToString("yyyy-MM-dd"),
            measurementDate   = output.MeasurementDate?.ToString("yyyy-MM-dd"),
            holderCuit        = new { value = output.HolderCuit, page = output.HolderCuitPage, quote = output.HolderCuitQuote },
            holderCuitMatches = outcome?.HolderCuitMatches,
            engagementType    = output.EngagementType,
            isComposite       = output.IsComposite,
            segments          = output.Segments,
            confidence        = output.Confidence,
            note              = output.Note,
            outcome           = outcome?.Status,
            reviewReasons     = outcome?.ReviewReasons ?? []
        }, ResultJsonOptions);
    }

    // ── Extraction (M10 Phase 2b) ──────────────────────────────────────────────────

    private async Task ExtractClaimedAsync(Guid documentId, string tenantId, JobState state, CancellationToken ct)
    {
        var work = await _legajoRepository.GetWorkAsync(documentId, tenantId, ct).ConfigureAwait(false);
        if (work is null)
        {
            _logger.LogWarning("Legajo document {DocumentId} tenant {TenantId} vanished after its extraction claim",
                documentId, tenantId);
            return;
        }

        // The claim filters on the type; only a type changed between the claim and this read lands here.
        var schema = LegajoExtractionPrompts.For(work.DocumentType);
        if (schema is null)
        {
            await BackToReadyForExtractionAsync(work, "no extractor for the document type", ct).ConfigureAwait(false);
            return;
        }

        // ── 2. Consent ──────────────────────────────────────────────────────────
        if (!work.HasActiveConsent)
        {
            await BackToReadyForExtractionAsync(work, "no active consent", ct).ConfigureAwait(false);
            return;
        }

        // ── 3. Attempt budget (per file) ────────────────────────────────────────
        var maxAttempts = _billingOptions.MaxExtractionAttemptsPerDocument;
        if (maxAttempts > 0)
        {
            var spent = await _documentRepository
                .SumExtractionAttemptsByHashAsync(work.FileHashSha256, tenantId, ct)
                .ConfigureAwait(false);

            if (spent >= maxAttempts)
            {
                await FailExtractionAsync(work, LegajoClassificationErrors.AttemptsExhausted, countsAsAttempt: false, ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        // ── 4. Bytes and page quality (local, no model) ─────────────────────────
        byte[] bytes;
        try
        {
            bytes = await _storage.ReadAsync(work.StoragePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Legajo document {DocumentId} tenant {TenantId}: storage read failed ({ExceptionType})",
                documentId, tenantId, ex.GetType().Name);
            await FailExtractionAsync(work, LegajoClassificationErrors.StorageUnavailable, countsAsAttempt: false, ct)
                .ConfigureAwait(false);
            return;
        }

        var quality   = PageTextQualityAnalyzer.Analyze(bytes);
        var context   = new LegajoCallContext(tenantId, documentId);
        var periodEnd = work.PeriodEnd is { } end ? DateOnly.FromDateTime(end) : (DateOnly?)null;

        Task<bool> StillAllowedAsync(CancellationToken token)
            => _legajoRepository.HasConsentAsync(work.PersonLegajoId, tenantId, token);

        LegajoExtractionAssessment? AssessPass(LegajoExtractionPassResult pass)
            => pass is { Succeeded: true, Reading: { } reading }
                ? LegajoExtractionAssessor.Assess(schema, reading.Result, _integrity, periodEnd, work.LegajoCuit,
                                                  _legajoOptions.FieldConfidenceThreshold)
                : null;

        // A paid run is stored BEFORE anything that reads its content can throw: the assessment runs
        // guarded, the run is written (without checks when the assessment failed), and only then does the
        // failure propagate to the catch-all (Failed, extraction_unexpected).
        async Task<LegajoExtractionAssessment?> StoreAndAssessAsync(LegajoExtractionPassResult pass, Func<LegajoExtractionAssessment?, bool> isCurrent)
        {
            LegajoExtractionAssessment? assessment = null;
            ExceptionDispatchInfo? failure = null;
            try
            {
                assessment = AssessPass(pass);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            if (pass.RequestSent)
                await StoreExtractionRunAsync(work, schema, pass, assessment, isCurrent(assessment), ct).ConfigureAwait(false);

            failure?.Throw();
            return assessment;
        }

        // ── 5. Pass 1 in the chosen read mode, then the checks ──────────────────
        var readMode = LegajoExtractionService.ChooseReadMode(bytes.LongLength, quality);
        var first = await _extractor
            .ExtractAsync(context, schema, bytes, quality, readMode, StillAllowedAsync, ct)
            .ConfigureAwait(false);
        state.Record(first);

        // Stored at once, current if usable: a paid run is durable even if this job dies before pass 2.
        var firstAssessment = await StoreAndAssessAsync(first, assessment => assessment is not null).ConfigureAwait(false);

        if (first.ErrorCode == LegajoClassificationErrors.ConsentRevoked)
        {
            await BackToReadyForExtractionAsync(work, "consent revoked before the call", ct).ConfigureAwait(false);
            return;
        }

        // ── 6. Pass 2, once, reading the pages as images ────────────────────────
        // When the output was unusable (whatever pass 1 read), or a check failed on a DOCUMENT read. A
        // failed check on pass 1's images would be the same input again at temperature 0.
        var kept     = firstAssessment;
        var keptPass = first;
        LegajoExtractionPassResult? second = null;

        var retry = first.ErrorCode == LegajoExtractionErrors.InvalidOutput
                 || (firstAssessment is { FailedChecks: > 0 } && first.ReadMode == LegajoReadModes.Document);
        if (retry)
        {
            second = await _extractor
                .ExtractAsync(context, schema, bytes, quality, LegajoReadModes.Images, StillAllowedAsync, ct)
                .ConfigureAwait(false);
            state.Record(second);

            // IsCurrent only when it is the better run: InsertRunAsync then swaps pass 1 off in the same
            // SaveChanges. Otherwise pass 1 stays current and pass 2 is kept as a record.
            var secondAssessment = await StoreAndAssessAsync(
                    second, assessment => LegajoExtractionSelection.IsBetter(firstAssessment, assessment))
                .ConfigureAwait(false);
            var better = LegajoExtractionSelection.IsBetter(firstAssessment, secondAssessment);

            if (second.ErrorCode == LegajoClassificationErrors.ConsentRevoked)
            {
                await BackToReadyForExtractionAsync(work, "consent revoked before the second pass", ct).ConfigureAwait(false);
                return;
            }

            if (better)
            {
                kept     = secondAssessment;
                keptPass = second;
            }
        }

        if (kept is null)
        {
            var failed = second ?? first;
            await FailExtractionAsync(work, failed.ErrorCode ?? LegajoClassificationErrors.Unexpected,
                                      countsAsAttempt: state.BillableRequestSent, ct)
                .ConfigureAwait(false);
            return;
        }

        // ── 7. Decide in C#, then move the document ─────────────────────────────
        // Consent may have been revoked while a call was in flight. The runs are kept (they were paid
        // for), but the document does not advance on a reading made without standing consent.
        if (!await _legajoRepository.HasConsentAsync(work.PersonLegajoId, tenantId, ct).ConfigureAwait(false))
        {
            await BackToReadyForExtractionAsync(work, "consent revoked while a call was in flight", ct).ConfigureAwait(false);
            return;
        }

        var moved = await _legajoRepository
            .CompleteExtractionAsync(documentId, tenantId, kept.Status, kept.HolderCuit, DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Legajo document {DocumentId} tenant {TenantId} extracted: {LegajoStatus} ({PassCount} pass(es), kept {ReadMode}; {FailedChecks} failed check(s), {MissingCount} missing, {FlaggedCount} flagged; applied: {Applied})",
            documentId, tenantId, kept.Status, second is null ? 1 : 2, keptPass.ReadMode, kept.FailedChecks,
            kept.Missing.Count, kept.Flagged.Count, moved);

        // A new current run replaced whatever this document contributed: a clean one (Ready) joins the
        // legajo's inventory and incomes, one that needs review leaves them until it is approved. Never
        // throws (a failed rebuild is repaired by the next one or the explicit endpoint).
        if (moved)
        {
            await _reconciliation.TryRebuildAsync(work.PersonLegajoId, tenantId, "extraction", ct).ConfigureAwait(false);
        }
    }

    private async Task BackToReadyForExtractionAsync(LegajoDocumentWork work, string reason, CancellationToken ct)
    {
        await _legajoRepository
            .TryTransitionAsync(work.DocumentId, work.TenantId, LegajoStatuses.Extracting, LegajoStatuses.ReadyForExtraction,
                                DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Legajo document {DocumentId} tenant {TenantId} returned to ReadyForExtraction: {Reason}",
            work.DocumentId, work.TenantId, reason);
    }

    private async Task FailExtractionAsync(LegajoDocumentWork work, string errorCode, bool countsAsAttempt, CancellationToken ct)
    {
        var code = LegajoExtractionErrors.ForDocument(errorCode);

        await _legajoRepository
            .FailAsync(work.DocumentId, work.TenantId, LegajoStatuses.Extracting, code, countsAsAttempt,
                       DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Legajo document {DocumentId} tenant {TenantId} failed extraction: {ErrorCode} (counts as attempt: {CountsAsAttempt})",
            work.DocumentId, work.TenantId, code, countsAsAttempt);
    }

    private async Task StoreExtractionRunAsync(
        LegajoDocumentWork work, LegajoExtractionSchema schema, LegajoExtractionPassResult pass,
        LegajoExtractionAssessment? assessment, bool isCurrent, CancellationToken ct)
    {
        var run = new LegajoExtraction
        {
            ExtractionId   = Guid.NewGuid(),
            TenantId       = work.TenantId,
            PersonLegajoId = work.PersonLegajoId,
            DocumentId     = work.DocumentId,
            Kind           = LegajoExtractionKinds.Extraction,
            DocumentType   = schema.DocumentType,
            SchemaVersion  = schema.SchemaVersion,
            PromptVersion  = schema.PromptVersion,
            Model          = pass.Model,
            ReadMode       = pass.ReadMode,
            Status         = pass.Succeeded ? LegajoExtractionStatuses.Succeeded : LegajoExtractionStatuses.Failed,
            ResultJson     = BuildExtractionResultJson(pass, assessment),
            ChecksJson     = assessment is null ? null : JsonSerializer.Serialize(assessment.Checks, ResultJsonOptions),
            Confidence     = assessment?.MeanConfidence,
            InputTokens    = pass.InputTokens,
            OutputTokens   = pass.OutputTokens,
            CostUsd        = ClaudeModelPricing.EstimateCostUsd(pass.Model, pass.InputTokens, pass.OutputTokens),
            InputHash      = pass.InputHash,
            OutputHash     = pass.OutputHash,
            IsCurrent      = isCurrent && assessment is not null,
            CreatedAt      = DateTime.UtcNow,
            CreatedBy      = null
        };

        await _legajoRepository.InsertRunAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An extraction run's ResultJson: the normalized reading (every field with its provenance; PII),
    /// or, for a failed pass, the code and the schema violations — pointers and codes, never values.
    /// </summary>
    internal static string BuildExtractionResultJson(LegajoExtractionPassResult pass, LegajoExtractionAssessment? assessment)
        => pass is { Succeeded: true, Reading: { } reading }
            ? reading.Result.ToJsonString()
            : JsonSerializer.Serialize(new { error = pass.ErrorCode, violations = pass.Reading?.Violations ?? [] }, ResultJsonOptions);

    /// <summary>What a job has done so far, readable by the catch-all after the claim.</summary>
    private sealed class JobState
    {
        /// <summary>A request reached the API and was not a 429: it counts against the file's budget.</summary>
        public bool BillableRequestSent { get; set; }

        /// <summary>Folds one extraction pass into the bookkeeping.</summary>
        public void Record(LegajoExtractionPassResult pass)
            => BillableRequestSent |= pass.RequestSent && pass.ErrorCode != LegajoClassificationErrors.RateLimited;
    }
}
