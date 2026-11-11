// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The PyME-level event alerts, keyed <c>entity:{EntityId}</c>: PYME_REVIEW_DUE (Argentine days since the
/// latest approved analysis) and PYME_BCRA_WORSENED (the dashboard's bcraSignals rule). Both only for a PyME
/// with an Active cupo.
/// </summary>
public sealed partial class CupoAlertServiceTests
{
    private static Entity Pyme(string tenantId = Tenant, string? bcraJson = null, DateTime? bcraUpdatedAt = null) => new()
    {
        EntityId          = Guid.NewGuid(),
        TenantId          = tenantId,
        CUIT              = "30" + Random.Shared.Next(10_000_000, 99_999_999) + "9",
        BusinessName      = "PyME " + Guid.NewGuid().ToString("N")[..6],
        CreatedAt         = Now,
        UpdatedAt         = Now,
        BcraDataJson      = bcraJson,
        BcraDataUpdatedAt = bcraUpdatedAt
    };

    private static Analysis AnalysisOf(
        Guid entityId, DateTime createdAt, DateTime? reviewedAt = null, string status = AnalysisStatuses.Approved,
        string kind = AnalysisKinds.Balance, string tenantId = Tenant) => new()
    {
        AnalysisId    = Guid.NewGuid(),
        TenantId      = tenantId,
        EntityId      = entityId,
        AnalysisKind  = kind,
        RequestedBy   = Guid.NewGuid(),
        Status        = status,
        FiscalYear    = 2025,
        AiModel       = "model",
        PromptVersion = "v1",
        CreatedAt     = createdAt,
        ReviewedAt    = reviewedAt
    };

    private static AnalysisResult ResultOf(Analysis analysis, string? bcraJson, DateTime? generatedAt = null) => new()
    {
        ResultId         = Guid.NewGuid(),
        AnalysisId       = analysis.AnalysisId,
        TenantId         = analysis.TenantId,
        GeneratedAt      = generatedAt ?? analysis.CreatedAt,
        BalanceJson      = """{"activo":1}""",
        AiSuggestedScore = 50m,
        AiSuggestedLevel = "Medium",
        AiInputHash      = "in",
        AiOutputHash     = "out",
        ValidationStatus = "Valid",
        EnrichmentStatus = nameof(BcraEnrichmentOutcome.Complete),
        BcraDataJson     = bcraJson
    };

    private static CreditLine CupoOf(Entity pyme, Analysis? analysis = null, string status = "Active", string currency = "ARS")
    {
        var cupo = Cupo(status: status, currency: currency, tenantId: pyme.TenantId, originAnalysisId: analysis?.AnalysisId);
        cupo.EntityId = pyme.EntityId;
        return cupo;
    }

    /// <summary>An approval at 15:00 UTC (12:00 in Buenos Aires) on <paramref name="localDate"/>.</summary>
    private static DateTime At(int year, int month, int day) => new(year, month, day, 15, 0, 0, DateTimeKind.Utc);

    private static string Debt(int situacion) => DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, situacion);

    private static readonly string NoDebt = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.NoDebtReported, null);
    private static readonly string Failed = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.LookupFailed, null);

    private async Task SetEntityBcraAsync(Guid entityId, string bcraJson)
    {
        await using var ctx = NewContext();
        var entity = await ctx.Entities.SingleAsync(e => e.EntityId == entityId);
        entity.BcraDataJson = bcraJson;
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
    }

    // ── PYME_REVIEW_DUE ─────────────────────────────────────────────────────────

    // Now is 25/10/2026 in Buenos Aires; 25/10/2025 is 365 days before it.
    [Theory]
    [InlineData(2025, 11, 25, 334, null,     null)]
    [InlineData(2025, 11, 24, 335, "Medium", "Revisión anual próxima")]
    [InlineData(2025, 10, 26, 364, "Medium", "Revisión anual próxima")]
    [InlineData(2025, 10, 25, 365, "High",   "Revisión anual vencida")]
    [InlineData(2024, 3, 1,   968, "High",   "Revisión anual vencida")]
    public async Task Review_SeverityByArgentineDaysSinceTheLatestApproval(
        int year, int month, int day, int ageDays, string? severity, string? title)
    {
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(year, month, day).AddDays(-3), reviewedAt: At(year, month, day));
        await SeedAsync(pyme, approved, CupoOf(pyme, approved));

        await ReconcileAsync();

        var open = await OpenAsync(EventAlertTypes.PymeReviewDue);
        if (severity is null)
        {
            Assert.Empty(open);
            return;
        }

        var alert = Assert.Single(open);
        Assert.Equal(EventAlertTypes.EntityMetricKey(pyme.EntityId), alert.MetricKey);
        Assert.Equal(pyme.EntityId, alert.EntityId);
        Assert.Equal(approved.AnalysisId, alert.AnalysisId);
        Assert.Equal(severity, alert.Severity);
        Assert.Equal(title, alert.Title);
        Assert.Equal(ageDays, alert.MetricValue);
        Assert.Equal(365m, alert.ThresholdValue);
        Assert.Equal(
            $"El último análisis aprobado es del {day:00}/{month:00}/{year} (hace {ageDays} días). Hacé un análisis nuevo de la PyME.",
            alert.Description);
    }

    [Fact]
    public async Task Review_TheApprovalIsDatedInArgentineTime()
    {
        // 01:00 UTC on 26/10/2025 is 22:00 on 25/10/2025 in Buenos Aires: 365 days, not the UTC date's 364.
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(2025, 10, 1),
            reviewedAt: new DateTime(2025, 10, 26, 1, 0, 0, DateTimeKind.Utc));
        await SeedAsync(pyme, approved, CupoOf(pyme, approved));

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.PymeReviewDue));
        Assert.Equal("High", alert.Severity);
        Assert.Equal(365m, alert.MetricValue);
        Assert.StartsWith("El último análisis aprobado es del 25/10/2025 (hace 365 días).", alert.Description);
    }

    [Fact]
    public async Task Review_WithoutReviewedAt_CountsFromCreatedAt()
    {
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(2025, 10, 1));
        await SeedAsync(pyme, approved, CupoOf(pyme, approved));

        await ReconcileAsync();

        Assert.Equal(389m, Assert.Single(await OpenAsync(EventAlertTypes.PymeReviewDue)).MetricValue);
    }

    [Fact]
    public async Task Review_TheLatestApprovalOfAnyKindCounts_AndOtherStatusesDoNot()
    {
        var pyme      = Pyme();
        var old       = AnalysisOf(pyme.EntityId, createdAt: At(2024, 1, 1), reviewedAt: At(2024, 1, 10));
        var latest    = AnalysisOf(pyme.EntityId, createdAt: At(2025, 6, 1), reviewedAt: At(2025, 6, 2), kind: AnalysisKinds.PersonaHumana);
        var pending   = AnalysisOf(pyme.EntityId, createdAt: At(2026, 10, 1), status: AnalysisStatuses.PendingReview);
        var rejected  = AnalysisOf(pyme.EntityId, createdAt: At(2026, 9, 1), reviewedAt: At(2026, 9, 2), status: AnalysisStatuses.Rejected);
        await SeedAsync(pyme, old, latest, pending, rejected, CupoOf(pyme, old));

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.PymeReviewDue));
        Assert.Equal(latest.AnalysisId, alert.AnalysisId);
        Assert.Equal(510m, alert.MetricValue); // 02/06/2025 → 25/10/2026
    }

    [Fact]
    public async Task Review_SeveralCuposOfOnePyme_AreOneAlert_AndASecondPassDoesNotDuplicate()
    {
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(2025, 1, 1));
        await SeedAsync(pyme, approved, CupoOf(pyme, approved), CupoOf(pyme, approved, currency: "USD"));

        await ReconcileAsync();
        var first = Assert.Single(await AlertsAsync());

        await ReconcileAsync(Now.AddMinutes(1));

        var after = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, after.AlertId);
        Assert.False(after.IsResolved);
    }

    [Fact]
    public async Task Review_TheDayCountUpdatesInPlace_AndAnEscalationMakesItUnreadAgain()
    {
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(2025, 10, 1), reviewedAt: At(2025, 10, 27)); // 363 days
        await SeedAsync(pyme, approved, CupoOf(pyme, approved));
        await ReconcileAsync();
        var first = Assert.Single(await AlertsAsync());
        Assert.Equal("Medium", first.Severity);
        await MarkReadAsync(first.AlertId);

        await ReconcileAsync(Now.AddDays(1)); // 364: same severity, still read
        var day364 = Assert.Single(await AlertsAsync());
        Assert.Equal((first.AlertId, 364m, true), (day364.AlertId, day364.MetricValue!.Value, day364.IsRead));

        await ReconcileAsync(Now.AddDays(2)); // 365: High, unread again
        var day365 = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, day365.AlertId);
        Assert.Equal("High", day365.Severity);
        Assert.Equal("Revisión anual vencida", day365.Title);
        Assert.False(day365.IsRead);
    }

    [Fact]
    public async Task Review_ANewApproval_ResolvesIt()
    {
        var pyme = Pyme();
        var old  = AnalysisOf(pyme.EntityId, createdAt: At(2025, 1, 1));
        await SeedAsync(pyme, old, CupoOf(pyme, old));
        await ReconcileAsync();
        Assert.Single(await OpenAsync(EventAlertTypes.PymeReviewDue));

        await SeedAsync(AnalysisOf(pyme.EntityId, createdAt: Now.AddDays(-3), reviewedAt: Now.AddHours(-1)));
        await ReconcileAsync(Now.AddMinutes(1));

        var alert = Assert.Single(await AlertsAsync());
        Assert.True(alert.IsResolved);
        Assert.Null(alert.ResolvedBy);
    }

    [Fact]
    public async Task Review_NoActiveCupo_RaisesNothing_AndResolvesTheOpenOne()
    {
        var withSuspended = Pyme();
        var oldA          = AnalysisOf(withSuspended.EntityId, createdAt: At(2024, 1, 1));
        var pyme          = Pyme();
        var oldB          = AnalysisOf(pyme.EntityId, createdAt: At(2024, 1, 1));
        var cupo          = CupoOf(pyme, oldB);
        await SeedAsync(withSuspended, oldA, CupoOf(withSuspended, oldA, status: "Suspended"), pyme, oldB, cupo);
        await ReconcileAsync();
        Assert.Equal(EventAlertTypes.EntityMetricKey(pyme.EntityId), Assert.Single(await AlertsAsync()).MetricKey);

        await SetCupoStatusAsync(cupo.CreditLineId, "Suspended");
        await ReconcileAsync(Now.AddMinutes(1));

        Assert.True(Assert.Single(await AlertsAsync()).IsResolved);
    }

    [Fact]
    public async Task Review_NoApprovedAnalysisAtAll_IsSkipped_AndAnOpenAlertIsLeftAlone()
    {
        var pyme     = Pyme();
        var pending  = AnalysisOf(pyme.EntityId, createdAt: At(2024, 1, 1), status: AnalysisStatuses.PendingReview);
        var existing = OpenCupoAlert(EventAlertTypes.PymeReviewDue, Guid.Empty, Now.AddDays(-1));
        existing.MetricKey = EventAlertTypes.EntityMetricKey(pyme.EntityId);
        await SeedAsync(pyme, pending, CupoOf(pyme, pending), existing);

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(existing.AlertId, alert.AlertId);
        Assert.False(alert.IsResolved);
    }

    [Fact]
    public async Task Review_Suppression_TheDayCountGrowingDoesNotDefeatADismissal_ButAnEscalationDoes()
    {
        var pyme     = Pyme();
        var approved = AnalysisOf(pyme.EntityId, createdAt: At(2025, 10, 1), reviewedAt: At(2025, 11, 20)); // 339 days
        await SeedAsync(pyme, approved, CupoOf(pyme, approved));
        await ReconcileAsync();
        var dismissed = Assert.Single(await AlertsAsync());
        await ResolveByUserAsync(dismissed.AlertId, Now.AddMinutes(1));

        await ReconcileAsync(Now.AddDays(10)); // 349 days: still Medium
        Assert.Empty(await OpenAsync(EventAlertTypes.PymeReviewDue));

        await ReconcileAsync(Now.AddDays(26)); // 365 days: High
        var open = Assert.Single(await OpenAsync(EventAlertTypes.PymeReviewDue));
        Assert.NotEqual(dismissed.AlertId, open.AlertId);
        Assert.Equal("High", open.Severity);
    }

    [Fact]
    public async Task Review_TenantIsolation_AnotherTenantsApprovalsAndCuposNeverCount()
    {
        var pyme = Pyme();
        var old  = AnalysisOf(pyme.EntityId, createdAt: At(2024, 1, 1));
        // Another tenant's recent approval carrying this PyME's id must not refresh it.
        var foreignApproval = AnalysisOf(pyme.EntityId, createdAt: Now.AddDays(-2), tenantId: Other);
        var theirs          = Pyme(tenantId: Other);
        var theirOld        = AnalysisOf(theirs.EntityId, createdAt: At(2024, 1, 1), tenantId: Other);
        await SeedAsync(pyme, old, CupoOf(pyme, old), foreignApproval, theirs, theirOld, CupoOf(theirs, theirOld));

        await ReconcileAsync();

        var alert = Assert.Single(await AlertsAsync(Tenant));
        Assert.Equal(old.AnalysisId, alert.AnalysisId);
        Assert.Empty(await AlertsAsync(Other));
    }

    // ── PYME_BCRA_WORSENED ──────────────────────────────────────────────────────

    /// <summary>A PyME with an Active cupo backed by an analysis whose result froze <paramref name="thenJson"/>.</summary>
    private async Task<(Entity Pyme, Analysis Analysis, CreditLine Cupo)> SeedBcraAsync(
        string? nowJson, string? thenJson, string tenantId = Tenant, string cupoStatus = "Active")
    {
        // 02:00 UTC on 25/10 is still 24/10 in Buenos Aires.
        var pyme     = Pyme(tenantId, nowJson, new DateTime(2026, 10, 25, 2, 0, 0, DateTimeKind.Utc));
        var analysis = AnalysisOf(pyme.EntityId, createdAt: Now.AddDays(-30), status: AnalysisStatuses.PendingReview, tenantId: tenantId);
        var cupo     = CupoOf(pyme, analysis, status: cupoStatus);
        await SeedAsync(pyme, analysis, ResultOf(analysis, thenJson), cupo);
        return (pyme, analysis, cupo);
    }

    [Fact]
    public async Task Bcra_Worsened_CreatesHighFromSituacion3_WithItsIdentityAndCopy()
    {
        var (pyme, analysis, _) = await SeedBcraAsync(Debt(4), Debt(2));

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.PymeBcraWorsened));
        Assert.Equal(EventAlertTypes.EntityMetricKey(pyme.EntityId), alert.MetricKey);
        Assert.Equal(pyme.EntityId, alert.EntityId);
        Assert.Equal(analysis.AnalysisId, alert.AnalysisId);
        Assert.Equal("High", alert.Severity);
        Assert.Equal("Empeoró la situación en el BCRA", alert.Title);
        Assert.Equal(4m, alert.MetricValue);
        Assert.Equal(2m, alert.ThresholdValue);
        Assert.Equal(
            "Pasó de situación 2 a 4 desde el análisis que respalda el cupo. Consulta al BCRA del 24/10/2026. " +
            "Revisá la PyME y evaluá reanalizarla o suspender el cupo.",
            alert.Description);
    }

    [Fact]
    public async Task Bcra_WorsenedBelowSituacion3_IsMedium()
    {
        await SeedBcraAsync(Debt(2), NoDebt); // no debt then reads as situación 1

        await ReconcileAsync();

        var alert = Assert.Single(await OpenAsync(EventAlertTypes.PymeBcraWorsened));
        Assert.Equal("Medium", alert.Severity);
        Assert.Equal((2m, 1m), (alert.MetricValue!.Value, alert.ThresholdValue!.Value));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 4)]
    public async Task Bcra_EqualOrBetter_RaisesNothing(int now, int then)
    {
        await SeedBcraAsync(Debt(now), Debt(then));

        await ReconcileAsync();

        Assert.Empty(await AlertsAsync());
    }

    [Fact]
    public async Task Bcra_Improving_AutoResolves_AndASecondPassDoesNotDuplicate()
    {
        var (pyme, _, _) = await SeedBcraAsync(Debt(3), Debt(1));
        await ReconcileAsync();
        await ReconcileAsync(Now.AddMinutes(1));
        var first = Assert.Single(await AlertsAsync());
        Assert.False(first.IsResolved);

        await SetEntityBcraAsync(pyme.EntityId, Debt(1));
        await ReconcileAsync(Now.AddMinutes(2));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, alert.AlertId);
        Assert.True(alert.IsResolved);
        Assert.Null(alert.ResolvedBy);
    }

    [Fact]
    public async Task Bcra_AnUnreadableReadingNow_LeavesTheOpenAlertAlone_AndRaisesNothingNew()
    {
        var (pyme, _, _) = await SeedBcraAsync(Debt(3), Debt(1));
        await ReconcileAsync();
        var first = Assert.Single(await AlertsAsync());

        await SetEntityBcraAsync(pyme.EntityId, Failed); // a transient outage is "unknown", not "better"
        await ReconcileAsync(Now.AddMinutes(1));

        var alert = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, alert.AlertId);
        Assert.False(alert.IsResolved);

        await SeedBcraAsync(Failed, Debt(1));
        await ReconcileAsync(Now.AddMinutes(2));
        Assert.Single(await AlertsAsync());
    }

    [Fact]
    public async Task Bcra_UpdatesInPlace_AndAnEscalationMakesItUnreadAgain()
    {
        var (pyme, _, _) = await SeedBcraAsync(Debt(2), Debt(1));
        await ReconcileAsync();
        var first = Assert.Single(await AlertsAsync());
        Assert.Equal("Medium", first.Severity);
        await MarkReadAsync(first.AlertId);

        await SetEntityBcraAsync(pyme.EntityId, Debt(3));
        await ReconcileAsync(Now.AddMinutes(1));

        var after = Assert.Single(await AlertsAsync());
        Assert.Equal(first.AlertId, after.AlertId);
        Assert.Equal(("High", 3m), (after.Severity, after.MetricValue!.Value));
        Assert.StartsWith("Pasó de situación 1 a 3", after.Description);
        Assert.False(after.IsRead);
    }

    [Fact]
    public async Task Bcra_NoActiveCupo_RaisesNothing_AndResolvesTheOpenOne()
    {
        await SeedBcraAsync(Debt(5), Debt(1), cupoStatus: "Suspended");
        var (_, _, cupo) = await SeedBcraAsync(Debt(5), Debt(1));
        await ReconcileAsync();
        Assert.Single(await AlertsAsync());

        await SetCupoStatusAsync(cupo.CreditLineId, "Suspended");
        await ReconcileAsync(Now.AddMinutes(1));

        Assert.True(Assert.Single(await AlertsAsync()).IsResolved);
    }

    [Fact]
    public async Task Bcra_Suppression_StaysDismissedUntilTheSituacionGetsWorse()
    {
        var (pyme, _, _) = await SeedBcraAsync(Debt(3), Debt(2));
        await ReconcileAsync();
        var dismissed = Assert.Single(await AlertsAsync());
        await ResolveByUserAsync(dismissed.AlertId, Now.AddMinutes(1));

        await ReconcileAsync(Now.AddDays(1));
        Assert.Empty(await OpenAsync(EventAlertTypes.PymeBcraWorsened));

        await SetEntityBcraAsync(pyme.EntityId, Debt(4));
        await ReconcileAsync(Now.AddDays(2));

        var open = Assert.Single(await OpenAsync(EventAlertTypes.PymeBcraWorsened));
        Assert.NotEqual(dismissed.AlertId, open.AlertId);
        Assert.Equal(4m, open.MetricValue);
    }

    [Fact]
    public async Task Bcra_TenantIsolation_AnotherTenantsPymesNeverProduceAlertsHere()
    {
        await SeedBcraAsync(Debt(5), Debt(1), tenantId: Other);

        await ReconcileAsync();

        Assert.Empty(await AlertsAsync(Tenant));
        Assert.Empty(await AlertsAsync(Other));
    }

    // ── One pass for every type ─────────────────────────────────────────────────

    [Fact]
    public async Task OnePass_RaisesEveryEventType_WithOneSaveForAll()
    {
        var pyme     = Pyme(bcraJson: Debt(3), bcraUpdatedAt: Now.AddDays(-1));
        var analysis = AnalysisOf(pyme.EntityId, createdAt: At(2025, 1, 1));
        var cupo     = CupoOf(pyme, analysis);
        cupo.Amount  = 100m;
        await SeedAsync(
            pyme, analysis, ResultOf(analysis, Debt(1)), cupo,
            Operacion(cupo, 60m, validUntil: Past),
            Operacion(cupo, 50m, validUntil: Now.Date.AddDays(3)));

        await ReconcileAsync();

        var types = (await AlertsAsync()).Where(a => !a.IsResolved).Select(a => a.AlertType).Order().ToList();
        Assert.Equal(EventAlertTypes.All.Order().ToList(), types);
    }
}
