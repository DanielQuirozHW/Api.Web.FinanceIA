// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-08

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query-level tests for <see cref="CreditLineRepository"/>, run against the EF Core in-memory
/// provider (a database provider, not a mocking library) so the actual status WHERE clauses and
/// ordering are exercised — behaviour a hand-rolled stub cannot verify because a stub simply
/// returns whatever it is handed.
///
/// The headline regression these lock down: a Suspended cupo must NOT disappear from the entity
/// read (it did while the query filtered Status == "Active"), because its Reactivate action lives
/// on that card. And the reactivation guard (GetActiveByEntityCurrencyAsync) must stay Active-only.
/// </summary>
public sealed class CreditLineRepositoryTests
{
    private const string Tenant = "tenant-1";

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"creditline-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static CreditLine Cupo(
        Guid entityId, string status, string currency,
        string tenantId = Tenant, Guid? originAnalysisId = null) => new()
    {
        CreditLineId     = Guid.NewGuid(),
        TenantId         = tenantId,
        EntityId         = entityId,
        Amount           = 100m,
        Currency         = currency,
        Status           = status,
        CreatedByUserId  = Guid.NewGuid(),
        CreatedAt        = DateTime.UtcNow,
        OriginAnalysisId = originAnalysisId,
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, params CreditLine[] lines)
    {
        ctx.CreditLines.AddRange(lines);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A cupo with an explicit <paramref name="createdAt"/> — the ordering key that decides which
    /// Active cupo is the MAIN one. The <see cref="Cupo"/> overload stamps DateTime.UtcNow, which
    /// cannot express "older than".
    /// </summary>
    private static CreditLine CupoAt(
        Guid entityId, string status, string currency, decimal amount, DateTime createdAt,
        string tenantId = Tenant)
    {
        var line = Cupo(entityId, status, currency, tenantId);
        line.Amount    = amount;
        line.CreatedAt = createdAt;
        return line;
    }

    private static CreditLineOperation Operacion(
        Guid creditLineId, decimal amount, string status, string tenantId = Tenant) => new()
    {
        OperationId     = Guid.NewGuid(),
        TenantId        = tenantId,
        CreditLineId    = creditLineId,
        AnalysisId      = Guid.NewGuid(),
        Type            = 0,
        Amount          = amount,
        Currency        = "ARS",
        Status          = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt       = DateTime.UtcNow,
    };

    private static async Task SeedOperationsAsync(
        FinanciaDbContext ctx, params CreditLineOperation[] operations)
    {
        ctx.CreditLineOperations.AddRange(operations);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    private static CreditLineRepository Repo(FinanciaDbContext ctx)
        => new(ctx, NullLogger<CreditLineRepository>.Instance);

    // ── GetNonTerminalByEntityAsync ──────────────────────────────────────────────

    /// <summary>
    /// THE REGRESSION: an entity whose ONLY cupo is Suspended must still see it on the read that
    /// feeds the credit-line card — otherwise the cupo (and its Reactivate button) silently vanish
    /// and the cupo becomes unreachable. This is exactly what the old Status == "Active" filter did.
    /// </summary>
    [Fact]
    public async Task GetNonTerminalByEntity_WhenOnlyCupoIsSuspended_ReturnsIt()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Suspended", "ARS"));

        var rows = await Repo(ctx).GetNonTerminalByEntityAsync(entityId, Tenant, CancellationToken.None);

        var only = Assert.Single(rows);
        Assert.Equal("Suspended", only.Status);
    }

    /// <summary>Active and Suspended are returned together, Active first (live money on top).</summary>
    [Fact]
    public async Task GetNonTerminalByEntity_WithActiveAndSuspended_ReturnsBothActiveFirst()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        // Seed Suspended first so it is ORDER — not insertion — that puts Active ahead.
        await SeedAsync(ctx,
            Cupo(entityId, "Suspended", "USD"),
            Cupo(entityId, "Active", "ARS"));

        var rows = await Repo(ctx).GetNonTerminalByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Equal(new[] { "Active", "Suspended" }, rows.Select(r => r.Status).ToArray());
    }

    /// <summary>Terminal 'Superseded' cupos stay out of the live card.</summary>
    [Fact]
    public async Task GetNonTerminalByEntity_ExcludesSuperseded()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Cupo(entityId, "Active", "ARS"),
            Cupo(entityId, "Superseded", "USD"));

        var rows = await Repo(ctx).GetNonTerminalByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Equal(new[] { "Active" }, rows.Select(r => r.Status).ToArray());
    }

    /// <summary>
    /// Tenant isolation: a cupo with the SAME entityId but belonging to another tenant must NOT be
    /// returned. A leaking WHERE here is the worst class of bug in a multi-tenant credit product —
    /// and one a stub could never catch, which is why this runs against a real query.
    /// </summary>
    [Fact]
    public async Task GetNonTerminalByEntity_WhenCupoBelongsToAnotherTenant_NotReturned()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Active", "ARS", tenantId: "other-tenant"));

        var rows = await Repo(ctx).GetNonTerminalByEntityAsync(entityId, Tenant, CancellationToken.None);

        Assert.Empty(rows);
    }

    // ── GetEntityIdsWithNonTerminalLineAsync (PyMEs-list marker) ──────────────────

    /// <summary>The PyMEs-list marker must count an entity whose ONLY cupo is Suspended.</summary>
    [Fact]
    public async Task GetEntityIdsWithNonTerminalLine_WhenOnlyCupoIsSuspended_IncludesEntity()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Suspended", "ARS"));

        var ids = await Repo(ctx)
            .GetEntityIdsWithNonTerminalLineAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.Contains(entityId, ids);
    }

    /// <summary>An entity whose only cupo is terminal ('Superseded') does NOT count as having a line.</summary>
    [Fact]
    public async Task GetEntityIdsWithNonTerminalLine_WhenOnlyCupoIsSuperseded_ExcludesEntity()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Superseded", "ARS"));

        var ids = await Repo(ctx)
            .GetEntityIdsWithNonTerminalLineAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.DoesNotContain(entityId, ids);
    }

    /// <summary>
    /// Tenant isolation: an entity whose ONLY cupo belongs to another tenant must NOT be marked as
    /// having a line. Guards the tenant predicate on the set-based marker query — a stub cannot.
    /// </summary>
    [Fact]
    public async Task GetEntityIdsWithNonTerminalLine_WhenCupoBelongsToAnotherTenant_ExcludesEntity()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Active", "ARS", tenantId: "other-tenant"));

        var ids = await Repo(ctx)
            .GetEntityIdsWithNonTerminalLineAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.DoesNotContain(entityId, ids);
    }

    // ── GetActiveByEntityCurrencyAsync (guard — must NOT drift) ───────────────────

    /// <summary>
    /// The reactivation guard depends on this staying Active-ONLY: a Suspended row must NOT be
    /// returned, or reactivation would fail to see the slot as free. Locks the semantics so a future
    /// refactor cannot quietly widen it to non-terminal like the entity read.
    /// </summary>
    [Fact]
    public async Task GetActiveByEntityCurrency_WhenRowIsSuspended_ReturnsNull()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Suspended", "ARS"));

        var found = await Repo(ctx)
            .GetActiveByEntityCurrencyAsync(entityId, "ARS", Tenant, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetActiveByEntityCurrency_WhenRowIsActive_ReturnsIt()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Active", "ARS"));

        var found = await Repo(ctx)
            .GetActiveByEntityCurrencyAsync(entityId, "ARS", Tenant, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Active", found!.Status);
    }

    // ── GetByOriginAnalysisAsync (provenance — ALL statuses) ──────────────────────

    /// <summary>
    /// Provenance includes terminal cupos: a 'Superseded' cupo originated by an analysis must still
    /// appear (deliberately unlike the non-terminal-only, id-only GetIdsByOriginAnalysisAsync). Cupos
    /// originated by a different analysis are not included.
    /// </summary>
    [Fact]
    public async Task GetByOriginAnalysis_ReturnsAllStatusesIncludingSuperseded()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Cupo(entityId, "Active", "ARS", originAnalysisId: analysisId),
            Cupo(entityId, "Superseded", "USD", originAnalysisId: analysisId),
            Cupo(entityId, "Active", "EUR", originAnalysisId: Guid.NewGuid())); // different origin

        var rows = await Repo(ctx).GetByOriginAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Status == "Superseded");
        Assert.All(rows, r => Assert.Equal(analysisId, r.OriginAnalysisId));
    }

    // ── GetActiveIdsByOriginAnalysisAsync (reject guard — ACTIVE only) ────────────

    /// <summary>
    /// The reject guard's set is ACTIVE-only: a 'Suspended' cupo (non-terminal but dormant) and a
    /// 'Superseded' cupo (terminal) originated by the same analysis are both excluded, as is an Active
    /// cupo from a different analysis. Narrower than the non-terminal-only GetIdsByOriginAnalysisAsync.
    /// </summary>
    [Fact]
    public async Task GetActiveIdsByOriginAnalysis_ReturnsOnlyActive_ExcludesSuspendedAndSuperseded()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var activeCupo = Cupo(entityId, "Active", "ARS", originAnalysisId: analysisId);
        using var ctx = NewContext();
        await SeedAsync(ctx,
            activeCupo,
            Cupo(entityId, "Suspended", "USD", originAnalysisId: analysisId),
            Cupo(entityId, "Superseded", "EUR", originAnalysisId: analysisId),
            Cupo(entityId, "Active", "BRL", originAnalysisId: Guid.NewGuid())); // different origin

        var ids = await Repo(ctx).GetActiveIdsByOriginAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.Equal(new[] { activeCupo.CreditLineId }, ids);
    }

    /// <summary>
    /// The regression that matters: all dependents Suspended → the ACTIVE set is empty (so cupos no
    /// longer block rejection), yet provenance still resolves them via GetByOriginAnalysisAsync with
    /// OriginAnalysisId intact — rejecting must never touch the cupo.
    /// </summary>
    [Fact]
    public async Task GetActiveIdsByOriginAnalysis_AllSuspended_ReturnsEmpty_WhileProvenanceStillResolves()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Cupo(entityId, "Suspended", "ARS", originAnalysisId: analysisId),
            Cupo(entityId, "Suspended", "USD", originAnalysisId: analysisId));

        var repo = Repo(ctx);
        var activeIds  = await repo.GetActiveIdsByOriginAnalysisAsync(analysisId, Tenant, CancellationToken.None);
        var provenance = await repo.GetByOriginAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.Empty(activeIds);
        Assert.Equal(2, provenance.Count);
        Assert.All(provenance, r => Assert.Equal(analysisId, r.OriginAnalysisId));
    }

    /// <summary>
    /// Tenant isolation: an Active cupo with the same origin analysis but a different tenant is not
    /// returned.
    /// </summary>
    [Fact]
    public async Task GetActiveIdsByOriginAnalysis_IsTenantScoped()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            Cupo(entityId, "Active", "ARS", tenantId: "other-tenant", originAnalysisId: analysisId));

        var ids = await Repo(ctx).GetActiveIdsByOriginAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.Empty(ids);
    }

    // ── GetMainActiveByEntityIdsAsync (PyMEs-list main cupo) ─────────────────────

    /// <summary>
    /// THE HEADLINE RULE: the main cupo is the ACTIVE one created MOST RECENTLY, whatever its
    /// currency — never the largest amount, because ARS and USD figures are not comparable. Here
    /// the newer cupo is the SMALLER one, in a different currency, so an implementation that
    /// silently ranked by Amount would pick the other row and pass every other test in this file.
    /// The older cupo is seeded LAST so it is ordering, not insertion, that decides.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_TwoActiveCuposInDifferentCurrencies_PicksMostRecentlyCreated()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            CupoAt(entityId, "Active", "USD", 5_000m,     new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            CupoAt(entityId, "Active", "ARS", 9_000_000m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        var main = Assert.Contains(entityId, map);
        Assert.Equal("USD",    main.Currency);
        Assert.Equal(5_000m,   main.Amount);
        Assert.Equal(0m,       main.Utilizado);   // no operations seeded
    }

    /// <summary>
    /// Utilizado sums the cupo's non-'Cancelada' operations, so disponible = Amount - Utilizado.
    /// 'Aprobada' RESERVES cupo from creation and 'Vigente' holds it; only 'Cancelada' frees saldo.
    /// This is the same predicate as CreditLinesController.OccupiesCupo — the two drifting apart
    /// would let the list advertise saldo the credit-line screen says is already committed.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_SumsNonCancelledOperationsOnly()
    {
        var entityId = Guid.NewGuid();
        var cupo     = CupoAt(entityId, "Active", "ARS", 1_000m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        using var ctx = NewContext();
        await SeedAsync(ctx, cupo);
        await SeedOperationsAsync(ctx,
            Operacion(cupo.CreditLineId, 100m, "Vigente"),
            Operacion(cupo.CreditLineId, 250m, "Aprobada"),    // reserves from creation
            Operacion(cupo.CreditLineId, 900m, "Cancelada"));  // frees saldo — must NOT count

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        var main = Assert.Contains(entityId, map);
        Assert.Equal(350m, main.Utilizado);
        Assert.Equal(650m, main.Amount - main.Utilizado);   // disponible, as the controller computes it
    }

    /// <summary>Operations under a DIFFERENT cupo must not be summed into this one's utilizado.</summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_IgnoresOperationsOfAnotherCupo()
    {
        var entityId = Guid.NewGuid();
        var mine     = CupoAt(entityId, "Active", "ARS", 1_000m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var other    = CupoAt(Guid.NewGuid(), "Active", "ARS", 1_000m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        using var ctx = NewContext();
        await SeedAsync(ctx, mine, other);
        await SeedOperationsAsync(ctx,
            Operacion(mine.CreditLineId,  100m, "Vigente"),
            Operacion(other.CreditLineId, 700m, "Vigente"));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.Equal(100m, Assert.Contains(entityId, map).Utilizado);
    }

    /// <summary>
    /// A 'Suspended' cupo is NOT the main cupo — this projection is Active-only, deliberately
    /// stricter than GetEntityIdsWithNonTerminalLineAsync, which counts the same row as a line.
    /// The entity is simply absent, which is how the list row gets all three fields null while
    /// still reporting hasCreditLine: true.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_WhenOnlyCupoIsSuspended_EntityIsAbsent()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Suspended", "ARS"));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.DoesNotContain(entityId, map);
    }

    /// <summary>Terminal 'Superseded' cupos are not the main cupo either.</summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_WhenOnlyCupoIsSuperseded_EntityIsAbsent()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Superseded", "ARS"));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.DoesNotContain(entityId, map);
    }

    /// <summary>
    /// A newer SUSPENDED cupo must not shadow the older ACTIVE one: the status filter is applied
    /// before the recency pick, not after. Otherwise suspending a cupo would blank the entity's
    /// figures on the list even though it still has live tope in another currency.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_NewerSuspendedCupo_DoesNotShadowOlderActiveOne()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx,
            CupoAt(entityId, "Suspended", "USD", 8_000m, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
            CupoAt(entityId, "Active",    "ARS", 3_000m, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        var main = Assert.Contains(entityId, map);
        Assert.Equal("ARS",  main.Currency);
        Assert.Equal(3_000m, main.Amount);
    }

    /// <summary>
    /// Tenant isolation on the cupo. A cupo with the SAME entityId under another tenant must never
    /// surface — the worst class of bug in a multi-tenant credit product, and one a stub cannot catch.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_WhenCupoBelongsToAnotherTenant_EntityIsAbsent()
    {
        var entityId = Guid.NewGuid();
        using var ctx = NewContext();
        await SeedAsync(ctx, Cupo(entityId, "Active", "ARS", tenantId: "other-tenant"));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.DoesNotContain(entityId, map);
    }

    /// <summary>
    /// Tenant isolation on the OPERATIONS subquery, which is a separate predicate from the one on
    /// the cupo. Another tenant's operation row must not consume this tenant's disponible.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_OperationOfAnotherTenant_DoesNotConsumeSaldo()
    {
        var entityId = Guid.NewGuid();
        var cupo     = CupoAt(entityId, "Active", "ARS", 1_000m, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        using var ctx = NewContext();
        await SeedAsync(ctx, cupo);
        await SeedOperationsAsync(ctx,
            Operacion(cupo.CreditLineId, 100m, "Vigente"),
            Operacion(cupo.CreditLineId, 400m, "Vigente", tenantId: "other-tenant"));

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(new[] { entityId }, Tenant, CancellationToken.None);

        Assert.Equal(100m, Assert.Contains(entityId, map).Utilizado);
    }

    /// <summary>
    /// The batch keys each entity to its OWN main cupo — one set-based call for the page, so a
    /// crossed key would put one PyME's tope on another's row.
    /// </summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_KeysEachEntityToItsOwnCupo()
    {
        var first  = Guid.NewGuid();
        var second = Guid.NewGuid();
        var absent = Guid.NewGuid();   // no cupo at all

        using var ctx = NewContext();
        await SeedAsync(ctx,
            CupoAt(first,  "Active", "ARS", 1_111m, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            CupoAt(second, "Active", "USD", 2_222m, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));

        var map = await Repo(ctx).GetMainActiveByEntityIdsAsync(
            new[] { first, second, absent }, Tenant, CancellationToken.None);

        Assert.Equal(2, map.Count);
        Assert.Equal(1_111m, Assert.Contains(first,  map).Amount);
        Assert.Equal(2_222m, Assert.Contains(second, map).Amount);
        Assert.DoesNotContain(absent, map);
    }

    /// <summary>An empty id batch short-circuits to an empty map without touching the database.</summary>
    [Fact]
    public async Task GetMainActiveByEntityIds_EmptyBatch_ReturnsEmptyMap()
    {
        using var ctx = NewContext();

        var map = await Repo(ctx)
            .GetMainActiveByEntityIdsAsync(Array.Empty<Guid>(), Tenant, CancellationToken.None);

        Assert.Empty(map);
    }

    // ── Emitted SQL (translation guard) ─────────────────────────────────────────

    /// <summary>
    /// A context on the SqlServer provider so the query can be compiled to real T-SQL. The server
    /// does not exist and is never contacted: ToQueryString() builds the command text and stops.
    /// The in-memory provider runs LINQ-to-objects, so it would happily execute a projection SQL
    /// Server cannot translate — only this proves the real one works.
    /// </summary>
    private static FinanciaDbContext NewSqlServerContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options;
        return new FinanciaDbContext(options);
    }

    /// <summary>
    /// THE PERFORMANCE CLAIM, locked. The main-cupo projection must be ONE statement in which the
    /// utilizado sum is a correlated subquery evaluated by SQL Server — not a second round-trip, and
    /// not client-side evaluation over materialised operation rows. This list runs over the whole
    /// tenant portfolio, so a projection that pulled CreditLineOperations back per cupo would be the
    /// N+1 this feature exists to remove. ToQueryString() also throws outright if EF cannot
    /// translate the projection, which is the other half of what this guards.
    /// </summary>
    [Fact]
    public void MainActiveByEntityIdsQuery_SumsOperationsInSqlAsASingleStatement()
    {
        using var ctx = NewSqlServerContext();

        var sql = Repo(ctx)
            .MainActiveByEntityIdsQuery(new[] { Guid.NewGuid(), Guid.NewGuid() }, Tenant)
            .ToQueryString();

        // The sum is delegated to SQL Server, against the operations table.
        Assert.Contains("SUM(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CreditLineOperations", sql, StringComparison.Ordinal);

        // Both status predicates survive into SQL: Active cupos, non-Cancelada operations.
        Assert.Contains("Active", sql, StringComparison.Ordinal);
        Assert.Contains("Cancelada", sql, StringComparison.Ordinal);

        // The sum is a CORRELATED SUBQUERY IN THE SELECT LIST — it appears before the outer FROM,
        // and correlates on the outer cupo's id. That is what makes this one round-trip per page
        // instead of one per cupo, and it is the assertion that would fail if the projection were
        // ever split into a second query or evaluated client-side.
        var operationsAt = sql.IndexOf("[dbo].[CreditLineOperations]", StringComparison.Ordinal);
        var outerFromAt  = sql.IndexOf("FROM [dbo].[CreditLines] AS", StringComparison.Ordinal);
        Assert.True(operationsAt >= 0 && outerFromAt > operationsAt,
            $"Expected the operations SUM to be a subquery in the SELECT list.\nSQL:\n{sql}");
        Assert.Contains("[c0].[CreditLineId] = [c].[CreditLineId]", sql, StringComparison.Ordinal);

        // ONE statement over CreditLines: a split query would scan the table twice.
        Assert.Single(Regex.Matches(sql, @"FROM \[dbo\]\.\[CreditLines\] AS"));
    }
}
