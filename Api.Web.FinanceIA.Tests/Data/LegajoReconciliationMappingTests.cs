// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 071 mapping to docs/database/071_legajo_items_and_incomes.sql, following
/// LegajoDocumentsMappingTests: every CHECK list against its constants class, every CREATE TABLE column
/// against the model (name, store type, nullability), the two ALTERed [LegajoExtractions] columns, the
/// defaults, the index names and uniqueness, and the deliberate absence of modelled FKs.
/// </summary>
public sealed class LegajoReconciliationMappingTests
{
    private const string Ddl071FileName = "071_legajo_items_and_incomes.sql";

    // ── CHECK lists vs constants ────────────────────────────────────────────────

    public static TheoryData<string, string[]> Checks => new()
    {
        { "CK_LegajoItems_Side",                 [.. LegajoItemSides.Known] },
        // 079 widened this CHECK with Efectivo (PersonInstallmentsAndCashKindMappingTests pins the new list).
        { "CK_LegajoItems_Kind",                 [.. LegajoItemKinds.Known.Where(k => k != LegajoItemKinds.Efectivo)] },
        { "CK_LegajoItems_MatchKeyKind",         [.. LegajoMatchKeyKinds.Known] },
        { "CK_LegajoItems_ReconciliationStatus", [.. LegajoReconciliationStatuses.Known] },
        { "CK_LegajoIncomes_Source",             [.. LegajoIncomeSources.Known] },
        { "CK_LegajoIncomes_Concept",            [.. LegajoIncomeConcepts.Known] }
    };

    [Theory]
    [MemberData(nameof(Checks))]
    public void Ddl071_Check_AdmitsExactlyTheConstants(string constraint, string[] expected)
    {
        Assert.Equal(Sorted(expected), Sorted(CheckConstraint(ReadDdl071(), constraint)));
    }

    [Fact]
    public void Ddl071_MatchKeyKinds_AreTheExtractionVocabulary()
    {
        // The schemas' matchKey.kind is what the reconciliation stores: the two lists cannot drift.
        Assert.Equal(global::Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary.MatchKeyKinds, LegajoMatchKeyKinds.Known);
    }

    [Fact]
    public void Ddl071_ItemKinds_AreTheManifestacionAnnexKinds()
    {
        // Every kind but Efectivo, which only a Bienes Personales rubro (DineroEfectivo) produces (engine 0.8).
        Assert.Equal(global::Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary.AnnexKinds,
            LegajoItemKinds.Known.Where(k => k != LegajoItemKinds.Efectivo));
    }

    [Fact]
    public void Ddl071_RangeChecks_AreDeclared()
    {
        var ddl = ReadDdl071();

        Assert.Matches(@"CONSTRAINT\s+\[CK_LegajoItems_OwnershipPct\]\s+CHECK\s*\(\[OwnershipPct\]\s+IS\s+NULL\s+OR", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[CK_LegajoIncomes_Period\]\s+CHECK\s*\(\[Period\]\s+IS\s+NULL", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[CK_LegajoIncomes_PeriodRange\]\s+CHECK\s*\(\[PeriodFrom\]\s+<=\s+\[PeriodTo\]\)", ddl);
    }

    [Theory]
    [InlineData("2025-03", true)]
    [InlineData("2025", true)]
    [InlineData("2025-3", false)]
    [InlineData("25-03", false)]
    public void Ddl071_PeriodPatterns_AdmitWhatTheReconciliationWrites(string period, bool admitted)
    {
        // The two LIKE patterns of CK_LegajoIncomes_Period, as a regex: what LegajoReconciliation.PeriodLabel writes.
        var ddl = ReadDdl071();
        Assert.Contains("[Period] LIKE N'[12][0-9][0-9][0-9]'", ddl);
        Assert.Contains("[Period] LIKE N'[12][0-9][0-9][0-9]-[01][0-9]'", ddl);

        Assert.Equal(admitted, Regex.IsMatch(period, @"^[12][0-9]{3}(-[01][0-9])?$"));
    }

    // ── Columns vs model ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LegajoItems", typeof(LegajoItem), 26)]
    [InlineData("LegajoIncomes", typeof(LegajoIncome), 13)]
    public void Ddl071_NewTable_DeclaresExactlyTheMappedColumns(string table, Type clrType, int columnCount)
    {
        var declared = DeclaredColumns(CreateTableBody(ReadDdl071(), table))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var entityType = TypeOfClr(clrType);
        Assert.Equal(table, entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());

        // The columns later scripts add (080: InvestmentClass) are pinned by their own mapping tests.
        var mapped = entityType.GetProperties()
            .Where(p => !LegajoInvestmentClassMappingTests.Ddl080Columns.Contains(p.GetColumnName()))
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(columnCount, declared.Count);
        Assert.Equal(declared, mapped);
    }

    [Theory]
    [InlineData("ReviewOverride", "bit")]
    [InlineData("ReviewReason", "nvarchar(1000)")]
    public void Ddl071_AlteredLegajoExtractionsColumn_MatchesTheModel(string column, string storeType)
    {
        Assert.Matches(@"ALTER\s+TABLE\s+\[dbo\]\.\[LegajoExtractions\]\s+ADD\s+\[" + column + @"\]\s+" +
                       Regex.Escape(storeType.ToUpperInvariant()) + @"\s+NULL;", ReadDdl071());

        var mapped = TypeOfClr(typeof(LegajoExtraction)).FindProperty(column)!;
        Assert.Equal(column, mapped.GetColumnName());
        Assert.True(mapped.IsNullable);
        Assert.Equal(storeType, StoreType(mapped));
    }

    // ── Defaults, keys, indexes ─────────────────────────────────────────────────

    [Fact]
    public void Ddl071_Defaults_MatchTheModel()
    {
        var ddl = ReadDdl071();

        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoItems_ItemId\]\s+DEFAULT\s*\(NEWSEQUENTIALID\(\)\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoItems_IsActivityAsset\]\s+DEFAULT\s*\(0\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoItems_IsHousing\]\s+DEFAULT\s*\(0\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoItems_UpdatedAt\]\s+DEFAULT\s*\(GETUTCDATE\(\)\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoIncomes_IncomeId\]\s+DEFAULT\s*\(NEWSEQUENTIALID\(\)\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoIncomes_Currency\]\s+DEFAULT\s*\(N'ARS'\)", ddl);

        var item = TypeOf<LegajoItem>();
        Assert.Equal(nameof(LegajoItem.ItemId), Assert.Single(item.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(false, item.FindProperty(nameof(LegajoItem.IsHousing))!.GetDefaultValue());
        Assert.True(item.FindProperty(nameof(LegajoItem.UpdatedAt))!.IsConcurrencyToken);
        Assert.Equal("ARS", TypeOf<LegajoIncome>().FindProperty(nameof(LegajoIncome.Currency))!.GetDefaultValue());
    }

    [Theory]
    [InlineData(typeof(LegajoItem), "UX_LegajoItems_Key", true, new[] { "TenantId", "PersonLegajoId", "ItemKey" })]
    [InlineData(typeof(LegajoItem), "IX_LegajoItems_TenantId_PersonLegajoId", false, new[] { "TenantId", "PersonLegajoId" })]
    [InlineData(typeof(LegajoIncome), "UX_LegajoIncomes_Key", true,
                new[] { "TenantId", "PersonLegajoId", "Source", "Concept", "PeriodFrom", "PeriodTo" })]
    [InlineData(typeof(LegajoIncome), "IX_LegajoIncomes_TenantId_PersonLegajoId", false, new[] { "TenantId", "PersonLegajoId" })]
    public void Index_IsModelledAndCreatedByTheDdl(Type clrType, string name, bool unique, string[] columns)
    {
        var index = Assert.Single(TypeOfClr(clrType).GetIndexes(), i => i.GetDatabaseName() == name);
        Assert.Equal(unique, index.IsUnique);
        Assert.Equal(columns, index.Properties.Select(p => p.GetColumnName()));
        Assert.Null(index.GetFilter());

        var kind = unique ? @"UNIQUE\s+NONCLUSTERED" : "NONCLUSTERED";
        var ddlColumns = string.Join(@",\s*", columns.Select(c => @"\[" + c + @"\]\s+ASC"));
        Assert.Matches(@"CREATE\s+" + kind + @"\s+INDEX\s+\[" + name + @"\]\s+ON\s+\[dbo\]\.\[\w+\]\s*\(" + ddlColumns + @"\)",
                       ReadDdl071());
    }

    // ── Foreign keys: in the DDL, deliberately NOT in the model ─────────────────

    [Theory]
    [InlineData("FK_LegajoItems_PersonLegajos")]
    [InlineData("FK_LegajoItems_MarketValueDocument")]
    [InlineData("FK_LegajoItems_FiscalValueDocument")]
    [InlineData("FK_LegajoIncomes_PersonLegajos")]
    [InlineData("FK_LegajoIncomes_Documents")]
    [InlineData("FK_LegajoIncomes_LegajoExtractions")]
    public void Ddl071_DeclaresTheForeignKey(string name)
    {
        Assert.Matches(@"CONSTRAINT\s+\[" + name + @"\]\s+FOREIGN\s+KEY", ReadDdl071());
    }

    [Theory]
    [InlineData(typeof(LegajoItem))]
    [InlineData(typeof(LegajoIncome))]
    public void NewRelationships_AreNotModelled(Type clrType)
    {
        Assert.Empty(TypeOfClr(clrType).GetForeignKeys());
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl071_IsIdempotentAndSetsTheSessionOptions()
    {
        var ddl = ReadDdl071();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        Assert.Equal(2, Regex.Matches(ddl, @"CREATE\s+TABLE").Count);
        Assert.Equal(2, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.objects").Count);
        Assert.Equal(4, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.indexes").Count);
        Assert.Equal(2, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns").Count);
        Assert.DoesNotContain("BEGIN TRANSACTION", ddl);
    }

    // ── Helpers (as in LegajoDocumentsMappingTests) ─────────────────────────────

    private static IEntityType TypeOf<TEntity>() => TypeOfClr(typeof(TEntity));

    private static IEntityType TypeOfClr(Type clrType)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(clrType)!;
    }

    private static string StoreType(IProperty property)
        => property.GetColumnType().Replace(" ", string.Empty).ToLowerInvariant();

    private static string ReadDdl071Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl071FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl071FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl071()
    {
        var text = Regex.Replace(ReadDdl071Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
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
