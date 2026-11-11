// Project : Api.Web.FinanceIA
// Created : 2026-06-08

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// User-scoped self-service endpoints for the currently authenticated user.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class MeController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<MeController> _logger;

    public MeController(
        IUserRepository userRepository,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<MeController> logger)
    {
        _userRepository = userRepository;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Returns the onboarding flags for the authenticated user.
    /// </summary>
    [HttpGet("onboarding-flags")]
    [ProducesResponseType(typeof(OnboardingFlagsResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetOnboardingFlagsAsync(CancellationToken cancellationToken)
    {
        var result = await ResolveUserAsync(cancellationToken).ConfigureAwait(false);
        if (result.Error is not null) return result.Error;

        var flags = ParseFlags(result.User!.OnboardingFlagsJson);
        return Ok(new OnboardingFlagsResponse { Flags = flags });
    }

    /// <summary>
    /// Merges the supplied flags into the user's stored onboarding flags.
    /// Keys present in the request are upserted; existing keys not in the request are preserved.
    /// </summary>
    [HttpPatch("onboarding-flags")]
    [ProducesResponseType(typeof(OnboardingFlagsResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> UpdateOnboardingFlagsAsync(
        [FromBody] UpdateOnboardingFlagsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ResolveUserAsync(cancellationToken).ConfigureAwait(false);
        if (result.Error is not null) return result.Error;

        var user = result.User!;
        var stored = ParseFlags(user.OnboardingFlagsJson);

        foreach (var kvp in request.Flags)
            stored[kvp.Key] = kvp.Value;

        var json = JsonSerializer.Serialize(stored);
        await _userRepository.UpdateOnboardingFlagsAsync(user.UserId, json, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Onboarding flags updated for user {UserId} in tenant {TenantId}",
            user.UserId,
            user.TenantId);

        return Ok(new OnboardingFlagsResponse { Flags = stored });
    }

    private async Task<(User? User, IActionResult? Error)> ResolveUserAsync(CancellationToken ct)
    {
        var uid = User.FindFirst("uid")?.Value;
        if (string.IsNullOrWhiteSpace(uid))
            return (null, Unauthorized());

        var user = await _userRepository.GetByFirebaseUidAsync(uid, ct).ConfigureAwait(false);

        if (user is null)
        {
            return (null, Problem(
                statusCode: 404,
                title: "Not Found",
                detail: "No se encontró el perfil del usuario."));
        }

        if (!user.IsActive)
        {
            return (null, Problem(
                statusCode: 403,
                title: "Forbidden",
                detail: "La cuenta está inactiva."));
        }

        return (user, null);
    }

    private static Dictionary<string, bool> ParseFlags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, bool>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
                   ?? new Dictionary<string, bool>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, bool>();
        }
    }
}
