// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using static Api.Web.FinanceIA.Servicios.AnalysisRuns.AnalysisPayloadJson;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The long part of POST /api/analyses, moved VERBATIM out of AnalysisController.TriggerAsync so the
/// same code serves both the synchronous request and the <c>Prefer: respond-async</c> background run.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, like everything it calls. It never touches an HttpContext, a claim or the tenant accessor:
/// <see cref="AnalysisRun"/> carries the tenant id, the requesting user and the two request values the
/// audit rows record, captured while the request was alive. In the background it runs in its own DI
/// scope (see <see cref="AnalysisJobWorker"/>), with nothing ambient to read.
/// </para>
/// <para>
/// Every difference between the two modes sits behind <see cref="AnalysisRun.IsBackground"/>, so the
/// synchronous run is the code that used to live in the controller, line for line:
/// <list type="bullet">
/// <item>a queued run RE-VALIDATES its document before anything is spent (deleted, moved into a
/// legajo, consent withdrawn → stop, nothing charged);</item>
/// <item>its Analyses row is dated <see cref="AnalysisRun.AcceptedAtUtc"/>, so the quota month that
/// admitted it is the month that counts it;</item>
/// <item>a cancellation after ownership is a fault like any other (no client can have disconnected),
/// and every fault reaches the requester's bell (nobody is reading the response).</item>
/// </list>
/// </para>
/// <para>
/// The constructor does plain assignment and checks nothing, matching the controller it came from:
/// tests pass null for dependencies a given path never reaches.
/// </para>
/// </remarks>
public sealed class AnalysisPipeline : IAnalysisPipeline
{
    private const string ProcessingStatus = "Processing";

    private readonly IDocumentRepository              _documentRepository;
    private readonly IAnalysisResultRepository        _analysisResultRepository;
    private readonly IAnalysisRepository              _analysisRepository;
    private readonly IDocumentStorageService          _storageService;
    private readonly IClaudeService                   _claudeService;
    private readonly ICashFlowReconciliationService   _cashFlowReconciliationService;
    private readonly IAlertRatioReconciliationService _alertRatioReconciliationService;
    private readonly IAlertRepository                 _alertRepository;
    private readonly IAuditService                    _auditService;
    private readonly IApiCallLogService               _apiCallLogService;
    private readonly IAlertRuleCatalogueRepository    _alertRuleCatalogueRepository;
    private readonly IAlertRuleEngine                 _alertRuleEngine;
    private readonly IAnalysisCompletionNotifier      _completionNotifier;
    private readonly ILegajoDocumentRepository        _legajoDocumentRepository;
    private readonly IAnalysisRunStateRepository      _analysisRunStateRepository;
    private readonly ILogger<AnalysisPipeline>        _logger;

    // The integration-dependent stages (entity + live BCRA, ratios, score, narrative), shared with the
    // re-score. Built here from the dependencies this constructor already receives, so its signature —
    // and every test that calls it — is unchanged.
    private readonly AnalysisScoringStage             _scoringStage;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisPipeline"/>.
    /// </summary>
    public AnalysisPipeline(
        IDocumentRepository              documentRepository,
        IAnalysisResultRepository        analysisResultRepository,
        IAnalysisRepository              analysisRepository,
        IDocumentStorageService          storageService,
        IClaudeService                   claudeService,
        IBcraService                     bcraService,
        IScoringService                  scoringService,
        IRatioCalculationService         ratioCalculationService,
        ICashFlowReconciliationService   cashFlowReconciliationService,
        IAlertRatioReconciliationService alertRatioReconciliationService,
        IEntityRepository                entityRepository,
        IAlertRepository                 alertRepository,
        IAuditService                    auditService,
        IApiCallLogService               apiCallLogService,
        IAlertRuleCatalogueRepository    alertRuleCatalogueRepository,
        IAlertRuleEngine                 alertRuleEngine,
        IAnalysisCompletionNotifier      completionNotifier,
        ILegajoDocumentRepository        legajoDocumentRepository,
        IAnalysisRunStateRepository      analysisRunStateRepository,
        ILogger<AnalysisPipeline>        logger)
    {
        _documentRepository              = documentRepository;
        _analysisResultRepository        = analysisResultRepository;
        _analysisRepository              = analysisRepository;
        _storageService                  = storageService;
        _claudeService                   = claudeService;
        _cashFlowReconciliationService   = cashFlowReconciliationService;
        _alertRatioReconciliationService = alertRatioReconciliationService;
        _alertRepository                 = alertRepository;
        _auditService                    = auditService;
        _apiCallLogService               = apiCallLogService;
        _alertRuleCatalogueRepository    = alertRuleCatalogueRepository;
        _alertRuleEngine                 = alertRuleEngine;
        _completionNotifier              = completionNotifier;
        _legajoDocumentRepository        = legajoDocumentRepository;
        _analysisRunStateRepository      = analysisRunStateRepository;
        _logger                          = logger;
        _scoringStage                    = new AnalysisScoringStage(
            entityRepository, bcraService, scoringService, ratioCalculationService, claudeService, logger);
    }

    /// <inheritdoc />
    public async Task<AnalysisRunOutcome> RunAsync(AnalysisRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        var tenantId           = run.TenantId;
        var storagePath        = run.StoragePath;
        var documentType       = run.DocumentType;
        var documentFiscalYear = run.DocumentFiscalYear;

        // Terminal-state guard: once "Processing" is committed, any fault below must
        // drive the row to a terminal state, never leave it stranded. Cancellation is
        // exempt ONLY once the server owns the work (see serverOwnsWork): from the Claude
        // call onward the server keeps finishing under CancellationToken.None, so a client
        // disconnect must not clobber it. BEFORE that point a cancellation is a plain
        // abandonment and must drive the row to "Failed" (re-analysable) like any other fault.
        var reachedTerminalState = false;
        var serverOwnsWork       = false;
        // Set once the requester has been told this run failed, so the catch-all below cannot emit
        // a second ANALYSIS_FAILED when the Claude-failure path's own status write is what faulted.
        var failureNotified      = false;
        // Set once the extraction ANSWERED (the document was read, the call was billed), and once its
        // ApiCallLogs row was written. A fault after that point is ours, not the document's: the
        // catch-all spends no attempt for it and writes the billed call's row if nothing else did.
        ClaudeAnalysisResult? extractionReturned = null;
        var extractionDurationMs = 0;
        var extractionCallLogged = false;
        try
        {
            // ── Background only: re-validate the document before anything is spent ────────
            // The checks above the queue ran when the request arrived; a queued run may start much
            // later. Deleted, moved into a legajo, or its entity's consent withdrawn in the meantime
            // → stop here, before the storage read and the Claude call: nothing is charged. A
            // document no longer in "Processing" is no longer this run's to touch at all.
            if (run.IsBackground)
            {
                var current = await _documentRepository
                    .GetByIdAsync(run.DocumentId, tenantId, ct)
                    .ConfigureAwait(false);

                if (current is null
                    || !string.Equals(current.ProcessingStatus, ProcessingStatus, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Background analysis for document {DocumentId} tenant {TenantId} dropped: the document " +
                        "is no longer Processing ({ProcessingStatus}); nothing was sent to the model",
                        run.DocumentId, tenantId, current?.ProcessingStatus ?? "missing");
                    return AnalysisRunOutcome.Abandoned();
                }

                var stopReason = await GetBackgroundStopReasonAsync(current, tenantId, ct).ConfigureAwait(false);
                if (stopReason is not null)
                {
                    await AbandonAsync(run, stopReason).ConfigureAwait(false);
                    return AnalysisRunOutcome.Abandoned();
                }

                // What the run reads is what the document says NOW, not what it said when queued.
                storagePath        = current.StoragePath;
                documentType       = current.DocumentType;
                documentFiscalYear = current.FiscalYear;
            }

            // ── Read file from storage ────────────────────────────────────────────────

            var fileBytes = await _storageService
                .ReadAsync(storagePath, ct)
                .ConfigureAwait(false);

            // ── Unwrap CPCEN oblea (embedded PDF attachment) if present ──────────────
            // AttachmentUnread: the PDF carries the balance as an attachment that could not be
            // opened, so the extraction below reads only the wrapper. Flagged on the result
            // (integrity.extraction.embeddedAttachment + an anomaly), never on the score.

            var unwrap = PdfAttachmentExtractor.Unwrap(
                fileBytes, _logger, run.DocumentId, tenantId);
            fileBytes = unwrap.Bytes;

            // ── Call Claude API (with fault isolation) ────────────────────────────────

            var processingStartedAt = DateTime.UtcNow;

            ClaudeAnalysisResult claudeResult;
            try
            {
                // From here the analysis runs on CancellationToken.None: the server owns the
                // work even if the client has already disconnected, so a cancellation at or
                // beyond this point must be re-thrown untouched, never turned into "Failed".
                serverOwnsWork = true;
                claudeResult = await _claudeService
                    .AnalyzeDocumentAsync(fileBytes, documentType, documentFiscalYear, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ClaudeApiException ex)
            {
                var failureCode = ExtractionFailureCode(ex);

                if (string.Equals(failureCode, AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements, StringComparison.Ordinal))
                {
                    // Not a failure of ours: the file holds no readable balance. One Warning — counts and
                    // the confidence only, never a figure, a name or any text of the document.
                    _logger.LogWarning(
                        "Extraction found no financial statements: tenant {TenantId} document {DocumentId} " +
                        "model {Model} passes {PassesRun} kept_confidence {KeptConfidence:F2}; no analysis created",
                        tenantId, run.DocumentId, ex.Model, ex.ExtractionPassesRun, ex.KeptConfidence);
                }
                else
                {
                    _logger.LogError(ex,
                        "Claude analysis failed for document {DocumentId} tenant {TenantId}",
                        run.DocumentId, tenantId);
                }

                // ── Background only: one automatic retry for a transient failure of ours ─────
                // The provider's 5xx, the network or a 429 say nothing about the document. The run is
                // retried ONCE (AnalysisAutoRetryPolicy) by AnalysisJobWorker, so the document stays in
                // Processing: no Failed, no Analysis.Failed audit row and no ANALYSIS_FAILED now — the
                // retry's own outcome is what the requester hears. What is billed or recorded today still
                // is: the failure code (none of these spends an attempt) and the failed call's ApiCallLogs row.
                if (AnalysisAutoRetryPolicy.IsEligible(run, failureCode))
                {
                    await RecordExtractionFailureAsync(
                        run.DocumentId, tenantId, failureCode,
                        countsAsAttempt: ExtractionFailureCountsAsAttempt(failureCode)).ConfigureAwait(false);

                    LogFailedExtractionCall(run, tenantId, ex, processingStartedAt);
                    if (ex.OrientationDetection is { } pendingOrientation)
                        LogOrientationDetectionCall(tenantId, run.RequestedBy, analysisId: null, run.DocumentId, pendingOrientation);
                    extractionCallLogged = true;

                    _logger.LogWarning(
                        "Background analysis for document {DocumentId} tenant {TenantId} failed with {FailureCode}; " +
                        "the document stays Processing for its one automatic retry",
                        run.DocumentId, tenantId, failureCode);

                    return AnalysisRunOutcome.AutoRetryPending(failureCode, ex.RetryAfterSeconds);
                }

                await _documentRepository
                    .UpdateStatusAsync(run.DocumentId, tenantId, "Failed", DateTime.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);

                // Recorded AFTER the status reset, and guarded, so a failed bookkeeping write can
                // never strand the document in "Processing". The code says what failed
                // (ExtractionFailureCode) and decides whether the file's budget is spent
                // (ExtractionFailureCountsAsAttempt): a failure WE caused — our output cap (max_tokens),
                // the provider's 5xx or 429, the network, our key (401/403), an answer we could not
                // read as JSON at all (claude_unparseable_answer) — is recorded without
                // spending it, so it can never end in the trigger's "subí el archivo en mejor calidad"
                // refusal. A timeout, a refusal or an answer that is not a balance still spend it; a
                // file with no financial statements does not (see ExtractionFailureCountsAsAttempt).
                await RecordExtractionFailureAsync(
                    run.DocumentId, tenantId, failureCode,
                    countsAsAttempt: ExtractionFailureCountsAsAttempt(failureCode)).ConfigureAwait(false);

                // The failed call is a billed call: one ApiCallLogs row (INSERT only), so the spend
                // and the failure are visible beside the successful extractions. No analysis exists,
                // so it is tied to the document.
                LogFailedExtractionCall(run, tenantId, ex, processingStartedAt);
                // The second pass's scan-orientation call, when one ran before the failure: billed too,
                // priced by its own model, tied to the document like the extraction row.
                if (ex.OrientationDetection is { } orientation)
                    LogOrientationDetectionCall(tenantId, run.RequestedBy, analysisId: null, run.DocumentId, orientation);
                extractionCallLogged = true;

                _ = _auditService.LogAsync(
                    tenantId:   tenantId,
                    userId:     run.RequestedBy,
                    action:     AnalysisRunFailureCodes.FailedAuditAction,
                    severity:   AuditSeverities.Warning,
                    entityType: "Document",
                    entityId:   run.DocumentId.ToString(),
                    ipAddress:  run.IpAddress,
                    requestId:  run.RequestId,
                    ct:         CancellationToken.None);

                // Last, after the document is terminal: the analyst may have left the screen long
                // before Claude gave up, so the bell is where they learn the run did not finish.
                failureNotified = true;
                await NotifyAnalysisFailedAsync(tenantId, run.RequestedBy, run)
                    .ConfigureAwait(false);

                return AnalysisRunOutcome.ExtractionFailed(failureCode);
            }

            var processingCompletedAt  = DateTime.UtcNow;
            var processingDurationMs   = (int)(processingCompletedAt - processingStartedAt).TotalMilliseconds;
            extractionReturned   = claudeResult;
            extractionDurationMs = processingDurationMs;

            // The early warning, before a growing balance starts failing at the cap like EL OMBU did.
            LogNearCapPasses(_logger, tenantId, claudeResult);

            // ── Determine validation status ───────────────────────────────────────────

            // The model's self-reported balance_check alone cannot catch two wrong subtotals that still
            // add up to the right total; the deterministic integrity checks can. A failed check turns
            // the flag to Warning. null (older output, or the evaluation failed) keeps the old rule.
            // The same rule a balance correction applies to its re-evaluated integrity.
            var validationStatus = BalanceValidationStatus.Derive(
                BalanceValidationStatus.ModelBalanceCheck(claudeResult.BalanceCheckBalanced, claudeResult.BalanceDifferencePct),
                BalanceValidationStatus.IntegrityEvidence(
                    claudeResult.BalanceIntegrityPassed, claudeResult.BalanceIntegrityFailedChecks,
                    claudeResult.BalanceIntegrityNetProfitSignFlip, claudeResult.BalanceIntegrityNetProfitColumnMix));

            // ── Persist Analysis + AnalysisResult ─────────────────────────────────────

            var analysisId = Guid.NewGuid();

            // ── Fetch entity + live BCRA lookup (fault-isolated) ──────────────────────
            // Shared with the re-score (AnalysisScoringStage), so a fresh analysis and a re-score
            // read the entity and the BCRA the same way. Never throws: a failure on either side is
            // recorded in the enrichment assessment, never re-thrown.

            var enrichmentSnapshot = await _scoringStage
                .ResolveEnrichmentAsync(run.EntityId, tenantId, analysisId, CancellationToken.None)
                .ConfigureAwait(false);

            Entity?   entity        = enrichmentSnapshot.Entity;
            string    entityName    = entity?.BusinessName ?? string.Empty;
            string?   bcraDataJson  = enrichmentSnapshot.BcraDataJson;
            string?   macroDataJson = enrichmentSnapshot.MacroDataJson;
            BcraData? bcraData      = enrichmentSnapshot.BcraData;
            var       enrichment    = enrichmentSnapshot.Enrichment;

            // ── Scoring (fault-isolated) ──────────────────────────────────────────

            decimal? aiScore          = null;
            string?  aiLevel          = null;
            string?  scoringNarrative = null;
            string?  scoreBreakdownJson = null;

            string?  computedRatiosJson    = null;
            string?  augmentedAnomaliesJson = claudeResult.AnomaliesJson;

            // BalanceJson with the cash-flow reconciliation OUTCOME injected under cash_flow (for the
            // Portal to warn on the card). Stays null unless injection runs; applied only at persistence
            // so claudeResult is never mutated and scoring inputs are provably untouched.
            string?  balanceJsonWithReconciliation = null;

            // Alerts JSON with any mislabeled-denominator percentages corrected in place. Stays null
            // unless a correction runs; applied only at persistence (RiskAlerts + the per-row Alert
            // fan-out) so claudeResult is never mutated and scoring inputs are provably untouched.
            string?  reconciledAlertsJson = null;

            // Alerts produced by the deterministic rule engine from the tenant's catalogue — the
            // `alerts` channel, kept strictly separate from the model's own findings (which become
            // `observations`). Stays null when no rule fires, when the catalogue is empty, or when
            // the engine block fails; a null here is indistinguishable, by design, from "no alert"
            // and can never empty out the model's findings.
            string?  catalogueAlertsJson = null;

            // executive_summary with any mislabeled-denominator percentages corrected in place.
            // Stays null unless a correction runs; applied only at persistence, same rationale as
            // reconciledAlertsJson above. Belt-and-suspenders alongside the read-time reconciliation
            // in GetById, which is what actually repairs analyses persisted before this fix.
            string?  reconciledExecutiveSummary = null;

            // Set only when the narrative call reported usage; logged once the Analysis row exists.
            NarrativeOutcome? narrativeUsage = null;

            // ── Fiscal year read vs fiscal year requested (context only — NEVER affects the score) ──
            // A finding about the CLIENT'S document — it states a different ejercicio than the one the
            // analysis was requested for — so it belongs in the anomalies list (the fourth writer, see
            // the invariant note below). The same comparison is exposed, for every analysis, as
            // extractionIntegrity.fiscalYear at read time. Self-contained: a failure here only costs the
            // anomaly, and it runs before the scoring block so a scoring failure cannot skip it.
            try
            {
                var detectedFiscalYear = ReadFiscalYearDetected(claudeResult.BalanceJson);
                var fiscalYearAnomaly  = FiscalYearCheck.Anomaly(run.FiscalYear, detectedFiscalYear);

                if (fiscalYearAnomaly is not null)
                {
                    var anomalies = string.IsNullOrWhiteSpace(augmentedAnomaliesJson)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(augmentedAnomaliesJson, BcraJsonOpts)
                          ?? new List<string>();
                    anomalies.Add(fiscalYearAnomaly);
                    augmentedAnomaliesJson = JsonSerializer.Serialize(anomalies);

                    _logger.LogWarning(
                        "Fiscal year mismatch for analysis {AnalysisId} tenant {TenantId}: requested {RequestedFiscalYear}, " +
                        "detected in the document {DetectedFiscalYear}",
                        analysisId, tenantId, run.FiscalYear, detectedFiscalYear);
                }
            }
            catch (Exception fyEx)
            {
                _logger.LogWarning(fyEx,
                    "Fiscal year check failed for analysis {AnalysisId} tenant {TenantId}; continuing without it",
                    analysisId, tenantId);
            }

            // ── Embedded attachment left unread (context only — NEVER affects the score) ──
            // The uploaded PDF carries the balance as an attachment that could not be opened, so
            // what was extracted is the wrapper (a legalization cover with a handful of totals).
            // A finding about the CLIENT'S document — the fifth anomaly writer, see the invariant
            // note below — plus a provenance key under integrity.extraction for the Portal's
            // extractionIntegrity. Self-contained: a failure here only costs the flag.
            // embeddedAttachmentSource records, provenance only (no anomaly), that the attachment
            // taken — or the one left unread — came from an orphaned file specification. Neither key
            // is written otherwise, so every other BalanceJson is byte-identical.
            if (unwrap.AttachmentUnread || unwrap.FromOrphanedFileSpec)
            {
                try
                {
                    balanceJsonWithReconciliation = EmbeddedAttachmentJson.Mark(
                        claudeResult.BalanceJson, unwrap.AttachmentUnread, unwrap.FromOrphanedFileSpec);

                    if (unwrap.AttachmentUnread)
                    {
                        var anomalies = string.IsNullOrWhiteSpace(augmentedAnomaliesJson)
                            ? new List<string>()
                            : JsonSerializer.Deserialize<List<string>>(augmentedAnomaliesJson, BcraJsonOpts)
                              ?? new List<string>();
                        anomalies.Add(EmbeddedAttachmentJson.UnreadAnomaly);
                        augmentedAnomaliesJson = JsonSerializer.Serialize(anomalies);
                    }
                }
                catch (Exception attachmentEx)
                {
                    _logger.LogWarning(
                        "Embedded-attachment flag could not be recorded for analysis {AnalysisId} tenant {TenantId} " +
                        "({ExceptionType}); continuing without it",
                        analysisId, tenantId, attachmentEx.GetType().Name);
                }
            }

            try
            {
                // ── Deterministic ratio calculation (shared with the re-score) ─────────────
                // Recomputed from the extracted balance, falling back to the model's own ratios;
                // never throws (see AnalysisScoringStage.ComputeRatios).
                var ratiosOutcome  = _scoringStage.ComputeRatios(
                    claudeResult.BalanceJson, claudeResult.RatiosJson, analysisId, tenantId);
                RatiosData ratios  = ratiosOutcome.Ratios;
                computedRatiosJson = ratiosOutcome.ComputedRatiosJson;

                // ── Cash-flow reconciliation (context only — NEVER affects the score) ──────
                // Validates the extracted cash flow statement's internal consistency and, on a
                // divergence, appends a finding about the CLIENT'S document (their cash flow does not
                // internally reconcile) to the anomalies list — which is why it belongs there, unlike
                // the ratio and alert reconciliation notes (comments about our own processing), both
                // removed from that list in this change. This block and the fiscal-year and
                // embedded-attachment checks above it are the only pipeline writers into the anomalies
                // array IN THIS METHOD. Invariant: the
                // anomalies array is written by exactly five sources — the model's own extraction
                // anomalies, this cash-flow reconciliation finding, (M11, appended inside ClaudeService
                // before the array reaches this method) the deterministic balance-integrity findings:
                // figures as extracted that do not reconcile with each other or that coincide with the
                // comparative column, worded neutrally so the analyst verifies them against the
                // document — the fiscal-year finding (the document states another ejercicio than the
                // one requested) — and the embedded-attachment finding (the PDF carries the balance as
                // an attachment that could not be opened, so only its wrapper was read). Nothing our
                // pipeline says about its own processing reaches it. Self-contained
                // try/catch: a failure here can never reach the outer catch, never aborts the analysis,
                // and never touches scoring inputs — it only mutates augmentedAnomaliesJson (not a
                // scoring input).
                //
                // Addendum (Part 2, verifiable-percentages): a later block in this same method may
                // rewrite the TEXT of an anomaly already in this array — correcting a percentage
                // whose stated value contradicts the denominator its own wording names — without
                // adding or removing any entry. That is in-place text correction, not a third
                // writer: it never introduces a new finding, exactly like the alert-description
                // correction elsewhere in this method fixes an alert's wording without adding a
                // new alert. The invariant above is about which sources may ADD an anomaly, not
                // whether an existing one's wording may later be repaired.
                try
                {
                    var cashFlow = TryExtractCashFlow(claudeResult.BalanceJson);

                    // Persist the reconciliation OUTCOME under cash_flow so the Portal can warn on the
                    // card without parsing the Spanish anomaly text or recomputing the tolerance. Runs
                    // whenever a cash_flow node exists (even present:false → both checks not_evaluated).
                    // Additive-only and never touches the score; see balanceJsonWithReconciliation.
                    if (cashFlow is not null)
                    {
                        var reconciliation = _cashFlowReconciliationService.Reconcile(cashFlow);
                        balanceJsonWithReconciliation =
                            // Chained on the embedded-attachment mark when one was set above (null
                            // otherwise, so a normal run injects into the model's blob exactly as before).
                            CashFlowReconciliationJson.Inject(
                                balanceJsonWithReconciliation ?? claudeResult.BalanceJson, reconciliation);
                    }

                    if (cashFlow is { Present: true })
                    {
                        var divergences = _cashFlowReconciliationService.Check(cashFlow);
                        if (divergences.Count > 0)
                        {
                            var cashFlowAnomalies = string.IsNullOrWhiteSpace(augmentedAnomaliesJson)
                                ? new List<string>()
                                : JsonSerializer.Deserialize<List<string>>(augmentedAnomaliesJson, BcraJsonOpts)
                                  ?? new List<string>();

                            foreach (var d in divergences)
                            {
                                // es-AR figures (thousands + 2 decimals), consistent with the rest of the
                                // report; pre-fix persisted rows are reformatted at render time by the PDF shim.
                                string expected = d.Expected.ToString("N2", EsArFormat);
                                string reported = d.Reported.ToString("N2", EsArFormat);
                                var msg = d.Check switch
                                {
                                    CashFlowReconciliationService.SectionsVsNetChange =>
                                        $"El estado de flujo de efectivo no concilia: suma de secciones {expected} " +
                                        $"vs variación informada {reported}. Las cifras se muestran tal como fueron extraídas.",
                                    CashFlowReconciliationService.BeginningPlusChangeVsEnd =>
                                        $"El estado de flujo de efectivo no concilia: efectivo inicial más variación {expected} " +
                                        $"vs efectivo final informado {reported}. Las cifras se muestran tal como fueron extraídas.",
                                    _ =>
                                        $"El estado de flujo de efectivo no concilia ({d.Check}): {expected} vs {reported}. " +
                                        "Las cifras se muestran tal como fueron extraídas."
                                };
                                cashFlowAnomalies.Add(msg);
                                _logger.LogWarning(
                                    "Cash flow reconciliation divergence [{Check}] analysis {AnalysisId} tenant {TenantId}: " +
                                    "expected={Expected} reported={Reported}",
                                    d.Check, analysisId, tenantId, d.Expected, d.Reported);
                            }

                            augmentedAnomaliesJson = JsonSerializer.Serialize(cashFlowAnomalies);
                        }
                    }
                }
                catch (Exception cfEx)
                {
                    _logger.LogWarning(cfEx,
                        "Cash flow reconciliation failed for analysis {AnalysisId} tenant {TenantId}; " +
                        "continuing without cash-flow anomalies",
                        analysisId, tenantId);
                }

                // ── Deterministic alert rule engine (the `alerts` channel) ─────────────────
                // Evaluates the tenant's ENABLED catalogue rules against the extracted balance.
                // The model's own findings are untouched here — they become `observations`, and no
                // deduplication is attempted between the two channels (see AlertRuleEngine).
                //
                // Self-contained try/catch, exactly like the cash-flow block above: a failure here
                // — including a missing AlertRuleCatalogue table on a database that has not yet had
                // DDL 038 applied — logs a warning, leaves catalogueAlertsJson null, and lets the
                // analysis complete with the model's findings fully intact. It never touches a
                // scoring input. Note this protection does NOT extend to the CatalogueAlerts
                // COLUMN: that is mapped in AnalysisResultConfiguration and so is emitted by EF in
                // every AnalysisResult SELECT/INSERT, outside this block. 038 is a hard
                // pre-deployment requirement, not a graceful degradation.
                try
                {
                    var catalogueRules = await _alertRuleCatalogueRepository
                        .GetEnabledAsync(tenantId, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (catalogueRules.Count > 0)
                    {
                        var catalogueAccounts = string.IsNullOrWhiteSpace(claudeResult.BalanceJson)
                            ? null
                            : JsonSerializer.Deserialize<BalanceAccounts>(claudeResult.BalanceJson, BcraJsonOpts);

                        var catalogueCashFlow = TryExtractCashFlow(claudeResult.BalanceJson);

                        var catalogueAlerts = _alertRuleEngine.Evaluate(
                            catalogueRules, catalogueAccounts, catalogueCashFlow);

                        if (catalogueAlerts.Count > 0)
                        {
                            catalogueAlertsJson = JsonSerializer.Serialize(catalogueAlerts);

                            _logger.LogInformation(
                                "Alert rule engine produced {AlertCount} catalogue alerts from {RuleCount} " +
                                "enabled rules for analysis {AnalysisId} tenant {TenantId}: {Codes}",
                                catalogueAlerts.Count, catalogueRules.Count, analysisId, tenantId,
                                string.Join(", ", catalogueAlerts.Select(a => a.Code)));
                        }
                    }
                }
                catch (Exception ruleEx)
                {
                    _logger.LogWarning(ruleEx,
                        "Alert rule engine failed for analysis {AnalysisId} tenant {TenantId}; " +
                        "continuing with the model's findings only",
                        analysisId, tenantId);
                }

                // ── Alert/narrative/anomaly percentage reconciliation (presentation only — NEVER affects the score) ──
                // The model authors each alert's description verbatim, so a percentage may be computed
                // against one denominator (e.g. equity) yet labeled as another (e.g. "del total del
                // pasivo"). Re-derive the balance figures (same source the ratio block reads) and, on a
                // proven mismatch, rewrite ONLY the number so it agrees with its own label. The
                // correction is applied to the alert text and LOGGED at Information level; it is NOT
                // surfaced as an anomaly — that section is reserved for findings about the client's
                // document, not notes about our own processing. Self-contained try/catch: a failure
                // here never aborts the analysis and never touches scoring inputs.
                // A third sub-block below applies this exact fix, in place, to each existing
                // anomaly string's wording too — see the addendum on the anomaly-writer invariant above.
                try
                {
                    // InPrintedUnits: the prose quotes amounts as printed, BalanceJson persists pesos.
                    var alertAccounts = string.IsNullOrWhiteSpace(claudeResult.BalanceJson)
                        ? null
                        : JsonSerializer.Deserialize<BalanceAccounts>(claudeResult.BalanceJson, BcraJsonOpts)?.InPrintedUnits();

                    if (alertAccounts is not null && !string.IsNullOrWhiteSpace(claudeResult.AlertsJson))
                    {
                        var alertList = JsonSerializer.Deserialize<List<ClaudeAlertJson>>(
                            claudeResult.AlertsJson, BcraJsonOpts);

                        if (alertList is { Count: > 0 })
                        {
                            var anyCorrected = false;

                            foreach (var al in alertList)
                            {
                                if (string.IsNullOrWhiteSpace(al.Description)) continue;

                                var rec = _alertRatioReconciliationService.Reconcile(al.Description, alertAccounts);
                                if (!rec.Corrected) continue;

                                al.Description = rec.Description;
                                anyCorrected = true;
                                _logger.LogWarning(
                                    "Alert percentage reconciled [{Code}] analysis {AnalysisId} tenant {TenantId}: " +
                                    "clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                    al.Code, analysisId, tenantId,
                                    rec.Clause, rec.StatedPercent, rec.ExpectedPercent, rec.Denominator);
                            }

                            // The corrected alert text is persisted (RiskAlerts); the correction is NOT
                            // appended to the anomalies array — see the block comment above.
                            if (anyCorrected)
                            {
                                reconciledAlertsJson = JsonSerializer.Serialize(alertList, BcraJsonOpts);
                            }
                        }
                    }

                    // Same fix, generalised to every percentage clause in the executive summary's
                    // free-form prose (not just the single-clause alert shape above).
                    if (alertAccounts is not null && !string.IsNullOrWhiteSpace(claudeResult.ExecutiveSummary))
                    {
                        var narrative = _alertRatioReconciliationService.ReconcileNarrative(
                            claudeResult.ExecutiveSummary, alertAccounts);

                        if (narrative.Corrections.Count > 0)
                        {
                            reconciledExecutiveSummary = narrative.Text;
                            foreach (var c in narrative.Corrections)
                                _logger.LogWarning(
                                    "Executive summary percentage reconciled analysis {AnalysisId} tenant {TenantId}: " +
                                    "clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                    analysisId, tenantId, c.Clause, c.StatedPercent, c.ExpectedPercent, c.Denominator);
                        }

                        if (narrative.UnresolvedClauses.Count > 0)
                            _logger.LogWarning(
                                "Executive summary has {Count} unverifiable percentage clause(s) analysis " +
                                "{AnalysisId} tenant {TenantId}: {Clauses}",
                                narrative.UnresolvedClauses.Count, analysisId, tenantId,
                                string.Join(" | ", narrative.UnresolvedClauses));
                    }

                    // Same fix, generalised to the anomalies array: each entry is a free-form
                    // plain-string finding (see the anomaly-writer invariant note above) and may itself
                    // contain a mislabeled-denominator percentage clause. Rewriting an existing
                    // entry's TEXT in place is not a third writer into the array — no entry is
                    // added or removed, only its wording is corrected, exactly like the alert-text
                    // correction above.
                    if (alertAccounts is not null && !string.IsNullOrWhiteSpace(augmentedAnomaliesJson))
                    {
                        var anomalyList = JsonSerializer.Deserialize<List<string>>(
                            augmentedAnomaliesJson, BcraJsonOpts);

                        if (anomalyList is { Count: > 0 })
                        {
                            var anyAnomalyCorrected = false;
                            var allUnresolved       = new List<string>();

                            for (int i = 0; i < anomalyList.Count; i++)
                            {
                                if (string.IsNullOrWhiteSpace(anomalyList[i])) continue;

                                var anomalyNarrative = _alertRatioReconciliationService.ReconcileNarrative(
                                    anomalyList[i], alertAccounts);

                                if (anomalyNarrative.Corrections.Count > 0)
                                {
                                    anomalyList[i] = anomalyNarrative.Text;
                                    anyAnomalyCorrected = true;
                                    foreach (var c in anomalyNarrative.Corrections)
                                        _logger.LogWarning(
                                            "Anomaly percentage reconciled analysis {AnalysisId} tenant {TenantId}: " +
                                            "clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                            analysisId, tenantId, c.Clause, c.StatedPercent, c.ExpectedPercent, c.Denominator);
                                }

                                if (anomalyNarrative.UnresolvedClauses.Count > 0)
                                    allUnresolved.AddRange(anomalyNarrative.UnresolvedClauses);
                            }

                            if (anyAnomalyCorrected)
                                augmentedAnomaliesJson = JsonSerializer.Serialize(anomalyList, BcraJsonOpts);

                            if (allUnresolved.Count > 0)
                                _logger.LogWarning(
                                    "Anomalies have {Count} unverifiable percentage clause(s) analysis {AnalysisId} " +
                                    "tenant {TenantId}: {Clauses}",
                                    allUnresolved.Count, analysisId, tenantId,
                                    string.Join(" | ", allUnresolved));
                        }
                    }
                }
                catch (Exception alertRecEx)
                {
                    _logger.LogWarning(alertRecEx,
                        "Alert/executive-summary/anomaly percentage reconciliation failed for analysis {AnalysisId} " +
                        "tenant {TenantId}; continuing with the model's original text",
                        analysisId, tenantId);
                }

                // ── Score, breakdown and narrative (shared with the re-score) ──────────────
                // ScoreAsync reads the LUFE sales series and the CASFOG guarantee rows off the SAME
                // in-memory entity blobs that are frozen into this result below, so the score and the
                // evidence stored beside it can never disagree. It throws when the engine does; the
                // catch below then persists the analysis without a score, exactly as before.
                // The reliability facts read the integrity verdict off the SAME balance this result
                // persists (the injections add cash_flow.reconciliation and,
                // when an embedded attachment was unread or came from an orphaned file specification,
                // integrity.extraction.embeddedAttachment / embeddedAttachmentSource — nothing else).
                var scored = await _scoringStage
                    .ScoreAsync(
                        tenantId, analysisId, entity, ratiosOutcome, bcraData,
                        balanceJsonWithReconciliation ?? claudeResult.BalanceJson, run.FiscalYear,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                aiScore            = scored.Scoring.Score;
                aiLevel            = scored.Scoring.Level;
                scoreBreakdownJson = scored.ScoreBreakdownJson;

                // A failed cheques lookup is a degraded enrichment only when the penalty counted in
                // THIS score — known now that the engine has read the tenant's config. With no score
                // (the catch below) nothing could have been penalised, so the deuda verdict stands.
                enrichment = AnalysisScoringStage.AssessAfterScoring(enrichmentSnapshot, scored.Scoring);
                if (enrichment.Outcome == BcraEnrichmentOutcome.Degraded
                    && enrichmentSnapshot.Enrichment.Outcome != BcraEnrichmentOutcome.Degraded)
                {
                    _logger.LogWarning(
                        "BCRA enrichment degraded for analysis {AnalysisId} tenant {TenantId}: {EnrichmentNotes}",
                        analysisId, tenantId, enrichment.Notes);
                }

                // The verdict the narrative states is the one this row will carry: the assessed enrichment,
                // this breakdown, the requested fiscal year, and now as the score's instant (GeneratedAt is
                // stamped a few statements below, in the same month but for a seconds-wide edge).
                var narrativeResult = await _scoringStage
                    .GenerateNarrativeAsync(
                        scored, ratios, bcraData, enrichment, run.FiscalYear, DateTime.UtcNow, rescoredAtUtc: null,
                        analysisId, tenantId, CancellationToken.None)
                    .ConfigureAwait(false);

                scoringNarrative = narrativeResult.Text;

                // Capture narrative usage for logging after Analysis row exists
                if (narrativeResult.HasUsage)
                    narrativeUsage = narrativeResult;

                _logger.LogInformation(
                    "Scoring completed for analysis {AnalysisId} tenant {TenantId}: " +
                    "score={Score} level={Level}",
                    analysisId, tenantId, aiScore, aiLevel);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Scoring failed for analysis {AnalysisId} tenant {TenantId}; continuing without score",
                    analysisId, tenantId);
            }

            var analysis   = new Analysis
            {
                AnalysisId            = analysisId,
                TenantId              = tenantId,
                EntityId              = run.EntityId,
                DocumentId            = run.DocumentId,
                RequestedBy           = run.RequestedBy,
                RequestedAmount       = run.RequestedAmount,
                Status                = "PendingReview",
                FiscalYear            = run.FiscalYear,
                AiModel               = claudeResult.ModelUsed,
                // Fixed by CK_Analyses_AiTemperature (= 0.00, NOT NULL; DDL 001). For a model whose
                // catalogue entry omits temperature (Sonnet 5.x) no temperature was sent and the API
                // default applied: read 0.00 as "no sampling override / not applicable" and AiModel as
                // what tells the cases apart. Recording the real value needs a DDL (follow-up).
                AiTemperature         = 0.00m,
                PromptVersion         = claudeResult.PromptVersion,
                SentToSGR             = false,
                ProcessingStartedAt   = processingStartedAt,
                ProcessingCompletedAt = processingCompletedAt,
                ProcessingDurationMs  = processingDurationMs,
                // The quota counts Analyses by CreatedAt. A background run is billed in the month
                // whose quota gate admitted it, not in the month a queue happened to start it in;
                // ProcessingStartedAt above stays the real start. Synchronous runs are unchanged.
                CreatedAt             = run.IsBackground ? run.AcceptedAtUtc : processingStartedAt
            };
            await _analysisRepository.CreateAsync(analysis, CancellationToken.None)
                .ConfigureAwait(false);

            // ── Log the extraction call (fire-and-forget, after Analysis row exists) ─────
            // And the scan-orientation call of the second pass, when one was made: its own row, priced
            // with ITS model (claude-haiku-4-5), never folded into the extraction's Sonnet tokens.
            // Operation Extraction: it is part of reading the balance, and an existing
            // CK_ApiCallLogs_Operation value — no DDL.
            LogSuccessfulExtractionCalls(run, tenantId, claudeResult, processingDurationMs, analysisId);
            extractionCallLogged = true;

            // ── Log narrative call (fire-and-forget, after Analysis row exists) ──────
            if (narrativeUsage is not null)
                AnalysisScoringStage.LogNarrativeCall(
                    _apiCallLogService, tenantId, run.RequestedBy, analysisId, narrativeUsage);

            if (scoreBreakdownJson is null)
                _logger.LogWarning(
                    "ScoreBreakdownJson is null for analysis {AnalysisId} tenant {TenantId}; " +
                    "scoring block may have failed silently",
                    analysisId, tenantId);

            var now    = DateTime.UtcNow;
            var result = new AnalysisResult
            {
                ResultId           = Guid.NewGuid(),
                AnalysisId         = analysisId,
                TenantId           = tenantId,
                RatiosJson         = computedRatiosJson ?? claudeResult.RatiosJson,
                BalanceJson        = balanceJsonWithReconciliation ?? claudeResult.BalanceJson,
                BcraDataJson       = bcraDataJson,
                MacroDataJson      = macroDataJson,
                RiskAlerts         = reconciledAlertsJson ?? claudeResult.AlertsJson,
                CatalogueAlerts    = catalogueAlertsJson,
                Anomalies          = augmentedAnomaliesJson,
                ExecutiveSummary   = reconciledExecutiveSummary ?? claudeResult.ExecutiveSummary,
                ScoringNarrative   = scoringNarrative,
                AiSuggestedScore   = aiScore,
                AiSuggestedLevel   = aiLevel,
                ScoreBreakdownJson = scoreBreakdownJson,
                // Point-in-time snapshot of the external-source context: copy what the
                // already-loaded entity holds in memory (no new external calls). Write-once —
                // never re-synced from the live entity afterwards. entity may be null here
                // (fault-isolated lookup above), in which case the copied snapshots stay null.
                LufeDataJson       = entity?.LufeDataJson,
                FiscalDataJson     = entity?.FiscalDataJson,
                NosisDataJson      = entity?.NosisDataJson,
                CasfogDataJson     = entity?.CasfogDataJson,
                AiInputHash        = claudeResult.InputHash,
                AiOutputHash       = claudeResult.OutputHash,
                AiInputTokens      = claudeResult.InputTokens,
                AiOutputTokens     = claudeResult.OutputTokens,
                ValidationStatus   = validationStatus,
                // Provenance of the external-source enrichment, NOT a data-quality verdict — kept
                // separate from ValidationStatus (balance-sheet arithmetic) on purpose. Note this
                // can legitimately disagree with scoreBreakdownJson's bcraExclusionReason: an
                // entity with no CUIT is NotApplicable here while the scorer, which only ever saw
                // a null payload, records "NoBcraData". Each answers a different question.
                EnrichmentStatus   = enrichment.Outcome.ToString(),
                EnrichmentNotes    = enrichment.Notes,
                GeneratedAt        = now
            };

            await _analysisResultRepository.CreateAsync(result, CancellationToken.None)
                .ConfigureAwait(false);

            // ── Persist alerts as individual rows (fault-isolated) ─────────────────
            // The model's answer about alerts is either KNOWN (a JSON array, possibly empty) or
            // UNKNOWN (null, blank, the literal null, or unreadable JSON). A known answer supersedes
            // the entity's previous active alerts — an EMPTY list included: "this balance raises no
            // alerts" is a real answer, and leaving the previous analysis's alerts active would keep
            // findings the latest analysis no longer makes. An unknown answer never supersedes:
            // "unknown" is not "none", and resolving alerts on a parse failure would hide them.
            try
            {
                var alertsJsonToPersist = reconciledAlertsJson ?? claudeResult.AlertsJson;

                if (!TryReadModelAlerts(alertsJsonToPersist, out var parsedAlerts, out var unreadable))
                {
                    if (unreadable)
                        _logger.LogWarning(
                            "Alerts JSON unreadable for entity {EntityId} analysis {AnalysisId} tenant {TenantId} " +
                            "(length {AlertsJsonLength}); previous alerts left active, no alerts persisted",
                            run.EntityId, analysisId, tenantId, alertsJsonToPersist?.Length ?? 0);
                }
                else
                {
                    // Supersede the entity's previous active alerts BEFORE inserting the new ones,
                    // so a re-analyzed entity does not accumulate duplicates across analyses. Old
                    // alerts move to "Resolved" (history preserved); only the latest analysis stays
                    // active. Fault-isolated inside the helper: a failure never fails the analysis
                    // and never stops the new alerts from being created.
                    await SupersedePreviousAlertsAsync(run.EntityId, tenantId, analysisId, parsedAlerts.Count)
                        .ConfigureAwait(false);

                    if (parsedAlerts.Count == 0)
                    {
                        _logger.LogInformation(
                            "Model reported no alerts for entity {EntityId} analysis {AnalysisId} tenant {TenantId}",
                            run.EntityId, analysisId, tenantId);
                    }
                    else
                    {
                        var alertEntities = parsedAlerts.Select(a => new Alert
                        {
                            AlertId        = Guid.NewGuid(),
                            TenantId       = tenantId,
                            EntityId       = run.EntityId,
                            AnalysisId     = analysisId,
                            AlertType      = a.Code ?? string.Empty,
                            Severity       = NormalizeSeverity(a.Level),
                            Title          = ResolveAlertTitle(a.Title, a.Code),
                            Description    = a.Description ?? string.Empty,
                            // Claude alerts are narrative (level/code/description), not metric-based.
                            // dbo.Alerts requires MetricKey/MetricValue/ThresholdValue NOT NULL, so
                            // supply neutral placeholders. TODO: when the schema is revised, make these
                            // columns NULL and store null here to distinguish "no metric" from a real zero.
                            MetricKey      = string.Empty,
                            MetricValue    = 0m,
                            ThresholdValue = 0m,
                            IsRead         = false,
                            IsResolved     = false,
                            CreatedAt      = DateTime.UtcNow
                        }).ToList();

                        await _alertRepository
                            .CreateManyAsync(alertEntities, CancellationToken.None)
                            .ConfigureAwait(false);

                        _logger.LogInformation(
                            "Persisted {AlertCount} alerts for analysis {AnalysisId} tenant {TenantId}",
                            alertEntities.Count, analysisId, tenantId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Alert persistence failed for analysis {AnalysisId} tenant {TenantId}; " +
                    "continuing without persisted alerts",
                    analysisId, tenantId);
            }

            // ── Set status to Completed ───────────────────────────────────────────────

            await _documentRepository
                .UpdateStatusAsync(run.DocumentId, tenantId, "Completed", now, CancellationToken.None)
                .ConfigureAwait(false);
            reachedTerminalState = true;

            // ── Audit ─────────────────────────────────────────────────────────────────

            _ = _auditService.LogAsync(
                tenantId:   tenantId,
                userId:     run.RequestedBy,
                action:     "Analysis.Completed",
                severity:   "Info",
                entityType: "Document",
                entityId:   run.DocumentId.ToString(),
                ipAddress:  run.IpAddress,
                requestId:  run.RequestId,
                ct:         CancellationToken.None);

            _logger.LogInformation(
                "Analysis completed: {ResultId} for document {DocumentId} tenant {TenantId} " +
                "status={ValidationStatus} confidence={Confidence:F2}",
                result.ResultId, run.DocumentId, tenantId,
                validationStatus, claudeResult.ConfidenceScore);

            // ── Tell the requester it is ready ────────────────────────────────────────
            // On every completion, not only after a disconnect: the server cannot tell whether the
            // browser is still waiting, and the bell doubles as a record. Placed before the response
            // is built so a fault in MapToResponse cannot cost the analyst the notification. The
            // score is the one already in hand — no extra query for the copy.
            await NotifyAnalysisCompletedAsync(
                tenantId, run.RequestedBy, analysisId, result.ResultId,
                entityName, analysis.FiscalYear, aiScore).ConfigureAwait(false);

            return AnalysisRunOutcome.Completed(analysis, result, entityName);
        }
        catch (OperationCanceledException) when (serverOwnsWork && !run.IsBackground)
        {
            // Cancellation AFTER the server took ownership (the Claude call onward, all on
            // CancellationToken.None): a client disconnect must not clobber in-flight or
            // completed work, so re-throw untouched and never mark the document Failed.
            // (TaskCanceledException derives from OperationCanceledException.)
            // A cancellation BEFORE this point is a plain abandonment: this filter is false,
            // so it falls through to the Failed path below like any other pre-Claude fault.
            // A BACKGROUND run has no client to disconnect: the filter is false there too, so the
            // document always ends Failed and the requester is told, instead of waiting on the sweep.
            throw;
        }
        catch (Exception ex) when (!reachedTerminalState)
        {
            _logger.LogError(ex,
                "Analysis pipeline faulted for document {DocumentId} tenant {TenantId}; " +
                "forcing terminal 'Failed' state to avoid stranding.",
                run.DocumentId, tenantId);

            // Best-effort reset; itself guarded and on CancellationToken.None so it can
            // neither be cancelled nor mask the original exception.
            try
            {
                await _documentRepository
                    .UpdateStatusAsync(run.DocumentId, tenantId, "Failed", DateTime.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception resetEx)
            {
                _logger.LogError(resetEx,
                    "Could not reset ProcessingStatus to 'Failed' for document {DocumentId} tenant {TenantId}.",
                    run.DocumentId, tenantId);
            }

            // Only a fault at or after the Claude call can have cost money — serverOwnsWork is set
            // on the line before it. A storage read or a PDF unwrap that blew up spent nothing, and
            // recording it against the file's extraction budget would lock an analyst out of a
            // perfectly readable balance over an infrastructure blip.
            //
            // InvalidOperationException is excluded for the same reason, one step further in:
            // this codebase throws it for missing required configuration (ClaudeService raises it
            // when Anthropic:ApiKey is absent), which happens before any request leaves the
            // process. A deployment missing its key would otherwise quietly burn every document's
            // budget while spending nothing, and those charges would outlive the fix.
            //
            // Of what is left, only an unexplained fault DURING the extraction still spends an
            // attempt (whether a request went out is unknown, and an endless loop must stay
            // impossible). An interruption (a background run cut off by shutdown) and any fault
            // after the extraction answered are ours: recorded, not charged. Every one of them
            // leaves the billed (or possibly billed) call its ApiCallLogs row, tied to the document.
            var interrupted = ex is OperationCanceledException;
            if (serverOwnsWork && ex is not InvalidOperationException)
            {
                await RecordExtractionFailureAsync(
                    run.DocumentId, tenantId, ex.GetType().Name,
                    countsAsAttempt: !interrupted && extractionReturned is null).ConfigureAwait(false);

                if (extractionReturned is null && !extractionCallLogged)
                {
                    LogUnfinishedExtractionCall(run, tenantId,
                        interrupted ? InterruptedCallMessage : $"{PipelineFaultCallMessagePrefix}{ex.GetType().Name}");
                }
            }

            // An extraction that answered was billed whatever failed after it — even a fault the
            // budget ignores (InvalidOperationException) — so its row is written if nothing else did.
            if (extractionReturned is not null && !extractionCallLogged)
                LogSuccessfulExtractionCalls(run, tenantId, extractionReturned, extractionDurationMs, analysisId: null);

            // Only once the server owned the work: a fault before the Claude call surfaces to a
            // caller who is still on the screen within seconds and sees the error there. A
            // BACKGROUND run has no caller on the screen — the 202 went out long ago — so there
            // every fault reaches the bell.
            if ((serverOwnsWork || run.IsBackground) && !failureNotified)
            {
                await NotifyAnalysisFailedAsync(tenantId, run.RequestedBy, run)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Emits the "analysis ready" notification for one completed run.
    /// </summary>
    /// <remarks>
    /// AWAITED, not fire-and-forget like the audit call: the notifier writes through the
    /// request-scoped FinanciaDbContext, so discarding the task would race that context's disposal.
    /// The notifier already swallows its own failures; this guard covers the rest, so a notification
    /// can never change the analysis outcome or the response. Always on
    /// <see cref="CancellationToken.None"/> — the client may already be gone.
    /// </remarks>
    private async Task NotifyAnalysisCompletedAsync(
        string tenantId, Guid userId, Guid analysisId, Guid resultId,
        string? entityName, int fiscalYear, decimal? score)
    {
        try
        {
            await _completionNotifier.NotifyCompletedAsync(
                tenantId:   tenantId,
                userId:     userId,
                analysisId: analysisId,
                resultId:   resultId,
                entityName: string.IsNullOrWhiteSpace(entityName) ? null : entityName,
                fiscalYear: fiscalYear,
                score:      score,
                ct:         CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(
                notifyEx,
                "Failed to emit the completion notification for analysis {AnalysisId} tenant {TenantId}. The analysis was persisted and the response is unaffected.",
                analysisId, tenantId);
        }
    }

    /// <summary>
    /// Emits the "analysis failed" notification for one run that faulted after the server owned it.
    /// </summary>
    /// <remarks>
    /// Same guard and token as <see cref="NotifyAnalysisCompletedAsync"/>. On the catch-all path it
    /// runs before the rethrow, so swallowing here is what keeps it from masking the original fault.
    /// </remarks>
    private async Task NotifyAnalysisFailedAsync(
        string tenantId, Guid userId, AnalysisRun run)
    {
        try
        {
            await _completionNotifier.NotifyFailedAsync(
                tenantId:   tenantId,
                userId:     userId,
                documentId: run.DocumentId,
                entityId:   run.EntityId,
                ct:         CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(
                notifyEx,
                "Failed to emit the failure notification for document {DocumentId} tenant {TenantId}. The document state is unaffected.",
                run.DocumentId, tenantId);
        }
    }

    /// <summary>
    /// Records one failed extraction attempt against the document, consuming a unit of the
    /// balance file's extraction budget.
    ///
    /// <para>
    /// Bookkeeping, never a failure path of its own: it swallows and logs its own exceptions so a
    /// write that fails can neither mask the original fault nor leave a document stranded in
    /// "Processing". Always on <see cref="CancellationToken.None"/> — a client that has already
    /// disconnected must not be able to skip the charge for spend that happened.
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// A short sanitized reason CODE. Never an exception message and never any part of a Claude
    /// payload: <c>Documents.LastExtractionError</c> must carry no PII and no document contents.
    /// </param>
    /// <param name="countsAsAttempt">
    /// False only for a failure this project caused itself (<see cref="ExtractionFailureCountsAsAttempt"/>):
    /// the reason and the time are still recorded, the budget is not spent.
    /// </param>
    private async Task RecordExtractionFailureAsync(Guid documentId, string tenantId, string reason, bool countsAsAttempt = true)
    {
        try
        {
            await _documentRepository
                .RecordExtractionFailureAsync(
                    documentId, tenantId, DateTime.UtcNow, reason, countsAsAttempt, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failing open: an unrecorded attempt means the analyst keeps a retry they should
            // have spent, which is the same direction every other billing fallback here leans.
            _logger.LogWarning(ex,
                "Could not record the failed extraction attempt for document {DocumentId} tenant {TenantId}; " +
                "the extraction budget was not charged for this attempt.",
                documentId, tenantId);
        }
    }

    /// <summary>
    /// The <c>Documents.LastExtractionError</c> code of a failed model call:
    /// <see cref="AnalysisRunFailureCodes.StoredClaudeRateLimited"/> for an HTTP 429,
    /// <see cref="AnalysisRunFailureCodes.StoredClaudeMaxTokens"/> for an answer that stopped at
    /// <c>max_tokens</c>, else <see cref="AnalysisRunFailureCodes.StoredClaudeApiError"/>. Never the
    /// exception's text.
    /// </summary>
    internal static string ExtractionFailureCode(ClaudeApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // The HTTP status first: an escalated call that failed at the transport keeps its status.
        switch (ex.ApiStatusCode)
        {
            case 429:
                return AnalysisRunFailureCodes.StoredClaudeRateLimited;
            case 401 or 403:
                return AnalysisRunFailureCodes.StoredClaudeAuthError;
            case >= 500:
                return AnalysisRunFailureCodes.StoredClaudeServerError;
        }

        switch (ex.FailureCode)
        {
            case ClaudeService.AnswerMaxTokens:
                return AnalysisRunFailureCodes.StoredClaudeMaxTokens;
            case ClaudeService.AnswerRefusal:
                return AnalysisRunFailureCodes.StoredClaudeRefusal;
            case ClaudeService.AnswerSchemaInvalid or ClaudeService.AnswerEmpty:
                return AnalysisRunFailureCodes.StoredClaudeUnreadableAnswer;
            case ClaudeService.AnswerUnparseable:
                return AnalysisRunFailureCodes.StoredClaudeUnparseableAnswer;
            case ClaudeService.AnswerNoFinancialStatements:
                return AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements;
        }

        // No status and no answer: the transport gave up. ClaudeService wraps the last attempt's
        // exception — a TaskCanceledException for the per-attempt timeout, an HttpRequestException
        // for the network.
        if (ex.ApiStatusCode is null)
        {
            if (ex.InnerException is OperationCanceledException)
                return AnalysisRunFailureCodes.StoredClaudeTimeout;
            if (ex.InnerException is HttpRequestException)
                return AnalysisRunFailureCodes.StoredClaudeNetworkError;
        }

        return AnalysisRunFailureCodes.StoredClaudeApiError;
    }

    /// <summary>
    /// One structured Warning per extraction pass that answered with at least
    /// <see cref="ExtractionModelCatalog.NearCapWarningRatio"/> of the <c>max_tokens</c> it was sent with
    /// (<see cref="ClaudeAnalysisResult.ExtractionPasses"/>): the tenant, the model, the cap, the output
    /// tokens and the share. No document name and no figure. Silent below the threshold.
    /// </summary>
    internal static void LogNearCapPasses(ILogger logger, string tenantId, ClaudeAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var pass in result.ExtractionPasses)
        {
            if (!ExtractionModelCatalog.IsNearCap(pass.OutputTokens, pass.MaxTokens))
                continue;

            logger.LogWarning(
                "Claude extraction near max_tokens: tenant {TenantId} model {Model} max_tokens {MaxTokens} " +
                "output_tokens {OutputTokens} pct {Pct:F1} escalated {Escalated}",
                tenantId, pass.Model, pass.MaxTokens, pass.OutputTokens,
                100d * pass.OutputTokens / pass.MaxTokens, pass.Escalated);
        }
    }

    /// <summary>
    /// Whether a failed extraction with <paramref name="failureCode"/> spends one unit of the file's
    /// <c>Billing:MaxExtractionAttemptsPerDocument</c> budget. A failure this project or its provider
    /// caused does NOT: our own <c>max_tokens</c> cap, the provider's 5xx or 429, the network, and our
    /// key (401/403) say nothing about the document, and a re-upload "in better quality" would not
    /// change them; nor does an answer we could not read as JSON at all (<c>claude_unparseable_answer</c>:
    /// our reading, not the document). A timeout (still billed), a refusal, an answer that is not a
    /// balance and any other 4xx still count, so an endless loop stays impossible.
    /// <para>
    /// Nor does a file with no financial statements (<c>claude_no_financial_statements</c>: a blank page, a
    /// CPCE legalization cover whose balance was not readable). The file comes from LUFE as it is — the
    /// analyst cannot get a different one there — and the budget is per file hash, so counting it would
    /// lock that file for good, even after a later fix reads it (e.g. a cover whose embedded balance a
    /// newer unwrap opens). No Analysis row and no score are created, so no monthly quota is spent either;
    /// the ApiCallLogs row keeps the spend visible.
    /// </para>
    /// </summary>
    internal static bool ExtractionFailureCountsAsAttempt(string failureCode)
        => !NonChargedFailureCodes.Contains(failureCode);

    private static readonly HashSet<string> NonChargedFailureCodes = new(StringComparer.Ordinal)
    {
        AnalysisRunFailureCodes.StoredClaudeMaxTokens,
        AnalysisRunFailureCodes.StoredClaudeRateLimited,
        AnalysisRunFailureCodes.StoredClaudeServerError,
        AnalysisRunFailureCodes.StoredClaudeNetworkError,
        AnalysisRunFailureCodes.StoredClaudeAuthError,
        AnalysisRunFailureCodes.StoredClaudeUnparseableAnswer,
        AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements,
    };

    // ApiCallLogs.ErrorMessage of an extraction the pipeline could not finish (text this project writes).
    internal const string InterruptedCallMessage          = "interrupted";
    internal const string PipelineFaultCallMessagePrefix  = "pipeline_fault: ";

    /// <summary>
    /// <c>fiscal_year_detected</c> from the extraction's BalanceJson, or null when absent, not a whole
    /// number, or the blob does not parse.
    /// </summary>
    internal static int? ReadFiscalYearDetected(string? balanceJson)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(balanceJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("fiscal_year_detected", out var year)
                   && year.ValueKind == JsonValueKind.Number
                   && year.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the ApiCallLogs row of the second pass's scan-orientation call (append-only INSERT,
    /// fire-and-forget like the extraction row): <c>Operation = Extraction</c>, its own model and tokens,
    /// its cost by <see cref="ClaudeModelPricing.EstimateCostUsd"/> for that model (null when no response
    /// came back), and its error CODE (text this project writes) when it failed — followed, for an HTTP
    /// error, by the API's parsed error type, clamped message and request id. Never throws.
    /// </summary>
    private void LogOrientationDetectionCall(
        string tenantId, Guid userId, Guid? analysisId, Guid? documentId, ScanOrientationUsage orientation)
    {
        try
        {
            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          "Claude",
                endpoint:         "/v1/messages",
                responseStatus:   orientation.HttpStatus,
                inputTokens:      orientation.InputTokens,
                outputTokens:     orientation.OutputTokens,
                // No response (timeout, network error): what was billed is unknown — null, not zero.
                estimatedCostUsd: orientation.HttpStatus is null
                    ? null
                    : ClaudeModelPricing.EstimateCostUsd(orientation.Model, orientation.InputTokens, orientation.OutputTokens),
                isSuccess:        orientation.Succeeded,
                errorMessage:     AnthropicErrorDiagnostics.WithDetail(orientation.ErrorCode, orientation.ErrorDetail),
                durationMs:       orientation.DurationMs,
                operation:        ApiCallLogOperations.Extraction,
                model:            orientation.Model,
                userId:           userId,
                analysisId:       analysisId,
                documentId:       documentId,
                ct:               CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx,
                "Could not log the scan-orientation call for analysis {AnalysisId} tenant {TenantId}.",
                analysisId, tenantId);
        }
    }

    /// <summary>
    /// Writes the ApiCallLogs rows of an extraction that ANSWERED (append-only INSERTs, fire-and-forget):
    /// the extraction row (<c>IsSuccess = true</c>, its tokens and cost) and, when the second pass asked
    /// for one, the scan-orientation row. With <paramref name="analysisId"/> the rows belong to the new
    /// analysis, as they always have; with null — a fault after the extraction answered, before the
    /// analysis row existed — they are tied to the document instead, so the billed call is never lost.
    /// Never throws.
    /// </summary>
    private void LogSuccessfulExtractionCalls(
        AnalysisRun run, string tenantId, ClaudeAnalysisResult result, int durationMs, Guid? analysisId)
    {
        var documentId = analysisId is null ? run.DocumentId : (Guid?)null;
        try
        {
            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          "Claude",
                endpoint:         "/v1/messages",
                responseStatus:   200,
                inputTokens:      result.InputTokens,
                outputTokens:     result.OutputTokens,
                // Priced by the model that actually ran (Anthropic:ExtractionModel), not a hardcoded
                // Sonnet 4.6 rate. For claude-sonnet-4-6 this is the same figure as before: (in×3 + out×15)
                // / 1e6 is already exact to the 6 decimals EstimateCostUsd rounds to. Every extraction
                // model is priced (LegajoCatalogueTests), so null only means an id nothing can send.
                estimatedCostUsd: ClaudeModelPricing.EstimateCostUsd(result.ModelUsed, result.InputTokens, result.OutputTokens),
                isSuccess:        true,
                errorMessage:     null,
                durationMs:       durationMs,
                operation:        ApiCallLogOperations.Extraction,
                model:            result.ModelUsed,
                userId:           run.RequestedBy,
                analysisId:       analysisId,
                documentId:       documentId,
                ct:               CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx,
                "Could not log the extraction call for document {DocumentId} tenant {TenantId}.",
                run.DocumentId, tenantId);
        }

        if (result.OrientationDetection is { } orientation)
            LogOrientationDetectionCall(tenantId, run.RequestedBy, analysisId, documentId, orientation);
    }

    /// <summary>
    /// Writes the ApiCallLogs row of an extraction that ended without an answer and without a
    /// <see cref="ClaudeApiException"/> — interrupted, or an unexpected fault (append-only INSERT,
    /// fire-and-forget): <c>IsSuccess = false</c>, no status, no tokens, no cost (what was billed is
    /// unknown), <paramref name="errorMessage"/> being text this project writes, tied to the document.
    /// Never throws.
    /// </summary>
    private void LogUnfinishedExtractionCall(AnalysisRun run, string tenantId, string errorMessage)
    {
        try
        {
            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          "Claude",
                endpoint:         "/v1/messages",
                responseStatus:   null,
                inputTokens:      0,
                outputTokens:     0,
                estimatedCostUsd: null,
                isSuccess:        false,
                errorMessage:     errorMessage,
                durationMs:       0,
                operation:        ApiCallLogOperations.Extraction,
                model:            null,
                userId:           run.RequestedBy,
                analysisId:       null,
                documentId:       run.DocumentId,
                ct:               CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx,
                "Could not log the unfinished extraction call for document {DocumentId} tenant {TenantId}.",
                run.DocumentId, tenantId);
        }
    }

    /// <summary>
    /// Writes the ApiCallLogs row of a FAILED balance extraction (append-only INSERT, fire-and-forget
    /// like the success row): <c>IsSuccess = false</c>, the model and the tokens when the API reported
    /// usage before failing (max_tokens, refusal, an unreadable answer) — else 0 tokens and no cost —
    /// and <see cref="ClaudeApiException.DiagnosticMessage"/>, text this project writes itself (never
    /// the API's body, never a figure). <c>AnalysisId</c> is null (none was created); the row is tied
    /// to the document. One row per failed extraction, not per HTTP retry inside it. Never throws.
    /// </summary>
    private void LogFailedExtractionCall(AnalysisRun run, string tenantId, ClaudeApiException ex, DateTime startedAtUtc)
    {
        try
        {
            var tokensKnown  = ex.InputTokens is not null || ex.OutputTokens is not null;
            var inputTokens  = ex.InputTokens  ?? 0;
            var outputTokens = ex.OutputTokens ?? 0;

            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          "Claude",
                endpoint:         "/v1/messages",
                // A failure after usage came back was a 2xx answer the pass could not use.
                responseStatus:   ex.ApiStatusCode ?? (tokensKnown ? 200 : null),
                inputTokens:      inputTokens,
                outputTokens:     outputTokens,
                estimatedCostUsd: tokensKnown && ex.Model is not null
                    ? ClaudeModelPricing.EstimateCostUsd(ex.Model, inputTokens, outputTokens)
                    : null,
                isSuccess:        false,
                errorMessage:     ex.DiagnosticMessage,
                durationMs:       (int)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds,
                operation:        ApiCallLogOperations.Extraction,
                model:            ex.Model,
                userId:           run.RequestedBy,
                analysisId:       null,
                documentId:       run.DocumentId,
                ct:               CancellationToken.None);
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx,
                "Could not log the failed extraction call for document {DocumentId} tenant {TenantId}.",
                run.DocumentId, tenantId);
        }
    }

    /// <summary>
    /// Why a queued run must not proceed on <paramref name="document"/>, or null when it still
    /// qualifies. The same rules the request applied before answering 202, read again now.
    /// </summary>
    private async Task<string?> GetBackgroundStopReasonAsync(Document document, string tenantId, CancellationToken ct)
    {
        if (document.IsDeleted)
            return "document_deleted";

        if (document.PersonLegajoId is not null)
            return "legajo_document";

        var (entityLegajoId, entityConsentActive) = await _legajoDocumentRepository
            .GetEntityLegajoConsentAsync(document.EntityId, tenantId, ct)
            .ConfigureAwait(false);

        return entityLegajoId is not null && !entityConsentActive ? AnalysisRunFailureCodes.StoredConsentRequired : null;
    }

    /// <summary>
    /// Ends a queued run that no longer qualifies: Failed (only while still Processing), an
    /// Analysis.Failed audit row carrying the reason code, and the requester's ANALYSIS_FAILED. No
    /// extraction attempt is recorded — nothing reached the model.
    /// </summary>
    private async Task AbandonAsync(AnalysisRun run, string reason)
    {
        _logger.LogWarning(
            "Background analysis for document {DocumentId} tenant {TenantId} stopped before any model call: {Reason}",
            run.DocumentId, run.TenantId, reason);

        var moved = await _analysisRunStateRepository
            .TryFailProcessingAsync(run.DocumentId, run.TenantId, DateTime.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        if (!moved)
            return;

        _ = _auditService.LogAsync(
            tenantId:     run.TenantId,
            userId:       run.RequestedBy,
            action:       AnalysisRunFailureCodes.FailedAuditAction,
            severity:     AuditSeverities.Warning,
            entityType:   "Document",
            entityId:     run.DocumentId.ToString(),
            ipAddress:    run.IpAddress,
            requestId:    run.RequestId,
            newValueJson: JsonSerializer.Serialize(new { reason }),
            ct:           CancellationToken.None);

        await NotifyAnalysisFailedAsync(run.TenantId, run.RequestedBy, run).ConfigureAwait(false);
    }

    // es-AR so reconciliation-anomaly figures carry thousands separators and two decimals,
    // consistent with the exported report (which also reformats pre-fix persisted rows).
    private static readonly System.Globalization.CultureInfo EsArFormat =
        System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Resolves the entity's previous active analysis alerts in favour of <paramref name="analysisId"/>.
    /// Fault-isolated: a failure logs a Warning and returns, so it never fails the analysis and never
    /// stops the new analysis's alerts from being created.
    /// </summary>
    private async Task SupersedePreviousAlertsAsync(
        Guid entityId, string tenantId, Guid analysisId, int newAlertCount)
    {
        try
        {
            var superseded = await _alertRepository
                .SupersedePreviousEntityAlertsAsync(entityId, tenantId, analysisId, CancellationToken.None)
                .ConfigureAwait(false);

            if (superseded > 0)
                _logger.LogInformation(
                    "Superseded {SupersededCount} previous alerts for entity {EntityId} analysis {AnalysisId} " +
                    "tenant {TenantId}; new analysis reported {NewAlertCount} alerts",
                    superseded, entityId, analysisId, tenantId, newAlertCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Superseding previous alerts failed for entity {EntityId} analysis {AnalysisId} tenant {TenantId} " +
                "(new analysis reported {NewAlertCount} alerts); continuing",
                entityId, analysisId, tenantId, newAlertCount);
        }
    }

    /// <summary>
    /// Reads the model's alerts. <c>true</c> means the answer is KNOWN: a JSON array, possibly empty.
    /// <c>false</c> means UNKNOWN — null, blank, the literal <c>null</c>, or unreadable JSON
    /// (<paramref name="unreadable"/> is <c>true</c> only for the last). Unknown never supersedes.
    /// </summary>
    internal static bool TryReadModelAlerts(
        string? json,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<ClaudeAlertJson>? alerts,
        out bool unreadable)
    {
        alerts     = null;
        unreadable = false;

        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            alerts = JsonSerializer.Deserialize<List<ClaudeAlertJson>>(json, BcraJsonOpts);
        }
        catch (JsonException)
        {
            unreadable = true;
            return false;
        }

        return alerts is not null;
    }

    private static string NormalizeSeverity(string? level) => level?.ToLowerInvariant() switch
    {
        "critical" => "Critical",
        "high"     => "High",
        "medium"   => "Medium",
        "low"      => "Low",
        _          => "Low"
    };

    private static string HumanizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        return string.Join(' ', code.Split('_')
            .Select(w => w.Length > 0
                ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()
                : w));
    }

    private const int AlertTitleMaxLength = 300; // matches dbo.Alerts.Title nvarchar(300)

    /// <summary>
    /// Resolves the alert Title for persistence. Prefers the es-AR title emitted by the
    /// model (prompt v2.7+); falls back to the humanised code for analyses persisted before
    /// titles existed, or when the model omits/blanks it. Truncated to the column width so a
    /// malformed over-long title can never abort persistence (presentation only, fault-isolated).
    /// </summary>
    internal static string ResolveAlertTitle(string? modelTitle, string? code)
    {
        var title = string.IsNullOrWhiteSpace(modelTitle)
            ? HumanizeCode(code)
            : modelTitle.Trim();

        return title.Length > AlertTitleMaxLength ? title[..AlertTitleMaxLength] : title;
    }
}
