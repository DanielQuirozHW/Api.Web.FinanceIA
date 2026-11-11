// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-17

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="CreditLineOperationRepository.HasNonCancelledByAnalysisAsync"/>,
/// run against the EF Core in-memory provider (a database provider, not a mocking library) so the
/// actual status WHERE clause is exercised — behaviour a hand-rolled stub cannot verify, because the
/// controller stub simply returns whatever boolean it is handed.
///
/// This is where the analysis-rejection guard is genuinely pinned. That guard asks "does this
/// analysis still justify live exposure?" and answers with an EXCLUSION predicate (stored status
/// != 'Cancelada'), which is why introducing 'Aprobada' required no change there: an approved-but-
/// not-yet-confirmed operation is live exposure, because it already reserves cupo. Rewriting the
/// predicate as an inclusion list (== 'Vigente') would silently let an analysis be rejected out from
/// under a live 'Aprobada' operation, and these tests fail if anyone does.
///
/// ConfirmVigenciaAsync and CancelAsync are deliberately NOT covered here: both use
/// ExecuteUpdateAsync, which the in-memory provider does not implement. Same reason
/// CreditLineRepository.SetStatusAsync and RaiseAsync are exercised only through controller stubs.
/// </summary>
public sealed class CreditLineOperationRepositoryTests
{
    private const string Tenant = "tenant-1";

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"creditlineops-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static CreditLineOperation Operation(
        Guid analysisId, string status, string tenantId = Tenant) => new()
    {
        OperationId     = Guid.NewGuid(),
        TenantId        = tenantId,
        CreditLineId    = Guid.NewGuid(),
        AnalysisId      = analysisId,
        Type            = 3,
        Amount          = 100m,
        Currency        = "ARS",
        Status          = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow,
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params CreditLineOperation[] ops)
    {
        ctx.CreditLineOperations.AddRange(ops);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static CreditLineOperationRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<CreditLineOperationRepository>.Instance);

    // ── Tests ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline: an 'Aprobada' operation is live exposure. It reserves cupo from creation, so an
    /// analyst must not be able to reject the analysis that justifies it.
    /// </summary>
    [Fact]
    public async Task HasNonCancelledByAnalysis_WhenOnlyOperationIsAprobada_ReturnsTrue()
    {
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Operation(analysisId, "Aprobada"));

        var result = await Repo(ctx).HasNonCancelledByAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task HasNonCancelledByAnalysis_WhenOnlyOperationIsVigente_ReturnsTrue()
    {
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Operation(analysisId, "Vigente"));

        var result = await Repo(ctx).HasNonCancelledByAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.True(result);
    }

    /// <summary>Cancellation is the only thing that clears the block.</summary>
    [Fact]
    public async Task HasNonCancelledByAnalysis_WhenOnlyOperationIsCancelada_ReturnsFalse()
    {
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Operation(analysisId, "Cancelada"));

        var result = await Repo(ctx).HasNonCancelledByAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.False(result);
    }

    /// <summary>A cancelled sibling does not mask a live 'Aprobada' one.</summary>
    [Fact]
    public async Task HasNonCancelledByAnalysis_WhenCanceladaAndAprobadaCoexist_ReturnsTrue()
    {
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Operation(analysisId, "Cancelada"), Operation(analysisId, "Aprobada"));

        var result = await Repo(ctx).HasNonCancelledByAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.True(result);
    }

    /// <summary>Another tenant's live operation must never block this tenant's rejection.</summary>
    [Fact]
    public async Task HasNonCancelledByAnalysis_WhenOperationBelongsToAnotherTenant_ReturnsFalse()
    {
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Operation(analysisId, "Aprobada", tenantId: "tenant-2"));

        var result = await Repo(ctx).HasNonCancelledByAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.False(result);
    }
}
