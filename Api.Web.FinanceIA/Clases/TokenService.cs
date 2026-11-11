// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Issues internal JWT session tokens signed with HMAC-SHA256.
/// Embeds tenant context, user identity, roles and access scope into the resulting token.
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
        string firebaseUid,
        TenantContext tenantContext,
        Guid userId,
        string role,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firebaseUid);
        ArgumentNullException.ThrowIfNull(tenantContext);

        cancellationToken.ThrowIfCancellationRequested();

        var signingKey = _jwtOptions.Value.SigningKey;

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            _logger.LogCritical("JWT SigningKey is not configured");
            throw new TokenGenerationException("JWT signing key is not configured.");
        }

        var claims = new List<Claim>
        {
            new("tenantId", tenantContext.TenantId),
            new("uid", firebaseUid),
            new("sub", userId.ToString()),
            new("userId", userId.ToString()),
            new("azp", "firebase-client"),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("scope", "api_access")
        };

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
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
                firebaseUid);

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
