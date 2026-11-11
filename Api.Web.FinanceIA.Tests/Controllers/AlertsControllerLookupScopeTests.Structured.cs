// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Json;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The structured fields the Alertas redesign reads instead of parsing Description — entityCuit,
/// entityIsPersonaHumana, resolvedByUserId/Name, isAutoResolved, creditLineId, facts, reopenedAfter — and
/// PATCH {id}/unresolve. Every lookup behind them is ONE batched call per request, never one per alert.
/// </summary>
public sealed partial class AlertsControllerLookupScopeTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static Alert EventAlert(
        string alertType, string metricKey, bool isResolved = false, Guid? resolvedBy = null, DateTime? createdAt = null)
    {
        var alert = Alert(entityId: Guid.NewGuid(), isResolved: isResolved);
        alert.AlertType  = alertType;
        alert.MetricKey  = metricKey;
        alert.ResolvedBy = resolvedBy;
        alert.ResolvedAt = isResolved ? T0.AddHours(1) : null;
        alert.CreatedAt  = createdAt ?? T0.AddDays(1);
        return alert;
    }

    private static User UserOf(string name, string tenant = Tenant) => new()
    {
        UserId = Guid.NewGuid(), TenantId = tenant, DisplayName = name, FirebaseUid = Guid.NewGuid().ToString("N")
    };

    // ── Entity fields ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_EntityCuitAndPersonaHumana_ComeFromTheSameEntityLookup()
    {
        var company = Guid.NewGuid();
        var person  = Guid.NewGuid();
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(company, "Metalúrgica del Sur S.A."), Ent(person, "Juana Pérez", PersonTypes.Humana)]
        };
        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn = [Alert(company), Alert(person), Alert(entityId: null), Alert(Guid.NewGuid())]
        };

        var body = BodyOf(await BuildSut(alertRepo, entityRepo, new StubAnalysisResultRepository())
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(("30-12345678-9", (bool?)false), (body.Alerts[0].EntityCuit, body.Alerts[0].EntityIsPersonaHumana));
        Assert.Equal(("30-12345678-9", (bool?)true), (body.Alerts[1].EntityCuit, body.Alerts[1].EntityIsPersonaHumana));
        Assert.Equal((null, (bool?)null), (body.Alerts[2].EntityCuit, body.Alerts[2].EntityIsPersonaHumana)); // system alert
        Assert.Equal((null, (bool?)null), (body.Alerts[3].EntityCuit, body.Alerts[3].EntityIsPersonaHumana)); // not this tenant's
        Assert.Equal(0, entityRepo.GetAllCallCount);
    }

    // ── Resolution fields ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_ResolvedBy_IsNamedFromOneBatchedTenantFilteredUsersRead()
    {
        var ana     = UserOf("Ana Gómez");
        var foreign = UserOf("De otro tenant", tenant: "tenant-2");
        var blank   = UserOf("  ");
        var users   = new StubUserRepository();
        users.Users.AddRange([ana, foreign, blank]);

        var byAna1  = Alert(isResolved: true);  byAna1.ResolvedBy  = ana.UserId;
        var byAna2  = Alert(isResolved: true);  byAna2.ResolvedBy  = ana.UserId;
        var byOther = Alert(isResolved: true);  byOther.ResolvedBy = foreign.UserId;
        var byBlank = Alert(isResolved: true);  byBlank.ResolvedBy = blank.UserId;
        var auto    = Alert(isResolved: true);
        var open    = Alert();
        var alertRepo = new StubAlertRepository { AlertsToReturn = [byAna1, byAna2, byOther, byBlank, auto, open] };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), userRepo: users)
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(1, users.GetByIdsCallCount);
        Assert.Equal(3, users.LastIds.Count); // distinct
        Assert.Equal((ana.UserId, "Ana Gómez", false), (body.Alerts[0].ResolvedByUserId, body.Alerts[0].ResolvedByName, body.Alerts[0].IsAutoResolved));
        Assert.Equal("Ana Gómez", body.Alerts[1].ResolvedByName);
        Assert.Equal((foreign.UserId, (string?)null), (body.Alerts[2].ResolvedByUserId, body.Alerts[2].ResolvedByName));
        Assert.Null(body.Alerts[3].ResolvedByName);
        Assert.Equal(((Guid?)null, (string?)null, true), (body.Alerts[4].ResolvedByUserId, body.Alerts[4].ResolvedByName, body.Alerts[4].IsAutoResolved));
        Assert.Equal(((Guid?)null, false), (body.Alerts[5].ResolvedByUserId, body.Alerts[5].IsAutoResolved));
    }

    [Fact]
    public async Task GetAlerts_WithNoUserResolvedAlert_ReadsNoUsers()
    {
        var users     = new StubUserRepository();
        var alertRepo = new StubAlertRepository { AlertsToReturn = [Alert(), Alert(isResolved: true)] };

        await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), userRepo: users)
            .GetAlertsAsync(null, CancellationToken.None);

        Assert.Equal(0, users.GetByIdsCallCount);
    }

    // ── creditLineId ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_CreditLineId_IsParsedOnlyForTheCupoEventTypes()
    {
        var creditLineId = Guid.NewGuid();
        var entityId     = Guid.NewGuid();
        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn =
            [
                EventAlert(EventAlertTypes.CupoOperationsExpiring, EventAlertTypes.CupoMetricKey(creditLineId)),
                EventAlert(EventAlertTypes.PymeReviewDue, EventAlertTypes.EntityMetricKey(entityId)),
                EventAlert("HIGH_LEVERAGE", EventAlertTypes.CupoMetricKey(creditLineId)), // not an event type
                EventAlert(EventAlertTypes.CupoUtilizationHigh, "cupo:not-a-guid"),
            ]
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository())
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal([creditLineId, null, null, null], body.Alerts.Select(a => a.CreditLineId));
    }

    // ── facts ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_Facts_AreAttachedToOpenAlertsFromThisRequestsPass()
    {
        var open     = EventAlert(EventAlertTypes.CupoOperationsExpired, EventAlertTypes.CupoMetricKey(Guid.NewGuid()));
        var resolved = EventAlert(EventAlertTypes.CupoOperationsExpired, EventAlertTypes.CupoMetricKey(Guid.NewGuid()), isResolved: true);
        var finding  = Alert();
        var openFacts = new AlertFactsDto { Currency = "ARS", Amount = 10m, CupoAmount = 100m, Count = 1, Date = new DateOnly(2026, 9, 3) };
        var alertRepo = new StubAlertRepository { AlertsToReturn = [open, resolved, finding] };
        var cupoAlerts = new RecordingCupoAlertService(alertRepo)
        {
            // A map naming a resolved alert must not put facts on it.
            FactsToReturn = new Dictionary<Guid, AlertFactsDto> { [open.AlertId] = openFacts, [resolved.AlertId] = openFacts }
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Same(openFacts, body.Alerts[0].Facts);
        Assert.Null(body.Alerts[1].Facts);
        Assert.Null(body.Alerts[2].Facts);
    }

    [Fact]
    public async Task GetAlerts_Facts_AreNullWhenThePassWasSkippedOrFailed()
    {
        var open      = EventAlert(EventAlertTypes.CupoOperationsExpired, EventAlertTypes.CupoMetricKey(Guid.NewGuid()));
        var alertRepo = new StubAlertRepository { AlertsToReturn = [open] };

        var skipped = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(),
                new RecordingCupoAlertService(alertRepo) { FactsToReturn = null })
            .GetAlertsAsync(null, CancellationToken.None));
        var threw = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(),
                new RecordingCupoAlertService(alertRepo) { ThrowOnReconcile = new InvalidOperationException("boom") })
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Null(Assert.Single(skipped.Alerts).Facts);
        Assert.Null(Assert.Single(threw.Alerts).Facts);
    }

    [Fact]
    public async Task GetAlerts_Facts_AreNullOnTheEntityIdPath_WhereNoPassRuns()
    {
        var open       = EventAlert(EventAlertTypes.PymeReviewDue, EventAlertTypes.EntityMetricKey(Guid.NewGuid()));
        var alertRepo  = new StubAlertRepository { AlertsToReturn = [open] };
        var cupoAlerts = new RecordingCupoAlertService(alertRepo)
        {
            FactsToReturn = new Dictionary<Guid, AlertFactsDto> { [open.AlertId] = new() { DaysDelta = 1 } }
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
            .GetAlertsAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Empty(cupoAlerts.Calls);
        Assert.Null(Assert.Single(body.Alerts).Facts);
    }

    // ── reopenedAfter ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_ReopenedAfter_IsThePreviousAlertOfTheKey_OnlyWhenAUserResolvedIt()
    {
        var ana   = UserOf("Ana Gómez");
        var users = new StubUserRepository();
        users.Users.Add(ana);

        var dismissedKey = EventAlertTypes.CupoMetricKey(Guid.NewGuid());
        var clearedKey   = EventAlertTypes.EntityMetricKey(Guid.NewGuid());
        var freshKey     = EventAlertTypes.CupoMetricKey(Guid.NewGuid());

        var reopened = EventAlert(EventAlertTypes.CupoOperationsExpired, dismissedKey);
        var cleared  = EventAlert(EventAlertTypes.PymeBcraWorsened, clearedKey);
        var fresh    = EventAlert(EventAlertTypes.CupoOperationsExpiring, freshKey);
        var stillResolved = EventAlert(EventAlertTypes.CupoOperationsExpired, freshKey, isResolved: true, resolvedBy: ana.UserId);

        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn   = [reopened, cleared, fresh, stillResolved, Alert()],
            PreviousToReturn =
            [
                // dismissedKey: an older auto-resolution, then the user's dismissal — the latest wins.
                new(Guid.NewGuid(), EventAlertTypes.CupoOperationsExpired, dismissedKey, T0.AddDays(-9), T0.AddDays(-8), null, 1m),
                new(Guid.NewGuid(), EventAlertTypes.CupoOperationsExpired, dismissedKey, T0.AddDays(-3), T0.AddDays(-2), ana.UserId, 2m),
                // A later alert of the same key (it cannot precede `reopened`) is ignored.
                new(Guid.NewGuid(), EventAlertTypes.CupoOperationsExpired, dismissedKey, T0.AddDays(5), T0.AddDays(6), ana.UserId, 9m),
                // The same key of another type is another situation.
                new(Guid.NewGuid(), EventAlertTypes.CupoUtilizationHigh, dismissedKey, T0, T0, ana.UserId, 7m),
                // clearedKey: a user dismissed it long ago, but the latest episode was resolved by the system.
                new(Guid.NewGuid(), EventAlertTypes.PymeBcraWorsened, clearedKey, T0.AddDays(-20), T0.AddDays(-19), ana.UserId, 3m),
                new(Guid.NewGuid(), EventAlertTypes.PymeBcraWorsened, clearedKey, T0.AddDays(-5), T0.AddDays(-4), null, 3m),
            ]
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), userRepo: users)
            .GetAlertsAsync(null, CancellationToken.None));

        var after = body.Alerts[0].ReopenedAfter;
        Assert.NotNull(after);
        Assert.Equal((T0.AddDays(-2), "Ana Gómez", 2m), (after!.ResolvedAt!.Value, after.ResolvedByName, after.MetricValue!.Value));
        Assert.Null(body.Alerts[1].ReopenedAfter);
        Assert.Null(body.Alerts[2].ReopenedAfter);
        Assert.Null(body.Alerts[3].ReopenedAfter); // resolved alerts carry none
        Assert.Null(body.Alerts[4].ReopenedAfter); // not an event alert

        Assert.Equal(1, alertRepo.PreviousCallCount);
        Assert.Equal(new[] { dismissedKey, clearedKey, freshKey }.Order(), alertRepo.LastPreviousKeys.Order());
        Assert.Equal(1, users.GetByIdsCallCount);
    }

    [Fact]
    public async Task GetAlerts_WithNoOpenEventAlert_ReadsNoAlertHistory()
    {
        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn = [Alert(), EventAlert(EventAlertTypes.CupoOperationsExpired, EventAlertTypes.CupoMetricKey(Guid.NewGuid()), isResolved: true)]
        };

        await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository())
            .GetAlertsAsync(null, CancellationToken.None);

        Assert.Equal(0, alertRepo.PreviousCallCount);
    }

    // ── No N+1 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlerts_ManyAlerts_StillOneCallPerLookup()
    {
        var users = new StubUserRepository();
        var alerts = new List<Alert>();
        for (var i = 0; i < 25; i++)
        {
            var resolver = UserOf($"U{i}");
            users.Users.Add(resolver);
            alerts.Add(EventAlert(EventAlertTypes.CupoOperationsExpired, EventAlertTypes.CupoMetricKey(Guid.NewGuid())));
            alerts.Add(EventAlert(EventAlertTypes.PymeReviewDue, EventAlertTypes.EntityMetricKey(Guid.NewGuid()), isResolved: true, resolvedBy: resolver.UserId));
        }
        var alertRepo  = new StubAlertRepository { AlertsToReturn = alerts };
        var entityRepo = new StubEntityRepository();

        var body = BodyOf(await BuildSut(alertRepo, entityRepo, new StubAnalysisResultRepository(), userRepo: users)
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(50, body.TotalCount);
        Assert.Equal(1, users.GetByIdsCallCount);
        Assert.Equal(1, alertRepo.PreviousCallCount);
        Assert.Equal(50, entityRepo.LastLookupIds.Count); // one lookup with every id
        Assert.Equal(0, entityRepo.GetAllCallCount);
    }

    // ── PATCH {id}/unresolve ─────────────────────────────────────────────────

    [Theory]
    [InlineData(AlertUnresolveOutcome.Reopened, 204)]
    [InlineData(AlertUnresolveOutcome.NotFound, 404)]
    [InlineData(AlertUnresolveOutcome.AutoResolved, 409)]
    [InlineData(AlertUnresolveOutcome.OpenDuplicate, 409)]
    public async Task Unresolve_MapsEachOutcome(AlertUnresolveOutcome outcome, int status)
    {
        var alertRepo = new StubAlertRepository { UnresolveOutcome = outcome };
        var alertId   = Guid.NewGuid();

        var result = await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository())
            .UnresolveAsync(alertId, CancellationToken.None);

        Assert.Equal((alertId, Tenant), alertRepo.LastUnresolve);
        if (status == 204)
        {
            Assert.IsType<NoContentResult>(result);
            return;
        }

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.False(string.IsNullOrWhiteSpace(details.Detail));
    }

    [Fact]
    public async Task Unresolve_AnUnknownCaller_Is401_AndTouchesNothing()
    {
        var alertRepo = new StubAlertRepository();
        var users     = new StubUserRepository { Caller = null };

        var result = await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), userRepo: users)
            .UnresolveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Null(alertRepo.LastUnresolve);
    }

    // ── Wire shape ───────────────────────────────────────────────────────────

    /// <summary>
    /// The contract the Portal codes against, serialised with the API's own options (web defaults plus the
    /// UTC DateTime converters, as Program.cs registers them): the new camelCase names, facts' dates as
    /// date-only ISO strings, and every pre-existing field still there.
    /// </summary>
    [Fact]
    public void AlertDto_SerialisesTheStructuredFields_AsThePortalReadsThem()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeConverter());
        options.Converters.Add(new NullableUtcDateTimeConverter());

        var dto = new AlertDto
        {
            AlertId = Guid.NewGuid(), AlertType = EventAlertTypes.PymeReviewDue, IsResolved = false,
            EntityCuit = "30-12345678-9", EntityIsPersonaHumana = false, IsAutoResolved = false,
            Facts = new AlertFactsDto { Date = new DateOnly(2025, 10, 25), SecondaryDate = new DateOnly(2026, 10, 25), DaysDelta = 0 },
            ReopenedAfter = new AlertReopenedAfterDto { ResolvedAt = T0, ResolvedByName = "Ana", MetricValue = 2m }
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, options));
        var root = json.RootElement;

        foreach (var name in new[]
        {
            "alertId", "tenantId", "entityId", "analysisId", "resultId", "alertType", "severity", "title", "description",
            "metricKey", "metricValue", "thresholdValue", "isRead", "isResolved", "resolvedAt", "entityName", "createdAt",
            "entityCuit", "entityIsPersonaHumana", "resolvedByUserId", "resolvedByName", "isAutoResolved", "creditLineId",
            "facts", "reopenedAfter"
        })
        {
            Assert.True(root.TryGetProperty(name, out _), name);
        }

        var facts = root.GetProperty("facts");
        Assert.Equal("2025-10-25", facts.GetProperty("date").GetString());
        Assert.Equal("2026-10-25", facts.GetProperty("secondaryDate").GetString());
        Assert.Equal(0, facts.GetProperty("daysDelta").GetInt32());
        Assert.Equal(JsonValueKind.Null, facts.GetProperty("currency").ValueKind);
        Assert.Equal("Ana", root.GetProperty("reopenedAfter").GetProperty("resolvedByName").GetString());
        Assert.Equal("2026-10-20T12:00:00.000Z", root.GetProperty("reopenedAfter").GetProperty("resolvedAt").GetString());
    }
}
