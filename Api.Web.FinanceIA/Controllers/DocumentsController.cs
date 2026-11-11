// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using System.Security.Cryptography;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Concurrency;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// PDF upload and document management endpoints.
/// All endpoints require an internal JWT (<c>Internal</c> scheme).
/// Tenant isolation is enforced on every query and mutation.
/// </summary>
[ApiController]
[Route("api/documents")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class DocumentsController : ControllerBase
{
    private const string PdfMimeType    = "application/pdf";

    /// <summary>Minimum non-blank length of a confirmation reason (mirrors CK_LegajoCorrections_Reason).</summary>
    private const int MinReasonLength = 5;

    // The legacy (company) vocabulary, case-insensitive exactly as before DDL 070. The constants
    // moved to DocumentTypes; the set and its behaviour did not.
    private static readonly HashSet<string> AllowedDocumentTypes =
        new(DocumentTypes.LegacyTypes, StringComparer.OrdinalIgnoreCase);

    private readonly ITenantContextAccessor    _tenantContextAccessor;
    private readonly IDocumentRepository       _documentRepository;
    private readonly IEntityRepository         _entityRepository;
    private readonly IDocumentStorageService   _storageService;
    private readonly IUserRepository           _userRepository;
    private readonly IAuditService             _auditService;
    private readonly ILegajoDocumentRepository _legajoRepository;
    private readonly IFiadorRepository         _fiadorRepository;
    private readonly ILegajoPipelineQueue      _pipelineQueue;
    private readonly ILegajoReconciliationService _reconciliation;
    private readonly IOptions<BillingOptions>  _billingOptions;
    private readonly IAnalysisRunListRepository _runListRepository;
    private readonly KeyedAsyncLock            _uploadLock;
    private readonly ILogger<DocumentsController> _logger;

    /// <summary>
    /// How long a company upload of the SAME file (same entity, same document type, same SHA-256) is answered
    /// with the document already stored instead of a new one: 200 OK with that document, and nothing written —
    /// no blob, no row, no <c>Document.Uploaded</c> audit. It absorbs a double submit (QA Q32: two rows 250 ms
    /// apart, one left Pending forever), not a deliberate re-upload.
    ///
    /// <para>
    /// WHY ONLY A SHORT WINDOW. <see cref="ReanalysisPolicy"/> is evaluated per DOCUMENT: today a later
    /// re-upload of the same bytes creates a fresh Pending row that may be analysed at once, while handing back
    /// the older row would, once that row is Completed, start demanding a rejection of its analysis that
    /// today's re-upload does not demand. Outside the window the upload behaves exactly as before.
    /// </para>
    ///
    /// <para>
    /// The look-up and the writes run under <see cref="KeyedAsyncLock"/> on (tenant, entity, hash), so the
    /// second of two near-simultaneous requests waits for the first's slow blob write and then finds its row.
    /// That lock is ONE process: with several App Service instances the Portal's double-submit guard is the
    /// other half.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan DuplicateUploadWindow = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Initializes a new instance of <see cref="DocumentsController"/>.
    /// </summary>
    public DocumentsController(
        ITenantContextAccessor    tenantContextAccessor,
        IDocumentRepository       documentRepository,
        IEntityRepository         entityRepository,
        IDocumentStorageService   storageService,
        IUserRepository           userRepository,
        IAuditService             auditService,
        ILegajoDocumentRepository legajoRepository,
        IFiadorRepository         fiadorRepository,
        ILegajoPipelineQueue      pipelineQueue,
        ILegajoReconciliationService reconciliation,
        IOptions<BillingOptions>  billingOptions,
        IAnalysisRunListRepository runListRepository,
        KeyedAsyncLock            uploadLock,
        ILogger<DocumentsController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _documentRepository    = documentRepository;
        _entityRepository      = entityRepository;
        _storageService        = storageService;
        _userRepository        = userRepository;
        _auditService          = auditService;
        _legajoRepository      = legajoRepository;
        _fiadorRepository      = fiadorRepository;
        _pipelineQueue         = pipelineQueue;
        _reconciliation        = reconciliation;
        _billingOptions        = billingOptions;
        _runListRepository     = runListRepository;
        _uploadLock            = uploadLock;
        _logger                = logger;
    }

    // ── GET /api/documents ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a paginated list of documents for the current tenant.
    /// Optionally filtered by <paramref name="entityId"/>.
    /// </summary>
    /// <param name="page">1-based page number. Default: 1.</param>
    /// <param name="pageSize">Items per page. Default: 20.</param>
    /// <param name="entityId">Optional entity filter.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="DocumentListResponse"/>.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(DocumentListResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] int   page             = 1,
        [FromQuery] int   pageSize         = 20,
        [FromQuery] Guid? entityId         = null,
        CancellationToken cancellationToken = default)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in DocumentsController.GetAllAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        IReadOnlyList<Document> all = entityId.HasValue
            ? await _documentRepository
                .GetByEntityIdAsync(entityId.Value, tenantId, cancellationToken)
                .ConfigureAwait(false)
            : await _documentRepository
                .GetAllByTenantAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);

        // Exclude soft-deleted documents
        var active = all.Where(d => !d.IsDeleted).ToList();
        var total  = active.Count;
        var pageDocuments = active
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        var items = pageDocuments.Select(MapToResponse).ToList();

        // The page's failure flags: ONE per-hash read for the whole page, never one per row.
        await FillFailureFlagsAsync(pageDocuments, items, tenantId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Listed {Count}/{Total} documents for tenant {TenantId} (page {Page}/{PageSize}, entityId={EntityId})",
            items.Count, total, tenantContext.TenantId, page, pageSize, entityId?.ToString() ?? "all");

        return Ok(new DocumentListResponse
        {
            Items    = items,
            Page     = page,
            PageSize = pageSize,
            Total    = total
        });
    }

    // ── GET /api/documents/{id} ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a single document by its primary key.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="DocumentResponse"/>, or 404 Not Found.</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in DocumentsController.GetByIdAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var document = await _documentRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null || document.IsDeleted)
        {
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el documento.");
        }

        var response = MapToResponse(document);
        await FillFailureFlagsAsync([document], [response], tenantId, cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }

    // ── GET /api/documents/{id}/download ────────────────────────────────────────

    /// <summary>
    /// Returns the raw file bytes for a document so the client can view or
    /// download it.  StoragePath is read from the persisted record — no
    /// path input is accepted from the caller, preventing path traversal.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 file stream, 404 Not Found, or 500 on storage error.</returns>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(typeof(FileContentResult), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> DownloadAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in DocumentsController.DownloadAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        // ── Load document (repo filters by tenantId — implicit isolation) ───────

        var document = await _documentRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null || document.IsDeleted)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el documento.");

        // ── Read bytes from storage (StoragePath comes from DB, not from caller) ─

        byte[] bytes;
        try
        {
            bytes = await _storageService
                .ReadAsync(document.StoragePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Storage read failed for document {DocumentId} tenant {TenantId}",
                id, tenantId);
            return Problem(statusCode: 404, title: "Not Found", detail: "El archivo no está disponible.");
        }

        // ── Resolve caller for audit ─────────────────────────────────────────────

        var downloadFirebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid? downloadUserId = null;
        if (!string.IsNullOrWhiteSpace(downloadFirebaseUid))
        {
            var downloadUser = await _userRepository
                .GetByFirebaseUidAsync(downloadFirebaseUid, cancellationToken)
                .ConfigureAwait(false);
            downloadUserId = downloadUser?.UserId;
        }

        // ── Audit (fire-and-forget) ──────────────────────────────────────────────

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     downloadUserId,
            action:     "Document.Downloaded",
            severity:   "Info",
            entityType: "Document",
            entityId:   id.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        // A persona humana's documents are often named after the person: their file name stays out
        // of the log (#237's convention). Company documents log it as they always have.
        _logger.LogInformation(
            "Document downloaded: {DocumentId} ({FileName}) for tenant {TenantId}",
            id, document.PersonLegajoId is null ? document.OriginalFileName : "legajo document", tenantId);

        // ── Stream file ──────────────────────────────────────────────────────────

        return File(bytes, document.MimeType ?? "application/octet-stream", document.OriginalFileName);
    }

    // ── POST /api/documents/upload ───────────────────────────────────────────────

    /// <summary>
    /// Accepts a multipart/form-data PDF upload and stores it.
    /// <para>
    /// Without <paramref name="personLegajoId"/> this is the legacy company-document upload, unchanged.
    /// With it, the document goes into a persona humana's legajo (DDL 070): see
    /// <see cref="UploadToLegajoAsync"/>.
    /// </para>
    /// <para>
    /// The company upload is idempotent for <see cref="DuplicateUploadWindow"/>: the same file for the same
    /// entity and document type, uploaded again within that window (a double submit), answers 200 OK with the
    /// document already stored — failure flags filled — and writes nothing.
    /// </para>
    /// </summary>
    /// <param name="file">The PDF file (required, max 25 MB — <see cref="PdfSizeLimits.MaxPdfBytes"/>).</param>
    /// <param name="entityId">Entity (PyME) the document belongs to.</param>
    /// <param name="documentType">Balance | Complemento | Certificado | Otro; with a legajo, one of DocumentTypes.LegajoTypes.</param>
    /// <param name="fiscalYear">Optional fiscal year.</param>
    /// <param name="personLegajoId">Optional: the legajo to upload into — the entity's person's, or an active fiador's of the entity.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 201 Created with <see cref="DocumentResponse"/> and Location header; 200 OK with the EXISTING document
    /// for a repeat of the same company upload within <see cref="DuplicateUploadWindow"/>.
    /// </returns>
    [HttpPost("upload")]
    [Authorize(Roles = Roles.Operators)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(DocumentResponse), 201)]
    [ProducesResponseType(typeof(DocumentResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [RequestSizeLimit(PdfSizeLimits.MaxUploadRequestBytes)]
    public async Task<IActionResult> UploadAsync(
        IFormFile         file,
        [FromForm] Guid   entityId,
        [FromForm] string documentType,
        [FromForm] int?   fiscalYear,
        [FromForm] Guid?  personLegajoId,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in DocumentsController.UploadAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        // ── Validate file ────────────────────────────────────────────────────────

        if (file is null || file.Length == 0)
            return ValidationProblem("file", "Se requiere un archivo no vacío.");

        if (file.Length > PdfSizeLimits.MaxPdfBytes)
            return ValidationProblem("file",
                $"El archivo supera el tamaño máximo permitido de {PdfSizeLimits.MaxPdfLabel}.");

        if (!string.Equals(file.ContentType, PdfMimeType, StringComparison.OrdinalIgnoreCase))
            return ValidationProblem("file", "Solo se aceptan archivos application/pdf.");

        // The Content-Type header above is client-supplied and trivially spoofed, so it is only a
        // cheap early reject. The authoritative check reads the file's own leading bytes.
        await using (var headerStream = file.OpenReadStream())
        {
            if (!await FileSignatureValidator
                    .MatchesAsync(headerStream, FileSignatureType.Pdf, cancellationToken)
                    .ConfigureAwait(false))
                return ValidationProblem("file", "El archivo no es un PDF válido.");
        }

        // ── Persona humana legajo upload (DDL 070) ───────────────────────────────
        // Branches off AFTER the file checks both paths share. Everything below this block is the
        // legacy upload, byte-for-byte.
        if (personLegajoId.HasValue)
        {
            return await UploadToLegajoAsync(
                    file, entityId, documentType, fiscalYear, personLegajoId.Value, tenantId, cancellationToken)
                .ConfigureAwait(false);
        }

        // ── Validate documentType ────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(documentType) || !AllowedDocumentTypes.Contains(documentType))
            return ValidationProblem("documentType", "documentType debe ser uno de: Balance, Complemento, Certificado, Otro.");

        // ── Verify entityId belongs to this tenant ───────────────────────────────

        var entity = await _entityRepository
            .GetByIdAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return Problem(statusCode: 404, title: "Not Found",
                detail: $"No se encontró la entidad '{entityId}' para este tenant.");
        }

        // ── Resolve uploading user ───────────────────────────────────────────────
        // Resolve the caller fully BEFORE writing anything to storage: an unresolved caller must
        // fail here, not after SaveAsync has already left an orphaned blob with no DB record.

        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (currentUser is null)
            return Unauthorized();

        var userId = currentUser.UserId;

        // ── Read file bytes, compute SHA-256 hash ────────────────────────────────

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        memoryStream.Position = 0;

        var hashBytes  = await SHA256.HashDataAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var hashHex    = Convert.ToHexString(hashBytes).ToLowerInvariant();
        memoryStream.Position = 0;

        var trimmedType = documentType.Trim();

        // ── Idempotency: a double submit of the same file answers with the first row ──
        // Look-up, blob write and row insert run under one lock per (tenant, entity, file) so the second of
        // two requests 250 ms apart waits for the first's slow blob write and then finds its row. See
        // DuplicateUploadWindow for why the window is short and for the lock's single-instance scope.
        Document document;
        await using (await _uploadLock
                         .AcquireAsync($"{tenantId}|{entityId:N}|{hashHex}", cancellationToken)
                         .ConfigureAwait(false))
        {
            var duplicate = await TryFindDuplicateUploadAsync(
                    entityId, tenantId, hashHex, trimmedType, cancellationToken)
                .ConfigureAwait(false);

            if (duplicate is not null)
            {
                // Ids only: the file name stays out of this line.
                _logger.LogInformation(
                    "Duplicate company upload answered with existing document {DocumentId} for entity {EntityId} " +
                    "tenant {TenantId}; nothing stored",
                    duplicate.DocumentId, entityId, tenantId);

                var existing = MapToResponse(duplicate);
                await FillFailureFlagsAsync([duplicate], [existing], tenantId, cancellationToken)
                    .ConfigureAwait(false);
                return Ok(existing);
            }

            document = await StoreCompanyDocumentAsync(
                    file, memoryStream, entityId, trimmedType, fiscalYear, hashHex, userId, tenantId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // ── Audit ────────────────────────────────────────────────────────────────

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     userId,
            action:     "Document.Uploaded",
            severity:   "Info",
            entityType: "Document",
            entityId:   document.DocumentId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Document uploaded: {DocumentId} ({FileName}, {Bytes} bytes) for entity {EntityId} tenant {TenantId}",
            document.DocumentId, document.OriginalFileName, document.FileSizeBytes,
            entityId, tenantContext.TenantId);

        var created = MapToResponse(document);
        await FillFailureFlagsAsync([document], [created], tenantId, cancellationToken).ConfigureAwait(false);

        return CreatedAtAction(
            nameof(GetById),
            new { id = document.DocumentId },
            created);
    }

    /// <summary>
    /// The company document a repeat of this upload should be answered with: same entity, type and hash,
    /// uploaded within <see cref="DuplicateUploadWindow"/>; null when there is none. FAULT-ISOLATED: a failed
    /// look-up logs a Warning and returns null, so the upload goes on exactly as it did before this check.
    /// </summary>
    private async Task<Document?> TryFindDuplicateUploadAsync(
        Guid entityId, string tenantId, string hashHex, string documentType, CancellationToken cancellationToken)
    {
        try
        {
            return await _documentRepository
                .FindRecentByHashAsync(
                    entityId, tenantId, hashHex, documentType,
                    DateTime.UtcNow - DuplicateUploadWindow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Duplicate-upload look-up failed for entity {EntityId} tenant {TenantId} ({ExceptionType}); " +
                "storing the upload as a new document.",
                entityId, tenantId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The company upload's writes, unchanged: the blob, then the Pending document row.</summary>
    private async Task<Document> StoreCompanyDocumentAsync(
        IFormFile         file,
        MemoryStream      content,
        Guid              entityId,
        string            documentType,
        int?              fiscalYear,
        string            hashHex,
        Guid              userId,
        string            tenantId,
        CancellationToken cancellationToken)
    {
        // ── Persist file to storage ──────────────────────────────────────────────

        var documentId   = Guid.NewGuid();
        var storagePath  = await _storageService
            .SaveAsync(tenantId, documentId, content, cancellationToken)
            .ConfigureAwait(false);

        // ── Persist document record ──────────────────────────────────────────────

        var now      = DateTime.UtcNow;
        var document = new Document
        {
            DocumentId         = documentId,
            TenantId           = tenantId,
            EntityId           = entityId,
            UploadedBy         = userId,
            OriginalFileName   = Path.GetFileName(file.FileName),
            StoredFileName     = $"{documentId}.pdf",
            StoragePath        = storagePath,
            FileSizeBytes      = file.Length,
            MimeType           = PdfMimeType,
            FileHashSHA256     = hashHex,
            DocumentType       = documentType,
            FiscalYear         = fiscalYear,
            ProcessingStatus   = "Pending",
            UploadedAt         = now,
            RetentionExpiresAt = now.AddYears(7),
            IsDeleted          = false
        };

        await _documentRepository.CreateAsync(document, cancellationToken)
            .ConfigureAwait(false);

        return document;
    }

    // ── DELETE /api/documents/{id} ───────────────────────────────────────────────

    /// <summary>
    /// Soft-deletes a document by setting <c>IsDeleted = true</c>.
    /// The record is never physically removed from the database.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>204 No Content on success, 404 if not found.</returns>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in DocumentsController.DeleteAsync");
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        // The authorization a legajo's consent stands on cannot disappear under it (DDL 070). Revoking
        // first is explicit and audited; deleting would leave consent recorded against nothing.
        if (await _legajoRepository.IsConsentDocumentInUseAsync(id, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return ConflictProblem(
                "Revocá la autorización antes de eliminar este documento.",
                "consent_document_in_use");
        }

        // A legajo document may be feeding the legajo's inventory and incomes: read its legajo first.
        var legajoId = (await _documentRepository.GetByIdAsync(id, tenantId, cancellationToken).ConfigureAwait(false))
            ?.PersonLegajoId;

        var deleted = await _documentRepository
            .SoftDeleteAsync(id, tenantId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (!deleted)
        {
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el documento.");
        }

        // Its figures leave the legajo's inventory and incomes. Never fails the delete.
        if (legajoId is { } personLegajoId)
        {
            await _reconciliation.TryRebuildAsync(personLegajoId, tenantId, "delete", cancellationToken)
                .ConfigureAwait(false);
        }

        var deleteFirebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid? deleteUserId = null;
        if (!string.IsNullOrWhiteSpace(deleteFirebaseUid))
        {
            var deleteUser = await _userRepository
                .GetByFirebaseUidAsync(deleteFirebaseUid, cancellationToken)
                .ConfigureAwait(false);
            deleteUserId = deleteUser?.UserId;
        }

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     deleteUserId,
            action:     "Document.Deleted",
            severity:   "Info",
            entityType: "Document",
            entityId:   id.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Document soft-deleted: {DocumentId} for tenant {TenantId}",
            id, tenantContext.TenantId);

        return NoContent();
    }

    // ── POST /api/documents/{id}/reprocess ──────────────────────────────────────

    /// <summary>
    /// Sends a Failed legajo document back to the stage that failed and to the queue: Failed →
    /// ReadyForExtraction when the EXTRACTION failed (its LastExtractionError carries the
    /// <see cref="LegajoExtractionErrors.StagePrefix"/>), so a clean or analyst-confirmed classification is
    /// never silently re-run; Failed → Queued otherwise. Refused with 409 when the document is not Failed
    /// or its file has spent Billing:MaxExtractionAttemptsPerDocument.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>202 Accepted with <see cref="DocumentResponse"/>.</returns>
    [HttpPost("{id:guid}/reprocess")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(DocumentResponse), 202)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> ReprocessAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var tenantId = tenantContext.TenantId;

        var document = await _documentRepository.GetByIdAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (document is null || document.IsDeleted)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el documento.");

        if (document.PersonLegajoId is null)
            return CodedProblem(400, "Bad Request", "not_legajo_document",
                "Solo se reprocesan documentos de un legajo de persona humana.");

        if (document.LegajoStatus != LegajoStatuses.Failed)
            return CodedProblem(409, "Conflict", "invalid_status",
                "Solo puede reprocesarse un documento en estado Failed.");

        var maxAttempts = _billingOptions.Value.MaxExtractionAttemptsPerDocument;
        if (maxAttempts > 0)
        {
            var spent = await _documentRepository
                .SumExtractionAttemptsByHashAsync(document.FileHashSHA256, tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (spent >= maxAttempts)
                return CodedProblem(409, "Conflict", "attempts_exhausted",
                    "El archivo agotó los intentos de procesamiento permitidos.");
        }

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        var target = LegajoExtractionErrors.IsExtractionStage(document.LastExtractionError)
                     && LegajoExtractionPrompts.For(document.DocumentType) is not null
            ? LegajoStatuses.ReadyForExtraction
            : LegajoStatuses.Queued;

        var now = DateTime.UtcNow;
        if (!await _legajoRepository
                .TryTransitionAsync(id, tenantId, LegajoStatuses.Failed, target, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return CodedProblem(409, "Conflict", "status_changed",
                "El documento cambió de estado; recargue e intente nuevamente.");
        }

        _pipelineQueue.Enqueue(new LegajoWorkItem(id, tenantId));

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Document.Reprocessed",
            severity:     "Info",
            entityType:   "Document",
            entityId:     id.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new { legajoStatus = target }),
            ct:           CancellationToken.None);

        _logger.LogInformation("Legajo document {DocumentId} requeued to {LegajoStatus} for tenant {TenantId}",
            id, target, tenantId);

        document.LegajoStatus          = target;
        document.LegajoStatusUpdatedAt = now;
        return StatusCode(202, MapToResponse(document));
    }

    // ── POST /api/documents/{id}/classification/confirm ─────────────────────────

    /// <summary>
    /// An analyst accepts a NeedsReview classification: NeedsReview → ReadyForExtraction, with the
    /// confirmed type and optional period end, and the document is queued for extraction. A reason is
    /// required and audited. A Blocked document (holder CUIT of another person) cannot be confirmed; it
    /// can only be deleted. A NeedsReview that belongs to an EXTRACTION (the document has a current
    /// extraction run) is refused with 409: that review is POST /api/documents/{id}/extraction/review.
    /// </summary>
    /// <param name="id">The document identifier.</param>
    /// <param name="request">The confirmed type, optional period end, and reason.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="DocumentResponse"/>.</returns>
    [HttpPost("{id:guid}/classification/confirm")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(DocumentResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> ConfirmClassificationAsync(
        Guid id,
        [FromBody] ConfirmClassificationRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var tenantId = tenantContext.TenantId;

        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < MinReasonLength)
            return ValidationProblem("reason", $"El motivo es obligatorio (al menos {MinReasonLength} caracteres).");

        var documentType = DocumentTypes.Canonical(request!.DocumentType, DocumentTypes.LegajoTypes);
        if (documentType is null || documentType == DocumentTypes.AutorizacionDatos)
            return ValidationProblem("documentType",
                "documentType debe ser un tipo de documento de legajo distinto de AutorizacionDatos.");

        var document = await _documentRepository.GetByIdAsync(id, tenantId, cancellationToken).ConfigureAwait(false);
        if (document is null || document.IsDeleted)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el documento.");

        if (document.PersonLegajoId is null)
            return CodedProblem(400, "Bad Request", "not_legajo_document",
                "Solo se confirman documentos de un legajo de persona humana.");

        if (document.LegajoStatus == LegajoStatuses.Blocked)
            return CodedProblem(409, "Conflict", "document_blocked",
                "El documento está bloqueado porque corresponde a otra persona. No puede confirmarse; solo puede eliminarse.");

        if (document.LegajoStatus != LegajoStatuses.NeedsReview)
            return CodedProblem(409, "Conflict", "invalid_status",
                "Solo puede confirmarse un documento en estado NeedsReview.");

        if (await _legajoRepository.HasCurrentExtractionAsync(id, tenantId, cancellationToken).ConfigureAwait(false))
            return CodedProblem(409, "Conflict", "classification_already_confirmed",
                "La clasificación ya fue confirmada; el documento espera la revisión de su extracción.");

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        var now       = DateTime.UtcNow;
        var periodEnd = request.PeriodEnd?.ToDateTime(TimeOnly.MinValue);

        if (!await _legajoRepository
                .ConfirmClassificationAsync(id, tenantId, documentType, periodEnd, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return CodedProblem(409, "Conflict", "status_changed",
                "El documento cambió de estado; recargue e intente nuevamente.");
        }

        // The audit row carries the analyst's reason and the confirmed values — never the holder CUIT.
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Document.ClassificationConfirmed",
            severity:     "Info",
            entityType:   "Document",
            entityId:     id.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new
            {
                documentType = document.DocumentType,
                periodEnd    = ToDateOnly(document.PeriodEnd)
            }),
            newValueJson: JsonSerializer.Serialize(new
            {
                documentType,
                periodEnd = request.PeriodEnd ?? ToDateOnly(document.PeriodEnd),
                reason
            }),
            ct: CancellationToken.None);

        // On to extraction. A type without an extractor loses that claim and waits for Phase 3.
        _pipelineQueue.Enqueue(new LegajoWorkItem(id, tenantId));

        _logger.LogInformation(
            "Legajo document {DocumentId} classification confirmed for tenant {TenantId}", id, tenantId);

        document.DocumentType          = documentType;
        document.PeriodEnd             = periodEnd ?? document.PeriodEnd;
        document.LegajoStatus          = LegajoStatuses.ReadyForExtraction;
        document.LegajoStatusUpdatedAt = now;
        return Ok(MapToResponse(document));
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// The legajo branch of <see cref="UploadAsync"/>:
    /// <list type="number">
    /// <item>documentType must be one of <see cref="DocumentTypes.LegajoTypes"/>;</item>
    /// <item>the entity and the legajo must exist in the tenant (404), and the legajo must be the one
    /// linked to the entity or the legajo of one of its ACTIVE fiadores (M10 Phase 5) (400) — the
    /// document keeps the entity's EntityId either way;</item>
    /// <item>a non-deleted document with the same SHA-256 already in this legajo → 409, with its
    /// documentId in the ProblemDetails extensions;</item>
    /// <item>pages are measured locally (PageCount, PdfType, PageQualityJson) — an unreadable PDF does
    /// not fail the upload;</item>
    /// <item>LegajoStatus = Uploaded, then Queued + enqueued when the legajo has consent. An
    /// AutorizacionDatos document goes straight to Ready and is NEVER sent to the model.</item>
    /// </list>
    /// The file name is not logged here: a person's documents are often named after them.
    /// </summary>
    private async Task<IActionResult> UploadToLegajoAsync(
        IFormFile         file,
        Guid              entityId,
        string            documentType,
        int?              fiscalYear,
        Guid              personLegajoId,
        string            tenantId,
        CancellationToken cancellationToken)
    {
        var canonicalType = DocumentTypes.Canonical(documentType, DocumentTypes.LegajoTypes);
        if (canonicalType is null)
            return ValidationProblem("documentType",
                "documentType debe ser uno de: " + string.Join(", ", DocumentTypes.LegajoTypes) + ".");

        var entity = await _entityRepository
            .GetByIdAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
            return Problem(statusCode: 404, title: "Not Found",
                detail: $"No se encontró la entidad '{entityId}' para este tenant.");

        var legajo = await _legajoRepository
            .GetLegajoSummaryAsync(personLegajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (legajo is null)
            return Problem(statusCode: 404, title: "Not Found",
                detail: $"No se encontró el legajo '{personLegajoId}' para este tenant.");

        // The entity's own legajo, or the legajo of one of its ACTIVE fiadores (M10 Phase 5): the fiador's
        // document keeps the PyME's EntityId (Documents.EntityId is NOT NULL) and carries the fiador's
        // PersonLegajoId. Everything after this is per legajo, consent included.
        if (entity.PersonLegajoId != personLegajoId
            && !await _fiadorRepository
                .HasActiveFiadorWithLegajoAsync(entityId, personLegajoId, tenantId, cancellationToken)
                .ConfigureAwait(false))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El legajo indicado no corresponde a la entidad.");

        // Resolve the caller BEFORE anything is written, as the legacy path does.
        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        byte[] bytes;
        using (var memoryStream = new MemoryStream())
        {
            await file.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            bytes = memoryStream.ToArray();
        }

        var hashHex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // ── Dedupe within the legajo ─────────────────────────────────────────────
        var existingId = await _legajoRepository
            .FindActiveByHashAsync(personLegajoId, tenantId, hashHex, cancellationToken)
            .ConfigureAwait(false);

        if (existingId is { } duplicateOf)
            return DuplicateDocumentProblem(duplicateOf);

        // ── Page text quality (local, never fails the upload) ────────────────────
        var documentId = Guid.NewGuid();
        var quality = PageTextQualityAnalyzer.Analyze(bytes, (ex, page) =>
            _logger.LogWarning(
                "Page-quality read failed for legajo document {DocumentId} tenant {TenantId} (page {Page}): {ExceptionType}",
                documentId, tenantId, page?.ToString() ?? "all", ex.GetType().Name));

        // ── Persist file and record ──────────────────────────────────────────────
        string storagePath;
        using (var content = new MemoryStream(bytes, writable: false))
        {
            storagePath = await _storageService
                .SaveAsync(tenantId, documentId, content, cancellationToken)
                .ConfigureAwait(false);
        }

        var isAuthorization = canonicalType == DocumentTypes.AutorizacionDatos;
        var now = DateTime.UtcNow;
        var document = new Document
        {
            DocumentId            = documentId,
            TenantId              = tenantId,
            EntityId              = entityId,
            UploadedBy            = userId.Value,
            OriginalFileName      = Path.GetFileName(file.FileName),
            StoredFileName        = $"{documentId}.pdf",
            StoragePath           = storagePath,
            FileSizeBytes         = file.Length,
            PageCount             = quality.PageCount,
            MimeType              = PdfMimeType,
            FileHashSHA256        = hashHex,
            DocumentType          = canonicalType,
            FiscalYear            = fiscalYear,
            PdfType               = quality.PdfType,
            ProcessingStatus      = "Pending",
            UploadedAt            = now,
            RetentionExpiresAt    = now.AddYears(7),
            IsDeleted             = false,
            PersonLegajoId        = personLegajoId,
            // The authorization itself is never classified: it is the document consent is recorded
            // against, so it is Ready the moment it is stored.
            LegajoStatus          = isAuthorization ? LegajoStatuses.Ready : LegajoStatuses.Uploaded,
            LegajoStatusUpdatedAt = now,
            PageQualityJson       = quality.ToJson()
        };

        // UX_Documents_Legajo_Hash closes the race the pre-check above cannot (a double-click): the
        // loser gets the same 409 as a plain duplicate, never a 500.
        var raceWinner = await _legajoRepository.CreateLegajoDocumentAsync(document, cancellationToken)
            .ConfigureAwait(false);
        if (raceWinner is { } winner)
            return DuplicateDocumentProblem(winner);

        // ── Queue for classification when consent is active ─────────────────────
        // Consent is read AFTER the insert, not reused from before it: a consent POST that ran while
        // this upload was in flight queued only the documents that existed then, so this one must see
        // the consent it recorded.
        if (!isAuthorization
            && await _legajoRepository.HasConsentAsync(personLegajoId, tenantId, cancellationToken).ConfigureAwait(false)
            && await _legajoRepository
                .TryTransitionAsync(documentId, tenantId, LegajoStatuses.Uploaded, LegajoStatuses.Queued, now,
                                    cancellationToken)
                .ConfigureAwait(false))
        {
            document.LegajoStatus = LegajoStatuses.Queued;
            _pipelineQueue.Enqueue(new LegajoWorkItem(documentId, tenantId));
        }

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     userId,
            action:     "Document.Uploaded",
            severity:   "Info",
            entityType: "Document",
            entityId:   documentId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Legajo document uploaded: {DocumentId} ({Bytes} bytes, {PageCount} pages, {PdfType}) into legajo {PersonLegajoId} status {LegajoStatus} tenant {TenantId}",
            documentId, document.FileSizeBytes, document.PageCount, document.PdfType ?? "unknown",
            personLegajoId, document.LegajoStatus, tenantId);

        return CreatedAtAction(nameof(GetById), new { id = documentId }, MapToResponse(document));
    }

    /// <summary>The caller's [dbo].[Users].UserId from the token's uid/sub claim, or null.</summary>
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

    /// <summary>409 duplicate_document, carrying the id of the document already in the legajo.</summary>
    private ObjectResult DuplicateDocumentProblem(Guid existingDocumentId)
    {
        var conflict = ConflictProblem("Este archivo ya fue cargado en el legajo.", "duplicate_document");
        ((ProblemDetails)conflict.Value!).Extensions["documentId"] = existingDocumentId;
        return conflict;
    }

    /// <summary>A 409 ProblemDetails with an <c>errorCode</c> extension the Portal can branch on.</summary>
    private ObjectResult ConflictProblem(string detail, string errorCode)
    {
        var problem = new ProblemDetails
        {
            Status   = 409,
            Title    = "Conflict",
            Detail   = detail,
            Type     = $"https://serviciosfinancieros.api/errors/{errorCode}",
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = errorCode;
        return StatusCode(409, problem);
    }

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

    /// <summary>
    /// Fills <c>failureReason</c>, <c>causeGroup</c> and <c>attemptsRemaining</c> on the company documents of
    /// <paramref name="responses"/> (index-aligned with <paramref name="documents"/>) — see
    /// <see cref="DocumentFailureFlags"/>. The attempts are read in ONE batched per-hash query, only when the
    /// budget is on and some row needs them. FAULT-ISOLATED: a failed read logs a Warning and leaves
    /// <c>attemptsRemaining</c> null; the reason and cause group need no read at all.
    /// </summary>
    private async Task FillFailureFlagsAsync(
        IReadOnlyList<Document>         documents,
        IReadOnlyList<DocumentResponse> responses,
        string                          tenantId,
        CancellationToken               cancellationToken)
    {
        var maxAttempts = _billingOptions?.Value?.MaxExtractionAttemptsPerDocument ?? 0;

        var hashes = documents
            .Where(DocumentFailureFlags.NeedsAttempts)
            .Select(d => d.FileHashSHA256)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyDictionary<string, int>? attemptsByHash = null;
        if (maxAttempts > 0 && hashes.Count > 0)
        {
            try
            {
                attemptsByHash = await _runListRepository
                    .SumExtractionAttemptsByHashesAsync(hashes, tenantId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Extraction attempts could not be read for the documents of tenant {TenantId} ({ExceptionType}); " +
                    "reporting attemptsRemaining as unknown.",
                    tenantId, ex.GetType().Name);
            }
        }

        for (var i = 0; i < documents.Count; i++)
            DocumentFailureFlags.Apply(responses[i], documents[i], attemptsByHash, maxAttempts);
    }

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static DocumentResponse MapToResponse(Document d) => new()
    {
        DocumentId       = d.DocumentId,
        TenantId         = d.TenantId,
        EntityId         = d.EntityId,
        UploadedBy       = d.UploadedBy,
        OriginalFileName = d.OriginalFileName,
        FileSizeBytes    = d.FileSizeBytes,
        PageCount        = d.PageCount,
        MimeType         = d.MimeType,
        FileHashSHA256   = d.FileHashSHA256,
        DocumentType     = d.DocumentType,
        FiscalYear       = d.FiscalYear,
        PdfType          = d.PdfType,
        ProcessingStatus = d.ProcessingStatus,
        UploadedAt       = d.UploadedAt,
        ProcessedAt      = d.ProcessedAt,
        Source           = d.Source,
        SourceRef        = d.SourceRef,
        PersonLegajoId   = d.PersonLegajoId,
        LegajoStatus     = d.LegajoStatus,
        PeriodEnd        = ToDateOnly(d.PeriodEnd),
        EngagementType   = d.EngagementType
    };

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
