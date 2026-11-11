# Tool: Scaffold Controller — Api.Web.ServiciosFinancieros

## Purpose
Generate a complete, production-ready API controller following 
all project standards. Includes route definition, DI injection,
tenant context access, error handling and response patterns.

## Usage
claude tool scaffold-controller {ControllerName} {scope}

Examples:
  claude tool scaffold-controller Factura authenticated
  claude tool scaffold-controller Onboarding unauthenticated
  claude tool scaffold-controller Admin admin

## Parameters
- ControllerName: PascalCase name without "Controller" suffix
- scope:
  - authenticated: requires Internal JWT, accesses TenantContext
  - unauthenticated: reCAPTCHA protected, no JWT required
  - admin: requires AdminClient policy, elevated permissions

## Template: authenticated scope

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Api.Web.ServiciosFinancieros.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public sealed class {ControllerName}Controller : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<{ControllerName}Controller> _logger;

    public {ControllerName}Controller(
        ITenantContextAccessor tenantContextAccessor,
        ILogger<{ControllerName}Controller> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    [HttpGet]
    [EnableRateLimiting("PerUserOrIp")]
    public async Task<IActionResult> GetAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantContext = _tenantContextAccessor.TenantContext;

            _logger.LogInformation(
                "{Controller} GET called for tenant {TenantId}",
                nameof({ControllerName}Controller),
                tenantContext.TenantId);

            // TODO: implement business logic

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Controller} GET failed for tenant {TenantId}",
                nameof({ControllerName}Controller),
                _tenantContextAccessor.TenantContext?.TenantId ?? "unknown");

            return StatusCode(500, new ProblemDetails
            {
                Status = 500,
                Title = "Internal Server Error",
                Detail = "An unexpected error occurred."
            });
        }
    }
}

## Template: unauthenticated scope

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Api.Web.ServiciosFinancieros.Controllers;

[Route("api/[controller]")]
[ApiController]
public sealed class {ControllerName}Controller : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IRecaptchaVerifier _recaptchaVerifier;
    private readonly ILogger<{ControllerName}Controller> _logger;

    public {ControllerName}Controller(
        ITenantContextAccessor tenantContextAccessor,
        IRecaptchaVerifier recaptchaVerifier,
        ILogger<{ControllerName}Controller> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _recaptchaVerifier = recaptchaVerifier;
        _logger = logger;
    }

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("PerUserOrIp")]
    public async Task<IActionResult> PostAsync(
        [FromBody] {ControllerName}Request request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantContext = _tenantContextAccessor.TenantContext;

            var recaptchaResult = await _recaptchaVerifier.VerifyAsync(
                tenantContext.RecaptchaSecretKey,
                request.RecaptchaToken,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);

            if (!recaptchaResult.Success || recaptchaResult.Score < 0.5)
            {
                _logger.LogWarning(
                    "reCAPTCHA validation failed for tenant {TenantId}. Score: {Score}",
                    tenantContext.TenantId,
                    recaptchaResult.Score);

                return StatusCode(403, new ProblemDetails
                {
                    Status = 403,
                    Title = "Forbidden",
                    Detail = "reCAPTCHA validation failed."
                });
            }

            // TODO: implement business logic

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Controller} POST failed for tenant {TenantId}",
                nameof({ControllerName}Controller),
                _tenantContextAccessor.TenantContext?.TenantId ?? "unknown");

            return StatusCode(500, new ProblemDetails
            {
                Status = 500,
                Title = "Internal Server Error",
                Detail = "An unexpected error occurred."
            });
        }
    }
}

## Template: admin scope

// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}
using Api.Web.ServiciosFinancieros.Clases;
using Api.Web.ServiciosFinancieros.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Api.Web.ServiciosFinancieros.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = "AdminClient")]
public sealed class {ControllerName}Controller : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<{ControllerName}Controller> _logger;

    public {ControllerName}Controller(
        ITenantContextAccessor tenantContextAccessor,
        ILogger<{ControllerName}Controller> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    [HttpGet]
    [EnableRateLimiting("PerUserOrIp")]
    public async Task<IActionResult> GetAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantContext = _tenantContextAccessor.TenantContext;

            _logger.LogInformation(
                "Admin {Controller} GET called for tenant {TenantId}",
                nameof({ControllerName}Controller),
                tenantContext.TenantId);

            // TODO: implement business logic

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Admin {Controller} GET failed for tenant {TenantId}",
                nameof({ControllerName}Controller),
                _tenantContextAccessor.TenantContext?.TenantId ?? "unknown");

            return StatusCode(500, new ProblemDetails
            {
                Status = 500,
                Title = "Internal Server Error",
                Detail = "An unexpected error occurred."
            });
        }
    }
}

## Rules Applied by This Tool
- Always sealed class
- Always explicit route attribute
- Always ILogger<T> injected
- Always ITenantContextAccessor injected
- Always EnableRateLimiting attribute
- Always structured logging with TenantId
- Always try/catch with ProblemDetails error response
- Always CancellationToken parameter on async methods
- Never business logic inside controller — delegate to services
- Never direct DbContext injection in controllers
- Never hardcoded tenant names or client IDs
- Request and response models always defined as records in Models/

## Post-Generation Steps
After generating the controller, remind developer to:
1. Add the service dependency to DI in Program.cs
2. Create the corresponding request/response records in Models/
3. Create the corresponding service interface in Clases/
4. Register the service in Program.cs