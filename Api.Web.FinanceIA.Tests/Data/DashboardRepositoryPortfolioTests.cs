// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// The portfolio reads of <see cref="Api.Web.FinanceIA.Data.Repositories.DashboardRepository"/> on the
/// in-memory provider: which rows count. Every test seeds the other tenant on every table — including keys
/// that would JOIN across tenants if a predicate were missing — and asserts none of it reaches the answer.
/// </summary>
public sealed class DashboardRepositoryPortfolioTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Other = DashboardFixture.Other;

    /// <summary>
    /// A cupo of tenant-2, an operation of tenant-2 hung on a tenant-1 cupo id, and a tenant-1 cupo whose
    /// analysis id resolves only in tenant-2. None of it may count for tenant-1.
    /// </summary>
    private static (CreditLine Local, CreditLine Crossed) SeedCrossTenantNoise(DashboardFixture f, Entity local)
    {
        var foreignEntity = f.AddEntity(tenant: Other);
        var foreignLine   = f.AddCreditLine(foreignEntity.EntityId, amount: 9_999m, tenant: Other);
        f.AddOperation(foreignLine, 9_999m, tenant: Other);

        var line = f.AddCreditLine(local.EntityId, amount: 1_000m);
        f.AddOperation(line, 300m);
        f.AddOperation(line, 7_777m, tenant: Other);   // tenant-2 row, tenant-1 cupo id

        var (foreignAnalysis, _) = f.AddAnalysis(Guid.NewGuid(), foreignEntity.EntityId,
            result: r => r.AiSuggestedLevel = "Critical", tenant: Other);
        var crossed = f.AddCreditLine(f.AddEntity().EntityId, currency: "USD", amount: 50m, originAnalysisId: foreignAnalysis.AnalysisId);

        return (line, crossed);
    }

    [Fact]
    public async Task LineTotals_GroupActiveAndSuspendedByCurrency_CountDistinctEntities_InThisTenantOnly()
    {
        await using var f = DashboardFixture.Create(nameof(LineTotals_GroupActiveAndSuspendedByCurrency_CountDistinctEntities_InThisTenantOnly));
        var e1 = f.AddEntity();
        var e2 = f.AddEntity();
        SeedCrossTenantNoise(f, e1);
        f.AddCreditLine(e2.EntityId, amount: 2_000m);
        f.AddCreditLine(e2.EntityId, amount: 400m, status: "Suspended");
        f.AddCreditLine(e2.EntityId, amount: 123m, status: "Superseded");
        await f.SaveAsync();

        var rows = await f.Repository().GetLineTotalsAsync(DashboardFixture.Tenant, Ct);

        var ars = Assert.Single(rows, r => r.Currency == "ARS" && r.Status == "Active");
        Assert.Equal((2, 2, 3_000m), (ars.Count, ars.EntityCount, ars.Amount));
        var suspended = Assert.Single(rows, r => r.Status == "Suspended");
        Assert.Equal((1, 400m), (suspended.Count, suspended.Amount));
        Assert.Equal(50m, Assert.Single(rows, r => r.Currency == "USD").Amount);
        Assert.DoesNotContain(rows, r => r.Status == "Superseded");
    }

    [Fact]
    public async Task OccupiedByCurrency_SumsNonCancelledOperationsOfActiveCupos_TenantScopedOnBothSides()
    {
        await using var f = DashboardFixture.Create(nameof(OccupiedByCurrency_SumsNonCancelledOperationsOfActiveCupos_TenantScopedOnBothSides));
        var entity = f.AddEntity();
        var (line, _) = SeedCrossTenantNoise(f, entity);
        f.AddOperation(line, 200m, status: "Aprobada");
        f.AddOperation(line, 50m, status: "Cancelada");
        f.AddOperation(line, 25m, validUntil: DashboardFixture.Now.AddDays(-40));   // effective Vencida still occupies
        var suspended = f.AddCreditLine(entity.EntityId, status: "Suspended");
        f.AddOperation(suspended, 1_000m);
        await f.SaveAsync();

        var rows = await f.Repository().GetOccupiedByCurrencyAsync(DashboardFixture.Tenant, Ct);

        var ars = Assert.Single(rows);
        Assert.Equal(("ARS", 525m), (ars.Currency, ars.Amount));
    }

    [Fact]
    public async Task ByRisk_ReadsTheRaiseAnalysisFirst_ThenTheOrigin_AndOnlyInThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(ByRisk_ReadsTheRaiseAnalysisFirst_ThenTheOrigin_AndOnlyInThisTenant));
        var entity = f.AddEntity();
        var (_, crossed) = SeedCrossTenantNoise(f, entity);
        var (origin, _) = f.AddAnalysis(Guid.NewGuid(), entity.EntityId, result: r => r.AiSuggestedLevel = "Low");
        var (raise, _)  = f.AddAnalysis(Guid.NewGuid(), entity.EntityId, result: r => r.AiSuggestedLevel = "High");
        var raised = f.AddCreditLine(entity.EntityId, currency: "EUR", amount: 10m,
            originAnalysisId: origin.AnalysisId, lastRaiseAnalysisId: raise.AnalysisId);
        f.AddOperation(raised, 4m);
        await f.SaveAsync();

        var lines = await f.Repository().GetLinesByRiskAsync(DashboardFixture.Tenant, Ct);
        var used  = await f.Repository().GetOccupiedByRiskAsync(DashboardFixture.Tenant, Ct);

        var eur = Assert.Single(lines, r => r.Currency == "EUR");
        Assert.Equal(("Balance", "High", 10m), (eur.AnalysisKind, eur.AiSuggestedLevel, eur.Amount));
        Assert.Equal(4m, Assert.Single(used, r => r.Currency == "EUR").Amount);

        // The tenant-1 cupo whose analysis lives in tenant-2 resolves to nothing.
        var usd = Assert.Single(lines, r => r.Currency == "USD");
        Assert.Equal(crossed.Amount, usd.Amount);
        Assert.Null(usd.AnalysisKind);
        Assert.Null(usd.AiSuggestedLevel);
    }

    [Fact]
    public async Task TopExposures_OrderByUtilizado_CapInTheQuery_PerCurrency_InThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(TopExposures_OrderByUtilizado_CapInTheQuery_PerCurrency_InThisTenant));
        var local = f.AddEntity();
        var (line, _) = SeedCrossTenantNoise(f, local);
        var big = f.AddCreditLine(f.AddEntity().EntityId, amount: 5_000m);
        f.AddOperation(big, 4_000m);
        f.AddOperation(big, 900m, status: "Cancelada");
        var none = f.AddCreditLine(f.AddEntity().EntityId, amount: 9_000m);
        await f.SaveAsync();

        var rows = await f.Repository().GetTopExposuresAsync(DashboardFixture.Tenant, "ARS", 2, Ct);

        Assert.Equal([big.CreditLineId, line.CreditLineId], rows.Select(r => r.CreditLineId));
        Assert.Equal([4_000m, 300m], rows.Select(r => r.Utilizado));
        Assert.Equal(["ARS", "ARS"], rows.Select(r => r.Currency));
        Assert.DoesNotContain(rows, r => r.CreditLineId == none.CreditLineId);
        Assert.Equal(["ARS", "USD"], await f.Repository().GetActiveLineCurrenciesAsync(DashboardFixture.Tenant, Ct));
    }

    [Fact]
    public async Task HighUtilization_CountsAndCapsInTheQuery_PerCurrency_InThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(HighUtilization_CountsAndCapsInTheQuery_PerCurrency_InThisTenant));
        SeedCrossTenantNoise(f, f.AddEntity());   // its tenant-1 ARS cupo is at 30 % without the 7,777 tenant-2 row
        var highest = f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m);
        f.AddOperation(highest, 990m);
        var middle = f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m);
        f.AddOperation(middle, 950m);
        var lowest = f.AddCreditLine(f.AddEntity().EntityId, amount: 1_000m);
        f.AddOperation(lowest, 900m);
        f.AddOperation(lowest, 500m, status: "Cancelada");
        await f.SaveAsync();

        var counts = await f.Repository().GetHighUtilizationCountsAsync(DashboardFixture.Tenant, 90m, Ct);
        var rows   = await f.Repository().GetHighUtilizationAsync(DashboardFixture.Tenant, "ARS", 90m, 2, Ct);

        var ars = Assert.Single(counts);
        Assert.Equal(("ARS", 3, 3_000m), (ars.Currency, ars.Count, ars.Amount));
        Assert.Equal([highest.CreditLineId, middle.CreditLineId], rows.Select(r => r.CreditLineId));
        Assert.Equal([990m, 950m], rows.Select(r => r.Utilizado));
        Assert.Empty(await f.Repository().GetHighUtilizationAsync(DashboardFixture.Tenant, "USD", 90m, 10, Ct));
    }

    [Fact]
    public async Task BySector_GroupsTheStoredSector_OfThisTenantsEntity()
    {
        await using var f = DashboardFixture.Create(nameof(BySector_GroupsTheStoredSector_OfThisTenantsEntity));
        var retail = f.AddEntity();
        retail.Sector = "Retail";
        var (line, _) = SeedCrossTenantNoise(f, retail);
        await f.SaveAsync();

        var lines = await f.Repository().GetLinesBySectorAsync(DashboardFixture.Tenant, Ct);
        var used  = await f.Repository().GetOccupiedBySectorAsync(DashboardFixture.Tenant, Ct);

        Assert.Equal(line.Amount, Assert.Single(lines, r => r.Sector == "Retail").Amount);
        Assert.Equal(300m, Assert.Single(used, r => r.Sector == "Retail").Amount);
        Assert.All(lines, r => Assert.True(r.Sector is null or "Retail"));
    }

    [Fact]
    public async Task Maturities_ReadOnlyStoredVigenteOperationsOfActiveCupos_ByCurrencyAndDate()
    {
        await using var f = DashboardFixture.Create(nameof(Maturities_ReadOnlyStoredVigenteOperationsOfActiveCupos_ByCurrencyAndDate));
        var entity = f.AddEntity();
        var line   = f.AddCreditLine(entity.EntityId);
        var day    = DashboardFixture.Now.Date.AddDays(10);
        f.AddOperation(line, 100m, validUntil: day.AddHours(5));
        f.AddOperation(line, 50m, validUntil: day);
        f.AddOperation(line, 999m, status: "Aprobada", validUntil: day);
        f.AddOperation(line, 999m, status: "Cancelada", validUntil: day);
        f.AddOperation(line, 999m);                                              // open-ended
        f.AddOperation(line, 999m, validUntil: day, tenant: Other);              // tenant-2 row on a local cupo
        f.AddOperation(f.AddCreditLine(entity.EntityId, status: "Suspended"), 999m, validUntil: day);
        var foreign = f.AddCreditLine(f.AddEntity(tenant: Other).EntityId, tenant: Other);
        f.AddOperation(foreign, 999m, validUntil: day);
        await f.SaveAsync();

        var groups = await f.Repository().GetMaturityGroupsAsync(DashboardFixture.Tenant, DashboardFixture.Now.Date.AddDays(91), Ct);
        var upcoming = await f.Repository().GetUpcomingMaturitiesAsync(DashboardFixture.Tenant, DashboardFixture.Now.Date, 10, Ct);

        var group = Assert.Single(groups);
        Assert.Equal(("ARS", day, 2, 150m), (group.Currency, group.ValidUntil, group.Count, group.Amount));
        Assert.Equal([50m, 100m], upcoming.Select(u => u.Amount));
        Assert.All(upcoming, u => Assert.Equal(entity.EntityId, u.EntityId));
    }

    [Fact]
    public async Task BcraComparisons_ReadTheScalarsOfBothSnapshots_AndNeverJoinAnotherTenant()
    {
        await using var f = DashboardFixture.Create(nameof(BcraComparisons_ReadTheScalarsOfBothSnapshots_AndNeverJoinAnotherTenant));
        var entity = f.AddEntity();
        entity.BcraDataJson      = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, 4);
        entity.BcraDataUpdatedAt = DashboardFixture.Now.AddDays(-2);
        var (analysis, result) = f.AddAnalysis(Guid.NewGuid(), entity.EntityId,
            result: r => r.BcraDataJson = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.NoDebtReported, null));
        f.AddCreditLine(entity.EntityId, originAnalysisId: analysis.AnalysisId);

        var (foreignAnalysis, _) = f.AddAnalysis(Guid.NewGuid(), tenant: Other,
            result: r => r.BcraDataJson = DashboardFixture.BcraJson(BcraDeudaConsultaEstado.DebtFound, 1));
        var crossed = f.AddEntity();
        f.AddCreditLine(crossed.EntityId, currency: "USD", originAnalysisId: foreignAnalysis.AnalysisId);
        f.AddCreditLine(f.AddEntity(tenant: Other).EntityId, tenant: Other);
        await f.SaveAsync();

        var rows = await f.Repository().GetBcraComparisonsAsync(DashboardFixture.Tenant, Ct);

        Assert.Equal(2, rows.Count);
        var row = Assert.Single(rows, r => r.EntityId == entity.EntityId);
        Assert.Equal(("0", "4", "1", null), (row.NowEstadoRaw, row.NowSituacionMaximaRaw, row.ThenEstadoRaw, row.ThenSituacionMaximaRaw));
        Assert.Equal((result.ResultId, entity.BcraDataUpdatedAt), (row.ResultId, row.BcraDataUpdatedAt));

        var other = Assert.Single(rows, r => r.EntityId == crossed.EntityId);
        Assert.Null(other.ResultId);
        Assert.Null(other.ThenEstadoRaw);
        Assert.Null(other.NowEstadoRaw);
    }
}
