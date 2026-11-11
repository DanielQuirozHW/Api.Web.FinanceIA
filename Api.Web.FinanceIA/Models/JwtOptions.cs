// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// JWT configuration options. SigningKey must be sourced from environment variable
/// or Azure App Configuration — never from appsettings.json in production.
/// </summary>
public sealed record JwtOptions
{
    /// <summary>The JWT issuer claim value.</summary>
    public string Issuer { get; init; } = "https://api.serviciosfinancieros.com";

    /// <summary>The JWT audience claim value.</summary>
    public string Audience { get; init; } = "serviciosfinancieros-api";

    /// <summary>
    /// HMAC-SHA256 signing key. Must be sourced from environment variable
    /// JWT__SIGNINGKEY or Azure App Configuration. Never store in appsettings.json.
    /// </summary>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>Lifetime in minutes for internal session JWTs. Default: 15.</summary>
    public int AccessTokenMinutes { get; init; } = 15;

    /// <summary>Lifetime in minutes for onboarding anonymous JWTs. Default: 10.</summary>
    public int OnboardingTokenMinutes { get; init; } = 10;
}
