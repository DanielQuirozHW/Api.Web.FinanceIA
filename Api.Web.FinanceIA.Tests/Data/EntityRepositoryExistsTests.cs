// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Covers <see cref="EntityRepository.ExistsByIdAsync"/> — the lightweight existence check that
/// replaced a full-row load on the paths that only ever null-checked the result.
///
/// <para>
/// GET /api/credit-lines?entityId= and the three notes endpoints each answered "does this entity
/// exist?" by calling <c>GetByIdAsync</c>, an unprojected <c>SingleOrDefaultAsync</c>. Its SELECT
/// names every column, so SQL Server followed the in-row LOB pointers and read all five
/// <c>nvarchar(max)</c> source snapshots off disk — the LUFE blob alone is ~26KB on a representative
/// PyME — to produce a boolean. <c>AnyAsync</c> emits an EXISTS that names no column at all.
/// </para>
///
/// <para>
/// These three tests pin the BEHAVIOUR, which is what a caller's 404 depends on: found, not found,
/// and — the one that would be a tenant leak rather than a bug — another tenant's entity reported as
/// absent. The SQL shape itself is not asserted here: <c>AnyAsync</c> is a terminal operator with no
/// <c>IQueryable</c> left to hand to <c>ToQueryString()</c>, unlike the projection seams in
/// <see cref="EntityRepositoryDetailTests"/>, and a seam rendering SQL the production call never
/// emits would assert nothing worth knowing. The regression guard for "the blob load is gone" lives
/// at the controller instead — see <c>CreditLinesControllerStructureTests</c> and
/// <c>EntitiesControllerNotesTests</c>, which count the calls.
/// </para>
/// </summary>
public sealed class EntityRepositoryExistsTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"entity-exists-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static EntityRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<EntityRepository>.Instance);

    /// <summary>
    /// All five snapshots populated, mirroring <see cref="EntityRepositoryDetailTests"/>: the row
    /// under test is the expensive kind, which is the whole reason this method exists.
    /// </summary>
    private static Entity Ent(Guid entityId, string tenantId = Tenant) => new()
    {
        EntityId       = entityId,
        TenantId       = tenantId,
        CUIT           = "30-71234567-1",
        BusinessName   = "Metalúrgica del Sur S.A.",
        CreatedAt      = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
        FiscalDataJson = """{"a":1}""",
        LufeDataJson   = """{"b":2}""",
        BcraDataJson   = """{"c":3}""",
        CasfogDataJson = """{"d":4}""",
        NosisDataJson  = """{"e":5}"""
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params Entity[] rows)
    {
        ctx.Entities.AddRange(rows);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    /// <summary>An entity of this tenant is found — the caller proceeds past its 404 guard.</summary>
    [Fact]
    public async Task ExistsById_SeededEntity_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(id));

        Assert.True(await Repo(ctx).ExistsByIdAsync(id, Tenant, CancellationToken.None));
    }

    /// <summary>An id nobody owns is false, which the controller turns into its 404.</summary>
    [Fact]
    public async Task ExistsById_UnknownId_ReturnsFalse()
    {
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(Guid.NewGuid()));

        Assert.False(await Repo(ctx).ExistsByIdAsync(Guid.NewGuid(), Tenant, CancellationToken.None));
    }

    /// <summary>
    /// The load-bearing half of the swap. <c>GetByIdAsync</c> filters on
    /// <c>(EntityId, TenantId)</c>, and this must filter on exactly the same pair: an existence
    /// check that dropped the tenant predicate would answer "yes" for another tenant's entity by
    /// exact id, turning a 404 into a confirmation that the id exists somewhere in the system.
    /// </summary>
    [Fact]
    public async Task ExistsById_EntityInAnotherTenant_ReturnsFalse()
    {
        var id = Guid.NewGuid();
        using var ctx = NewInMemoryContext();
        await SeedAsync(ctx, Ent(id, Other));

        Assert.False(await Repo(ctx).ExistsByIdAsync(id, Tenant, CancellationToken.None));
    }
}
