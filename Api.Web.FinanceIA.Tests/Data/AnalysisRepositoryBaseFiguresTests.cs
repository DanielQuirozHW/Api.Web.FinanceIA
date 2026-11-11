// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// <see cref="AnalysisRepository.GetBalanceBaseFiguresAsync"/> on the InMemory provider, where
/// <c>SqlJsonFunctions.JsonValue</c> is client-evaluated with SQL Server's lax semantics.
/// </summary>
public sealed class AnalysisRepositoryBaseFiguresTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static FinanciaDbContext NewContext() => new(new DbContextOptionsBuilder<FinanciaDbContext>()
        .UseInMemoryDatabase($"analyses-base-figures-{Guid.NewGuid()}")
        .Options);

    private static Guid Seed(FinanciaDbContext db, string tenantId, string? balanceJson)
    {
        var analysisId = Guid.NewGuid();
        db.AnalysisResults.Add(new AnalysisResult
        {
            ResultId    = Guid.NewGuid(),
            AnalysisId  = analysisId,
            TenantId    = tenantId,
            BalanceJson = balanceJson,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return analysisId;
    }

    private static AnalysisRepository Sut(FinanciaDbContext db) => new(db, NullLogger<AnalysisRepository>.Instance);

    [Fact]
    public async Task ReadsTheSevenFiguresAsText_AndNullForAbsentOrJsonNull()
    {
        using var db = NewContext();
        var id = Seed(db, Tenant,
            """{"assets":1500.50,"current_assets":null,"liabilities":0,"equity":-20,"net_profit":1e3,"inventory":7}""");

        var figures = await Sut(db).GetBalanceBaseFiguresAsync(id, Tenant, CancellationToken.None);

        Assert.Equal(new BalanceBaseFiguresProjection
        {
            HasBalance  = true,
            Assets      = "1500.50",
            Liabilities = "0",
            Equity      = "-20",
            NetProfit   = "1e3",
        }, figures);
    }

    [Fact]
    public async Task ABalanceWithNoBaseFigure_ReadsAllNull()
    {
        using var db = NewContext();
        var id = Seed(db, Tenant, """{"balanced":false,"fiscal_year_detected":2019,"cash_flow":{}}""");

        var figures = await Sut(db).GetBalanceBaseFiguresAsync(id, Tenant, CancellationToken.None);

        Assert.Equal(new BalanceBaseFiguresProjection { HasBalance = true }, figures);
    }

    [Fact]
    public async Task NoStoredBalance_IsReportedAsSuch()
    {
        using var db = NewContext();
        var id = Seed(db, Tenant, null);

        var figures = await Sut(db).GetBalanceBaseFiguresAsync(id, Tenant, CancellationToken.None);

        Assert.Equal(new BalanceBaseFiguresProjection { HasBalance = false }, figures);
    }

    [Fact]
    public async Task AnotherTenantsResult_IsNeverRead()
    {
        using var db = NewContext();
        var id = Seed(db, Other, """{"assets":1}""");

        Assert.Null(await Sut(db).GetBalanceBaseFiguresAsync(id, Tenant, CancellationToken.None));
        Assert.Null(await Sut(db).GetBalanceBaseFiguresAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }
}
