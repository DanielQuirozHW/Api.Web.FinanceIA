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
/// Query-level tests for <see cref="CreditLineRepository.GetActiveForCupoAlertsAsync"/> on the EF Core
/// in-memory provider: Active cupos only, cupo-occupying operations only, this tenant only — on BOTH
/// sides of the join, because an operation row of another tenant pointing at this tenant's cupo must
/// not eat into its disponible.
/// </summary>
public sealed class CreditLineRepositoryCupoAlertsTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"creditline-cupo-alerts-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static CreditLine Cupo(string status, string tenantId = Tenant) => new()
    {
        CreditLineId        = Guid.NewGuid(),
        TenantId            = tenantId,
        EntityId            = Guid.NewGuid(),
        Amount              = 1_000m,
        Currency            = "USD",
        Status              = status,
        CreatedByUserId     = Guid.NewGuid(),
        CreatedAt           = DateTime.UtcNow,
        OriginAnalysisId    = Guid.NewGuid(),
        LastRaiseAnalysisId = null,
    };

    private static CreditLineOperation Operacion(CreditLine cupo, decimal amount, string status, string? tenantId = null) => new()
    {
        OperationId     = Guid.NewGuid(),
        TenantId        = tenantId ?? cupo.TenantId,
        CreditLineId    = cupo.CreditLineId,
        AnalysisId      = Guid.NewGuid(),
        Amount          = amount,
        Currency        = cupo.Currency,
        Status          = status,
        ValidUntil      = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetActiveForCupoAlerts_ReturnsActiveCuposWithTheirOccupyingOperationsOfThisTenantOnly()
    {
        await using var ctx = NewContext();
        var active     = Cupo("Active");
        var bare       = Cupo("Active");
        var suspended  = Cupo("Suspended");
        var superseded = Cupo("Superseded");
        var theirs     = Cupo("Active", Other);

        var vigente   = Operacion(active, 100m, CreditLineRules.Vigente);
        var aprobada  = Operacion(active, 200m, CreditLineRules.Aprobada);
        var cancelada = Operacion(active, 400m, CreditLineRules.Cancelada);
        var foreign   = Operacion(active, 800m, CreditLineRules.Vigente, tenantId: Other);

        ctx.CreditLines.AddRange(active, bare, suspended, superseded, theirs);
        ctx.CreditLineOperations.AddRange(
            vigente, aprobada, cancelada, foreign,
            Operacion(suspended, 10m, CreditLineRules.Vigente),
            Operacion(superseded, 10m, CreditLineRules.Vigente),
            Operacion(theirs, 10m, CreditLineRules.Vigente));
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();

        var result = await new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance)
            .GetActiveForCupoAlertsAsync(Tenant, CancellationToken.None);

        Assert.Equal(2, result.Count);

        var source = Assert.Single(result, s => s.CreditLineId == active.CreditLineId);
        Assert.Equal(active.EntityId, source.EntityId);
        Assert.Equal(1_000m, source.Amount);
        Assert.Equal("USD", source.Currency);
        Assert.Equal(active.OriginAnalysisId, source.AnalysisId); // never raised → the origin analysis
        Assert.Equal(
            new[] { vigente.OperationId, aprobada.OperationId }.OrderBy(id => id),
            source.OccupyingOperations.Select(o => o.OperationId).OrderBy(id => id));
        Assert.Equal(300m, source.OccupyingOperations.Sum(o => o.Amount));
        Assert.All(source.OccupyingOperations, o => Assert.True(CreditLineRules.OccupiesCupo(o)));

        var empty = Assert.Single(result, s => s.CreditLineId == bare.CreditLineId);
        Assert.Empty(empty.OccupyingOperations);
    }

    [Fact]
    public async Task GetActiveForCupoAlerts_PrefersTheLastRaiseAnalysis()
    {
        await using var ctx = NewContext();
        var raised = Cupo("Active");
        raised.LastRaiseAnalysisId = Guid.NewGuid();
        ctx.CreditLines.Add(raised);
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();

        var result = await new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance)
            .GetActiveForCupoAlertsAsync(Tenant, CancellationToken.None);

        Assert.Equal(raised.LastRaiseAnalysisId, Assert.Single(result).AnalysisId);
    }

    /// <summary>
    /// The operations read compiled to real T-SQL — the in-memory provider runs every predicate as C#
    /// and cannot prove SQL Server translates them. Pins one statement, the tenant on both tables, the
    /// OccupiesCupo predicate in SQL, and that no Notes column travels. The server is never contacted.
    /// </summary>
    [Fact]
    public void CupoAlertOperationsQuery_Translates_AndScopesBothTables()
    {
        using var ctx = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options);

        var sql = new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance)
            .CupoAlertOperationsQuery(Tenant)
            .ToQueryString();

        Assert.Contains("FROM [dbo].[CreditLineOperations]", sql);
        Assert.Contains("INNER JOIN", sql);
        Assert.Contains("[c].[TenantId] = @", sql);
        Assert.Contains("[c0].[TenantId] = @", sql);
        Assert.Contains("N'Cancelada'", sql);
        Assert.Contains("N'Active'", sql);
        Assert.Contains("[c].[CreatedAt]", sql);
        Assert.Contains("[c].[VigenteConfirmadaEl]", sql);
        Assert.DoesNotContain("[Notes]", sql);
    }

    // ── GetLatestApprovedAnalysesOfActiveEntitiesAsync ───────────────────────────

    private static Analysis AnalysisOf(Guid entityId, string status, DateTime createdAt, DateTime? reviewedAt = null, string tenantId = Tenant) => new()
    {
        AnalysisId    = Guid.NewGuid(),
        TenantId      = tenantId,
        EntityId      = entityId,
        AnalysisKind  = AnalysisKinds.Balance,
        RequestedBy   = Guid.NewGuid(),
        Status        = status,
        FiscalYear    = 2025,
        AiModel       = "model",
        PromptVersion = "v1",
        CreatedAt     = createdAt,
        ReviewedAt    = reviewedAt
    };

    [Fact]
    public async Task GetLatestApprovedAnalyses_OnePerPymeWithAnActiveCupo_LatestApprovalOfThisTenantOnly()
    {
        await using var ctx = NewContext();
        var day = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var active   = Cupo("Active");
        var second   = Cupo("Active");
        second.EntityId = active.EntityId;                      // two cupos, one PyME
        var bare     = Cupo("Active");                          // no approved analysis
        var suspended = Cupo("Suspended");
        var theirs   = Cupo("Active", Other);

        var older    = AnalysisOf(active.EntityId, AnalysisStatuses.Approved, day.AddDays(-90), day.AddDays(-80));
        var latest   = AnalysisOf(active.EntityId, AnalysisStatuses.Approved, day.AddDays(-20));                 // CreatedAt stands in
        var pending  = AnalysisOf(active.EntityId, AnalysisStatuses.PendingReview, day.AddDays(-1));
        var foreign  = AnalysisOf(active.EntityId, AnalysisStatuses.Approved, day, day, tenantId: Other);
        var reviewed = AnalysisOf(bare.EntityId, AnalysisStatuses.Rejected, day.AddDays(-5), day.AddDays(-4));

        ctx.CreditLines.AddRange(active, second, bare, suspended, theirs);
        ctx.Analyses.AddRange(older, latest, pending, foreign, reviewed,
            AnalysisOf(suspended.EntityId, AnalysisStatuses.Approved, day),
            AnalysisOf(theirs.EntityId, AnalysisStatuses.Approved, day, tenantId: Other));
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();

        var result = await new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance)
            .GetLatestApprovedAnalysesOfActiveEntitiesAsync(Tenant, CancellationToken.None);

        Assert.Equal(2, result.Count);
        var pyme = Assert.Single(result, r => r.EntityId == active.EntityId);
        Assert.Equal((latest.AnalysisId, latest.CreatedAt), (pyme.AnalysisId, pyme.ApprovedAt));
        var none = Assert.Single(result, r => r.EntityId == bare.EntityId);
        Assert.Null(none.AnalysisId);
        Assert.Null(none.ApprovedAt);
    }

    [Fact]
    public async Task GetLatestApprovedAnalyses_ReviewedAtWinsOverCreatedAt_AndATieIsStable()
    {
        await using var ctx = NewContext();
        var day  = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cupo = Cupo("Active");
        // Created later, but approved earlier than the other one was.
        var createdLate = AnalysisOf(cupo.EntityId, AnalysisStatuses.Approved, day, day.AddDays(1));
        var reviewedLate = AnalysisOf(cupo.EntityId, AnalysisStatuses.Approved, day.AddDays(-30), day.AddDays(2));
        var tie = AnalysisOf(cupo.EntityId, AnalysisStatuses.Approved, day.AddDays(-40), day.AddDays(2));
        ctx.CreditLines.Add(cupo);
        ctx.Analyses.AddRange(createdLate, reviewedLate, tie);
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();

        var repository = new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance);
        var first  = Assert.Single(await repository.GetLatestApprovedAnalysesOfActiveEntitiesAsync(Tenant, CancellationToken.None));
        var second = Assert.Single(await repository.GetLatestApprovedAnalysesOfActiveEntitiesAsync(Tenant, CancellationToken.None));

        Assert.Equal(day.AddDays(2), first.ApprovedAt);
        Assert.Contains(first.AnalysisId!.Value, new[] { reviewedLate.AnalysisId, tie.AnalysisId });
        Assert.Equal(first.AnalysisId, second.AnalysisId);
    }

    /// <summary>
    /// The review read compiled to real T-SQL: one statement, one GROUP BY, the tenant on the cupos and on
    /// both reads of Analyses, the Approved / Active literals in SQL, and only ids and dates selected.
    /// </summary>
    [Fact]
    public void LatestApprovedAnalysesQuery_Translates_AndScopesEveryTable()
    {
        using var ctx = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options);

        var sql = new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance)
            .LatestApprovedAnalysesQuery(Tenant)
            .ToQueryString();

        Assert.Contains("FROM [dbo].[CreditLines]", sql);
        Assert.Contains("[dbo].[Analyses]", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("MAX(", sql);
        Assert.Contains("LEFT JOIN", sql);
        Assert.Contains("N'Approved'", sql);
        Assert.Contains("N'Active'", sql);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(sql, @"\.\[TenantId\] = @").Count);
        Assert.DoesNotContain("Json]", sql);
        Assert.DoesNotContain("[ReviewNotes]", sql);
    }
}
