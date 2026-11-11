// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="FiadorRepository"/>, run against the EF Core in-memory provider
/// (a database provider, not a mocking library) so the actual WHERE clauses and ordering are
/// exercised — behaviour a hand-rolled stub cannot verify, because a stub returns whatever it is
/// handed.
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in the controller tests, and for exactly that reason: the
/// controller tests stub this repository away, so they can show the controller passes a tenant id
/// but never that a query honours it. A fiador seeded under another tenant must be invisible to
/// every read on this type.
/// </para>
///
/// <para>
/// The other headline behaviour locked down: 'Removed' rows never appear in the list or in the
/// duplicate check, so a removed fiador can be re-added — matching the filtered unique index
/// [UX_Fiadores_ActivePerEntityCuit], which only guards Active rows.
/// </para>
///
/// <para>
/// <see cref="FiadorRepository.SetStatusAsync"/> (and, likewise, <c>SetNosisSnapshotAsync</c> and the
/// DDL 074 <c>UpdateDetailsAsync</c>) is NOT exercised here: each is a conditional
/// <c>ExecuteUpdateAsync</c>, which the in-memory provider does not implement, and the test project
/// has no relational provider to fall back on. <c>CreditLineRepositoryTests</c> omits its
/// <c>SetStatusAsync</c> and <c>RaiseAsync</c> for the same reason. The behaviour that matters to
/// callers — Active → Removed only, and false on a concurrent transition becoming a 409 rather than
/// a silent overwrite — is covered from the controller side in <c>FiadoresControllerTests</c>.
/// </para>
/// </summary>
public sealed class FiadorRepositoryTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"fiador-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static Fiador Guarantor(
        Guid entityId, string cuit, string status = "Active",
        string tenantId = Tenant, string? denominacion = null) => new()
    {
        FiadorId        = Guid.NewGuid(),
        TenantId        = tenantId,
        EntityId        = entityId,
        CUIT            = cuit,
        Denominacion    = denominacion,
        Cargo           = "Presidente",
        PorcAccionista  = 51.00m,
        Origen          = "Lufe",
        Status          = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow,
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Fiador[] fiadores)
    {
        ctx.Fiadores.AddRange(fiadores);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static FiadorRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<FiadorRepository>.Instance);

    // ── GetActiveByEntityAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveByEntity_ExcludesRemoved()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Guarantor(entityId, "20-11111111-1"),
            Guarantor(entityId, "27-22222222-2", status: "Removed"));

        var result = await Repo(ctx).GetActiveByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Equal("20-11111111-1", Assert.Single(result).CUIT);
    }

    [Fact]
    public async Task GetActiveByEntity_ExcludesOtherEntities()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Guarantor(entityId, "20-11111111-1"),
            Guarantor(Guid.NewGuid(), "27-22222222-2"));

        var result = await Repo(ctx).GetActiveByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Equal("20-11111111-1", Assert.Single(result).CUIT);
    }

    /// <summary>
    /// THE tenant test for the list read. Another tenant's fiador is not merely filtered from a
    /// response — it never leaves the database.
    /// </summary>
    [Fact]
    public async Task GetActiveByEntity_IsTenantIsolated()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        // Same entity id, different tenant: the id alone must not be enough to read the row.
        await SeedAsync(ctx, Guarantor(entityId, "20-11111111-1", tenantId: Other));

        var result = await Repo(ctx).GetActiveByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>Ordered by Denominacion then CUIT, so the Portal's list does not reshuffle between calls.</summary>
    [Fact]
    public async Task GetActiveByEntity_OrdersByDenominacionThenCuit()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Guarantor(entityId, "27-33333333-3", denominacion: "Zabala, Ana"),
            Guarantor(entityId, "20-11111111-1", denominacion: "Álvarez, Beto"),
            Guarantor(entityId, "23-22222222-2", denominacion: "Álvarez, Beto"));

        var result = await Repo(ctx).GetActiveByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Equal(
            new[] { "20-11111111-1", "23-22222222-2", "27-33333333-3" },
            result.Select(f => f.CUIT).ToArray());
    }

    // ── GetActiveByEntityCuitAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetActiveByEntityCuit_FindsTheActiveRow()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Guarantor(entityId, "20-11111111-1"));

        var result = await Repo(ctx)
            .GetActiveByEntityCuitAsync(entityId, "20-11111111-1", Tenant, CancellationToken.None);

        Assert.NotNull(result);
    }

    /// <summary>
    /// A removed fiador does not block re-adding the same CUIT — the same rule the filtered unique
    /// index enforces. If this read matched 'Removed' rows, a re-add would 409 forever.
    /// </summary>
    [Fact]
    public async Task GetActiveByEntityCuit_IgnoresRemovedRows()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Guarantor(entityId, "20-11111111-1", status: "Removed"));

        var result = await Repo(ctx)
            .GetActiveByEntityCuitAsync(entityId, "20-11111111-1", Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// The duplicate check is a plain string equality, so the caller must hand it the CUIT in the
    /// stored hyphenated form. An unformatted CUIT missing the hyphens finds nothing — which is why
    /// the controller normalises through FormatCuit before calling.
    /// </summary>
    [Fact]
    public async Task GetActiveByEntityCuit_DoesNotMatchUnformattedCuit()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Guarantor(entityId, "20-11111111-1"));

        var result = await Repo(ctx)
            .GetActiveByEntityCuitAsync(entityId, "20111111111", Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveByEntityCuit_IsTenantIsolated()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Guarantor(entityId, "20-11111111-1", tenantId: Other));

        var result = await Repo(ctx)
            .GetActiveByEntityCuitAsync(entityId, "20-11111111-1", Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    // ── GetByIdAsync ────────────────────────────────────────────────────────────

    /// <summary>
    /// Status-agnostic on purpose: the remove path needs to tell an already-'Removed' fiador (204)
    /// from a missing one (404), which an Active-only read could not do.
    /// </summary>
    [Fact]
    public async Task GetById_ReturnsRemovedRowsToo()
    {
        var entityId = Guid.NewGuid();
        var fiador   = Guarantor(entityId, "20-11111111-1", status: "Removed");
        using var ctx = NewContext();
        await SeedAsync(ctx, fiador);

        var result = await Repo(ctx).GetByIdAsync(fiador.FiadorId, Tenant, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Removed", result.Status);
    }

    [Fact]
    public async Task GetById_IsTenantIsolated()
    {
        var fiador = Guarantor(Guid.NewGuid(), "20-11111111-1", tenantId: Other);
        using var ctx = NewContext();
        await SeedAsync(ctx, fiador);

        var result = await Repo(ctx).GetByIdAsync(fiador.FiadorId, Tenant, CancellationToken.None);

        Assert.Null(result);
    }

    // ── CreateAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsTheRow()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();

        await Repo(ctx).CreateAsync(Guarantor(entityId, "20-11111111-1"), CancellationToken.None);

        var stored = Assert.Single(
            await ctx.Fiadores.AsNoTracking().Where(f => f.EntityId == entityId).ToListAsync());
        Assert.Equal("Active", stored.Status);
        Assert.Equal("Lufe",   stored.Origen);
        Assert.Null(stored.NosisDataJson);
        Assert.Null(stored.NosisFetchedAt);
    }

    // ── The soft remove ─────────────────────────────────────────────────────────

    /// <summary>
    /// The READ-SIDE consequence of a soft remove: once the row is 'Removed' it disappears from the
    /// entity's list, but it is still there — never hard-deleted — so the analyst's selection
    /// history survives and <see cref="FiadorRepository.GetByIdAsync"/> can still tell the remove
    /// path "already removed" (204) apart from "no such fiador" (404).
    ///
    /// <para>
    /// The transition is applied here by writing the row directly rather than by calling
    /// <see cref="FiadorRepository.SetStatusAsync"/>, because that method is a conditional
    /// <c>ExecuteUpdateAsync</c> and the in-memory provider does not implement it — the same reason
    /// <c>CreditLineRepositoryTests</c> covers neither <c>SetStatusAsync</c> nor <c>RaiseAsync</c>.
    /// What the conditional UPDATE itself guarantees (Active → Removed only, false on a concurrent
    /// transition → 409) is covered from the controller's side in <c>FiadoresControllerTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SoftRemovedRow_StopsBeingListed_ButIsNeverDeleted()
    {
        var entityId = Guid.NewGuid();
        var fiador   = Guarantor(entityId, "20-11111111-1");
        using var ctx = NewContext();
        await SeedAsync(ctx, fiador);
        var repo = Repo(ctx);

        Assert.Single(await repo.GetActiveByEntityAsync(entityId, Tenant, CancellationToken.None));

        var tracked = await ctx.Fiadores.SingleAsync(f => f.FiadorId == fiador.FiadorId);
        tracked.Status = "Removed";
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();

        Assert.Empty(await repo.GetActiveByEntityAsync(entityId, Tenant, CancellationToken.None));
        // The tombstone is still there, and still readable by id.
        Assert.NotNull(await repo.GetByIdAsync(fiador.FiadorId, Tenant, CancellationToken.None));
        Assert.Equal(1, await ctx.Fiadores.CountAsync());
    }
}
