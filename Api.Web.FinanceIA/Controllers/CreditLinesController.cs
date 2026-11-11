// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// CRUD over an entity's credit lines (cupos) and their operations.
/// Tenant-isolated. Exactly one Active cupo exists per (Entity, Currency); creating
/// a second Active one for the same currency is a conflict (no auto-supersede here).
/// Operations are born 'Aprobada' and reach 'Vigente' only through confirmar-vigencia;
/// both reserve cupo. Operation 'Vencida' status is derived at read time and never stored.
/// </summary>
[ApiController]
[Route("api/credit-lines")]
[Route("api/bff/credit-lines")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class CreditLinesController : ControllerBase
{
    private readonly ITenantContextAccessor            _tenantContextAccessor;
    private readonly ICreditLineRepository             _creditLineRepository;
    private readonly ICreditLineOperationRepository    _operationRepository;
    private readonly IEntityRepository                 _entityRepository;
    private readonly IAnalysisRepository               _analysisRepository;
    private readonly IAnalysisResultRepository         _analysisResultRepository;
    private readonly IUserRepository                   _userRepository;
    private readonly IAuditService                     _auditService;
    private readonly ILogger<CreditLinesController>    _logger;

    /// <summary>Formats the decision amount quoted back in a 400, the way the Portal shows money.</summary>
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    public CreditLinesController(
        ITenantContextAccessor         tenantContextAccessor,
        ICreditLineRepository          creditLineRepository,
        ICreditLineOperationRepository operationRepository,
        IEntityRepository              entityRepository,
        IAnalysisRepository            analysisRepository,
        IAnalysisResultRepository      analysisResultRepository,
        IUserRepository                userRepository,
        IAuditService                  auditService,
        ILogger<CreditLinesController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _creditLineRepository     = creditLineRepository;
        _operationRepository      = operationRepository;
        _entityRepository         = entityRepository;
        _analysisRepository       = analysisRepository;
        _analysisResultRepository = analysisResultRepository;
        _userRepository           = userRepository;
        _auditService             = auditService;
        _logger                   = logger;
    }

    // Supported ISO 4217 currency codes now live in Clases/Currencies.cs: an approval limit and a
    // decision amount carry a currency too, and three private copies of the same three strings is
    // how they drift apart. Behaviour is unchanged — same three codes, same ordinal comparison.

    // The SGR One Click credit-line type catalog now lives in Clases/CreditLineTypes.cs, for the
    // same reason Currencies moved: a decision's proposed and confirmed operations name a type too.
    // Behaviour is unchanged — same eighteen codes, code 0 still accepted, still no DB CHECK.

    // ── GET /api/credit-lines?entityId={guid} ──────────────────────────────────

    /// <summary>
    /// Returns the full credit-line structure for an entity: every non-terminal cupo
    /// ('Active' or 'Suspended', so a suspended cupo stays reachable for reactivation) with its
    /// operations and derived Utilizado/Disponible/OperationsCount. Active cupos are listed first.
    /// </summary>
    /// <response code="200">The entity's credit-line structure.</response>
    /// <response code="404">Entity not found for this tenant.</response>
    [HttpGet]
    [Authorize(Roles = Roles.CreditLineReaders)]
    [ProducesResponseType(typeof(CreditLineStructureResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetStructureAsync(
        [FromQuery] Guid entityId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // Existence only — nothing below reads a field off the entity, so this must not be a
        // full-row load dragging five nvarchar(max) snapshots off disk. See IEntityRepository.
        var exists = await _entityRepository
            .ExistsByIdAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var creditLines = await _creditLineRepository
            .GetNonTerminalByEntityAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Resolve OriginAnalysisId -> AnalysisResult.ResultId in ONE batched query (the detail route
        // is keyed by ResultId, so a back-link built from OriginAnalysisId alone would 404). Keyed on
        // the distinct non-null origins of the cupos being returned — no per-row lookup.
        var originAnalysisIds = creditLines
            .Where(c => c.OriginAnalysisId.HasValue)
            .Select(c => c.OriginAnalysisId!.Value)
            .Distinct()
            .ToList();
        var resultIdByAnalysis = await _analysisResultRepository
            .GetResultIdsByAnalysisIdsAsync(originAnalysisIds, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Gather every cupo with its operations FIRST, so the creator-name lookup below can be a
        // single batched query over the whole response rather than one per cupo or per operation.
        var loaded = new List<(CreditLine Line, IReadOnlyList<CreditLineOperation> Operations, Guid? OriginResultId)>(
            creditLines.Count);
        foreach (var line in creditLines)
        {
            var operations = await _operationRepository
                .GetByCreditLineAsync(line.CreditLineId, tenantId, cancellationToken)
                .ConfigureAwait(false);

            Guid? originAnalysisResultId =
                line.OriginAnalysisId.HasValue
                && resultIdByAnalysis.TryGetValue(line.OriginAnalysisId.Value, out var rid)
                    ? rid
                    : null;

            loaded.Add((line, operations, originAnalysisResultId));
        }

        // One lookup for the authors of every cupo AND every operation being returned. Authorship
        // is what tells the client which rows it may offer to mutate, so it rides the read model.
        var creatorNames = await ResolveCreatorNamesAsync(
            loaded.Select(x => x.Line.CreatedByUserId)
                  .Concat(loaded.SelectMany(x => x.Operations).Select(o => o.CreatedByUserId)),
            cancellationToken)
            .ConfigureAwait(false);

        var responses = loaded
            .Select(x => MapCreditLine(x.Line, x.Operations, creatorNames, x.OriginResultId))
            .ToList();

        _logger.LogInformation(
            "Listed credit-line structure for entity {EntityId} tenant {TenantId}: {Count} non-terminal cupos",
            entityId, tenantId, responses.Count);

        return Ok(new CreditLineStructureResponse { CreditLines = responses });
    }

    // ── POST /api/credit-lines/entity/{entityId} ───────────────────────────────

    /// <summary>
    /// Creates the Active cupo for an entity in a currency. Requires the approved analysis
    /// (same tenant, same entity) that justifies the cupo. Fails with 409 when an Active
    /// cupo already exists for that (entity, currency).
    /// </summary>
    /// <response code="201">Cupo created.</response>
    /// <response code="400">Invalid currency, non-positive amount, missing analysis, analysis for another entity, analysis not Approved, or the amount/currency not covered by the analysis' decision (incl. a decision with no recorded amount).</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Entity or analysis not found for this tenant.</response>
    /// <response code="409">An Active cupo already exists for this entity and currency.</response>
    [HttpPost("entity/{entityId:guid}")]
    [Authorize(Roles = Roles.CreditLineBuilders)]
    [ProducesResponseType(typeof(CreditLineResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> CreateCreditLineAsync(
        Guid entityId,
        [FromBody] CreateCreditLineRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, _, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var entity = await _entityRepository
            .GetByIdAsync(entityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la entidad.");

        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!Currencies.Known.Contains(currency))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"La moneda '{request.Currency}' no es un código ISO 4217 admitido.");

        if (request.Amount <= 0m)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El importe debe ser mayor a cero.");

        // The cupo must record the approved analysis that justifies it — a cupo with no
        // recorded justification is the audit gap this endpoint closes. These checks run
        // before the duplicate-active-cupo 409: for a request with a valid analysis they
        // all pass and the outcome is unchanged, so "no/invalid justification" (especially
        // an analysis for another entity) wins over "a cupo already exists".
        if (request.AnalysisId == Guid.Empty)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Se requiere el análisis aprobado que justifica el cupo.");

        var analysis = await _analysisRepository
            .GetByIdAsync(request.AnalysisId, cancellationToken)
            .ConfigureAwait(false);
        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el análisis.");

        if (analysis.EntityId != entityId)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El análisis indicado no corresponde a la entidad para la que se está creando el cupo.");

        if (!string.Equals(analysis.Status, "Approved", StringComparison.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Solo se puede crear un cupo a partir de un análisis aprobado.");

        // The cupo can be no larger than what the decision actually committed, in its currency —
        // otherwise any approved analysis justifies any figure and the approval limits mean nothing.
        var decisionProblem = DecisionBoundProblem(analysis, request.Amount, currency);
        if (decisionProblem is not null) return decisionProblem;

        // A Suspended cupo still reserves the (entity, currency) slot: it can carry live 'Vigente'
        // operations, so a parallel Active cupo would make utilizado/disponible ambiguous. The
        // filtered index only guards Active rows, so this broader non-terminal check is enforced here.
        var existing = await _creditLineRepository
            .GetBlockingByEntityCurrencyAsync(entityId, currency, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return Problem(statusCode: 409, title: "Conflict",
                detail: string.Equals(existing.Status, "Suspended", StringComparison.Ordinal)
                    ? $"Ya existe un cupo suspendido en {currency} para esta entidad. Reactívelo en lugar de crear uno nuevo."
                    : $"Ya existe un cupo activo en {currency} para esta entidad.");

        var creditLine = new CreditLine
        {
            TenantId         = tenantId,
            EntityId         = entityId,
            Amount           = request.Amount,
            Currency         = currency,
            Status           = "Active",
            CreatedByUserId  = userId,
            CreatedAt        = DateTime.UtcNow,
            OriginAnalysisId = request.AnalysisId,
        };

        await _creditLineRepository.CreateAsync(creditLine, cancellationToken).ConfigureAwait(false);

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     userId,
            action:     "CreditLine.Created",
            severity:   "Info",
            entityType: "CreditLine",
            entityId:   creditLine.CreditLineId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Created cupo {CreditLineId} for entity {EntityId} currency {Currency} analysis {AnalysisId} tenant {TenantId}",
            creditLine.CreditLineId, entityId, currency, request.AnalysisId, tenantId);

        // The cupo was just created by the caller, so its only author is the caller.
        var creatorNames = await ResolveCreatorNamesAsync([userId], cancellationToken)
            .ConfigureAwait(false);

        return StatusCode(StatusCodes.Status201Created,
            MapCreditLine(creditLine, Array.Empty<CreditLineOperation>(), creatorNames));
    }

    // ── POST /api/credit-lines/{creditLineId}/raise ────────────────────────────

    /// <summary>
    /// Raises (amplía) an Active cupo's amount in place. The new amount must be strictly
    /// greater than the current amount and be backed by an Approved analysis created after
    /// the cupo's last raise (or its creation, if never raised). Operations are untouched;
    /// this is an in-place update of the same cupo, never a supersede.
    /// </summary>
    /// <response code="200">Cupo raised; returns the updated structure.</response>
    /// <response code="400">New amount not greater than current, analysis for another entity, analysis not approved, or the new amount/currency not covered by the analysis' decision (incl. a decision with no recorded amount).</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">The caller is neither the cupo's creator nor an Admin or RiskManager.</response>
    /// <response code="404">Cupo or analysis not found for this tenant.</response>
    /// <response code="409">Cupo not Active, stale analysis, or the raise would leave the cupo underwater.</response>
    [HttpPost("{creditLineId:guid}/raise")]
    [Authorize(Roles = Roles.CreditLineManagers)]
    [ProducesResponseType(typeof(CreditLineResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> RaiseCreditLineAsync(
        Guid creditLineId,
        [FromBody] RaiseCreditLineRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, role, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var creditLine = await _creditLineRepository
            .GetByIdAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (creditLine is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el cupo.");

        // Checked before the status guard: whether the caller may touch this cupo at all is a fact
        // about the caller, independent of the cupo's state, and answering 409 first would tell a
        // non-owner what state a cupo they have no business in is in.
        if (!CanMutate(creditLine.CreatedByUserId, userId, role))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No tenés permiso para modificar este cupo.");

        if (!string.Equals(creditLine.Status, "Active", StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict", detail: "El cupo no está activo.");

        // Raising only: a lower or equal amount is out of scope — reductions have different
        // semantics because existing operations may already exceed the smaller figure.
        if (request.NewAmount <= creditLine.Amount)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El nuevo importe debe ser mayor al importe actual del cupo.");

        var analysis = await _analysisRepository
            .GetByIdAsync(request.AnalysisId, cancellationToken)
            .ConfigureAwait(false);
        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el análisis.");

        // Same rule CreateCreditLineAsync applies: an analysis of another company justifies nothing
        // about this cupo, however approved it is.
        if (analysis.EntityId != creditLine.EntityId)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El análisis indicado no corresponde a la entidad del cupo.");

        if (!string.Equals(analysis.Status, "Approved", StringComparison.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Solo se puede ampliar el cupo a partir de un análisis aprobado.");

        // The NEW tope, not the increment, is what the decision has to cover: the Portal records the
        // full new tope as the decision amount when it raises from the approval modal.
        var decisionProblem = DecisionBoundProblem(analysis, request.NewAmount, creditLine.Currency);
        if (decisionProblem is not null) return decisionProblem;

        // Core rule: the tope is the scoring engine's output, so it cannot be raised without a
        // NEWER approved analysis. The baseline is the last raise, or creation if never raised,
        // so the same analysis can never justify two raises.
        var freshnessBaseline = creditLine.LastRaisedAt ?? creditLine.CreatedAt;
        if (analysis.CreatedAt <= freshnessBaseline)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El análisis debe ser posterior a la última ampliación o creación del cupo. Se requiere un análisis más reciente para ampliar el tope.");

        // Raising can never leave the cupo underwater: the new amount must still cover the sum
        // of the cupo's cupo-occupying operations (same predicate as CreateOperationAsync).
        var operations = await _operationRepository
            .GetByCreditLineAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var utilizado = operations.Where(CreditLineRules.OccupiesCupo).Sum(o => o.Amount);
        if (request.NewAmount < utilizado)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El nuevo importe no puede ser menor a la suma de las operaciones del cupo que reservan saldo (aprobadas y vigentes).");

        var oldAmount = creditLine.Amount;

        // Pass the baseline we validated the freshness rule against; the repository's conditional
        // UPDATE only applies if LastRaisedAt is still this value, so a concurrent raise cannot
        // be silently overwritten.
        var updated = await _creditLineRepository
            .RaiseAsync(creditLineId, tenantId, request.NewAmount, request.AnalysisId,
                        creditLine.LastRaisedAt, cancellationToken)
            .ConfigureAwait(false);
        // Null means the cupo is no longer Active, or another request raised it first (its
        // LastRaisedAt no longer matches the baseline we validated against) — a lost update we
        // refuse rather than silently overwrite.
        if (updated is null)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El cupo ya no está activo o fue modificado por otra operación. El estado actual debe verificarse antes de reintentar la ampliación.");

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "CreditLine.Raised",
            severity:     "Info",
            entityType:   "CreditLine",
            entityId:     updated.CreditLineId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new { Amount = oldAmount }),
            newValueJson: JsonSerializer.Serialize(new
            {
                Amount     = updated.Amount,
                AnalysisId = request.AnalysisId,
                LastRaisedAt = updated.LastRaisedAt,
            }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Raised cupo {CreditLineId} from {OldAmount} to {NewAmount} analysis {AnalysisId} tenant {TenantId}",
            updated.CreditLineId, oldAmount, updated.Amount, request.AnalysisId, tenantId);

        var creatorNames = await ResolveCreatorNamesAsync(
            operations.Select(o => o.CreatedByUserId).Append(updated.CreatedByUserId),
            cancellationToken)
            .ConfigureAwait(false);

        return Ok(MapCreditLine(updated, operations, creatorNames));
    }

    // ── POST /api/credit-lines/{creditLineId}/suspend ──────────────────────────

    /// <summary>
    /// Suspends an Active cupo: sets its status to 'Suspended' to stop new lending. Nothing else
    /// changes — the amount, origin analysis, and existing operations (and their statuses) are left
    /// exactly as they are. Suspension does not undo anything; a suspended cupo still reserves the
    /// entity+currency slot and can carry live 'Vigente' operations.
    /// </summary>
    /// <response code="200">Cupo suspended; returns the updated structure.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">The caller is neither the cupo's creator nor an Admin or RiskManager.</response>
    /// <response code="404">Cupo not found for this tenant.</response>
    /// <response code="409">Cupo is not currently Active.</response>
    [HttpPost("{creditLineId:guid}/suspend")]
    [Authorize(Roles = Roles.CreditLineManagers)]
    [ProducesResponseType(typeof(CreditLineResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> SuspendCreditLineAsync(
        Guid creditLineId,
        [FromBody] SuspendCreditLineRequest? request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, role, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var creditLine = await _creditLineRepository
            .GetByIdAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (creditLine is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el cupo.");

        if (!CanMutate(creditLine.CreatedByUserId, userId, role))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No tenés permiso para modificar este cupo.");

        if (!string.Equals(creditLine.Status, "Active", StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict", detail: "Solo se puede suspender un cupo activo.");

        var applied = await _creditLineRepository
            .SetStatusAsync(creditLineId, tenantId, expectedStatus: "Active", newStatus: "Suspended", cancellationToken)
            .ConfigureAwait(false);
        // False means the cupo left 'Active' between our read and write (a concurrent transition) —
        // refuse rather than silently overwrite.
        if (!applied)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El cupo fue modificado por otra operación. Verifique el estado actual antes de reintentar.");

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "CreditLine.Suspended",
            severity:     "Info",
            entityType:   "CreditLine",
            entityId:     creditLine.CreditLineId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new { Status = "Active" }),
            newValueJson: JsonSerializer.Serialize(new { Status = "Suspended", Reason = request?.Reason }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Suspended cupo {CreditLineId} tenant {TenantId}",
            creditLine.CreditLineId, tenantId);

        // Reflect the persisted transition on the loaded entity for the response mapping. Operations
        // are read only to build utilizado/disponible — they are not touched.
        creditLine.Status = "Suspended";
        var operations = await _operationRepository
            .GetByCreditLineAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var creatorNames = await ResolveCreatorNamesAsync(
            operations.Select(o => o.CreatedByUserId).Append(creditLine.CreatedByUserId),
            cancellationToken)
            .ConfigureAwait(false);
        return Ok(MapCreditLine(creditLine, operations, creatorNames));
    }

    // ── POST /api/credit-lines/{creditLineId}/reactivate ───────────────────────

    /// <summary>
    /// Reactivates a Suspended cupo: sets its status back to 'Active'. Rejected when another Active
    /// cupo already exists for the same (entity, currency) — one entity may not hold two simultaneous
    /// credit lines in the same currency.
    /// </summary>
    /// <response code="200">Cupo reactivated; returns the updated structure.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">The caller is neither the cupo's creator nor an Admin or RiskManager.</response>
    /// <response code="404">Cupo not found for this tenant.</response>
    /// <response code="409">Cupo is not currently Suspended, or another Active cupo exists for this entity+currency.</response>
    [HttpPost("{creditLineId:guid}/reactivate")]
    [Authorize(Roles = Roles.CreditLineManagers)]
    [ProducesResponseType(typeof(CreditLineResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> ReactivateCreditLineAsync(
        Guid creditLineId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, role, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var creditLine = await _creditLineRepository
            .GetByIdAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (creditLine is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el cupo.");

        if (!CanMutate(creditLine.CreatedByUserId, userId, role))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No tenés permiso para modificar este cupo.");

        if (!string.Equals(creditLine.Status, "Suspended", StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict", detail: "Solo se puede reactivar un cupo suspendido.");

        // The (entity, currency) slot must be free of any OTHER Active cupo. Since this cupo is
        // Suspended, an Active-only lookup can only return a different, conflicting line.
        var activeConflict = await _creditLineRepository
            .GetActiveByEntityCurrencyAsync(creditLine.EntityId, creditLine.Currency, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (activeConflict is not null)
            return Problem(statusCode: 409, title: "Conflict",
                detail: $"Ya existe un cupo activo en {creditLine.Currency} para esta entidad; no se puede reactivar mientras exista otro cupo activo.");

        var applied = await _creditLineRepository
            .SetStatusAsync(creditLineId, tenantId, expectedStatus: "Suspended", newStatus: "Active", cancellationToken)
            .ConfigureAwait(false);
        if (!applied)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El cupo fue modificado por otra operación. Verifique el estado actual antes de reintentar.");

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "CreditLine.Reactivated",
            severity:     "Info",
            entityType:   "CreditLine",
            entityId:     creditLine.CreditLineId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new { Status = "Suspended" }),
            newValueJson: JsonSerializer.Serialize(new { Status = "Active" }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Reactivated cupo {CreditLineId} tenant {TenantId}",
            creditLine.CreditLineId, tenantId);

        creditLine.Status = "Active";
        var operations = await _operationRepository
            .GetByCreditLineAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var creatorNames = await ResolveCreatorNamesAsync(
            operations.Select(o => o.CreatedByUserId).Append(creditLine.CreatedByUserId),
            cancellationToken)
            .ConfigureAwait(false);
        return Ok(MapCreditLine(creditLine, operations, creatorNames));
    }

    // ── POST /api/credit-lines/{creditLineId}/operations ───────────────────────

    /// <summary>
    /// Creates an operation under an Active cupo, tied to an approved analysis. The operation is
    /// born 'Aprobada' — granted and committed, but not in force until an analyst confirms it via
    /// confirmar-vigencia. It reserves cupo from creation: the sum of the cupo's cupo-occupying
    /// operations (including the new one) must not exceed the cupo amount.
    /// </summary>
    /// <response code="201">Operation created, with status 'Aprobada'.</response>
    /// <response code="400">Invalid currency/type/amount, analysis for another entity, analysis not approved, or sum exceeds cupo.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Cupo or analysis not found for this tenant.</response>
    /// <response code="409">The cupo is not Active.</response>
    [HttpPost("{creditLineId:guid}/operations")]
    [Authorize(Roles = Roles.CreditLineBuilders)]
    [ProducesResponseType(typeof(OperationResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> CreateOperationAsync(
        Guid creditLineId,
        [FromBody] CreateOperationRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // No ownership check here, deliberately: this is a creation, so there is no owner yet to
        // compare against. The cupo's own creator is NOT required to match — an analyst may add an
        // operation under a colleague's cupo, and only mutating that operation afterwards is gated.
        var (userId, _, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var creditLine = await _creditLineRepository
            .GetByIdAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (creditLine is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el cupo.");

        if (!string.Equals(creditLine.Status, "Active", StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict",
                detail: "El cupo no está activo.");

        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.Equals(currency, creditLine.Currency, StringComparison.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"La moneda de la operación '{request.Currency}' debe coincidir con la del cupo '{creditLine.Currency}'.");

        if (!CreditLineTypes.IsKnown(request.Type))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"El tipo '{request.Type}' no es un tipo de línea SGR válido.");

        if (request.Amount <= 0m)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El importe debe ser mayor a cero.");

        var analysis = await _analysisRepository
            .GetByIdAsync(request.AnalysisId, cancellationToken)
            .ConfigureAwait(false);
        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el análisis.");

        if (analysis.EntityId != creditLine.EntityId)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El análisis indicado no corresponde a la entidad del cupo.");

        if (!string.Equals(analysis.Status, "Approved", StringComparison.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Solo se puede crear una operación a partir de un análisis aprobado.");

        // Sum rule: existing cupo-occupying operations + the new one must fit within the cupo. An
        // 'Aprobada' operation already reserves its amount, so a second one that would exceed the
        // tope is refused even though nobody has confirmed the first in force yet.
        var operations = await _operationRepository
            .GetByCreditLineAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var utilizado = operations.Where(CreditLineRules.OccupiesCupo).Sum(o => o.Amount);
        if (utilizado + request.Amount > creditLine.Amount)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "La suma de operaciones supera el cupo disponible.");

        var operation = new CreditLineOperation
        {
            TenantId        = tenantId,
            CreditLineId    = creditLineId,
            AnalysisId      = request.AnalysisId,
            Type            = request.Type,
            Amount          = request.Amount,
            Currency        = currency,
            ValidUntil      = request.ValidUntil,
            Notes           = request.Notes,
            // Condition flags: recorded as given, false when the caller omits them. They are pure
            // metadata — deliberately NOT consulted by OccupiesCupo, by the sum rule above, or by
            // anything that decides a status.
            ASolaFirma                 = request.ASolaFirma,
            RequiereContragarantiaReal = request.RequiereContragarantiaReal,
            // Born 'Aprobada', never 'Vigente': granting the operation and confirming it is actually
            // in force are two separate acts, and only an analyst performs the second.
            Status          = "Aprobada",
            CreatedByUserId = userId,
            CreatedAt       = DateTime.UtcNow,
        };

        await _operationRepository.CreateAsync(operation, cancellationToken).ConfigureAwait(false);

        _ = _auditService.LogAsync(
            tenantId:   tenantId,
            userId:     userId,
            action:     "CreditLineOperation.Created",
            severity:   "Info",
            entityType: "CreditLineOperation",
            entityId:   operation.OperationId.ToString(),
            ipAddress:  HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:  HttpContext.TraceIdentifier,
            ct:         CancellationToken.None);

        _logger.LogInformation(
            "Created operation {OperationId} under cupo {CreditLineId} analysis {AnalysisId} tenant {TenantId}",
            operation.OperationId, creditLineId, request.AnalysisId, tenantId);

        // The operation was just created by the caller, so its only author is the caller.
        var creatorNames = await ResolveCreatorNamesAsync([userId], cancellationToken)
            .ConfigureAwait(false);

        return StatusCode(StatusCodes.Status201Created, MapOperation(operation, creatorNames));
    }

    // ── POST /api/credit-lines/{id}/operations/{operationId}/confirmar-vigencia ─

    /// <summary>
    /// Confirms that an 'Aprobada' operation is actually in force, moving it to 'Vigente' and
    /// recording who confirmed it and when. This does NOT change the cupo's utilizado — an
    /// 'Aprobada' operation already reserved its amount at creation; the confirmation records a
    /// fact about the world, it does not grant anything further.
    /// </summary>
    /// <response code="200">Vigencia confirmed; returns the cupo with its refreshed figures.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">The caller is neither the operation's creator nor an Admin or RiskManager.</response>
    /// <response code="404">Cupo or operation not found for this tenant, or the operation belongs to another cupo.</response>
    /// <response code="409">The operation is not 'Aprobada'.</response>
    [HttpPost("{creditLineId:guid}/operations/{operationId:guid}/confirmar-vigencia")]
    [Authorize(Roles = Roles.CreditLineManagers)]
    [ProducesResponseType(typeof(CreditLineResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> ConfirmOperationVigenciaAsync(
        Guid creditLineId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, role, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var creditLine = await _creditLineRepository
            .GetByIdAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (creditLine is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el cupo.");

        var operation = await _operationRepository
            .GetByIdAsync(operationId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        // Also 404 when the operation exists but hangs off a different cupo: the route asserts a
        // parent, and honouring a mismatched one would let a caller confirm an operation through a
        // cupo they were not looking at.
        if (operation is null || operation.CreditLineId != creditLineId)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la operación.");

        // The OPERATION's creator, not the cupo's: the cupo read above is a 404 guard only, and an
        // analyst may legitimately hold an operation under a colleague's cupo.
        if (!CanMutate(operation.CreatedByUserId, userId, role))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No tenés permiso para modificar esta operación.");

        // Deliberately NOT idempotent from 'Vigente': re-confirming would overwrite a real
        // confirmation timestamp with a later, false one. Cancelled and already-vigente operations
        // are conflicts, not no-ops.
        if (!string.Equals(operation.Status, "Aprobada", StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict",
                detail: "Solo se puede confirmar la vigencia de una operación aprobada.");

        // Stamped once and reused for both the write and the response, so the timestamp the client
        // is shown is the one actually persisted.
        var confirmedAt = DateTime.UtcNow;

        // No cupo-status guard on purpose: confirming lends nothing further (the reservation
        // happened at creation and utilizado does not move), so requiring an Active cupo would
        // strand an 'Aprobada' operation under a suspended cupo with no exit but cancellation.
        var applied = await _operationRepository
            .ConfirmVigenciaAsync(operationId, tenantId, userId, confirmedAt, cancellationToken)
            .ConfigureAwait(false);
        // False means the operation left 'Aprobada' between our read and write (a concurrent
        // transition) — refuse rather than silently overwrite.
        if (!applied)
            return Problem(statusCode: 409, title: "Conflict",
                detail: "La operación fue modificada por otra operación. Verifique el estado actual antes de reintentar.");

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "CreditLineOperation.VigenciaConfirmed",
            severity:     "Info",
            entityType:   "CreditLineOperation",
            entityId:     operationId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new { Status = "Aprobada" }),
            newValueJson: JsonSerializer.Serialize(new { Status = "Vigente" }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Confirmed vigencia of operation {OperationId} under cupo {CreditLineId} tenant {TenantId}",
            operationId, creditLineId, tenantId);

        // Re-read so the response carries every sibling operation, then reflect the persisted
        // transition on this one — the read is AsNoTracking, so it still shows the pre-write row.
        var operations = await _operationRepository
            .GetByCreditLineAsync(creditLineId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var confirmed = operations.FirstOrDefault(o => o.OperationId == operationId);
        if (confirmed is not null)
        {
            confirmed.Status               = "Vigente";
            confirmed.VigenteConfirmadaEl  = confirmedAt;
            confirmed.VigenteConfirmadaPor = userId;
        }

        var creatorNames = await ResolveCreatorNamesAsync(
            operations.Select(o => o.CreatedByUserId).Append(creditLine.CreatedByUserId),
            cancellationToken)
            .ConfigureAwait(false);

        return Ok(MapCreditLine(creditLine, operations, creatorNames));
    }

    // ── DELETE /api/credit-lines/operations/{operationId} ──────────────────────

    /// <summary>
    /// Cancels (soft) an operation, recording who cancelled it and when on the row, and which
    /// transition it was — 'Aprobada' → 'Cancelada' is a deal that fell through ("se cayó"),
    /// 'Vigente' → 'Cancelada' is a close — in the audit trail. Idempotent: an already-cancelled
    /// operation returns 204 and writes nothing, so a real cancellation stamp is never overwritten.
    /// </summary>
    /// <response code="204">Operation cancelled (or already cancelled).</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">The caller is neither the operation's creator nor an Admin or RiskManager.</response>
    /// <response code="404">Operation not found for this tenant.</response>
    [HttpDelete("operations/{operationId:guid}")]
    [Authorize(Roles = Roles.CreditLineManagers)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> CancelOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, role, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        // This read is load-bearing three times over, not a convenience: it is the only thing that
        // tells a genuinely missing operation (404) from one someone else already cancelled (204),
        // it carries the creator the ownership check needs, and it is where the status the audit
        // entry has to name comes from. Do not drop it in favour of letting the conditional UPDATE
        // decide — every miss would collapse into a silent 204.
        var operation = await _operationRepository
            .GetByIdAsync(operationId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (operation is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la operación.");

        // BEFORE the idempotent 204 below, deliberately: a caller who may not cancel this operation
        // must never be handed a 204 implying that they did.
        if (!CanMutate(operation.CreatedByUserId, userId, role))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No tenés permiso para modificar esta operación.");

        // Already cancelled: idempotent no-op, no audit entry. Re-cancelling would overwrite a real
        // cancellation stamp with a later, false one — the same reason confirmar-vigencia refuses.
        if (string.Equals(operation.Status, "Cancelada", StringComparison.Ordinal))
            return NoContent();

        // Stamped once and reused across the retry below, so losing a race does not push the recorded
        // cancellation later than the moment the caller actually asked for it.
        var cancelledAt = DateTime.UtcNow;
        // Stored status, not effective: an operation past its ValidUntil reads as 'Vencida' but is
        // stored 'Vigente', and 'Vencida' is not a status anything was ever cancelled from.
        var priorStatus = operation.Status;

        var applied = await _operationRepository
            .CancelAsync(operationId, tenantId, priorStatus, userId, cancelledAt, cancellationToken)
            .ConfigureAwait(false);

        if (!applied)
        {
            // The row moved between our read and our write. Re-read to learn which way: a concurrent
            // cancel leaves nothing to do, while a concurrent confirmar-vigencia only changed the
            // status this cancellation has to be recorded against. Bounded to a single retry — a
            // second loss means another writer is already there, and 204 is honest either way.
            operation = await _operationRepository
                .GetByIdAsync(operationId, tenantId, cancellationToken)
                .ConfigureAwait(false);
            if (operation is null || string.Equals(operation.Status, "Cancelada", StringComparison.Ordinal))
                return NoContent();

            priorStatus = operation.Status;
            applied = await _operationRepository
                .CancelAsync(operationId, tenantId, priorStatus, userId, cancelledAt, cancellationToken)
                .ConfigureAwait(false);
            if (!applied)
                return NoContent();
        }

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "CreditLineOperation.Cancelled",
            severity:     "Info",
            entityType:   "CreditLineOperation",
            entityId:     operationId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            // Both ends of the transition, so a "se cayó" (from 'Aprobada') stays distinguishable
            // from a close (from 'Vigente'). The row records that it happened and by whom; only the
            // trail records what it was before.
            oldValueJson: JsonSerializer.Serialize(new { Status = priorStatus }),
            newValueJson: JsonSerializer.Serialize(new { Status = "Cancelada" }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Cancelled operation {OperationId} under cupo {CreditLineId} tenant {TenantId}",
            operationId, operation.CreditLineId, tenantId);

        return NoContent();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in CreditLinesController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    /// <summary>
    /// Resolves the calling user's id and application role from [dbo].[Users].
    /// <para>
    /// The role comes off the DATABASE row, not the JWT's role claim, deliberately. It is used by
    /// <see cref="CanMutate"/> to GRANT an Admin or RiskManager a bypass over the ownership rule, and
    /// one demoted through the settings screen keeps the old claim until their token expires. For a
    /// check that widens access, the row that the demotion actually wrote is the safer authority.
    /// The claim still guards the door: every mutator that consults <see cref="CanMutate"/> is still
    /// gated to <see cref="Roles.CreditLineManagers"/>, and this is a second layer inside the
    /// method. (The two creations are gated to <see cref="Roles.CreditLineBuilders"/> and the
    /// structure read to <see cref="Roles.CreditLineReaders"/> instead, and none of them calls
    /// <see cref="CanMutate"/>.)
    /// </para>
    /// </summary>
    private async Task<(Guid userId, string role, IActionResult? error)> ResolveCurrentUserAsync(
        CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return (Guid.Empty, string.Empty, Unauthorized());

        var user = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, ct)
            .ConfigureAwait(false);

        if (user is null)
            return (Guid.Empty, string.Empty, Unauthorized());

        return (user.UserId, user.Role, null);
    }

    /// <summary>
    /// True when the caller may mutate a row created by <paramref name="createdByUserId"/>: they
    /// are its creator, or they are an Admin or a RiskManager. Both of those are tenant-wide — the
    /// RiskManager as the supervising role, which also CREATES the cupo when it confirms a
    /// pre-approval, so leaving it out left that cupo manageable by an Admin alone. An Analyst still
    /// manages only what they created. Tenant isolation is not this method's job: every row it is
    /// asked about was already loaded scoped to the caller's tenant.
    /// <para>
    /// Checked in the CONTROLLER, against the row every mutator already loads, rather than pushed
    /// into the repository's conditional UPDATE the way the status guard is. Two reasons.
    /// <c>CreatedByUserId</c> is written once at creation and never updated by any code path, so
    /// unlike <c>Status</c> it cannot change between the read and the write — there is no race for
    /// an atomic predicate to close. And folding it into that predicate would destroy information:
    /// <c>ConfirmVigenciaAsync</c> / <c>CancelAsync</c> returning false means exactly "the status
    /// moved", which <see cref="CancelOperationAsync"/>'s retry depends on. Made ambiguous, a
    /// non-owner's confirm would report 409 and their cancel would report 204 — success, for a
    /// write that never happened.
    /// </para>
    /// </summary>
    private static bool CanMutate(Guid createdByUserId, Guid currentUserId, string currentUserRole)
        => createdByUserId == currentUserId
        || string.Equals(currentUserRole, Roles.Admin, StringComparison.Ordinal)
        || string.Equals(currentUserRole, Roles.RiskManager, StringComparison.Ordinal);

    /// <summary>
    /// Null when the analysis' final decision covers <paramref name="amount"/> in
    /// <paramref name="currency"/>; otherwise the 400 to return. Shared by the cupo creation and the
    /// raise, which both turn an approved analysis into a tope.
    /// <para>
    /// Reads <c>DecisionAmount</c> / <c>DecisionCurrency</c> (DDL 058) — the figure the approval
    /// actually committed, the RiskManager's number on a confirmation — so the approval limits and
    /// the two-signature flow bound the cupo instead of stopping at the analysis.
    /// </para>
    /// <para>
    /// An Approved analysis with no recorded decision amount (decided before DDL 058, which did not
    /// backfill) is refused, not waved through: there is nothing to bound the cupo with, and
    /// <c>RequestedAmount</c> is neither a decision nor in any currency. Re-deciding it fixes that.
    /// </para>
    /// </summary>
    private IActionResult? DecisionBoundProblem(Analysis analysis, decimal amount, string currency)
    {
        if (analysis.DecisionAmount is not { } decisionAmount
            || string.IsNullOrWhiteSpace(analysis.DecisionCurrency))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El análisis aprobado no registra el monto aprobado (fue aprobado antes de que se registrara). Debe volver a decidirse para justificar un cupo o una ampliación.");

        // Normalised the way DecideAsync normalises it on the way in, so a hand-edited row reading
        // 'ars' does not read as a different currency from 'ARS'.
        var decisionCurrency = analysis.DecisionCurrency.Trim().ToUpperInvariant();
        if (!string.Equals(decisionCurrency, currency, StringComparison.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"La moneda no coincide con la del análisis aprobado ({decisionCurrency}).");

        if (amount > decisionAmount)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"El importe supera el monto aprobado en el análisis ({decisionCurrency} {decisionAmount.ToString("N2", EsAr)}).");

        return null;
    }

    /// <summary>
    /// Resolves display names for the given creator ids in ONE batched query, keyed by UserId.
    /// Ids that no longer resolve to a user are simply absent from the dictionary; the map helpers
    /// substitute "Usuario" for those. Mirrors the author lookup in the chat conversation list.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ResolveCreatorNamesAsync(
        IEnumerable<Guid> creatorIds, CancellationToken ct)
    {
        var ids = creatorIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var users = await _userRepository
            .GetByIdsAsync(ids, ct)
            .ConfigureAwait(false);

        return users.ToDictionary(u => u.UserId, u => u.DisplayName);
    }

    /// <summary>
    /// Display name for a creator id, falling back to "Usuario" when the id is unknown or the
    /// stored name is blank — the same fallback the chat conversation list uses.
    /// </summary>
    private static string CreatorName(Guid creatorId, IReadOnlyDictionary<Guid, string> creatorNames)
        => creatorNames.TryGetValue(creatorId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "Usuario";

    private static OperationResponse MapOperation(
        CreditLineOperation op, IReadOnlyDictionary<Guid, string> creatorNames) => new()
    {
        OperationId  = op.OperationId,
        CreditLineId = op.CreditLineId,
        AnalysisId   = op.AnalysisId,
        Type         = op.Type,
        Amount       = op.Amount,
        Currency     = op.Currency,
        ValidUntil   = op.ValidUntil,
        Notes        = op.Notes,
        ASolaFirma   = op.ASolaFirma,
        RequiereContragarantiaReal = op.RequiereContragarantiaReal,
        Status       = CreditLineRules.EffectiveStatus(op),
        CreatedAt    = op.CreatedAt,
        CreatedByUserId = op.CreatedByUserId,
        CreatedByName   = CreatorName(op.CreatedByUserId, creatorNames),
        VigenteConfirmadaEl = op.VigenteConfirmadaEl,
        CanceladaEl = op.CanceladaEl,
    };

    private static CreditLineResponse MapCreditLine(
        CreditLine line, IReadOnlyList<CreditLineOperation> operations,
        IReadOnlyDictionary<Guid, string> creatorNames,
        Guid? originAnalysisResultId = null)
    {
        // Utilizado / OperationsCount count every cupo-occupying operation — 'Aprobada' reserves
        // from creation, and effective-'Vencida' ops still consume until explicitly cancelled. Both
        // figures come off the SAME list on purpose: counting the amount but not the operation would
        // report a cupo as utilizado 300 with 0 operations.
        var occupying = operations.Where(CreditLineRules.OccupiesCupo).ToList();
        var utilizado = occupying.Sum(o => o.Amount);

        // originAnalysisResultId is resolved by the caller (the structure read batches it). Optional
        // so the create/raise/suspend callers stay unchanged and leave it null — those responses do
        // not carry the navigable id.
        return new CreditLineResponse
        {
            CreditLineId       = line.CreditLineId,
            EntityId           = line.EntityId,
            Amount             = line.Amount,
            Currency           = line.Currency,
            Status             = line.Status,
            CreatedAt          = line.CreatedAt,
            CreatedByUserId    = line.CreatedByUserId,
            CreatedByName      = CreatorName(line.CreatedByUserId, creatorNames),
            LastRaisedAt       = line.LastRaisedAt,
            LastRaiseAnalysisId = line.LastRaiseAnalysisId,
            OriginAnalysisId   = line.OriginAnalysisId,
            OriginAnalysisResultId = originAnalysisResultId,
            Utilizado          = utilizado,
            Disponible         = line.Amount - utilizado,
            OperationsCount    = occupying.Count,
            Operations         = operations.Select(o => MapOperation(o, creatorNames)).ToList(),
        };
    }
}
