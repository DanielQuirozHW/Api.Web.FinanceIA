// Project : Api.Web.FinanceIA
// Created : 2026-10-26

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The TenantSettings read every transactional email shares: the invitation
/// (<see cref="UserInvitationService"/>), the password reset and the email verification. One copy, so
/// the three cannot drift on what "not configured" means or on how a failed read degrades.
/// </summary>
internal static class TenantEmailSettings
{
    /// <summary>
    /// The branding keys every email reads alongside its own: the company name and the logo URL. Both
    /// are optional and have no in-code default; absent, the template field is omitted.
    /// </summary>
    internal static readonly string[] BrandingKeys =
    [
        TenantSettingKeys.BrandingCompanyName,
        TenantSettingKeys.BrandingLogoUrl
    ];

    /// <summary>
    /// Reads <paramref name="keys"/> for the tenant in one query. A blank row means "not configured",
    /// so it is dropped; the rest are trimmed. Fails open: a read error is logged as a Warning and
    /// every value stays on its global default, like every other TenantSettings read.
    /// </summary>
    /// <param name="emailKind">Names the email in the Warning, e.g. <c>"password reset"</c>.</param>
    internal static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        ITenantSettingsRepository repository,
        string tenantId,
        IReadOnlyCollection<string> keys,
        string emailKind,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var values = await repository
                .GetValuesAsync(tenantId, keys, ct)
                .ConfigureAwait(false);

            // A blank row means "not configured", not "configured as empty".
            return values
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "TenantSettings read failed for {EmailKind} in tenant {TenantId}: {ExceptionType}; using global defaults",
                emailKind, tenantId, ex.GetType().Name);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The tenant's <c>branding.logo_url</c> when it is an absolute https URL, else null. Anything
    /// else is ignored with a Warning that names the key, not the value.
    /// </summary>
    internal static string? ResolveLogoUrl(
        IReadOnlyDictionary<string, string> settings,
        string tenantId,
        ILogger logger)
    {
        var raw = settings.GetValueOrDefault(TenantSettingKeys.BrandingLogoUrl);
        if (raw is null)
            return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return raw;

        logger.LogWarning(
            "Ignoring TenantSettings {SettingKey} for tenant {TenantId}: not an absolute https URL",
            TenantSettingKeys.BrandingLogoUrl, tenantId);
        return null;
    }
}
