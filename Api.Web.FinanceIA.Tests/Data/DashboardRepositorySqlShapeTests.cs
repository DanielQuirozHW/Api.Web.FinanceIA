// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// The dashboard queries compiled to real T-SQL. The in-memory suite proves WHICH rows come back; it
/// runs every predicate as C#, so it cannot prove SQL Server can translate them at all. This does —
/// a construct the provider cannot translate throws here, not in production — and pins the two
/// properties the dashboard promises: every table carries the tenant predicate, and no nvarchar(max)
/// blob is selected.
/// </summary>
public sealed class DashboardRepositorySqlShapeTests
{
    private const string Tenant = "tenant-1";

    /// <summary>
    /// A context on the SqlServer provider so the query can be compiled to real T-SQL. The server
    /// does not exist and is never contacted: ToQueryString() builds the command text and stops.
    /// </summary>
    private static FinanciaDbContext NewSqlServerContext()
        => new(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=localhost;Database=none;Trusted_Connection=False;")
            .Options);

    [Fact]
    public void MyPendingQuery_TranslatesTheRiskOrder_AndScopesBothTables()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).MyPendingQuery(Tenant, Guid.NewGuid()).Take(20).ToQueryString();

        Assert.Contains("INNER JOIN (", sql);
        Assert.Contains("FROM [dbo].[AnalysisResults]", sql);
        Assert.Contains("WHEN [a1].[AiSuggestedLevel] = N'Critical' THEN 0", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        Assert.Contains("[a0].[TenantId] = @", sql);
        AssertNoBlob(sql);
    }

    /// <summary>
    /// The LUFE "no data" flag is decided in SQL from the prefixes LufeCallOutcome owns, so the
    /// ErrorMessage text itself never reaches the client; the read stays one tenant-scoped statement.
    /// </summary>
    [Fact]
    public void ApiCallOutcomesQuery_FlagsTheLufeNoDataMarkersInSql_WithoutSelectingTheText()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx)
            .ApiCallOutcomesQuery(Tenant, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).ToQueryString();

        Assert.Contains("LIKE N'Empty (HTTP %'", sql);
        Assert.Contains("LIKE N'No legajo (HTTP %'", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        Assert.DoesNotContain("[ErrorMessage]", OutermostSelectList(sql));
    }

    [Fact]
    public void ReturnedQuery_Translates_AndScopesBothTables()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).ReturnedQuery(Tenant, Guid.NewGuid()).Take(20).ToQueryString();

        Assert.Contains("[a].[ReviewedBy] IS NOT NULL", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        Assert.Contains("[a0].[TenantId] = @", sql);
        AssertNoBlob(sql);
        // The notes are fetched by a second, page-sized read — never on the list projection.
        Assert.DoesNotContain("[ReviewNotes]", sql);
    }

    [Fact]
    public void BalancePresenceQuery_TestsTheBlobInTheDatabase_WithoutSelectingIt()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).BalancePresenceQuery(Tenant, [Guid.NewGuid()]).ToQueryString();

        Assert.Contains("[BalanceJson]", sql);
        Assert.Contains("[a].[TenantId] = @", sql);
        // Only the id leaves the database.
        Assert.StartsWith("SELECT [a].[ResultId]", sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Rows_ForEveryFilterShape_Translates()
    {
        using var ctx = NewSqlServerContext();
        var repository = new DashboardRepository(ctx);
        var userId     = Guid.NewGuid();
        var ids        = new List<Guid> { Guid.NewGuid() };
        var since      = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

        var shapes = new[]
        {
            repository.Rows(Tenant, a => a.Status == AnalysisStatuses.PreApproved && a.PreApprovedBy == userId).OrderBy(x => x.PreApprovedAt),
            repository.Rows(Tenant, a => a.Status == AnalysisStatuses.PreApproved).OrderBy(x => x.PreApprovedAt),
            repository.Rows(Tenant, a => a.RequestedBy == userId).OrderByDescending(x => x.CreatedAt),
            repository.Rows(Tenant, a => a.ReviewedBy == userId && a.ReviewedAt >= since).OrderBy(x => x.ResultId),
            repository.Rows(Tenant, a => ids.Contains(a.AnalysisId)).OrderBy(x => x.ResultId),
        };

        foreach (var shape in shapes)
        {
            var sql = shape.ToQueryString();
            Assert.Contains("[a].[TenantId] = @", sql);
            Assert.Contains("[a0].[TenantId] = @", sql);
            AssertNoBlob(sql);
        }
    }

    // ── portfolio / oversight / operations ─────────────────────────────────────

    /// <summary>Every new query shape, compiled to T-SQL by the real provider.</summary>
    public static TheoryData<string> NewShapes => new()
    {
        nameof(DashboardRepository.LineTotalsQuery),
        nameof(DashboardRepository.OccupiedByCurrencyQuery),
        nameof(DashboardRepository.LinesByRiskQuery),
        nameof(DashboardRepository.OccupiedByRiskQuery),
        nameof(DashboardRepository.TopExposuresQuery),
        nameof(DashboardRepository.HighUtilizationCountsQuery),
        nameof(DashboardRepository.HighUtilizationQuery),
        nameof(DashboardRepository.LinesBySectorQuery),
        nameof(DashboardRepository.OccupiedBySectorQuery),
        nameof(DashboardRepository.MaturityGroupsQuery),
        nameof(DashboardRepository.UpcomingMaturitiesQuery),
        nameof(DashboardRepository.BcraComparisonsQuery),
        nameof(DashboardRepository.BcraCurrentQuery),
        nameof(DashboardRepository.LatestApprovedAnalysesQuery),
        nameof(DashboardRepository.OccupyingOperationsOfActiveLinesQuery),
        nameof(DashboardRepository.TeamQueueQuery),
        nameof(DashboardRepository.TeamQueueByRequesterQuery),
        nameof(DashboardRepository.AgainstSuggestionCandidatesQuery),
        nameof(DashboardRepository.RecentDecisionsQuery),
        nameof(DashboardRepository.RecentRescoresQuery),
        nameof(DashboardRepository.CreatedByStatusQuery),
        nameof(DashboardRepository.DecisionTimesQuery),
        nameof(DashboardRepository.ApiCallOutcomesQuery),
        nameof(DashboardRepository.ApiCallDurationsQuery),
        nameof(DashboardRepository.FreshnessQuery),
    };

    [Theory]
    [MemberData(nameof(NewShapes))]
    public void NewShape_Translates_AndScopesEveryTableByTenant(string shape)
    {
        using var ctx = NewSqlServerContext();

        var sql = ShapeSql(new DashboardRepository(ctx), shape);

        AssertEveryTableIsTenantScoped(sql);
    }

    [Theory]
    [MemberData(nameof(NewShapes))]
    public void NewShape_SelectsNoBlob_BcraOnlyThroughJsonValue(string shape)
    {
        using var ctx = NewSqlServerContext();

        var sql = ShapeSql(new DashboardRepository(ctx), shape);

        // Derived tables may NAME a blob column for the outer JSON_VALUE to read; what reaches the client
        // is the outermost SELECT list, and in it a blob may only appear as JSON_VALUE's argument — BcraDataJson
        // and, for the persona suggestions of the recent re-scores, PreviousValuesJson.
        var selectList = OutermostSelectList(sql);
        foreach (var blob in new[]
                 {
                     "[BalanceJson]", "[RatiosJson]", "[ScoreBreakdownJson]", "[ExecutiveSummary]", "[FiscalDataJson]",
                     "[LufeDataJson]", "[CasfogDataJson]", "[NosisDataJson]", "[ProposedOperationsJson]",
                     "[ConfirmedOperationsJson]", "[PreApprovalChecksJson]", "[ConfirmationChecksJson]", "[ReviewNotes]",
                     "[SourcesJson]", "[ErrorMessage]", "[RequestBodyHash]", "[ResponseBodyHash]",
                     "[FiltersJson]"
                 })
        {
            Assert.DoesNotContain(blob, selectList);
        }

        foreach (var readThroughJsonValue in new[] { "BcraDataJson", "PreviousValuesJson" })
        {
            var named = System.Text.RegularExpressions.Regex.Matches(selectList, $@"\[\w+\]\.\[{readThroughJsonValue}\]").Count;
            var viaJsonValue = System.Text.RegularExpressions.Regex.Matches(selectList, $@"JSON_VALUE\(\[\w+\]\.\[{readThroughJsonValue}\]").Count;
            Assert.Equal(named, viaJsonValue);
        }
    }

    [Fact]
    public void BcraComparisonsQuery_ReadsBothSnapshotsWithJsonValue_OnTheDetailsPaths()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).BcraComparisonsQuery(Tenant).ToQueryString();

        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(sql, @"JSON_VALUE\(").Count);
        Assert.Contains("N'$.deuda.estado'", sql);
        Assert.Contains("N'$.deuda.situacionMaxima'", sql);
        Assert.Contains("FROM [dbo].[Entities]", sql);
        Assert.Contains("FROM [dbo].[AnalysisResults]", sql);
    }

    [Fact]
    public void BcraCurrentQuery_ReadsTheCurrentSnapshotWithJsonValue_LeftJoined_WithUtilizadoUnderTheCupoRule()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).BcraCurrentQuery(Tenant).ToQueryString();

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, @"JSON_VALUE\(").Count);
        Assert.Contains("N'$.deuda.estado'", sql);
        Assert.Contains("N'$.deuda.situacionMaxima'", sql);
        Assert.Contains("[BcraDataUpdatedAt]", sql);
        Assert.Contains("LEFT JOIN", sql);
        Assert.Contains("FROM [dbo].[CreditLineOperations]", sql);
        Assert.Contains("[Status] <> N'Cancelada'", sql);
        Assert.DoesNotContain("[dbo].[AnalysisResults]", sql);
    }

    [Fact]
    public void OccupyingOperationsOfActiveLinesQuery_FiltersTheCupoRuleInSql_AndLeavesEffectiveStatusToCSharp()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).OccupyingOperationsOfActiveLinesQuery(Tenant).ToQueryString();

        Assert.Contains("[Status] <> N'Cancelada'", sql);
        Assert.Contains("N'Active'", sql);
        // Vencida is decided in C#, on the UTC date: no date comparison here.
        Assert.DoesNotContain("[ValidUntil] <", sql);
        Assert.DoesNotContain("N'Vigente'", sql);
    }

    /// <summary>
    /// The annualReview and PYME_REVIEW_DUE read the SAME statement: the dashboard cannot select other
    /// PyMEs or other dates than the alert does.
    /// </summary>
    [Fact]
    public void LatestApprovedAnalysesQuery_IsTheStatementTheCupoAlertsRead()
    {
        using var ctx = NewSqlServerContext();

        var dashboard = new DashboardRepository(ctx).LatestApprovedAnalysesQuery(Tenant).ToQueryString();
        var alerts    = new CreditLineRepository(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<CreditLineRepository>.Instance)
            .LatestApprovedAnalysesQuery(Tenant)
            .ToQueryString();

        Assert.Equal(alerts, dashboard);
    }

    [Theory]
    [InlineData(nameof(DashboardRepository.TopExposuresQuery))]
    [InlineData(nameof(DashboardRepository.HighUtilizationQuery))]
    [InlineData(nameof(DashboardRepository.UpcomingMaturitiesQuery))]
    [InlineData(nameof(DashboardRepository.TeamQueueQuery))]
    [InlineData(nameof(DashboardRepository.RecentDecisionsQuery))]
    [InlineData(nameof(DashboardRepository.RecentRescoresQuery))]
    public void CappedShapes_ApplyTheCapInSql(string shape)
    {
        using var ctx = NewSqlServerContext();

        Assert.Contains("SELECT TOP(", ShapeSql(new DashboardRepository(ctx), shape));
    }

    [Fact]
    public void RecentRescoresQuery_LeftJoinsAnalyses_ScopedOnBothSides_WithoutTheRescoreBlobs()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).RecentRescoresQuery(Tenant).Take(10).ToQueryString();

        Assert.Contains("FROM [dbo].[AnalysisRescores]", sql);
        Assert.Contains("LEFT JOIN", sql);
        Assert.Contains("[dbo].[Analyses]", sql);
        AssertEveryTableIsTenantScoped(sql);
        // Neither nvarchar(max) column leaves the database: SourcesJson is never named, and PreviousValuesJson is
        // named only as the argument of the two JSON_VALUEs that read the persona suggestions out of it.
        Assert.DoesNotContain("[SourcesJson]", sql);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, @"\[PreviousValuesJson\]").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, @"JSON_VALUE\(\[\w+\]\.\[PreviousValuesJson\]").Count);
        Assert.Contains("N'$.suggestions.previous'", sql);
        Assert.Contains("N'$.suggestions.new'", sql);
    }

    [Fact]
    public void LineTotalsQuery_CountsDistinctEntitiesInSql()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).LineTotalsQuery(Tenant).ToQueryString();

        Assert.Contains("COUNT(DISTINCT", sql);
        Assert.Contains("GROUP BY [c].[Currency], [c].[Status]", sql);
    }

    [Fact]
    public void TopExposuresQuery_SumsUtilizadoAsACorrelatedSubquery_UnderTheCupoRule()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).TopExposuresQuery(Tenant, "ARS").Take(10).ToQueryString();

        Assert.Contains("FROM [dbo].[CreditLineOperations]", sql);
        Assert.Contains("[Status] <> N'Cancelada'", sql);
        Assert.Contains("ORDER BY COALESCE((", sql);
    }

    [Fact]
    public void HighUtilizationQueries_CompareTheExactAmounts_UnderTheCupoRule_WithTheRatioGuarded()
    {
        using var ctx = NewSqlServerContext();
        var repository = new DashboardRepository(ctx);

        var page   = repository.HighUtilizationQuery(Tenant, "ARS", 90m).Take(10).ToQueryString();
        var counts = repository.HighUtilizationCountsQuery(Tenant, 90m).ToQueryString();

        foreach (var sql in new[] { page, counts })
        {
            Assert.Contains("FROM [dbo].[CreditLineOperations]", sql);
            Assert.Contains("[Status] <> N'Cancelada'", sql);
            Assert.Contains("[c].[Amount] > 0.0", sql);
            Assert.Contains("* 100.0 >= [c].[Amount] * @", sql);
        }

        // The ratio is only ever computed under the Amount > 0 guard — SQL Server may evaluate ORDER BY before WHERE.
        Assert.Contains("ORDER BY CASE", page);
        Assert.Contains("WHEN [c].[Amount] > 0.0 THEN", page);
        Assert.Contains("END DESC, [c].[Amount] DESC, [c].[EntityId]", page);
        Assert.Contains("GROUP BY [c].[Currency]", counts);
    }

    [Fact]
    public void MaturityGroupsQuery_GroupsByTheValidUntilDate()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx).MaturityGroupsQuery(Tenant, new DateTime(2026, 11, 1)).ToQueryString();

        Assert.Contains("CONVERT(date, [c].[ValidUntil])", sql);
        Assert.Contains("[c].[Status] = N'Vigente'", sql);
    }

    /// <summary>The outermost SELECT list: from the first SELECT to its matching top-level FROM.</summary>
    private static string OutermostSelectList(string sql)
    {
        var start = sql.IndexOf("SELECT", StringComparison.Ordinal);
        var depth = 0;
        for (var i = start; i < sql.Length - 4; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(sql, i, "FROM ", 0, 5) == 0 && i > 0 && char.IsWhiteSpace(sql[i - 1]))
                return sql[start..i];
        }

        return sql[start..];
    }

    /// <summary>The SQL a named shape emits, with the cap applied where the repository applies it in SQL.</summary>
    private static string ShapeSql(DashboardRepository repository, string shape)
    {
        var at   = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        return shape switch
        {
            nameof(DashboardRepository.LineTotalsQuery)                  => repository.LineTotalsQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.OccupiedByCurrencyQuery)          => repository.OccupiedByCurrencyQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.LinesByRiskQuery)                 => repository.LinesByRiskQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.OccupiedByRiskQuery)              => repository.OccupiedByRiskQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.TopExposuresQuery)                => repository.TopExposuresQuery(Tenant, "ARS").Take(10).ToQueryString(),
            nameof(DashboardRepository.HighUtilizationCountsQuery)       => repository.HighUtilizationCountsQuery(Tenant, 90m).ToQueryString(),
            nameof(DashboardRepository.HighUtilizationQuery)             => repository.HighUtilizationQuery(Tenant, "ARS", 90m).Take(10).ToQueryString(),
            nameof(DashboardRepository.LinesBySectorQuery)               => repository.LinesBySectorQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.OccupiedBySectorQuery)            => repository.OccupiedBySectorQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.MaturityGroupsQuery)              => repository.MaturityGroupsQuery(Tenant, at.AddDays(91)).ToQueryString(),
            nameof(DashboardRepository.UpcomingMaturitiesQuery)          => repository.UpcomingMaturitiesQuery(Tenant, at).Take(10).ToQueryString(),
            nameof(DashboardRepository.BcraComparisonsQuery)             => repository.BcraComparisonsQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.BcraCurrentQuery)                 => repository.BcraCurrentQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.LatestApprovedAnalysesQuery)      => repository.LatestApprovedAnalysesQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.OccupyingOperationsOfActiveLinesQuery) => repository.OccupyingOperationsOfActiveLinesQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.TeamQueueQuery)                   => repository.TeamQueueQuery(Tenant).Take(50).ToQueryString(),
            nameof(DashboardRepository.TeamQueueByRequesterQuery)        => repository.TeamQueueByRequesterQuery(Tenant).ToQueryString(),
            nameof(DashboardRepository.AgainstSuggestionCandidatesQuery) => repository.AgainstSuggestionCandidatesQuery(Tenant, at.AddDays(-90)).ToQueryString(),
            nameof(DashboardRepository.RecentDecisionsQuery)             => repository.RecentDecisionsQuery(Tenant).Take(20).ToQueryString(),
            nameof(DashboardRepository.RecentRescoresQuery)              => repository.RecentRescoresQuery(Tenant).Take(10).ToQueryString(),
            nameof(DashboardRepository.CreatedByStatusQuery)             => repository.CreatedByStatusQuery(Tenant, at.AddMonths(-1), at).ToQueryString(),
            nameof(DashboardRepository.DecisionTimesQuery)               => repository.DecisionTimesQuery(Tenant, at.AddMonths(-1), at).ToQueryString(),
            nameof(DashboardRepository.ApiCallOutcomesQuery)           => repository.ApiCallOutcomesQuery(Tenant, at.AddDays(-1), at).ToQueryString(),
            nameof(DashboardRepository.ApiCallDurationsQuery)            => repository.ApiCallDurationsQuery(Tenant, at.AddDays(-1), at).ToQueryString(),
            nameof(DashboardRepository.FreshnessQuery)                   => repository.FreshnessQuery(Tenant, at.AddDays(-30), at.AddDays(-90)).ToQueryString(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
    }

    /// <summary>
    /// Score credibility 1b-2b: the page-sized follow-up read behind every dashboard item's verdict. It is
    /// the ONLY dashboard statement allowed to name ScoreBreakdownJson — four columns, both tables
    /// tenant-scoped, an IN-list of the page's results — and the shared row projection still never does.
    /// </summary>
    [Fact]
    public void ScoreReliabilitySourceQuery_ReadsExactlyFourColumns_ForThePageOnly_TenantScoped()
    {
        using var ctx = NewSqlServerContext();

        var sql = new DashboardRepository(ctx)
            .ScoreReliabilitySourceQuery(Tenant, [Guid.NewGuid(), Guid.NewGuid()])
            .ToQueryString();

        var select = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..sql.IndexOf("FROM", StringComparison.Ordinal)];
        Assert.Equal(4, select.Split(',').Length);
        Assert.Contains("[ResultId]", select);
        Assert.Contains("[RescoredAt]", select);
        Assert.Contains("[FiscalYear]", select);
        Assert.Contains("[ScoreBreakdownJson]", select);
        Assert.Contains(" IN (", sql);
        AssertEveryTableIsTenantScoped(sql);
        foreach (var blob in new[] { "[BalanceJson]", "[RatiosJson]", "[BcraDataJson]", "[ExecutiveSummary]" })
            Assert.DoesNotContain(blob, sql);
    }

    /// <summary>
    /// Every table the statement reads — each FROM/JOIN [dbo].[X] AS [alias] — carries its own
    /// [alias].[TenantId] = @param predicate. A join keyed by a global id would otherwise reach rows of
    /// another tenant.
    /// </summary>
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

    /// <summary>None of the nvarchar(max) blobs of either table is ever selected by a row projection.</summary>
    private static void AssertNoBlob(string sql)
    {
        foreach (var blob in new[]
                 {
                     "[BalanceJson]", "[RatiosJson]", "[ScoreBreakdownJson]", "[BcraDataJson]", "[ExecutiveSummary]",
                     "[ProposedOperationsJson]", "[ConfirmedOperationsJson]", "[PreApprovalChecksJson]", "[ConfirmationChecksJson]"
                 })
        {
            Assert.DoesNotContain(blob, sql);
        }
    }
}
