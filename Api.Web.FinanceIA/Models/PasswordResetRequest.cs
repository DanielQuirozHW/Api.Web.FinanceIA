// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Body of the anonymous POST api/bff/auth/password-reset. The tenant comes from the
/// X-Tenant-ID header, never from the body.
/// </summary>
public sealed record PasswordResetRequest
{
    /// <summary>The address the analyst typed on the Portal's /recover step 1.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>reCAPTCHA V3 token issued by the frontend Google reCAPTCHA widget.</summary>
    public string RecaptchaToken { get; init; } = string.Empty;
}
