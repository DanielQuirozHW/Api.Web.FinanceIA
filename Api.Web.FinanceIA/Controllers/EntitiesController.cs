// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Concurrency;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.NosisReport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// CRUD endpoints for PyME entities within the current tenant.
/// All endpoints require an internal JWT (<c>Internal</c> scheme).
/// Tenant isolation is enforced: every query and mutation is scoped to
/// the TenantId resolved from <see cref="ITenantContextAccessor"/>.
/// </summary>
[ApiController]
[Route("api/entities")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed partial class EntitiesController : ControllerBase
{
    private static readonly Regex CuitRegex =
        new(@"^\d{2}-\d{8}-\d{1}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private const string LufeDocumentHost = "legajounicoapi.produccion.gob.ar";
    private const string ManualLufeImportAuditAction = "Document.ImportedFromLufe";
    private const string AutoLufeImportAuditAction   = "Document.AutoImportedFromLufe";

    /// <summary>Cap on LUFE balance documents imported automatically per refresh / create.</summary>
    private const int MaxAutoImportBalances = 3;

    /// <summary>Per-download bound for the automatic balance import.</summary>
    private static readonly TimeSpan LufeAutoImportDownloadTimeout = TimeSpan.FromSeconds(30);

    private readonly ITenantContextAccessor      _tenantContextAccessor;
    private readonly IEntityRepository            _entityRepository;
    private readonly IArcaPadronService            _arcaPadronService;
    private readonly IArcaConstanciaService        _arcaConstanciaService;
    private readonly IArcaWsaaService              _arcaWsaaService;
    private readonly IApiCallLogService            _apiCallLogService;
    private readonly IAuditService                 _auditService;
    private readonly ILufeService                  _lufeService;
    private readonly IBcraService                  _bcraService;
    private readonly ICasfogService                _casfogService;
    private readonly INosisService                 _nosisService;
    private readonly IIntegrationAccessService     _integrationAccess;
    private readonly IDocumentRepository           _documentRepository;
    private readonly IDocumentStorageService       _storageService;
    private readonly IUserRepository               _userRepository;
    private readonly ICreditLineRepository         _creditLineRepository;
    private readonly IAnalysisRepository           _analysisRepository;
    private readonly INoteRepository               _noteRepository;
    private readonly IPersonLegajoRepository       _personLegajoRepository;
    private readonly INosisReportRepository        _nosisReportRepository;
    private readonly INosisManagerReportParser     _nosisReportParser;
    private readonly KeyedAsyncLock                _nosisReportLock;
    private readonly IConfiguration                _configuration;
    private readonly ILogger<EntitiesController>   _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EntitiesController"/>.
    /// </summary>
    public EntitiesController(
        ITenantContextAccessor      tenantContextAccessor,
        IEntityRepository           entityRepository,
        IArcaPadronService          arcaPadronService,
        IArcaConstanciaService      arcaConstanciaService,
        IArcaWsaaService            arcaWsaaService,
        IApiCallLogService          apiCallLogService,
        IAuditService               auditService,
        ILufeService                lufeService,
        IBcraService                bcraService,
        ICasfogService              casfogService,
        INosisService               nosisService,
        IIntegrationAccessService   integrationAccess,
        IDocumentRepository         documentRepository,
        IDocumentStorageService     storageService,
        IUserRepository             userRepository,
        ICreditLineRepository       creditLineRepository,
        IAnalysisRepository         analysisRepository,
        INoteRepository             noteRepository,
        IPersonLegajoRepository     personLegajoRepository,
        INosisReportRepository      nosisReportRepository,
        INosisManagerReportParser   nosisReportParser,
        KeyedAsyncLock              nosisReportLock,
        IConfiguration              configuration,
        ILogger<EntitiesController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _entityRepository      = entityRepository;
        _arcaPadronService     = arcaPadronService;
        _arcaConstanciaService = arcaConstanciaService;
        _arcaWsaaService       = arcaWsaaService;
        _apiCallLogService     = apiCallLogService;
        _auditService          = auditService;
        _lufeService           = lufeService;
        _bcraService           = bcraService;
        _casfogService         = casfogService;
        _nosisService          = nosisService;
        _integrationAccess     = integrationAccess;
        _documentRepository    = documentRepository;
        _storageService        = storageService;
        _userRepository        = userRepository;
        _creditLineRepository  = creditLineRepository;
        _analysisRepository    = analysisRepository;
        _noteRepository        = noteRepository;
        _personLegajoRepository = personLegajoRepository;
        _nosisReportRepository = nosisReportRepository;
        _nosisReportParser     = nosisReportParser;
        _nosisReportLock       = nosisReportLock;
        _configuration         = configuration;
        _logger                = logger;
    }

    /// <summary>
    /// Returns a paginated list of all entities for the current tenant.
    /// <para>
    /// Rows are the narrow <see cref="EntityListItemResponse"/>: the five source snapshots the
    /// table never renders are neither deserialised nor shipped. Fetch GET /api/entities/{id}
    /// for those.
    /// </para>
    /// </summary>
    /// <param name="page">1-based page number. Default: 1.</param>
    /// <param name="pageSize">Items per page. Default: 20.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="EntityListResponse"/>.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(EntityListResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetAllAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        // Ordering and paging happen in SQL. The repository projects to EntityListProjection, so the
        // five nvarchar(max) source snapshots are never read off disk — #138 stopped deserialising
        // them, this stops selecting them. Total is a COUNT(*), not a List.Count.
        var (page_rows, total) = await _entityRepository
            .GetListPageByTenantAsync(tenantId, (page - 1) * pageSize, pageSize, cancellationToken)
            .ConfigureAwait(false);

        var items = page_rows.Select(MapToListItem).ToList();

        // Project author name, hasCreditLine and latestAnalysisStatus for the page (set-based).
        var latestScores = await EnrichProjectionAsync(items, tenantId, cancellationToken).ConfigureAwait(false);

        // Whether each latest score is provisional — list only, from the rows the projection already read.
        ApplyLatestScoreProvisional(items, latestScores, tenantId);

        // Then the main cupo's amount/disponible/currency — list only, one more set-based query.
        await EnrichMainCreditLineAsync(items, tenantId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Listed {Count}/{Total} entities for tenant {TenantId} (page {Page}/{PageSize})",
            items.Count, total, tenantContext.TenantId, page, pageSize);

        return Ok(new EntityListResponse
        {
            Items    = items,
            Page     = page,
            PageSize = pageSize,
            Total    = total
        });
    }

    /// <summary>
    /// Returns a single entity by its primary key.
    /// <para>
    /// Carries the FISCAL and LUFE snapshots, which the detail view renders on load. It does NOT
    /// carry BCRA, CASFOG or NOSIS: those render only when the user opens their tab, and are read
    /// then from <c>GET {id}/bcra</c>, <c>{id}/casfog</c> and <c>{id}/nosis</c>. Their
    /// <c>*DataUpdatedAt</c> timestamps are still here, so the client can render "last refreshed"
    /// without fetching a payload. The query behind this never selects those three
    /// <c>nvarchar(max)</c> columns — see <see cref="EntityDetailProjection"/>.
    /// </para>
    ///
    /// <para>
    /// A null timestamp does NOT mean the source has no data — see
    /// <see cref="EntityDetailProjection"/> for why. Callers deciding whether a source has anything
    /// to show must ask the per-source endpoint, not infer it from this response.
    /// </para>
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="EntityResponse"/>, or 404 Not Found.</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetByIdAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var detail = await _entityRepository
            .GetDetailByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        var response = MapToDetailResponse(detail);
        await EnrichProjectionAsync([response], tenantId, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    // ── GET /api/entities/{id}/label ──────────────────────────────────────────────

    /// <summary>
    /// Returns just the business name and CUIT of one entity — the breadcrumb label on the PyME
    /// detail page. Read-only: three narrow columns, no external call, no write.
    ///
    /// <para>
    /// Split out of <see cref="GetById"/> for the same reason /ratios was split out of the analysis
    /// detail. The Portal's breadcrumb hook runs in its shell, OUTSIDE the page transition, so it is
    /// a SECOND read on every navigation to an entity detail — and it was fetching the whole detail
    /// to display one string. That detail still names FiscalDataJson and LufeDataJson, and LUFE
    /// alone is ~26KB on a representative PyME, so a breadcrumb paid for two <c>nvarchar(max)</c>
    /// columns and eleven scalars it never renders.
    /// </para>
    ///
    /// <para>
    /// <c>businessName</c> and <c>cuit</c> are byte-for-byte what <see cref="GetById"/> already
    /// returns for those two fields — same names, same types — so a caller swaps the URL and changes
    /// nothing else. The detail response still carries both; this is purely additive.
    /// </para>
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="EntityLabelResponse"/>, or 404 Not Found if there is no such entity in
    /// this tenant. Neither field is ever null on a 200 — both columns are NOT NULL.
    /// </returns>
    [HttpGet("{id:guid}/label")]
    [ProducesResponseType(typeof(EntityLabelResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetLabelAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetLabelAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var name = await _entityRepository
            .GetNameByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (name is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        return Ok(new EntityLabelResponse
        {
            BusinessName = name.BusinessName,
            CUIT         = name.CUIT
        });
    }

    // ── On-demand source snapshots ────────────────────────────────────────────────
    // The three snapshots GetById stopped shipping. Each READS the stored column and deserialises
    // it — no external call, no write, no side effect. Re-consulting the source is a different
    // operation with a different verb: POST {id}/refresh-bcra, /refresh-casfog, /refresh-nosis.
    // Keeping them apart matters most for NOSIS, which is billed per query: opening a tab must
    // never spend money.

    /// <summary>
    /// Returns the STORED BCRA snapshot for an entity. Read-only and idempotent — it does not
    /// consult BCRA. Use POST <c>{id}/refresh-bcra</c> to re-fetch from the source.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="EntityBcraResponse"/> — <c>bcra</c> is null when the entity exists but
    /// BCRA has never been fetched for it — or 404 Not Found if the entity does not exist.
    /// </returns>
    [HttpGet("{id:guid}/bcra")]
    [ProducesResponseType(typeof(EntityBcraResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetBcraAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetBcraAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var snapshot = await _entityRepository
            .GetBcraSnapshotAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        return Ok(new EntityBcraResponse
        {
            Bcra              = ParseSnapshot<BcraData>(snapshot.Json, snapshot.EntityId, "BcraDataJson"),
            BcraDataUpdatedAt = snapshot.UpdatedAt
        });
    }

    /// <summary>
    /// Returns the STORED CASFOG snapshot for an entity. Read-only and idempotent — it does not
    /// consult CASFOG. Use POST <c>{id}/refresh-casfog</c> to re-fetch from the source.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="EntityCasfogResponse"/> — <c>casfog</c> is null when the entity exists
    /// but CASFOG has never been fetched for it — or 404 Not Found if the entity does not exist.
    /// </returns>
    [HttpGet("{id:guid}/casfog")]
    [ProducesResponseType(typeof(EntityCasfogResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetCasfogAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetCasfogAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var snapshot = await _entityRepository
            .GetCasfogSnapshotAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        return Ok(new EntityCasfogResponse
        {
            Casfog              = ParseSnapshot<CasfogData>(snapshot.Json, snapshot.EntityId, "CasfogDataJson"),
            CasfogDataUpdatedAt = snapshot.UpdatedAt
        });
    }

    /// <summary>
    /// Returns the STORED NOSIS snapshot for an entity. Read-only and idempotent — it does not
    /// consult NOSIS, which is billed per query. Use POST <c>{id}/refresh-nosis</c> to re-fetch
    /// from the source and incur that cost deliberately.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="EntityNosisResponse"/> — <c>nosis</c> is null when the entity exists
    /// but NOSIS has never been fetched for it, which is the norm for a newly created entity, and
    /// <c>report</c> is null until a NOSIS Manager report is imported — or 404 Not Found if the entity
    /// does not exist.
    /// </returns>
    [HttpGet("{id:guid}/nosis")]
    [ProducesResponseType(typeof(EntityNosisResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetNosisAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetNosisAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var snapshot = await _entityRepository
            .GetNosisSnapshotAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        var report = await _nosisReportRepository
            .GetAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new EntityNosisResponse
        {
            Nosis              = ParseSnapshot<NosisData>(snapshot.Json, snapshot.EntityId, "NosisDataJson"),
            NosisDataUpdatedAt = snapshot.UpdatedAt,
            Report             = ParseNosisReport(report?.Json, snapshot.EntityId)
        });
    }

    /// <summary>
    /// Returns all PyME-level internal notes for an entity, oldest first.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the list of <see cref="NoteResponse"/>, or 404 Not Found.</returns>
    [HttpGet("{id:guid}/notes")]
    [ProducesResponseType(typeof(List<NoteResponse>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetNotes(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetNotes");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        // Existence only — the entity is never read for a field here, so this must not be a
        // full-row load dragging five nvarchar(max) snapshots off disk. See IEntityRepository.
        var exists = await _entityRepository
            .ExistsByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        var notes = await _noteRepository
            .GetByEntityAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(notes.Select(MapToNoteResponse).ToList());
    }

    /// <summary>
    /// Creates a new PyME-level internal note for an entity.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="request">The note content.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>201 Created with the created <see cref="NoteResponse"/>.</returns>
    [HttpPost("{id:guid}/notes")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(NoteResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> CreateNote(
        Guid id,
        [FromBody] CreateNoteRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.CreateNote");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return ValidationProblem("Content", "El contenido es obligatorio.");
        }

        if (request.Content.Length > 4000)
        {
            return ValidationProblem("Content", "El contenido no puede superar los 4000 caracteres.");
        }

        // Existence only — the note is built from the request and the caller, never from the
        // entity, so this must not be a full-row blob load. See IEntityRepository.
        var exists = await _entityRepository
            .ExistsByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository.GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (currentUser is null)
            return Unauthorized();

        var note = new Note
        {
            EntityId     = id,
            AnalysisId   = null,
            TenantId     = tenantId,
            Content      = request.Content.Trim(),
            AuthorUserId = currentUser.UserId,
            AuthorName   = currentUser.DisplayName,
            CreatedAt    = DateTime.UtcNow
        };

        await _noteRepository.CreateAsync(note, cancellationToken)
            .ConfigureAwait(false);

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     currentUser.UserId,
            action:     "Note.Created",
            severity:   "Info",
            entityType: "Entity",
            entityId:   id.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Note created: {NoteId} for entity {EntityId} tenant {TenantId}",
            note.NoteId, note.EntityId, tenantId);

        return CreatedAtAction(
            nameof(GetNotes),
            new { id },
            MapToNoteResponse(note));
    }

    /// <summary>
    /// Soft-deletes a PyME-level internal note (tombstone). The row is retained for audit; the note's
    /// content is never returned again. A user may delete their own note; an Admin may delete any note
    /// in the tenant.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="noteId">The note identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>204 No Content on success; 401/403/404 otherwise.</returns>
    [HttpDelete("{id:guid}/notes/{noteId:long}")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> DeleteNote(
        Guid id,
        long noteId,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.DeleteNote");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        // Existence only — the delete is authorised off the note and the caller, never off the
        // entity, so this must not be a full-row blob load. See IEntityRepository.
        var exists = await _entityRepository
            .ExistsByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository.GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (currentUser is null)
            return Unauthorized();

        var isAdmin = string.Equals(currentUser.Role, "Admin", StringComparison.OrdinalIgnoreCase);

        var result = await _noteRepository
            .SoftDeleteAsync(noteId, tenantId, currentUser.UserId, isAdmin, cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case SoftDeleteResult.NotFound:
                return Problem(
                    statusCode: 404,
                    title: "Not Found",
                    detail: "No se encontró la nota.");

            case SoftDeleteResult.Forbidden:
                return Problem(
                    statusCode: 403,
                    title: "Forbidden",
                    detail: "Solo se pueden eliminar las notas propias.");
        }

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     currentUser.UserId,
            action:     "Note.Deleted",
            severity:   "Info",
            entityType: "Entity",
            entityId:   id.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Note deleted: {NoteId} for entity {EntityId} tenant {TenantId}",
            noteId, id, tenantId);

        return NoContent();
    }

    /// <summary>
    /// Returns a single entity by CUIT.
    /// </summary>
    /// <param name="cuit">The CUIT identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="EntityResponse"/>, or 404 Not Found.</returns>
    [HttpGet("cuit/{cuit}")]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetByCuitAsync(
        string cuit,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.GetByCuitAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var entity = await _entityRepository
            .GetByCuitAsync(cuit, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        return Ok(await BuildEntityResponseAsync(entity, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Creates a new entity for the current tenant.
    /// Returns 409 Conflict if an entity with the same CUIT already exists in this tenant — the same
    /// CUIT may exist once per tenant (UQ_Entities_TenantId_CUIT, DDL 069).
    /// <para>
    /// Persona humana (DDL 069): the person type is resolved by <see cref="CuitHelper.ResolvePersonType"/>
    /// from the CUIT prefix and the optional <c>personType</c>. A Humana gets — or is linked to the
    /// tenant's existing — <see cref="PersonLegajo"/>, written in the SAME SaveChanges as the entity:
    /// the legajo is part of the entity, not an enrichment. The BCRA snapshot the create already
    /// fetches is copied onto the legajo; no extra vendor call is made.
    /// </para>
    /// </summary>
    /// <param name="request">Entity creation payload.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>201 Created with a Location header and the created <see cref="EntityResponse"/>.</returns>
    [HttpPost]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateEntityRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.CreateAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        if (!CuitRegex.IsMatch(request.CUIT ?? string.Empty))
        {
            return ValidationProblem("CUIT", "El CUIT debe tener el formato DD-DDDDDDDD-D.");
        }

        // Normalized BEFORE the emptiness check, so a name that is only quotes or control characters
        // gets the same 400 as a blank one. See BusinessNameNormalizer for what changes and what not.
        var businessName = BusinessNameNormalizer.Normalize(request.BusinessName);
        if (businessName.Length == 0)
        {
            return ValidationProblem("BusinessName", "La razón social es obligatoria.");
        }

        if (!string.IsNullOrWhiteSpace(request.PersonType) && PersonTypes.Canonicalize(request.PersonType) is null)
        {
            return ValidationProblem("PersonType", "El tipo de persona debe ser 'Juridica' o 'Humana'.");
        }

        // request.CUIT is non-null here — CuitRegex.IsMatch would have returned false on null.
        var normalizedCuit = request.CUIT!;

        var existing = await _entityRepository
            .GetByCuitAsync(normalizedCuit, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return CuitConflict(normalizedCuit);
        }

        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository.GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (currentUser is null)
            return Unauthorized();

        // ── Person type and fiscal regime (DDL 069) ───────────────────────────────────────────
        // The prefix decides when it can (20/23/24/27 Humana, 30/33/34 Juridica); request.PersonType
        // only breaks the tie for any other prefix. ARCA is not called here — see the note below.
        var personType = CuitHelper.ResolvePersonType(normalizedCuit, null, request.PersonType, _logger);
        var isHumana   = personType == PersonTypes.Humana;

        var fiscalJson = request.FiscalData is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(request.FiscalData);

        var now    = DateTime.UtcNow;
        var entity = new Entity
        {
            EntityId             = Guid.NewGuid(),
            TenantId             = tenantId,
            CUIT                 = normalizedCuit,
            BusinessName         = businessName,
            TradeName            = request.TradeName?.Trim(),
            Sector               = request.Sector?.Trim(),
            MipymeCategory       = NormalizeMipymeCategoria(request.MipymeCategory),
            SgrOneClickEntityId  = request.SgrOneClickEntityId?.Trim(),
            IsMonitored          = request.IsMonitored,
            FiscalDataJson       = fiscalJson,
            PersonType           = personType,
            FiscalRegime         = ResolveRequestedFiscalRegime(request.FiscalData?.FiscalRegime, isHumana),
            CreatedBy            = currentUser.UserId,
            CreatedAt            = now,
            UpdatedAt            = now
        };

        // A Humana is created together with its legajo, in one SaveChanges; a Juridica alone. Either
        // write can lose a race against the per-tenant CUIT index the pre-check above cannot close,
        // and that is the same conflict the pre-check reports — 409, not 500.
        PersonLegajo? legajo = null;
        try
        {
            if (isHumana)
            {
                legajo = await CreateWithLegajoAsync(
                        entity,
                        FiscalRegimes.NormalizeMonotributoCategory(request.FiscalData?.MonotributoCategoria),
                        fiscalJson,
                        ArcaActivityStart.From(request.FiscalData),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _entityRepository.CreateAsync(entity, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (DuplicateCuitException)
        {
            return CuitConflict(normalizedCuit);
        }

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     currentUser.UserId,
            action:     "Entity.Created",
            severity:   "Info",
            entityType: "Entity",
            entityId:   entity.EntityId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        if (legajo is not null)
        {
            // No CUIT here: for a persona humana it is a CUIL, which is personal data.
            _logger.LogInformation(
                "Entity created: {EntityId} ({PersonType}) with legajo {PersonLegajoId} for tenant {TenantId}",
                entity.EntityId,
                entity.PersonType,
                legajo.PersonLegajoId,
                tenantContext.TenantId);
        }
        else
        {
            _logger.LogInformation(
                "Entity created: {EntityId} CUIT {CUIT} for tenant {TenantId}",
                entity.EntityId,
                entity.CUIT,
                tenantContext.TenantId);
        }

        // ── Tenant profile (§7.0b): which OPTIONAL integrations this tenant may use ──────────
        // Resolved ONCE for the whole fan-out and consulted in front of the LUFE and CASFOG blocks
        // below. Two gates, in this order: entitlement (may the tenant use the vendor at all —
        // the profile), then availability (can this installation reach it — the LUFE key, the
        // CASFOG credentials). A disabled profile short-circuits before either config lookup or
        // network call. BCRA is a base integration and is not gated (ARCA is too, but it is not
        // called on create: the client sends the fiscal snapshot it got from lookup-arca).
        // CancellationToken.None, like the BCRA and CASFOG calls beside it: this section is
        // deliberately decoupled from the request token (see the TECH DEBT note below).
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, CancellationToken.None)
            .ConfigureAwait(false);

        // ── Lufe enrichment (fault-isolated) ──────────────────────────────────────────────────
        // TECH DEBT: Enrichment (Lufe/BCRA/CASFOG) runs synchronously inside CreateAsync. BCRA
        // /centraldedeudores intermittently RSTs the connection, and slow/failed lookups delay
        // entity creation. Proper fix: move enrichment to a background IHostedService + Channel<T>
        // with its own DI scope/DbContext, decoupled from the request. This also becomes the basis
        // for Phase B (auto-refresh 'always latest' for the three snapshots).
        // The synchronous timeout approach was removed because a 10 s CancelAfter on a
        // CreateLinkedTokenSource canceled in-flight successful calls (Task.WhenAll is
        // all-or-nothing). The background redesign will solve this properly — no
        // request-coupled timeout needed.
        if (!profile.LufeEnabled)
        {
            _logger.LogInformation(
                "LUFE enrichment skipped for entity {EntityId} tenant {TenantId}: the tenant profile disables LUFE.",
                entity.EntityId, tenantId);
        }
        else
        {
            try
            {
                var lufeApiKey = _integrationAccess.GetLufeApiKey(tenantId);
                if (lufeApiKey is not null)
                {
                    var lufeCuit = long.Parse(entity.CUIT.Replace("-", ""));
                    using var lufeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    // Only the data matters here: a CUIT with no legajo and an outage both store
                    // nothing, exactly as before.
                    var lufeData = (await _lufeService
                        .GetLufeDataAsync(tenantId, lufeCuit, lufeApiKey, lufeTimeout.Token)
                        .ConfigureAwait(false)).Data;

                    if (lufeData is not null && ShouldPersistLufeSnapshot(entity, lufeData))
                    {
                        entity.LufeDataJson = System.Text.Json.JsonSerializer.Serialize(lufeData);
                        entity.LufeDataUpdatedAt = DateTime.UtcNow;

                        // Same blank-only backfill as refresh-lufe, and this is the path that stops
                        // the blank being created at all: request.MipymeCategory is null in exactly
                        // the failing case (the 4 s lookup-arca prefill timed out), while this block
                        // has a fresh snapshot in hand from a 30 s call. Folded into the write above
                        // rather than a second UpdateAsync.
                        var mipymeFromLufe = NormalizeMipymeCategoria(lufeData.Entidad?.CertificadoPyme?.Categoria);
                        if (string.IsNullOrWhiteSpace(entity.MipymeCategory) && mipymeFromLufe is not null)
                        {
                            entity.MipymeCategory = mipymeFromLufe;

                            _logger.LogInformation(
                                "MiPyME category resolved from Lufe for entity {EntityId} tenant {TenantId}: {MipymeCategory}",
                                entity.EntityId, tenantId, mipymeFromLufe);
                        }

                        await _entityRepository.UpdateAsync(entity, CancellationToken.None).ConfigureAwait(false);

                        // Same automatic balance import as refresh-lufe, on the same request-
                        // decoupled token as the rest of this section. Never throws.
                        await AutoImportLufeBalancesAsync(
                                entity, lufeData, tenantId, lufeApiKey, currentUser.UserId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Lufe enrichment failed for entity {EntityId} tenant {TenantId}; entity created without Lufe data",
                    entity.EntityId, tenantId);
            }
        }

        // ── BCRA enrichment (fault-isolated) ──────────────────────────────────────────────────
        try
        {
            var bcraData = await _bcraService
                .GetBcraDataForAnalysisAsync(entity.CUIT, CancellationToken.None)
                .ConfigureAwait(false);

            if (bcraData is not null)
            {
                // The new entity has nothing to lose, but the legajo may predate it (a fiador's, or the
                // person's other entity) and already hold a snapshot. That one is never overwritten
                // with a failed Central de Deudores, and a failed cheques lookup keeps its stored
                // cheques — the same rules refresh-bcra applies. The entity stores what came back.
                var copyToLegajo = legajo is not null
                    && (legajo.BcraDataJson is null || PersonSnapshotWriter.IsDeudaAnswered(bcraData));

                if (copyToLegajo && legajo!.BcraDataJson is not null)
                {
                    PersonSnapshotWriter.KeepStoredChequesOnFailure(bcraData, legajo.BcraDataJson);
                }
                else if (legajo is not null && !copyToLegajo)
                {
                    _logger.LogWarning(
                        "BCRA Central de Deudores unavailable while creating entity {EntityId} tenant {TenantId}; the existing legajo {PersonLegajoId} keeps its stored snapshot.",
                        entity.EntityId, tenantId, legajo.PersonLegajoId);
                }

                entity.BcraDataJson = System.Text.Json.JsonSerializer.Serialize(bcraData);
                // Stamped here as well as in refresh-bcra, matching what the Lufe branch above
                // already does. Leaving it null made a freshly created entity report that it had
                // never been enriched, which is now visible: the detail response ships this
                // timestamp without the payload. Fixes new rows only — rows created before this
                // keep a populated blob with a null timestamp, which is why the timestamp is not a
                // has-data flag.
                entity.BcraDataUpdatedAt = DateTime.UtcNow;

                // A persona humana's snapshot is the PERSON's (queried by CUIL), so it is copied onto
                // the legajo too — the same payload from this one call, never a second BCRA query —
                // and written with the entity in one save. Still inside this fault-isolated block.
                if (copyToLegajo)
                {
                    PersonSnapshotWriter.CopyBcraSnapshot(entity, legajo!);
                }

                // A legajo that keeps its snapshot is not part of this save: it was already written
                // with the entity's creation, and nothing on it changed here.
                await SaveEntityAndLinkedLegajoAsync(entity, copyToLegajo ? legajo : null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "BCRA enrichment failed for entity {EntityId} tenant {TenantId}; entity created without BCRA data",
                entity.EntityId, tenantId);
        }

        // ── CASFOG enrichment (fault-isolated) ────────────────────────────────────────────────
        // Entitlement-gated by the profile resolved above. The non-SGR preset switches CASFOG off
        // (its registry is SGR-specific), so for such a tenant this block never runs; there is no
        // controller-side availability gate — CasfogService checks its own credentials.
        if (!profile.CasfogEnabled)
        {
            _logger.LogInformation(
                "CASFOG enrichment skipped for entity {EntityId} tenant {TenantId}: the tenant profile disables CASFOG.",
                entity.EntityId, tenantId);
        }
        else
        {
            try
            {
                var casfogCuit = long.Parse(entity.CUIT.Replace("-", ""));
                var casfogData = await _casfogService
                    .GetCentralDeudoresAsync(casfogCuit, CancellationToken.None)
                    .ConfigureAwait(false);

                if (casfogData.Estado != CasfogConsultaEstado.LookupFailed)
                {
                    entity.CasfogDataJson = System.Text.Json.JsonSerializer.Serialize(casfogData);
                    // Same reasoning as the BCRA branch above.
                    entity.CasfogDataUpdatedAt = DateTime.UtcNow;
                    await _entityRepository.UpdateAsync(entity, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CASFOG enrichment failed for entity {EntityId} tenant {TenantId}; entity created without CASFOG data",
                    entity.EntityId, tenantId);
            }
        }

        // NOSIS is deliberately NOT enriched here. Unlike its four siblings above it is billed per
        // query, so it is fetched on demand only — POST /api/entities/{id}/refresh-nosis — when an
        // analyst actually asks for it. A newly created entity therefore has NosisDataJson and
        // NosisDataUpdatedAt null, and an analysis run before the first refresh snapshots null.
        // EntitiesControllerCreateTests pins this; do not add an eager lookup back for symmetry.
        // The same holds for a persona humana's legajo: its NosisDataJson stays null in this phase.

        return CreatedAtAction(
            nameof(GetById),
            new { id = entity.EntityId },
            await BuildEntityResponseAsync(entity, legajo, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Updates an existing entity.
    /// Returns 409 Conflict if the new CUIT is already in use by a different entity.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="request">Entity update payload.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the updated <see cref="EntityResponse"/>.</returns>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateEntityRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.UpdateAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        if (!CuitRegex.IsMatch(request.CUIT ?? string.Empty))
        {
            return ValidationProblem("CUIT", "El CUIT debe tener el formato DD-DDDDDDDD-D.");
        }

        // Normalized BEFORE the emptiness check, so a name that is only quotes or control characters
        // gets the same 400 as a blank one. See BusinessNameNormalizer for what changes and what not.
        var businessName = BusinessNameNormalizer.Normalize(request.BusinessName);
        if (businessName.Length == 0)
        {
            return ValidationProblem("BusinessName", "La razón social es obligatoria.");
        }

        // request.CUIT is non-null here — CuitRegex.IsMatch would have returned false on null.
        var normalizedCuit = request.CUIT!;

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        // If CUIT is changing, check it is not already used by another entity in this tenant.
        var cuitChanged = !string.Equals(entity.CUIT, normalizedCuit, StringComparison.Ordinal);
        if (cuitChanged)
        {
            var cuitOwner = await _entityRepository
                .GetByCuitAsync(normalizedCuit, tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (cuitOwner is not null && cuitOwner.EntityId != id)
            {
                return CuitConflict(normalizedCuit);
            }
        }

        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository.GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (currentUser is null)
            return Unauthorized();

        entity.CUIT                = normalizedCuit;
        entity.BusinessName        = businessName;
        entity.TradeName           = request.TradeName?.Trim();
        entity.Sector              = request.Sector?.Trim();
        entity.MipymeCategory      = NormalizeMipymeCategoria(request.MipymeCategory);
        entity.SgrOneClickEntityId = request.SgrOneClickEntityId?.Trim();
        entity.IsMonitored         = request.IsMonitored;
        entity.UpdatedAt           = DateTime.UtcNow;

        // A corrected CUIT is another company: the vendor snapshots on the row were fetched for the
        // OLD CUIT and must not keep feeding scores, the detail or the PDF. Cleared here so they ride
        // the save below — no second write. Nothing is fetched for the new CUIT: the analyst refreshes
        // each source, and the next analysis queries BCRA live. Documents, analyses and the legajos
        // (the old one stays the old CUIT's person) are left alone.
        IReadOnlyList<ClearedSnapshot> clearedSnapshots = cuitChanged ? ClearVendorSnapshots(entity) : [];

        // A changed CUIT re-derives the person type and relinks the legajo (DDL 069), in the same
        // save. A lost race on the per-tenant CUIT index is the conflict the check above reports.
        PersonLegajo? legajo;
        try
        {
            legajo = await SaveUpdatedEntityAsync(entity, cuitChanged, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DuplicateCuitException)
        {
            return CuitConflict(normalizedCuit);
        }

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     currentUser.UserId,
            action:     "Entity.Updated",
            severity:   "Info",
            entityType: "Entity",
            entityId:   entity.EntityId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            // Only a CUIT correction carries JSON, and never the CUIT values themselves (no CUIT
            // enters AuditLogs): that it changed, and which snapshots it cleared with their dates.
            oldValueJson: cuitChanged
                ? System.Text.Json.JsonSerializer.Serialize(new
                {
                    cuitChanged = true,
                    snapshots   = clearedSnapshots.Select(s => new { source = s.Source, updatedAt = s.UpdatedAt })
                })
                : null,
            newValueJson: cuitChanged
                ? System.Text.Json.JsonSerializer.Serialize(new
                {
                    cuitChanged      = true,
                    clearedSnapshots = clearedSnapshots.Select(s => s.Source)
                })
                : null,
            ct:         CancellationToken.None);

        if (cuitChanged)
        {
            _logger.LogInformation(
                "CUIT corrected for entity {EntityId} tenant {TenantId}; cleared vendor snapshots: {ClearedSources}",
                entity.EntityId, tenantId,
                clearedSnapshots.Count == 0 ? "none" : string.Join(",", clearedSnapshots.Select(s => s.Source)));
        }

        _logger.LogInformation(
            "Entity updated: {EntityId} CUIT {CUIT} for tenant {TenantId}",
            entity.EntityId,
            entity.CUIT,
            tenantContext.TenantId);

        return Ok(await BuildEntityResponseAsync(entity, legajo, cancellationToken).ConfigureAwait(false));
    }

    // ── ARCA lookup endpoints ───────────────────────────────────────────────────

    /// <summary>
    /// Lookup entity data from ARCA Padron A13 by CUIT.
    /// Returns pre-filled data for entity creation. Does NOT create the entity.
    /// Returns 409 if entity already exists in the database.
    /// </summary>
    [HttpPost("lookup-arca")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(ArcaLookupResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> LookupEntityFromArca(
        [FromBody] ArcaLookupByCuitRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.LookupEntityFromArca");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        _logger.LogInformation("ARCA lookup initiated for tenant {TenantId}", tenantId);

        // Normalize CUIT (remove hyphens)
        var cuitNormalized = request.Cuit.Replace("-", "");

        // Check if entity already exists in our DB (formatted with hyphens)
        var cuitFormatted = FormatCuit(cuitNormalized);
        var existing = await _entityRepository
            .GetByCuitAsync(cuitFormatted, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _logger.LogInformation(
                "ARCA lookup skipped — entity already exists for tenant {TenantId}",
                tenantId);

            return Problem(
                statusCode: 409,
                title: "Conflict",
                detail: "Ya existe una entidad con este CUIT en su organización.");
        }

        // Get tenant's CUIT for cuitRepresentada parameter
        var cuitRepresentada = GetTenantCuit(tenantId);

        var sw = Stopwatch.StartNew();
        try
        {
            var arcaData = await _arcaConstanciaService
                .GetPersonaByCuitAsync(cuitNormalized, cuitRepresentada, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();

            if (arcaData is null)
            {
                _ = LogArcaCallAsync(tenantId, "getPersona_v2", false, (int)sw.ElapsedMilliseconds, null);

                return Problem(
                    statusCode: 404,
                    title: "Not Found",
                    detail: "No se encontró ninguna entidad en ARCA con el CUIT ingresado.");
            }

            _ = LogArcaCallAsync(tenantId, "getPersona_v2", true, (int)sw.ElapsedMilliseconds, null);

            // --- LUFE: attempt to resolve MiPyME category (fault-isolated, 4 s timeout) ---
            // Entitlement first (the tenant profile, §7.0b), then availability (the LUFE key).
            // The resolver never throws past cancellation, so it cannot reach the
            // HttpRequestException catch below and the ARCA outcome is already decided by here.
            string? mipymeCategoria = null;
            var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (!profile.LufeEnabled)
            {
                _logger.LogInformation(
                    "LUFE MiPyME lookup skipped during ARCA lookup for tenant {TenantId}: the tenant profile disables LUFE.",
                    tenantId);
            }
            else
            {
                var lufeKey = _integrationAccess.GetLufeApiKey(tenantId);
                if (lufeKey is not null)
                {
                    try
                    {
                        var lufeCuit = long.Parse(cuitNormalized);
                        using var lufeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                            lufeTimeout.Token, cancellationToken);
                        var rawCategoria = await _lufeService
                            .GetMipymeCategoriaAsync(tenantId, lufeCuit, lufeKey, linked.Token)
                            .ConfigureAwait(false);
                        mipymeCategoria = NormalizeMipymeCategoria(rawCategoria);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "LUFE enrichment skipped during ARCA lookup for tenant {TenantId}",
                            tenantId);
                    }
                }
            }

            // The same resolver POST /api/entities uses, so the type shown here is the type the entity
            // gets on create: the CUIT prefix when unambiguous, otherwise ARCA's tipoPersona.
            var personType = CuitHelper.ResolvePersonType(cuitFormatted, arcaData.TipoPersona, null, _logger);

            // The key status rides inside `fiscal`, stamped with the time it was read, so the snapshot
            // the client sends back on create carries it (create does not call ARCA).
            arcaData = arcaData with
            {
                FiscalData = PersonSnapshotWriter.WithKeyStatus(arcaData.FiscalData, arcaData.EstadoClave, DateTime.UtcNow)
            };

            var response = MapArcaToLookupResponse(arcaData, personType, mipymeCategoria);

            _logger.LogInformation("ARCA lookup successful for tenant {TenantId}", tenantId);

            return Ok(response);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _ = LogArcaCallAsync(tenantId, "getPersona_v2", false, (int)sw.ElapsedMilliseconds, ex.Message);

            _logger.LogError(ex, "ARCA API request failed for tenant {TenantId}", tenantId);

            return Problem(
                statusCode: 502,
                title: "External service unavailable",
                detail: "El servicio de ARCA no está disponible temporalmente.");
        }
    }

    /// <summary>
    /// Lookup CUIT(s) by DNI from ARCA Padron A13.
    /// Returns a list of formatted CUITs associated with the given DNI.
    /// </summary>
    [HttpPost("lookup-arca/by-dni")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(List<string>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> LookupCuitByDni(
        [FromBody] ArcaLookupByDniRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.LookupCuitByDni");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        _logger.LogInformation("ARCA DNI lookup initiated for tenant {TenantId}", tenantId);

        var cuitRepresentada = GetTenantCuit(tenantId);

        var sw = Stopwatch.StartNew();
        try
        {
            var cuits = await _arcaPadronService
                .GetCuitsByDniAsync(request.Dni, cuitRepresentada, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();

            if (cuits.Count == 0)
            {
                _ = LogArcaCallAsync(tenantId, "getIdPersonaListByDocumento", false,
                    (int)sw.ElapsedMilliseconds, null);

                return Problem(
                    statusCode: 404,
                    title: "Not Found",
                    detail: "No se encontraron CUIT para el DNI ingresado.");
            }

            _ = LogArcaCallAsync(tenantId, "getIdPersonaListByDocumento", true,
                (int)sw.ElapsedMilliseconds, null);

            var formattedCuits = cuits.Select(FormatCuit).ToList();

            return Ok(formattedCuits);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _ = LogArcaCallAsync(tenantId, "getIdPersonaListByDocumento", false,
                (int)sw.ElapsedMilliseconds, ex.Message);

            _logger.LogError(ex, "ARCA DNI lookup failed for tenant {TenantId}", tenantId);

            return Problem(
                statusCode: 502,
                title: "External service unavailable",
                detail: "El servicio de ARCA no está disponible temporalmente.");
        }
    }

    // ── Lufe endpoints ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the latest Lufe data for the given entity and persists it to <c>LufeDataJson</c>.
    /// Returns 400 if the tenant profile disables LUFE or no Lufe API key is configured for the
    /// tenant, 404 if entity not found, 404 with errorCode <c>lufe_no_legajo</c> if Lufe answered
    /// that the PyME has no legajo or has not authorized sharing it with this entity, and 502 with errorCode <c>lufe_service_unavailable</c> if the
    /// Lufe calls failed. Neither error writes anything, so a stored snapshot survives.
    /// A BLANK <c>MipymeCategory</c> is backfilled from the snapshot's
    /// <c>Entidad.CertificadoPyme.Categoria</c>; a category already set is never overwritten, so
    /// manual edits survive a refresh exactly as they do in <see cref="RefreshArcaAsync"/>.
    /// When the snapshot is stored, its balance documents are imported automatically (see
    /// <see cref="AutoImportLufeBalancesAsync"/>) and the count of new ones is returned as
    /// <see cref="EntityResponse.AutoImportedDocuments"/>; that import never changes the status.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with updated <see cref="EntityResponse"/>.</returns>
    [HttpPost("{id:guid}/refresh-lufe")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshLufeAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.RefreshLufeAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        // Entitlement (tenant profile, §7.0b) before availability (the per-tenant key). Both are
        // deliberate configuration states, so both answer 400 rather than a 502 that invites a
        // retry; the detail text tells them apart. Checked AFTER the 404 so a bad id is still
        // reported as a bad id.
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!profile.LufeEnabled)
        {
            _logger.LogInformation(
                "LUFE refresh: requested for entity {EntityId} tenant {TenantId} while the tenant profile disables LUFE.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "LUFE no está habilitado para este tenant.");
        }

        var lufeApiKey = _integrationAccess.GetLufeApiKey(tenantId);
        if (lufeApiKey is null)
        {
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "Lufe no está configurado para este tenant.");
        }

        var lufeCuit = long.Parse(entity.CUIT.Replace("-", ""));
        var lufeLookup = await _lufeService
            .GetLufeDataAsync(tenantId, lufeCuit, lufeApiKey, cancellationToken)
            .ConfigureAwait(false);

        // Three outcomes, three answers. A CUIT with no legajo is a legitimate not-found (404), not
        // a gateway error; only a transport failure is the retryable 502. Neither writes anything,
        // so a previously good snapshot is never touched. errorCode lets the Portal branch without
        // parsing the detail text.
        if (lufeLookup.Estado == LufeConsultaEstado.NoLegajoReported)
        {
            _logger.LogInformation(
                "LUFE refresh: no legajo in LUFE for entity {EntityId} tenant {TenantId}; nothing stored.",
                entity.EntityId, tenantId);
            return CodedProblem(
                statusCode: 404,
                title: "Not Found",
                detail: "La PyME no tiene legajo en LUFE o no autorizó compartirlo con esta entidad.",
                errorCode: "lufe_no_legajo");
        }

        if (lufeLookup is not { Estado: LufeConsultaEstado.LegajoFound, Data: { } lufeData })
        {
            _logger.LogWarning(
                "LUFE refresh: lookup failed for entity {EntityId} tenant {TenantId}; stored snapshot left unchanged.",
                entity.EntityId, tenantId);
            return CodedProblem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "El servicio de LUFE no está disponible en este momento. Intentá nuevamente en unos minutos.",
                errorCode: "lufe_service_unavailable");
        }

        var autoImportedDocuments = 0;

        if (ShouldPersistLufeSnapshot(entity, lufeData))
        {
            // A FAILED sales sub-call must not wipe the stored series: it is carried forward, dated,
            // and feeds the sales adjustment like a fresh one. Read before the overwrite below.
            if (LufeSalesSeriesCarryForward.Apply(lufeData, entity.LufeDataJson, entity.LufeDataUpdatedAt))
            {
                _logger.LogWarning(
                    "LUFE refresh: the monthly sales call failed for entity {EntityId} tenant {TenantId}; the stored series (as of {SeriesAsOf}) was carried forward.",
                    entity.EntityId, tenantId, lufeData.VentasComprasMensualAsOf);
            }

            entity.LufeDataJson = System.Text.Json.JsonSerializer.Serialize(lufeData);
            entity.LufeDataUpdatedAt = DateTime.UtcNow;

            // Backfill a BLANK MiPyME category from the snapshot we are already persisting. The
            // prefill on POST /api/entities/lookup-arca runs under a 4 s LUFE timeout and leaves the
            // column null whenever LUFE is slow, and nothing else ever fills it afterwards. Only a
            // blank value is filled: a category the analyst set by hand survives a refresh, matching
            // the rule RefreshArcaAsync states for these same three columns.
            // One write, not two — the category lives under Entidad.CertificadoPyme, so it is
            // readable exactly when ShouldPersistLufeSnapshot returned true (Entidad is not null).
            var mipymeFromLufe   = NormalizeMipymeCategoria(lufeData.Entidad?.CertificadoPyme?.Categoria);
            var backfilledMipyme = string.IsNullOrWhiteSpace(entity.MipymeCategory) && mipymeFromLufe is not null;

            if (backfilledMipyme)
            {
                entity.MipymeCategory = mipymeFromLufe;
            }

            await _entityRepository.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Lufe data refreshed for entity {EntityId} tenant {TenantId}",
                entity.EntityId, tenantId);

            if (backfilledMipyme)
            {
                _logger.LogInformation(
                    "MiPyME category backfilled from Lufe for entity {EntityId} tenant {TenantId}: {MipymeCategory}",
                    entity.EntityId, tenantId, mipymeFromLufe);
            }

            // The snapshot is stored — import its balance document(s) so the analyst no longer
            // has to. Fault-isolated: it never throws and never changes this response's status.
            autoImportedDocuments = await AutoImportLufeBalancesAsync(
                    entity, lufeData, tenantId, lufeApiKey, knownUserId: null, cancellationToken)
                .ConfigureAwait(false);
        }

        // When the guard preserved existing data, `entity` is the untouched DB row, so the
        // response carries the stored good Lufe data and the UNCHANGED LufeDataUpdatedAt — the
        // signal the Portal reads (before/after timestamp comparison) to know nothing landed.
        // AutoImportedDocuments rides at the TOP LEVEL of the body, beside the entity fields:
        // the Portal reads it there to show its "balance guardado" toast when it is > 0.
        var response = await BuildEntityResponseAsync(entity, null, cancellationToken).ConfigureAwait(false);
        response.AutoImportedDocuments = autoImportedDocuments;
        return Ok(response);
    }

    /// <summary>
    /// Decides whether a freshly-fetched Lufe snapshot is safe to persist over what is already
    /// stored. A snapshot whose <see cref="Models.Lufe.LufeData.Entidad"/> block is null has lost
    /// the corporate identity AND the MiPyME certificate (both live under <c>LufeData.Entidad</c>
    /// and <c>Entidad.CertificadoPyme</c>). Overwriting good stored data with such a partial
    /// snapshot destroys it, so the write is skipped when data already exists. When nothing is
    /// stored yet a partial snapshot still beats none, so it is persisted. A warning is logged
    /// either way, naming the CUIT and the missing sections.
    /// </summary>
    private bool ShouldPersistLufeSnapshot(Entity entity, Models.Lufe.LufeData lufeData)
    {
        if (lufeData.Entidad is not null)
        {
            return true;   // Identity block present — complete enough to persist.
        }

        var missing = new List<string> { "Entidad" };
        if (lufeData.Autoridades is null || lufeData.Autoridades.Length == 0) missing.Add("Autoridades");
        if (lufeData.Indicadores is null) missing.Add("Indicadores");
        if (lufeData.Documentos is null || lufeData.Documentos.Length == 0) missing.Add("Documentos");
        var missingSections = string.Join(", ", missing);

        if (!string.IsNullOrWhiteSpace(entity.LufeDataJson))
        {
            _logger.LogWarning(
                "Partial Lufe snapshot for entity {EntityId} tenant {TenantId} CUIT {CUIT} is missing " +
                "the Entidad identity block (missing sections: {MissingSections}); preserving existing " +
                "LufeDataJson and leaving LufeDataUpdatedAt unchanged.",
                entity.EntityId, entity.TenantId, entity.CUIT, missingSections);
            return false;   // Keep the stored good data.
        }

        _logger.LogWarning(
            "Partial Lufe snapshot for entity {EntityId} tenant {TenantId} CUIT {CUIT} is missing the " +
            "Entidad identity block (missing sections: {MissingSections}); no Lufe data stored yet, " +
            "persisting the partial snapshot.",
            entity.EntityId, entity.TenantId, entity.CUIT, missingSections);
        return true;        // Nothing to lose — partial data beats none.
    }

    /// <summary>
    /// Re-fetches BCRA Central de Deudores and macroeconomic data for an existing entity
    /// and persists the updated snapshot. Idempotent — safe to retry after a failed enrichment.
    /// A failed Central de Deudores is never stored: when every BCRA source failed the answer is 502
    /// <c>bcra_unavailable</c>, and when only the deuda failed it is 502 <c>bcra_deuda_unavailable</c>;
    /// neither writes the entity or its legajo, so a good stored snapshot survives. A failed cheques
    /// lookup next to a good deuda keeps the stored cheques block when it holds a real answer
    /// (<see cref="PersonSnapshotWriter.KeepStoredChequesOnFailure"/>).
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with updated <see cref="EntityResponse"/>.</returns>
    [HttpPost("{id:guid}/refresh-bcra")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshBcraAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.RefreshBcraAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        var bcraData = await _bcraService
            .GetBcraDataForAnalysisAsync(entity.CUIT, cancellationToken)
            .ConfigureAwait(false);

        if (bcraData is null)
        {
            return CodedProblem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "El servicio de BCRA no está disponible o no devolvió datos.",
                errorCode: "bcra_unavailable");
        }

        // Macro or cambiario answering does not make the snapshot usable: the deuda drives the score.
        // Nothing is written, so the stored snapshot (entity and legajo) stays exactly as it was.
        if (!PersonSnapshotWriter.IsDeudaAnswered(bcraData))
        {
            _logger.LogWarning(
                "BCRA refresh: Central de Deudores unavailable for entity {EntityId} tenant {TenantId}; stored snapshot left unchanged.",
                entity.EntityId, tenantId);
            return CodedProblem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "No se pudo consultar la Central de Deudores del BCRA. No se actualizó la información guardada; intentá nuevamente en unos minutos.",
                errorCode: "bcra_deuda_unavailable");
        }

        var keptStoredCheques = PersonSnapshotWriter.KeepStoredChequesOnFailure(bcraData, entity.BcraDataJson);

        // A persona humana's legajo carries the same person-level snapshot (DDL 069): refreshed with
        // the entity, in the same save, from this one call. PersonSnapshotWriter is the write the
        // legajo-scoped refresh-bcra shares.
        var legajo = await LoadLinkedLegajoAsync(entity, cancellationToken).ConfigureAwait(false);
        PersonSnapshotWriter.ApplyBcra(entity, legajo, bcraData, DateTime.UtcNow);

        await SaveEntityAndLinkedLegajoAsync(entity, legajo, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "BCRA data refreshed for entity {EntityId} tenant {TenantId}; stored cheques kept after a failed cheques lookup: {KeptStoredCheques}",
            entity.EntityId, tenantId, keptStoredCheques);

        return Ok(await BuildEntityResponseAsync(entity, legajo, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Re-fetches CASFOG (SGR central de deudores / garantías) data for an existing entity
    /// and persists the updated snapshot. Idempotent — safe to retry after a failed enrichment.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with updated <see cref="EntityResponse"/>.</returns>
    [HttpPost("{id:guid}/refresh-casfog")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshCasfogAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.RefreshCasfogAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        _logger.LogInformation(
            "CASFOG refresh requested for entity {EntityId} tenant {TenantId}",
            id, tenantId);

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            _logger.LogWarning(
                "CASFOG refresh: entity {EntityId} not found for tenant {TenantId}.",
                id, tenantId);
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        // Entitlement (tenant profile, §7.0b). CASFOG has no controller-side availability gate —
        // CasfogService checks its own credentials — so this is the only deliberate state the
        // endpoint can report as a 400 instead of a retry-inviting 502. Checked AFTER the 404.
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!profile.CasfogEnabled)
        {
            _logger.LogInformation(
                "CASFOG refresh: requested for entity {EntityId} tenant {TenantId} while the tenant profile disables CASFOG.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "CASFOG no está habilitado para este tenant.");
        }

        // A malformed stored CUIT must not become a raw 500 — guard the parse.
        if (!long.TryParse(entity.CUIT?.Replace("-", ""), out var casfogCuit))
        {
            _logger.LogWarning(
                "CASFOG refresh: stored CUIT for entity {EntityId} tenant {TenantId} is not a valid number; cannot query CASFOG.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "El CUIT almacenado no es válido; no se puede consultar CASFOG.");
        }

        var casfogData = await _casfogService
            .GetCentralDeudoresAsync(casfogCuit, cancellationToken)
            .ConfigureAwait(false);

        if (casfogData.Estado == CasfogConsultaEstado.LookupFailed)
        {
            _logger.LogWarning(
                "CASFOG refresh: lookup failed for entity {EntityId} tenant {TenantId}; returning 502.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "El servicio de CASFOG no está disponible o no devolvió datos.");
        }

        try
        {
            entity.CasfogDataJson = System.Text.Json.JsonSerializer.Serialize(casfogData);
            entity.CasfogDataUpdatedAt = DateTime.UtcNow;
            await _entityRepository.UpdateAsync(entity, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CASFOG refresh: failed to persist data for entity {EntityId} tenant {TenantId}.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "Failed to persist CASFOG data.");
        }

        _logger.LogInformation(
            "CASFOG data refreshed for entity {EntityId} tenant {TenantId}: estado={Estado}",
            entity.EntityId, tenantId, casfogData.Estado);

        return Ok(await BuildEntityResponseAsync(entity, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Re-fetches NOSIS (WS.01 variables) data for an existing entity and persists the updated
    /// snapshot. Idempotent — safe to retry after a failed enrichment.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with updated <see cref="EntityResponse"/>.</returns>
    [HttpPost("{id:guid}/refresh-nosis")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshNosisAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.RefreshNosisAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        _logger.LogInformation(
            "NOSIS refresh requested for entity {EntityId} tenant {TenantId}",
            id, tenantId);

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            _logger.LogWarning(
                "NOSIS refresh: entity {EntityId} not found for tenant {TenantId}.",
                id, tenantId);
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        // Entitlement (tenant profile, §7.0b) before availability (the installation's NOSIS
        // licence below). Checked AFTER the 404 so a bad id is still reported as a bad id.
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!profile.NosisEnabled)
        {
            _logger.LogInformation(
                "NOSIS refresh: requested for entity {EntityId} tenant {TenantId} while the tenant profile disables NOSIS.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "NOSIS no está habilitado para este tenant.");
        }

        // NosisService collapses "switched off", "no credentials" and "NOSIS is down" into the same
        // LookupFailed, which is right for the fault-isolated fan-out but wrong for an endpoint an
        // operator triggers by hand: a 502 invites a retry that can never succeed. Separate the
        // deliberate configuration state here, the way refresh-lufe reports a missing Lufe key.
        if (!_integrationAccess.IsNosisConfigured())
        {
            _logger.LogWarning(
                "NOSIS refresh: requested for entity {EntityId} tenant {TenantId} while NOSIS is disabled or unconfigured.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "NOSIS no está habilitado o no está configurado.");
        }

        // A malformed stored CUIT must not become a raw 500 — guard the parse.
        if (!long.TryParse(entity.CUIT?.Replace("-", ""), out var nosisCuit))
        {
            _logger.LogWarning(
                "NOSIS refresh: stored CUIT for entity {EntityId} tenant {TenantId} is not a valid number; cannot query NOSIS.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "El CUIT almacenado no es válido; no se puede consultar NOSIS.");
        }

        var nosisData = await _nosisService
            .GetVariablesAsync(tenantId, nosisCuit, NosisVariableGroup.Principal, cancellationToken)
            .ConfigureAwait(false);

        // Return before any write: a transient failure must never overwrite a good stored snapshot
        // with an empty one.
        if (nosisData.Estado == NosisConsultaEstado.LookupFailed)
        {
            _logger.LogWarning(
                "NOSIS refresh: lookup failed for entity {EntityId} tenant {TenantId}; returning 502.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "El servicio de NOSIS no está disponible o no devolvió datos.");
        }

        try
        {
            entity.NosisDataJson = System.Text.Json.JsonSerializer.Serialize(nosisData);
            entity.NosisDataUpdatedAt = DateTime.UtcNow;
            await _entityRepository.UpdateAsync(entity, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "NOSIS refresh: failed to persist data for entity {EntityId} tenant {TenantId}.",
                entity.EntityId, tenantId);
            return Problem(
                statusCode: 502,
                title: "Bad Gateway",
                detail: "Failed to persist NOSIS data.");
        }

        _logger.LogInformation(
            "NOSIS data refreshed for entity {EntityId} tenant {TenantId}: estado={Estado}",
            entity.EntityId, tenantId, nosisData.Estado);

        return Ok(await BuildEntityResponseAsync(entity, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Re-fetches the ARCA fiscal snapshot for an existing entity and persists it.
    /// Idempotent — safe to retry. Unlike POST /api/entities/lookup-arca, which runs
    /// before an entity exists, this refreshes the entity's own CUIT in place.
    /// FiscalDataJson and the derived FiscalRegime are updated; BusinessName, Sector and
    /// MipymeCategory are left untouched so manual edits survive a refresh, and the person type is
    /// never changed. A persona humana's legajo gets the same regime, monotributo category and
    /// snapshot, in the same save.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with updated <see cref="EntityResponse"/>.</returns>
    [HttpPost("{id:guid}/refresh-arca")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(EntityResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshArcaAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in EntitiesController.RefreshArcaAsync");
            return Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        var tenantId = tenantContext.TenantId;

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró la entidad.");
        }

        // ARCA expects the CUIT without hyphens.
        var cuitNormalized = entity.CUIT.Replace("-", "");

        // GetTenantCuit throws when the tenant has no configured CUIT.
        string cuitRepresentada;
        try
        {
            cuitRepresentada = GetTenantCuit(tenantId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ARCA refresh blocked for tenant {TenantId} — no CUIT configured", tenantId);
            return Problem(
                statusCode: 400,
                title: "Bad Request",
                detail: "No hay un CUIT de ARCA configurado para este tenant.");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var arcaData = await _arcaConstanciaService
                .GetPersonaByCuitAsync(cuitNormalized, cuitRepresentada, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();

            if (arcaData is null)
            {
                _ = LogArcaCallAsync(tenantId, "getPersona_v2", false, (int)sw.ElapsedMilliseconds, null);

                return Problem(
                    statusCode: 404,
                    title: "Not Found",
                    detail: "No se encontró ninguna entidad en ARCA con el CUIT ingresado.");
            }

            // ARCA answered, so the call itself succeeded — but a persona without a fiscal
            // block gives us nothing to persist. Leave the existing snapshot intact rather
            // than overwriting a good one with null.
            _ = LogArcaCallAsync(tenantId, "getPersona_v2", true, (int)sw.ElapsedMilliseconds, null);

            if (arcaData.FiscalData is null)
            {
                if (string.IsNullOrWhiteSpace(arcaData.EstadoClave))
                {
                    _logger.LogWarning(
                        "ARCA returned no fiscal data for entity {EntityId} tenant {TenantId} — snapshot left unchanged",
                        entity.EntityId, tenantId);

                    return Problem(
                        statusCode: 502,
                        title: "Bad Gateway",
                        detail: "ARCA no devolvió datos fiscales para este CUIT.");
                }

                return await RefreshKeyStatusOnlyAsync(entity, arcaData.EstadoClave, cancellationToken)
                    .ConfigureAwait(false);
            }

            // The person type is set at create time (and by a CUIT change on PUT), never here. ARCA
            // disagreeing is logged for a human to look at; no CUIT or name is logged.
            var arcaPersonType = CuitHelper.PersonTypeFromArca(arcaData.TipoPersona);
            if (arcaPersonType is not null && arcaPersonType != entity.PersonType)
            {
                _logger.LogWarning(
                    "ARCA reports person type {ArcaPersonType} for entity {EntityId} tenant {TenantId}, stored as {PersonType}; refresh-arca never changes the person type.",
                    arcaPersonType, entity.EntityId, tenantId, entity.PersonType);
            }

            // The snapshot, its stamp and the fiscal regime (DDL 069) on the entity and, for a persona
            // humana, the same snapshot plus the regime, monotributo category and activity start on its
            // legajo — the write the legajo-scoped refresh-arca shares (PersonSnapshotWriter).
            var legajo = await LoadLinkedLegajoAsync(entity, cancellationToken).ConfigureAwait(false);
            PersonSnapshotWriter.ApplyArca(entity, legajo, arcaData, DateTime.UtcNow);

            await SaveEntityAndLinkedLegajoAsync(entity, legajo, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "ARCA data refreshed for entity {EntityId} tenant {TenantId}",
                entity.EntityId, tenantId);

            return Ok(await BuildEntityResponseAsync(entity, legajo, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _ = LogArcaCallAsync(tenantId, "getPersona_v2", false, (int)sw.ElapsedMilliseconds, ex.Message);

            _logger.LogError(ex, "ARCA API request failed for tenant {TenantId}", tenantId);

            return Problem(
                statusCode: 502,
                title: "External service unavailable",
                detail: "El servicio de ARCA no está disponible temporalmente.");
        }
    }

    /// <summary>
    /// Downloads a Lufe document for the given entity by index into the persisted
    /// <c>Documentos</c> array. The BFF acts as a SSRF-safe proxy — only the official
    /// Lufe host is allowed.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="docIndex">Zero-based index into the entity's <c>Documentos</c> array.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>File content on success, or an appropriate error response.</returns>
    [HttpGet("{id:guid}/lufe-document")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> GetLufeDocumentAsync(
        Guid id,
        [FromQuery] int docIndex,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
            return Problem(statusCode: 500, detail: "Tenant context unavailable.");

        var tenantId = tenantContext.TenantId;

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null) return NotFound();

        if (string.IsNullOrEmpty(entity.LufeDataJson))
            return NotFound(new { message = "No hay datos Lufe para esta entidad." });

        Models.Lufe.LufeData? lufeData;
        try
        {
            lufeData = System.Text.Json.JsonSerializer.Deserialize<Models.Lufe.LufeData>(
                entity.LufeDataJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return Problem(statusCode: 500, detail: "Error deserializando datos Lufe.");
        }

        var documentos = lufeData?.Documentos;
        if (documentos is null or { Length: 0 })
            return NotFound(new { message = "La entidad no tiene documentos Lufe registrados." });

        if (docIndex < 0 || docIndex >= documentos.Length)
            return BadRequest(new { message = $"docIndex fuera de rango (0-{documentos.Length - 1})." });

        var doc = documentos[docIndex];
        if (string.IsNullOrEmpty(doc.Url))
            return Problem(statusCode: 500, detail: "URL del documento no disponible.");

        // SSRF guard — only allow the official Lufe host
        if (!Uri.TryCreate(doc.Url, UriKind.Absolute, out var docUri) ||
            !string.Equals(docUri.Host, "legajounicoapi.produccion.gob.ar",
                           StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Lufe document URL has unexpected host {Host} for entity {EntityId}, tenant {TenantId}",
                docUri?.Host, id, tenantId);
            return BadRequest(new { message = "URL del documento inválida." });
        }

        // Entitlement (tenant profile, §7.0b) before availability (the per-tenant key): the
        // download is a LUFE call with the LUFE key, whatever is already stored locally.
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!profile.LufeEnabled)
        {
            _logger.LogInformation(
                "LUFE document download: requested for entity {EntityId} tenant {TenantId} while the tenant profile disables LUFE.",
                id, tenantId);
            return Problem(statusCode: 400, detail: "LUFE no está habilitado para este tenant.");
        }

        var apiKey = _integrationAccess.GetLufeApiKey(tenantId);
        if (string.IsNullOrEmpty(apiKey))
            return Problem(statusCode: 400, detail: "Lufe no está configurado para este tenant.");

        var result = await _lufeService
            .DownloadDocumentAsync(doc.Url, apiKey, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 502, detail: "Error descargando el documento desde Lufe.");

        if (!result.Found)
            return NotFound(new { message = "El documento ya no está disponible en Lufe." });

        if (!IsValidPdf(result.Content, result.ContentType))
            return Problem(
                statusCode: 415,
                detail: "El documento no es un PDF válido para análisis.");

        // Ensure filename ends with .pdf
        var rawName      = doc.Nombre ?? "documento";
        var fileNameBase = Path.GetFileNameWithoutExtension(rawName);
        var fileName     = string.IsNullOrWhiteSpace(fileNameBase)
                           ? "documento.pdf"
                           : fileNameBase + ".pdf";

        return File(result.Content!, "application/pdf", fileName);
    }

    // ── POST /api/entities/{id:guid}/lufe-document/import ────────────────────────

    /// <summary>
    /// Downloads a Lufe document by index and imports it as a first-class <see cref="Document"/>
    /// record so it can enter the existing analysis pipeline. Balance documents are now also
    /// imported automatically on a LUFE refresh; this endpoint stays for backward compatibility.
    /// </summary>
    /// <param name="id">The entity identifier.</param>
    /// <param name="request">Import request containing the zero-based document index.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 201 Created with <see cref="DocumentResponse"/> on success; 200 OK with the EXISTING
    /// document when it was already imported (same LUFE URL or same SHA-256).
    /// </returns>
    [HttpPost("{id:guid}/lufe-document/import")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(DocumentResponse), 201)]
    [ProducesResponseType(typeof(DocumentResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 415)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> ImportLufeDocumentAsync(
        Guid id,
        [FromBody] ImportLufeDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
            return Problem(statusCode: 500, detail: "Tenant context unavailable.");

        var tenantId = tenantContext.TenantId;

        var entity = await _entityRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        if (string.IsNullOrEmpty(entity.LufeDataJson))
            return Problem(statusCode: 404, title: "Not Found",
                detail: "No hay datos Lufe para esta entidad.");

        Models.Lufe.LufeData? lufeData;
        try
        {
            lufeData = System.Text.Json.JsonSerializer.Deserialize<Models.Lufe.LufeData>(
                entity.LufeDataJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return Problem(statusCode: 500, detail: "Error deserializando datos Lufe.");
        }

        var documentos = lufeData?.Documentos;
        if (documentos is null or { Length: 0 })
            return Problem(statusCode: 404, title: "Not Found",
                detail: "La entidad no tiene documentos Lufe registrados.");

        if (request.DocIndex < 0 || request.DocIndex >= documentos.Length)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"docIndex fuera de rango (0-{documentos.Length - 1}).");

        var doc = documentos[request.DocIndex];
        if (string.IsNullOrEmpty(doc.Url))
            return Problem(statusCode: 500, detail: "URL del documento no disponible.");

        // SSRF guard — only allow the official Lufe host. Checked here, before the entitlement
        // gate, so a bad URL keeps answering as a bad URL; the shared import re-checks it.
        if (!IsAllowedLufeDocumentUrl(doc.Url, out var docHost))
        {
            _logger.LogWarning(
                "Lufe document URL has unexpected host {Host} for entity {EntityId}, tenant {TenantId}",
                docHost, id, tenantId);
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "URL del documento inválida.");
        }

        // Entitlement (tenant profile, §7.0b) before availability (the per-tenant key): the
        // import downloads from LUFE with the LUFE key, whatever is already stored locally.
        var profile = await _integrationAccess.ResolveEntitlementsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!profile.LufeEnabled)
        {
            _logger.LogInformation(
                "LUFE document import: requested for entity {EntityId} tenant {TenantId} while the tenant profile disables LUFE.",
                id, tenantId);
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "LUFE no está habilitado para este tenant.");
        }

        var apiKey = _integrationAccess.GetLufeApiKey(tenantId);
        if (string.IsNullOrEmpty(apiKey))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Lufe no está configurado para este tenant.");

        // Resolve the current user BEFORE downloading and storing, so an unknown caller never
        // leaves an orphaned file in storage.
        var firebaseUid = GetCallerFirebaseUid();
        if (string.IsNullOrWhiteSpace(firebaseUid)) return Unauthorized();

        var currentUser = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var outcome = await ImportLufeDocumentCoreAsync(
                entity, doc, tenantId, apiKey, currentUser.UserId,
                ManualLufeImportAuditAction, downloadTimeout: null, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Status switch
        {
            LufeImportStatus.Imported => CreatedAtAction(
                "GetById",
                "Documents",
                new { id = outcome.Document!.DocumentId },
                MapToDocumentResponse(outcome.Document)),

            // Already imported (same LUFE URL, or byte-identical content): hand back the existing
            // row instead of creating a second one. 200, not 201 — nothing was created.
            LufeImportStatus.Duplicate => Ok(MapToDocumentResponse(outcome.Document!)),

            LufeImportStatus.InvalidUrl => Problem(statusCode: 400, title: "Bad Request",
                detail: "URL del documento inválida."),
            LufeImportStatus.NotFound => Problem(statusCode: 404, title: "Not Found",
                detail: "El documento ya no está disponible en Lufe."),
            LufeImportStatus.NotPdf => Problem(statusCode: 415,
                detail: "El documento no es un PDF válido para análisis."),
            LufeImportStatus.TooLarge => Problem(statusCode: 400, title: "Bad Request",
                detail: $"El documento supera el tamaño máximo permitido ({PdfSizeLimits.MaxPdfLabel})."),
            _ => Problem(statusCode: 502, detail: "Error descargando el documento desde Lufe.")
        };
    }

    // ── LUFE document import: shared core + automatic balance import ─────────────────────────────

    /// <summary>Outcome of <see cref="ImportLufeDocumentCoreAsync"/>.</summary>
    private enum LufeImportStatus
    {
        Imported,
        Duplicate,
        InvalidUrl,
        DownloadFailed,
        NotFound,
        NotPdf,
        TooLarge
    }

    /// <summary>
    /// <see cref="Document"/> is the new row for <see cref="LufeImportStatus.Imported"/> and the
    /// EXISTING row for <see cref="LufeImportStatus.Duplicate"/>; null otherwise.
    /// </summary>
    private sealed record LufeImportOutcome(LufeImportStatus Status, Document? Document);

    /// <summary>
    /// Downloads one LUFE document and stores it as a first-class <see cref="Document"/>
    /// (<c>Source = "Lufe"</c>, <c>SourceRef</c> = the LUFE URL, <c>ProcessingStatus = "Pending"</c>).
    /// Shared by the manual import endpoint and the automatic balance import on a LUFE refresh.
    /// Entitlement, API key and caller resolution are the caller's job.
    /// <para>
    /// Dedup, against the entity's NON-deleted documents: a row with <c>Source = "Lufe"</c> and
    /// the same <c>SourceRef</c> short-circuits BEFORE any download; after the download, a row
    /// with the same <c>FileHashSHA256</c> short-circuits BEFORE anything is stored (LUFE
    /// sometimes re-issues the URL for the same file). Either way the existing row is returned
    /// as <see cref="LufeImportStatus.Duplicate"/>.
    /// </para>
    /// </summary>
    /// <param name="downloadTimeout">
    /// Bounds the LUFE download only (the LUFE HttpClient has no timeout of its own). A timeout
    /// is reported as <see cref="LufeImportStatus.DownloadFailed"/>; caller cancellation still throws.
    /// </param>
    private async Task<LufeImportOutcome> ImportLufeDocumentCoreAsync(
        Entity            entity,
        LufeDocumento     doc,
        string            tenantId,
        string            apiKey,
        Guid              userId,
        string            auditAction,
        TimeSpan?         downloadTimeout,
        CancellationToken ct)
    {
        if (!IsAllowedLufeDocumentUrl(doc.Url, out var host))
        {
            _logger.LogWarning(
                "Lufe document URL has unexpected host {Host} for entity {EntityId}, tenant {TenantId}",
                host, entity.EntityId, tenantId);
            return new LufeImportOutcome(LufeImportStatus.InvalidUrl, null);
        }

        var url = doc.Url!;

        var existing = (await _documentRepository
                .GetByEntityIdAsync(entity.EntityId, tenantId, ct)
                .ConfigureAwait(false))
            .Where(d => !d.IsDeleted)
            .ToList();

        var sameSource = existing.FirstOrDefault(d =>
            string.Equals(d.Source, "Lufe", StringComparison.Ordinal) &&
            string.Equals(d.SourceRef, url, StringComparison.Ordinal));

        if (sameSource is not null)
        {
            _logger.LogInformation(
                "Lufe document already imported as {DocumentId} (same LUFE URL) for entity {EntityId} tenant {TenantId}; skipping",
                sameSource.DocumentId, entity.EntityId, tenantId);
            return new LufeImportOutcome(LufeImportStatus.Duplicate, sameSource);
        }

        LufeDocumentDownload? result;
        using (var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            if (downloadTimeout is { } timeout)
                downloadCts.CancelAfter(timeout);

            try
            {
                result = await _lufeService
                    .DownloadDocumentAsync(url, apiKey, downloadCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Lufe document download timed out after {TimeoutSeconds}s for entity {EntityId} tenant {TenantId}",
                    downloadTimeout?.TotalSeconds, entity.EntityId, tenantId);
                return new LufeImportOutcome(LufeImportStatus.DownloadFailed, null);
            }
        }

        if (result is null)
            return new LufeImportOutcome(LufeImportStatus.DownloadFailed, null);

        if (!result.Found)
            return new LufeImportOutcome(LufeImportStatus.NotFound, null);

        if (!IsValidPdf(result.Content, result.ContentType))
            return new LufeImportOutcome(LufeImportStatus.NotPdf, null);

        if (result.Content!.Length > PdfSizeLimits.MaxPdfBytes)
            return new LufeImportOutcome(LufeImportStatus.TooLarge, null);

        // SHA-256 hash
        using var hashStream = new MemoryStream(result.Content!);
        var hashBytes = await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false);
        var hashHex   = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // A blank stored hash is never a match — it must not stand in for "same file".
        var sameContent = existing.FirstOrDefault(d =>
            !string.IsNullOrWhiteSpace(d.FileHashSHA256) &&
            string.Equals(d.FileHashSHA256, hashHex, StringComparison.OrdinalIgnoreCase));

        if (sameContent is not null)
        {
            _logger.LogInformation(
                "Lufe document content already stored as {DocumentId} (same SHA-256) for entity {EntityId} tenant {TenantId}; skipping",
                sameContent.DocumentId, entity.EntityId, tenantId);
            return new LufeImportOutcome(LufeImportStatus.Duplicate, sameContent);
        }

        // Persist to storage
        var documentId  = Guid.NewGuid();
        using var storeStream = new MemoryStream(result.Content!);
        var storagePath = await _storageService
            .SaveAsync(tenantId, documentId, storeStream, ct)
            .ConfigureAwait(false);

        // Build document entity
        var rawName  = doc.Nombre ?? "documento";
        var baseName = Path.GetFileNameWithoutExtension(rawName);
        var origName = string.IsNullOrWhiteSpace(baseName) ? "documento.pdf" : baseName + ".pdf";
        var now      = DateTime.UtcNow;
        var document = new Document
        {
            DocumentId         = documentId,
            TenantId           = tenantId,
            EntityId           = entity.EntityId,
            UploadedBy         = userId,
            OriginalFileName   = origName,
            StoredFileName     = $"{documentId}.pdf",
            StoragePath        = storagePath,
            FileSizeBytes      = result.Content!.LongLength,
            MimeType           = "application/pdf",
            FileHashSHA256     = hashHex,
            DocumentType       = MapLufeDocumentType(doc.Nombre),
            FiscalYear         = DeriveFiscalYear(doc),
            ProcessingStatus   = "Pending",
            UploadedAt         = now,
            RetentionExpiresAt = now.AddYears(7),
            IsDeleted          = false,
            Source             = "Lufe",
            SourceRef          = BuildLufeSourceRef(doc)
        };

        await _documentRepository.CreateAsync(document, ct)
            .ConfigureAwait(false);

        // Audit (fire-and-forget)
        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     userId,
            action:     auditAction,
            severity:   "Info",
            entityType: "Document",
            entityId:   document.DocumentId.ToString(),
            ipAddress:  HttpContext?.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext?.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Lufe document imported ({AuditAction}): {DocumentId} ({FileName}, {Bytes} bytes) for entity {EntityId} tenant {TenantId}",
            auditAction, document.DocumentId, document.OriginalFileName, document.FileSizeBytes,
            entity.EntityId, tenantId);

        return new LufeImportOutcome(LufeImportStatus.Imported, document);
    }

    /// <summary>
    /// Imports the entity's LUFE balance documents right after a LUFE snapshot is persisted, so
    /// the analyst no longer has to press "Usar para análisis". Returns how many NEW documents
    /// were imported (duplicates do not count).
    /// <para>
    /// Fault-isolated end to end: never throws, and nothing it does changes the calling
    /// endpoint's response or status. Each document runs in its own try/catch and a failure is a
    /// Warning naming the entity and the document. Bounded: at most
    /// <see cref="MaxAutoImportBalances"/> documents, newest period first, each download capped
    /// at <see cref="LufeAutoImportDownloadTimeout"/>.
    /// </para>
    /// <para>
    /// Runs only for <see cref="Roles.Operators"/>. The calling endpoints are already gated on that
    /// policy; the check here keeps a read-only role from ever importing should one of them be
    /// widened later.
    /// </para>
    /// </summary>
    /// <param name="knownUserId">The caller's UserId when the endpoint already resolved it; null to resolve it here.</param>
    private async Task<int> AutoImportLufeBalancesAsync(
        Entity            entity,
        LufeData          lufeData,
        string            tenantId,
        string            apiKey,
        Guid?             knownUserId,
        CancellationToken ct)
    {
        var principal = HttpContext?.User;
        if (principal is null || !Roles.Operators.Split(',').Any(principal.IsInRole))
            return 0;

        var balances = (lufeData.Documentos ?? [])
            .Where(d => MapLufeDocumentType(d.Nombre) == DocumentTypes.Balance && !string.IsNullOrWhiteSpace(d.Url))
            .OrderByDescending(d => DeriveFiscalYear(d) ?? int.MinValue)
            .Take(MaxAutoImportBalances)
            .ToList();

        if (balances.Count == 0)
            return 0;

        Guid userId;
        try
        {
            if (knownUserId is { } known)
            {
                userId = known;
            }
            else
            {
                var firebaseUid = GetCallerFirebaseUid();
                var user = string.IsNullOrWhiteSpace(firebaseUid)
                    ? null
                    : await _userRepository.GetByFirebaseUidAsync(firebaseUid, ct).ConfigureAwait(false);

                if (user is null)
                {
                    _logger.LogWarning(
                        "LUFE balance auto-import skipped for entity {EntityId} tenant {TenantId}: caller has no user row",
                        entity.EntityId, tenantId);
                    return 0;
                }

                userId = user.UserId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LUFE balance auto-import skipped for entity {EntityId} tenant {TenantId}: caller could not be resolved",
                entity.EntityId, tenantId);
            return 0;
        }

        var imported = 0;

        foreach (var doc in balances)
        {
            try
            {
                var outcome = await ImportLufeDocumentCoreAsync(
                        entity, doc, tenantId, apiKey, userId,
                        AutoLufeImportAuditAction, LufeAutoImportDownloadTimeout, ct)
                    .ConfigureAwait(false);

                switch (outcome.Status)
                {
                    case LufeImportStatus.Imported:
                        imported++;
                        break;

                    case LufeImportStatus.Duplicate:
                        break;   // Already stored — logged by the core.

                    default:
                        _logger.LogWarning(
                            "LUFE balance auto-import did not import document {DocumentName} for entity {EntityId} tenant {TenantId}: {Status}",
                            doc.Nombre, entity.EntityId, tenantId, outcome.Status);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LUFE balance auto-import failed for document {DocumentName} for entity {EntityId} tenant {TenantId}",
                    doc.Nombre, entity.EntityId, tenantId);

                if (ct.IsCancellationRequested)
                    break;
            }
        }

        return imported;
    }

    /// <summary>
    /// SSRF guard for LUFE document URLs: absolute, on the official LUFE host only.
    /// <paramref name="host"/> is the parsed host (for logging), or null when unparseable.
    /// </summary>
    private static bool IsAllowedLufeDocumentUrl(string? url, out string? host)
    {
        host = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        host = uri.Host;
        return string.Equals(uri.Host, LufeDocumentHost, StringComparison.OrdinalIgnoreCase);
    }

    private string? GetCallerFirebaseUid() =>
        User?.FindFirst("uid")?.Value
        ?? User?.FindFirst("sub")?.Value
        ?? User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    // ── Private helpers ──────────────────────────────────────────────────────────

    private static bool IsValidPdf(byte[]? content, string? contentType)
    {
        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return true;
        return content is { Length: >= 4 }
            && content[0] == 0x25 && content[1] == 0x50
            && content[2] == 0x44 && content[3] == 0x46;
    }

    private static string MapLufeDocumentType(string? nombre) =>
        nombre switch
        {
            _ when nombre?.Contains("balance",     StringComparison.OrdinalIgnoreCase) == true => DocumentTypes.Balance,
            _ when nombre?.Contains("certificado", StringComparison.OrdinalIgnoreCase) == true => DocumentTypes.Certificado,
            _ => DocumentTypes.Otro
        };

    /// <summary>
    /// Builds the durable source reference for a LUFE-imported document from the LUFE
    /// entry's URL — the only field LUFE exposes that distinguishes two entries sharing
    /// the same Nombre and Periodo. Stored verbatim so the Portal can match it exactly
    /// against the LUFE listing entry's own URL. Returns null (never throws) for a
    /// missing/blank URL so a degraded LUFE entry can never abort the import; the link
    /// is best-effort, importing is not.
    /// </summary>
    internal static string? BuildLufeSourceRef(LufeDocumento doc) =>
        string.IsNullOrWhiteSpace(doc.Url) ? null : doc.Url;

    /// <summary>
    /// Derives the fiscal year for an imported document from the document's OWN period
    /// (<see cref="LufeDocumento.Periodo"/>). The entity-level <c>indicadores.periodo</c> is
    /// deliberately NOT used: it is the period of the entity's most-recent statements and would
    /// stamp every imported document with the latest year, mislabelling any historical balance.
    /// Returns null when the period cannot be parsed to a plausible year — never a guessed
    /// value, since a wrong fiscal year is indistinguishable from a real one and determines
    /// which period scoring runs against.
    /// </summary>
    private static int? DeriveFiscalYear(LufeDocumento doc) =>
        LufePeriodo.ParseYear(doc.Periodo, DateTime.UtcNow.Year + 1);

    private static DocumentResponse MapToDocumentResponse(Document d) => new()
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
        SourceRef        = d.SourceRef
    };

    /// <summary>
    /// Maps a projected list row to the narrow LIST DTO. Static because — unlike
    /// <see cref="MapToResponse"/>, which needs <c>_logger</c> for its five parse-failure warnings —
    /// this touches NO *DataJson column, so there is nothing that can fail and nothing to log. That
    /// is the whole point: the list stops paying to deserialise five nvarchar(max) blobs per row
    /// that the table never shows.
    ///
    /// Takes <see cref="EntityListProjection"/>, not <see cref="Entity"/>: the full row is no longer
    /// loaded for a list request, so the snapshots are not merely unread here — they were never
    /// fetched from the database at all.
    ///
    /// CreatedByName / HasCreditLine / LatestAnalysisStatus are left unset here; they need
    /// cross-row lookups and are filled in by <see cref="EnrichProjectionAsync"/>.
    /// </summary>
    private static EntityListItemResponse MapToListItem(EntityListProjection e) => new()
    {
        EntityId           = e.EntityId,
        CUIT               = e.CUIT,
        BusinessName       = e.BusinessName,
        TradeName          = e.TradeName,
        Sector             = e.Sector,
        MipymeCategory     = e.MipymeCategory,
        IsMonitored        = e.IsMonitored,
        RiskScore          = e.RiskScore,
        RiskScoreUpdatedAt = e.RiskScoreUpdatedAt,
        CreatedAt          = e.CreatedAt,
        CreatedBy          = e.CreatedBy,
        PersonType         = e.PersonType,
        FiscalRegime       = e.FiscalRegime
    };

    /// <summary>
    /// Maps a FULL entity row to the FULL detail DTO, parsing all five source snapshots.
    /// <para>
    /// Still the mapper for GetByCuit, Create, Update and the five <c>refresh-*</c> actions, which
    /// all hold a complete <see cref="Entity"/> and whose payloads are unchanged. GET
    /// /api/entities/{id} no longer comes through here — it never loads three of these five columns
    /// now, so it maps from <see cref="EntityDetailProjection"/> via
    /// <see cref="MapToDetailResponse"/> instead.
    /// </para>
    /// <para>
    /// DO NOT overload this name. <c>EntitiesControllerCasfogTests</c> and
    /// <c>EntitiesControllerNosisTests</c> reach it by reflection with
    /// <c>GetMethod("MapToResponse", …)</c>, which throws <c>AmbiguousMatchException</c> the moment
    /// a second overload exists — which is why the projection mapper is called
    /// <see cref="MapToDetailResponse"/> and not a second <c>MapToResponse</c>.
    /// </para>
    /// </summary>
    private EntityResponse MapToResponse(Entity e)
    {
        var fiscal = ParseSnapshot<ArcaFiscalData>(e.FiscalDataJson, e.EntityId, "FiscalDataJson");
        var lufe   = ParseSnapshot<Models.Lufe.LufeData>(e.LufeDataJson, e.EntityId, "LufeDataJson");
        var bcra   = ParseSnapshot<BcraData>(e.BcraDataJson, e.EntityId, "BcraDataJson");
        var casfog = ParseSnapshot<CasfogData>(e.CasfogDataJson, e.EntityId, "CasfogDataJson");
        var nosis  = ParseSnapshot<NosisData>(e.NosisDataJson, e.EntityId, "NosisDataJson");

        return new EntityResponse
        {
            EntityId            = e.EntityId,
            TenantId            = e.TenantId,
            CUIT                = e.CUIT,
            BusinessName        = e.BusinessName,
            TradeName           = e.TradeName,
            Sector              = e.Sector,
            MipymeCategory      = e.MipymeCategory,
            SgrOneClickEntityId = e.SgrOneClickEntityId,
            RiskScore           = e.RiskScore,
            RiskScoreUpdatedAt  = e.RiskScoreUpdatedAt,
            IsMonitored         = e.IsMonitored,
            CreatedAt           = e.CreatedAt,
            UpdatedAt           = e.UpdatedAt,
            Fiscal              = fiscal,
            FiscalDataUpdatedAt = e.FiscalDataUpdatedAt,
            Lufe                = lufe,
            LufeDataUpdatedAt   = e.LufeDataUpdatedAt,
            Bcra                = bcra,
            BcraDataUpdatedAt   = e.BcraDataUpdatedAt,
            Casfog              = casfog,
            CasfogDataUpdatedAt = e.CasfogDataUpdatedAt,
            Nosis               = nosis,
            NosisDataUpdatedAt  = e.NosisDataUpdatedAt,
            CreatedBy           = e.CreatedBy,
            PersonType          = e.PersonType,
            FiscalRegime        = e.FiscalRegime,
            PersonLegajoId      = e.PersonLegajoId
            // MonotributoCategory lives on the legajo: filled by BuildEntityResponseAsync.
        };
    }

    /// <summary>
    /// Maps the projected detail row to the SAME <see cref="EntityResponse"/> clients already
    /// receive, parsing FISCAL and LUFE only.
    ///
    /// <para>
    /// <c>Bcra</c>, <c>Casfog</c> and <c>Nosis</c> are left null — not because parsing them failed,
    /// but because <see cref="EntityDetailProjection"/> does not carry those columns and the query
    /// behind it never selected them. The detail view renders FISCAL and LUFE on load; the other
    /// three appear only when the user opens their tab, and are fetched then from
    /// <c>GET {id}/bcra</c>, <c>{id}/casfog</c> and <c>{id}/nosis</c>.
    /// </para>
    ///
    /// <para>
    /// All three <c>*DataUpdatedAt</c> timestamps ARE carried, so the client can still render "last
    /// refreshed" without paying for the payloads — but a null timestamp is not proof the source is
    /// empty; see <see cref="EntityDetailProjection"/>. CreatedByName / HasCreditLine /
    /// LatestAnalysisStatus are filled in afterwards by <see cref="EnrichProjectionAsync"/>,
    /// exactly as before.
    /// </para>
    /// </summary>
    private EntityResponse MapToDetailResponse(EntityDetailProjection e)
    {
        var fiscal = ParseSnapshot<ArcaFiscalData>(e.FiscalDataJson, e.EntityId, "FiscalDataJson");
        var lufe   = ParseSnapshot<Models.Lufe.LufeData>(e.LufeDataJson, e.EntityId, "LufeDataJson");

        return new EntityResponse
        {
            EntityId            = e.EntityId,
            TenantId            = e.TenantId,
            CUIT                = e.CUIT,
            BusinessName        = e.BusinessName,
            TradeName           = e.TradeName,
            Sector              = e.Sector,
            MipymeCategory      = e.MipymeCategory,
            SgrOneClickEntityId = e.SgrOneClickEntityId,
            RiskScore           = e.RiskScore,
            RiskScoreUpdatedAt  = e.RiskScoreUpdatedAt,
            IsMonitored         = e.IsMonitored,
            CreatedAt           = e.CreatedAt,
            UpdatedAt           = e.UpdatedAt,
            Fiscal              = fiscal,
            FiscalDataUpdatedAt = e.FiscalDataUpdatedAt,
            Lufe                = lufe,
            LufeDataUpdatedAt   = e.LufeDataUpdatedAt,
            // Payload deliberately absent — never selected, never parsed. Timestamp still shipped.
            Bcra                = null,
            BcraDataUpdatedAt   = e.BcraDataUpdatedAt,
            Casfog              = null,
            CasfogDataUpdatedAt = e.CasfogDataUpdatedAt,
            Nosis               = null,
            NosisDataUpdatedAt  = e.NosisDataUpdatedAt,
            CreatedBy           = e.CreatedBy,
            PersonType          = e.PersonType,
            FiscalRegime        = e.FiscalRegime,
            PersonLegajoId      = e.PersonLegajoId,
            MonotributoCategory = e.MonotributoCategory
        };
    }

    /// <summary>
    /// <see cref="MapToResponse"/> plus the one field that lives on the persona humana's legajo,
    /// <see cref="EntityResponse.MonotributoCategory"/>. Every action that answers with a full
    /// <see cref="Entity"/> goes through here, so the payload is the same whichever action produced it.
    /// <para>
    /// A separate name, not a <c>MapToResponse</c> overload, for the reason given on that method. The
    /// category comes from <paramref name="legajo"/> when the action already holds it, otherwise from
    /// a one-column, tenant-scoped read — made only when the entity has a legajo at all, so a
    /// Juridica costs nothing extra.
    /// </para>
    /// </summary>
    private async Task<EntityResponse> BuildEntityResponseAsync(
        Entity entity,
        PersonLegajo? legajo,
        CancellationToken ct)
    {
        var response = MapToResponse(entity);

        if (entity.PersonLegajoId is not Guid legajoId) return response;

        response.MonotributoCategory = legajo is not null && legajo.PersonLegajoId == legajoId
            ? legajo.MonotributoCategory
            : await _personLegajoRepository
                .GetMonotributoCategoryAsync(legajoId, entity.TenantId, ct)
                .ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Shared options for every stored-snapshot parse in this controller.
    /// <para>
    /// One static instance, not one per column: <c>JsonSerializerOptions</c> is the cache key for
    /// System.Text.Json's per-type metadata, so allocating a fresh one at each call site — as the
    /// five inlined blocks in <see cref="MapToResponse"/> used to — throws that cache away every
    /// time. Immutable once used for (de)serialisation, therefore safe to share across requests.
    /// This is what every service in the project and <c>AnalysisController</c> already do.
    /// </para>
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions SnapshotJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Deserialises one stored source snapshot, or returns null if it is absent or unparseable.
    ///
    /// <para>
    /// The single implementation of a try/catch-and-warn block that used to be copy-pasted per
    /// column. A stored snapshot that will not parse must degrade to null and a warning, never to a
    /// 500: the blob is a cached copy of a third party's payload, and one bad row must not take the
    /// entity's whole detail view — or its BCRA tab — down with it.
    /// </para>
    ///
    /// <para>
    /// Instance rather than static only because it needs <c>_logger</c>. <paramref name="column"/>
    /// is the column name for the log line; the snapshot contents are never logged, which matters
    /// for NOSIS in particular since <c>NosisData</c> carries PII.
    /// </para>
    /// </summary>
    private T? ParseSnapshot<T>(string? json, Guid entityId, string column) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, SnapshotJsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not parse {SnapshotColumn} for entity {EntityId}. The snapshot will be null.",
                column, entityId);
            return null;
        }
    }

    /// <summary>
    /// Enriches mapped entity responses with the projected fields that need cross-row
    /// lookups: hasCreditLine (Active credit lines), latestAnalysisStatus, the latest stored
    /// score + level pair (latestScore / latestRiskLevel / latestScoreAnalysisId / latestScoredAt),
    /// and the author display name. Set-based — one query per source, no N+1.
    ///
    /// Takes <see cref="IEntityProjectionTarget"/> rather than a concrete DTO so the LIST
    /// (<see cref="EntityListItemResponse"/>) and the DETAIL (<see cref="EntityResponse"/>) share
    /// these three queries and this loop instead of duplicating them. Both call sites pass their
    /// own list unchanged — <c>IReadOnlyList&lt;out T&gt;</c> is covariant.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> EnrichProjectionAsync(
        IReadOnlyList<IEntityProjectionTarget> responses,
        string tenantId,
        CancellationToken ct)
    {
        if (responses.Count == 0) return new Dictionary<Guid, EntityLatestScoreProjection>();

        var entityIds = responses.Select(r => r.EntityId).ToList();

        var withNonTerminalLine = await _creditLineRepository
            .GetEntityIdsWithNonTerminalLineAsync(entityIds, tenantId, ct)
            .ConfigureAwait(false);

        var latestStatusByEntity = await _analysisRepository
            .GetLatestStatusByEntityIdsAsync(entityIds, tenantId, ct)
            .ConfigureAwait(false);

        // Score and level of the latest SCORED result, read as one pair from one row: the level is
        // the one the BFF stored, never recomputed here or by the Portal from the score.
        var latestScoreByEntity = await _analysisRepository
            .GetLatestScoreByEntityIdsAsync(entityIds, tenantId, ct)
            .ConfigureAwait(false);

        var creatorIds = responses
            .Where(r => r.CreatedBy.HasValue)
            .Select(r => r.CreatedBy!.Value)
            .Distinct();

        var creators = await _userRepository.GetByIdsAsync(creatorIds, ct).ConfigureAwait(false);
        var creatorNameById = creators.ToDictionary(u => u.UserId, u => u.DisplayName);

        foreach (var r in responses)
        {
            r.HasCreditLine = withNonTerminalLine.Contains(r.EntityId);
            r.LatestAnalysisStatus = latestStatusByEntity.TryGetValue(r.EntityId, out var status)
                ? status
                : null;

            var latestScore = latestScoreByEntity.TryGetValue(r.EntityId, out var scored) ? scored : null;
            r.LatestScore           = latestScore?.Score;
            r.LatestRiskLevel       = latestScore?.Level;
            r.LatestScoreAnalysisId = latestScore?.AnalysisId;
            r.LatestScoredAt        = latestScore?.ScoredAt;

            r.CreatedByName =r.CreatedBy.HasValue
                && creatorNameById.TryGetValue(r.CreatedBy.Value, out var name)
                ? name
                : null;
        }

        return latestScoreByEntity;
    }

    /// <summary>
    /// Sets <see cref="EntityListItemResponse.LatestScoreProvisional"/> on each LIST row from the very row
    /// that supplied its LatestScore, through <see cref="Servicios.ScoreReliability.ScoreReliabilityAdapter"/> —
    /// the adapter the analysis detail, the analysis list and the dashboard use, so the PyMEs list never
    /// disagrees with the detail. No query of its own. Logs once per response, counts only, when a breakdown
    /// could not be read (that row is then judged facts_unavailable, as the detail would).
    /// </summary>
    private void ApplyLatestScoreProvisional(
        IReadOnlyList<EntityListItemResponse> items,
        IReadOnlyDictionary<Guid, EntityLatestScoreProjection> latestScores,
        string tenantId)
    {
        var unreadable = 0;
        foreach (var item in items)
        {
            if (!latestScores.TryGetValue(item.EntityId, out var scored))
            {
                item.LatestScoreProvisional = null;
                continue;
            }

            var (provisional, breakdownUnreadable) = LatestScoreProvisional(scored);
            item.LatestScoreProvisional = provisional;
            if (breakdownUnreadable)
                unreadable++;
        }

        if (unreadable > 0)
        {
            _logger.LogWarning(
                "{Count} score breakdown(s) on the entity list page could not be read for tenant {TenantId}; " +
                "their scores are judged facts_unavailable",
                unreadable, tenantId);
        }
    }

    /// <summary>
    /// The list's <c>latestScoreProvisional</c> for one scored row: true for a <c>provisional</c> verdict,
    /// false for <c>complete</c> / <c>with_caveats</c>, null for no verdict or <c>none</c>. Pure; internal for tests.
    /// </summary>
    internal static (bool? Provisional, bool BreakdownUnreadable) LatestScoreProvisional(EntityLatestScoreProjection scored)
    {
        ArgumentNullException.ThrowIfNull(scored);

        var verdict = Servicios.ScoreReliability.ScoreReliabilityAdapter.Evaluate(
            new Servicios.ScoreReliability.ScoreReliabilityRow(
                AiSuggestedScore: scored.Score,
                EnrichmentStatus: scored.EnrichmentStatus,
                AnalysisKind:     scored.AnalysisKind,
                FiscalYear:       scored.FiscalYear,
                GeneratedAt:      scored.GeneratedAt,
                RescoredAt:       scored.RescoredAt),
            scored.ScoreBreakdownJson);

        bool? provisional = verdict.Reliability?.Level switch
        {
            ScoreReliabilityLevels.Provisional                               => true,
            ScoreReliabilityLevels.Complete or ScoreReliabilityLevels.WithCaveats => false,
            _                                                                => null,
        };

        return (provisional, verdict.BreakdownUnreadable);
    }

    /// <summary>
    /// Projects each row's MAIN credit line — amount, disponible and currency — onto the LIST rows,
    /// so the Portal's PyMEs table renders "cupo + disponible + a utilisation bar" without a
    /// per-row credit-line call.
    ///
    /// <para>
    /// Separate from <see cref="EnrichProjectionAsync"/> and typed to the concrete
    /// <see cref="EntityListItemResponse"/> ON PURPOSE: that method is shared with the DETAIL
    /// endpoint through <see cref="IEntityProjectionTarget"/>, and these three fields belong to the
    /// list row only. Putting them on the interface would silently widen
    /// <c>GET /api/entities/{id}</c> too.
    /// </para>
    ///
    /// <para>
    /// One set-based query for the page (see
    /// <see cref="ICreditLineRepository.GetMainActiveByEntityIdsAsync"/>), never one per row. The
    /// three fields are left null together for an entity with no ACTIVE cupo — including one whose
    /// only cupo is Suspended, which still reports <c>hasCreditLine: true</c>.
    /// </para>
    /// </summary>
    private async Task EnrichMainCreditLineAsync(
        IReadOnlyList<EntityListItemResponse> items,
        string tenantId,
        CancellationToken ct)
    {
        if (items.Count == 0) return;

        var mainByEntity = await _creditLineRepository
            .GetMainActiveByEntityIdsAsync(items.Select(i => i.EntityId), tenantId, ct)
            .ConfigureAwait(false);

        foreach (var item in items)
        {
            if (!mainByEntity.TryGetValue(item.EntityId, out var main))
                continue;   // no Active cupo — all three stay null, together

            item.MainCreditLineAmount   = main.Amount;
            // Mirrors CreditLineResponse.Disponible exactly, negative result included: an
            // over-committed cupo must read as underwater, not be clamped to a reassuring zero.
            item.MainCreditLineDisponible = main.Amount - main.Utilizado;
            item.MainCreditLineCurrency = main.Currency;
        }
    }

    private static NoteResponse MapToNoteResponse(Note n) => new()
    {
        NoteId       = n.NoteId,
        EntityId     = n.EntityId,
        // Tombstone: a soft-deleted note never ships its original text to the client.
        Content      = n.IsDeleted ? string.Empty : n.Content,
        AuthorUserId = n.AuthorUserId,
        AuthorName   = n.AuthorName,
        CreatedAt    = n.CreatedAt,
        IsDeleted    = n.IsDeleted,
        DeletedAt    = n.DeletedAt
    };

    /// <summary>
    /// ProblemDetails for the refresh-lufe and refresh-bcra outcomes. Goes through <see cref="ControllerBase.Problem"/>
    /// so title/detail/traceId come from the factory like every other Problem() here, then adds
    /// the machine-readable errorCode extension the Portal reads as response.data.errorCode.
    /// </summary>
    private ObjectResult CodedProblem(int statusCode, string title, string detail, string errorCode)
    {
        var result = Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            type: $"https://serviciosfinancieros.api/errors/{errorCode}");

        if (result.Value is ProblemDetails problemDetails)
        {
            problemDetails.Extensions["errorCode"] = errorCode;
        }

        return result;
    }

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

    // ── Persona humana helpers (DDL 069) ─────────────────────────────────────────

    /// <summary>
    /// The 409 for a CUIT another entity of this tenant already has: the answer of the pre-check
    /// reads AND of a write that lost the race against UQ_Entities_TenantId_CUIT, worded identically.
    /// </summary>
    private ObjectResult CuitConflict(string cuit) => Problem(
        statusCode: 409,
        title: "Conflict",
        detail: $"Ya existe una entidad con el CUIT '{cuit}' para este tenant.");

    /// <summary>
    /// The fiscal regime a create stores from the client's ARCA snapshot: the regime when it is one
    /// of <see cref="FiscalRegimes.Known"/>, Desconocido for anything else. With no regime at all (no
    /// fiscal data, or an older client that does not send it) a Juridica stays null — "not
    /// determined" — while a Humana gets Desconocido, since its legajo column is NOT NULL.
    /// </summary>
    private static string? ResolveRequestedFiscalRegime(string? sent, bool isHumana)
    {
        if (string.IsNullOrWhiteSpace(sent)) return isHumana ? FiscalRegimes.Desconocido : null;

        return FiscalRegimes.IsKnown(sent) ? sent : FiscalRegimes.Desconocido;
    }

    /// <summary>
    /// Creates a persona humana: links <paramref name="entity"/> to the tenant's legajo for its CUIT
    /// — creating it when there is none — and writes both in ONE SaveChanges.
    /// <para>
    /// An existing legajo is linked, never duplicated, and only INFORMATIVE values are merged into it
    /// (see <see cref="MergeIntoLegajo"/>). If another request creates the same person's legajo
    /// between the read and the write, the write loses on UX_PersonLegajos_TenantId_CUIT; the winner
    /// is then re-read and linked, once. Any other lost race surfaces as
    /// <see cref="DuplicateCuitException"/> for the caller's 409.
    /// </para>
    /// </summary>
    private async Task<PersonLegajo> CreateWithLegajoAsync(
        Entity entity,
        string? monotributoCategory,
        string? fiscalJson,
        DateTime? activityStart,
        CancellationToken ct)
    {
        var now             = entity.CreatedAt;
        var requestedRegime = entity.FiscalRegime;

        var existing = await _personLegajoRepository
            .GetByCuitAsync(entity.CUIT, entity.TenantId, ct)
            .ConfigureAwait(false);

        var legajo = existing is null
            ? NewLegajo(entity, requestedRegime ?? FiscalRegimes.Desconocido, monotributoCategory, fiscalJson, activityStart, entity.CreatedBy, now)
            : MergeIntoLegajo(existing, requestedRegime, monotributoCategory, fiscalJson, activityStart, now);

        LinkToLegajo(entity, legajo);

        try
        {
            await _personLegajoRepository
                .SaveEntityWithLegajoAsync(entity, entityIsNew: true, legajo, legajoIsNew: existing is null, ct)
                .ConfigureAwait(false);

            return legajo;
        }
        catch (DuplicateCuitException ex) when (ex.Scope == DuplicateCuitScope.PersonLegajo && existing is null)
        {
            // Without deletes the winner is always there; if it is not, answer the same conflict.
            var winner = await _personLegajoRepository
                    .GetByCuitAsync(entity.CUIT, entity.TenantId, ct)
                    .ConfigureAwait(false)
                ?? throw new DuplicateCuitException(DuplicateCuitScope.Entity);

            MergeIntoLegajo(winner, requestedRegime, monotributoCategory, fiscalJson, activityStart, now);
            LinkToLegajo(entity, winner);

            await _personLegajoRepository
                .SaveEntityWithLegajoAsync(entity, entityIsNew: true, winner, legajoIsNew: false, ct)
                .ConfigureAwait(false);

            return winner;
        }
    }

    /// <summary>
    /// A new legajo for the person <paramref name="entity"/> names. Same hyphenated CUIT as the
    /// entity — the (TenantId, CUIT) key the DDL 069 backfill uses too — and an application-supplied
    /// key, so the entity can point at it before the single SaveChanges.
    /// </summary>
    private static PersonLegajo NewLegajo(
        Entity entity,
        string fiscalRegime,
        string? monotributoCategory,
        string? fiscalJson,
        DateTime? activityStart,
        Guid? createdBy,
        DateTime now)
    {
        var legajo = new PersonLegajo
        {
            PersonLegajoId      = Guid.NewGuid(),
            TenantId            = entity.TenantId,
            CUIT                = entity.CUIT,
            FullName            = entity.BusinessName,
            FiscalRegime        = fiscalRegime,
            MonotributoCategory = monotributoCategory,
            ArcaSnapshotJson    = fiscalJson,
            ArcaDataUpdatedAt   = fiscalJson is null ? null : now,
            ActivityStartDate   = activityStart,
            CreatedBy           = createdBy,
            CreatedAt           = now,
            UpdatedAt           = now
        };

        AlignCategoryWithRegime(legajo);
        return legajo;
    }

    /// <summary>
    /// Folds a create's data into a legajo the tenant already has — INFORMATIVE values only. A
    /// Desconocido (or missing) regime never replaces a known one, so creating a second entity for
    /// the same person without fresh ARCA data cannot downgrade the legajo; a category, a snapshot and
    /// ARCA's activity start are taken only when present. FullName is the person's, set when the legajo
    /// was created, and is left alone.
    /// </summary>
    private static PersonLegajo MergeIntoLegajo(
        PersonLegajo legajo,
        string? fiscalRegime,
        string? monotributoCategory,
        string? fiscalJson,
        DateTime? activityStart,
        DateTime now)
    {
        if (FiscalRegimes.IsDeterminate(fiscalRegime))
        {
            legajo.FiscalRegime = fiscalRegime!;
        }

        if (monotributoCategory is not null)
        {
            legajo.MonotributoCategory = monotributoCategory;
        }

        if (fiscalJson is not null)
        {
            legajo.ArcaSnapshotJson  = fiscalJson;
            legajo.ArcaDataUpdatedAt = now;
        }

        // ARCA's activity start, from the same snapshot; ARCA reporting none never clears a stored one.
        if (activityStart is not null)
        {
            legajo.ActivityStartDate = activityStart;
        }

        AlignCategoryWithRegime(legajo);
        legajo.UpdatedAt = now;
        return legajo;
    }

    /// <summary>A category belongs to a monotributista only: a known other regime clears it.</summary>
    private static void AlignCategoryWithRegime(PersonLegajo legajo)
    {
        if (FiscalRegimes.IsDeterminate(legajo.FiscalRegime)
            && legajo.FiscalRegime != FiscalRegimes.Monotributo)
        {
            legajo.MonotributoCategory = null;
        }
    }

    /// <summary>
    /// Points the entity at its legajo and makes the entity's regime the legajo's, so the two rows
    /// that describe one person cannot disagree.
    /// </summary>
    private static void LinkToLegajo(Entity entity, PersonLegajo legajo)
    {
        entity.PersonLegajoId = legajo.PersonLegajoId;
        entity.FiscalRegime   = legajo.FiscalRegime;
    }

    /// <summary>One vendor snapshot a CUIT correction cleared: its source name and the date it carried.</summary>
    private sealed record ClearedSnapshot(string Source, DateTime? UpdatedAt);

    /// <summary>
    /// Nulls the five vendor snapshots of <paramref name="entity"/> — ARCA (FiscalDataJson), LUFE, BCRA,
    /// CASFOG and NOSIS — together with their *DataUpdatedAt stamps and the uploaded NOSIS Manager report
    /// (which is not a score source and is not returned), and returns the ones that held
    /// something (payload or stamp), named as the score sources name them. Mutates only; the caller's
    /// existing save persists it. MipymeCategory, Sector and FiscalRegime are not snapshots and stay.
    /// </summary>
    private static IReadOnlyList<ClearedSnapshot> ClearVendorSnapshots(Entity entity)
    {
        var cleared = new List<ClearedSnapshot>(5);

        if (entity.FiscalDataJson is not null || entity.FiscalDataUpdatedAt is not null)
            cleared.Add(new ClearedSnapshot(RescoreSource.Arca, entity.FiscalDataUpdatedAt));
        if (entity.LufeDataJson is not null || entity.LufeDataUpdatedAt is not null)
            cleared.Add(new ClearedSnapshot(RescoreSource.Lufe, entity.LufeDataUpdatedAt));
        if (entity.BcraDataJson is not null || entity.BcraDataUpdatedAt is not null)
            cleared.Add(new ClearedSnapshot(RescoreSource.Bcra, entity.BcraDataUpdatedAt));
        if (entity.CasfogDataJson is not null || entity.CasfogDataUpdatedAt is not null)
            cleared.Add(new ClearedSnapshot(RescoreSource.Casfog, entity.CasfogDataUpdatedAt));
        if (entity.NosisDataJson is not null || entity.NosisDataUpdatedAt is not null)
            cleared.Add(new ClearedSnapshot(RescoreSource.Nosis, entity.NosisDataUpdatedAt));

        entity.FiscalDataJson      = null;
        entity.FiscalDataUpdatedAt = null;
        entity.LufeDataJson        = null;
        entity.LufeDataUpdatedAt   = null;
        entity.BcraDataJson        = null;
        entity.BcraDataUpdatedAt   = null;
        entity.CasfogDataJson      = null;
        entity.CasfogDataUpdatedAt = null;
        entity.NosisDataJson       = null;
        entity.NosisDataUpdatedAt  = null;

        // The uploaded NOSIS Manager report (DDL 087) describes the old CUIT too. It is not a score
        // source, so it is cleared without joining the list the rescore and the audit read.
        entity.NosisReportJson       = null;
        entity.NosisReportDate       = null;
        entity.NosisReportImportedAt = null;

        return cleared;
    }

    /// <summary>
    /// Saves a PUT. An unchanged CUIT is a plain update. A CHANGED CUIT re-derives the person type
    /// from the new prefix when that prefix is unambiguous, then relinks: a Humana gets the tenant's
    /// legajo for the new CUIT — created here if there is none, with nothing copied from the entity,
    /// whose snapshots belong to the old CUIT — and a Juridica is unlinked. The old legajo is kept:
    /// it is the old CUIT's person. Returns the legajo now linked, or null.
    /// </summary>
    private async Task<PersonLegajo?> SaveUpdatedEntityAsync(
        Entity entity,
        bool cuitChanged,
        Guid currentUserId,
        CancellationToken ct)
    {
        if (!cuitChanged)
        {
            await _entityRepository.UpdateAsync(entity, ct).ConfigureAwait(false);
            return null;
        }

        var fromPrefix = CuitHelper.PersonTypeFromPrefix(entity.CUIT);
        if (fromPrefix is not null && fromPrefix != entity.PersonType)
        {
            _logger.LogInformation(
                "Entity {EntityId} tenant {TenantId}: the new CUIT prefix changes the person type from {PreviousPersonType} to {PersonType}",
                entity.EntityId, entity.TenantId, entity.PersonType, fromPrefix);

            entity.PersonType = fromPrefix;
        }

        if (entity.PersonType != PersonTypes.Humana)
        {
            entity.PersonLegajoId = null;
            await _entityRepository.UpdateAsync(entity, ct).ConfigureAwait(false);
            return null;
        }

        var existing = await _personLegajoRepository
            .GetByCuitAsync(entity.CUIT, entity.TenantId, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            LinkToLegajo(entity, existing);
            await _entityRepository.UpdateAsync(entity, ct).ConfigureAwait(false);
            return existing;
        }

        var legajo = NewLegajo(entity, FiscalRegimes.Desconocido, null, null, null, currentUserId, entity.UpdatedAt);
        LinkToLegajo(entity, legajo);

        try
        {
            await _personLegajoRepository
                .SaveEntityWithLegajoAsync(entity, entityIsNew: false, legajo, legajoIsNew: true, ct)
                .ConfigureAwait(false);

            return legajo;
        }
        catch (DuplicateCuitException ex) when (ex.Scope == DuplicateCuitScope.PersonLegajo)
        {
            var winner = await _personLegajoRepository
                    .GetByCuitAsync(entity.CUIT, entity.TenantId, ct)
                    .ConfigureAwait(false)
                ?? throw new DuplicateCuitException(DuplicateCuitScope.Entity);

            LinkToLegajo(entity, winner);
            await _entityRepository.UpdateAsync(entity, ct).ConfigureAwait(false);
            return winner;
        }
    }

    /// <summary>
    /// The entity's legajo, read tenant-scoped, or null for an entity without one. A dangling link
    /// (the FK makes it impossible, so this is belt and braces) is logged and treated as no legajo.
    /// </summary>
    private async Task<PersonLegajo?> LoadLinkedLegajoAsync(Entity entity, CancellationToken ct)
    {
        if (entity.PersonLegajoId is not Guid legajoId) return null;

        var legajo = await _personLegajoRepository
            .GetByIdAsync(legajoId, entity.TenantId, ct)
            .ConfigureAwait(false);

        if (legajo is null)
        {
            _logger.LogWarning(
                "Entity {EntityId} links legajo {PersonLegajoId}, which was not found for tenant {TenantId}; updating the entity only.",
                entity.EntityId, legajoId, entity.TenantId);
        }

        return legajo;
    }

    /// <summary>
    /// Saves an existing entity — together with its legajo, in one SaveChanges, when it has one —
    /// after an action changed both. Without a legajo this is the plain entity update it always was.
    /// </summary>
    private async Task SaveEntityAndLinkedLegajoAsync(Entity entity, PersonLegajo? legajo, CancellationToken ct)
    {
        if (legajo is null)
        {
            await _entityRepository.UpdateAsync(entity, ct).ConfigureAwait(false);
            return;
        }

        legajo.UpdatedAt = DateTime.UtcNow;

        await _personLegajoRepository
            .SaveEntityWithLegajoAsync(entity, entityIsNew: false, legajo, legajoIsNew: false, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// refresh-arca when ARCA answered with datosGenerales and a key status but no fiscal section — the
    /// likely shape for a CUIT whose key is not active, i.e. exactly the case arca_cuit_activo exists to
    /// catch. The key status and its read date are merged into the entity's and the legajo's snapshots (a
    /// minimal one with empty lists when there is none). Lists, regime, category, the activity start and
    /// both snapshot stamps are left as they were, so older lists never look freshly read: the key status
    /// carries its own date. A snapshot that does not parse is left untouched.
    /// </summary>
    private async Task<IActionResult> RefreshKeyStatusOnlyAsync(
        Entity entity,
        string estadoClave,
        CancellationToken ct)
    {
        var legajo = await LoadLinkedLegajoAsync(entity, ct).ConfigureAwait(false);
        PersonSnapshotWriter.ApplyKeyStatusOnly(entity, legajo, estadoClave, DateTime.UtcNow);

        await SaveEntityAndLinkedLegajoAsync(entity, legajo, ct).ConfigureAwait(false);

        // No CUIT, no name and no status text: the ids are enough to find the row.
        _logger.LogWarning(
            "ARCA returned the key status but no fiscal section for entity {EntityId} (legajo {PersonLegajoId}) tenant {TenantId}; only the key status was stored.",
            entity.EntityId, legajo?.PersonLegajoId, entity.TenantId);

        return Ok(await BuildEntityResponseAsync(entity, legajo, ct).ConfigureAwait(false));
    }

    // ── ARCA helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps ARCA's answer to the lookup payload. <paramref name="personType"/> is the resolved type
    /// (<see cref="CuitHelper.ResolvePersonType"/>), which also decides the razón social: a company
    /// keeps ARCA's <c>razonSocial</c>; a persona humana gets "APELLIDO, NOMBRE" built from the parts
    /// ARCA actually sent, so a missing part never leaves a stray ", ".
    /// </summary>
    private static ArcaLookupResponse MapArcaToLookupResponse(
        ArcaPersonaData data,
        string personType,
        string? mipymeCategoria = null)
    {
        // Normalized here so the Portal autofills a clean name (no wrapping quotes, no stray control
        // characters) and POST /api/entities receives it clean. data.FiscalData — the payload stored as
        // FiscalDataJson — is passed through untouched: vendor blobs stay exactly as ARCA sent them.
        var composed = personType == PersonTypes.Humana
            ? ComposeHumanaName(data)
            : data.RazonSocial;
        var razonSocial = BusinessNameNormalizer.Normalize(composed);

        var domicilioFiscal = data.DomicilioFiscal is null
            ? null
            : new ArcaDomicilioFiscalDto(
                CodPostal:            data.DomicilioFiscal.CodigoPostal,
                Direccion:            FormatDireccion(data.DomicilioFiscal.Calle, data.DomicilioFiscal.Numero),
                Localidad:            data.DomicilioFiscal.Localidad,
                DescripcionProvincia: data.DomicilioFiscal.DescripcionProvincia,
                IdProvincia:          data.DomicilioFiscal.IdProvincia,
                TipoDomicilio:        data.DomicilioFiscal.TipoDomicilio);

        return new ArcaLookupResponse
        {
            Cuit               = FormatCuit(data.Cuit),
            RazonSocial        = razonSocial.Length > 0 ? razonSocial : "DESCONOCIDO",
            TipoPersona        = data.TipoPersona,
            EstadoClave        = data.EstadoClave,
            DomicilioFiscal    = domicilioFiscal,
            Sector             = MapCiiuToSector(data.IdActividadPrincipal),
            MipymeCategoria    = mipymeCategoria,
            NombreFantasia     = null,
            ActividadPrincipal = data.DescripcionActividadPrincipal,
            CodigoCiiu         = data.IdActividadPrincipal,
            MesCierre          = data.MesCierre,
            FormaJuridica      = data.FormaJuridica,
            Fiscal             = data.FiscalData,
            PersonType         = personType
        };
    }

    /// <summary>
    /// "APELLIDO, NOMBRE" from the non-empty parts only; falls back to ARCA's razonSocial, then null
    /// (the caller then answers "DESCONOCIDO").
    /// </summary>
    private static string? ComposeHumanaName(ArcaPersonaData data)
    {
        var parts = new[] { data.Apellido, data.Nombre }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();

        if (parts.Length > 0) return string.Join(", ", parts);

        return string.IsNullOrWhiteSpace(data.RazonSocial) ? null : data.RazonSocial;
    }

    private static string? MapCiiuToSector(string? ciiu)
    {
        if (string.IsNullOrEmpty(ciiu) || ciiu.Length < 2) return null;

        return ciiu[..2] switch
        {
            "01" or "02" or "03" or "11" or "14" or "16"               => "Agropecuario",
            "10"                                                         => "Alimentos",
            "12" or "13" or "15" or "17" or "18" or "19" or "20" or
            "21" or "22" or "23" or "24" or "25" or "26" or "27" or
            "28" or "29" or "30" or "31" or "32" or "33"               => "Manufactura",
            "41" or "42" or "43"                                         => "Construccion",
            "45" or "46" or "47"                                         => "Comercio",
            "49" or "50" or "51" or "52" or "53"                        => "Logistica",
            "55" or "56" or "58" or "59" or "60" or "61" or "69" or
            "70" or "71" or "72" or "73" or "74" or "75"               => "Servicios",
            "62" or "63"                                                 => "Software / Tech",
            "68"                                                         => "Real Estate",
            _                                                            => null
        };
    }

    private static string? NormalizeMipymeCategoria(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var n = raw.Trim()
            .Replace("á","a").Replace("é","e").Replace("í","i")
            .Replace("ó","o").Replace("ú","u").Replace("ñ","n")
            .Replace("Á","A").Replace("É","E").Replace("Í","I")
            .Replace("Ó","O").Replace("Ú","U").Replace("Ñ","N")
            .ToUpperInvariant();
        return n switch
        {
            "MICRO"    => "Micro",
            "PEQUENA"  => "Pequenia",   // covers "Pequeña" and "Pequena"
            "PEQUENIA" => "Pequenia",   // the canonical value itself, so re-normalising is a no-op
            "MEDIANA"  => "Mediana",
            _          => null
        };
    }

    private static string FormatCuit(string cuit)
    {
        cuit = cuit.Replace("-", "");
        return cuit.Length == 11
            ? $"{cuit[..2]}-{cuit.Substring(2, 8)}-{cuit[10]}"
            : cuit;
    }

    private static string? FormatDireccion(string? calle, string? numero)
    {
        var parts = new[] { calle, numero }.Where(s => !string.IsNullOrWhiteSpace(s));
        var result = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private string GetTenantCuit(string tenantId)
    {
        return _configuration[$"Tenants:{tenantId}:Cuit"]
            ?? throw new InvalidOperationException($"CUIT not configured for tenant {tenantId}");
    }

    private Task LogArcaCallAsync(
        string tenantId,
        string method,
        bool isSuccess,
        int durationMs,
        string? errorMessage)
    {
        return _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "ARCA",
            endpoint:         $"/ws_sr_constancia_inscripcion/{method}",
            responseStatus:   isSuccess ? 200 : 502,
            inputTokens:      null,
            outputTokens:     null,
            estimatedCostUsd: null,
            isSuccess:        isSuccess,
            errorMessage:     errorMessage,
            durationMs:       durationMs,
            ct:               CancellationToken.None);
    }
}
