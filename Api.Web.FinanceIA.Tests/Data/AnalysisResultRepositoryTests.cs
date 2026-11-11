// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-08

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for the AnalysisId → ResultId batch resolver
/// <see cref="AnalysisResultRepository.GetResultIdsByAnalysisIdsAsync"/> (used to build navigable
/// back-links from a cupo to its origin analysis without N+1), run against the EF Core in-memory
/// provider.
/// </summary>
public sealed class AnalysisResultRepositoryTests
{
    private const string Tenant = "tenant-1";

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analysisresult-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static async Task SeedAsync(FinanciaDbContext ctx, params AnalysisResult[] results)
    {
        ctx.AnalysisResults.AddRange(results);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static AnalysisResultRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<AnalysisResultRepository>.Instance);

    /// <summary>
    /// Maps each analysis id to its result id; an analysis with no result row is simply ABSENT from
    /// the map (the caller then leaves originAnalysisResultId null).
    /// </summary>
    [Fact]
    public async Task GetResultIdsByAnalysisIds_MapsAnalysisIdToResultId_AbsentWhenNoResult()
    {
        var withResult    = Guid.NewGuid();
        var withoutResult = Guid.NewGuid();
        var resultId      = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, new AnalysisResult { ResultId = resultId, AnalysisId = withResult, TenantId = Tenant });

        var map = await Repo(ctx).GetResultIdsByAnalysisIdsAsync(
            new[] { withResult, withoutResult }, Tenant, CancellationToken.None);

        Assert.Equal(resultId, map[withResult]);
        Assert.False(map.ContainsKey(withoutResult));
    }

    /// <summary>The resolver is tenant-isolated: another tenant's result never leaks into the map.</summary>
    [Fact]
    public async Task GetResultIdsByAnalysisIds_IsTenantIsolated()
    {
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, new AnalysisResult { ResultId = Guid.NewGuid(), AnalysisId = analysisId, TenantId = "other-tenant" });

        var map = await Repo(ctx).GetResultIdsByAnalysisIdsAsync(
            new[] { analysisId }, Tenant, CancellationToken.None);

        Assert.Empty(map);
    }

    /// <summary>Empty input short-circuits to an empty map (no query, no error).</summary>
    [Fact]
    public async Task GetResultIdsByAnalysisIds_WhenNoIds_ReturnsEmpty()
    {
        using var ctx = NewContext();

        var map = await Repo(ctx).GetResultIdsByAnalysisIdsAsync(
            Array.Empty<Guid>(), Tenant, CancellationToken.None);

        Assert.Empty(map);
    }
}
