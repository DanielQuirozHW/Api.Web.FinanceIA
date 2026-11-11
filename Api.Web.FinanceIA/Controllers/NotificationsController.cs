// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Returns the CURRENT USER's notifications, ordered by CreatedAt DESC, one keyset page at a time,
/// and manages their read state. TenantId is always taken from the tenant context; the user is
/// always resolved from the JWT.
/// </summary>
/// <remarks>
/// <para>
/// The per-user counterpart to <see cref="AlertsController"/>. The structural difference is the
/// scope of every operation: alerts are tenant-wide, so any user of a tenant can read and resolve
/// any of them, whereas a notification belongs to one user and is invisible to the rest. Neither
/// the tenant nor the user is ever taken from the request body or query string — the query string
/// carries only filters, which narrow the caller's own inbox and can never widen it.
/// </para>
/// <para>
/// The Portal polls the listing every 60 s, so the listing is bounded (50 by default, 100 at most)
/// and the badge has its own cheap endpoint. <c>unreadCount</c> is ALWAYS the user's total unread,
/// a COUNT of its own, whatever page or filter was asked for.
/// </para>
/// </remarks>
[ApiController]
[Route("api/notifications")]
[Route("api/bff/notifications")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class NotificationsController : ControllerBase
{
    private readonly ITenantContextAccessor            _tenantContextAccessor;
    private readonly INotificationRepository           _notificationRepository;
    private readonly IUserRepository                   _userRepository;
    private readonly ILogger<NotificationsController>  _logger;

    public NotificationsController(
        ITenantContextAccessor           tenantContextAccessor,
        INotificationRepository          notificationRepository,
        IUserRepository                  userRepository,
        ILogger<NotificationsController> logger)
    {
        _tenantContextAccessor  = tenantContextAccessor;
        _notificationRepository = notificationRepository;
        _userRepository         = userRepository;
        _logger                 = logger;
    }

    /// <summary>Default page size of the listing — what a caller with no <c>limit</c> gets.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Largest page, and the largest batch POST mark-read accepts.</summary>
    public const int MaxPageSize = 100;

    // ── GET /api/notifications ─────────────────────────────────────────────────

    /// <summary>
    /// Returns one page of the current user's notifications, ordered by CreatedAt DESC then
    /// NotificationId DESC, with the user's total unread count, the count for the current filter
    /// and the cursor of the next page.
    /// </summary>
    /// <param name="limit">Page size, 1..100. Default 50.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page. Omit for the first page.</param>
    /// <param name="unreadOnly">True → only unread notifications.</param>
    /// <param name="type">One notification type code, e.g. ANALYSIS_COMPLETED (exact).</param>
    /// <param name="sourceType">Alert | Analysis | AnalysisRun.</param>
    /// <param name="sourceId">The source's id; requires <paramref name="sourceType"/>.</param>
    /// <param name="entityId">The PyME: notifications about its analyses and analysis runs.</param>
    /// <param name="from">CreatedAt lower bound, UTC, inclusive.</param>
    /// <param name="to">CreatedAt upper bound, UTC, exclusive.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <response code="200">One page, with unreadCount, totalCount and nextCursor.</response>
    /// <response code="400">A filter or the cursor is invalid.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    [HttpGet]
    [ProducesResponseType(typeof(NotificationsResponseDto), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    // IActionResult rather than ActionResult<NotificationsResponseDto>: unlike the alerts listing,
    // this endpoint also resolves the current user, and that helper's failure path is
    // Unauthorized() — an UnauthorizedResult, not an ObjectResult. AlertsController's
    // `return (ObjectResult)error;` cast is only safe because ResolveTenant always yields
    // Problem(); applying it here would compile and then throw at runtime on a 401.
    public async Task<IActionResult> GetNotificationsAsync(
        [FromQuery] int?      limit,
        [FromQuery] string?   cursor,
        [FromQuery] bool?     unreadOnly,
        [FromQuery] string?   type,
        [FromQuery] string?   sourceType,
        [FromQuery] Guid?     sourceId,
        [FromQuery] Guid?     entityId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken     cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // Validated before the user lookup: a malformed request costs no database round-trip.
        var (query, validationError) = BuildQuery(
            limit, cursor, unreadOnly, type, sourceType, sourceId, entityId, from, to);
        if (validationError is not null) return validationError;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        // Three reads, sequential on the one scoped DbContext (it is not thread-safe). Each is a seek on
        // a (TenantId, UserId)-leading index: the page on DDL 083's keyset index, the unread count on
        // DDL 049's IsRead index, and the filtered count on whichever the filter suits.
        var page = await _notificationRepository
            .GetPageForUserAsync(tenantId, userId, query!, cancellationToken)
            .ConfigureAwait(false);

        var totalCount = await _notificationRepository
            .CountForUserAsync(tenantId, userId, query!, cancellationToken)
            .ConfigureAwait(false);

        // NOT counted from the page: the badge shows every unread notification, including the ones
        // beyond this page and outside this filter.
        var unreadCount = await _notificationRepository
            .GetUnreadCountForUserAsync(tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        var notificationDtos = page.Items.Select(ToDto).ToList();

        var last = page.Items.Count > 0 ? page.Items[^1] : null;
        var nextCursor = page.HasMore && last is not null
            ? NotificationCursor.Encode(last.CreatedAt, last.NotificationId)
            : null;

        // Counts and filter PRESENCE only — never a filter value, a title or a body.
        _logger.LogInformation(
            "Listed {Count} of {TotalCount} notifications ({UnreadCount} unread) for user {UserId} tenant {TenantId}; HasCursor {HasCursor} Filtered {IsFiltered} HasMore {HasMore}",
            notificationDtos.Count, totalCount, unreadCount, userId, tenantId,
            query!.AfterCreatedAt is not null, IsFiltered(query), page.HasMore);

        return Ok(new NotificationsResponseDto
        {
            Notifications = notificationDtos,
            UnreadCount   = unreadCount,
            NextCursor    = nextCursor,
            TotalCount    = totalCount,
        });
    }

    // ── GET /api/notifications/unread-count ─────────────────────────────────────

    /// <summary>
    /// Returns how many of the current user's notifications are unread — the badge, between full
    /// refreshes of the listing.
    /// </summary>
    /// <response code="200">The user's total unread count.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountResponseDto), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<IActionResult> GetUnreadCountAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var unreadCount = await _notificationRepository
            .GetUnreadCountForUserAsync(tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new UnreadCountResponseDto { UnreadCount = unreadCount });
    }

    // ── PATCH /api/notifications/{notificationId}/read ──────────────────────────

    /// <summary>
    /// Marks one of the current user's notifications as read.
    /// </summary>
    /// <response code="204">Notification marked as read.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Notification not found, or addressed to another user.</response>
    [HttpPatch("{notificationId:guid}/read")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> MarkAsReadAsync(
        Guid notificationId, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var success = await _notificationRepository
            .MarkAsReadAsync(notificationId, tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        // 404 covers both "no such notification" and "belongs to another user" — the repository
        // scopes by userId, and the two cases are deliberately indistinguishable to the caller so
        // this endpoint cannot be used to probe for other users' notification ids.
        return success
            ? NoContent()
            : Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la notificación.");
    }

    // ── PATCH /api/notifications/{notificationId}/unread ────────────────────────

    /// <summary>
    /// Marks one of the current user's notifications as unread again (IsRead = false, ReadAt = null).
    /// Idempotent.
    /// </summary>
    /// <response code="204">Notification marked as unread.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Notification not found, or addressed to another user.</response>
    [HttpPatch("{notificationId:guid}/unread")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> MarkAsUnreadAsync(
        Guid notificationId, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var success = await _notificationRepository
            .MarkAsUnreadAsync(notificationId, tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        // The same indistinguishable 404 as /read: it cannot be used to probe other users' ids.
        return success
            ? NoContent()
            : Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la notificación.");
    }

    // ── POST /api/notifications/mark-read ──────────────────────────────────────

    /// <summary>
    /// Marks the given notifications of the current user as read (1..100 ids). Ids that are unknown,
    /// already read or addressed to another user are not counted and not touched.
    /// </summary>
    /// <response code="200">Number of notifications that went from unread to read.</response>
    /// <response code="400">No ids, or more than 100.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    [HttpPost("mark-read")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<IActionResult> MarkManyAsReadAsync(
        [FromBody] MarkNotificationsReadRequest? request, CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var ids = request?.Ids;
        if (ids is null || ids.Count == 0)
            return ValidationProblem("ids", "Indicá al menos una notificación.");

        if (ids.Count > MaxPageSize)
            return ValidationProblem("ids", $"Se pueden marcar como máximo {MaxPageSize} notificaciones por pedido.");

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var count = await _notificationRepository
            .MarkManyAsReadAsync(tenantId, userId, ids, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new { updated = count });
    }

    // ── POST /api/notifications/mark-all-read ──────────────────────────────────

    /// <summary>
    /// Marks every unread notification of the current user as read.
    /// </summary>
    /// <response code="200">Number of notifications updated.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    [HttpPost("mark-all-read")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<IActionResult> MarkAllAsReadAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        var count = await _notificationRepository
            .MarkAllAsReadForUserAsync(tenantId, userId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new { updated = count });
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static NotificationDto ToDto(Notification n) => new()
    {
        NotificationId = n.NotificationId,
        Type           = n.Type,
        Title          = n.Title,
        Body           = n.Body,
        SourceType     = n.SourceType,
        SourceId       = n.SourceId,
        IsRead         = n.IsRead,
        ReadAt         = n.ReadAt,
        CreatedAt      = n.CreatedAt,
    };

    private static bool IsFiltered(NotificationQuery q)
        => q.UnreadOnly || q.Type is not null || q.SourceType is not null || q.SourceId is not null
        || q.EntityId is not null || q.FromUtc is not null || q.ToUtc is not null;

    /// <summary>
    /// Validates the listing's query string into a <see cref="NotificationQuery"/>, or a 400 naming the
    /// first offending parameter. Values that cannot even bind (a non-guid sourceId, a non-date from)
    /// never get here: [ApiController] answers those with its own 400.
    /// </summary>
    private (NotificationQuery? query, IActionResult? error) BuildQuery(
        int? limit, string? cursor, bool? unreadOnly, string? type, string? sourceType,
        Guid? sourceId, Guid? entityId, DateTime? from, DateTime? to)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize < 1 || pageSize > MaxPageSize)
            return (null, ValidationProblem("limit", $"limit debe estar entre 1 y {MaxPageSize}."));

        DateTime? afterCreatedAt = null;
        Guid?     afterId        = null;
        if (cursor is not null)
        {
            if (!NotificationCursor.TryDecode(cursor, out var c, out var id))
                return (null, ValidationProblem("cursor", "El cursor no es válido. Volvé a pedir la primera página."));

            afterCreatedAt = c;
            afterId        = id;
        }

        var typeFilter = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
        if (typeFilter is not null && !NotificationCatalog.KnownTypes.Contains(typeFilter))
            return (null, ValidationProblem("type", "El tipo de notificación no es válido."));

        var sourceTypeFilter = string.IsNullOrWhiteSpace(sourceType) ? null : sourceType.Trim();
        if (sourceTypeFilter is not null && !NotificationCatalog.KnownSourceTypes.Contains(sourceTypeFilter))
            return (null, ValidationProblem("sourceType", "El origen de la notificación no es válido."));

        if (sourceId is not null && sourceTypeFilter is null)
            return (null, ValidationProblem("sourceId", "sourceId requiere sourceType."));

        var fromUtc = ToUtc(from);
        var toUtc   = ToUtc(to);
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            return (null, ValidationProblem("from", "from debe ser anterior a to."));

        return (new NotificationQuery
        {
            Limit               = pageSize,
            AfterCreatedAt      = afterCreatedAt,
            AfterNotificationId = afterId,
            UnreadOnly          = unreadOnly == true,
            Type                = typeFilter,
            SourceType          = sourceTypeFilter,
            SourceId            = sourceId,
            EntityId            = entityId,
            FromUtc             = fromUtc,
            ToUtc               = toUtc,
        }, null);
    }

    /// <summary>
    /// CreatedAt is stored in UTC. A value with no offset is taken as UTC already; one that model
    /// binding turned into local time (it carried a "Z" or an offset) is converted back to UTC.
    /// </summary>
    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null                                            => null,
        { Kind: DateTimeKind.Local } local              => local.ToUniversalTime(),
        { Kind: DateTimeKind.Unspecified } unspecified  => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
        { } utc                                         => utc,
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

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in NotificationsController");
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
