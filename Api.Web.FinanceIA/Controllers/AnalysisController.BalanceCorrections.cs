// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>Manual correction of the extracted balance (Phase 10-F3a).</summary>
public sealed partial class AnalysisController
{
    // ── POST /api/analyses/{id}/balance-corrections ───────────────────────────────

    /// <summary>
    /// Corrects figures of the analysis's extracted balance by hand and re-scores it over the corrected
    /// balance — the same engine, no new extraction. All-or-nothing: the corrected BalanceJson (with its
    /// <c>manual_corrections</c> trail and re-evaluated <c>integrity</c>), the re-score, its history row and
    /// one [AnalysisEdits] row per figure are written in ONE transaction, or nothing is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paths are a closed whitelist: the 13 top-level figures (<c>current_assets</c>, …) and
    /// <c>current_assets_items[i].amount</c>, <c>current_liabilities_items[i].amount</c>,
    /// <c>non_current_liabilities_items[i].amount</c> for an existing line <c>i</c>. Values are finite
    /// numbers in pesos (the unit BalanceJson stores, after amounts_scale), or null.
    /// </para>
    /// <para>
    /// The gate is exactly the re-score's: PendingReview, no vote or decision, document not processing
    /// (409, the same errorCodes); the same cooldown (429) and BCRA gates (503), both with Retry-After. A
    /// correction stamps RescoredAt, so it shares that cooldown with POST {id}/rescore.
    /// </para>
    /// </remarks>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="request">The reason and the corrections.</param>
    /// <param name="rescoreService">The re-score service.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 with the GET /api/analyses/{id} body plus <c>rescore</c>; 400 validation_error with field paths;
    /// 404; 409 (person_analysis_not_correctable and the re-score's codes); 429 too_many_requests and 503
    /// bcra_unavailable, both with Retry-After; 500 rescore_failed.
    /// </returns>
    [HttpPost("{id:guid}/balance-corrections")]
    [Authorize(Roles = Roles.AnalysisCreators)]
    [ProducesResponseType(typeof(AnalysisResultResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public async Task<IActionResult> CorrectBalanceAsync(
        Guid id,
        [FromBody] BalanceCorrectionRequest? request,
        [FromServices] IAnalysisRescoreService rescoreService,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // Request-only rules first: nothing is read for a malformed request.
        var (command, errors) = BalanceCorrectionValidator.ValidateRequest(request);
        if (command is null)
            return ValidationProblem(errors);

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null)
            return Unauthorized();

        var outcome = await rescoreService
            .CorrectBalanceAsync(
                id,
                tenantId,
                currentUser.UserId,
                command,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                HttpContext.TraceIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.StatusCode == 404 && outcome.ErrorCode is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        if (outcome.ValidationErrors is { } balanceErrors)
            return ValidationProblem(balanceErrors);

        if (!outcome.IsSuccess)
            return RescoreRefusal(outcome);

        // Committed: a failure to READ the detail back must not turn a done correction into an error.
        var response = await BuildDetailResponseAsync(id, tenantId, CancellationToken.None).ConfigureAwait(false);
        if (response is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        response.Rescore = outcome.Summary;
        return Ok(response);
    }

    // ── POST /api/analyses/{id}/balance-corrections/suggestions ───────────────────

    /// <summary>
    /// "Sugerir corrección" (Phase 10-F4a): asks the extraction model, once, for the figures of the FAILED
    /// structure integrity checks and of a failed net_profit_reconciliation as printed on the analysis' document — read off the same upright pages
    /// (and halves) the second extraction pass reads — and verifies the answer by applying it to a COPY of
    /// the balance and re-running the same checks. NOTHING is applied: accepting a suggestion is the
    /// analyst's POST /balance-corrections with <c>origin: "ai_suggestion"</c>. No request body.
    /// </summary>
    /// <remarks>
    /// <c>outcome</c>: <c>fixes</c> (every previously failed targeted check — structure or
    /// net_profit_reconciliation — passes, none newly fails),
    /// <c>reduces</c> (fewer fail, none newly), <c>document_error</c> (the model read every involved figure as
    /// stored: the document itself does not add up; no suggestions) or <c>no_suggestion</c>. Each suggestion
    /// carries a whitelisted <c>path</c> (BalanceCorrectionPath syntax), the stored and suggested values in
    /// pesos (amounts_scale applied), the label and page as printed, a JPEG crop of the row (±7% of the
    /// height around the reported band; or null) and a <c>highlight</c> — the band as a rectangle in fractions
    /// of its full page (a band read on a half spans that half's x-range; or null). <c>pages</c> carries, once
    /// per distinct page, the upright full page the model read (≤ 1600 px, ≤ 450 KB JPEG), so the evidence
    /// survives an imprecise band. Each suggestion also carries <c>basis</c>: <c>printed</c>, or
    /// <c>derived</c> for a result_before_tax calculated from the confirmed net result and income tax when
    /// no printed pre-tax line reconciles — then <c>derivation</c> (es-AR) says how, <c>evidence</c> /
    /// <c>highlight</c> point at the net result line and <c>evidenceSecondary</c> is the tax line's crop.
    /// <c>textMatch</c> is where the figure is PRINTED, found in the page's text layer rather than the model's
    /// band: <c>{ page, top, bottom, left, right }</c>, fractions of that page's <c>pages</c> entry (top-left
    /// origin, the frame of <c>highlight</c>), or null on a scan, an unreadable text layer or when the figure is
    /// not printed there unambiguously. For a derived figure it is the net result line and
    /// <c>textMatchSecondary</c> the tax line (null otherwise).
    /// When the stored net_profit_reconciliation recorded a column mix (result before tax and tax read from the
    /// prior-year column), nothing is derived from them: the message opens by saying so, and the current-year
    /// figures are proposed (as <c>printed</c>) only when located on a page and reconciling.
    /// The model call is logged in ApiCallLogs (Operation Extraction) whatever
    /// the outcome.
    /// <para>
    /// A 200 is cached for 30 minutes per (tenant, result, stored BalanceJson): asking again returns it with
    /// <c>cachedAt</c> (UTC) and makes no model call and no ApiCallLogs row; a correction changes BalanceJson
    /// and so the answer. <c>?refresh=true</c> recomputes and replaces it. Refusals and 502s are never cached,
    /// and every refusal is still checked before the cache. Two requests over the same figures at once make ONE
    /// model call: the second awaits the first's outcome (per App Service instance); a caller that disconnects
    /// does not cancel the call for the other.
    /// </para>
    /// </remarks>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="refresh">True to bypass and replace a cached suggestion.</param>
    /// <param name="suggestionService">The suggestion service.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 <see cref="BalanceCorrectionSuggestionResponse"/>; 404; 409 person_analysis_not_correctable,
    /// the re-score's eligibility codes (analysis_result_missing, analysis_not_pending, analysis_has_votes,
    /// analysis_processing), suggestion_not_applicable or document_unavailable; 502 suggestion_unavailable.
    /// </returns>
    [HttpPost("{id:guid}/balance-corrections/suggestions")]
    [Authorize(Roles = Roles.AnalysisCreators)]
    [ProducesResponseType(typeof(BalanceCorrectionSuggestionResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> SuggestBalanceCorrectionsAsync(
        Guid id,
        [FromQuery] bool refresh,
        [FromServices] IBalanceCorrectionSuggestionService suggestionService,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null)
            return Unauthorized();

        var outcome = await suggestionService
            .SuggestAsync(id, tenantId, currentUser.UserId, refresh, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.IsSuccess)
            return Ok(outcome.Response);

        if (outcome.StatusCode == 404 && outcome.ErrorCode is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        var errorCode = outcome.ErrorCode ?? RescoreErrorCodes.SuggestionUnavailable;
        var problem = new ProblemDetails
        {
            Status   = outcome.StatusCode,
            Title    = outcome.StatusCode switch
            {
                409 => "Conflict",
                502 => "Bad Gateway",
                _   => "Internal Server Error"
            },
            Detail   = SuggestionRefusalDetail(errorCode),
            Type     = ErrorTypeBase + errorCode,
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = errorCode;
        return StatusCode(outcome.StatusCode, problem);
    }

    // ── GET /api/analyses/{id}/balance-corrections ────────────────────────────────

    /// <summary>
    /// The corrected figures of an analysis, newest first, from the append-only [AnalysisEdits]: which path,
    /// the value before and after, the reason, when, by whom and from where (<c>origin</c>: manual or
    /// ai_suggestion, read off the matching manual_corrections entry). An empty array when nothing was corrected.
    /// Readable by everyone who can read the analysis.
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="rescoreRepository">Resolves the result's analysis and balance, tenant-scoped.</param>
    /// <param name="editRepository">The corrections.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with <see cref="BalanceCorrectionHistoryResponse"/> items, or 404.</returns>
    [HttpGet("{id:guid}/balance-corrections")]
    [ProducesResponseType(typeof(IReadOnlyList<BalanceCorrectionHistoryResponse>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetBalanceCorrectionsAsync(
        Guid id,
        [FromServices] IAnalysisRescoreRepository rescoreRepository,
        [FromServices] IAnalysisEditRepository editRepository,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var source = await rescoreRepository
            .GetCorrectionSourceAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        var edits = await editRepository
            .GetByAnalysisAsync(source.Value.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Each row's origin is on the trail entry the same correction wrote (same path, author and instant).
        var trail = ManualCorrectionsReader.Read(source.Value.BalanceJson);

        var names = await ResolveActorNamesAsync(
            edits.Select(e => (Guid?)e.EditedBy), cancellationToken).ConfigureAwait(false);

        return Ok(edits
            .Select(e => new BalanceCorrectionHistoryResponse(
                e.AnalysisEditId,
                e.FieldPath,
                ParseInvariant(e.OldValue),
                ParseInvariant(e.NewValue),
                e.EditReason,
                e.EditedAt,
                e.EditedBy.ToString(),
                ActorName(e.EditedBy, names),
                trail?.OriginOf(e.FieldPath, e.EditedBy, e.EditedAt) ?? BalanceCorrectionOrigins.Manual))
            .ToList());
    }

    /// <summary>
    /// <c>balanceCorrections</c> for the detail: the summary of BalanceJson.manual_corrections with the
    /// latest editor's display name; null when nothing was corrected. Never throws: a name that cannot be
    /// read falls back like every actor name.
    /// </summary>
    private async Task<BalanceCorrectionsResponse?> BuildBalanceCorrectionsAsync(string? balanceJson, CancellationToken ct)
    {
        var summary = ManualCorrectionsReader.Read(balanceJson);
        if (summary is null)
            return null;

        string? lastByName = null;
        if (summary.LastBy is { } lastBy)
        {
            try
            {
                var names = await ResolveActorNamesAsync([lastBy], ct).ConfigureAwait(false);
                lastByName = ActorName(lastBy, names);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Balance-correction author could not be resolved for the analysis detail; showing the fallback name");
                lastByName = "Usuario";
            }
        }

        return new BalanceCorrectionsResponse
        {
            Count      = summary.Count,
            LastAt     = summary.LastAt,
            LastByName = lastByName
        };
    }

    /// <summary>Field-level 400 with every error, in the shape of <see cref="ValidationProblem(string, string)"/>.</summary>
    private IActionResult ValidationProblem(IReadOnlyCollection<ValidationError> errors)
    {
        var pd = new ProblemDetails
        {
            Status   = 400,
            Title    = "Validation Error",
            Detail   = "Se produjeron uno o más errores de validación.",
            Type     = ErrorTypeBase + RescoreErrorCodes.ValidationError,
            Instance = HttpContext.Request.Path
        };
        pd.Extensions["errorCode"] = RescoreErrorCodes.ValidationError;
        pd.Extensions["errors"]    = errors.Select(e => new { field = e.Field, message = e.Message }).ToArray();
        return StatusCode(400, pd);
    }

    private static string SuggestionRefusalDetail(string errorCode) => errorCode switch
    {
        RescoreErrorCodes.SuggestionNotApplicable =>
            "El balance extraído no tiene controles de integridad fallidos para los que sugerir una corrección.",
        RescoreErrorCodes.DocumentUnavailable =>
            "No se pudo leer el documento del análisis para sugerir una corrección.",
        RescoreErrorCodes.SuggestionUnavailable =>
            "No se pudo obtener una sugerencia de corrección en este momento. Probá de nuevo en unos minutos.",
        _ => RescoreEligibility.Detail(errorCode),
    };

    private static double? ParseInvariant(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}
