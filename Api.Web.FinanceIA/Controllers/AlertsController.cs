// Project : Api.Web.FinanceIA
// Created : 2026-05-10
// Modified: 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Returns tenant-isolated alerts ordered by CreatedAt DESC.
/// Entity name, CUIT and person type, resolver names, and the event alerts' facts and reopenedAfter are
/// joined server-side, each in one batched read. TenantId is always taken from JWT.
/// Provides read/resolve/unresolve actions on individual alerts.
/// </summary>
[ApiController]
[Route("api/alerts")]
[Route("api/bff/alerts")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class AlertsController : ControllerBase
{
    private readonly ITenantContextAccessor       _tenantContextAccessor;
    private readonly IAlertRepository             _alertRepository;
    private readonly IEntityRepository            _entityRepository;
    private readonly IUserRepository              _userRepository;
    private readonly IAnalysisResultRepository    _analysisResultRepository;
    private readonly ICupoAlertService            _cupoAlertService;
    private readonly ILogger<AlertsController>    _logger;

    public AlertsController(
        ITenantContextAccessor    tenantContextAccessor,
        IAlertRepository          alertRepository,
        IEntityRepository         entityRepository,
        IUserRepository           userRepository,
        IAnalysisResultRepository analysisResultRepository,
        ICupoAlertService         cupoAlertService,
        ILogger<AlertsController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _alertRepository          = alertRepository;
        _entityRepository         = entityRepository;
        _userRepository           = userRepository;
        _analysisResultRepository = analysisResultRepository;
        _cupoAlertService         = cupoAlertService;
        _logger                   = logger;
    }

    // ── GET /api/alerts ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all alerts for the current tenant ordered by CreatedAt DESC.
    /// The full-tenant listing first reconciles the cupo event alerts, so it already reflects them;
    /// the ?entityId= variant reads what is stored.
    /// </summary>
    /// <response code="200">Alerts list with severity counts.</response>
    /// <response code="401">Unauthorized.</response>
    [HttpGet]
    [ProducesResponseType(typeof(AlertsResponseDto), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<ActionResult<AlertsResponseDto>> GetAlertsAsync(
        [FromQuery] Guid? entityId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return (ObjectResult)error;

        // The facts of the open event alerts, as this request's pass computed them. Null on the entityId
        // path (no pass runs there) and whenever the pass was skipped or failed.
        var facts = entityId.HasValue
            ? null
            : await ReconcileCupoAlertsAsync(tenantId, cancellationToken).ConfigureAwait(false);

        var alerts = entityId.HasValue
            ? await _alertRepository
                .GetByEntityIdAsync(entityId.Value, tenantId, cancellationToken)
                .ConfigureAwait(false)
            : await _alertRepository
                .GetAllByTenantAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);

        // Batch-load entity names BY ID, not by tenant. This used to call GetAllByTenantAsync,
        // which reads every entity the tenant owns with all five of its nvarchar(max) source
        // snapshots — a full-table blob scan to spell one business name, and it ran even when
        // entityId had already narrowed the list to a single PyME. Same fix as #139.
        var neededEntityIds = alerts
            .Where(a => a.EntityId.HasValue)
            .Select(a => a.EntityId!.Value)
            .Distinct()
            .ToList();
        var entityLookup = await _entityRepository
            .GetNameLookupByIdsAsync(neededEntityIds, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Resolve AnalysisId -> ResultId. Alerts store AnalysisId, but the detail page is keyed
        // by ResultId, so the "Ver" action needs the ResultId. Also by id: the previous
        // GetAllByTenantAsync read every AnalysisResult of the tenant with all fourteen of its
        // nvarchar(max) columns to keep two Guids per row. UQ_AnalysisResults_AnalysisId makes
        // the mapping 1:1, so nothing is lost by asking only for the ids these alerts name.
        var neededAnalysisIds = alerts
            .Where(a => a.AnalysisId.HasValue)
            .Select(a => a.AnalysisId!.Value)
            .Distinct()
            .ToList();
        var resultIdLookup = await _analysisResultRepository
            .GetResultIdsByAnalysisIdsAsync(neededAnalysisIds, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // reopenedAfter: one read of the resolved history of the open event alerts' keys.
        var reopenedAfter = await PreviousUserResolutionsAsync(tenantId, alerts, cancellationToken)
            .ConfigureAwait(false);

        // resolvedByName (of the alert, and of the dismissal reopenedAfter names): one users read.
        var userNames = await UserNamesAsync(
                tenantId,
                alerts.Select(a => a.ResolvedBy).Concat(reopenedAfter.Values.Select(p => p.ResolvedBy)),
                cancellationToken)
            .ConfigureAwait(false);

        var alertDtos = alerts.Select(a =>
        {
            var entity = a.EntityId.HasValue && entityLookup.TryGetValue(a.EntityId.Value, out var found)
                ? found
                : null;

            return new AlertDto
            {
                AlertId        = a.AlertId,
                TenantId       = a.TenantId,
                EntityId       = a.EntityId,
                AnalysisId     = a.AnalysisId,
                ResultId       = a.AnalysisId.HasValue && resultIdLookup.TryGetValue(a.AnalysisId.Value, out var resultId)
                    ? resultId
                    : null,
                AlertType      = a.AlertType,
                Severity       = a.Severity,
                Title          = a.Title,
                Description    = a.Description,
                MetricKey      = a.MetricKey,
                MetricValue    = a.MetricValue,
                ThresholdValue = a.ThresholdValue,
                IsRead         = a.IsRead,
                IsResolved     = a.IsResolved,
                ResolvedAt     = a.ResolvedAt,
                EntityName     = entity?.BusinessName ?? "Sistema",
                CreatedAt      = a.CreatedAt,

                EntityCuit            = entity?.CUIT,
                EntityIsPersonaHumana = entity is null
                    ? null
                    : string.Equals(entity.PersonType, PersonTypes.Humana, StringComparison.Ordinal),
                ResolvedByUserId      = a.ResolvedBy,
                ResolvedByName        = NameOf(a.ResolvedBy, userNames),
                IsAutoResolved        = a.IsResolved && a.ResolvedBy is null,
                CreditLineId          = EventAlertTypes.Cupo.Contains(a.AlertType)
                                        && EventAlertTypes.TryParseCupoMetricKey(a.MetricKey, out var creditLineId)
                    ? creditLineId
                    : null,
                Facts                 = !a.IsResolved && facts is not null && facts.TryGetValue(a.AlertId, out var alertFacts)
                    ? alertFacts
                    : null,
                ReopenedAfter         = reopenedAfter.TryGetValue(a.AlertId, out var previous)
                    ? new AlertReopenedAfterDto
                    {
                        ResolvedAt     = previous.ResolvedAt,
                        ResolvedByName = NameOf(previous.ResolvedBy, userNames),
                        MetricValue    = previous.MetricValue
                    }
                    : null,
            };
        }).ToList();

        _logger.LogInformation(
            "Listed {Count} alerts for tenant {TenantId} (entityId={EntityId})",
            alertDtos.Count, tenantId, entityId?.ToString() ?? "all");

        return Ok(new AlertsResponseDto
        {
            Alerts        = alertDtos,
            TotalCount    = alertDtos.Count,
            CriticalCount = alertDtos.Count(a => a.Severity == "Critical"),
            HighCount     = alertDtos.Count(a => a.Severity == "High"),
            MediumCount   = alertDtos.Count(a => a.Severity == "Medium"),
            LowCount      = alertDtos.Count(a => a.Severity == "Low"),
            ResolvedCount = alertDtos.Count(a => a.IsResolved),
        });
    }

    // ── PATCH /api/alerts/{alertId}/read ────────────────────────────────────────

    /// <summary>
    /// Marks an alert as read by the current user. The read state is tenant-wide, so this is gated on
    /// <see cref="Roles.AlertReadMarkers"/>: a SuperAdmin gets 403 — see that constant.
    /// </summary>
    /// <response code="204">Alert marked as read.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">Caller is a SuperAdmin (read-only support).</response>
    /// <response code="404">Alert not found.</response>
    [HttpPatch("{alertId:guid}/read")]
    [Authorize(Roles = Roles.AlertReadMarkers)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> MarkAsReadAsync(
        Guid alertId, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var success = await _alertRepository
            .MarkAsReadAsync(alertId, tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        return success
            ? NoContent()
            : Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la alerta.");
    }

    // ── PATCH /api/alerts/{alertId}/resolve ─────────────────────────────────────

    /// <summary>
    /// Marks an alert as resolved by the current user.
    /// </summary>
    /// <response code="204">Alert marked as resolved.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Alert not found.</response>
    [HttpPatch("{alertId:guid}/resolve")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> MarkAsResolvedAsync(
        Guid alertId, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var success = await _alertRepository
            .MarkAsResolvedAsync(alertId, tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        return success
            ? NoContent()
            : Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la alerta.");
    }

    // ── PATCH /api/alerts/{alertId}/unresolve ───────────────────────────────────

    /// <summary>
    /// Reopens an alert a user resolved. Refused with 409 when the system resolved it (the situation
    /// cleared) or when another open alert already exists for the same situation (the system recreated it).
    /// Not audited, like resolve.
    /// </summary>
    /// <response code="204">Alert reopened (or it was already open).</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Alert not found.</response>
    /// <response code="409">Auto-resolved, or another open alert exists for the same situation.</response>
    [HttpPatch("{alertId:guid}/unresolve")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> UnresolveAsync(
        Guid alertId, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (_, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var outcome = await _alertRepository
            .UnresolveAsync(alertId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            AlertUnresolveOutcome.Reopened      => NoContent(),
            AlertUnresolveOutcome.NotFound      => Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la alerta."),
            AlertUnresolveOutcome.AutoResolved  => Problem(statusCode: 409, title: "Conflict", detail: "La alerta fue resuelta automáticamente; no se puede reabrir."),
            AlertUnresolveOutcome.OpenDuplicate => Problem(statusCode: 409, title: "Conflict", detail: "Ya existe una alerta abierta para la misma situación."),
            _                                   => throw new InvalidOperationException($"Unknown unresolve outcome {outcome}.")
        };
    }

    // ── POST /api/alerts/mark-all-read ─────────────────────────────────────────

    /// <summary>
    /// Marks every unread alert for the current tenant as read. Gated on
    /// <see cref="Roles.AlertReadMarkers"/>, like the single-alert read: a SuperAdmin gets 403.
    /// </summary>
    /// <response code="200">Number of alerts updated.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="403">Caller is a SuperAdmin (read-only support).</response>
    [HttpPost("mark-all-read")]
    [Authorize(Roles = Roles.AlertReadMarkers)]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<IActionResult> MarkAllAsReadAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var count = await _alertRepository
            .MarkAllAsReadAsync(tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new { updated = count });
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Brings the cupo event alerts up to date before the listing reads them. ReconcileAsync already
    /// never throws; the catch here is the second guard on the hottest read in the app — whatever goes
    /// wrong with the cupo alerts, the stored alerts are still listed with a 200. Only a cancellation of
    /// this request propagates.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, AlertFactsDto>?> ReconcileCupoAlertsAsync(string tenantId, CancellationToken ct)
    {
        try
        {
            return await _cupoAlertService.ReconcileAsync(tenantId, DateTime.UtcNow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cupo alert reconciliation threw for tenant {TenantId}; listing the stored alerts.",
                tenantId);
            return null;
        }
    }

    /// <summary>
    /// For each OPEN event alert in <paramref name="alerts"/>: the alert that came before it for the same
    /// (AlertType, MetricKey) — the latest earlier one — when a USER resolved it. If the system resolved it
    /// instead, the situation cleared and came back, which is not a return after a dismissal. One read for
    /// the whole page; none at all when it holds no open event alert.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, PreviousAlertRow>> PreviousUserResolutionsAsync(
        string tenantId, IReadOnlyList<Alert> alerts, CancellationToken ct)
    {
        var open = alerts
            .Where(a => !a.IsResolved
                     && EventAlertTypes.All.Contains(a.AlertType)
                     && !string.IsNullOrEmpty(a.MetricKey))
            .ToList();

        if (open.Count == 0) return new Dictionary<Guid, PreviousAlertRow>();

        var history = await _alertRepository
            .GetPreviousByKeysAsync(tenantId, open.Select(a => a.MetricKey!).Distinct(StringComparer.Ordinal).ToList(), ct)
            .ConfigureAwait(false);

        var byKey = history.ToLookup(h => (h.AlertType, h.MetricKey));
        var result = new Dictionary<Guid, PreviousAlertRow>();
        foreach (var alert in open)
        {
            var previous = byKey[(alert.AlertType, alert.MetricKey!)]
                .Where(h => h.AlertId != alert.AlertId && h.CreatedAt <= alert.CreatedAt)
                .OrderByDescending(h => h.CreatedAt)
                .ThenByDescending(h => h.AlertId)
                .FirstOrDefault();

            if (previous?.ResolvedBy is not null)
                result[alert.AlertId] = previous;
        }

        return result;
    }

    /// <summary>
    /// DisplayName of each user id, in one read. GetByIdsAsync carries no tenant predicate, so rows of
    /// another tenant are dropped here — the DashboardService.UsersAsync pattern.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(
        string tenantId, IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var users = await _userRepository.GetByIdsAsync(ids, ct).ConfigureAwait(false);

        return users
            .Where(u => string.Equals(u.TenantId, tenantId, StringComparison.Ordinal)
                     && !string.IsNullOrWhiteSpace(u.DisplayName))
            .GroupBy(u => u.UserId)
            .ToDictionary(g => g.Key, g => g.First().DisplayName);
    }

    private static string? NameOf(Guid? userId, IReadOnlyDictionary<Guid, string> names)
        => userId is { } id && names.TryGetValue(id, out var name) ? name : null;

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in AlertsController");
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
}
