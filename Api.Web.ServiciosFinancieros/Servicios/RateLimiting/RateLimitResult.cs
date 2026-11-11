// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-30
// Modified: 2026-04-30

namespace Api.Web.ServiciosFinancieros.Servicios.RateLimiting;

/// <summary>
/// Encapsulates the outcome of a rate limit check, including headers data and error details.
/// </summary>
public sealed class RateLimitResult
{
    public bool    IsAllowed         { get; init; }
    public string? ErrorCode         { get; init; }
    public string? ErrorMessage      { get; init; }
    public int?    RetryAfterSeconds { get; init; }

    public int  UserLimit          { get; init; }
    public int  UserRemaining      { get; init; }
    public long UserResetTimestamp { get; init; }

    public int?  TenantLimit          { get; init; }
    public int?  TenantRemaining      { get; init; }
    public long? TenantResetTimestamp { get; init; }
}
