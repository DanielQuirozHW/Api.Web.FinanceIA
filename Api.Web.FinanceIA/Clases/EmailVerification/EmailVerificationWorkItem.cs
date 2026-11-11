// Project : Api.Web.FinanceIA
// Created : 2026-10-21

using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases.EmailVerification;

/// <summary>
/// One accepted email-verification request, handed from the controller to
/// <see cref="EmailVerificationWorker"/>. It carries the resolved <see cref="TenantContext"/> because
/// the worker runs outside any HTTP request and must not re-resolve the tenant. Lives only in memory;
/// <see cref="Email"/> is PII and is never logged.
/// </summary>
/// <param name="TenantContext">The tenant resolved for the original request.</param>
/// <param name="UserId">The signed-in user the email is for.</param>
/// <param name="Email">That user's address, from the Users row.</param>
/// <param name="IpAddress">Caller IP, for the audit row.</param>
/// <param name="TraceId">Request trace id, for the audit row and log correlation.</param>
public sealed record EmailVerificationWorkItem(
    TenantContext TenantContext,
    Guid UserId,
    string Email,
    string? IpAddress,
    string? TraceId);
