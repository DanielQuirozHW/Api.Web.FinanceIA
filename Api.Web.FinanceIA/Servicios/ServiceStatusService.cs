// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using System.Net.Http.Json;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios;

public sealed class ServiceStatusService : IServiceStatusService
{
    private const string ClaudeApiComponentId = "k8w3r06qmzrp";
    private const string CacheKey = "anthropic_status_summary";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FinanciaDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ServiceStatusService> _logger;

    public ServiceStatusService(
        IHttpClientFactory httpClientFactory,
        FinanciaDbContext dbContext,
        IMemoryCache cache,
        ILogger<ServiceStatusService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ServiceStatusResponse> GetStatusAsync(string tenantId, CancellationToken ct)
    {
        var claudeTask = GetClaudePlatformStatusAsync(ct);
        var dbTask = GetDatabaseStatusAsync(ct);
        var lastCallTask = GetLastAiCallAsync(tenantId, ct);

        await Task.WhenAll(claudeTask, dbTask, lastCallTask).ConfigureAwait(false);

        return new ServiceStatusResponse(
            await claudeTask.ConfigureAwait(false),
            await dbTask.ConfigureAwait(false),
            await lastCallTask.ConfigureAwait(false));
    }

    private async Task<ClaudePlatformStatus> GetClaudePlatformStatusAsync(CancellationToken ct)
    {
        try
        {
            var summary = await _cache.GetOrCreateAsync(CacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);

                var client = _httpClientFactory.CreateClient("AnthropicStatusClient");
                return await client
                    .GetFromJsonAsync<StatuspageSummary>("/api/v2/summary.json", ct)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (summary is null)
                return UnknownPlatformStatus();

            var claudeComponent = summary.Components
                .FirstOrDefault(c => c.Id == ClaudeApiComponentId);

            var affectedComponents = claudeComponent is not null &&
                                     claudeComponent.Status != "operational"
                ? [new AffectedComponent(claudeComponent.Name, claudeComponent.Status)]
                : new List<AffectedComponent>();

            var activeIncidents = summary.Incidents
                .Where(i => i.Status != "resolved" && i.Status != "postmortem")
                .Select(i => new ActiveIncident(
                    i.Name,
                    i.Impact,
                    i.IncidentUpdates.FirstOrDefault()?.Body,
                    i.UpdatedAt))
                .ToList();

            var indicator = claudeComponent?.Status switch
            {
                "operational" => "none",
                "degraded_performance" => "minor",
                "partial_outage" => "major",
                "major_outage" => "critical",
                _ => summary.Status.Indicator
            };

            return new ClaudePlatformStatus(
                indicator,
                summary.Status.Description,
                affectedComponents,
                activeIncidents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Anthropic status page");
            return UnknownPlatformStatus();
        }
    }

    private async Task<DatabaseStatus> GetDatabaseStatusAsync(CancellationToken ct)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(ct).ConfigureAwait(false);
            return new DatabaseStatus(
                canConnect ? "ok" : "error",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database connectivity check failed");
            return new DatabaseStatus("error", DateTime.UtcNow);
        }
    }

    private async Task<LastAiCallStatus?> GetLastAiCallAsync(string tenantId, CancellationToken ct)
    {
        try
        {
            var lastCall = await _dbContext.ApiCallLogs
                .Where(x => x.TenantId == tenantId &&
                            (x.Service == "Claude" || x.Service == "ClaudeChat"))
                .OrderByDescending(x => x.CalledAt)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (lastCall is null)
                return null;

            return new LastAiCallStatus(
                lastCall.IsSuccess ? "ok" : "error",
                lastCall.CalledAt,
                lastCall.Operation,
                lastCall.Model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query last AI call for tenant {TenantId}", tenantId);
            return null;
        }
    }

    private static ClaudePlatformStatus UnknownPlatformStatus() =>
        new("unknown", "Unable to retrieve status", [], []);
}
