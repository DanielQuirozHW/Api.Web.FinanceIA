// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// One accepted password-reset request, handed from the controller to
/// <see cref="PasswordResetWorker"/>. It carries the resolved <see cref="TenantContext"/> because the
/// worker runs outside any HTTP request and must not re-resolve the tenant. Lives only in memory;
/// <see cref="Email"/> is PII and is never logged.
/// </summary>
/// <param name="TenantContext">The tenant resolved from X-Tenant-ID for the original request.</param>
/// <param name="Email">The trimmed, lower-cased address that was submitted.</param>
/// <param name="IpAddress">Caller IP, for the audit row.</param>
/// <param name="TraceId">Request trace id, for the audit row and log correlation.</param>
public sealed record PasswordResetWorkItem(
    TenantContext TenantContext,
    string Email,
    string? IpAddress,
    string? TraceId);
