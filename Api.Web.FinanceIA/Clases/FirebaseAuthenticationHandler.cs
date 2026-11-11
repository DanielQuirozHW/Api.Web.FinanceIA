// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AppFirebaseAuthException = Api.Web.FinanceIA.Clases.Exceptions.FirebaseAuthException;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// ASP.NET Core authentication handler that validates Firebase ID tokens
/// against the tenant-specific <see cref="FirebaseAdmin.Auth.FirebaseAuth"/> instance
/// obtained from <see cref="ITenantFirebaseRegistry"/>.
/// Registered under the <see cref="SchemeName"/> scheme and used by the SmartJwt
/// PolicyScheme to route Firebase Bearer tokens to this handler.
/// Never uses <see cref="FirebaseAdmin.Auth.FirebaseAuth.DefaultInstance"/>.
/// </summary>
public sealed class FirebaseAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IFirebaseTokenValidator firebaseTokenValidator,
    ITenantContextAccessor tenantContextAccessor)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The authentication scheme name used for Firebase Bearer token validation.</summary>
    public const string SchemeName = "FirebaseBearer";

    private readonly IFirebaseTokenValidator _firebaseTokenValidator = firebaseTokenValidator;
    private readonly ITenantContextAccessor _tenantContextAccessor = tenantContextAccessor;

    /// <summary>
    /// Attempts to authenticate the current request by extracting and validating
    /// the Firebase Bearer token from the Authorization header.
    /// Returns <see cref="AuthenticateResult.NoResult"/> when no Bearer token is present,
    /// <see cref="AuthenticateResult.Fail"/> when validation fails, and
    /// <see cref="AuthenticateResult.Success"/> on a valid token.
    /// Never includes exception details in failure messages.
    /// </summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Step 1 — Extract Bearer token from Authorization header
        var authorizationHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        // Step 2 — Get TenantContext
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            Logger.LogWarning("TenantContext not available in FirebaseAuthenticationHandler");
            return AuthenticateResult.Fail("Tenant context not resolved.");
        }

        // Step 3 — Validate Firebase token
        try
        {
            var firebaseToken = await _firebaseTokenValidator
                .ValidateAsync(token, tenantContext, Context.RequestAborted)
                .ConfigureAwait(false);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, firebaseToken.Uid),
                new Claim("uid", firebaseToken.Uid),
                new Claim("tenantId", tenantContext.TenantId),
            };

            if (firebaseToken.Claims.GetValueOrDefault("email") is string email)
            {
                claims.Add(new Claim(ClaimTypes.Email, email));
            }

            if (firebaseToken.Claims.GetValueOrDefault("email_verified")?.ToString() is string emailVerified)
            {
                claims.Add(new Claim("email_verified", emailVerified));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        catch (AppFirebaseAuthException)
        {
            Logger.LogWarning(
                "Firebase authentication failed for tenant {TenantId}",
                tenantContext.TenantId);

            return AuthenticateResult.Fail("Invalid or expired token.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Unexpected error in FirebaseAuthenticationHandler for tenant {TenantId}",
                tenantContext.TenantId);

            return AuthenticateResult.Fail("Authentication error.");
        }
    }
}
