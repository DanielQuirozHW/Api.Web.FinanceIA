// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="AnalysisRepository.GetLatestScoreByEntityIdsAsync"/>, the source of
/// the entity list's and header's <c>latestScore</c> / <c>latestRiskLevel</c> pair (score level
/// contract). Run against the EF Core in-memory provider so the join and its WHERE clause are
/// exercised rather than a stub's return value.
/// <para>
/// The pair must come from ONE result row: the score and the level the BFF stored beside it. A level
/// rebuilt from the score with any cutoffs is exactly the contradiction this exists to end, so the
/// fixtures store levels that the default cutoffs would NOT produce.
/// </para>
/// </summary>
public sealed class AnalysisRepositoryLatestScoreTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"analyses-latest-score-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static DateTime Utc(int day, int hour = 12) => new(2026, 10, day, hour, 0, 0, DateTimeKind.Utc);

    private static (Analysis Analysis, AnalysisResult Result) Scored(
        Guid entityId, DateTime createdAt, decimal? score, string? level,
        string tenantId = Tenant, DateTime? rescoredAt = null)
    {
        var analysis = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = tenantId,
            EntityId      = entityId,
            DocumentId    = Guid.NewGuid(),
            RequestedBy   = Guid.NewGuid(),
            Status        = "PendingReview",
            FiscalYear    = 2025,
            AiModel       = "claude-sonnet-4-6",
            AiTemperature = 0.00m,
            PromptVersion = "v1",
            SentToSGR     = false,
            CreatedAt     = createdAt
        };

        var result = new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = analysis.AnalysisId,
            TenantId         = tenantId,
            AiSuggestedScore = score,
            AiSuggestedLevel = level,
            AiInputHash      = "in",
            AiOutputHash     = "out",
            ValidationStatus = "Valid",
            EnrichmentStatus = "Complete",
            GeneratedAt      = createdAt.AddMinutes(2),
            RescoredAt       = rescoredAt
        };

        return (analysis, result);
    }

    private static async Task SeedAsync(FinanciaDbContext ctx, params (Analysis Analysis, AnalysisResult Result)[] rows)
    {
        ctx.Analyses.AddRange(rows.Select(r => r.Analysis));
        ctx.AnalysisResults.AddRange(rows.Select(r => r.Result));
        ctx.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> ReadAsync(
        FinanciaDbContext ctx, string tenantId, params Guid[] entityIds)
        => new AnalysisRepository(ctx, NullLogger<AnalysisRepository>.Instance)
            .GetLatestScoreByEntityIdsAsync(entityIds, tenantId, CancellationToken.None);

    [Fact]
    public async Task ReturnsTheLatestScoredResult_WithTheLevelStoredOnThatSameRow()
    {
        using var ctx = NewContext();
        var entity = Guid.NewGuid();
        var older  = Scored(entity, Utc(1), 20m, "Low");
        // 32 is "Low" under the default cutoffs; this tenant stored "Medium". The stored one wins.
        var newer  = Scored(entity, Utc(5), 32m, "Medium");
        await SeedAsync(ctx, older, newer);

        var latest = Assert.Single(await ReadAsync(ctx, Tenant, entity)).Value;

        Assert.Equal(entity,                    latest.EntityId);
        Assert.Equal(newer.Analysis.AnalysisId, latest.AnalysisId);
        Assert.Equal(32m,                       latest.Score);
        Assert.Equal("Medium",                  latest.Level);
        Assert.Equal(newer.Result.GeneratedAt,  latest.ScoredAt);
    }

    /// <summary>A newer analysis with no score (failed run, persona humana) does not hide the last real one.</summary>
    [Fact]
    public async Task SkipsANewerUnscoredResult()
    {
        using var ctx = NewContext();
        var entity   = Guid.NewGuid();
        var scored   = Scored(entity, Utc(1), 70m, "High");
        var unscored = Scored(entity, Utc(9), null, null);
        await SeedAsync(ctx, scored, unscored);

        var latest = Assert.Single(await ReadAsync(ctx, Tenant, entity)).Value;

        Assert.Equal(scored.Analysis.AnalysisId, latest.AnalysisId);
        Assert.Equal(70m,    latest.Score);
        Assert.Equal("High", latest.Level);
    }

    [Fact]
    public async Task AnEntityWithNoScoredResult_IsAbsent()
    {
        using var ctx = NewContext();
        var entity = Guid.NewGuid();
        await SeedAsync(ctx, Scored(entity, Utc(1), null, null));

        Assert.Empty(await ReadAsync(ctx, Tenant, entity, Guid.NewGuid()));
    }

    /// <summary>A re-scored result reports the re-score's time, not its original generation.</summary>
    [Fact]
    public async Task ScoredAt_IsTheRescoreTimeWhenThereIsOne()
    {
        using var ctx = NewContext();
        var entity = Guid.NewGuid();
        var row    = Scored(entity, Utc(1), 45m, "Medium", rescoredAt: Utc(20));
        await SeedAsync(ctx, row);

        Assert.Equal(Utc(20), Assert.Single(await ReadAsync(ctx, Tenant, entity)).Value.ScoredAt);
    }

    /// <summary>
    /// No EF global query filter exists: the TenantId predicates are the only thing keeping another
    /// tenant's score — even a newer one on the same entity id — out of this tenant's header.
    /// </summary>
    [Fact]
    public async Task IsTenantIsolated()
    {
        using var ctx = NewContext();
        var entity  = Guid.NewGuid();
        var mine    = Scored(entity, Utc(1), 10m, "Low");
        var theirs  = Scored(entity, Utc(9), 95m, "Critical", tenantId: Other);
        await SeedAsync(ctx, mine, theirs);

        var latest = Assert.Single(await ReadAsync(ctx, Tenant, entity)).Value;

        Assert.Equal(mine.Analysis.AnalysisId, latest.AnalysisId);
        Assert.Equal("Low", latest.Level);
    }

    [Fact]
    public async Task ReturnsOnePairPerRequestedEntity_AndIgnoresOthers()
    {
        using var ctx = NewContext();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var notAsked = Guid.NewGuid();
        await SeedAsync(ctx,
            Scored(a, Utc(1), 30m, "Low"),
            Scored(b, Utc(2), 62m, "High"),
            Scored(notAsked, Utc(3), 90m, "Critical"));

        var map = await ReadAsync(ctx, Tenant, a, b);

        Assert.Equal(2, map.Count);
        Assert.Equal("Low",  map[a].Level);
        Assert.Equal("High", map[b].Level);
        Assert.False(map.ContainsKey(notAsked));
    }

    [Fact]
    public async Task NoEntityIds_ReturnsEmptyWithoutQuerying()
    {
        using var ctx = NewContext();

        Assert.Empty(await ReadAsync(ctx, Tenant));
    }

    // ── The reliability inputs ride on the same row (K21) ────────────────────────

    [Fact]
    public async Task CarriesTheWinningRowsReliabilityInputs_AndOnlyItsBreakdown()
    {
        using var ctx = NewContext();
        var entity = Guid.NewGuid();
        var older  = Scored(entity, Utc(1), 20m, "Low");
        var newer  = Scored(entity, Utc(5), 32m, "Medium", rescoredAt: Utc(6));
        older.Result.ScoreBreakdownJson = """{"older":true}""";
        older.Result.EnrichmentStatus   = "Complete";
        newer.Result.ScoreBreakdownJson = """{"newer":true}""";
        newer.Result.EnrichmentStatus   = "Degraded";
        newer.Analysis.AnalysisKind     = AnalysisKinds.Balance;
        newer.Analysis.FiscalYear       = 2024;
        await SeedAsync(ctx, older, newer);

        var latest = Assert.Single(await ReadAsync(ctx, Tenant, entity)).Value;

        Assert.Equal(newer.Result.ResultId,    latest.ResultId);
        Assert.Equal("""{"newer":true}""",     latest.ScoreBreakdownJson);
        Assert.Equal("Degraded",               latest.EnrichmentStatus);
        Assert.Equal(AnalysisKinds.Balance,    latest.AnalysisKind);
        Assert.Equal(2024,                     latest.FiscalYear);
        Assert.Equal(newer.Result.GeneratedAt, latest.GeneratedAt);
        Assert.Equal(Utc(6),                   latest.RescoredAt);
        Assert.Equal(Utc(6),                   latest.ScoredAt);
    }

    [Fact]
    public async Task TheBreakdownRead_IsScopedToTheTenant()
    {
        using var ctx = NewContext();
        var entity = Guid.NewGuid();
        var mine   = Scored(entity, Utc(1), 50m, "Medium");
        var theirs = Scored(entity, Utc(9), 90m, "High", tenantId: Other);
        mine.Result.ScoreBreakdownJson   = """{"mine":true}""";
        theirs.Result.ScoreBreakdownJson = """{"theirs":true}""";
        await SeedAsync(ctx, mine, theirs);

        var latest = Assert.Single(await ReadAsync(ctx, Tenant, entity)).Value;

        Assert.Equal(mine.Result.ResultId, latest.ResultId);
        Assert.Equal("""{"mine":true}""",  latest.ScoreBreakdownJson);
    }
}
