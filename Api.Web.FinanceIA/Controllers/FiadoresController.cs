// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// CRUD over a PyME's fiadores (guarantors). Tenant-isolated, and nested under the entity because
/// the guarantor relationship belongs to the PyME and persists across analyses.
///
/// <para>
/// Candidates come from the entity's ALREADY-PERSISTED LUFE blob (<c>LufeData.Autoridades[]</c>) —
/// this controller never calls LUFE. Exactly one fiador is Active per (Entity, CUIT); adding a
/// second is a 409, checked here before the filtered unique index would throw. Removal is soft:
/// Status flips 'Active' -&gt; 'Removed' and no row is ever deleted.
/// </para>
///
/// <para>
/// MANUAL ADD AND EDIT (DDL 074). LUFE's socios/autoridades are often wrong or incomplete, so a fiador
/// can also be added by hand (Origen='Manual', any CUIT with a valid check digit, LUFE or not), and the
/// name, cargo and percentage of any Active fiador can be corrected by PATCH. Origen never changes; what
/// LUFE currently says is surfaced beside a 'Lufe' fiador (<see cref="FiadorResponse.LufeOriginal"/>,
/// <see cref="FiadorResponse.Editado"/>), derived per read from the persisted snapshot. ARCA can prefill
/// the manual form (<see cref="ArcaLookupAsync"/>) without creating anything.
/// </para>
///
/// <para>
/// NOSIS is fetched ON DEMAND ONLY, by <see cref="RefreshFiadorNosisAsync"/>, against the FIADOR
/// variable group (<c>Nosis:VrFiador</c>) and the fiador's OWN CUIT rather than the PyME's. Adding
/// a fiador does NOT query it: NOSIS is billed per query, so it is fetched when an analyst asks —
/// the same rule <c>EntitiesController</c> applies to the PyME's own snapshot, which is likewise
/// not enriched at creation. A fiador therefore carries a null snapshot until someone refreshes it.
/// </para>
///
/// <para>
/// LEGAJO (M10 Phase 5). A fiador persona humana gets the tenant's legajo for its CUIT through
/// <see cref="LinkFiadorLegajoAsync"/> — created, or linked when the person already has one — the only
/// writer of Fiadores.PersonLegajoId. The legajo then takes the fiador's documents (uploaded through
/// this PyME's entity), its own consent, and its BCRA/ARCA refreshes (api/person-legajos/{id}).
/// </para>
///
/// <para>
/// PII: every response this controller returns names a natural person. Nothing here is ever logged
/// beyond FiadorId / EntityId / TenantId, and no prompt builder reads these DTOs.
/// </para>
/// </summary>
[ApiController]
[Route("api/entities/{entityId:guid}/fiadores")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class FiadoresController : ControllerBase
{
    private readonly ITenantContextAccessor         _tenantContextAccessor;
    private readonly IFiadorRepository              _fiadorRepository;
    private readonly IEntityRepository              _entityRepository;
    private readonly IUserRepository                _userRepository;
    private readonly IAuditService                  _auditService;
    private readonly INosisService                  _nosisService;
    private readonly IIntegrationAccessService      _integrationAccess;
    private readonly IArcaConstanciaService         _arcaConstanciaService;
    private readonly IApiCallLogService             _apiCallLogService;
    private readonly IPersonLegajoRepository        _personLegajoRepository;
    private readonly IConfiguration                 _configuration;
    private readonly ILogger<FiadoresController>    _logger;

    private const string OrigenLufe   = "Lufe";
    private const string OrigenManual = "Manual";

    public FiadoresController(
        ITenantContextAccessor      tenantContextAccessor,
        IFiadorRepository           fiadorRepository,
        IEntityRepository           entityRepository,
        IUserRepository             userRepository,
        IAuditService               auditService,
        INosisService               nosisService,
        IIntegrationAccessService   integrationAccess,
        IArcaConstanciaService      arcaConstanciaService,
        IApiCallLogService          apiCallLogService,
        IPersonLegajoRepository     personLegajoRepository,
        IConfiguration              configuration,
        ILogger<FiadoresController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _fiadorRepository      = fiadorRepository;
        _entityRepository      = entityRepository;
        _userRepository        = userRepository;
        _auditService          = auditService;
        _nosisService          = nosisService;
        _integrationAccess     = integrationAccess;
        _arcaConstanciaService = arcaConstanciaService;
        _apiCallLogService     = apiCallLogService;
        _personLegajoRepository = personLegajoRepository;
        _configuration         = configuration;
        _logger                = logger;
    }

    // ── GET /api/entities/{entityId}/fiadores/candidates ───────────────────────

    /// <summary>
    /// Returns the PyME's socios/autoridades as candidate fiadores, each flagged with whether it is
    /// already an Active fiador so the Portal can disable it. Read from the entity's PERSISTED LUFE
    /// snapshot — this does NOT re-fetch LUFE.
    /// </summary>
    /// <response code="200">The candidate list. Empty when the entity has no LUFE data yet, or none could be read.</response>
    /// <response code="404">Entity not found for this tenant.</response>
    [HttpGet("candidates")]
    [Authorize(Roles = Roles.FiadorReaders)]
    [ProducesResponseType(typeof(List<FiadorCandidateResponse>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetCandidatesAsync(
        [FromRoute] Guid entityId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // Three columns, not the whole row: the entity's other four nvarchar(max) snapshots are
        // nothing to do with fiadores. A null result means no such entity in this tenant (404); a
        // non-null one whose Json is null means the entity exists but LUFE was never fetched (200,
        // empty list). Collapsing those two into one check would 404 a real entity.
        var snapshot = await _entityRepository
            .GetLufeSnapshotAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var activeFiadores = await _fiadorRepository
            .GetActiveByEntityAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var candidates = BuildCandidates(snapshot.Json, activeFiadores, entityId);

        _logger.LogInformation(
            "Listed {Count} fiador candidates for entity {EntityId} tenant {TenantId}",
            candidates.Count, entityId, tenantId);

        return Ok(candidates);
    }

    // ── GET /api/entities/{entityId}/fiadores ──────────────────────────────────

    /// <summary>
    /// Returns the PyME's Active fiadores. 'Removed' rows are tombstones and are never listed.
    /// Each carries its stored fields, its own NOSIS snapshot (null until it is refreshed), and the
    /// edit markers derived from the entity's persisted LUFE snapshot (<c>editado</c>,
    /// <c>lufeOriginal</c>, <c>updatedAt</c>, <c>updatedByName</c>).
    /// </summary>
    /// <response code="200">The entity's Active fiadores.</response>
    /// <response code="404">Entity not found for this tenant.</response>
    [HttpGet]
    [Authorize(Roles = Roles.FiadorReaders)]
    [ProducesResponseType(typeof(List<FiadorResponse>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetFiadoresAsync(
        [FromRoute] Guid entityId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // The LUFE column and nothing else — the same narrow read the candidates use, never a full-row
        // load dragging five nvarchar(max) snapshots off disk. Null means no such entity (404); a null
        // Json only means LUFE was never fetched, and then no fiador carries a lufeOriginal.
        var snapshot = await _entityRepository
            .GetLufeSnapshotAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var fiadores = await _fiadorRepository
            .GetActiveByEntityAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var responses = await MapFiadoresAsync(fiadores, snapshot.Json, entityId, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Listed {Count} fiadores for entity {EntityId} tenant {TenantId}",
            responses.Count, entityId, tenantId);

        return Ok(responses);
    }

    // ── POST /api/entities/{entityId}/fiadores ─────────────────────────────────

    /// <summary>
    /// Adds a fiador, by one of two paths chosen by <see cref="AddFiadorRequest.Origen"/>.
    ///
    /// <para>
    /// LUFE (origen absent or "Lufe" — unchanged): one of the PyME's LUFE socios/autoridades. The CUIT
    /// must appear in the entity's persisted <c>Autoridades[]</c>: the row is stamped Origen='Lufe', so
    /// a CUIT LUFE never reported would make that provenance a lie. Denominación, cargo and porcentaje
    /// are copied from the matched LUFE entry — the request may carry them, but the blob wins.
    /// </para>
    ///
    /// <para>
    /// MANUAL (origen "Manual", DDL 074): the analyst's own data, stored as sent and stamped
    /// Origen='Manual'. The CUIT needs a valid check digit and may or may not appear in LUFE; the
    /// denominación is required (2–300), the cargo optional (≤ 200, free text), the porcentaje optional
    /// (0–100, 2 decimals). See <see cref="AddManualFiadorAsync"/>.
    /// </para>
    ///
    /// <para>
    /// Both paths share the one-Active-per-(entity, CUIT) 409 and the NOSIS rule: adding never queries it.
    /// </para>
    /// </summary>
    /// <response code="201">Fiador added. The NOSIS snapshot is null — fetch it with POST {fiadorId}/refresh-nosis.</response>
    /// <response code="400">CUIT absent/malformed, unknown origen, not among the entity's socios/autoridades (LUFE path), or a manual field out of bounds.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Entity not found for this tenant.</response>
    /// <response code="409">This CUIT is already an Active fiador of this entity.</response>
    [HttpPost]
    [Authorize(Roles = Roles.FiadorManagers)]
    [ProducesResponseType(typeof(FiadorResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> AddFiadorAsync(
        [FromRoute] Guid entityId,
        [FromBody] AddFiadorRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        if (string.IsNullOrWhiteSpace(request.Cuit))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Se requiere el CUIT del fiador.");

        var origen = ResolveOrigen(request.Origen);
        if (origen is null)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El origen del fiador debe ser 'Lufe' o 'Manual'.");

        if (origen == OrigenManual)
            return await AddManualFiadorAsync(entityId, tenantId, userId, request, cancellationToken)
                .ConfigureAwait(false);

        // Normalised to the stored hyphenated form BEFORE anything is compared: the column holds
        // 30-71632727-9, and both the candidate match and the duplicate check are plain string
        // equalities that a bare 30716327279 would silently fail.
        var cuit = FormatCuit(request.Cuit);

        var snapshot = await _entityRepository
            .GetLufeSnapshotAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var activeFiadores = await _fiadorRepository
            .GetActiveByEntityAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var match = BuildCandidates(snapshot.Json, activeFiadores, entityId)
            .FirstOrDefault(c => string.Equals(c.Cuit, cuit, StringComparison.Ordinal));
        if (match is null)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El CUIT no figura entre los socios/autoridades informados por Lufe para esta entidad.");

        // Checked here, not left to [UX_Fiadores_ActivePerEntityCuit]: the caller gets a clean 409
        // instead of a DbUpdateException surfacing as a 500. The index remains the real guarantee.
        var existing = await _fiadorRepository
            .GetActiveByEntityCuitAsync(entityId, cuit, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El CUIT ya figura como fiador activo de esta entidad.");

        var fiador = new Fiador
        {
            TenantId        = tenantId,
            EntityId        = entityId,
            CUIT            = match.Cuit,
            // From the blob, never from the request: the row must agree with the provenance
            // Origen='Lufe' asserts. The request's optional fields are advisory (see AddFiadorRequest).
            Denominacion    = match.Denominacion,
            Cargo           = match.Cargo,
            PorcAccionista  = match.PorcAccionista,
            Origen          = OrigenLufe,
            // Deliberately null, not a missing feature: NOSIS is billed per query, so adding a
            // fiador never spends one. The analyst fetches it explicitly via
            // POST {fiadorId}/refresh-nosis — the same on-demand rule EntitiesController applies to
            // the PyME's own snapshot, which is likewise not enriched at creation.
            NosisDataJson   = null,
            NosisFetchedAt  = null,
            Status          = "Active",
            CreatedByUserId = userId,
            CreatedAt       = DateTime.UtcNow,
        };

        await _fiadorRepository.CreateAsync(fiador, cancellationToken).ConfigureAwait(false);

        LogFiadorAdded(fiador, tenantId, userId);

        var response = await MapFiadoresAsync([fiador], snapshot.Json, entityId, cancellationToken)
            .ConfigureAwait(false);

        return StatusCode(StatusCodes.Status201Created, response[0]);
    }

    /// <summary>
    /// The MANUAL half of <see cref="AddFiadorAsync"/> (DDL 074): stores the analyst's CUIT, name, cargo
    /// and percentage as sent, stamped Origen='Manual'. Unlike the LUFE path the CUIT is NOT matched
    /// against <c>Autoridades[]</c> — that is the point: LUFE is often incomplete — so the check digit is
    /// verified instead, the only guard left against a typo. Fields are validated before anything is
    /// read, then entity existence (404) and the one-Active-per-(entity, CUIT) rule (409).
    /// </summary>
    private async Task<IActionResult> AddManualFiadorAsync(
        Guid entityId, string tenantId, Guid userId, AddFiadorRequest request, CancellationToken ct)
    {
        if (!TryNormalizeCuit(request.Cuit, out var digits))
            return Problem(statusCode: 400, title: "Bad Request", detail: InvalidCuitDetail);

        var (denominacion, denominacionError) = NormalizeDenominacion(request.Denominacion);
        if (denominacionError is not null)
            return Problem(statusCode: 400, title: "Bad Request", detail: denominacionError);

        var (cargo, cargoError) = NormalizeCargo(request.Cargo);
        if (cargoError is not null)
            return Problem(statusCode: 400, title: "Bad Request", detail: cargoError);

        var porcError = ValidatePorcAccionista(request.PorcAccionista);
        if (porcError is not null)
            return Problem(statusCode: 400, title: "Bad Request", detail: porcError);

        // Existence only: a manual fiador never reads the LUFE blob, and its response carries no
        // lufeOriginal (that is shown for Origen='Lufe' only).
        var exists = await _entityRepository
            .ExistsByIdAsync(entityId, tenantId, ct)
            .ConfigureAwait(false);
        if (!exists)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var cuit = LegajoCuit.Format(digits);

        // Same 409 as the LUFE path, and for the same reason: the filtered unique index is the real
        // guarantee, but it would surface as a 500. A manual add of a CUIT already added from LUFE (or
        // vice versa) is the same person twice, so it is refused regardless of origen.
        var existing = await _fiadorRepository
            .GetActiveByEntityCuitAsync(entityId, cuit, tenantId, ct)
            .ConfigureAwait(false);
        if (existing is not null)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El CUIT ya figura como fiador activo de esta entidad.");

        var fiador = new Fiador
        {
            TenantId        = tenantId,
            EntityId        = entityId,
            CUIT            = cuit,
            Denominacion    = denominacion,
            Cargo           = cargo,
            PorcAccionista  = request.PorcAccionista,
            Origen          = OrigenManual,
            // Null for the same reason as the LUFE path: NOSIS is billed per query and is fetched only
            // on explicit demand via POST {fiadorId}/refresh-nosis.
            NosisDataJson   = null,
            NosisFetchedAt  = null,
            Status          = "Active",
            CreatedByUserId = userId,
            CreatedAt       = DateTime.UtcNow,
        };

        await _fiadorRepository.CreateAsync(fiador, ct).ConfigureAwait(false);

        LogFiadorAdded(fiador, tenantId, userId);

        var response = await MapFiadoresAsync([fiador], lufeJson: null, entityId, ct).ConfigureAwait(false);

        return StatusCode(StatusCodes.Status201Created, response[0]);
    }

    /// <summary>
    /// The audit row and log line both add paths write. The audit records the ORIGEN only: the identity
    /// of the person added is exactly what must not enter the audit trail as free text, and the FiadorId
    /// points at the row for anyone entitled to read it. The log line carries ids only.
    /// </summary>
    private void LogFiadorAdded(Fiador fiador, string tenantId, Guid userId)
    {
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Fiador.Added",
            severity:     "Info",
            entityType:   "Fiador",
            entityId:     fiador.FiadorId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new { fiador.Origen }),
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Added fiador {FiadorId} to entity {EntityId} tenant {TenantId}",
            fiador.FiadorId, fiador.EntityId, tenantId);
    }

    // ── PATCH /api/entities/{entityId}/fiadores/{fiadorId} ─────────────────────

    /// <summary>
    /// Corrects the name, cargo and/or percentage of an ACTIVE fiador of ANY origen (DDL 074). Merge-patch
    /// semantics (see <see cref="PatchFiadorRequest"/>): an absent field is left alone, an explicit null
    /// clears cargo or porcentaje, and the denominación can be changed but never cleared. The CUIT is not
    /// editable — sending a different one is a 400 — and Origen never changes, so a corrected LUFE fiador
    /// stays 'Lufe' and shows what LUFE reports beside it.
    ///
    /// <para>
    /// Stamps UpdatedAt / UpdatedByUserId only when something actually changed: a PATCH that repeats the
    /// stored values returns 200 with the current state and writes nothing — no stamp, no audit row.
    /// Concurrency is handled like the remove: the write is one conditional UPDATE guarded on
    /// Status='Active', so a fiador removed meanwhile is a 409. Two concurrent edits are last-writer-wins,
    /// as with every other fiador write (there is no rowversion column).
    /// </para>
    /// </summary>
    /// <response code="200">The fiador, edited (or unchanged when the request repeated its values).</response>
    /// <response code="400">No editable field sent, a field out of bounds, or a CUIT different from the stored one.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Entity or fiador not found for this tenant, or the fiador belongs to another entity.</response>
    /// <response code="409">The fiador is not Active, or was removed by a concurrent request.</response>
    [HttpPatch("{fiadorId:guid}")]
    [Authorize(Roles = Roles.FiadorManagers)]
    [ProducesResponseType(typeof(FiadorResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> PatchFiadorAsync(
        [FromRoute] Guid entityId,
        [FromRoute] Guid fiadorId,
        [FromBody] PatchFiadorRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        if (!request.DenominacionSpecified && !request.CargoSpecified && !request.PorcAccionistaSpecified)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "No se indicó ningún campo a modificar (denominacion, cargo, porcAccionista).");

        // Field validation first: it needs nothing from the database.
        string? newDenominacion = null;
        if (request.DenominacionSpecified)
        {
            var (value, denominacionError) = NormalizeDenominacion(request.Denominacion);
            if (denominacionError is not null)
                return Problem(statusCode: 400, title: "Bad Request", detail: denominacionError);
            newDenominacion = value;
        }

        string? newCargo = null;
        if (request.CargoSpecified)
        {
            var (value, cargoError) = NormalizeCargo(request.Cargo);
            if (cargoError is not null)
                return Problem(statusCode: 400, title: "Bad Request", detail: cargoError);
            newCargo = value;
        }

        if (request.PorcAccionistaSpecified)
        {
            var porcError = ValidatePorcAccionista(request.PorcAccionista);
            if (porcError is not null)
                return Problem(statusCode: 400, title: "Bad Request", detail: porcError);
        }

        // The LUFE column, not mere existence: the response compares the fiador with what LUFE reports.
        var snapshot = await _entityRepository
            .GetLufeSnapshotAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var fiador = await _fiadorRepository
            .GetByIdAsync(fiadorId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        // Same parent rule as remove and refresh-nosis: a fiador of PyME A is not editable through PyME
        // B's route inside the same tenant.
        if (fiador is null || fiador.EntityId != entityId)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el fiador.");

        if (!string.Equals(fiador.Status, "Active", StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El fiador no está activo; no se puede editar.");

        // Normalised before comparing, so the same CUIT posted without hyphens is not a "change".
        if (request.Cuit is not null
            && !string.Equals(FormatCuit(request.Cuit), fiador.CUIT, StringComparison.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El CUIT del fiador no es editable.");

        var denominacion   = request.DenominacionSpecified   ? newDenominacion        : fiador.Denominacion;
        var cargo          = request.CargoSpecified          ? newCargo               : fiador.Cargo;
        var porcAccionista = request.PorcAccionistaSpecified ? request.PorcAccionista : fiador.PorcAccionista;

        // Field NAMES, the only thing about this edit that may enter the audit trail.
        var changedFields = new List<string>(3);
        if (!string.Equals(denominacion, fiador.Denominacion, StringComparison.Ordinal))
            changedFields.Add("denominacion");
        if (!string.Equals(cargo, fiador.Cargo, StringComparison.Ordinal))
            changedFields.Add("cargo");
        if (porcAccionista != fiador.PorcAccionista)
            changedFields.Add("porcAccionista");

        if (changedFields.Count > 0)
        {
            var updatedAt = DateTime.UtcNow;

            // CancellationToken.None, as the other fiador writes: a client that walks away after the
            // checks must not leave the analyst unsure whether the correction landed.
            var applied = await _fiadorRepository
                .UpdateDetailsAsync(
                    fiadorId, tenantId, denominacion, cargo, porcAccionista, updatedAt, userId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            // False means the fiador left 'Active' between our read and write (a concurrent removal).
            if (!applied)
                return Problem(statusCode: 409, title: "Conflict",
                    detail: "El fiador fue modificado por otra operación. Verifique el estado actual antes de reintentar.");

            // Never the old or new values: they identify a natural person.
            _ = _auditService.LogAsync(
                tenantId:     tenantId,
                userId:       userId,
                action:       "Fiador.Edited",
                severity:     "Info",
                entityType:   "Fiador",
                entityId:     fiadorId.ToString(),
                ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
                requestId:    HttpContext.TraceIdentifier,
                newValueJson: JsonSerializer.Serialize(new { ChangedFields = changedFields }),
                ct:           CancellationToken.None);

            _logger.LogInformation(
                "Edited fiador {FiadorId} of entity {EntityId} tenant {TenantId}",
                fiadorId, entityId, tenantId);

            // The read was AsNoTracking; the instance is brought in line with what was just written.
            fiador.Denominacion    = denominacion;
            fiador.Cargo           = cargo;
            fiador.PorcAccionista  = porcAccionista;
            fiador.UpdatedAt       = updatedAt;
            fiador.UpdatedByUserId = userId;
        }

        var response = await MapFiadoresAsync([fiador], snapshot.Json, entityId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response[0]);
    }

    // ── GET /api/entities/{entityId}/fiadores/arca-lookup ──────────────────────

    /// <summary>
    /// Looks a CUIT up in ARCA to PREFILL the manual fiador form: returns the name and person type ARCA
    /// knows, and creates nothing — no fiador, no entity, no audit row. Reuses the client behind
    /// <c>POST /api/entities/lookup-arca</c> (ws_sr_constancia_inscripcion getPersona_v2) with the same
    /// <c>Tenants:{tenantId}:Cuit</c> representation, and writes the same "ARCA" ApiCallLogs row.
    ///
    /// <para>
    /// FAULT-ISOLATED: any ARCA failure (HTTP error, timeout, SOAP/auth fault) — and a tenant with no ARCA
    /// CUIT configured — is a 502 with a Spanish detail, never a 500; the analyst can still type the name.
    /// Only a cancellation by the caller propagates. A person ARCA does not know is a 404 whose detail
    /// differs from the unknown-entity 404.
    /// </para>
    /// </summary>
    /// <response code="200">What ARCA knows about the CUIT.</response>
    /// <response code="400">CUIT absent or invalid (11 digits with a valid check digit).</response>
    /// <response code="404">Entity not found for this tenant, or ARCA does not know the CUIT.</response>
    /// <response code="502">ARCA is unavailable or not configured for this tenant.</response>
    [HttpGet("arca-lookup")]
    [Authorize(Roles = Roles.FiadorManagers)]
    [ProducesResponseType(typeof(FiadorArcaLookupResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> ArcaLookupAsync(
        [FromRoute] Guid entityId,
        [FromQuery] string? cuit,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        if (!TryNormalizeCuit(cuit, out var digits))
            return Problem(statusCode: 400, title: "Bad Request", detail: InvalidCuitDetail);

        var exists = await _entityRepository
            .ExistsByIdAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        // Read directly rather than through a throwing helper: a tenant without an ARCA CUIT cannot use
        // the prefill, which is an unavailable integration (502), not a server fault (500).
        var cuitRepresentada = _configuration[$"Tenants:{tenantId}:Cuit"];
        if (string.IsNullOrWhiteSpace(cuitRepresentada))
        {
            _logger.LogWarning(
                "Fiador ARCA lookup: no ARCA CUIT configured for tenant {TenantId}; entity {EntityId}.",
                tenantId, entityId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "La consulta a ARCA no está configurada para esta organización.");
        }

        var sw = Stopwatch.StartNew();
        ArcaPersonaData? arcaData;
        try
        {
            arcaData = await _arcaConstanciaService
                .GetPersonaByCuitAsync(digits, cuitRepresentada, cancellationToken)
                .ConfigureAwait(false);
        }
        // An HttpClient timeout is a TaskCanceledException too; only the CALLER's cancellation propagates.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _ = LogArcaCallAsync(tenantId, isSuccess: false, (int)sw.ElapsedMilliseconds, ex.GetType().Name);

            // The exception TYPE only, not the exception: a SOAP fault's text can echo the CUIT queried,
            // which for a persona humana is personal data.
            _logger.LogWarning(
                "Fiador ARCA lookup failed ({ExceptionType}) for entity {EntityId} tenant {TenantId}.",
                ex.GetType().Name, entityId, tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "El servicio de ARCA no está disponible temporalmente.");
        }

        sw.Stop();

        if (arcaData is null)
        {
            _ = LogArcaCallAsync(tenantId, isSuccess: false, (int)sw.ElapsedMilliseconds, null);
            return Problem(statusCode: 404, title: "Not Found",
                detail: "ARCA no registra ninguna persona con el CUIT ingresado.");
        }

        _ = LogArcaCallAsync(tenantId, isSuccess: true, (int)sw.ElapsedMilliseconds, null);

        var cuitFormatted = LegajoCuit.Format(digits);

        // The resolver EntitiesController uses: the CUIT prefix when unambiguous, otherwise ARCA's
        // tipoPersona. Its disagreement warning names the two types only, never the CUIT.
        var tipoPersona = CuitHelper.ResolvePersonType(cuitFormatted, arcaData.TipoPersona, null, _logger);

        // Normalized like the PyME autofill (BusinessNameNormalizer): the form is prefilled with a clean
        // name. Null when nothing is left, as before.
        var composedName = tipoPersona == PersonTypes.Humana
            ? ComposeHumanaName(arcaData)
            : arcaData.RazonSocial;
        var normalizedName = BusinessNameNormalizer.Normalize(composedName);
        var denominacion   = normalizedName.Length > 0 ? normalizedName : null;

        _logger.LogInformation(
            "Fiador ARCA lookup answered for entity {EntityId} tenant {TenantId}.",
            entityId, tenantId);

        return Ok(new FiadorArcaLookupResponse
        {
            Cuit         = cuitFormatted,
            Denominacion = denominacion,
            TipoPersona  = tipoPersona,
        });
    }

    // ── DELETE /api/entities/{entityId}/fiadores/{fiadorId} ────────────────────

    /// <summary>
    /// Soft-removes a fiador: flips Status 'Active' -&gt; 'Removed'. The row is never deleted, so
    /// the analyst's selection history survives and the CUIT can be re-added later (a 'Removed' row
    /// does not occupy the one-Active-per-(entity, CUIT) slot). Idempotent: an already-removed
    /// fiador returns 204 and writes nothing.
    /// </summary>
    /// <response code="204">Fiador removed (or already removed).</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Fiador not found for this tenant, or it belongs to another entity.</response>
    /// <response code="409">The fiador was modified by a concurrent request.</response>
    [HttpDelete("{fiadorId:guid}")]
    [Authorize(Roles = Roles.FiadorManagers)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> RemoveFiadorAsync(
        [FromRoute] Guid entityId,
        [FromRoute] Guid fiadorId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        // Load-bearing twice over: it is the only thing that tells a genuinely missing fiador (404)
        // from one someone else already removed (204), and it is where the parent check comes from.
        var fiador = await _fiadorRepository
            .GetByIdAsync(fiadorId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        // Also 404 when the fiador exists but hangs off a different entity: the route asserts a
        // parent, and honouring a mismatched one would let a caller remove a fiador through an
        // entity they were not looking at.
        if (fiador is null || fiador.EntityId != entityId)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el fiador.");

        if (string.Equals(fiador.Status, "Removed", StringComparison.Ordinal))
            return NoContent();

        var applied = await _fiadorRepository
            .SetStatusAsync(fiadorId, tenantId, expectedStatus: "Active", newStatus: "Removed", cancellationToken)
            .ConfigureAwait(false);
        // False means the fiador left 'Active' between our read and write (a concurrent removal) —
        // refuse rather than silently overwrite.
        if (!applied)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El fiador fue modificado por otra operación. Verifique el estado actual antes de reintentar.");

        // Both ends of the transition. Safe to record: these are statuses, not identities.
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Fiador.Removed",
            severity:     "Info",
            entityType:   "Fiador",
            entityId:     fiadorId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new { Status = "Active" }),
            newValueJson: JsonSerializer.Serialize(new { Status = "Removed" }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Removed fiador {FiadorId} from entity {EntityId} tenant {TenantId}",
            fiadorId, entityId, tenantId);

        return NoContent();
    }

    // ── POST /api/entities/{entityId}/fiadores/{fiadorId}/refresh-nosis ────────

    /// <summary>
    /// Fetches this fiador's OWN NOSIS snapshot — by the FIADOR's CUIT, against the FIADOR
    /// variable group (<c>Nosis:VrFiador</c>, falling back to <c>Nosis:VR</c>) — and persists it on
    /// the fiador row. Idempotent: safe to retry after a failed fetch, and re-running simply takes
    /// a newer snapshot.
    ///
    /// <para>
    /// Separate from the list on purpose, and a POST rather than a GET, because NOSIS is BILLED PER
    /// QUERY: opening the fiadores tab must never spend one. This mirrors
    /// <c>POST /api/entities/{id}/refresh-nosis</c> gate for gate, with one deliberate difference —
    /// the fiadores routes carry a role gate and that gate is kept here, where the entity route has
    /// no role requirement at all. Here it is the MANAGE gate (<see cref="Roles.FiadorManagers"/>:
    /// Admin, Analyst, RiskManager) rather than the wider read gate the two GETs use
    /// (<see cref="Roles.FiadorReaders"/>, which adds Auditor): spending a billed query is not
    /// something read-only oversight does.
    /// </para>
    ///
    /// <para>
    /// A failed lookup returns BEFORE any write. A transient NOSIS outage must never overwrite a
    /// good stored snapshot with an empty one, and "we could not ask" must never be persisted as
    /// "NOSIS has nothing on this person" — the distinction <c>NosisConsultaEstado</c> exists to
    /// keep. A <c>NoDataReported</c> answer, by contrast, IS persisted: it is a real answer.
    /// </para>
    /// </summary>
    /// <param name="entityId">The PyME the fiador guarantees.</param>
    /// <param name="fiadorId">The fiador to refresh.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">Snapshot fetched and stored; the updated fiador is returned.</response>
    /// <response code="400">NOSIS is not enabled for this tenant, or not configured in this installation.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Entity or fiador not found for this tenant, or the fiador belongs to another entity.</response>
    /// <response code="409">The fiador is not Active, or was removed by a concurrent request.</response>
    /// <response code="502">NOSIS is unavailable, the stored CUIT is unusable, or the snapshot could not be persisted.</response>
    [HttpPost("{fiadorId:guid}/refresh-nosis")]
    [Authorize(Roles = Roles.FiadorManagers)]
    [ProducesResponseType(typeof(FiadorResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    public async Task<IActionResult> RefreshFiadorNosisAsync(
        [FromRoute] Guid entityId,
        [FromRoute] Guid fiadorId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null)
            return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null)
            return userError;

        // The LUFE column only, like GetFiadoresAsync: the returned fiador carries the same LUFE-derived
        // markers as the list, and a full entity read would drag five nvarchar(max) snapshot columns
        // across the wire for that. Null means no such entity.
        var entitySnapshot = await _entityRepository
            .GetLufeSnapshotAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entitySnapshot is null)
        {
            _logger.LogWarning(
                "NOSIS refresh: entity {EntityId} not found for tenant {TenantId}.",
                entityId, tenantId);
            return Problem(statusCode: 404, title: "Not Found", detail: "Entidad no encontrada.");
        }

        var fiador = await _fiadorRepository
            .GetByIdAsync(fiadorId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // The EntityId check is not redundant with the tenant scope: it stops a fiador of PyME A
        // being refreshed through PyME B's route inside the same tenant. Same rule as the remove path.
        if (fiador is null || fiador.EntityId != entityId)
        {
            _logger.LogWarning(
                "NOSIS refresh: fiador {FiadorId} not found for entity {EntityId} tenant {TenantId}.",
                fiadorId, entityId, tenantId);
            return Problem(statusCode: 404, title: "Not Found", detail: "Fiador no encontrado.");
        }

        // Checked before anything is billed: a removed fiador is a tombstone, and spending a paid
        // query to write a snapshot the repository would then refuse is pure waste.
        if (!string.Equals(fiador.Status, "Active", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "NOSIS refresh: fiador {FiadorId} tenant {TenantId} is not Active; refusing.",
                fiadorId, tenantId);
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El fiador no está activo; no se puede consultar NOSIS.");
        }

        // Entitlement (the tenant profile, §7.0b) before availability (the installation's NOSIS
        // configuration) — the same order and the same IIntegrationAccessService that
        // EntitiesController.RefreshNosisAsync uses. The entitlement read fails open: a degraded or
        // unreadable profile counts as entitled, leaving availability as the only gate.
        var profile = await _integrationAccess
            .ResolveEntitlementsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!profile.NosisEnabled)
        {
            _logger.LogInformation(
                "NOSIS refresh: requested for fiador {FiadorId} tenant {TenantId} while the tenant profile disables NOSIS.",
                fiadorId, tenantId);
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "NOSIS no está habilitado para este tenant.");
        }

        // NosisService collapses "switched off", "no credentials" and "NOSIS is down" into the same
        // LookupFailed, so the two configuration cases are separated out here and answered 400.
        // A 502 would invite a retry that can never succeed.
        if (!_integrationAccess.IsNosisConfigured())
        {
            _logger.LogWarning(
                "NOSIS refresh: requested for fiador {FiadorId} tenant {TenantId} while NOSIS is disabled or unconfigured.",
                fiadorId, tenantId);
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "NOSIS no está habilitado o no está configurado.");
        }

        // The column stores the hyphenated form (see FormatCuit); NOSIS wants the digits.
        if (!long.TryParse(fiador.CUIT?.Replace("-", ""), out var nosisCuit))
        {
            _logger.LogWarning(
                "NOSIS refresh: stored CUIT for fiador {FiadorId} tenant {TenantId} is not a valid number; cannot query NOSIS.",
                fiadorId, tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "El CUIT almacenado no es válido; no se puede consultar NOSIS.");
        }

        var nosisData = await _nosisService
            .GetVariablesAsync(tenantId, nosisCuit, NosisVariableGroup.Fiador, cancellationToken)
            .ConfigureAwait(false);

        // Return before any write: a transient failure must never overwrite a good stored snapshot
        // with an empty one, nor record "we did not ask" as "nothing on file".
        if (nosisData.Estado == NosisConsultaEstado.LookupFailed)
        {
            _logger.LogWarning(
                "NOSIS refresh: lookup failed for fiador {FiadorId} tenant {TenantId}; returning 502.",
                fiadorId, tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "El servicio de NOSIS no está disponible o no devolvió datos.");
        }

        var fetchedAt = DateTime.UtcNow;
        var snapshotJson = JsonSerializer.Serialize(nosisData);

        bool applied;
        try
        {
            // CancellationToken.None, as the add and remove paths do: once NOSIS has answered, a
            // client that walks away must not cost us the paid result.
            applied = await _fiadorRepository
                .SetNosisSnapshotAsync(fiadorId, tenantId, snapshotJson, fetchedAt, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The snapshot is never in the log line: NosisData carries PII.
            _logger.LogError(ex,
                "NOSIS refresh: failed to persist the snapshot for fiador {FiadorId} tenant {TenantId}.",
                fiadorId, tenantId);
            return Problem(statusCode: 502, title: "Bad Gateway",
                detail: "No se pudo guardar la información de NOSIS.");
        }

        // False means the fiador left 'Active' between our read and write (a concurrent removal).
        if (!applied)
        {
            _logger.LogWarning(
                "NOSIS refresh: fiador {FiadorId} tenant {TenantId} was modified concurrently; snapshot not stored.",
                fiadorId, tenantId);
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El fiador fue modificado por otra operación. Verifique el estado actual antes de reintentar.");
        }

        // No oldValueJson/newValueJson, for the same reason as Fiador.Added: everything that changed
        // here is the person's NOSIS record, which is exactly what must not enter the audit trail as
        // free text. The FiadorId points at the row for anyone entitled to read it.
        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     userId,
            action:     "Fiador.NosisRefreshed",
            severity:   "Info",
            entityType: "Fiador",
            entityId:   fiadorId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        // Only the estado is logged, never the payload.
        _logger.LogInformation(
            "NOSIS snapshot refreshed for fiador {FiadorId} entity {EntityId} tenant {TenantId}: estado={Estado}",
            fiadorId, entityId, tenantId, nosisData.Estado);

        // The read was AsNoTracking, so the in-memory instance is updated to match what was just
        // written rather than re-reading the row for a value we already hold.
        fiador.NosisDataJson  = snapshotJson;
        fiador.NosisFetchedAt = fetchedAt;

        var response = await MapFiadoresAsync([fiador], entitySnapshot.Json, entityId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response[0]);
    }

    // ── POST /api/entities/{entityId}/fiadores/{fiadorId}/legajo ───────────────

    /// <summary>
    /// Gives an ACTIVE fiador who is a persona humana a legajo (M10 Phase 5): creates the tenant's
    /// legajo for the fiador's CUIT, or links the one the tenant already has — the fiador may also be a
    /// client, or a fiador of another PyME, and a legajo is unique per (tenant, CUIT). Once linked, the
    /// fiador's documents are uploaded through this PyME's entity
    /// (POST api/documents/upload with entityId + personLegajoId), its consent is recorded on the legajo,
    /// and its BCRA/ARCA snapshots refresh through api/person-legajos/{personLegajoId}.
    ///
    /// <para>
    /// IDEMPOTENT: a fiador already linked gets the same legajo back, 200 with created=false. Two
    /// concurrent first calls end on ONE legajo: the insert that loses UX_PersonLegajos_TenantId_CUIT
    /// re-reads the winner and links it. Only Fiadores.PersonLegajoId is written on the fiador row.
    /// </para>
    ///
    /// <para>
    /// The PyME's own CUIT is refused (409 fiador_is_solicitante): the solicitante is never its own
    /// fiador, and linking would hand the solicitante's legajo to the fiador path. Logs and the audit row
    /// carry ids only; the new legajo's name is the fiador's stored denominación, and its fiscal regime
    /// starts Desconocido until the legajo's refresh-arca.
    /// </para>
    /// </summary>
    /// <param name="entityId">The PyME the fiador guarantees.</param>
    /// <param name="fiadorId">The fiador.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="201">A legajo was created and linked.</response>
    /// <response code="200">An existing legajo was linked, or the fiador was already linked.</response>
    /// <response code="400">fiador_not_persona_humana: the fiador's CUIT is not a persona humana's.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Entity or fiador not found for this tenant, or the fiador belongs to another entity.</response>
    /// <response code="409">fiador_not_active (the fiador was removed) or fiador_is_solicitante.</response>
    [HttpPost("{fiadorId:guid}/legajo")]
    [Authorize(Roles = Roles.FiadorManagers)]
    [ProducesResponseType(typeof(FiadorLegajoLinkResponse), 201)]
    [ProducesResponseType(typeof(FiadorLegajoLinkResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> LinkFiadorLegajoAsync(
        [FromRoute] Guid entityId,
        [FromRoute] Guid fiadorId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null)
            return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null)
            return userError;

        // CUIT only (a one-row, no-snapshot projection): it is what tells the solicitante apart.
        var entity = await _entityRepository
            .GetNameByIdAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            _logger.LogWarning(
                "Fiador legajo: entity {EntityId} not found for tenant {TenantId}.",
                entityId, tenantId);
            return Problem(statusCode: 404, title: "Not Found", detail: "Entidad no encontrada.");
        }

        var fiador = await _fiadorRepository
            .GetByIdAsync(fiadorId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // The EntityId check stops a fiador of PyME A being linked through PyME B's route inside the
        // same tenant, as on every other fiador write.
        if (fiador is null || fiador.EntityId != entityId)
        {
            _logger.LogWarning(
                "Fiador legajo: fiador {FiadorId} not found for entity {EntityId} tenant {TenantId}.",
                fiadorId, entityId, tenantId);
            return Problem(statusCode: 404, title: "Not Found", detail: "Fiador no encontrado.");
        }

        // 409, as refresh-nosis answers a removed fiador: it exists, but is a tombstone.
        if (!string.Equals(fiador.Status, "Active", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Fiador legajo: fiador {FiadorId} tenant {TenantId} is not Active; refusing.",
                fiadorId, tenantId);
            return CodedProblem(409, "Conflict", "fiador_not_active",
                "El fiador no está activo; no se le puede crear un legajo.");
        }

        if (CuitHelper.PersonTypeFromPrefix(fiador.CUIT) != PersonTypes.Humana)
        {
            _logger.LogInformation(
                "Fiador legajo: fiador {FiadorId} tenant {TenantId} is not a persona humana; refusing.",
                fiadorId, tenantId);
            return CodedProblem(400, "Bad Request", "fiador_not_persona_humana",
                "Solo un fiador persona humana (CUIT 20, 23, 24 o 27) puede tener legajo.");
        }

        if (string.Equals(LegajoCuit.Digits(fiador.CUIT), LegajoCuit.Digits(entity.CUIT), StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Fiador legajo: fiador {FiadorId} of entity {EntityId} tenant {TenantId} is the solicitante itself; refusing.",
                fiadorId, entityId, tenantId);
            return CodedProblem(409, "Conflict", "fiador_is_solicitante",
                "El fiador es la propia entidad solicitante; su legajo no puede vincularse como fiador.");
        }

        // Idempotent: an existing link is returned as it is. A dangling one (the FK makes it impossible,
        // so this is belt and braces) falls through and is relinked by CUIT.
        if (fiador.PersonLegajoId is Guid linkedId)
        {
            var linked = await _personLegajoRepository
                .GetByIdAsync(linkedId, tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (linked is not null)
            {
                return Ok(new FiadorLegajoLinkResponse { PersonLegajoId = linked.PersonLegajoId, Created = false });
            }

            _logger.LogWarning(
                "Fiador legajo: fiador {FiadorId} links legajo {PersonLegajoId}, which was not found for tenant {TenantId}; relinking.",
                fiadorId, linkedId, tenantId);
        }

        var existing = await _personLegajoRepository
            .GetByCuitAsync(fiador.CUIT, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var legajo = existing ?? NewFiadorLegajo(fiador, userId);
        fiador.PersonLegajoId = legajo.PersonLegajoId;

        var created = existing is null;
        try
        {
            await _personLegajoRepository
                .SaveFiadorLegajoLinkAsync(fiador, legajo, legajoIsNew: created, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (DuplicateCuitException ex) when (ex.Scope == DuplicateCuitScope.PersonLegajo && created)
        {
            // Another request created the legajo for this CUIT between the read and the insert. Link the
            // winner instead, once; the retry inserts nothing, so it cannot lose the same race again.
            var winner = await _personLegajoRepository
                .GetByCuitAsync(fiador.CUIT, tenantId, CancellationToken.None)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The legajo insert lost a uniqueness race, but no legajo was found for the fiador's CUIT.");

            legajo  = winner;
            created = false;
            fiador.PersonLegajoId = legajo.PersonLegajoId;

            await _personLegajoRepository
                .SaveFiadorLegajoLinkAsync(fiador, legajo, legajoIsNew: false, CancellationToken.None)
                .ConfigureAwait(false);
        }

        // Ids and the flag only: the legajo names a natural person.
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Fiador.LegajoLinked",
            severity:     "Info",
            entityType:   "Fiador",
            entityId:     fiadorId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new { legajo.PersonLegajoId, Created = created }),
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Fiador {FiadorId} of entity {EntityId} linked to legajo {PersonLegajoId} (created: {Created}) tenant {TenantId}",
            fiadorId, entityId, legajo.PersonLegajoId, created, tenantId);

        var body = new FiadorLegajoLinkResponse { PersonLegajoId = legajo.PersonLegajoId, Created = created };
        return created ? StatusCode(StatusCodes.Status201Created, body) : Ok(body);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// A new legajo for a fiador: the CUIT as stored on the fiador (hyphenated, like Entities.CUIT), the
    /// fiador's denominación as its name (empty when LUFE gave none), and an undetermined fiscal regime
    /// until the legajo's refresh-arca reads it. No snapshot is copied: the fiador has none of its own.
    /// </summary>
    private static PersonLegajo NewFiadorLegajo(Fiador fiador, Guid userId)
    {
        var now = DateTime.UtcNow;
        return new PersonLegajo
        {
            PersonLegajoId = Guid.NewGuid(),
            TenantId       = fiador.TenantId,
            CUIT           = fiador.CUIT,
            FullName       = fiador.Denominacion ?? string.Empty,
            FiscalRegime   = FiscalRegimes.Desconocido,
            CreatedBy      = userId,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
    }

    /// <summary>A ProblemDetails carrying an <c>errorCode</c> extension, as PersonLegajosController builds it.</summary>
    private ObjectResult CodedProblem(int status, string title, string errorCode, string detail)
    {
        var result = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = errorCode;
        return result;
    }

    /// <summary>The 400 detail for a CUIT that fails <see cref="TryNormalizeCuit"/>.</summary>
    private const string InvalidCuitDetail =
        "El CUIT no es válido: debe tener 11 dígitos y un dígito verificador correcto.";

    /// <summary>
    /// Maps <see cref="AddFiadorRequest.Origen"/> onto the stored value: absent or "Lufe" → 'Lufe' (the
    /// unchanged path every existing client takes), "Manual" → 'Manual', case-insensitively. Anything
    /// else → null, which the caller answers 400 — there is no CHECK constraint behind Origen (046), so
    /// this is the only thing keeping the column to its two values.
    /// </summary>
    private static string? ResolveOrigen(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return OrigenLufe;

        var trimmed = requested.Trim();
        if (string.Equals(trimmed, OrigenLufe, StringComparison.OrdinalIgnoreCase))
            return OrigenLufe;
        if (string.Equals(trimmed, OrigenManual, StringComparison.OrdinalIgnoreCase))
            return OrigenManual;

        return null;
    }

    /// <summary>
    /// Validates a typed CUIT: hyphens and spaces are stripped, and what remains must be exactly 11 ASCII
    /// digits carrying a valid modulo-11 check digit (<see cref="LegajoCuit.HasValidCheckDigit"/>). Any
    /// other character — a dot, a letter — fails rather than being silently dropped. On success
    /// <paramref name="digits"/> holds the 11 digits; <see cref="LegajoCuit.Format"/> gives the stored form.
    /// </summary>
    private static bool TryNormalizeCuit(string? raw, out string digits)
    {
        digits = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var stripped = new string(raw.Where(ch => ch != '-' && !char.IsWhiteSpace(ch)).ToArray());
        if (!LegajoCuit.HasValidCheckDigit(stripped))
            return false;

        digits = stripped;
        return true;
    }

    /// <summary>Trimmed denominación, required, 2–300 characters (the column is nvarchar(300)).</summary>
    private static (string? Value, string? Error) NormalizeDenominacion(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return (null, "Se requiere la denominación del fiador.");

        return trimmed.Length is < 2 or > 300
            ? (null, "La denominación del fiador debe tener entre 2 y 300 caracteres.")
            : (trimmed, null);
    }

    /// <summary>Trimmed cargo, optional (blank → null), at most 200 characters (nvarchar(200)).</summary>
    private static (string? Value, string? Error) NormalizeCargo(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return (null, null);

        return trimmed.Length > 200
            ? (null, "El cargo del fiador no puede superar los 200 caracteres.")
            : (trimmed, null);
    }

    /// <summary>
    /// Percentage, optional, 0–100 with at most 2 decimals. Refused rather than rounded: the column is
    /// decimal(5,2), and SQL Server would round a third decimal silently into a value nobody typed.
    /// </summary>
    private static string? ValidatePorcAccionista(decimal? value)
    {
        if (value is not { } porc)
            return null;

        if (porc is < 0m or > 100m)
            return "El porcentaje accionario debe estar entre 0 y 100.";

        return decimal.Round(porc, 2) != porc
            ? "El porcentaje accionario admite como máximo 2 decimales."
            : null;
    }

    /// <summary>
    /// "APELLIDO, NOMBRE" from the parts ARCA sent for a persona humana, falling back to its razón social.
    /// A copy of <c>EntitiesController.ComposeHumanaName</c>, which is private to that controller — the
    /// project duplicates such helpers rather than sharing them (see <see cref="FormatCuit"/>).
    /// </summary>
    private static string? ComposeHumanaName(ArcaPersonaData data)
    {
        var parts = new[] { data.Apellido, data.Nombre }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();

        if (parts.Length > 0) return string.Join(", ", parts);

        return string.IsNullOrWhiteSpace(data.RazonSocial) ? null : data.RazonSocial.Trim();
    }

    /// <summary>
    /// The "ARCA" ApiCallLogs row, in the shape <c>EntitiesController.LogArcaCallAsync</c> writes, so the
    /// fiador prefill is visible in the same table as every other ARCA call. The error text is the
    /// exception type only (see <see cref="ArcaLookupAsync"/>).
    /// </summary>
    private Task LogArcaCallAsync(string tenantId, bool isSuccess, int durationMs, string? errorMessage)
    {
        return _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "ARCA",
            endpoint:         "/ws_sr_constancia_inscripcion/getPersona_v2",
            responseStatus:   isSuccess ? 200 : 502,
            inputTokens:      null,
            outputTokens:     null,
            estimatedCostUsd: null,
            isSuccess:        isSuccess,
            errorMessage:     errorMessage,
            durationMs:       durationMs,
            ct:               CancellationToken.None);
    }

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in FiadoresController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    private async Task<(Guid userId, IActionResult? error)> ResolveCurrentUserAsync(
        CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return (Guid.Empty, Unauthorized());

        var user = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, ct)
            .ConfigureAwait(false);

        if (user is null)
            return (Guid.Empty, Unauthorized());

        return (user.UserId, null);
    }

    /// <summary>
    /// Projects the stored LUFE blob's <c>Autoridades[]</c> into candidate DTOs, flagging the ones
    /// already added as Active fiadores.
    ///
    /// <para>
    /// FAULT ISOLATION: a blob that is absent, or that will not parse, yields an EMPTY LIST and a
    /// warning — never a 500. The blob is a cached copy of a third party's payload, and one bad row
    /// must not take the entity's fiadores panel down with it. Same rule as
    /// <c>EntitiesController.ParseSnapshot</c>.
    /// </para>
    ///
    /// <para>
    /// Deduplicated by formatted CUIT: LUFE can list the same person twice under two cargos, and
    /// only one of them could ever be added — a duplicate row would be an add button that 409s.
    /// The first occurrence wins, so the cargo shown is the one LUFE listed first.
    /// </para>
    ///
    /// <para>
    /// Shared by the candidates read and the add path so the set a caller may add from is exactly
    /// the set it was shown. Two separate projections could drift and let the add accept a CUIT the
    /// list never offered.
    /// </para>
    /// </summary>
    private List<FiadorCandidateResponse> BuildCandidates(
        string? lufeJson, IReadOnlyList<Fiador> activeFiadores, Guid entityId)
    {
        Models.Lufe.LufeData? lufe;
        try
        {
            lufe = string.IsNullOrEmpty(lufeJson)
                ? null
                : JsonSerializer.Deserialize<Models.Lufe.LufeData>(lufeJson, SnapshotJsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not parse LufeDataJson for entity {EntityId}. The candidate list will be empty.",
                entityId);
            return [];
        }

        var autoridades = lufe?.Autoridades;
        if (autoridades is null || autoridades.Length == 0)
            return [];

        var alreadyAdded = activeFiadores
            .Select(f => f.CUIT)
            .ToHashSet(StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<FiadorCandidateResponse>(autoridades.Length);

        foreach (var autoridad in autoridades)
        {
            var cuit = FormatCuit(autoridad.Cuit.ToString(CultureInfo.InvariantCulture));
            if (!seen.Add(cuit)) continue;

            candidates.Add(new FiadorCandidateResponse
            {
                Cuit           = cuit,
                Denominacion   = autoridad.Denominacion,
                Cargo          = autoridad.Cargo,
                PorcAccionista = autoridad.PorcAccionista,
                EsAccionista   = autoridad.EsAccionista,
                AlreadyAdded   = alreadyAdded.Contains(cuit),
            });
        }

        return candidates;
    }

    /// <summary>
    /// Shared options for the stored LUFE snapshot parse. One static instance, not one per call:
    /// <c>JsonSerializerOptions</c> is the cache key for System.Text.Json's per-type metadata, so a
    /// fresh one at each call site throws that cache away every time. The two settings match
    /// <c>EntitiesController.SnapshotJsonOpts</c> and <c>AnalysisController.SnapshotJsonOpts</c>
    /// exactly — the blob is written by one and must be readable by all three, including
    /// AllowReadingFromString so numeric fields stored as strings still parse.
    /// </summary>
    private static readonly JsonSerializerOptions SnapshotJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Formats a CUIT into the hyphenated XX-XXXXXXXX-X form both [dbo].[Entities].CUIT and
    /// [dbo].[Fiadores].CUIT store — which is why both columns are nvarchar(13).
    ///
    /// <para>
    /// A copy of <c>EntitiesController.FormatCuit</c>, which is private to that controller; the
    /// project duplicates such helpers rather than sharing them (<c>ValidationProblem</c> likewise
    /// exists three times). Behaviour must not drift: a value that is not 11 digits is returned
    /// unchanged, so junk degrades into a harmless non-matching string rather than throwing.
    /// </para>
    ///
    /// <para>
    /// Callers pass LUFE's <c>Autoridades[].cuit</c>, which arrives as a JSON number and is a
    /// <c>long</c> on the model. A long cannot carry a leading zero, which is harmless here: every
    /// Argentine CUIT prefix is 20/23/24/27/30/33/34.
    /// </para>
    /// </summary>
    private static string FormatCuit(string cuit)
    {
        cuit = cuit.Replace("-", "").Trim();
        return cuit.Length == 11
            ? $"{cuit[..2]}-{cuit.Substring(2, 8)}-{cuit[10]}"
            : cuit;
    }

    /// <summary>
    /// Maps stored fiadores to their wire model, with the edit markers DDL 074 added.
    ///
    /// <para>
    /// The LUFE comparison uses <see cref="BuildCandidates"/> — the very projection the candidate list and
    /// the LUFE add path use (deduplicated by CUIT, first occurrence wins) — so "what LUFE says about this
    /// CUIT" means the same thing everywhere. The blob is parsed only when a 'Lufe' fiador is present, and
    /// a blob that will not parse degrades to "LUFE says nothing" (no lufeOriginal), never a 500.
    /// </para>
    ///
    /// <para>
    /// Editor names come from one <see cref="IUserRepository.GetByIdsAsync"/> call, skipped entirely when
    /// no fiador has been edited — the common case — so an untouched list costs no extra query.
    /// </para>
    /// </summary>
    private async Task<List<FiadorResponse>> MapFiadoresAsync(
        IReadOnlyList<Fiador> fiadores, string? lufeJson, Guid entityId, CancellationToken ct)
    {
        if (fiadores.Count == 0)
            return [];

        IReadOnlyDictionary<string, FiadorCandidateResponse> lufeByCuit =
            fiadores.Any(IsLufeOrigin)
                ? BuildCandidates(lufeJson, [], entityId).ToDictionary(c => c.Cuit, StringComparer.Ordinal)
                : new Dictionary<string, FiadorCandidateResponse>(StringComparer.Ordinal);

        var editorIds = fiadores
            .Where(f => f.UpdatedByUserId is not null)
            .Select(f => f.UpdatedByUserId!.Value)
            .Distinct()
            .ToList();

        IReadOnlyDictionary<Guid, string> editorNames = new Dictionary<Guid, string>();
        if (editorIds.Count > 0)
        {
            var editors = await _userRepository
                .GetByIdsAsync(editorIds, ct)
                .ConfigureAwait(false);
            editorNames = editors.ToDictionary(u => u.UserId, u => u.DisplayName);
        }

        return fiadores.Select(f => MapFiador(f, lufeByCuit, editorNames)).ToList();
    }

    private static bool IsLufeOrigin(Fiador f) =>
        string.Equals(f.Origen, OrigenLufe, StringComparison.Ordinal);

    /// <summary>
    /// True when a 'Lufe' fiador's stored fields disagree with LUFE's current entry for its CUIT. Text is
    /// compared trimmed and ordinal (blank equals null); the percentage at 2 decimals, the precision of the
    /// decimal(5,2) column, so a LUFE value with more decimals is not a false "edited".
    /// </summary>
    private static bool DiffersFromLufe(Fiador f, FiadorCandidateResponse lufe) =>
        !string.Equals(NormalizeText(f.Denominacion), NormalizeText(lufe.Denominacion), StringComparison.Ordinal)
        || !string.Equals(NormalizeText(f.Cargo), NormalizeText(lufe.Cargo), StringComparison.Ordinal)
        || RoundPorc(f.PorcAccionista) != RoundPorc(lufe.PorcAccionista);

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? RoundPorc(decimal? value) =>
        value is { } porc ? decimal.Round(porc, 2) : null;

    /// <summary>
    /// Maps a stored fiador to its wire model, parsing the NOSIS snapshot when one exists.
    /// <see cref="FiadorResponse.Nosis"/> is null until the fiador has been refreshed at least once
    /// (POST {fiadorId}/refresh-nosis), so a null here is "never fetched", never "nothing on file" —
    /// that distinction lives inside the blob, in <see cref="NosisData.Estado"/>. The parse is a
    /// defensive read: an unparseable snapshot degrades to null rather than failing the request.
    ///
    /// <para>
    /// <see cref="FiadorResponse.LufeOriginal"/> is set only for a 'Lufe' fiador whose CUIT LUFE still
    /// lists; <see cref="FiadorResponse.Editado"/> is true once the fiador has been edited, or while such a
    /// fiador disagrees with that entry. <see cref="FiadorResponse.UpdatedByName"/> falls back to
    /// "Usuario" for an editor id that no longer resolves — the CreditLinesController convention.
    /// </para>
    /// </summary>
    private FiadorResponse MapFiador(
        Fiador f,
        IReadOnlyDictionary<string, FiadorCandidateResponse> lufeByCuit,
        IReadOnlyDictionary<Guid, string> editorNames)
    {
        Models.Nosis.NosisData? nosis = null;
        if (!string.IsNullOrEmpty(f.NosisDataJson))
        {
            try
            {
                nosis = JsonSerializer.Deserialize<Models.Nosis.NosisData>(f.NosisDataJson, SnapshotJsonOpts);
            }
            catch (Exception ex)
            {
                // The snapshot contents are never logged: NosisData carries PII.
                _logger.LogWarning(ex,
                    "Could not parse NosisDataJson for fiador {FiadorId}. The snapshot will be null.",
                    f.FiadorId);
            }
        }

        var lufe = IsLufeOrigin(f) && lufeByCuit.TryGetValue(f.CUIT, out var entry) ? entry : null;

        string? updatedByName = null;
        if (f.UpdatedByUserId is { } editorId)
        {
            updatedByName = editorNames.TryGetValue(editorId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : "Usuario";
        }

        return new FiadorResponse
        {
            FiadorId       = f.FiadorId,
            EntityId       = f.EntityId,
            PersonLegajoId = f.PersonLegajoId,
            CUIT           = f.CUIT,
            Denominacion   = f.Denominacion,
            Cargo          = f.Cargo,
            PorcAccionista = f.PorcAccionista,
            Origen         = f.Origen,
            Editado        = f.UpdatedAt is not null || (lufe is not null && DiffersFromLufe(f, lufe)),
            LufeOriginal   = lufe is null
                ? null
                : new FiadorLufeOriginalResponse
                {
                    Denominacion   = lufe.Denominacion,
                    Cargo          = lufe.Cargo,
                    PorcAccionista = lufe.PorcAccionista,
                },
            UpdatedAt      = f.UpdatedAt,
            UpdatedByName  = updatedByName,
            Status         = f.Status,
            CreatedAt      = f.CreatedAt,
            Nosis          = nosis,
            NosisFetchedAt = f.NosisFetchedAt,
        };
    }
}
