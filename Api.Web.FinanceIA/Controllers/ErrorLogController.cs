// Project : Api.Web.FinanceIA
// Created : 2026-09-02

using System.Diagnostics;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Receives browser-side errors from the Portal and persists them to the same
/// [dbo].[ErrorLogs] table the backend writes to, discriminated by the
/// <c>Source</c> convention: "financia-portal:{tenant}" here,
/// "financia-bff:{tenant}" from <see cref="GlobalExceptionHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is <see cref="AllowAnonymousAttribute"/> on purpose: the browser must be able
/// to report a crash even when the session is exactly what broke. It is still rate limited —
/// <c>RateLimitingMiddleware</c> runs for every path and <c>RateLimitService</c> applies a
/// per-IP budget to <c>/api/errorlog</c> (<c>RateLimiting:PerIP:ClientErrorLog</c>).
/// </para>
/// <para>
/// Authentication is not required but is still honoured: <c>DefaultAuthenticateScheme</c> is
/// "Internal", so <c>UseAuthentication()</c> populates <see cref="ControllerBase.User"/> from a
/// valid bearer token or the <c>bff_session</c> cookie even on an anonymous action, while an
/// expired or malformed token fails silently and simply leaves the principal anonymous. The
/// user id is therefore recorded when it is available and omitted when it is not — never demanded.
/// </para>
/// <para>
/// Nothing here reaches <c>FinanciaDbContext</c>; ErrorLogs lives in AppDbContext, so there is no
/// business-data tenant scoping to apply. The tenant is recorded, not used as a filter.
/// </para>
/// </remarks>
[ApiController]
[Route("api/errorlog")]
[Route("api/bff/errorlog")]
[AllowAnonymous]
public sealed class ErrorLogController : ControllerBase
{
    // Column limits. Message, ExceptionDetails and StackTrace map to nvarchar(max), so SQL imposes
    // no ceiling of its own — these clamps exist because the payload is unauthenticated browser
    // input and an unbounded row is a cheap way to abuse the table.
    private const int MaxMessageLength          = 4000;
    private const int MaxExceptionDetailsLength = 4000;
    private const int MaxStackTraceLength       = 8000;
    private const int MaxDigestLength           = 100;
    private const int MaxUrlLength              = 2048;   // [Url] varchar(2048)
    private const int MaxUserIdLength           = 255;    // [UserId] varchar(255)
    private const int MaxIpAddressLength        = 45;     // [IPAddress] varchar(45)
    private const int MaxTraceIdLength          = 100;    // [TraceId] varchar(100)

    private const string SourcePrefix    = "financia-portal";
    private const string UnknownTenantId = "unknown";

    private readonly IErrorLogService            _errorLogService;
    private readonly ILogger<ErrorLogController> _logger;

    public ErrorLogController(
        IErrorLogService            errorLogService,
        ILogger<ErrorLogController> logger)
    {
        _errorLogService = errorLogService;
        _logger          = logger;
    }

    // ── POST /api/errorlog/client ──────────────────────────────────────────────

    /// <summary>
    /// Persists a browser-side error reported by the Portal.
    /// </summary>
    /// <param name="request">The client error report. Only <c>message</c> is required.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>204 No Content on success; 400 only when <c>message</c> is missing.</returns>
    /// <response code="204">Report accepted. Also returned when persistence failed — see remarks.</response>
    /// <response code="400"><c>message</c> was missing, empty or whitespace.</response>
    /// <response code="429">Per-IP rate limit exceeded.</response>
    /// <remarks>
    /// Telemetry must never become a second failure the browser has to handle, so a persistence
    /// failure still answers 204: <c>ErrorLogService</c> and <c>ErrorLogRepository</c> both catch
    /// and log internally, and the try/catch here guarantees it even if that ever changes.
    /// A blank message is the one case that does answer 400 — it means the Portal's reporter is
    /// broken, and silently accepting it would hide that forever.
    /// </remarks>
    [HttpPost("client")]
    [RequestSizeLimit(32 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LogClientErrorAsync(
        [FromBody] ClientErrorLogRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title     : "Bad Request",
                detail    : "El campo message es obligatorio.");
        }

        var tenantId = HttpContext.Items["TenantContext"] is TenantContext tenantContext
            ? tenantContext.TenantId
            : UnknownTenantId;

        var traceId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        var userId = User?.FindFirst("uid")?.Value
                     ?? User?.FindFirst("sub")?.Value;

        var entry = new ErrorLogEntry(
            Level           : "Error",
            Message         : Clamp(request.Message, MaxMessageLength)!,
            ExceptionDetails: BuildExceptionDetails(request.ExceptionDetails, request.Digest),
            Source          : $"{SourcePrefix}:{tenantId}",
            Url             : Clamp(request.Url, MaxUrlLength),
            UserId          : Clamp(userId, MaxUserIdLength),
            IpAddress       : Clamp(HttpContext.Connection.RemoteIpAddress?.ToString(), MaxIpAddressLength),
            StackTrace      : Clamp(request.StackTrace, MaxStackTraceLength),
            TraceId         : Clamp(traceId, MaxTraceIdLength));

        try
        {
            await _errorLogService.LogAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defence in depth: LogAsync already swallows everything. If a future change to the
            // service starts throwing, the browser must still not receive a 5xx for a crash report.
            _logger.LogCritical(ex,
                "Failed to persist client error log. Source: {Source} | TraceId: {TraceId}",
                entry.Source,
                traceId);
        }

        return NoContent();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Composes the [ExceptionDetails] value. The digest has no column of its own and the table
    /// may not be altered, so it rides along in the pipe-separated form that column already uses
    /// for backend errors ("{ExceptionType} | ErrorCode: {code}") rather than being dropped.
    /// </summary>
    private static string BuildExceptionDetails(string? exceptionDetails, string? digest)
    {
        var details = Clamp(exceptionDetails, MaxExceptionDetailsLength);
        var trimmedDigest = Clamp(digest, MaxDigestLength);

        if (string.IsNullOrWhiteSpace(details))
        {
            details = "ClientError";
        }

        return string.IsNullOrWhiteSpace(trimmedDigest)
            ? details
            : $"{details} | Digest: {trimmedDigest}";
    }

    /// <summary>
    /// Trims <paramref name="value"/> and truncates it to <paramref name="maxLength"/>.
    /// Returns null for null, empty or whitespace input so the column stays NULL rather than "".
    /// </summary>
    private static string? Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }
}
