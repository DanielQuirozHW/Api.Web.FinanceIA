// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// The calibration board's queries compiled to real T-SQL (the DashboardRepositorySqlShapeTests approach):
/// the in-memory suite runs every predicate as C# and cannot prove SQL Server translates them. These pin that
/// they translate, that every table carries the tenant predicate, and that the analysis read selects no
/// participant blob.
/// </summary>
public sealed class PersonCalibrationRepositorySqlShapeTests
{
    private const string Tenant = "tenant-1";

    /// <summary>A SqlServer-provider context; the server is never contacted — ToQueryString() stops at the text.</summary>
    private static FinanciaDbContext NewSqlServerContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options);

    [Fact]
    public void AnalysesQuery_WithATeam_FiltersByCurrentMembership_InOneTenantScopedStatement()
    {
        using var ctx = NewSqlServerContext();

        var sql = new PersonCalibrationRepository(ctx)
            .AnalysesQuery(Tenant, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, Guid.NewGuid())
            .ToQueryString();

        Assert.Contains("FROM [dbo].[Analyses]", sql);
        Assert.Contains("[a].[AnalysisKind] = N'PersonaHumana'", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        Assert.Contains("[dbo].[Users]", sql);
        Assert.Contains("[u].[TenantId] = @", sql);
        Assert.Contains("[u].[TeamId] = @", sql);
        Assert.DoesNotContain("AnalysisParticipants", sql);
        Assert.DoesNotContain("FiltersJson", sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnalysesQuery_ReadsTheResultId_FromATenantScopedSubquery_WithoutAnyResultBlob(bool withTeam)
    {
        using var ctx = NewSqlServerContext();

        var sql = new PersonCalibrationRepository(ctx)
            .AnalysesQuery(Tenant, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, withTeam ? Guid.NewGuid() : null)
            .ToQueryString();

        // A correlated TOP(1), not a join: an analysis with no result row still lists.
        Assert.Matches(@"SELECT TOP\(1\) \[\w+\]\.\[ResultId\]\s+FROM \[dbo\]\.\[AnalysisResults\]", sql);
        Assert.DoesNotContain("JOIN [dbo].[AnalysisResults]", sql);
        AssertEveryTableIsTenantScoped(sql);

        // The key only: none of the result's nvarchar(max) blobs leaves the database.
        foreach (var blob in new[]
                 {
                     "[BalanceJson]", "[RatiosJson]", "[ScoreBreakdownJson]", "[ExecutiveSummary]", "[BcraDataJson]",
                     "[MacroDataJson]", "[FiscalDataJson]", "[LufeDataJson]", "[CasfogDataJson]", "[NosisDataJson]"
                 })
        {
            Assert.DoesNotContain(blob, sql);
        }
    }

    [Fact]
    public void RescoresQuery_SelectsTheResultId()
    {
        using var ctx = NewSqlServerContext();

        var sql = new PersonCalibrationRepository(ctx)
            .RescoresQuery(Tenant, [Guid.NewGuid()])
            .ToQueryString();

        Assert.Contains("[a].[ResultId]", sql);
        AssertEveryTableIsTenantScoped(sql);
    }

    [Fact]
    public void ParticipantsQuery_IsTenantScoped_AndLeavesScoreJsonOut()
    {
        using var ctx = NewSqlServerContext();

        var sql = new PersonCalibrationRepository(ctx)
            .ParticipantsQuery(Tenant, [Guid.NewGuid(), Guid.NewGuid()])
            .ToQueryString();

        Assert.Contains("FROM [dbo].[AnalysisParticipants]", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        Assert.Contains("[FiltersJson]", sql);
        Assert.DoesNotContain("[ScoreJson]", sql);
    }

    [Fact]
    public void RescoresQuery_ReadsTheSuggestionsWithJsonValue_WithoutSelectingTheBlob()
    {
        using var ctx = NewSqlServerContext();

        var sql = new PersonCalibrationRepository(ctx)
            .RescoresQuery(Tenant, [Guid.NewGuid()])
            .ToQueryString();

        Assert.Contains("JSON_VALUE([a].[PreviousValuesJson], N'$.suggestions.previous')", sql);
        Assert.Contains("JSON_VALUE([a].[PreviousValuesJson], N'$.suggestions.new')", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        Assert.DoesNotContain("[a].[PreviousValuesJson]" + Environment.NewLine, sql);
        Assert.DoesNotContain("[a].[PreviousValuesJson], [", sql);
    }

    /// <summary>Every <c>FROM/JOIN [dbo].[T] AS [alias]</c> carries <c>[alias].[TenantId] = @</c> (as in DashboardRepositorySqlShapeTests).</summary>
    private static void AssertEveryTableIsTenantScoped(string sql)
    {
        var aliases = System.Text.RegularExpressions.Regex
            .Matches(sql, @"(?:FROM|JOIN) \[dbo\]\.\[(?<table>\w+)\] AS \[(?<alias>\w+)\]")
            .Select(m => (Table: m.Groups["table"].Value, Alias: m.Groups["alias"].Value))
            .ToList();

        Assert.NotEmpty(aliases);
        foreach (var (table, alias) in aliases)
        {
            Assert.True(
                sql.Contains($"[{alias}].[TenantId] = @", StringComparison.Ordinal),
                $"[{table}] AS [{alias}] has no TenantId predicate:\n{sql}");
        }
    }
}
