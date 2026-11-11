// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 072 mapping to docs/database/072_person_analyses_and_participants.sql, following
/// LegajoReconciliationMappingTests: the CHECK lists against AnalysisKinds / ParticipantRoles, the
/// AnalysisParticipants columns against the model (name, store type, nullability), the two [Analyses]
/// changes, the indexes and the deliberate absence of modelled FKs.
/// </summary>
public sealed class PersonAnalysisMappingTests
{
    private const string Ddl072FileName = "072_person_analyses_and_participants.sql";

    // ── CHECK lists vs constants ────────────────────────────────────────────────

    public static TheoryData<string, string[]> Checks => new()
    {
        { "CK_Analyses_AnalysisKind",     [.. AnalysisKinds.Known] },
        { "CK_AnalysisParticipants_Role", [.. ParticipantRoles.Known] }
    };

    [Theory]
    [MemberData(nameof(Checks))]
    public void Ddl072_Check_AdmitsExactlyTheConstants(string constraint, string[] expected)
    {
        Assert.Equal(Sorted(expected), Sorted(CheckConstraint(ReadDdl072(), constraint)));
    }

    [Fact]
    public void Ddl072_DocumentIdNull_IsLegalOnlyOnAPersonaAnalysis()
    {
        Assert.Matches(
            @"CONSTRAINT\s+\[CK_Analyses_DocumentId_Kind\]\s+CHECK\s*\(\[DocumentId\]\s+IS\s+NOT\s+NULL\s+OR\s+\[AnalysisKind\]\s*=\s*N'" +
            AnalysisKinds.PersonaHumana + @"'\)",
            ReadDdl072());
    }

    [Fact]
    public void Ddl072_ParticipantChecks_AreDeclared()
    {
        var ddl = ReadDdl072();

        Assert.Matches(@"CONSTRAINT\s+\[CK_AnalysisParticipants_AmountAtCharge\]\s+CHECK\s*\(\[AmountAtCharge\]\s*>\s*0\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[CK_AnalysisParticipants_InstallmentAmount\]\s+CHECK\s*\(\[InstallmentAmount\]\s+IS\s+NULL\s+OR", ddl);
        foreach (var column in new[] { "DocumentVersionsJson", "FiltersJson", "IndicatorsJson", "CoverageJson" })
            Assert.Matches(@"CONSTRAINT\s+\[CK_AnalysisParticipants_" + column + @"\]\s+CHECK\s*\(ISJSON\(\[" + column + @"\]\)\s*=\s*1\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[CK_AnalysisParticipants_ScoreJson\]\s+CHECK\s*\(\[ScoreJson\]\s+IS\s+NULL\s+OR\s+ISJSON", ddl);
    }

    // ── [Analyses] changes vs model ─────────────────────────────────────────────

    [Fact]
    public void Ddl072_AnalysisKind_IsNotNullWithBalanceDefault_AndMapped()
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[Analyses\]\s+ADD\s+\[AnalysisKind\]\s+NVARCHAR\(20\)\s+NOT\s+NULL\s+" +
            @"CONSTRAINT\s+\[DF_Analyses_AnalysisKind\]\s+DEFAULT\s*\(N'" + AnalysisKinds.Balance + @"'\);",
            ReadDdl072());

        var mapped = TypeOf<Analysis>().FindProperty(nameof(Analysis.AnalysisKind))!;
        Assert.Equal("AnalysisKind", mapped.GetColumnName());
        Assert.False(mapped.IsNullable);
        Assert.Equal("nvarchar(20)", StoreType(mapped));
        Assert.Equal(AnalysisKinds.Balance, new Analysis().AnalysisKind);
    }

    [Fact]
    public void Ddl072_DocumentId_BecomesNullable_AndTheModelSaysSo()
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[Analyses\]\s+ALTER\s+COLUMN\s+\[DocumentId\]\s+UNIQUEIDENTIFIER\s+NULL;",
            ReadDdl072());

        Assert.True(TypeOf<Analysis>().FindProperty(nameof(Analysis.DocumentId))!.IsNullable);
    }

    [Fact]
    public void Ddl072_DocumentIdAlter_IsGuardedAndStopsOnAnUnknownIndex()
    {
        var ddl = ReadDdl072();

        Assert.Matches(@"name\s*=\s*N'DocumentId'\s+AND\s+is_nullable\s*=\s*0", ddl);
        Assert.Contains("RAISERROR", ddl);
        Assert.DoesNotMatch(@"DROP\s+INDEX", ddl);

        // RAISERROR ends only its batch: NOEXEC is what keeps the later batches from running over a
        // DocumentId that is still NOT NULL, and the script must turn it back off at the end.
        Assert.Matches(@"RAISERROR[^;]*;\s*SET\s+NOEXEC\s+ON;", ddl);
        Assert.Matches(@"SET\s+NOEXEC\s+OFF;\s*GO\s*$", ddl.TrimEnd());
    }

    [Theory]
    [InlineData("PreApprovalChecksJson")]
    [InlineData("ConfirmationChecksJson")]
    public void Ddl072_ChecksColumn_IsNullableJson_AndMapped(string column)
    {
        var ddl = ReadDdl072();
        Assert.Matches(@"ALTER\s+TABLE\s+\[dbo\]\.\[Analyses\]\s+ADD\s+\[" + column + @"\]\s+NVARCHAR\(MAX\)\s+NULL;", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[CK_Analyses_" + column + @"\]\s+CHECK\s*\(\[" + column + @"\]\s+IS\s+NULL\s+OR\s+ISJSON\(\[" + column + @"\]\)\s*=\s*1\)", ddl);

        var mapped = TypeOf<Analysis>().FindProperty(column)!;
        Assert.Equal(column, mapped.GetColumnName());
        Assert.True(mapped.IsNullable);
        Assert.Equal("nvarchar(max)", StoreType(mapped));
    }

    // ── AnalysisParticipants columns vs model ───────────────────────────────────

    [Fact]
    public void Ddl072_AnalysisParticipants_DeclaresExactlyTheMappedColumns()
    {
        var declared = DeclaredColumns(CreateTableBody(ReadDdl072(), "AnalysisParticipants"))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var entityType = TypeOf<AnalysisParticipant>();
        Assert.Equal("AnalysisParticipants", entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());

        // DDL 077 added the five fiador columns, DDL 078 the two agro terms and DDL 079 the declared installments
        // with ALTER TABLE; AnalysisParticipantFiadorMappingTests, AnalysisParticipantAgroMappingTests and
        // PersonInstallmentsAndCashKindMappingTests pin those against the model, so this one compares the 072
        // table with the rest.
        var mapped = entityType.GetProperties()
            .Where(p => !AnalysisParticipantFiadorMappingTests.Ddl077Columns.Contains(p.GetColumnName())
                     && !AnalysisParticipantAgroMappingTests.Ddl078Columns.Contains(p.GetColumnName())
                     && !PersonInstallmentsAndCashKindMappingTests.Ddl079Columns.Contains(p.GetColumnName()))
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(15, declared.Count);
        Assert.Equal(declared, mapped);
    }

    [Theory]
    [InlineData("UX_AnalysisParticipants_Analysis_Legajo_Role", true, new[] { "TenantId", "AnalysisId", "PersonLegajoId", "Role" })]
    [InlineData("IX_AnalysisParticipants_TenantId_AnalysisId", false, new[] { "TenantId", "AnalysisId" })]
    public void Index_IsModelledAndCreatedByTheDdl(string name, bool unique, string[] columns)
    {
        var index = Assert.Single(TypeOf<AnalysisParticipant>().GetIndexes(), i => i.GetDatabaseName() == name);
        Assert.Equal(unique, index.IsUnique);
        Assert.Equal(columns, index.Properties.Select(p => p.GetColumnName()));
        Assert.Null(index.GetFilter());

        var kind = unique ? @"UNIQUE\s+NONCLUSTERED" : "NONCLUSTERED";
        var ddlColumns = string.Join(@",\s*", columns.Select(c => @"\[" + c + @"\]\s+ASC"));
        Assert.Matches(@"CREATE\s+" + kind + @"\s+INDEX\s+\[" + name + @"\]\s+ON\s+\[dbo\]\.\[AnalysisParticipants\]\s*\(" + ddlColumns + @"\)",
                       ReadDdl072());
    }

    [Theory]
    [InlineData("FK_AnalysisParticipants_Analyses")]
    [InlineData("FK_AnalysisParticipants_PersonLegajos")]
    public void Ddl072_DeclaresTheForeignKey(string name)
    {
        Assert.Matches(@"CONSTRAINT\s+\[" + name + @"\]\s+FOREIGN\s+KEY", ReadDdl072());
    }

    [Fact]
    public void ParticipantRelationships_AreNotModelled()
    {
        Assert.Empty(TypeOf<AnalysisParticipant>().GetForeignKeys());
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl072_IsIdempotentAndSetsTheSessionOptions()
    {
        var ddl = ReadDdl072();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        Assert.Single(Regex.Matches(ddl, @"CREATE\s+TABLE"));
        Assert.Single(Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.objects"));
        Assert.Equal(2, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.indexes").Count);
        Assert.Equal(4, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints").Count);
        Assert.Equal(3, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns").Count);
        Assert.DoesNotContain("BEGIN TRANSACTION", ddl);
    }

    // ── Helpers (as in LegajoReconciliationMappingTests) ────────────────────────

    private static IEntityType TypeOf<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(TEntity))!;
    }

    private static string StoreType(IProperty property)
        => property.GetColumnType().Replace(" ", string.Empty).ToLowerInvariant();

    private static string ReadDdl072Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl072FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl072FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl072()
    {
        var text = Regex.Replace(ReadDdl072Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }

    private static HashSet<string> CheckConstraint(string ddl, string name)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CONSTRAINT\s+\[" + Regex.Escape(name) + @"\]\s+CHECK\s*\((?<expr>[^;]*?)\bIN\s*\((?<list>[^)]*)\)",
            RegexOptions.Singleline));

        return Regex.Matches(match.Groups["list"].Value, @"N?'(?<value>[^']*)'")
            .Select(m => m.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);
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

    private static string[] Sorted(IEnumerable<string> values)
        => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();
}
