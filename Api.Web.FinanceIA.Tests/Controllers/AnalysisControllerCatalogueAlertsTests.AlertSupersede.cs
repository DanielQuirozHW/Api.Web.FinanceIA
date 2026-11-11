// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The alert-persistence block of <see cref="AnalysisPipeline"/>, driven end to end through
/// <see cref="Api.Web.FinanceIA.Controllers.AnalysisController.TriggerAsync"/> over a real
/// <see cref="AlertRepository"/> on the EF Core in-memory provider.
///
/// <para>
/// The rule: a KNOWN model answer about alerts (a JSON array, empty included) supersedes the entity's
/// previous active alerts; an UNKNOWN one (null, blank, the literal null, unreadable) leaves them alone.
/// Before the fix an empty list was treated like unknown, so a re-analysis with no alerts left the
/// previous analysis's alerts active on the entity.
/// </para>
/// </summary>
public sealed partial class AnalysisControllerCatalogueAlertsTests
{
    private static readonly Guid PreviousAnalysisId = Guid.Parse("0b6a3f7e-2d41-4c9a-9e85-5f1c2a7d4e30");

    private static FinanciaDbContext NewAlertsContext(string name) =>
        new(new DbContextOptionsBuilder<FinanciaDbContext>().UseInMemoryDatabase(name).Options);

    private static Alert PreviousAlert(string tenantId, Guid entityId) => new()
    {
        AlertId        = Guid.NewGuid(),
        TenantId       = tenantId,
        EntityId       = entityId,
        AnalysisId     = PreviousAnalysisId,
        AlertType      = "ALERTA_ANTERIOR",
        Severity       = "High",
        Title          = "Alerta anterior",
        Description    = "Texto de la alerta anterior.",
        MetricKey      = string.Empty,
        MetricValue    = 0m,
        ThresholdValue = 0m,
        IsRead         = false,
        IsResolved     = false,
        CreatedAt      = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    private static async Task SeedAsync(string name, params Alert[] alerts)
    {
        await using var seed = NewAlertsContext(name);
        seed.Alerts.AddRange(alerts);
        seed.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await seed.SaveChangesAsync();
    }

    private static async Task<Dictionary<Guid, Alert>> ReadAlertsAsync(string name)
    {
        await using var read = NewAlertsContext(name);
        return await read.Alerts.AsNoTracking().ToDictionaryAsync(a => a.AlertId);
    }

    private static async Task<(IActionResult Response, AnalysisResult? Created, StubDocumentRepository Docs)> RunAsync(
        IAlertRepository alertRepository, string? alertsJson)
    {
        var documentId = Guid.NewGuid();
        var docs       = new StubDocumentRepository();
        var (sut, results) = BuildSut(
            new StubAlertRuleCatalogueRepository(), documentId,
            alertRepository:    alertRepository,
            overrideAlertsJson: true,
            alertsJson:         alertsJson,
            documentRepository: docs);

        var response = await sut.TriggerAsync(BuildRequest(documentId), CancellationToken.None);
        return (response, results.Created, docs);
    }

    // ── Valid empty list ──────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenModelReportsNoAlerts_SupersedesPreviousAlertsAndInsertsNothing()
    {
        var name     = $"pipeline-alerts-empty-{Guid.NewGuid()}";
        var previous = PreviousAlert(Tenant, FixtureEntityId);
        await SeedAsync(name, previous);

        await using var ctx = NewAlertsContext(name);
        var (response, created, docs) = await RunAsync(
            new AlertRepository(ctx, NullLogger<AlertRepository>.Instance), "[]");

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.NotNull(created);
        Assert.Contains("Completed", docs.StatusWrites);

        var byId = await ReadAlertsAsync(name);
        Assert.Single(byId);
        Assert.True(byId[previous.AlertId].IsResolved);
        Assert.NotNull(byId[previous.AlertId].ResolvedAt);
        Assert.Null(byId[previous.AlertId].ResolvedBy); // system auto-resolution
    }

    [Fact]
    public async Task TriggerAsync_WhenModelReportsNoAlerts_LeavesOtherTenantsAndOtherEntitiesAlone()
    {
        var name        = $"pipeline-alerts-scope-{Guid.NewGuid()}";
        var previous    = PreviousAlert(Tenant, FixtureEntityId);
        var otherTenant = PreviousAlert("tenant-2", FixtureEntityId);
        var otherEntity = PreviousAlert(Tenant, Guid.NewGuid());
        await SeedAsync(name, previous, otherTenant, otherEntity);

        await using var ctx = NewAlertsContext(name);
        await RunAsync(new AlertRepository(ctx, NullLogger<AlertRepository>.Instance), "[]");

        var byId = await ReadAlertsAsync(name);
        Assert.Equal(3, byId.Count);
        Assert.True(byId[previous.AlertId].IsResolved);
        Assert.False(byId[otherTenant.AlertId].IsResolved);
        Assert.False(byId[otherEntity.AlertId].IsResolved);
    }

    // ── Unknown answer ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""[{"level":"high",""")]
    [InlineData("null")]
    public async Task TriggerAsync_WhenAlertsAreUnknown_LeavesPreviousAlertsActive(string? alertsJson)
    {
        var name     = $"pipeline-alerts-unknown-{Guid.NewGuid()}";
        var previous = PreviousAlert(Tenant, FixtureEntityId);
        await SeedAsync(name, previous);

        await using var ctx = NewAlertsContext(name);
        var (response, created, docs) = await RunAsync(
            new AlertRepository(ctx, NullLogger<AlertRepository>.Instance), alertsJson);

        Assert.IsType<CreatedAtActionResult>(response);
        Assert.NotNull(created);
        Assert.Contains("Completed", docs.StatusWrites);

        var byId = await ReadAlertsAsync(name);
        Assert.Single(byId);
        Assert.False(byId[previous.AlertId].IsResolved);
        Assert.Null(byId[previous.AlertId].ResolvedAt);
    }

    // ── Non-empty list: today's behaviour ─────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenModelReportsAlerts_SupersedesPreviousAndInsertsTheNewOnes()
    {
        var name     = $"pipeline-alerts-nonempty-{Guid.NewGuid()}";
        var previous = PreviousAlert(Tenant, FixtureEntityId);
        await SeedAsync(name, previous);

        await using var ctx = NewAlertsContext(name);
        var (_, created, _) = await RunAsync(
            new AlertRepository(ctx, NullLogger<AlertRepository>.Instance), ModelAlertsJson);

        Assert.NotNull(created);
        var byId = await ReadAlertsAsync(name);
        Assert.Equal(2, byId.Count);
        Assert.True(byId[previous.AlertId].IsResolved);

        var inserted = Assert.Single(byId.Values, a => a.AlertId != previous.AlertId);
        Assert.Equal(created!.AnalysisId, inserted.AnalysisId);
        Assert.Equal(Tenant, inserted.TenantId);
        Assert.Equal(FixtureEntityId, inserted.EntityId);
        Assert.Equal("OBSERVACION_DEL_MODELO", inserted.AlertType);
        Assert.Equal("Medium", inserted.Severity);
        Assert.False(inserted.IsResolved);
    }

    // ── Fault isolation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("[]", 0)]
    [InlineData(ModelAlertsJson, 1)]
    public async Task TriggerAsync_WhenSupersedeThrows_AnalysisStillCompletesAndNewAlertsAreCreated(
        string alertsJson, int expectedInserted)
    {
        var name     = $"pipeline-alerts-throw-{Guid.NewGuid()}";
        var previous = PreviousAlert(Tenant, FixtureEntityId);
        await SeedAsync(name, previous);

        await using var ctx = NewAlertsContext(name);
        var repo = new ThrowingSupersedeAlertRepository(
            new AlertRepository(ctx, NullLogger<AlertRepository>.Instance));

        var (response, created, docs) = await RunAsync(repo, alertsJson);

        Assert.Equal(1, repo.SupersedeCalls);
        Assert.IsType<CreatedAtActionResult>(response);
        Assert.NotNull(created);
        Assert.Contains("Completed", docs.StatusWrites);

        var byId = await ReadAlertsAsync(name);
        Assert.False(byId[previous.AlertId].IsResolved);
        Assert.Equal(expectedInserted, byId.Values.Count(a => a.AlertId != previous.AlertId));
    }

    // ── The classifier itself ─────────────────────────────────────────────────

    [Theory]
    [InlineData("[]", true, 0, false)]
    [InlineData(ModelAlertsJson, true, 1, false)]
    [InlineData(null, false, 0, false)]
    [InlineData(" ", false, 0, false)]
    [InlineData("null", false, 0, false)]
    [InlineData("{oops", false, 0, true)]
    public void TryReadModelAlerts_TellsKnownFromUnknown(
        string? json, bool expectedKnown, int expectedCount, bool expectedUnreadable)
    {
        var known = AnalysisPipeline.TryReadModelAlerts(json, out var alerts, out var unreadable);

        Assert.Equal(expectedKnown, known);
        Assert.Equal(expectedUnreadable, unreadable);
        Assert.Equal(expectedCount, alerts?.Count ?? 0);
    }

    /// <summary>A real repository whose supersede fails — the "DB hiccup on the UPDATE" case.</summary>
    private sealed class ThrowingSupersedeAlertRepository(IAlertRepository inner) : IAlertRepository
    {
        public int SupersedeCalls { get; private set; }

        public Task<int> SupersedePreviousEntityAlertsAsync(Guid entityId, string tenantId, Guid? excludeAnalysisId, CancellationToken ct)
        {
            SupersedeCalls++;
            throw new InvalidOperationException("supersede failed");
        }

        public Task CreateManyAsync(IEnumerable<Alert> alerts, CancellationToken ct) => inner.CreateManyAsync(alerts, ct);
        public Task<IReadOnlyList<Alert>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => inner.GetAllByTenantAsync(tenantId, ct);
        public Task<IReadOnlyList<Alert>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => inner.GetByEntityIdAsync(entityId, tenantId, ct);
        public Task<Alert?> GetByIdAsync(Guid alertId, string tenantId, CancellationToken ct) => inner.GetByIdAsync(alertId, tenantId, ct);
        public Task<bool> MarkAsReadAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct) => inner.MarkAsReadAsync(alertId, tenantId, userId, ct);
        public Task<bool> MarkAsResolvedAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct) => inner.MarkAsResolvedAsync(alertId, tenantId, userId, ct);
        public Task<int> MarkAllAsReadAsync(string tenantId, Guid userId, CancellationToken ct) => inner.MarkAllAsReadAsync(tenantId, userId, ct);
        public Task<bool> ExistsByTypeInPeriodAsync(string tenantId, string alertType, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => inner.ExistsByTypeInPeriodAsync(tenantId, alertType, periodStartUtc, periodEndUtc, ct);
        public Task<IReadOnlyList<Alert>> GetOpenByTypesForUpdateAsync(string tenantId, IReadOnlyCollection<string> alertTypes, CancellationToken ct) => inner.GetOpenByTypesForUpdateAsync(tenantId, alertTypes, ct);
        public Task<IReadOnlyList<Alert>> GetOpenByTypesCreatedBeforeForUpdateAsync(string tenantId, IReadOnlyCollection<string> alertTypes, DateTime createdBeforeUtc, CancellationToken ct) => inner.GetOpenByTypesCreatedBeforeForUpdateAsync(tenantId, alertTypes, createdBeforeUtc, ct);
        public Task<IReadOnlyDictionary<(string AlertType, string MetricKey), Alert>> GetLatestByMetricKeysAsync(string tenantId, IReadOnlyCollection<string> alertTypes, IReadOnlyCollection<string> metricKeys, CancellationToken ct) => inner.GetLatestByMetricKeysAsync(tenantId, alertTypes, metricKeys, ct);
        public Task SaveReconciliationAsync(string tenantId, IReadOnlyCollection<Alert> toCreate, CancellationToken ct) => inner.SaveReconciliationAsync(tenantId, toCreate, ct);
        public Task<IReadOnlyList<PreviousAlertRow>> GetPreviousByKeysAsync(string tenantId, IReadOnlyCollection<string> metricKeys, CancellationToken ct) => inner.GetPreviousByKeysAsync(tenantId, metricKeys, ct);
        public Task<AlertUnresolveOutcome> UnresolveAsync(Guid alertId, string tenantId, CancellationToken ct) => inner.UnresolveAsync(alertId, tenantId, ct);
    }
}
