// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Diagnostics;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// The outcome of a correction suggestion: a 200 with its <see cref="Response"/>, or a refusal with an
/// HTTP status and (except a plain 404) an <c>errorCode</c>.
/// </summary>
public sealed record BalanceCorrectionSuggestionOutcome
{
    public int StatusCode { get; init; }

    public string? ErrorCode { get; init; }

    public BalanceCorrectionSuggestionResponse? Response { get; init; }

    public bool IsSuccess => Response is not null;

    public static BalanceCorrectionSuggestionOutcome Ok(BalanceCorrectionSuggestionResponse response) =>
        new() { StatusCode = 200, Response = response };

    public static BalanceCorrectionSuggestionOutcome NotFound() => new() { StatusCode = 404 };

    public static BalanceCorrectionSuggestionOutcome Refused(int statusCode, string errorCode) =>
        new() { StatusCode = statusCode, ErrorCode = errorCode };
}

/// <summary>POST /api/analyses/{id}/balance-corrections/suggestions (Phase 10-F4a "Sugerir corrección").</summary>
public interface IBalanceCorrectionSuggestionService
{
    /// <summary>
    /// Asks the extraction model for the figures of the failed structure checks and net_profit_reconciliation
    /// as printed (deriving a result_before_tax when only the presentation is at fault), verifies the
    /// answer with the integrity checks over a copy, and returns the proposal. Writes nothing but the
    /// ApiCallLogs cost rows: accepting a suggestion is the analyst's POST /balance-corrections. A 200 over
    /// the same stored BalanceJson is served from the cache (no call, no cost row) unless
    /// <paramref name="refresh"/>, which recomputes it and replaces the cached one.
    /// </summary>
    Task<BalanceCorrectionSuggestionOutcome> SuggestAsync(
        Guid resultId, string tenantId, Guid userId, bool refresh, CancellationToken ct);
}

/// <summary>
/// The correction suggestion. Refusals, in order, all before any model spend: 404; 409
/// person_analysis_not_correctable; the re-score's eligibility 409s (a suggestion the analyst could not
/// accept is not worth a call); 409 suggestion_not_applicable (no failed structure check and no failed
/// net_profit_reconciliation in the stored integrity); 409 document_unavailable. Then the cache (<see cref="BalanceSuggestionCache"/>): a hit
/// over the same stored BalanceJson is returned with <c>cachedAt</c> and costs nothing. Otherwise ONE model
/// call — 502 suggestion_unavailable when it fails, never cached. Concurrent misses over the same figures share
/// that one call (<see cref="BalanceSuggestionInFlight"/>): the second caller awaits the first's outcome.
/// </summary>
/// <remarks>
/// Logs carry ids, counts, the outcome and timings only — never a figure, a label, an image, the
/// document's name or a model body. The images, crops and pages live in memory only: for this request,
/// and in the in-process cache for up to <see cref="BalanceSuggestionCache.Ttl"/>. Never persisted.
/// </remarks>
public sealed class BalanceCorrectionSuggestionService : IBalanceCorrectionSuggestionService
{
    private const string ClaudeServiceName = "Claude";
    private const string MessagesEndpoint  = "/v1/messages";

    private readonly IAnalysisResultRepository   _analysisResultRepository;
    private readonly IAnalysisRepository         _analysisRepository;
    private readonly IDocumentRepository         _documentRepository;
    private readonly IAnalysisRescoreRepository  _rescoreRepository;
    private readonly IDocumentStorageService     _storageService;
    private readonly IClaudeService              _claudeService;
    private readonly IApiCallLogService          _apiCallLogService;
    private readonly IBalanceIntegrityService    _integrity;
    private readonly IMemoryCache                _cache;
    private readonly BalanceSuggestionInFlight   _inFlight;
    private readonly TimeProvider                _timeProvider;
    private readonly ILogger<BalanceCorrectionSuggestionService> _logger;

    /// <summary>Initializes a new instance of <see cref="BalanceCorrectionSuggestionService"/> on the system clock.</summary>
    public BalanceCorrectionSuggestionService(
        IAnalysisResultRepository   analysisResultRepository,
        IAnalysisRepository         analysisRepository,
        IDocumentRepository         documentRepository,
        IAnalysisRescoreRepository  rescoreRepository,
        IDocumentStorageService     storageService,
        IClaudeService              claudeService,
        IApiCallLogService          apiCallLogService,
        IBalanceIntegrityService    integrity,
        IMemoryCache                cache,
        BalanceSuggestionInFlight   inFlight,
        ILogger<BalanceCorrectionSuggestionService> logger)
        : this(analysisResultRepository, analysisRepository, documentRepository, rescoreRepository, storageService,
               claudeService, apiCallLogService, integrity, cache, inFlight, TimeProvider.System, logger)
    {
    }

    /// <summary>Clock-injectable constructor, so tests can pin <c>cachedAt</c>.</summary>
    internal BalanceCorrectionSuggestionService(
        IAnalysisResultRepository   analysisResultRepository,
        IAnalysisRepository         analysisRepository,
        IDocumentRepository         documentRepository,
        IAnalysisRescoreRepository  rescoreRepository,
        IDocumentStorageService     storageService,
        IClaudeService              claudeService,
        IApiCallLogService          apiCallLogService,
        IBalanceIntegrityService    integrity,
        IMemoryCache                cache,
        BalanceSuggestionInFlight   inFlight,
        TimeProvider                timeProvider,
        ILogger<BalanceCorrectionSuggestionService> logger)
    {
        _analysisResultRepository = analysisResultRepository;
        _analysisRepository       = analysisRepository;
        _documentRepository       = documentRepository;
        _rescoreRepository        = rescoreRepository;
        _storageService           = storageService;
        _claudeService            = claudeService;
        _apiCallLogService        = apiCallLogService;
        _integrity                = integrity;
        _cache                    = cache;
        _inFlight                 = inFlight;
        _timeProvider             = timeProvider;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task<BalanceCorrectionSuggestionOutcome> SuggestAsync(
        Guid resultId, string tenantId, Guid userId, bool refresh, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // ── The result and its analysis, tenant-scoped ─────────────────────────────────
        var result = await _analysisResultRepository.GetByIdAsync(resultId, tenantId, ct).ConfigureAwait(false);
        if (result is null)
            return BalanceCorrectionSuggestionOutcome.NotFound();

        var analysis = await _analysisRepository.GetByIdAsync(result.AnalysisId, ct).ConfigureAwait(false);
        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return BalanceCorrectionSuggestionOutcome.NotFound();

        if (string.Equals(analysis.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            return Refuse(analysis.AnalysisId, tenantId, userId, 409, RescoreErrorCodes.PersonAnalysisNotCorrectable);

        // ── The correction's own gate: no call for a suggestion that could not be accepted ──
        var (blocked, document) = await CorrectionEligibility
            .EvaluateAsync(analysis, result, tenantId, _rescoreRepository, _documentRepository, ct)
            .ConfigureAwait(false);
        if (blocked is not null)
            return Refuse(analysis.AnalysisId, tenantId, userId, 409, blocked);

        // ── What failed, and what to ask ──────────────────────────────────────────────────
        var plan = BalanceSuggestionPlanner.Build(result.BalanceJson, analysis.FiscalYear);
        if (plan is null)
            return Refuse(analysis.AnalysisId, tenantId, userId, 409, RescoreErrorCodes.SuggestionNotApplicable);

        // ── The cache: the same stored figures were already asked about (every gate above still ran) ──
        var cacheKey = BalanceSuggestionCache.Key(tenantId, resultId, plan.BalanceJson);
        if (!refresh && TryServeCached(cacheKey, analysis.AnalysisId, tenantId, userId) is { } hit)
            return hit;

        // ── One computation per key at a time: a concurrent caller awaits this one's outcome ──
        var (outcome, joined) = await _inFlight
            .RunAsync(
                cacheKey,
                sharedCt => ComputeAsync(cacheKey, refresh, analysis, document, plan, tenantId, userId, sharedCt),
                ct)
            .ConfigureAwait(false);

        if (joined)
        {
            _logger.LogInformation(
                "Correction suggestion for analysis {AnalysisId} tenant {TenantId} by user {UserId}: joined the " +
                "computation in flight (status {StatusCode})",
                analysis.AnalysisId, tenantId, userId, outcome.StatusCode);
        }

        return outcome;
    }

    // The shared part: everything after the gates and the cache. Runs once per key at a time, on the shared
    // token (never one caller's), on the scoped services of the caller that started it.
    private async Task<BalanceCorrectionSuggestionOutcome> ComputeAsync(
        string cacheKey, bool refresh, Analysis analysis, Document? document, BalanceSuggestionPlan plan,
        string tenantId, Guid userId, CancellationToken ct)
    {
        // A run that finished between this caller's cache miss and its start has already cached its 200.
        if (!refresh && TryServeCached(cacheKey, analysis.AnalysisId, tenantId, userId) is { } hit)
            return hit;

        // ── The analysis' PDF, unwrapped exactly as the extraction reads it ───────────────
        if (document is null || document.IsDeleted || string.IsNullOrWhiteSpace(document.StoragePath))
            return Refuse(analysis.AnalysisId, tenantId, userId, 409, RescoreErrorCodes.DocumentUnavailable);

        byte[] pdfBytes;
        try
        {
            pdfBytes = await _storageService.ReadAsync(document.StoragePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Correction suggestion for analysis {AnalysisId} tenant {TenantId}: the document could not be read ({ExceptionType})",
                analysis.AnalysisId, tenantId, ex.GetType().Name);
            return Refuse(analysis.AnalysisId, tenantId, userId, 409, RescoreErrorCodes.DocumentUnavailable);
        }

        pdfBytes = PdfAttachmentExtractor.UnwrapEmbeddedBalance(pdfBytes, _logger, document.DocumentId, tenantId);

        // ── ONE model call ──────────────────────────────────────────────────────────────
        var stopwatch = Stopwatch.StartNew();
        var reply = await _claudeService
            .SuggestBalanceCorrectionsAsync(pdfBytes, plan.Question, ct)
            .ConfigureAwait(false);

        LogCalls(tenantId, userId, analysis.AnalysisId, reply);

        if (!reply.Succeeded)
        {
            _logger.LogWarning(
                "Correction suggestion for analysis {AnalysisId} tenant {TenantId} by user {UserId} failed: {FailureCode} " +
                "(status {HttpStatus}, {ElapsedMs} ms)",
                analysis.AnalysisId, tenantId, userId, reply.FailureCode, reply.HttpStatus, stopwatch.ElapsedMilliseconds);
            return BalanceCorrectionSuggestionOutcome.Refused(502, RescoreErrorCodes.SuggestionUnavailable);
        }

        // ── Verify, never trust; then find each kept figure where it is PRINTED (local, no call) ───────
        using var text = new BalanceSuggestionPageText(pdfBytes, failure => _logger.LogWarning(
            "Correction suggestion for analysis {AnalysisId} tenant {TenantId}: the text layer could not be read " +
            "({ExceptionType}); no text match on the affected pages",
            analysis.AnalysisId, tenantId, failure));
        var verdict = BalanceSuggestionVerifier.Verify(plan, reply.Answer!, reply.Images, _integrity, text);

        // ── The full pages the suggestions refer to; a highlight or text match is only kept with its page ──
        var pages       = BalanceSuggestionEvidence.Pages(verdict.Suggestions, reply.Images);
        var pageNumbers = pages.Select(p => p.Page).ToHashSet();
        var suggestions = verdict.Suggestions
            .Select(s => s with
            {
                Highlight          = s.Highlight is { } h && pageNumbers.Contains(h.Page) ? h : null,
                TextMatch          = s.TextMatch is { } m && pageNumbers.Contains(m.Page) ? m : null,
                TextMatchSecondary = s.TextMatchSecondary is { } n && pageNumbers.Contains(n.Page) ? n : null,
            })
            .ToList();

        _logger.LogInformation(
            "Correction suggestion for analysis {AnalysisId} tenant {TenantId} by user {UserId}: outcome {Outcome}; " +
            "failed checks {FailedBefore} -> {FailedAfter}; asked fields {AskedFields}, item groups {AskedGroups}; " +
            "proposed {Proposed}, kept {Kept}, dropped outside {DroppedOutside}, invalid {DroppedInvalid}, " +
            "unchanged {Unchanged}, unmatched printed lines {UnmatchedPrinted}, derived {Derived}, with evidence {WithEvidence}, " +
            "with highlight {WithHighlight}, with text match {WithTextMatch} (secondary {WithTextMatchSecondary}, " +
            "searched {TextSearched}), pages {Pages}; model {Model}, {ElapsedMs} ms",
            analysis.AnalysisId, tenantId, userId, verdict.Outcome,
            verdict.FailedChecksBefore.Count, verdict.FailedChecksAfter.Count,
            plan.Question.Fields.Count, plan.Question.ItemGroups.Count,
            verdict.Proposed, verdict.Suggestions.Count, verdict.DroppedOutside, verdict.DroppedInvalid,
            verdict.Unchanged, verdict.UnmatchedPrinted, verdict.Derived, suggestions.Count(s => s.Evidence is not null),
            suggestions.Count(s => s.Highlight is not null), suggestions.Count(s => s.TextMatch is not null),
            suggestions.Count(s => s.TextMatchSecondary is not null), text.Searched, pages.Count,
            reply.Model, stopwatch.ElapsedMilliseconds);

        var response = new BalanceCorrectionSuggestionResponse
        {
            Outcome            = verdict.Outcome,
            Message            = verdict.Message,
            Suggestions        = suggestions,
            FailedChecksBefore = verdict.FailedChecksBefore,
            FailedChecksAfter  = verdict.FailedChecksAfter,
            Model              = reply.Model,
            Pages              = pages,
        };

        Remember(cacheKey, response, analysis.AnalysisId, tenantId);
        return BalanceCorrectionSuggestionOutcome.Ok(response);
    }

    private BalanceCorrectionSuggestionOutcome? TryServeCached(string cacheKey, Guid analysisId, string tenantId, Guid userId)
    {
        if (!_cache.TryGetValue(cacheKey, out CachedBalanceSuggestion? cached) || cached is null)
            return null;

        _logger.LogInformation(
            "Correction suggestion for analysis {AnalysisId} tenant {TenantId} by user {UserId}: served from cache " +
            "(outcome {Outcome}, cached at {CachedAt:o})",
            analysisId, tenantId, userId, cached.Response.Outcome, cached.CachedAt);
        return BalanceCorrectionSuggestionOutcome.Ok(cached.Response with { CachedAt = cached.CachedAt });
    }

    // Only a 200 reaches here; a refresh replaces the entry. Too large an answer is served, not cached.
    private void Remember(string cacheKey, BalanceCorrectionSuggestionResponse response, Guid analysisId, string tenantId)
    {
        var bytes = BalanceSuggestionCache.EstimatedBytes(response);
        if (bytes > BalanceSuggestionCache.MaxEntryBytes)
        {
            _logger.LogInformation(
                "Correction suggestion for analysis {AnalysisId} tenant {TenantId} not cached: ~{EstimatedBytes} bytes",
                analysisId, tenantId, bytes);
            return;
        }

        _cache.Set(
            cacheKey,
            new CachedBalanceSuggestion(response, _timeProvider.GetUtcNow()),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = BalanceSuggestionCache.Ttl });
    }

    private BalanceCorrectionSuggestionOutcome Refuse(
        Guid analysisId, string tenantId, Guid userId, int statusCode, string errorCode)
    {
        _logger.LogInformation(
            "Correction suggestion for analysis {AnalysisId} tenant {TenantId} refused for user {UserId}: {ErrorCode}",
            analysisId, tenantId, userId, errorCode);
        return BalanceCorrectionSuggestionOutcome.Refused(statusCode, errorCode);
    }

    /// <summary>
    /// The cost rows, fire-and-forget like every ApiCallLogs write: the suggestion request (Operation
    /// Extraction — the extraction model reading the balance document; CK_ApiCallLogs_Operation admits it
    /// without DDL) and, apart, the scan-orientation call priced with ITS model, as the pipeline logs it.
    /// errorMessage is a fixed failure code, followed for an HTTP error by the API's parsed error type,
    /// clamped message and request id (<see cref="AnthropicErrorDiagnostics.WithDetail"/>) — never a body.
    /// </summary>
    private void LogCalls(string tenantId, Guid userId, Guid analysisId, BalanceSuggestionReply reply)
    {
        try
        {
            if (reply.OrientationDetection is { } orientation)
            {
                _ = _apiCallLogService.LogAsync(
                    tenantId:         tenantId,
                    service:          ClaudeServiceName,
                    endpoint:         MessagesEndpoint,
                    responseStatus:   orientation.HttpStatus,
                    inputTokens:      orientation.InputTokens,
                    outputTokens:     orientation.OutputTokens,
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
                    ct:               CancellationToken.None);
            }

            if (!reply.RequestSent)
                return;

            var tokensKnown = reply.InputTokens > 0 || reply.OutputTokens > 0;
            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          ClaudeServiceName,
                endpoint:         MessagesEndpoint,
                responseStatus:   reply.HttpStatus,
                inputTokens:      tokensKnown ? reply.InputTokens : null,
                outputTokens:     tokensKnown ? reply.OutputTokens : null,
                estimatedCostUsd: tokensKnown
                    ? ClaudeModelPricing.EstimateCostUsd(reply.Model, reply.InputTokens, reply.OutputTokens)
                    : null,
                isSuccess:        reply.Succeeded,
                errorMessage:     AnthropicErrorDiagnostics.WithDetail(reply.FailureCode, reply.FailureDetail),
                durationMs:       reply.DurationMs,
                operation:        ApiCallLogOperations.Extraction,
                model:            reply.Model,
                userId:           userId,
                analysisId:       analysisId,
                ct:               CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Correction suggestion for analysis {AnalysisId} tenant {TenantId}: the ApiCallLogs write failed",
                analysisId, tenantId);
        }
    }
}
