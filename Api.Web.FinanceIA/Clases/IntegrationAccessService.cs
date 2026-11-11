// Project : Api.Web.FinanceIA
// Created : 2026-10-24

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IIntegrationAccessService"/>. Registered Scoped, like the
/// <see cref="ITenantProfileService"/> it reads through (which memoises the profile per request).
/// </summary>
/// <remarks>
/// Reads <see cref="IConfiguration"/> directly rather than an options type because the LUFE key
/// lives under a per-tenant path (<c>Tenants:{tenantId}:LufeApiKey</c>). The NOSIS and CASFOG key
/// names are borrowed from <see cref="NosisService"/> and <see cref="CasfogService"/>, so the gate
/// here and the gate inside each service cannot name different settings. No value is ever logged.
/// </remarks>
public sealed class IntegrationAccessService : IIntegrationAccessService
{
    /// <summary>
    /// The profile every gate falls back to when the tenant's real one cannot be trusted: a
    /// degraded read (<see cref="TenantProfileResolution.IsDegraded"/>) or an exception out of
    /// the resolver. Every optional integration is ON, spelled out rather than borrowed from
    /// <see cref="TenantProfilePresets.Default"/>, so the fail-open guarantee does not silently
    /// follow a preset whose flags may change. Dropping an integration because a settings SELECT
    /// blipped would remove enrichment data from an analysis without anyone noticing.
    /// </summary>
    private static readonly TenantProfile FailOpenProfile =
        TenantProfilePresets.Default with { LufeEnabled = true, CasfogEnabled = true, NosisEnabled = true };

    private readonly ITenantProfileService             _tenantProfileService;
    private readonly IConfiguration                    _configuration;
    private readonly ILogger<IntegrationAccessService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="IntegrationAccessService"/>.
    /// </summary>
    public IntegrationAccessService(
        ITenantProfileService             tenantProfileService,
        IConfiguration                    configuration,
        ILogger<IntegrationAccessService> logger)
    {
        _tenantProfileService = tenantProfileService;
        _configuration        = configuration;
        _logger               = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The ONE profile read a caller makes: every gate in that caller takes its answer from the
    /// returned <see cref="TenantProfile"/>, so a request cannot ask twice and cannot get two
    /// different answers.
    /// </para>
    /// <para>
    /// <c>GetEffectiveAsync</c> is documented never to throw; the wrap is belt and braces, for the
    /// same reason <c>AnalysisController</c> wraps the billing read — a contract is not the place
    /// to take a tenant's enrichment data on faith. A profile that legitimately disables an
    /// integration (<see cref="TenantProfileSource.Configured"/> or
    /// <see cref="TenantProfileSource.NoConfiguration"/>) is the tenant's declared state, not an
    /// anomaly, and the caller logs it at Information.
    /// </para>
    /// </remarks>
    public async Task<TenantProfile> ResolveEntitlementsAsync(string tenantId, CancellationToken ct)
    {
        TenantProfileResolution resolution;

        try
        {
            resolution = await _tenantProfileService
                .GetEffectiveAsync(tenantId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Tenant profile could not be resolved for tenant {TenantId}; treating LUFE, CASFOG and NOSIS as enabled.",
                tenantId);

            return FailOpenProfile;
        }

        if (resolution.IsDegraded)
        {
            // TenantProfileService already logged the failed read at Warning; a second Warning per
            // consumer would count one blip several times. Information records the decision only.
            _logger.LogInformation(
                "Tenant profile for tenant {TenantId} is degraded (settings could not be read); treating LUFE, CASFOG and NOSIS as enabled.",
                tenantId);

            return FailOpenProfile;
        }

        return resolution.Profile;
    }

    /// <inheritdoc />
    public string? GetLufeApiKey(string tenantId) =>
        _configuration[$"Tenants:{tenantId}:LufeApiKey"];

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors the gate inside <see cref="NosisService"/> so a caller can report "not turned on" as
    /// a 400 instead of letting it arrive as a 502 that looks like an outage. It checks
    /// <c>Nosis:VR</c> and NOT <c>Nosis:VrFiador</c> on purpose: the fiador group falls back to
    /// <c>Nosis:VR</c>, so VR present is exactly the condition under which any lookup can succeed.
    /// </remarks>
    public bool IsNosisConfigured() =>
        _configuration.GetValue<bool>(NosisService.ConfigEnabledKey)
        && !string.IsNullOrWhiteSpace(_configuration[NosisService.ConfigApiKeyKey])
        && !string.IsNullOrWhiteSpace(_configuration[NosisService.ConfigVrKey]);

    /// <inheritdoc />
    public bool IsCasfogConfigured() =>
        !string.IsNullOrWhiteSpace(_configuration[CasfogService.ConfigEmailKey])
        && !string.IsNullOrWhiteSpace(_configuration[CasfogService.ConfigPasswordKey]);

    /// <inheritdoc />
    public async Task<OptionalIntegrationAccess> ResolveUsableAsync(string tenantId, CancellationToken ct)
    {
        var profile = await ResolveEntitlementsAsync(tenantId, ct).ConfigureAwait(false);

        return new OptionalIntegrationAccess(
            Lufe:   profile.LufeEnabled && !string.IsNullOrWhiteSpace(GetLufeApiKey(tenantId)),
            Casfog: profile.CasfogEnabled && IsCasfogConfigured(),
            Nosis:  profile.NosisEnabled && IsNosisConfigured());
    }
}
