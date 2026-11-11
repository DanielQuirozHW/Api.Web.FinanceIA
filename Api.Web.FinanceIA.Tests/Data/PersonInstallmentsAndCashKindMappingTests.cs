// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 079 mapping to docs/database/079_person_installments_and_cash_kind.sql, following
/// AnalysisParticipantAgroMappingTests: the declared installments column against the model (name, store type,
/// nullability) and its CHECK, the widened CK_LegajoItems_Kind against <see cref="LegajoItemKinds.Known"/>, and
/// the script shape (idempotent, NOEXEC restored at the end, the kind CHECK swapped inside one transaction).
/// </summary>
public sealed class PersonInstallmentsAndCashKindMappingTests
{
    private const string Ddl079FileName = "079_person_installments_and_cash_kind.sql";

    /// <summary>The columns DDL 079 adds; PersonAnalysisMappingTests leaves these out of its 072 table.</summary>
    internal static readonly IReadOnlySet<string> Ddl079Columns = new HashSet<string>(StringComparer.Ordinal)
    {
        "ExistingInstallmentsAmount"
    };

    // ── Columns vs model ────────────────────────────────────────────────────────

    [Fact]
    public void Ddl079_AddsExactlyTheMappedColumn_Nullable()
    {
        var declared = Regex.Matches(
                ReadDdl079(),
                @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisParticipants\]\s+ADD\s+\[(?<name>\w+)\]\s+(?<type>[A-Z]+(?:\(\d+(?:,\s*\d+)?\))?)\s+(?<null>NOT\s+NULL|NULL);")
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.Replace(" ", string.Empty).ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var mapped = TypeOf<AnalysisParticipant>().GetProperties()
            .Where(p => Ddl079Columns.Contains(p.GetColumnName()))
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Ddl079Columns.Count, declared.Count);
        Assert.All(declared, c => Assert.True(c.IsNullable, c.Name + " must be NULLABLE: DDL first is safe only so."));
        Assert.Equal(declared, mapped);
        Assert.Equal(("ExistingInstallmentsAmount", "decimal(18,2)", true), Assert.Single(mapped));
    }

    [Fact]
    public void Ddl079_DeclaresNoDefault()
    {
        // NULL means "estimate" — every row written before 079; a default would turn it into a declaration.
        Assert.DoesNotMatch(@"\bDEFAULT\b", ReadDdl079());
    }

    [Fact]
    public void NewParticipant_LeavesTheDeclaredInstallmentsNull()
    {
        Assert.Null(new AnalysisParticipant().ExistingInstallmentsAmount);
    }

    [Fact]
    public void Ddl079_InstallmentsCheck_AdmitsNullAndZero_RejectsNegatives()
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisParticipants\]\s+WITH\s+CHECK\s+ADD\s+CONSTRAINT\s+\[CK_AnalysisParticipants_ExistingInstallmentsAmount\]\s+" +
            @"CHECK\s*\(\[ExistingInstallmentsAmount\]\s+IS\s+NULL\s+OR\s+\[ExistingInstallmentsAmount\]\s*>=\s*0\);",
            ReadDdl079());
    }

    // ── The item kind CHECK vs constants ────────────────────────────────────────

    [Fact]
    public void Ddl079_ItemKindCheck_AdmitsExactlyTheConstants()
    {
        var match = Regex.Match(
            ReadDdl079(),
            @"ADD\s+CONSTRAINT\s+\[CK_LegajoItems_Kind\]\s+CHECK\s*\(\[Kind\]\s+IN\s*\((?<list>[^)]*)\)\)");
        Assert.True(match.Success);

        var declared = Regex.Matches(match.Groups["list"].Value, @"N'(?<v>[^']+)'").Select(m => m.Groups["v"].Value).ToList();

        // In LegajoItemKinds order: the inventory groups follow it.
        Assert.Equal(LegajoItemKinds.Known, declared);
        Assert.Contains(LegajoItemKinds.Efectivo, declared);
    }

    [Fact]
    public void Ddl079_SwapsTheKindCheck_InOneTransaction_OnlyWhenItLacksEfectivo()
    {
        var ddl = ReadDdl079();

        Assert.Matches(
            @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints\s+WHERE\s+name\s*=\s*N'CK_LegajoItems_Kind'\s+" +
            @"AND\s+parent_object_id\s*=\s*OBJECT_ID\(N'\[dbo\]\.\[LegajoItems\]'\)\s+AND\s+definition\s+LIKE\s+N'%Efectivo%'\s*\)",
            ddl);
        Assert.Matches(
            @"SET\s+XACT_ABORT\s+ON;\s+BEGIN\s+TRANSACTION;[\s\S]*DROP\s+CONSTRAINT\s+\[CK_LegajoItems_Kind\];[\s\S]*" +
            @"WITH\s+CHECK\s+ADD\s+CONSTRAINT\s+\[CK_LegajoItems_Kind\][\s\S]*COMMIT\s+TRANSACTION;",
            ddl);
        // The only DROP is the kind CHECK's: nothing else is removed.
        Assert.Single(Regex.Matches(ddl, @"\bDROP\b"));
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl079_IsIdempotent_SetsTheSessionOptions_AndRestoresExecution()
    {
        var ddl = ReadDdl079();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        // The guarded ADD only: section 0's "ELSE IF NOT EXISTS (... sys.columns ...)" is the 078 precondition.
        Assert.Single(Regex.Matches(ddl, @"(?m)^IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns"));
        Assert.Equal(2, Regex.Matches(ddl, @"(?m)^IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints").Count);

        // Each precondition stops every later batch, and the script turns NOEXEC back off at the end.
        Assert.Equal(2, Regex.Matches(ddl, @"RAISERROR[^;]*;\s*(?:--[^\r\n]*\s*)*SET\s+NOEXEC\s+ON;").Count);
        Assert.Matches(@"SET\s+NOEXEC\s+OFF;\s*GO\s*$", ddl.TrimEnd());
    }

    [Fact]
    public void Ddl079_StopsWhen078HasNotRun()
    {
        Assert.Matches(
            @"ELSE\s+IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns\s+WHERE\s+object_id\s*=\s*OBJECT_ID\(N'\[dbo\]\.\[AnalysisParticipants\]'\)\s+" +
            @"AND\s+name\s*=\s*N'CuotasPorAnio'\s*\)\s*BEGIN\s+RAISERROR",
            ReadDdl079());
    }

    // ── Helpers (as in AnalysisParticipantAgroMappingTests) ─────────────────────

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

    private static string ReadDdl079Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl079FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl079FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl079()
    {
        var text = Regex.Replace(ReadDdl079Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }
}
