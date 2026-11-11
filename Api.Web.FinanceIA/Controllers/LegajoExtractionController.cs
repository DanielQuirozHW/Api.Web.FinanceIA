// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// The extraction review of a legajo document (M10 Phase 2b): the effective view, the analyst's
/// corrections (append-only) and the approval. Reads are open to any authenticated role of the tenant;
/// writes are <see cref="Roles.Operators"/>. Tenant-scoped throughout; ProblemDetails for every error.
///
/// <para>
/// The model extracts, C# computes and checks, the analyst decides: nothing here calls the model — a
/// correction recomputes the checks in C# on the effective values.
/// </para>
///
/// <para>
/// PERSONAL DATA: the view carries what the model read from a person's paperwork. It never carries the
/// holder CUIT the extraction read (only whether it matches), and nothing here logs a value, a reason or
/// a name — ids, paths and counts only.
/// </para>
/// </summary>
[ApiController]
[Route("api/documents/{id:guid}/extraction")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class LegajoExtractionController : ControllerBase
{
    /// <summary>Minimum non-blank length of a reason (mirrors CK_LegajoCorrections_Reason).</summary>
    private const int MinReasonLength = 5;

    /// <summary>LegajoCorrections.Reason is nvarchar(1000); the review reason (audit only) is held to the same.</summary>
    private const int MaxReasonLength = 1000;

    private readonly ITenantContextAccessor    _tenantContextAccessor;
    private readonly IDocumentRepository       _documentRepository;
    private readonly ILegajoDocumentRepository _legajoRepository;
    private readonly IUserRepository           _userRepository;
    private readonly IAuditService             _auditService;
    private readonly ILegajoIntegrityService   _integrity;
    private readonly ILegajoReconciliationService _reconciliation;
    private readonly LegajoOptions             _legajoOptions;
    private readonly ILogger<LegajoExtractionController> _logger;

    /// <summary>Initializes a new instance of <see cref="LegajoExtractionController"/>.</summary>
    public LegajoExtractionController(
        ITenantContextAccessor    tenantContextAccessor,
        IDocumentRepository       documentRepository,
        ILegajoDocumentRepository legajoRepository,
        IUserRepository           userRepository,
        IAuditService             auditService,
        ILegajoIntegrityService   integrity,
        ILegajoReconciliationService reconciliation,
        IOptions<LegajoOptions>   legajoOptions,
        ILogger<LegajoExtractionController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _documentRepository    = documentRepository;
        _legajoRepository      = legajoRepository;
        _userRepository        = userRepository;
        _auditService          = auditService;
        _integrity             = integrity;
        _reconciliation        = reconciliation;
        _legajoOptions         = legajoOptions.Value;
        _logger                = logger;
    }

    // ── GET /api/documents/{id}/extraction ──────────────────────────────────────

    /// <summary>
    /// The current extraction's effective view: every field with its provenance and correction history,
    /// the checks recomputed on the effective values, and the review queues (missing, flagged).
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="LegajoExtractionViewResponse"/>; 404 when there is no extraction, or
    /// <c>extraction_unavailable</c> when the document is Blocked.
    /// </returns>
    [HttpGet]
    [ProducesResponseType(typeof(LegajoExtractionViewResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var loaded = await LoadAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null)
            return loaded.Error;

        return Ok(await ViewAsync(loaded.Extraction!, loaded.Document!, tenantId, cancellationToken).ConfigureAwait(false));
    }

    // ── POST /api/documents/{id}/extraction/corrections ─────────────────────────

    /// <summary>
    /// Records one correction to one field of the current extraction (append-only; never updated or
    /// deleted) and returns the view with the checks recomputed. Only in NeedsReview or Ready; a
    /// correction of a Ready document puts it back to NeedsReview until it is approved again.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="request">The field (JSON Pointer), its new value (JSON) and the reason.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="LegajoExtractionViewResponse"/>.</returns>
    [HttpPost("corrections")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(LegajoExtractionViewResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> CorrectAsync(
        Guid id,
        [FromBody] LegajoCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < MinReasonLength)
            return ValidationProblem("reason", $"El motivo es obligatorio (al menos {MinReasonLength} caracteres).");
        if (reason.Length > MaxReasonLength)
            return ValidationProblem("reason", $"El motivo no puede superar {MaxReasonLength} caracteres.");

        var loaded = await LoadAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null)
            return loaded.Error;

        var document   = loaded.Document!;
        var extraction = loaded.Extraction!;

        if (document.LegajoStatus is not (LegajoStatuses.NeedsReview or LegajoStatuses.Ready))
            return CodedProblem(409, "Conflict", "extraction_not_editable",
                "Solo se corrige la extracción de un documento en estado NeedsReview o Ready.");

        var validation = LegajoCorrectionValidator.Validate(
            extraction.Schema, extraction.Effective, request!.FieldPath, request.Value);
        if (!validation.IsValid)
            return ValidationProblem("fieldPath", validation.Error!);

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        var now = DateTime.UtcNow;
        var correction = new LegajoCorrection
        {
            CorrectionId       = Guid.NewGuid(),
            TenantId           = tenantId,
            ExtractionId       = extraction.Run.ExtractionId,
            FieldPath          = request.FieldPath!,
            OriginalValueJson  = validation.Original?.ToJsonString(),
            CorrectedValueJson = validation.Value?.ToJsonString(),
            Reason             = reason,
            CorrectedBy        = userId.Value,
            CorrectedAt        = now
        };

        // One transaction: the row, and ALWAYS the conditional Ready → NeedsReview with the review stamp
        // cleared — an approval that landed after the status read above is reopened, never left standing
        // on values it did not see.
        if (await _legajoRepository
                .InsertCorrectionAndReopenAsync(correction, id, tenantId, now, cancellationToken)
                .ConfigureAwait(false))
        {
            document.LegajoStatus = LegajoStatuses.NeedsReview;
            extraction.Run.ReviewedBy     = null;
            extraction.Run.ReviewedAt     = null;
            extraction.Run.ReviewOverride = null;
            extraction.Run.ReviewReason   = null;

            // The run is no longer approved: its figures leave the legajo's inventory and incomes.
            await _reconciliation
                .TryRebuildAsync(document.PersonLegajoId!.Value, tenantId, "correction", cancellationToken)
                .ConfigureAwait(false);
        }

        // The audit row names the field and carries the reason — never the values (personal data).
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Document.ExtractionCorrected",
            severity:     "Info",
            entityType:   "Document",
            entityId:     id.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                extractionId = extraction.Run.ExtractionId,
                correctionId = correction.CorrectionId,
                fieldPath    = correction.FieldPath,
                reason
            }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Legajo document {DocumentId} tenant {TenantId}: correction {CorrectionId} recorded on run {ExtractionId}",
            id, tenantId, correction.CorrectionId, extraction.Run.ExtractionId);

        var updated = Evaluate(extraction.Run, [.. extraction.Corrections, correction], document, loaded.LegajoCuit);
        return Ok(await ViewAsync(updated!, document, tenantId, cancellationToken).ConfigureAwait(false));
    }

    // ── POST /api/documents/{id}/extraction/review ──────────────────────────────

    /// <summary>
    /// Approves the current extraction: NeedsReview → Ready, stamping the reviewer on the run. When a
    /// check still fails or a required field is still missing on the effective values, a reason is
    /// mandatory — the approval is then an explicit override, recorded in the audit.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="request">
    /// The optional (or, for an override, mandatory) reason, and optionally the extraction id and
    /// corrections count the analyst reviewed (409 <c>extraction_changed</c> when either moved).
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="LegajoExtractionViewResponse"/>.</returns>
    [HttpPost("review")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(LegajoExtractionViewResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> ReviewAsync(
        Guid id,
        [FromBody] LegajoReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var reason = request?.Reason?.Trim();
        if (reason is not null && reason.Length > MaxReasonLength)
            return ValidationProblem("reason", $"El motivo no puede superar {MaxReasonLength} caracteres.");

        var loaded = await LoadAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null)
            return loaded.Error;

        var document   = loaded.Document!;
        var extraction = loaded.Extraction!;

        if (document.LegajoStatus != LegajoStatuses.NeedsReview)
            return CodedProblem(409, "Conflict", "extraction_not_reviewable",
                "Solo se aprueba la extracción de un documento en estado NeedsReview.");

        // Bound to what the analyst saw: a client that sends the run and corrections count it reviewed
        // loses here when either moved — before the override rule, so a stale screen is never asked for a
        // reason that no longer applies. A client that sends neither behaves exactly as before.
        if ((request?.ExtractionId is { } seenRun && seenRun != extraction.Run.ExtractionId)
            || (request?.CorrectionsCount is { } seenCount && seenCount != extraction.Corrections.Count))
        {
            _logger.LogInformation(
                "Legajo document {DocumentId} tenant {TenantId}: approval refused, the client reviewed a different state of run {ExtractionId}",
                id, tenantId, extraction.Run.ExtractionId);
            return ExtractionChanged();
        }

        var assessment = extraction.Assessment;
        var isOverride = assessment.FailedChecks > 0 || assessment.Missing.Count > 0;

        if (isOverride && (string.IsNullOrWhiteSpace(reason) || reason.Length < MinReasonLength))
            return ValidationProblem("reason",
                $"Hay {assessment.FailedChecks} control(es) fallido(s) y {assessment.Missing.Count} campo(s) obligatorio(s) " +
                $"sin valor: aprobar requiere un motivo (al menos {MinReasonLength} caracteres).");

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        // Optimistic: approves only what was evaluated — still NeedsReview, still this run, still exactly
        // these corrections. A correction committed in between makes it lose.
        var now = DateTime.UtcNow;
        if (!await _legajoRepository
                .TryApproveExtractionAsync(id, tenantId, extraction.Run.ExtractionId, extraction.Corrections.Count,
                                           userId.Value, isOverride, string.IsNullOrEmpty(reason) ? null : reason, now,
                                           cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Legajo document {DocumentId} tenant {TenantId}: approval of run {ExtractionId} lost to a concurrent change",
                id, tenantId, extraction.Run.ExtractionId);
            return ExtractionChanged();
        }

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Document.ExtractionReviewed",
            severity:     isOverride ? "Warning" : "Info",
            entityType:   "Document",
            entityId:     id.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                extractionId     = extraction.Run.ExtractionId,
                @override        = isOverride,
                failedChecks     = assessment.FailedChecks,
                missingCount     = assessment.Missing.Count,
                flaggedCount     = assessment.Flagged.Count,
                correctionsCount = extraction.Corrections.Count,
                reason
            }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Legajo document {DocumentId} tenant {TenantId}: extraction {ExtractionId} approved (override: {Override})",
            id, tenantId, extraction.Run.ExtractionId, isOverride);

        document.LegajoStatus = LegajoStatuses.Ready;
        extraction.Run.ReviewedBy     = userId;
        extraction.Run.ReviewedAt     = now;
        extraction.Run.ReviewOverride = isOverride;
        extraction.Run.ReviewReason   = string.IsNullOrEmpty(reason) ? null : reason;

        // The approved figures join the legajo's inventory and incomes. Never fails the approval.
        await _reconciliation
            .TryRebuildAsync(document.PersonLegajoId!.Value, tenantId, "approval", cancellationToken)
            .ConfigureAwait(false);

        return Ok(await ViewAsync(extraction, document, tenantId, cancellationToken).ConfigureAwait(false));
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private sealed record Loaded(
        Document? Document, LegajoEffectiveExtraction? Extraction, string LegajoCuit, IActionResult? Error);

    /// <summary>
    /// The document, its current extraction on the effective values, and the legajo's CUIT — or the 404.
    /// A Blocked document's extraction is another person's data: it is never served, corrected or
    /// approved (404 <c>extraction_unavailable</c>).
    /// </summary>
    private async Task<Loaded> LoadAsync(Guid id, string tenantId, CancellationToken ct)
    {
        Loaded NotFound(string detail)
            => new(null, null, string.Empty, Problem(statusCode: 404, title: "Not Found", detail: detail));

        var document = await _documentRepository.GetByIdAsync(id, tenantId, ct).ConfigureAwait(false);
        if (document is null || document.IsDeleted || document.PersonLegajoId is not { } legajoId)
            return NotFound("No se encontró el documento.");

        var legajo = await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, ct).ConfigureAwait(false);
        if (legajo is null)
            return NotFound("No se encontró el documento.");

        if (document.LegajoStatus == LegajoStatuses.Blocked)
            return new(null, null, string.Empty, ProblemWithCode(404, "Not Found", "extraction_unavailable",
                "La extracción no está disponible: el documento corresponde a otra persona."));

        var run = await _legajoRepository.GetCurrentExtractionAsync(id, tenantId, ct).ConfigureAwait(false);
        if (run is null)
            return NotFound("El documento no tiene una extracción.");

        var corrections = await _legajoRepository
            .GetCorrectionsAsync([run.ExtractionId], tenantId, ct)
            .ConfigureAwait(false);

        var extraction = Evaluate(run, corrections, document, legajo.CUIT);
        return extraction is null
            ? NotFound("El documento no tiene una extracción.")
            : new Loaded(document, extraction, legajo.CUIT, null);
    }

    private LegajoEffectiveExtraction? Evaluate(
        LegajoExtraction run, IEnumerable<LegajoCorrection> corrections, Document document, string legajoCuit)
        => LegajoExtractionReview.Evaluate(
            run,
            corrections,
            _integrity,
            document.PeriodEnd is { } end ? DateOnly.FromDateTime(end) : null,
            legajoCuit,
            _legajoOptions.FieldConfidenceThreshold);

    private async Task<LegajoExtractionViewResponse> ViewAsync(
        LegajoEffectiveExtraction extraction, Document document, string tenantId, CancellationToken ct)
    {
        var ids = extraction.Corrections.Select(c => c.CorrectedBy)
            .Concat(extraction.Run.ReviewedBy is { } by ? [by] : [])
            .Distinct()
            .ToList();

        // Only users of this tenant are named.
        var names = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _userRepository.GetByIdsAsync(ids, ct).ConfigureAwait(false))
                .Where(u => string.Equals(u.TenantId, tenantId, StringComparison.Ordinal))
                .ToDictionary(u => u.UserId, u => u.DisplayName);

        return LegajoExtractionReview.BuildView(extraction, document, names);
    }

    private async Task<Guid?> ResolveCallerIdAsync(CancellationToken cancellationToken)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(firebaseUid))
            return null;

        var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid, cancellationToken).ConfigureAwait(false);
        return user?.UserId;
    }

    /// <summary>409 extraction_changed: what the analyst reviewed is no longer the current extraction.</summary>
    private ObjectResult ExtractionChanged()
        => ProblemWithCode(409, "Conflict", "extraction_changed",
            "La extracción cambió mientras revisabas. Recargá y volvé a revisar.");

    /// <summary>
    /// <see cref="ControllerBase.Problem(string, string, int?, string, string)"/> plus a stable
    /// <c>errorCode</c> extension the Portal can branch on: type, title, detail and traceId stay exactly
    /// what Problem() produces, so the code is purely additive.
    /// </summary>
    private ObjectResult CodedProblem(int status, string title, string errorCode, string detail)
    {
        var result = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = errorCode;
        return result;
    }

    /// <summary>A ProblemDetails carrying a stable <c>errorCode</c> extension the Portal can branch on.</summary>
    private ObjectResult ProblemWithCode(int status, string title, string errorCode, string detail)
    {
        var pd = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = detail,
            Type     = "https://serviciosfinancieros.api/errors/" + errorCode,
            Instance = HttpContext.Request.Path
        };
        pd.Extensions["errorCode"] = errorCode;
        return StatusCode(status, pd);
    }

    /// <summary>The 400 shape DocumentsController uses: errorCode validation_error and errors[{field, message}].</summary>
    private IActionResult ValidationProblem(string field, string message)
    {
        var pd = new ProblemDetails
        {
            Status   = 400,
            Title    = "Validation Error",
            Detail   = "Se produjeron uno o más errores de validación.",
            Type     = "https://serviciosfinancieros.api/errors/validation_error",
            Instance = HttpContext.Request.Path
        };
        pd.Extensions["errorCode"] = "validation_error";
        pd.Extensions["errors"]    = new[] { new { field, message } };
        return StatusCode(400, pd);
    }
}
