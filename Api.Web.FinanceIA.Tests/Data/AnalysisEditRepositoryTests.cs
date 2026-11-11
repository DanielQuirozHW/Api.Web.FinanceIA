// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Reflection;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// [AnalysisEdits] is APPEND-ONLY: the repository can insert and read, nothing else — the interface is
/// held to that by reflection, so an Update or Delete cannot be added without breaking this test.
/// </summary>
public sealed class AnalysisEditRepositoryTests
{
    private const string Tenant = "tenant-edits";

    private readonly string _databaseName = $"edits-{Guid.NewGuid()}";

    [Fact]
    public void The_interface_has_an_insert_and_a_read_and_no_update_or_delete()
    {
        var methods = typeof(IAnalysisEditRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AddRangeAsync", "GetByAnalysisAsync"], methods);
        Assert.DoesNotContain(methods, m =>
            m.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Upsert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_implementation_exposes_nothing_beyond_the_interface()
    {
        var extra = typeof(AnalysisEditRepository).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Except(typeof(IAnalysisEditRepository).GetMethods().Select(m => m.Name))
            .ToList();

        Assert.Empty(extra);
    }

    [Fact]
    public async Task The_read_is_tenant_scoped_analysis_scoped_and_newest_first()
    {
        var analysisId = Guid.NewGuid();
        var otherId    = Guid.NewGuid();
        var t0         = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

        await using (var db = NewContext())
        {
            await new AnalysisEditRepository(db).AddRangeAsync(
            [
                Edit(analysisId, Tenant, "equity", t0),
                Edit(analysisId, Tenant, "current_assets", t0.AddMinutes(5)),
                Edit(analysisId, "tenant-other", "assets", t0.AddMinutes(9)),
                Edit(otherId, Tenant, "liabilities", t0.AddMinutes(7)),
            ], CancellationToken.None);
        }

        await using var read = NewContext();
        var items = await new AnalysisEditRepository(read).GetByAnalysisAsync(analysisId, Tenant, CancellationToken.None);

        Assert.Equal(["current_assets", "equity"], items.Select(i => i.FieldPath));
        Assert.Empty(await new AnalysisEditRepository(read).GetByAnalysisAsync(analysisId, "tenant-none", CancellationToken.None));
    }

    [Fact]
    public async Task An_insert_leaves_nothing_tracked_and_an_empty_list_is_a_no_op()
    {
        await using var db = NewContext();
        var repository = new AnalysisEditRepository(db);

        await repository.AddRangeAsync([], CancellationToken.None);
        await repository.AddRangeAsync([Edit(Guid.NewGuid(), Tenant, "equity", DateTime.UtcNow)], CancellationToken.None);

        Assert.Empty(db.ChangeTracker.Entries<AnalysisEdit>());
        Assert.Equal(1, await db.AnalysisEdits.CountAsync());
    }

    [Fact]
    public async Task DI_builds_the_rescore_repository_with_the_edit_repository_on_the_same_scoped_context()
    {
        // The registrations of Program.cs: two public constructors, and DI must pick the one that takes
        // the edit repository — on the SAME scoped context, so its insert joins the write transaction.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FinanciaDbContext>(o => o.UseInMemoryDatabase(_databaseName));
        services.AddScoped<IAnalysisEditRepository, AnalysisEditRepository>();
        services.AddScoped<IAnalysisRescoreRepository, AnalysisRescoreRepository>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope    = provider.CreateAsyncScope();

        var rescore = Assert.IsType<AnalysisRescoreRepository>(scope.ServiceProvider.GetRequiredService<IAnalysisRescoreRepository>());
        var edits   = scope.ServiceProvider.GetRequiredService<IAnalysisEditRepository>();
        var field   = typeof(AnalysisRescoreRepository).GetField("_editRepository", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.Same(edits, field.GetValue(rescore));
    }

    private FinanciaDbContext NewContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private static AnalysisEdit Edit(Guid analysisId, string tenantId, string path, DateTime at) => new()
    {
        AnalysisId = analysisId,
        TenantId   = tenantId,
        EditedBy   = Guid.NewGuid(),
        FieldPath  = path,
        OldValue   = "1",
        NewValue   = "2",
        EditReason = "Motivo de prueba suficientemente largo.",
        EditedAt   = at
    };
}
