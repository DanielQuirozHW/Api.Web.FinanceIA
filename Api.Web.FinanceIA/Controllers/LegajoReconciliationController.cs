// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// A legajo's reconciled view (M10 Phase 3b): the inventory with market and fiscal values side by side,
/// the incomes by period with their coherence, the analyst's edits to an item, and the explicit rebuild.
/// Reads are open to any authenticated role of the tenant, like the legajo review; writes are
/// <see cref="Roles.Operators"/>. Tenant-scoped throughout; every 4xx carries an <c>errorCode</c>.
///
/// <para>
/// Nothing here calls the model. Totals, coherence and precedence picks are computed at read time
/// (<see cref="LegajoReconciliationReadModel"/>); the rows are rebuilt by
/// <see cref="ILegajoReconciliationService"/> whenever what is approved changes.
/// </para>
///
/// <para>
/// PERSONAL DATA: a person's patrimony and income. Nothing here logs a value, a description, a note or
/// a reason — ids and counts only. The audit records which fields changed and the reason, never values.
/// </para>
/// </summary>
[ApiController]
[Route("api/person-legajos/{legajoId:guid}")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class LegajoReconciliationController : ControllerBase
{
    /// <summary>Minimum non-blank length of a reason (the same rule as an extraction correction).</summary>
    private const int MinReasonLength = 5;

    /// <summary>Maximum length of a reason, and of a note (LegajoItems.AnalystNote is nvarchar(1000)).</summary>
    private const int MaxTextLength = 1000;

    private readonly ITenantContextAccessor          _tenantContextAccessor;
    private readonly ILegajoDocumentRepository       _legajoRepository;
    private readonly ILegajoReconciliationRepository _reconciliationRepository;
    private readonly ILegajoReconciliationService    _reconciliation;
    private readonly IUserRepository                 _userRepository;
    private readonly IAuditService                   _auditService;
    private readonly ILogger<LegajoReconciliationController> _logger;

    /// <summary>Initializes a new instance of <see cref="LegajoReconciliationController"/>.</summary>
    public LegajoReconciliationController(
        ITenantContextAccessor          tenantContextAccessor,
        ILegajoDocumentRepository       legajoRepository,
        ILegajoReconciliationRepository reconciliationRepository,
        ILegajoReconciliationService    reconciliation,
        IUserRepository                 userRepository,
        IAuditService                   auditService,
        ILogger<LegajoReconciliationController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _legajoRepository         = legajoRepository;
        _reconciliationRepository = reconciliationRepository;
        _reconciliation           = reconciliation;
        _userRepository           = userRepository;
        _auditService             = auditService;
        _logger                   = logger;
    }

    // ── GET /api/person-legajos/{legajoId}/inventory ────────────────────────────

    /// <summary>
    /// The reconciled inventory: items grouped by side and kind, each with both values, their origin
    /// documents and its reconciliation status, and totals computed at read time (pesos); plus the guarantees
    /// given its manifestación declares (garantiasOtorgadasDeclaradas, mb-1.2), informative only.
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="LegajoInventoryResponse"/>, or 404 <c>legajo_not_found</c>.</returns>
    [HttpGet("inventory")]
    [ProducesResponseType(typeof(LegajoInventoryResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetInventoryAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        if (await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken).ConfigureAwait(false) is null)
            return LegajoNotFound();

        var items = await _reconciliationRepository.GetItemsAsync(legajoId, tenantId, cancellationToken).ConfigureAwait(false);
        var names = await UserNamesAsync(items.Select(i => i.UpdatedBy).OfType<Guid>(), tenantId, cancellationToken)
            .ConfigureAwait(false);
        var inventory = LegajoReconciliationReadModel.BuildInventory(legajoId, items, names);

        // The manifestación's declared guarantees given (mb-1.2) ride beside the items: read from its current run.
        if (inventory.ManifestacionDocumentIds.Count == 0)
            return Ok(inventory);

        var runs = await _legajoRepository
            .GetCurrentExtractionsAsync(inventory.ManifestacionDocumentIds, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var corrections = await _legajoRepository
            .GetCorrectionsAsync(runs.Values.Select(r => r.ExtractionId).ToList(), tenantId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(LegajoReconciliationReadModel.WithGuaranteesGiven(inventory, runs, corrections));
    }

    // ── GET /api/person-legajos/{legajoId}/incomes ──────────────────────────────

    /// <summary>The incomes by period and source, newest first, each period with its sales coherence.</summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="LegajoIncomesResponse"/>, or 404 <c>legajo_not_found</c>.</returns>
    [HttpGet("incomes")]
    [ProducesResponseType(typeof(LegajoIncomesResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetIncomesAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        if (await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken).ConfigureAwait(false) is null)
            return LegajoNotFound();

        var incomes = await _reconciliationRepository.GetIncomesAsync(legajoId, tenantId, cancellationToken).ConfigureAwait(false);
        return Ok(LegajoReconciliationReadModel.BuildIncomes(legajoId, incomes));
    }

    // ── PATCH /api/person-legajos/{legajoId}/items/{itemId} ─────────────────────

    /// <summary>
    /// The analyst's edits to one item: the housing and activity flags, the ownership percentage, the class of an
    /// investment, the note, and the confirmation of a conflict. Edited fields survive a rebuild; a confirmation
    /// survives while the same conflict stands. Reason mandatory; audited without the values.
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="request">The edits and the reason.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="LegajoItemResponse"/>; 400 <c>validation_error</c>; 404 <c>legajo_not_found</c>
    /// or <c>item_not_found</c>; 409 <c>item_not_in_conflict</c> or <c>item_changed</c>.
    /// </returns>
    [HttpPatch("items/{itemId:guid}")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(LegajoItemResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> UpdateItemAsync(
        Guid legajoId,
        Guid itemId,
        [FromBody] LegajoItemUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        // ── Validation, before any read ─────────────────────────────────────────
        var reason = request?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < MinReasonLength)
            return ValidationProblem("reason", $"El motivo es obligatorio (al menos {MinReasonLength} caracteres).");
        if (reason.Length > MaxTextLength)
            return ValidationProblem("reason", $"El motivo no puede superar {MaxTextLength} caracteres.");

        if (request!.IsHousing is null && request.IsActivityAsset is null && request.OwnershipPct is null
            && request.InvestmentClass is null && request.AnalystNote is null && request.ConfirmConflict is not true)
            return ValidationProblem("request", "Indicá al menos un cambio.");

        if (request.OwnershipPct is { } pct && (pct < 0 || pct > 100))
            return ValidationProblem("ownershipPct", "El porcentaje de titularidad debe estar entre 0 y 100.");

        // "" clears the class; anything else must be one of the known classes (exact spelling).
        var investmentClass = string.IsNullOrWhiteSpace(request.InvestmentClass) ? null : request.InvestmentClass.Trim();
        if (investmentClass is not null && !LegajoInvestmentClasses.Known.Contains(investmentClass))
            return ValidationProblem("investmentClass",
                "La clase de inversión no es válida: TituloPublico, AccionCotizante, ObligacionNegociable, CuotaparteFci u Otra.");

        var note = request.AnalystNote?.Trim();
        if (note is { Length: > MaxTextLength })
            return ValidationProblem("analystNote", $"La nota no puede superar {MaxTextLength} caracteres.");

        // ── The item ────────────────────────────────────────────────────────────
        if (await _legajoRepository.GetLegajoSummaryAsync(legajoId, tenantId, cancellationToken).ConfigureAwait(false) is null)
            return LegajoNotFound();

        var item = await _reconciliationRepository.GetItemAsync(itemId, legajoId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
            return CodedProblem(404, "Not Found", "item_not_found", "No se encontró el ítem en este legajo.");

        if (request.InvestmentClass is not null && item.Kind != LegajoItemKinds.Inversion)
            return ValidationProblem("investmentClass", "Solo un ítem de tipo inversión admite una clase de inversión.");

        if (request.ConfirmConflict == true && item.ReconciliationStatus != LegajoReconciliationStatuses.Conflict)
            return CodedProblem(409, "Conflict", "item_not_in_conflict",
                "Solo se confirma un ítem en conflicto.");

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        if (userId is null)
            return Unauthorized();

        // ── Apply ───────────────────────────────────────────────────────────────
        var expectedUpdatedAt = item.UpdatedAt;
        var edited  = LegajoReconciliation.EditedFieldsOf(item).ToHashSet();
        var changed = new List<string>();

        if (request.IsHousing is { } isHousing)
        {
            item.IsHousing = isHousing;
            edited.Add(LegajoItemEditableFields.IsHousing);
            changed.Add("isHousing");
        }

        if (request.IsActivityAsset is { } isActivityAsset)
        {
            item.IsActivityAsset = isActivityAsset;
            edited.Add(LegajoItemEditableFields.IsActivityAsset);
            changed.Add("isActivityAsset");
        }

        if (request.OwnershipPct is { } ownership)
        {
            item.OwnershipPct = Math.Round(ownership, 4);
            edited.Add(LegajoItemEditableFields.OwnershipPct);
            changed.Add("ownershipPct");
        }

        if (request.InvestmentClass is not null)
        {
            item.InvestmentClass = investmentClass;
            edited.Add(LegajoItemEditableFields.InvestmentClass);
            changed.Add("investmentClass");
        }

        if (request.AnalystNote is not null)
        {
            item.AnalystNote = string.IsNullOrEmpty(note) ? null : note;
            changed.Add("analystNote");
        }

        if (request.ConfirmConflict == true)
        {
            item.ReconciliationStatus = LegajoReconciliationStatuses.AnalystConfirmed;
            item.ConfirmedFingerprint = LegajoReconciliation.Fingerprint(item);
            changed.Add("confirmConflict");
        }

        item.EditedFields = LegajoReconciliation.EditedFieldsCsv(edited);
        item.UpdatedAt    = DateTime.UtcNow;
        item.UpdatedBy    = userId;

        if (!await _reconciliationRepository.TryUpdateItemAsync(item, expectedUpdatedAt, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Legajo {PersonLegajoId} tenant {TenantId}: edit of item {ItemId} lost to a concurrent change",
                legajoId, tenantId, itemId);
            return CodedProblem(409, "Conflict", "item_changed",
                "El ítem cambió mientras lo editabas (otra edición o una reconstrucción). Recargá y volvé a intentar.");
        }

        // The audit row names the fields and carries the reason — never the values or the note (personal data).
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "LegajoItem.Updated",
            severity:     "Info",
            entityType:   "LegajoItem",
            entityId:     itemId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                personLegajoId = legajoId,
                fields         = changed,
                reason
            }),
            ct: CancellationToken.None);

        _logger.LogInformation(
            "Legajo {PersonLegajoId} tenant {TenantId}: item {ItemId} edited ({Fields})",
            legajoId, tenantId, itemId, string.Join(",", changed));

        var names = await UserNamesAsync([userId.Value], tenantId, cancellationToken).ConfigureAwait(false);
        return Ok(LegajoReconciliationReadModel.Item(item, names));
    }

    // ── POST /api/person-legajos/{legajoId}/reconciliation/rebuild ──────────────

    /// <summary>
    /// Rebuilds the legajo's inventory and incomes from its approved documents now. The same rebuild runs
    /// on its own after an approval, a reopening correction, a new extraction or a delete; this one repairs
    /// a rebuild that failed there.
    /// </summary>
    /// <param name="legajoId">The legajo identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="LegajoReconciliationResultResponse"/>; 404 <c>legajo_not_found</c>; 409
    /// <c>reconciliation_busy</c> when it lost twice to a concurrent write.
    /// </returns>
    [HttpPost("reconciliation/rebuild")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(typeof(LegajoReconciliationResultResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> RebuildAsync(Guid legajoId, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        var outcome = await _reconciliation.RebuildAsync(legajoId, tenantId, cancellationToken).ConfigureAwait(false);
        if (!outcome.LegajoFound)
            return LegajoNotFound();

        if (outcome.LostRace || outcome.Result is null)
            return CodedProblem(409, "Conflict", "reconciliation_busy",
                "El legajo se está actualizando en este momento. Volvé a intentar en unos segundos.");

        var userId = await ResolveCallerIdAsync(cancellationToken).ConfigureAwait(false);
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "PersonLegajo.ReconciliationRebuilt",
            severity:     "Info",
            entityType:   "PersonLegajo",
            entityId:     legajoId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                itemsCount          = outcome.Result.ItemsCount,
                incomesCount        = outcome.Result.IncomesCount,
                itemsAdded          = outcome.Result.ItemsAdded,
                itemsUpdated        = outcome.Result.ItemsUpdated,
                itemsRemoved        = outcome.Result.ItemsRemoved,
                confirmationsLapsed = outcome.Result.ConfirmationsLapsed
            }),
            ct: CancellationToken.None);

        return Ok(outcome.Result);
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    /// <summary>Display names of the given users — only those of this tenant.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(
        IEnumerable<Guid> userIds, string tenantId, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return (await _userRepository.GetByIdsAsync(ids, ct).ConfigureAwait(false))
            .Where(u => string.Equals(u.TenantId, tenantId, StringComparison.Ordinal))
            .ToDictionary(u => u.UserId, u => u.DisplayName);
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
        => CodedProblem(404, "Not Found", "legajo_not_found", "No se encontró el legajo.");

    /// <summary>
    /// <see cref="ControllerBase.Problem(string, string, int?, string, string)"/> plus a stable
    /// <c>errorCode</c> extension the Portal can branch on.
    /// </summary>
    private ObjectResult CodedProblem(int status, string title, string errorCode, string detail)
    {
        var result = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = errorCode;
        return result;
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
