// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Response model containing a JWT access token.
/// </summary>
public sealed record AuthTokenResponse
{
    /// <summary>Signed internal JWT. Also set as an httpOnly cookie named <c>bff_session</c>.</summary>
    public string Token { get; init; } = string.Empty;
}
