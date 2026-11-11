// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="AlertRepository.SupersedePreviousEntityAlertsAsync"/>, the call
/// AnalysisPipeline makes before inserting a new analysis's alerts, on the EF Core in-memory provider.
///
/// <para>
/// The point here is what it must NOT touch: the cupo event alerts are about the entity's cupo, not
/// about the previous analysis, so a re-analysis resolving them would hide an expired operation or a
/// full cupo that is still there.
/// </para>
/// </summary>
public sealed class AlertRepositorySupersedeTests
{
    private const string Tenant = "tenant-1";

    private static readonly DateTime CreatedAt = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static FinanciaDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new FinanciaDbContext(options);
    }

    private static Alert EntityAlert(Guid entityId, string alertType, Guid? analysisId) => new()
    {
        AlertId        = Guid.NewGuid(),
        TenantId       = Tenant,
        EntityId       = entityId,
        AnalysisId     = analysisId,
        AlertType      = alertType,
        Severity       = "High",
        Title          = "t",
        Description    = "d",
        MetricKey      = EventAlertTypes.Cupo.Contains(alertType) ? EventAlertTypes.CupoMetricKey(Guid.NewGuid())
                       : EventAlertTypes.Pyme.Contains(alertType) ? EventAlertTypes.EntityMetricKey(entityId)
                       : string.Empty,
        MetricValue    = 0m,
        ThresholdValue = 0m,
        CreatedAt      = CreatedAt
    };

    [Fact]
    public async Task Supersede_ResolvesTheEntitysAnalysisAlerts_ButLeavesCupoAlertsOpen()
    {
        var name             = $"alerts-supersede-{Guid.NewGuid()}";
        var entityId         = Guid.NewGuid();
        var previousAnalysis = Guid.NewGuid();
        var newAnalysis      = Guid.NewGuid();

        var finding  = EntityAlert(entityId, "HIGH_LEVERAGE", previousAnalysis);
        var expired  = EntityAlert(entityId, EventAlertTypes.CupoOperationsExpired, previousAnalysis);
        var full     = EntityAlert(entityId, EventAlertTypes.CupoUtilizationHigh, null);

        await using (var seed = NewContext(name))
        {
            seed.Alerts.AddRange(finding, expired, full);
            seed.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
            await seed.SaveChangesAsync();
        }

        int superseded;
        await using (var ctx = NewContext(name))
        {
            superseded = await new AlertRepository(ctx, NullLogger<AlertRepository>.Instance)
                .SupersedePreviousEntityAlertsAsync(entityId, Tenant, newAnalysis, CancellationToken.None);
        }

        Assert.Equal(1, superseded);

        await using var read = NewContext(name);
        var byId = await read.Alerts.AsNoTracking().ToDictionaryAsync(a => a.AlertId);
        Assert.True(byId[finding.AlertId].IsResolved);
        Assert.False(byId[expired.AlertId].IsResolved);
        Assert.False(byId[full.AlertId].IsResolved);
    }

    [Fact]
    public async Task Supersede_LeavesEveryEventAlertTypeOpen()
    {
        var name             = $"alerts-supersede-all-{Guid.NewGuid()}";
        var entityId         = Guid.NewGuid();
        var previousAnalysis = Guid.NewGuid();

        var finding = EntityAlert(entityId, "HIGH_LEVERAGE", previousAnalysis);
        var events  = EventAlertTypes.All.Select(type => EntityAlert(entityId, type, previousAnalysis)).ToList();

        await using (var seed = NewContext(name))
        {
            seed.Alerts.Add(finding);
            seed.Alerts.AddRange(events);
            seed.ChangeTracker.DetectChanges();
            await seed.SaveChangesAsync();
        }

        await using (var ctx = NewContext(name))
        {
            await new AlertRepository(ctx, NullLogger<AlertRepository>.Instance)
                .SupersedePreviousEntityAlertsAsync(entityId, Tenant, Guid.NewGuid(), CancellationToken.None);
        }

        await using var read = NewContext(name);
        var byId = await read.Alerts.AsNoTracking().ToDictionaryAsync(a => a.AlertId);
        Assert.True(byId[finding.AlertId].IsResolved);
        Assert.Equal(5, events.Count);
        Assert.All(events, e => Assert.False(byId[e.AlertId].IsResolved, e.AlertType));
    }

    [Fact]
    public async Task Supersede_TouchesOnlyThisTenantAndEntitysActiveAlertsOfOtherAnalyses()
    {
        var name             = $"alerts-supersede-scope-{Guid.NewGuid()}";
        var entityId         = Guid.NewGuid();
        var otherEntityId    = Guid.NewGuid();
        var previousAnalysis = Guid.NewGuid();
        var newAnalysis      = Guid.NewGuid();
        var resolvedAt       = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var resolvedBy       = Guid.NewGuid();

        var finding       = EntityAlert(entityId, "HIGH_LEVERAGE", previousAnalysis);
        var otherTenant   = EntityAlert(entityId, "HIGH_LEVERAGE", previousAnalysis);
        otherTenant.TenantId = "tenant-2";
        var otherEntity   = EntityAlert(otherEntityId, "HIGH_LEVERAGE", previousAnalysis);
        var ownAlert      = EntityAlert(entityId, "LOW_LIQUIDITY", newAnalysis);
        var userResolved  = EntityAlert(entityId, "LOW_MARGIN", previousAnalysis);
        userResolved.IsResolved = true;
        userResolved.ResolvedAt = resolvedAt;
        userResolved.ResolvedBy = resolvedBy;

        await using (var seed = NewContext(name))
        {
            seed.Alerts.AddRange(finding, otherTenant, otherEntity, ownAlert, userResolved);
            seed.ChangeTracker.DetectChanges();
            await seed.SaveChangesAsync();
        }

        int superseded;
        await using (var ctx = NewContext(name))
        {
            superseded = await new AlertRepository(ctx, NullLogger<AlertRepository>.Instance)
                .SupersedePreviousEntityAlertsAsync(entityId, Tenant, newAnalysis, CancellationToken.None);
        }

        Assert.Equal(1, superseded);

        await using var read = NewContext(name);
        var byId = await read.Alerts.AsNoTracking().ToDictionaryAsync(a => a.AlertId);
        Assert.True(byId[finding.AlertId].IsResolved);
        Assert.Null(byId[finding.AlertId].ResolvedBy);
        Assert.False(byId[otherTenant.AlertId].IsResolved);
        Assert.False(byId[otherEntity.AlertId].IsResolved);
        Assert.False(byId[ownAlert.AlertId].IsResolved);
        Assert.Equal(resolvedAt, byId[userResolved.AlertId].ResolvedAt);
        Assert.Equal(resolvedBy, byId[userResolved.AlertId].ResolvedBy);
    }
}
