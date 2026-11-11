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
/// The persona humana legajo (DDL 069/070): its checklist, its documents, and the person's
/// data-processing authorization. Reads are open to any authenticated role of the tenant (like
/// GET /api/documents); writes are <see cref="Roles.Operators"/>. Tenant-scoped throughout.
///
/// <para>
/// No response carries the person's name or CUIT, nor the holder CUIT a classification read. Logs
/// carry the legajo and document ids only (#237's convention).
/// </para>
/// </summary>
[ApiController]
[Route("api/person-legajos")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class PersonLegajosController : ControllerBase
{
    private readonly ITenantContextAccessor    _tenantContextAccessor;
    private readonly ILegajoDocumentRepository _legajoRepository;
    private readonly IDocumentRepository       _documentRepository;
    private readonly IUserRepository           _userRepository;
    private readonly IAuditService             _auditService;
    private readonly ILegajoPipelineQueue      _pipelineQueue;
    private readonly ILegajoIntegrityService   _integrity;
    private readonly IPersonLegajoSnapshotRefreshService _snapshotRefresh;
    private readonly IPersonExpectedDocumentsService _expectedDocuments;
    private readonly IConfiguration            _configuration;
    private readonly LegajoOptions             _legajoOptions;
    private readonly ILogger<PersonLegajosController> _logger;

    /// <summary>Initializes a new instance of <see cref="PersonLegajosController"/>.</summary>
    public PersonLegajosController(
        ITenantContextAccessor    tenantContextAccessor,
        ILegajoDocumentRepository legajoRepository,
        IDocumentRepository       documentRepository,
        IUserRepository           userRepository,
        IAuditService             auditService,
        ILegajoPipelineQueue      pipelineQueue,
        ILegajoIntegrityService   integrity,
        IPersonLegajoSnapshotRefreshService snapshotRefresh,
        IPersonExpectedDocumentsService expectedDocuments,
        IConfiguration            configuration,
        IOptions<LegajoOptions>   legajoOptions,
        ILogger<PersonLegajosController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _legajoRepository      = legajoRepository;
        _documentRepository    = documentRepository;
        _userRepository        = userRepository;
        _auditService          = auditService;
        _pipelineQueue         = pipelineQueue;
        _integrity             = integrity;
        _snapshotRefresh       = snapshotRefresh;
        _expectedDocuments     = expectedDocuments;
        _configuration         = configuration;
        _legajoOptions         = legajoOptions.Value;
        _logger                = logger;
    }

    // ── GET /api/person-legajos/{legajoId} ──────────────────────────────────────

    /// <summary>
    /// The legajo: regime, consent, and the expected-documents checklist — the tenant's matrix for the regime, or
    /// the code default (<c>expectedDocumentsSource</c> says which).
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="PersonLegajoResponse"/>, or 404.</returns>
    [HttpGet("{legajoId:guid}")]
    [ProducesResponseType(typeof(PersonLegajoResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var legajo = await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (legajo is null)
            return LegajoNotFound();

        return Ok(await BuildLegajoResponseAsync(legajo, cancellationToken).ConfigureAwait(false));
    }

    // ── GET /api/person-legajos/{legajoId}/documents ────────────────────────────

    /// <summary>The legajo's non-deleted documents, newest first, with status, classification and current extraction.</summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the list, or 404.</returns>
    [HttpGet("{legajoId:guid}/documents")]
    [ProducesResponseType(typeof(IReadOnlyList<LegajoDocumentResponse>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetDocumentsAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var legajo = await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (legajo is null)
            return LegajoNotFound();

        var documents = await _legajoRepository.GetLegajoDocumentsAsync(legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var documentIds = documents.Select(d => d.DocumentId).ToList();

        var runs = await _legajoRepository
            .GetCurrentClassificationsAsync(documentIds, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Phase 2b: the current extraction of each document, summarized on its effective values.
        var extractions = await _legajoRepository
            .GetCurrentExtractionsAsync(documentIds, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var corrections = await _legajoRepository
            .GetCorrectionsAsync(extractions.Values.Select(r => r.ExtractionId).ToList(), tenantId, cancellationToken)
            .ConfigureAwait(false);

        LegajoExtractionSummaryResponse? Summary(Document d)
        {
            // A Blocked document's extraction read another person's paperwork: nothing of it is shown.
            if (d.LegajoStatus == LegajoStatuses.Blocked || !extractions.TryGetValue(d.DocumentId, out var run))
                return null;

            // One document's figures must never cost the whole list its 200.
            try
            {
                var evaluated = LegajoExtractionReview.Evaluate(
                    run, corrections, _integrity, d.PeriodEnd is { } end ? DateOnly.FromDateTime(end) : null,
                    legajo.CUIT, _legajoOptions.FieldConfidenceThreshold);
                return evaluated is null ? null : LegajoExtractionReview.Summarize(evaluated);
            }
            catch (ArithmeticException)
            {
                _logger.LogWarning(
                    "Legajo document {DocumentId} tenant {TenantId}: extraction summary skipped (arithmetic)",
                    d.DocumentId, tenantId);
                return null;
            }
        }

        var items = documents
            .Select(d => LegajoReadModel.BuildDocument(d, legajo.CUIT, runs.GetValueOrDefault(d.DocumentId), Summary(d)))
            .ToList();

        return Ok(items);
    }

    // ── POST /api/person-legajos/{legajoId}/consent ─────────────────────────────

    /// <summary>
    /// Records the person's data-processing authorization against an AutorizacionDatos document of the
    /// same legajo, audits it, and queues every Uploaded document of the legajo for classification and
    /// every ReadyForExtraction one for extraction (those whose consent was revoked mid-way wait there).
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="request">The AutorizacionDatos document.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="PersonLegajoResponse"/>.</returns>
    [HttpPost("{legajoId:guid}/consent")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(PersonLegajoResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> RecordConsentAsync(
        Guid legajoId,
        [FromBody] RecordConsentRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        if (request is null || request.DocumentId == Guid.Empty)
        {
            // Title and detail as they always were; the errors[] entry is the ValidationProblem shape.
            var missing = CodedProblem(400, "Bad Request", "validation_error", "Se requiere documentId.");
            ((ProblemDetails)missing.Value!).Extensions["errors"] =
                new[] { new { field = "documentId", message = "Se requiere documentId." } };
            return missing;
        }

        var legajo = await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (legajo is null)
            return LegajoNotFound();

        var document = await _documentRepository.GetByIdAsync(request.DocumentId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el documento.");

        if (document.IsDeleted
            || document.PersonLegajoId != legajoId
            || document.DocumentType != DocumentTypes.AutorizacionDatos)
        {
            return CodedProblem(400, "Bad Request", "consent_document_invalid",
                "El documento debe ser una autorización de tratamiento de datos (AutorizacionDatos) vigente de este legajo.");
        }

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        var now = DateTime.UtcNow;

        // Writes [dbo].[PersonLegajos] only, in its own SaveChanges: the document was stored earlier.
        if (!await _legajoRepository
                .SetConsentAsync(legajoId, tenantId, document.DocumentId, userId.Value, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return LegajoNotFound();
        }

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "PersonLegajo.ConsentRecorded",
            severity:     "Info",
            entityType:   "PersonLegajo",
            entityId:     legajoId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: legajo.ConsentRecordedAt is null
                ? null
                : JsonSerializer.Serialize(new { documentId = legajo.ConsentDocumentId, recordedAt = legajo.ConsentRecordedAt }),
            newValueJson: JsonSerializer.Serialize(new { documentId = document.DocumentId, recordedAt = now }),
            ct:           CancellationToken.None);

        // Every document waiting for consent goes to the queue now.
        var queued = 0;
        var waiting = await _legajoRepository
            .GetDocumentIdsInStatusAsync(legajoId, tenantId, LegajoStatuses.Uploaded, cancellationToken)
            .ConfigureAwait(false);

        foreach (var documentId in waiting)
        {
            if (await _legajoRepository
                    .TryTransitionAsync(documentId, tenantId, LegajoStatuses.Uploaded, LegajoStatuses.Queued, now,
                                        cancellationToken)
                    .ConfigureAwait(false))
            {
                _pipelineQueue.Enqueue(new LegajoWorkItem(documentId, tenantId));
                queued++;
            }
        }

        // Documents already classified (or confirmed) resume at extraction; a type without an extractor
        // simply loses the claim.
        var readyForExtraction = await _legajoRepository
            .GetDocumentIdsInStatusAsync(legajoId, tenantId, LegajoStatuses.ReadyForExtraction, cancellationToken)
            .ConfigureAwait(false);

        foreach (var documentId in readyForExtraction)
        {
            _pipelineQueue.Enqueue(new LegajoWorkItem(documentId, tenantId));
            queued++;
        }

        _logger.LogInformation(
            "Consent recorded for legajo {PersonLegajoId} tenant {TenantId}; {QueuedCount} document(s) queued",
            legajoId, tenantId, queued);

        var updated = legajo with
        {
            ConsentRecordedAt = now,
            ConsentDocumentId = document.DocumentId,
            ConsentRecordedBy = userId,
            HasActiveConsent  = true
        };
        return Ok(await BuildLegajoResponseAsync(updated, cancellationToken).ConfigureAwait(false));
    }

    // ── DELETE /api/person-legajos/{legajoId}/consent ───────────────────────────

    /// <summary>
    /// Revokes the authorization: clears the three consent columns and audits it. No further model call
    /// is made for this legajo (the worker re-checks before every request); documents still waiting in
    /// the queue go back to Uploaded. Runs already made stay.
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="PersonLegajoResponse"/>.</returns>
    [HttpDelete("{legajoId:guid}/consent")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(PersonLegajoResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> RevokeConsentAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var legajo = await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (legajo is null)
            return LegajoNotFound();

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        var now = DateTime.UtcNow;
        if (!await _legajoRepository.ClearConsentAsync(legajoId, tenantId, now, cancellationToken).ConfigureAwait(false))
            return LegajoNotFound();

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "PersonLegajo.ConsentRevoked",
            severity:     "Info",
            entityType:   "PersonLegajo",
            entityId:     legajoId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new { documentId = legajo.ConsentDocumentId, recordedAt = legajo.ConsentRecordedAt }),
            ct:           CancellationToken.None);

        // Documents not yet claimed go back to waiting; one already Classifying is stopped by the
        // worker's own consent check before its request.
        var unqueued = 0;
        var queued = await _legajoRepository
            .GetDocumentIdsInStatusAsync(legajoId, tenantId, LegajoStatuses.Queued, cancellationToken)
            .ConfigureAwait(false);

        foreach (var documentId in queued)
        {
            if (await _legajoRepository
                    .TryTransitionAsync(documentId, tenantId, LegajoStatuses.Queued, LegajoStatuses.Uploaded, now,
                                        cancellationToken)
                    .ConfigureAwait(false))
            {
                unqueued++;
            }
        }

        _logger.LogInformation(
            "Consent revoked for legajo {PersonLegajoId} tenant {TenantId}; {UnqueuedCount} queued document(s) returned to Uploaded",
            legajoId, tenantId, unqueued);

        var updated = legajo with
        {
            ConsentRecordedAt = null, ConsentDocumentId = null, ConsentRecordedBy = null, HasActiveConsent = false
        };
        return Ok(await BuildLegajoResponseAsync(updated, cancellationToken).ConfigureAwait(false));
    }

    // ── POST /api/person-legajos/{legajoId}/refresh-bcra ────────────────────────

    /// <summary>
    /// Re-fetches BCRA (Central de Deudores, cheques, macro) for the legajo's CUIT and stores it on the
    /// legajo — and on its entity too, in the same save, when the legajo is an entity's, exactly as
    /// POST api/entities/{id}/refresh-bcra does. The route a FIADOR's legajo refreshes through, since it
    /// has no entity. Reachable for a legajo of the tenant that is an entity's or an ACTIVE fiador's;
    /// anything else is 404, before BCRA is called.
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">Stored; <see cref="LegajoSnapshotRefreshResponse"/>.</response>
    /// <response code="404">No reachable legajo with this id in the tenant.</response>
    /// <response code="502">bcra_unavailable: BCRA returned nothing usable; nothing was written.</response>
    [HttpPost("{legajoId:guid}/refresh-bcra")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(LegajoSnapshotRefreshResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshBcraAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var outcome = await _snapshotRefresh.RefreshBcraAsync(legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return MapRefreshOutcome(outcome);
    }

    // ── POST /api/person-legajos/{legajoId}/refresh-arca ────────────────────────

    /// <summary>
    /// Re-fetches ARCA's constancia de inscripción for the legajo's CUIT and stores the snapshot, the
    /// fiscal regime, the monotributo category and the activity start on the legajo — and the snapshot
    /// and regime on its entity, in the same save, when the legajo is an entity's, exactly as
    /// POST api/entities/{id}/refresh-arca does. Same reach rule as refresh-bcra.
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">Stored (or, when ARCA sent only the key status, the key status merged in).</response>
    /// <response code="400">arca_not_configured: no ARCA CUIT is configured for this tenant.</response>
    /// <response code="404">No reachable legajo with this id (no errorCode), or arca_not_found: ARCA has no such person.</response>
    /// <response code="502">arca_no_fiscal_data or arca_unavailable; nothing was written.</response>
    [HttpPost("{legajoId:guid}/refresh-arca")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(LegajoSnapshotRefreshResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshArcaAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        // The same per-tenant setting EntitiesController reads; the service answers ArcaNotConfigured
        // for a blank one only after the legajo resolved, so an unknown legajo is still a 404.
        var cuitRepresentada = _configuration[$"Tenants:{tenantId}:Cuit"];

        var outcome = await _snapshotRefresh.RefreshArcaAsync(legajoId, tenantId, cuitRepresentada, cancellationToken)
            .ConfigureAwait(false);

        return MapRefreshOutcome(outcome);
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    private async Task<PersonLegajoResponse> BuildLegajoResponseAsync(LegajoSummary legajo, CancellationToken ct)
    {
        var documents = await _legajoRepository
            .GetLegajoDocumentsAsync(legajo.PersonLegajoId, legajo.TenantId, ct)
            .ConfigureAwait(false);

        // Never throws: a settings fault reads the code defaults, so it can never fail a legajo read.
        var expected = await _expectedDocuments
            .ForAsync(legajo.TenantId, legajo.FiscalRegime, ct)
            .ConfigureAwait(false);

        LegajoConsentResponse? consent = null;
        if (legajo.ConsentRecordedAt is { } recordedAt)
        {
            string? recordedByName = null;
            if (legajo.ConsentRecordedBy is { } recordedBy)
            {
                var user = await _userRepository.GetByUserIdAsync(recordedBy, ct).ConfigureAwait(false);
                // Only a user of this tenant is named.
                if (user is not null && string.Equals(user.TenantId, legajo.TenantId, StringComparison.Ordinal))
                    recordedByName = user.DisplayName;
            }

            consent = new LegajoConsentResponse
            {
                RecordedAt     = recordedAt,
                DocumentId     = legajo.ConsentDocumentId,
                RecordedByName = recordedByName,
                IsActive       = legajo.HasActiveConsent
            };
        }

        return new PersonLegajoResponse
        {
            PersonLegajoId      = legajo.PersonLegajoId,
            EntityId            = legajo.EntityId,
            FiscalRegime        = legajo.FiscalRegime,
            MonotributoCategory = legajo.MonotributoCategory,
            Consent             = consent,
            ExpectedDocuments       = LegajoReadModel.BuildExpectedDocuments(expected.Lines, documents),
            ExpectedDocumentsSource = expected.Source
        };
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

    private ObjectResult LegajoNotFound()
        => Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el legajo.");

    /// <summary>
    /// The status code of a legajo-scoped refresh. The details are the entity routes' wording; the
    /// errorCodes let the Portal tell the 404 of an unknown legajo from ARCA's "no such person".
    /// </summary>
    private IActionResult MapRefreshOutcome(LegajoSnapshotRefreshOutcome outcome) => outcome.Status switch
    {
        LegajoSnapshotRefreshStatus.Refreshed or LegajoSnapshotRefreshStatus.KeyStatusOnly
            when outcome.Legajo is { } legajo => Ok(new LegajoSnapshotRefreshResponse
            {
                PersonLegajoId      = legajo.PersonLegajoId,
                EntityId            = outcome.EntityId,
                FiscalRegime        = legajo.FiscalRegime,
                MonotributoCategory = legajo.MonotributoCategory,
                BcraDataUpdatedAt   = legajo.BcraDataUpdatedAt,
                ArcaDataUpdatedAt   = legajo.ArcaDataUpdatedAt,
            }),
        LegajoSnapshotRefreshStatus.ArcaNotConfigured => CodedProblem(400, "Bad Request", "arca_not_configured",
            "No hay un CUIT de ARCA configurado para este tenant."),
        LegajoSnapshotRefreshStatus.BcraUnavailable => CodedProblem(502, "Bad Gateway", "bcra_unavailable",
            "El servicio de BCRA no está disponible o no devolvió datos."),
        LegajoSnapshotRefreshStatus.ArcaPersonNotFound => CodedProblem(404, "Not Found", "arca_not_found",
            "No se encontró ninguna persona en ARCA con el CUIT del legajo."),
        LegajoSnapshotRefreshStatus.ArcaNoFiscalData => CodedProblem(502, "Bad Gateway", "arca_no_fiscal_data",
            "ARCA no devolvió datos fiscales para este CUIT."),
        LegajoSnapshotRefreshStatus.ArcaUnavailable => CodedProblem(502, "External service unavailable", "arca_unavailable",
            "El servicio de ARCA no está disponible temporalmente."),
        _ => LegajoNotFound(),
    };

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
}
