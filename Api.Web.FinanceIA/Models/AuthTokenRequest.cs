// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Request model for unauthenticated token issuance.
/// Contains reCAPTCHA V3 token for bot protection.
/// </summary>
public sealed record AuthTokenRequest
{
    /// <summary>reCAPTCHA V3 token issued by the frontend Google reCAPTCHA widget.</summary>
    public string RecaptchaToken { get; init; } = string.Empty;
}
