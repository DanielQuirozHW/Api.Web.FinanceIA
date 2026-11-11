// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="DashboardService.GetPortfolioAsync"/> over real repositories on one in-memory database: money
/// per currency and never mixed, the risk buckets, the caps, sector folding, maturity bands in Argentine days
/// with Vencida decided by CreditLineRules, and BCRA signals only where the situación got worse.
/// </summary>
public sealed class DashboardServicePortfolioTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = DashboardFixture.Now;   // 12:00 UTC = 09:00 in Buenos Aires
    private const string Tenant = DashboardFixture.Tenant;

    private static Task<PortfolioResponse> PortfolioAsync(DashboardFixture f, DateTime? now = null, IDashboardRepository? repository = null)
        => f.Service(repository).GetPortfolioAsync(Tenant, now ?? Now, Ct);

    /// <summary>An analysis row with NO AnalysisResults row.</summary>
    private static Analysis AddAnalysisWithoutResult(DashboardFixture f, Guid entityId)
    {
        var analysis = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = Tenant,
            EntityId      = entityId,
            AnalysisKind  = AnalysisKinds.Balance,
            RequestedBy   = Guid.NewGuid(),
            Status        = AnalysisStatuses.Approved,
            FiscalYear    = 2025,
            AiModel       = "model",
            PromptVersion = "v1",
            CreatedAt     = Now.AddDays(-5)
        };
        f.Context.Analyses.Add(analysis);
        return analysis;
    }

    [Fact]
    public async Task EverySection_KeepsCurrenciesApart()
    {
        await using var f = DashboardFixture.Create(nameof(EverySection_KeepsCurrenciesApart));
        var entity = f.AddEntity();
        entity.Sector = "Agro";
        var (analysis, _) = f.AddAnalysis(Guid.NewGuid(), entity.EntityId, result: r => r.AiSuggestedLevel = "High");
        var ars = f.AddCreditLine(entity.EntityId, "ARS", 1_000m, originAnalysisId: analysis.AnalysisId);
        var usd = f.AddCreditLine(entity.EntityId, "USD", 10m, originAnalysisId: analysis.AnalysisId);
        f.AddOperation(ars, 300m, validUntil: Now.Date.AddDays(5));
        f.AddOperation(usd, 4m, validUntil: Now.Date.AddDays(5));
        f.AddCreditLine(entity.EntityId, "EUR", 77m, status: "Suspended");
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Empty(response.DegradedSections);
        Assert.Equal(["ARS", "EUR", "USD"], response.Totals!.Select(t => t.Currency));
        var totals = response.Totals!.ToDictionary(t => t.Currency);
        Assert.Equal((1_000m, 300m, 700m, 1, 1), (totals["ARS"].Otorgado, totals["ARS"].Utilizado, totals["ARS"].Disponible, totals["ARS"].LineCount, totals["ARS"].EntityCount));
        Assert.Equal((10m, 4m, 6m), (totals["USD"].Otorgado, totals["USD"].Utilizado, totals["USD"].Disponible));
        Assert.Equal((0m, 1, 77m), (totals["EUR"].Otorgado, totals["EUR"].Suspended.Count, totals["EUR"].Suspended.Amount));

        var risk = response.ByRisk!.ToDictionary(r => r.Currency);
        Assert.Equal((1_000m, 300m), (Assert.Single(risk["ARS"].Buckets).Otorgado, risk["ARS"].Buckets[0].Utilizado));
        Assert.Equal((10m, 4m), (Assert.Single(risk["USD"].Buckets).Otorgado, risk["USD"].Buckets[0].Utilizado));

        var top = response.TopExposures!.ToDictionary(t => t.Currency);
        Assert.Equal(300m, Assert.Single(top["ARS"].Items).Utilizado);
        Assert.Equal(4m, Assert.Single(top["USD"].Items).Utilizado);

        var sectors = response.BySector!.ToDictionary(s => s.Currency);
        Assert.Equal((1_000m, 300m), (Assert.Single(sectors["ARS"].Sectors).Otorgado, sectors["ARS"].Sectors[0].Utilizado));
        Assert.Equal((10m, 4m), (Assert.Single(sectors["USD"].Sectors).Otorgado, sectors["USD"].Sectors[0].Utilizado));

        var maturities = response.Maturities!.ByCurrency.ToDictionary(m => m.Currency);
        Assert.Equal(300m, maturities["ARS"].Days0To30.Amount);
        Assert.Equal(4m, maturities["USD"].Days0To30.Amount);
    }

    [Fact]
    public async Task ByRisk_BucketsByTheCuposAnalysis_IncludingPersonaHumanaAndUnclassified()
    {
        await using var f = DashboardFixture.Create(nameof(ByRisk_BucketsByTheCuposAnalysis_IncludingPersonaHumanaAndUnclassified));
        var e = f.AddEntity();
        var (critical, _) = f.AddAnalysis(Guid.NewGuid(), e.EntityId, result: r => r.AiSuggestedLevel = "Critical");
        var (origin, _)   = f.AddAnalysis(Guid.NewGuid(), e.EntityId, result: r => r.AiSuggestedLevel = "Low");
        var (persona, _)  = f.AddAnalysis(Guid.NewGuid(), e.EntityId, a => a.AnalysisKind = AnalysisKinds.PersonaHumana,
            r => { r.AiSuggestedLevel = null; r.AiSuggestedScore = null; });
        var noResult = AddAnalysisWithoutResult(f, e.EntityId);

        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 100m, originAnalysisId: origin.AnalysisId, lastRaiseAnalysisId: critical.AnalysisId), 10m);
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 200m, originAnalysisId: persona.AnalysisId), 20m);
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 300m), 30m);
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 400m, originAnalysisId: noResult.AnalysisId), 40m);
        await f.SaveAsync();

        var buckets = Assert.Single((await PortfolioAsync(f)).ByRisk!).Buckets;

        Assert.Equal([DashboardRiskBuckets.Critical, DashboardRiskBuckets.PersonaHumana, DashboardRiskBuckets.Unclassified],
            buckets.Select(b => b.Bucket));
        Assert.Equal((1, 100m, 10m), Of(DashboardRiskBuckets.Critical));        // the raise wins over the origin
        Assert.Equal((1, 200m, 20m), Of(DashboardRiskBuckets.PersonaHumana));
        Assert.Equal((2, 700m, 70m), Of(DashboardRiskBuckets.Unclassified));    // no analysis, and no result

        (int, decimal, decimal) Of(string bucket)
        {
            var b = buckets.Single(x => x.Bucket == bucket);
            return (b.LineCount, b.Otorgado, b.Utilizado);
        }
    }

    [Fact]
    public async Task TopExposures_AreTenPerCurrency_ByUtilizado_WithNamesLevelAndResult()
    {
        await using var f = DashboardFixture.Create(nameof(TopExposures_AreTenPerCurrency_ByUtilizado_WithNamesLevelAndResult));
        var (analysis, result) = f.AddAnalysis(Guid.NewGuid(), result: r => r.AiSuggestedLevel = "Medium");
        var lines = Enumerable.Range(1, 12)
            .Select(i =>
            {
                var line = f.AddCreditLine(f.AddEntity(name: $"PyME {i:00}").EntityId, amount: 1_000m, originAnalysisId: analysis.AnalysisId);
                f.AddOperation(line, i * 10m);
                return line;
            })
            .ToList();
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, "USD", 5m), 1m);
        await f.SaveAsync();

        var top = (await PortfolioAsync(f)).TopExposures!.ToDictionary(t => t.Currency);

        Assert.Equal(10, top["ARS"].Items.Count);
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(i => i * 10m), top["ARS"].Items.Select(i => i.Utilizado));
        var first = top["ARS"].Items[0];
        Assert.Equal(("PyME 12", 120m, 880m, "Medium", DashboardRiskBuckets.Medium), (first.EntityName, first.Utilizado, first.Disponible, first.AiSuggestedLevel, first.RiskBucket));
        Assert.Equal((analysis.AnalysisId, result.ResultId, lines[^1].CreditLineId), (first.AnalysisId, first.ResultId, first.CreditLineId));
        Assert.NotNull(first.EntityCuit);
        Assert.Single(top["USD"].Items);
    }

    [Fact]
    public async Task BySector_FoldsCaseAndBlanks_ShowsTheCommonestSpelling_AndPutsNoSectorLast()
    {
        await using var f = DashboardFixture.Create(nameof(BySector_FoldsCaseAndBlanks_ShowsTheCommonestSpelling_AndPutsNoSectorLast));
        void Line(string? sector, decimal amount, decimal used)
        {
            var e = f.AddEntity();
            e.Sector = sector;
            f.AddOperation(f.AddCreditLine(e.EntityId, amount: amount), used);
        }

        Line("Retail", 100m, 10m);
        Line("Retail", 100m, 10m);
        Line(" retail ", 100m, 10m);
        Line("RETAIL", 100m, 10m);
        Line("Agro", 50m, 5m);
        Line(null, 10m, 1m);
        Line("   ", 10m, 1m);
        await f.SaveAsync();

        var sectors = Assert.Single((await PortfolioAsync(f)).BySector!).Sectors;

        Assert.Equal(["Retail", "Agro", null], sectors.Select(s => s.Sector));
        Assert.Equal((4, 400m, 40m), (sectors[0].LineCount, sectors[0].Otorgado, sectors[0].Utilizado));
        Assert.Equal((2, 20m, 2m), (sectors[2].LineCount, sectors[2].Otorgado, sectors[2].Utilizado));
    }

    [Fact]
    public async Task Maturities_BandArgentineDays_AndTakeVencidaFromEffectiveStatus()
    {
        await using var f = DashboardFixture.Create(nameof(Maturities_BandArgentineDays_AndTakeVencidaFromEffectiveStatus));
        var line  = f.AddCreditLine(f.AddEntity().EntityId);
        var today = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);   // Now is 09:00 on the 23rd in Buenos Aires
        foreach (var days in new[] { 0, 30, 31, 60, 61, 90, 91 })
            f.AddOperation(line, 1m + days, validUntil: today.AddDays(days));
        f.AddOperation(line, 1_000m, validUntil: today.AddDays(-1));                        // Vencida, still occupying
        f.AddOperation(line, 5_000m, status: "Aprobada", validUntil: today.AddDays(-1));    // never expires into Vencida
        f.AddOperation(line, 7_000m, status: "Cancelada", validUntil: today.AddDays(3));
        f.AddOperation(line, 9_000m);                                                       // open-ended
        await f.SaveAsync();

        var maturities = (await PortfolioAsync(f)).Maturities!;
        var ars = Assert.Single(maturities.ByCurrency);

        Assert.Equal(new DateOnly(2026, 10, 23), maturities.AsOfDate);
        Assert.Equal((2, 1m + 31m), (ars.Days0To30.Count, ars.Days0To30.Amount));       // days 0 and 30
        Assert.Equal((2, 32m + 61m), (ars.Days31To60.Count, ars.Days31To60.Amount));    // days 31 and 60
        Assert.Equal((2, 62m + 91m), (ars.Days61To90.Count, ars.Days61To90.Amount));    // days 61 and 90
        Assert.Equal((1, 1_000m), (ars.Vencidas.Count, ars.Vencidas.Amount));

        Assert.Equal([0, 30, 31, 60, 61, 90, 91], maturities.Upcoming.Select(u => u.DaysUntil));
        Assert.Equal(new DateOnly(2026, 10, 23), maturities.Upcoming[0].ValidUntil);
    }

    [Fact]
    public async Task Maturities_Between2100And2400InBuenosAires_ADueTodayOperationIsAlreadyVencida_AsOnTheCupoScreen()
    {
        await using var f = DashboardFixture.Create(nameof(Maturities_Between2100And2400InBuenosAires_ADueTodayOperationIsAlreadyVencida_AsOnTheCupoScreen));
        var line = f.AddCreditLine(f.AddEntity().EntityId);
        var dueArgentineToday    = f.AddOperation(line, 10m, validUntil: new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc));
        var dueArgentineTomorrow = f.AddOperation(line, 20m, validUntil: new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc));
        await f.SaveAsync();

        // 01:30 UTC on the 24th is 22:30 on the 23rd in Buenos Aires.
        var lateEvening = new DateTime(2026, 10, 24, 1, 30, 0, DateTimeKind.Utc);
        var maturities  = (await PortfolioAsync(f, lateEvening)).Maturities!;

        Assert.Equal(new DateOnly(2026, 10, 23), maturities.AsOfDate);
        Assert.Equal("Vencida", CreditLineRules.EffectiveStatus(dueArgentineToday, lateEvening));
        var ars = Assert.Single(maturities.ByCurrency);
        Assert.Equal((1, 10m), (ars.Vencidas.Count, ars.Vencidas.Amount));
        Assert.Equal((1, 20m), (ars.Days0To30.Count, ars.Days0To30.Amount));
        var upcoming = Assert.Single(maturities.Upcoming);
        Assert.Equal((dueArgentineTomorrow.OperationId, 1), (upcoming.OperationId, upcoming.DaysUntil));
    }

    /// <summary>
    /// Every BCRA case the signals rule distinguishes, on one fixture: worse by 2 and by 1, equal, better,
    /// never queried, a failed lookup on either side, several cupos of one PyME from different analyses, and
    /// a cupo whose analysis has no result. Shared with the PYME_BCRA_WORSENED parity test.
    /// </summary>
    private static (Entity WorseBy2, Entity WorseBy1) SeedBcraCases(DashboardFixture f)
    {
        Entity Case(string? nowJson, string? thenJson, string currency = "ARS", DateTime? generatedAt = null, Entity? entity = null)
        {
            entity ??= f.AddEntity();
            entity.BcraDataJson      = nowJson;
            entity.BcraDataUpdatedAt = Now.AddDays(-1);
            var (analysis, _) = f.AddAnalysis(Guid.NewGuid(), entity.EntityId, result: r =>
            {
                r.BcraDataJson = thenJson;
                r.GeneratedAt  = generatedAt ?? Now.AddDays(-30);
            });
            f.AddCreditLine(entity.EntityId, currency, originAnalysisId: analysis.AnalysisId);
            return entity;
        }

        string Debt(int s) => DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, s);
        var noDebt = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.NoDebtReported, null);
        var failed = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.LookupFailed, null);

        var worseBy2   = Case(Debt(4), Debt(2));
        var worseBy1   = Case(Debt(2), noDebt);                  // no debt then reads as situación 1
        Case(Debt(3), Debt(3));                                   // equal
        Case(Debt(2), Debt(4));                                   // better
        Case(null, Debt(1));                                      // never queried now
        Case(failed, Debt(1));                                    // lookup failed now
        Case(Debt(5), failed);                                    // lookup failed then
        var multi = Case(Debt(3), noDebt, "ARS", Now.AddDays(-60));
        Case(Debt(3), Debt(3), "USD", Now.AddDays(-10), multi);  // the latest analysis behind its cupos: equal
        var noResult = f.AddEntity();
        noResult.BcraDataJson = Debt(6);
        f.AddCreditLine(noResult.EntityId, originAnalysisId: AddAnalysisWithoutResult(f, noResult.EntityId).AnalysisId);
        return (worseBy2, worseBy1);
    }

    [Fact]
    public async Task BcraSignals_OnlyWhenTheSituacionIsWorseThanAtTheCuposLatestAnalysis()
    {
        await using var f = DashboardFixture.Create(nameof(BcraSignals_OnlyWhenTheSituacionIsWorseThanAtTheCuposLatestAnalysis));
        var (worseBy2, worseBy1) = SeedBcraCases(f);
        await f.SaveAsync();

        var signals = (await PortfolioAsync(f)).BcraSignals!;

        Assert.Equal(2, signals.TotalCount);
        Assert.Equal([worseBy2.EntityId, worseBy1.EntityId], signals.Items.Select(s => s.EntityId));
        Assert.Equal((2, 4), (signals.Items[0].SituacionThen, signals.Items[0].SituacionNow));
        Assert.Equal((1, 2), (signals.Items[1].SituacionThen, signals.Items[1].SituacionNow));
        Assert.Equal(Now.AddDays(-1), signals.Items[0].LastQueriedAt);
        Assert.NotNull(signals.Items[0].EntityName);
    }

    /// <summary>
    /// The dashboard's bcraSignals and the PYME_BCRA_WORSENED alerts on the same fixture: the same PyMEs,
    /// with the same situación then and now — both read <see cref="BcraSignalRule"/>, so they cannot disagree.
    /// </summary>
    [Fact]
    public async Task BcraSignals_AndPymeBcraWorsenedAlerts_AgreeOnTheSameFixture()
    {
        await using var f = DashboardFixture.Create(nameof(BcraSignals_AndPymeBcraWorsenedAlerts_AgreeOnTheSameFixture));
        SeedBcraCases(f);
        await f.SaveAsync();

        var signals = (await PortfolioAsync(f)).BcraSignals!;

        await new CupoAlertService(
                new CreditLineRepository(f.Context, NullLogger<CreditLineRepository>.Instance),
                f.Repository(),
                new AlertRepository(f.Context, NullLogger<AlertRepository>.Instance),
                NullLogger<CupoAlertService>.Instance)
            .ReconcileAsync(Tenant, Now, Ct);
        f.Context.ChangeTracker.Clear();

        var alerts = await f.Context.Alerts.AsNoTracking()
            .Where(a => a.TenantId == Tenant && a.AlertType == EventAlertTypes.PymeBcraWorsened && !a.IsResolved)
            .ToListAsync();

        Assert.Equal(2, signals.TotalCount);
        Assert.Equal(
            signals.Items.Select(s => (s.EntityId, (decimal)s.SituacionThen, (decimal)s.SituacionNow, (Guid?)s.AnalysisId)).OrderBy(x => x.EntityId),
            alerts.Select(a => (a.EntityId!.Value, a.ThresholdValue!.Value, a.MetricValue!.Value, a.AnalysisId)).OrderBy(x => x.Item1));
    }

    [Fact]
    public async Task BcraSignals_AreCappedAtTwenty_WithTheWholeCount()
    {
        await using var f = DashboardFixture.Create(nameof(BcraSignals_AreCappedAtTwenty_WithTheWholeCount));
        for (var i = 0; i < 23; i++)
        {
            var e = f.AddEntity();
            e.BcraDataJson = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, 3);
            var (analysis, _) = f.AddAnalysis(Guid.NewGuid(), e.EntityId,
                result: r => r.BcraDataJson = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, 1));
            f.AddCreditLine(e.EntityId, originAnalysisId: analysis.AnalysisId);
        }
        await f.SaveAsync();

        var signals = (await PortfolioAsync(f)).BcraSignals!;

        Assert.Equal(23, signals.TotalCount);
        Assert.Equal(20, signals.Items.Count);
    }

    [Fact]
    public async Task IsTenantIsolated()
    {
        await using var f = DashboardFixture.Create(nameof(IsTenantIsolated));
        var foreign = f.AddEntity(tenant: DashboardFixture.Other);
        foreign.BcraDataJson = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, 6);
        var (analysis, _) = f.AddAnalysis(Guid.NewGuid(), foreign.EntityId, tenant: DashboardFixture.Other,
            result: r => r.BcraDataJson = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, 1));
        var line = f.AddCreditLine(foreign.EntityId, originAnalysisId: analysis.AnalysisId, tenant: DashboardFixture.Other);
        f.AddOperation(line, 10m, validUntil: Now.Date.AddDays(3));
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Empty(response.DegradedSections);
        Assert.Empty(response.Totals!);
        Assert.Empty(response.ByRisk!);
        Assert.Empty(response.TopExposures!);
        Assert.Empty(response.BySector!);
        Assert.Empty(response.Maturities!.ByCurrency);
        Assert.Empty(response.Maturities.Upcoming);
        Assert.Equal(0, response.BcraSignals!.TotalCount);
        Assert.Empty(response.HighUtilization!);
    }

    [Fact]
    public async Task AFailingSection_IsNullAndListed_TheRestStillReturns()
    {
        await using var f = DashboardFixture.Create(nameof(AFailingSection_IsNullAndListed_TheRestStillReturns));
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 100m), 40m);
        await f.SaveAsync();

        var failing  = new FailingDashboardRepository(f.Repository(),
            nameof(IDashboardRepository.GetOccupiedByRiskAsync), nameof(IDashboardRepository.GetBcraComparisonsAsync));
        var response = await PortfolioAsync(f, repository: failing);

        Assert.Equal([DashboardSections.ByRisk, DashboardSections.BcraSignals], response.DegradedSections);
        Assert.Null(response.ByRisk);
        Assert.Null(response.BcraSignals);
        Assert.Equal(40m, Assert.Single(response.Totals!).Utilizado);
        Assert.NotNull(response.TopExposures);
        Assert.NotNull(response.BySector);
        Assert.NotNull(response.Maturities);
    }

    // ── highUtilization ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HighUtilization_StartsAtExactlyNinetyPercent_OnTheExactAmounts()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_StartsAtExactlyNinetyPercent_OnTheExactAmounts));
        CreditLine Line(params decimal[] used)
        {
            var line = f.AddCreditLine(f.AddEntity().EntityId, amount: 10_000m);
            foreach (var amount in used)
                f.AddOperation(line, amount);
            return line;
        }

        var below    = Line(8_999m);           // 89.99 %
        var ninety   = Line(4_500m, 4_500m);   // 90 %, summed over two operations
        var full     = Line(10_000m);          // 100 %
        var exceeded = Line(12_345m);          // 123.45 %
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Empty(response.DegradedSections);
        var ars = Assert.Single(response.HighUtilization!);
        Assert.Equal(("ARS", 3), (ars.Currency, ars.TotalCount));
        Assert.Equal([exceeded.CreditLineId, full.CreditLineId, ninety.CreditLineId], ars.Items.Select(i => i.CreditLineId));
        Assert.Equal([123.5m, 100.0m, 90.0m], ars.Items.Select(i => i.UtilizationPct));   // half away from zero
        Assert.Equal([12_345m, 10_000m, 9_000m], ars.Items.Select(i => i.Utilizado));
        Assert.Equal([-2_345m, 0m, 1_000m], ars.Items.Select(i => i.Disponible));
        Assert.All(ars.Items, i => Assert.Equal(10_000m, i.Otorgado));
        Assert.DoesNotContain(ars.Items, i => i.CreditLineId == below.CreditLineId);
    }

    [Fact]
    public async Task HighUtilization_ExcludesAmountZero_SuspendedCupos_AndCanceladaOperations()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_ExcludesAmountZero_SuspendedCupos_AndCanceladaOperations));
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 0m), 10m);
        f.AddCreditLine(f.AddEntity().EntityId, amount: 0m);                                   // 0 × 100 ≥ 0 × 90 holds
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 100m, status: "Suspended"), 100m);

        var halfCancelled = f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m);
        f.AddOperation(halfCancelled, 500m);
        f.AddOperation(halfCancelled, 500m, status: "Cancelada");

        var occupied = f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m);
        f.AddOperation(occupied, 500m, status: "Aprobada");
        f.AddOperation(occupied, 450m, validUntil: Now.Date.AddDays(-5));                     // Vencida still occupies
        f.AddOperation(occupied, 900m, status: "Cancelada");
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Empty(response.DegradedSections);
        var ars  = Assert.Single(response.HighUtilization!);
        var item = Assert.Single(ars.Items);
        Assert.Equal((1, occupied.CreditLineId, 950m, 50m, 95.0m),
            (ars.TotalCount, item.CreditLineId, item.Utilizado, item.Disponible, item.UtilizationPct));
    }

    [Fact]
    public async Task HighUtilization_KeepsCurrenciesApart_AndListsOnlyCurrenciesWithACupoPastTheThreshold()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_KeepsCurrenciesApart_AndListsOnlyCurrenciesWithACupoPastTheThreshold));
        var entity = f.AddEntity();
        var usd = f.AddCreditLine(entity.EntityId, "USD", 10m);
        f.AddOperation(usd, 9m);
        var ars = f.AddCreditLine(entity.EntityId, "ARS", 1_000m);
        f.AddOperation(ars, 950m);
        f.AddOperation(f.AddCreditLine(entity.EntityId, "EUR", 100m), 50m);
        await f.SaveAsync();

        var high = (await PortfolioAsync(f)).HighUtilization!;

        Assert.Equal(["ARS", "USD"], high.Select(h => h.Currency));
        var arsItem = Assert.Single(high[0].Items);
        var usdItem = Assert.Single(high[1].Items);
        Assert.Equal((1, ars.CreditLineId, 1_000m, 950m, 95.0m),
            (high[0].TotalCount, arsItem.CreditLineId, arsItem.Otorgado, arsItem.Utilizado, arsItem.UtilizationPct));
        Assert.Equal((1, usd.CreditLineId, 10m, 9m, 90.0m),
            (high[1].TotalCount, usdItem.CreditLineId, usdItem.Otorgado, usdItem.Utilizado, usdItem.UtilizationPct));
    }

    [Fact]
    public async Task HighUtilization_IsTenPerCurrency_WithTheWholeCount()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_IsTenPerCurrency_WithTheWholeCount));
        for (var i = 0; i < 12; i++)
            f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m), 900m + i * 5m);   // 90.0 % .. 95.5 %
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, "USD", 10m), 10m);
        await f.SaveAsync();

        var high = (await PortfolioAsync(f)).HighUtilization!.ToDictionary(h => h.Currency);

        Assert.Equal((12, 10), (high["ARS"].TotalCount, high["ARS"].Items.Count));
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(i => 90m + i * 0.5m), high["ARS"].Items.Select(i => i.UtilizationPct));
        Assert.Equal((1, 1), (high["USD"].TotalCount, high["USD"].Items.Count));
    }

    [Fact]
    public async Task HighUtilization_OrdersByUtilizationThenOtorgado_WithNamesBucketAndResult()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_OrdersByUtilizationThenOtorgado_WithNamesBucketAndResult));
        var (analysis, result) = f.AddAnalysis(Guid.NewGuid(), result: r => r.AiSuggestedLevel = "High");
        CreditLine Line(decimal amount, decimal used, string? name = null, Guid? analysisId = null)
        {
            var line = f.AddCreditLine(f.AddEntity(name: name).EntityId, amount: amount, originAnalysisId: analysisId);
            f.AddOperation(line, used);
            return line;
        }

        var legacy   = Line(100m, 91m);                                       // 91 %, no analysis
        var smallTie = Line(1_000m, 920m);                                    // 92 %
        var top      = Line(1_000m, 950m, "PyME Top", analysis.AnalysisId);   // 95 %
        var bigTie   = Line(5_000m, 4_600m);                                  // 92 %, larger otorgado
        await f.SaveAsync();

        var items = Assert.Single((await PortfolioAsync(f)).HighUtilization!).Items;

        Assert.Equal([top.CreditLineId, bigTie.CreditLineId, smallTie.CreditLineId, legacy.CreditLineId], items.Select(i => i.CreditLineId));
        Assert.Equal([95.0m, 92.0m, 92.0m, 91.0m], items.Select(i => i.UtilizationPct));
        var first = items[0];
        Assert.Equal(("PyME Top", top.EntityId, DashboardRiskBuckets.High, (Guid?)analysis.AnalysisId, (Guid?)result.ResultId),
            (first.EntityName, first.EntityId, first.RiskBucket, first.AnalysisId, first.ResultId));
        Assert.NotNull(first.EntityCuit);
        Assert.Equal((DashboardRiskBuckets.Unclassified, (Guid?)null, (Guid?)null), (items[3].RiskBucket, items[3].AnalysisId, items[3].ResultId));
    }

    [Fact]
    public async Task HighUtilization_IsTenantIsolated_OnTheCuposAndOnTheirOperations()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_IsTenantIsolated_OnTheCuposAndOnTheirOperations));
        var local = f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m);
        f.AddOperation(local, 300m);
        f.AddOperation(local, 7_000m, tenant: DashboardFixture.Other);     // tenant-2 row, tenant-1 cupo id
        var foreign = f.AddCreditLine(f.AddEntity(tenant: DashboardFixture.Other).EntityId, amount: 100m, tenant: DashboardFixture.Other);
        f.AddOperation(foreign, 100m);
        await f.SaveAsync();

        var mine   = await PortfolioAsync(f);
        var theirs = await f.Service().GetPortfolioAsync(DashboardFixture.Other, Now, Ct);

        Assert.Empty(mine.DegradedSections);
        Assert.Empty(mine.HighUtilization!);
        var other = Assert.Single(Assert.Single(theirs.HighUtilization!).Items);
        Assert.Equal((foreign.CreditLineId, 100m), (other.CreditLineId, other.Utilizado));
    }

    [Theory]
    [InlineData(nameof(IDashboardRepository.GetHighUtilizationCountsAsync))]
    [InlineData(nameof(IDashboardRepository.GetHighUtilizationAsync))]
    public async Task HighUtilization_AFailingQuery_DegradesOnlyThatSection(string method)
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_AFailingQuery_DegradesOnlyThatSection) + method);
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 100m), 95m);
        await f.SaveAsync();

        var response = await PortfolioAsync(f, repository: new FailingDashboardRepository(f.Repository(), method));

        Assert.Equal([DashboardSections.HighUtilization], response.DegradedSections);
        Assert.Null(response.HighUtilization);
        Assert.Equal(95m, Assert.Single(response.Totals!).Utilizado);
        Assert.NotNull(response.ByRisk);
        Assert.Equal(95m, Assert.Single(Assert.Single(response.TopExposures!).Items).Utilizado);
        Assert.NotNull(response.BySector);
        Assert.NotNull(response.Maturities);
        Assert.NotNull(response.BcraSignals);
    }

    [Fact]
    public async Task Cancellation_IsNotASectionFailure()
    {
        await using var f = DashboardFixture.Create(nameof(Cancellation_IsNotASectionFailure));
        await f.SaveAsync();

        var cancelling = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetTopExposuresAsync),
            nameof(IDashboardRepository.GetActiveLineCurrenciesAsync))
        {
            Throw = () => new OperationCanceledException()
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => PortfolioAsync(f, repository: cancelling));
    }
}
