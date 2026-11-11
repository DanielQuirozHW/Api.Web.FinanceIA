// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using System.Text.Json;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="ISgrSettingsService"/>, over <see cref="ITenantSettingsRepository"/>.
///
/// <para>
/// No cache: TenantSettings reads are per-request (see <see cref="TenantSettingsService"/>), so the GET that
/// follows a PUT — and the next dashboard — reads the stored rows, and there is nothing to invalidate.
/// </para>
/// </summary>
public sealed class SgrSettingsService : ISgrSettingsService
{
    private readonly ITenantSettingsRepository   _repository;
    private readonly IAuditService               _auditService;
    private readonly ILogger<SgrSettingsService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="SgrSettingsService"/>.
    /// </summary>
    public SgrSettingsService(
        ITenantSettingsRepository   repository,
        IAuditService               auditService,
        ILogger<SgrSettingsService> logger)
    {
        _repository   = repository;
        _auditService = auditService;
        _logger       = logger;
    }

    /// <inheritdoc />
    public async Task<SgrFundSettings> GetAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var rows = await _repository
            .GetValuesAsync(tenantId, SgrFundSettings.Keys.ToList(), ct)
            .ConfigureAwait(false);

        var (settings, invalidKeys) = SgrFundSettings.Parse(rows);

        foreach (var key in invalidKeys)
        {
            // The value is operator input of unknown shape: the key is logged, never the value.
            _logger.LogWarning(
                "Tenant setting {SettingKey} of tenant {TenantId} is not a usable value; it is treated as not configured.",
                key, tenantId);
        }

        return settings;
    }

    /// <inheritdoc />
    public async Task<SgrFundSettings> UpdateAsync(
        Guid userId,
        string tenantId,
        UpdateSgrSettingsRequest request,
        DateTime nowUtc,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(request);

        var errors = SgrFundSettings.Validate(request, MaturityBands.AsOf(nowUtc));
        if (errors.Count > 0)
            // No paramName: the message is the Spanish detail the controller returns as is.
            throw new ArgumentException(string.Join(" ", errors));

        // Read BEFORE the write, for the audit trail — after it the old values are gone.
        var before = await GetAsync(tenantId, ct).ConfigureAwait(false);
        var after  = SgrFundSettings.From(request);

        await _repository
            .SetValuesAsync(tenantId, after.ToRows(), ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "SGR Fondo de Riesgo settings updated for TenantId: {TenantId} (exchange rate set: {HasRate})",
            tenantId, after.TipoCambioUsd.HasValue);

        // AuditService owns its try/catch and never propagates, so awaiting it cannot fail the write that
        // already committed above.
        await _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Settings.UpdateSgrFundSettings",
            entityType:   "TenantSettings",
            entityId:     "sgr",
            ipAddress:    ipAddress,
            requestId:    requestId,
            oldValueJson: Serialize(before),
            newValueJson: Serialize(after),
            ct:           ct).ConfigureAwait(false);

        return after;
    }

    private static string Serialize(SgrFundSettings settings)
        => JsonSerializer.Serialize(settings.ToDto(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
