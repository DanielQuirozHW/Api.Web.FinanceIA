// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-22

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 073 mapping to docs/database/073_analysis_rescores.sql, following
/// PersonAnalysisMappingTests: the three [AnalysisResults] columns (and the concurrency token the
/// re-score write depends on), the [AnalysisRescores] columns against the model, the index, the FK and
/// the shape of the script.
/// </summary>
public sealed class AnalysisRescoreMappingTests
{
    private const string Ddl073FileName = "073_analysis_rescores.sql";

    // ── [AnalysisResults] columns vs model ──────────────────────────────────────

    [Theory]
    [InlineData("RescoredAt", "DATETIME2",        "datetime2")]
    [InlineData("RescoredBy", "UNIQUEIDENTIFIER", "uniqueidentifier")]
    public void Ddl073_NullableResultColumn_IsAddedAndMapped(string column, string ddlType, string storeType)
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisResults\]\s+ADD\s+\[" + column + @"\]\s+" + ddlType + @"\s+NULL;",
            ReadDdl073());

        var mapped = TypeOf<AnalysisResult>().FindProperty(column)!;
        Assert.Equal(column, mapped.GetColumnName());
        Assert.True(mapped.IsNullable);
        Assert.Equal(storeType, StoreType(mapped));
    }

    [Fact]
    public void Ddl073_RescoreCount_IsNotNullDefaultZero_AndTheConcurrencyToken()
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisResults\]\s+ADD\s+\[RescoreCount\]\s+INT\s+NOT\s+NULL\s+" +
            @"CONSTRAINT\s+\[DF_AnalysisResults_RescoreCount\]\s+DEFAULT\s*\(0\);",
            ReadDdl073());

        var mapped = TypeOf<AnalysisResult>().FindProperty(nameof(AnalysisResult.RescoreCount))!;
        Assert.Equal("RescoreCount", mapped.GetColumnName());
        Assert.False(mapped.IsNullable);
        Assert.Equal("int", StoreType(mapped));

        // The re-score write is UPDATE … WHERE ResultId = @id AND RescoreCount = @expected only because
        // of this flag; losing it would let two concurrent re-scores both land.
        Assert.True(mapped.IsConcurrencyToken);
    }

    [Fact]
    public void AnalysisResult_HasNoOtherConcurrencyToken()
    {
        var tokens = TypeOf<AnalysisResult>().GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name);

        Assert.Equal([nameof(AnalysisResult.RescoreCount)], tokens);
    }

    // ── [AnalysisRescores] columns vs model ─────────────────────────────────────

    [Fact]
    public void Ddl073_AnalysisRescores_DeclaresExactlyTheMappedColumns()
    {
        var declared = DeclaredColumns(CreateTableBody(ReadDdl073(), "AnalysisRescores"))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var entityType = TypeOf<AnalysisRescore>();
        Assert.Equal("AnalysisRescores", entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());

        var mapped = entityType.GetProperties()
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(14, declared.Count);
        Assert.Equal(declared, mapped);
    }

    [Fact]
    public void Ddl073_ScoreLevelAndStatusColumns_MatchTheirAnalysisResultsTwins()
    {
        var results  = TypeOf<AnalysisResult>();
        var rescores = TypeOf<AnalysisRescore>();

        foreach (var (twin, columns) in new[]
                 {
                     (nameof(AnalysisResult.AiSuggestedScore), new[] { "PreviousScore", "NewScore" }),
                     (nameof(AnalysisResult.AiSuggestedLevel), new[] { "PreviousLevel", "NewLevel" }),
                     (nameof(AnalysisResult.EnrichmentStatus), new[] { "PreviousEnrichmentStatus", "NewEnrichmentStatus" })
                 })
        {
            foreach (var column in columns)
                Assert.Equal(StoreType(results.FindProperty(twin)!), StoreType(rescores.FindProperty(column)!));
        }
    }

    [Fact]
    public void Ddl073_JsonColumns_AreCheckedWithIsJson()
    {
        var ddl = ReadDdl073();

        foreach (var column in new[] { "PreviousValuesJson", "SourcesJson" })
            Assert.Matches(@"CONSTRAINT\s+\[CK_AnalysisRescores_" + column + @"\]\s+CHECK\s*\(ISJSON\(\[" + column + @"\]\)\s*=\s*1\)", ddl);
    }

    [Fact]
    public void Index_IsModelledAndCreatedByTheDdl_WithRescoredAtDescending()
    {
        const string name = "IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt";

        // IsDescending is design-time metadata: the read-optimised runtime model does not keep it.
        var index = Assert.Single(DesignTimeTypeOf<AnalysisRescore>().GetIndexes(), i => i.GetDatabaseName() == name);
        Assert.False(index.IsUnique);
        Assert.Equal(["TenantId", "AnalysisId", "RescoredAt"], index.Properties.Select(p => p.GetColumnName()));
        Assert.Equal([false, false, true], index.IsDescending);

        Assert.Matches(
            @"CREATE\s+NONCLUSTERED\s+INDEX\s+\[" + name + @"\]\s+ON\s+\[dbo\]\.\[AnalysisRescores\]\s*\(" +
            @"\[TenantId\]\s+ASC,\s*\[AnalysisId\]\s+ASC,\s*\[RescoredAt\]\s+DESC\)",
            ReadDdl073());
    }

    [Fact]
    public void Ddl073_DeclaresTheForeignKey_ButTheModelDoesNot()
    {
        Assert.Matches(
            @"CONSTRAINT\s+\[FK_AnalysisRescores_AnalysisId\]\s+FOREIGN\s+KEY\s*\(\[AnalysisId\]\)\s+REFERENCES\s+\[dbo\]\.\[Analyses\]",
            ReadDdl073());

        Assert.Empty(TypeOf<AnalysisRescore>().GetForeignKeys());
    }

    [Fact]
    public void RescoreId_IsSuppliedByTheApplication()
    {
        Assert.Equal(ValueGenerated.Never, TypeOf<AnalysisRescore>().FindProperty(nameof(AnalysisRescore.RescoreId))!.ValueGenerated);
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl073_IsIdempotentAndSetsTheSessionOptions()
    {
        var ddl = ReadDdl073();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        Assert.Single(Regex.Matches(ddl, @"CREATE\s+TABLE"));
        Assert.Single(Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.objects"));
        Assert.Single(Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.indexes"));
        Assert.Equal(3, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns").Count);
        Assert.DoesNotContain("BEGIN TRANSACTION", ddl);
        Assert.DoesNotMatch(@"\bDROP\s+", ddl);
    }

    // ── Helpers (as in PersonAnalysisMappingTests) ──────────────────────────────

    private static IEntityType TypeOf<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(TEntity))!;
    }

    private static IEntityType DesignTimeTypeOf<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity))!;
    }

    private static string StoreType(IProperty property)
        => property.GetColumnType().Replace(" ", string.Empty).ToLowerInvariant();

    private static string ReadDdl073Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl073FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl073FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented post-checks and rollback name the same objects.</summary>
    private static string ReadDdl073()
    {
        var text = Regex.Replace(ReadDdl073Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }

    private static string CreateTableBody(string ddl, string table)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CREATE\s+TABLE\s+\[dbo\]\.\[" + Regex.Escape(table) + @"\]\s*\((?<body>.*?)\r?\n\s*\);",
            RegexOptions.Singleline));

        return match.Groups["body"].Value;
    }

    private static List<(string Name, string Type, bool IsNullable)> DeclaredColumns(string body)
        => Regex.Matches(
                body,
                @"^\s*\[(?<name>\w+)\]\s+(?<type>[A-Z0-9]+(?:\((?:\d+|MAX)(?:,\s*\d+)?\))?)\s+(?<null>NOT\s+NULL|NULL)",
                RegexOptions.Multiline)
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.Replace(" ", string.Empty).ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .ToList();
}
