// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases.Filters;

/// <summary>
/// The filter behind <see cref="SetupKeyGateAttribute"/>.
/// <list type="bullet">
/// <item><c>Setup:BootstrapKey</c> unset or shorter than
/// <see cref="SetupOptions.MinimumBootstrapKeyLength"/>: bare 404, the same empty answer as an
/// unmatched route — the endpoint is off.</item>
/// <item><c>X-Setup-Key</c> missing or different: bare 401, with no hint of which.</item>
/// </list>
/// The comparison hashes both values with SHA-256 and compares the digests in constant time, so
/// neither the content nor the length of the key leaks through timing. The key is never logged.
/// </summary>
public sealed class SetupKeyGateFilter : IAsyncResourceFilter
{
    /// <summary>Request header that carries the bootstrap key.</summary>
    public const string HeaderName = "X-Setup-Key";

    private readonly IOptions<SetupOptions> _options;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<SetupKeyGateFilter> _logger;

    public SetupKeyGateFilter(
        IOptions<SetupOptions> options,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<SetupKeyGateFilter> logger)
    {
        _options = options;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var configuredKey = _options.Value.BootstrapKey;

        if (!IsEnabled(configuredKey))
        {
            // Nothing logged: an absent endpoint has nothing to say.
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        var suppliedKey = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (!KeyMatches(configuredKey!, suppliedKey))
        {
            _logger.LogWarning(
                "Setup endpoint refused: invalid or missing setup key. TenantId: {TenantId}",
                _tenantContextAccessor.TenantContext?.TenantId ?? "unresolved");

            context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>True when <paramref name="configuredKey"/> is long enough to turn the endpoint on.</summary>
    internal static bool IsEnabled(string? configuredKey)
        => !string.IsNullOrEmpty(configuredKey)
           && configuredKey.Length >= SetupOptions.MinimumBootstrapKeyLength;

    /// <summary>Constant-time comparison of the configured key and the supplied one.</summary>
    internal static bool KeyMatches(string configuredKey, string? suppliedKey)
    {
        if (string.IsNullOrEmpty(suppliedKey))
            return false;

        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        byte[] actual   = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
