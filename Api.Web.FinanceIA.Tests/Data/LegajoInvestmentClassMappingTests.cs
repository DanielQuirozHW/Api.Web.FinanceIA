// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 080 mapping to docs/database/080_legajo_items_investment_class.sql, following
/// PersonInstallmentsAndCashKindMappingTests: the class column against the model (name, store type, nullability, no
/// default), its CHECK against <see cref="LegajoInvestmentClasses.Known"/> and Kind = Inversion, and the script shape
/// (idempotent, the 079 precondition, the CHECK in its own batch, NOEXEC restored, the rollback order).
/// </summary>
public sealed class LegajoInvestmentClassMappingTests
{
    private const string Ddl080FileName = "080_legajo_items_investment_class.sql";

    /// <summary>The columns DDL 080 adds; LegajoReconciliationMappingTests leaves these out of its 071 table.</summary>
    internal static readonly IReadOnlySet<string> Ddl080Columns = new HashSet<string>(StringComparer.Ordinal)
    {
        "InvestmentClass"
    };

    // ── Column vs model ─────────────────────────────────────────────────────────

    [Fact]
    public void Ddl080_AddsExactlyTheMappedColumn_Nullable()
    {
        var declared = Regex.Matches(
                ReadDdl080(),
                @"ALTER\s+TABLE\s+\[dbo\]\.\[LegajoItems\]\s+ADD\s+\[(?<name>\w+)\]\s+(?<type>[A-Z]+(?:\(\d+\))?)\s+(?<null>NOT\s+NULL|NULL);")
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .ToList();

        var mapped = TypeOf<LegajoItem>().GetProperties()
            .Where(p => Ddl080Columns.Contains(p.GetColumnName()))
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .ToList();

        Assert.Equal(declared, mapped);
        Assert.Equal(("InvestmentClass", "nvarchar(30)", true), Assert.Single(mapped));
    }

    [Fact]
    public void Ddl080_DeclaresNoDefault()
    {
        // NULL means "not classified" — every row written before 080; a default would classify them.
        Assert.DoesNotMatch(@"\bDEFAULT\b", ReadDdl080());
    }

    [Fact]
    public void NewItem_IsUnclassified()
    {
        Assert.Null(new LegajoItem().InvestmentClass);
    }

    [Fact]
    public void TheLongestClass_FitsTheColumn()
    {
        Assert.All(LegajoInvestmentClasses.Known, c => Assert.True(c.Length <= 30, c));
    }

    // ── The CHECK vs constants ──────────────────────────────────────────────────

    [Fact]
    public void Ddl080_ClassCheck_AdmitsNull_OrExactlyTheConstants_OnAnInversionRow()
    {
        var match = Regex.Match(
            ReadDdl080(),
            @"ALTER\s+TABLE\s+\[dbo\]\.\[LegajoItems\]\s+WITH\s+CHECK\s+ADD\s+CONSTRAINT\s+\[CK_LegajoItems_InvestmentClass\]\s+" +
            @"CHECK\s*\(\[InvestmentClass\]\s+IS\s+NULL\s+OR\s*\(\[Kind\]\s*=\s*N'(?<kind>\w+)'\s+AND\s+\[InvestmentClass\]\s+IN\s*\((?<list>[^)]*)\)\)\);");
        Assert.True(match.Success);

        Assert.Equal(LegajoItemKinds.Inversion, match.Groups["kind"].Value);

        var declared = Regex.Matches(match.Groups["list"].Value, @"N'(?<v>[^']+)'").Select(m => m.Groups["v"].Value).ToList();
        Assert.Equal(LegajoInvestmentClasses.Known, declared);
    }

    [Fact]
    public void Ddl080_AddsTheCheck_InItsOwnBatch_AfterTheColumn()
    {
        var batches = Regex.Split(ReadDdl080(), @"(?m)^\s*GO\s*$");

        var column = Array.FindIndex(batches, b => Regex.IsMatch(b, @"ADD\s+\[InvestmentClass\]"));
        var check  = Array.FindIndex(batches, b => b.Contains("ADD CONSTRAINT [CK_LegajoItems_InvestmentClass]", StringComparison.Ordinal));

        Assert.True(column >= 0);
        Assert.True(check > column, "The CHECK must be compiled in a later batch than the column it references.");
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl080_IsIdempotent_SetsTheSessionOptions_AndRestoresExecution()
    {
        var ddl = ReadDdl080();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        Assert.Single(Regex.Matches(ddl, @"(?m)^IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns"));
        Assert.Single(Regex.Matches(ddl, @"(?m)^IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints"));

        // A PRINT in both branches of each guard.
        Assert.Equal(2, Regex.Matches(ddl, @"PRINT\s+'[^']*added to \[dbo\]\.\[LegajoItems\]\.'").Count);
        Assert.Equal(2, Regex.Matches(ddl, @"PRINT\s+'[^']*already exists on \[dbo\]\.\[LegajoItems\] — skipped\.'").Count);

        // Each precondition stops every later batch, and the script turns NOEXEC back off at the end.
        Assert.Equal(2, Regex.Matches(ddl, @"RAISERROR[^;]*;\s*(?:--[^\r\n]*\s*)*SET\s+NOEXEC\s+ON;").Count);
        Assert.Matches(@"SET\s+NOEXEC\s+OFF;\s*GO\s*$", ddl.TrimEnd());

        // Nothing is dropped and nothing runs in a transaction: one column, one CHECK.
        Assert.DoesNotMatch(@"\bDROP\b", ddl);
        Assert.DoesNotContain("BEGIN TRANSACTION", ddl);
    }

    [Fact]
    public void Ddl080_StopsWhen079HasNotRun()
    {
        Assert.Matches(
            @"ELSE\s+IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints\s+WHERE\s+name\s*=\s*N'CK_LegajoItems_Kind'\s+" +
            @"AND\s+parent_object_id\s*=\s*OBJECT_ID\(N'\[dbo\]\.\[LegajoItems\]'\)\s+AND\s+definition\s+LIKE\s+N'%Efectivo%'\s*\)\s*BEGIN\s+RAISERROR",
            ReadDdl080());
    }

    [Fact]
    public void Ddl080_PostCheck_ProvesTypeNullability_AndATrustedEnabledCheck()
    {
        var raw = ReadDdl080Raw();
        var post = raw[raw.IndexOf("── Post-check", StringComparison.Ordinal)..raw.IndexOf("── Rollback", StringComparison.Ordinal)];

        Assert.Contains("c.max_length, c.is_nullable", post);
        Assert.Contains("N'InvestmentClass'", post);
        Assert.Contains("is_disabled, is_not_trusted", post);
        Assert.Contains("N'CK_LegajoItems_InvestmentClass'", post);
    }

    [Fact]
    public void Ddl080_Rollback_ExportsFirst_ThenDropsTheCheck_ThenTheColumn()
    {
        var raw = ReadDdl080Raw();
        var rollback = raw[raw.IndexOf("── Rollback", StringComparison.Ordinal)..];

        var export      = rollback.IndexOf("SELECT [ItemId], [PersonLegajoId], [InvestmentClass]", StringComparison.Ordinal);
        var dropCheck   = rollback.IndexOf("DROP CONSTRAINT [CK_LegajoItems_InvestmentClass]", StringComparison.Ordinal);
        var dropColumn  = rollback.IndexOf("DROP COLUMN [InvestmentClass]", StringComparison.Ordinal);

        Assert.True(export >= 0 && dropCheck > export && dropColumn > dropCheck,
            "The rollback exports the classes, then drops the CHECK, then the column.");
    }

    // ── Helpers (as in PersonInstallmentsAndCashKindMappingTests) ───────────────

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

    private static string ReadDdl080Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl080FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl080FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl080()
    {
        var text = Regex.Replace(ReadDdl080Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }
}
