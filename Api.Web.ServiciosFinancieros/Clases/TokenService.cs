// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Api.Web.ServiciosFinancieros.Clases.Exceptions;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Clases;

/// <summary>
/// Issues internal JWT session tokens signed with HMAC-SHA256.
/// Extracts identity claims from a <see cref="ClaimsPrincipal"/> and embeds
/// tenant context, user identity, roles and access scope into the resulting token.
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly ILogger<TokenService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="TokenService"/>.
    /// </summary>
    /// <param name="jwtOptions">JWT configuration options resolved from <c>Jwt</c> config section.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public TokenService(IOptions<JwtOptions> jwtOptions, ILogger<TokenService> logger)
    {
        _jwtOptions = jwtOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> IssueAsync(
        ClaimsPrincipal principal,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(tenantContext);

        cancellationToken.ThrowIfCancellationRequested();

        var signingKey = _jwtOptions.Value.SigningKey;

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            _logger.LogCritical("JWT SigningKey is not configured");
            throw new TokenGenerationException("JWT signing key is not configured.");
        }

        var uid = principal.FindFirst("uid")?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? string.Empty;

        var claims = new List<Claim>
        {
            new("tenantId", tenantContext.TenantId),
            new("uid", uid),
            new("sub", uid),
            new("azp", "firebase-client"),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("scope", "api_access")
        };

        foreach (var roleClaim in principal.FindAll("roles"))
        {
            claims.Add(new Claim("roles", roleClaim.Value));
        }

        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var now = DateTimeOffset.UtcNow;
            var jwt = new JwtSecurityToken(
                issuer: _jwtOptions.Value.Issuer,
                audience: _jwtOptions.Value.Audience,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: now.AddMinutes(_jwtOptions.Value.AccessTokenMinutes).UtcDateTime,
                signingCredentials: creds);

            var token = new JwtSecurityTokenHandler().WriteToken(jwt);

            _logger.LogInformation(
                "Internal JWT issued for tenant {TenantId} uid {Uid}",
                tenantContext.TenantId,
                uid);

            return Task.FromResult(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate JWT for tenant {TenantId}",
                tenantContext.TenantId);

            throw new TokenGenerationException("Failed to generate internal JWT.", ex);
        }
    }
}
