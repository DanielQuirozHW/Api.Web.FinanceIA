// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Clases.SchemaVerification;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the [AnalysisEdits] mapping to its two scripts: 001_initial_schema.sql, which created the table
/// (UTF-16, SSMS-style), and 081_analysis_edits_tenantid.sql, which retypes TenantId and replaces the
/// index. 081 must be idempotent: every block guarded on its sys.* view, with a PRINT in both branches.
/// </summary>
public sealed class AnalysisEditMappingTests
{
    private const string Ddl081FileName = "081_analysis_edits_tenantid.sql";
    private const string Ddl001FileName = "001_initial_schema.sql";

    // ── The mapping against 001 (every column but TenantId) ─────────────────────

    [Theory]
    [InlineData("AnalysisEditId", "bigint",           false)]
    [InlineData("AnalysisId",     "uniqueidentifier", false)]
    [InlineData("EditedBy",       "uniqueidentifier", false)]
    [InlineData("FieldPath",      "nvarchar(200)",    false)]
    [InlineData("OldValue",       "nvarchar(max)",    true)]
    [InlineData("NewValue",       "nvarchar(max)",    true)]
    [InlineData("EditReason",     "nvarchar(max)",    true)]
    [InlineData("EditedAt",       "datetime2",        false)]
    public void Every_001_column_is_mapped_with_its_type(string column, string storeType, bool nullable)
    {
        var body = Ddl001AnalysisEditsBody();
        Assert.Matches(
            @"\[" + column + @"\]\s+\[[a-z0-9]+\](\([^)]*\))?(\s+IDENTITY\(1,1\))?\s+" + (nullable ? "NULL," : "NOT NULL,"),
            body);

        var mapped = TypeOf<AnalysisEdit>().GetProperties().Single(p => p.GetColumnName() == column);
        Assert.Equal(storeType, StoreType(mapped));
        Assert.Equal(nullable, mapped.IsNullable);
    }

    [Fact]
    public void The_table_has_exactly_the_nine_columns_of_001()
    {
        var ddlColumns = Regex.Matches(Ddl001AnalysisEditsBody(), @"^\s*\[(?<name>\w+)\]\s+\[", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .OrderBy(n => n, StringComparer.Ordinal);

        var mapped = TypeOf<AnalysisEdit>().GetProperties().Select(p => p.GetColumnName()).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(ddlColumns, mapped);
        Assert.Equal(9, mapped.Count());
    }

    [Fact]
    public void TenantId_is_a_slug_in_the_model_although_001_created_it_uniqueidentifier()
    {
        Assert.Matches(@"\[TenantId\]\s+\[uniqueidentifier\]\s+NOT\s+NULL", Ddl001AnalysisEditsBody());

        var mapped = TypeOf<AnalysisEdit>().FindProperty(nameof(AnalysisEdit.TenantId))!;
        Assert.Equal("nvarchar(100)", StoreType(mapped));
        Assert.False(mapped.IsNullable);
    }

    [Fact]
    public void The_key_is_the_identity_and_the_history_index_is_declared()
    {
        var type = TypeOf<AnalysisEdit>();
        Assert.Equal(["AnalysisEditId"], type.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()));
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd,
            type.FindProperty(nameof(AnalysisEdit.AnalysisEditId))!.ValueGenerated);
        Assert.Equal("AnalysisEdits", type.GetTableName());
        Assert.Equal("dbo", type.GetSchema());

        var index = Assert.Single(DesignTimeTypeOf<AnalysisEdit>().GetIndexes());
        Assert.Equal("IX_AnalysisEdits_TenantId_AnalysisId_EditedAt", index.GetDatabaseName());
        Assert.Equal(["TenantId", "AnalysisId", "EditedAt"], index.Properties.Select(p => p.GetColumnName()));
        Assert.Equal([false, false, true], index.IsDescending);
    }

    [Fact]
    public void The_schema_gate_checks_the_TenantId_type_of_this_table()
    {
        var expectation = SchemaExpectation.FromModel(TypeOf<AnalysisEdit>().Model);

        var typed = Assert.Single(expectation.ColumnTypes);
        Assert.Equal(("dbo", "AnalysisEdits", "TenantId", "nvarchar"), (typed.Schema, typed.Table, typed.Column, typed.SqlType));
    }

    // ── 081: the shape of the script ────────────────────────────────────────────

    [Fact]
    public void Ddl081_creates_the_table_only_when_it_is_missing_with_a_slug_TenantId()
    {
        var ddl = ReadDdl081();

        Assert.Matches(
            @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.objects\s+WHERE\s+object_id\s*=\s*OBJECT_ID\(N'\[dbo\]\.\[AnalysisEdits\]'\)\s+AND\s+type\s*=\s*N'U'\s*\)\s*BEGIN\s+CREATE\s+TABLE",
            ddl);
        Assert.Matches(@"\[TenantId\]\s+NVARCHAR\(100\)\s+NOT\s+NULL", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[PK_AnalysisEdits\]\s+PRIMARY\s+KEY\s+CLUSTERED\s*\(\[AnalysisEditId\]\s+ASC\)", ddl);
        Assert.Matches(@"\[AnalysisEditId\]\s+BIGINT\s+IDENTITY\(1,\s*1\)\s+NOT\s+NULL", ddl);
    }

    [Fact]
    public void Ddl081_retypes_TenantId_only_while_uniqueidentifier_only_on_an_empty_table_in_a_transaction()
    {
        var ddl = ReadDdl081();

        var block = Regex.Match(ddl,
            @"IF\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns\s+c\s+JOIN\s+sys\.types\s+t.*?t\.name\s*=\s*N'uniqueidentifier'\s*\)\s*BEGIN(?<body>.*?)\bEND\s+ELSE\s+BEGIN\s+PRINT",
            RegexOptions.Singleline);
        Assert.True(block.Success, "the type-guarded block is missing");

        var body = block.Groups["body"].Value;
        Assert.Matches(@"COUNT_BIG\(\*\)\s+FROM\s+\[dbo\]\.\[AnalysisEdits\]", body);
        Assert.Matches(@"IF\s+@Rows\s*>\s*0\s+BEGIN\s+RAISERROR", body);
        Assert.Matches(@"SET\s+XACT_ABORT\s+ON;\s+BEGIN\s+TRANSACTION;", body);

        // The indexes on TenantId go BEFORE the ALTER — SQL Server cannot alter an indexed column.
        var drop  = body.IndexOf("DROP INDEX IF EXISTS [IX_AnalysisEdits_TenantId_AnalysisId]", StringComparison.Ordinal);
        var drop2 = body.IndexOf("DROP INDEX IF EXISTS [IX_AnalysisEdits_TenantId_AnalysisId_EditedAt]", StringComparison.Ordinal);
        var alter = Regex.Match(body, @"ALTER\s+COLUMN\s+\[TenantId\]\s+NVARCHAR\(100\)\s+NOT\s+NULL").Index;
        Assert.True(drop >= 0 && drop2 >= 0 && alter > drop && alter > drop2, "the indexes must be dropped before the ALTER");
        Assert.Matches(@"COMMIT\s+TRANSACTION;", body);
    }

    [Fact]
    public void Ddl081_replaces_the_001_index_by_the_history_index_once_TenantId_is_nvarchar()
    {
        var ddl = ReadDdl081();

        Assert.Matches(new Regex(
            @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.indexes.*?name\s*=\s*N'IX_AnalysisEdits_TenantId_AnalysisId_EditedAt'\s*\)\s*AND\s+EXISTS.*?t\.name\s*=\s*N'nvarchar'\s*\)\s*BEGIN\s+CREATE\s+NONCLUSTERED\s+INDEX\s+\[IX_AnalysisEdits_TenantId_AnalysisId_EditedAt\]\s+ON\s+\[dbo\]\.\[AnalysisEdits\]\s*\(\[TenantId\]\s+ASC,\s*\[AnalysisId\]\s+ASC,\s*\[EditedAt\]\s+DESC\)",
            RegexOptions.Singleline), ddl);
        Assert.Matches(new Regex(
            @"name\s*=\s*N'IX_AnalysisEdits_TenantId_AnalysisId'\s*\)\s*AND\s+NOT\s+EXISTS.*?N'uniqueidentifier'\s*\)\s*BEGIN\s+DROP\s+INDEX\s+\[IX_AnalysisEdits_TenantId_AnalysisId\]",
            RegexOptions.Singleline), ddl);
    }

    [Fact]
    public void Ddl081_is_idempotent_every_block_prints_in_both_branches()
    {
        var ddl = ReadDdl081();

        // Four guarded blocks, each with a BEGIN … PRINT … END ELSE BEGIN PRINT … END.
        Assert.Equal(4, Regex.Matches(ddl, @"END\s+ELSE\s+BEGIN\s+PRINT\s+'[^']+';\s+END\s+GO", RegexOptions.Singleline).Count);
        Assert.Equal(4, Regex.Matches(ddl, @"^IF\s+(NOT\s+)?EXISTS", RegexOptions.Multiline).Count);
        Assert.DoesNotMatch(@"\bDROP\s+TABLE\b", ddl);
        Assert.DoesNotMatch(@"\bDELETE\b", ddl);
        Assert.DoesNotMatch(@"\bUPDATE\b", ddl);
        Assert.Matches(@"^SET\s+ANSI_NULLS\s+ON;\s+SET\s+QUOTED_IDENTIFIER\s+ON;\s+GO", ddl.TrimStart());
    }

    [Fact]
    public void Ddl081_is_registered_in_the_readme_history_and_has_its_rationale()
    {
        var readme = File.ReadAllText(DocPath("docs", "database", "README.md"));
        Assert.Contains("| " + Ddl081FileName, readme);
        Assert.True(File.Exists(DocPath("docs", "db-changes", "081_analysis_edits_tenantid.md")));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IEntityType TypeOf<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>().UseSqlServer("Server=none;Database=none;").Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(TEntity))!;
    }

    private static IEntityType DesignTimeTypeOf<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>().UseSqlServer("Server=none;Database=none;").Options;
        using var context = new FinanciaDbContext(options);
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity))!;
    }

    private static string StoreType(IProperty property)
        => property.GetColumnType().Replace(" ", string.Empty).ToLowerInvariant();

    private static string DocPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, "Api.Web.FinanceIA", .. parts]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"{Path.Combine(parts)} was not found above {AppContext.BaseDirectory}.");
    }

    /// <summary>081 with comments stripped: the commented post-checks and rollback name the same objects.</summary>
    private static string ReadDdl081()
    {
        var text = Regex.Replace(File.ReadAllText(DocPath("docs", "database", Ddl081FileName)), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }

    /// <summary>The column list of [AnalysisEdits] in 001 (UTF-16 with a BOM; ReadAllText detects it).</summary>
    private static string Ddl001AnalysisEditsBody()
    {
        var ddl   = File.ReadAllText(DocPath("docs", "database", Ddl001FileName));
        var match = Regex.Match(ddl, @"CREATE\s+TABLE\s+\[dbo\]\.\[AnalysisEdits\]\((?<body>.*?)CONSTRAINT\s+\[PK_AnalysisEdits\]", RegexOptions.Singleline);
        Assert.True(match.Success, "001 no longer defines [AnalysisEdits]");
        return match.Groups["body"].Value;
    }
}
