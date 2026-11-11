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
/// Locks the DDL 078 mapping to docs/database/078_analysis_participants_agro_terms.sql, following
/// AnalysisParticipantFiadorMappingTests: the two agro term columns against the model (name, store type,
/// nullability), the two CHECKs (the closed set of cuotas a year, nothing on a Fiador row), and the script
/// shape (idempotent, no transaction, NOEXEC restored at the end).
/// </summary>
public sealed class AnalysisParticipantAgroMappingTests
{
    private const string Ddl078FileName = "078_analysis_participants_agro_terms.sql";

    /// <summary>The columns DDL 078 adds; PersonAnalysisMappingTests leaves these out of its 072 table.</summary>
    internal static readonly IReadOnlySet<string> Ddl078Columns = new HashSet<string>(StringComparer.Ordinal)
    {
        "CuotasPorAnio", "AgroModeOverride"
    };

    // ── Columns vs model ────────────────────────────────────────────────────────

    [Fact]
    public void Ddl078_AddsExactlyTheMappedAgroColumns_AllNullable()
    {
        var declared = Regex.Matches(
                ReadDdl078(),
                @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisParticipants\]\s+ADD\s+\[(?<name>\w+)\]\s+(?<type>[A-Z]+(?:\(\d+(?:,\s*\d+)?\))?)\s+(?<null>NOT\s+NULL|NULL);")
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.Replace(" ", string.Empty).ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var mapped = TypeOf<AnalysisParticipant>().GetProperties()
            .Where(p => Ddl078Columns.Contains(p.GetColumnName()))
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Ddl078Columns.Count, declared.Count);
        Assert.All(declared, c => Assert.True(c.IsNullable, c.Name + " must be NULLABLE: DDL first is safe only so."));
        Assert.Equal(declared, mapped);
    }

    [Fact]
    public void Ddl078_DeclaresNoDefault()
    {
        // NULL is the 0.6 behaviour (a monthly cuota, the mode detected); a default would rewrite old rows' meaning.
        Assert.DoesNotMatch(@"\bDEFAULT\b", ReadDdl078());
    }

    [Theory]
    [InlineData(nameof(AnalysisParticipant.CuotasPorAnio), "tinyint")]
    [InlineData(nameof(AnalysisParticipant.AgroModeOverride), "bit")]
    public void AgroColumn_IsMappedNullable(string property, string storeType)
    {
        var mapped = TypeOf<AnalysisParticipant>().FindProperty(property)!;

        Assert.Equal(property, mapped.GetColumnName());
        Assert.True(mapped.IsNullable);
        Assert.Equal(storeType, StoreType(mapped));
    }

    [Fact]
    public void NewParticipant_LeavesBothAgroColumnsNull()
    {
        var participant = new AnalysisParticipant();

        Assert.Null(participant.CuotasPorAnio);
        Assert.Null(participant.AgroModeOverride);
    }

    // ── CHECKs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl078_CuotasPorAnioCheck_IsTheAllowedSet()
    {
        var match = Regex.Match(
            ReadDdl078(),
            @"ADD\s+CONSTRAINT\s+\[CK_AnalysisParticipants_CuotasPorAnio\]\s+" +
            @"CHECK\s*\(\[CuotasPorAnio\]\s+IS\s+NULL\s+OR\s+\[CuotasPorAnio\]\s+IN\s*\((?<set>[\d,\s]+)\)\);");

        Assert.True(match.Success);
        var declared = match.Groups["set"].Value.Split(',').Select(v => int.Parse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PersonEvaluationDefaults.AllowedCuotasPorAnio, declared);
    }

    [Fact]
    public void Ddl078_AgroTermsRoleCheck_KeepsAFiadorRowFree()
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisParticipants\]\s+WITH\s+CHECK\s+ADD\s+CONSTRAINT\s+\[CK_AnalysisParticipants_AgroTerms_Role\]\s+" +
            @"CHECK\s*\(\[Role\]\s*<>\s*N'" + ParticipantRoles.Fiador + @"'\s+OR\s+\(\[CuotasPorAnio\]\s+IS\s+NULL\s+AND\s+\[AgroModeOverride\]\s+IS\s+NULL\)\);",
            ReadDdl078());
    }

    [Fact]
    public void ParticipantRelationships_AreStillNotModelled()
    {
        Assert.Empty(TypeOf<AnalysisParticipant>().GetForeignKeys());
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl078_IsIdempotent_SetsTheSessionOptions_AndRestoresExecution()
    {
        var ddl = ReadDdl078();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        // The guarded ADDs only: section 0's "ELSE IF NOT EXISTS (... sys.columns ...)" is the 077 precondition.
        Assert.Equal(2, Regex.Matches(ddl, @"(?m)^IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns").Count);
        Assert.Equal(2, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints").Count);
        Assert.DoesNotContain("BEGIN TRANSACTION", ddl);
        Assert.DoesNotMatch(@"\bDROP\b", ddl);

        // Each precondition stops every later batch, and the script turns NOEXEC back off at the end.
        Assert.Equal(2, Regex.Matches(ddl, @"RAISERROR[^;]*;\s*(?:--[^\r\n]*\s*)*SET\s+NOEXEC\s+ON;").Count);
        Assert.Matches(@"SET\s+NOEXEC\s+OFF;\s*GO\s*$", ddl.TrimEnd());
    }

    [Fact]
    public void Ddl078_StopsWhen077HasNotRun()
    {
        Assert.Matches(
            @"ELSE\s+IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns\s+WHERE\s+object_id\s*=\s*OBJECT_ID\(N'\[dbo\]\.\[AnalysisParticipants\]'\)\s+" +
            @"AND\s+name\s*=\s*N'FiadorId'\s*\)\s*BEGIN\s+RAISERROR",
            ReadDdl078());
    }

    // ── Helpers (as in AnalysisParticipantFiadorMappingTests) ───────────────────

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

    private static string ReadDdl078Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl078FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl078FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl078()
    {
        var text = Regex.Replace(ReadDdl078Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }
}
