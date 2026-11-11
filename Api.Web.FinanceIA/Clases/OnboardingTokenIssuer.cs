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
/// Issues short-lived anonymous onboarding JWTs scoped to <c>onboarding_anon</c>.
/// These tokens contain only tenant and scope claims — no user identity or roles.
/// </summary>
public sealed class OnboardingTokenIssuer : IOnboardingTokenIssuer
{
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly ILogger<OnboardingTokenIssuer> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="OnboardingTokenIssuer"/>.
    /// </summary>
    /// <param name="jwtOptions">JWT configuration options resolved from <c>Jwt</c> config section.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public OnboardingTokenIssuer(IOptions<JwtOptions> jwtOptions, ILogger<OnboardingTokenIssuer> logger)
    {
        _jwtOptions = jwtOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> IssueAsync(
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
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
            new("scope", "onboarding_anon"),
            new("typ", "onboarding_session")
        };

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
                expires: now.AddMinutes(_jwtOptions.Value.OnboardingTokenMinutes).UtcDateTime,
                signingCredentials: creds);

            var token = new JwtSecurityTokenHandler().WriteToken(jwt);

            _logger.LogInformation(
                "Onboarding JWT issued for tenant {TenantId}",
                tenantContext.TenantId);

            return Task.FromResult(token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate onboarding JWT for tenant {TenantId}",
                tenantContext.TenantId);

            throw new TokenGenerationException("Failed to generate internal JWT.", ex);
        }
    }
}
