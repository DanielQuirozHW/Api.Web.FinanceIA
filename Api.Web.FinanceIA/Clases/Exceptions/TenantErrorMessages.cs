// Project : Api.Web.FinanceIA
// Created : 2026-08-03
// Modified: 2026-08-03

namespace Api.Web.FinanceIA.Clases.Exceptions;

/// <summary>
/// Single source of truth for the tenant-resolution ProblemDetails <c>detail</c> strings.
/// The same messages are emitted both by the tenant exceptions (via <see cref="AppException"/> →
/// <c>GlobalExceptionHandler</c>) and by the tenant middlewares, which write ProblemDetails
/// directly. Centralising the literals here keeps the two code paths from drifting into
/// different wordings (or languages) for the same error.
/// </summary>
internal static class TenantErrorMessages
{
    /// <summary>HTTP 400 — the X-Tenant-ID header is missing, empty or malformed.</summary>
    public const string InvalidOrMissingIdentifier = "El identificador de tenant es inválido o falta.";

    /// <summary>HTTP 404 — the tenant identifier does not exist.</summary>
    public const string NotFound = "No se encontró el tenant.";

    /// <summary>
    /// HTTP 500 — the tenant exists but its configuration is incomplete. Diagnostic/support
    /// message; kept in English on purpose (it is not actionable end-user copy).
    /// </summary>
    public const string ConfigError = "Tenant configuration error.";
}
