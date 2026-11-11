// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 077 mapping to docs/database/077_analysis_participants_fiador.sql, following
/// PersonAnalysisMappingTests: the five fiador columns against the model (name, store type,
/// nullability), the two CHECKs, the FK declared in the script but not modelled, and the script shape
/// (idempotent, no transaction, NOEXEC restored at the end).
/// </summary>
public sealed class AnalysisParticipantFiadorMappingTests
{
    private const string Ddl077FileName = "077_analysis_participants_fiador.sql";

    /// <summary>The columns DDL 077 adds; PersonAnalysisMappingTests leaves these out of its 072 table.</summary>
    internal static readonly IReadOnlySet<string> Ddl077Columns = new HashSet<string>(StringComparer.Ordinal)
    {
        "FiadorId", "GuaranteeType", "SpouseConsent", "HousingInopposable", "GuaranteesGivenAmount"
    };

    // ── Columns vs model ────────────────────────────────────────────────────────

    [Fact]
    public void Ddl077_AddsExactlyTheMappedFiadorColumns_AllNullable()
    {
        var declared = Regex.Matches(
                ReadDdl077(),
                @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisParticipants\]\s+ADD\s+\[(?<name>\w+)\]\s+(?<type>[A-Z]+(?:\(\d+(?:,\s*\d+)?\))?)\s+(?<null>NOT\s+NULL|NULL);")
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.Replace(" ", string.Empty).ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var mapped = TypeOf<AnalysisParticipant>().GetProperties()
            .Where(p => Ddl077Columns.Contains(p.GetColumnName()))
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Ddl077Columns.Count, declared.Count);
        Assert.All(declared, c => Assert.True(c.IsNullable, c.Name + " must be NULLABLE: DDL first is safe only so."));
        Assert.Equal(declared, mapped);
    }

    [Fact]
    public void Ddl077_DeclaresNoDefault()
    {
        // A default would make the old INSERT write a value the code of this release never chose.
        Assert.DoesNotMatch(@"\bDEFAULT\b", ReadDdl077());
    }

    [Theory]
    [InlineData(nameof(AnalysisParticipant.FiadorId), "uniqueidentifier")]
    [InlineData(nameof(AnalysisParticipant.GuaranteeType), "nvarchar(30)")]
    [InlineData(nameof(AnalysisParticipant.SpouseConsent), "bit")]
    [InlineData(nameof(AnalysisParticipant.HousingInopposable), "bit")]
    [InlineData(nameof(AnalysisParticipant.GuaranteesGivenAmount), "decimal(18,2)")]
    public void FiadorColumn_IsMappedNullable(string property, string storeType)
    {
        var mapped = TypeOf<AnalysisParticipant>().FindProperty(property)!;

        Assert.Equal(property, mapped.GetColumnName());
        Assert.True(mapped.IsNullable);
        Assert.Equal(storeType, StoreType(mapped));
    }

    [Fact]
    public void NewParticipant_LeavesEveryFiadorColumnNull()
    {
        // Nothing writes these in this release: the persona analysis keeps inserting Solicitante rows
        // with FiadorId NULL, which is what CK_AnalysisParticipants_FiadorId_Role requires.
        var participant = new AnalysisParticipant();

        Assert.Equal(ParticipantRoles.Solicitante, participant.Role);
        Assert.Null(participant.FiadorId);
        Assert.Null(participant.GuaranteeType);
        Assert.Null(participant.SpouseConsent);
        Assert.Null(participant.HousingInopposable);
        Assert.Null(participant.GuaranteesGivenAmount);
    }

    // ── CHECKs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl077_FiadorIdRoleCheck_IsOneImplicationPerRole()
    {
        Assert.Matches(
            @"ALTER\s+TABLE\s+\[dbo\]\.\[AnalysisParticipants\]\s+WITH\s+CHECK\s+ADD\s+CONSTRAINT\s+\[CK_AnalysisParticipants_FiadorId_Role\]\s+" +
            @"CHECK\s*\(\(\[Role\]\s*<>\s*N'" + ParticipantRoles.Fiador + @"'\s+OR\s+\[FiadorId\]\s+IS\s+NOT\s+NULL\)\s+AND\s+" +
            @"\(\[Role\]\s*<>\s*N'" + ParticipantRoles.Solicitante + @"'\s+OR\s+\[FiadorId\]\s+IS\s+NULL\)\);",
            ReadDdl077());
    }

    [Fact]
    public void Ddl077_FiadorIdRoleCheck_CoversEveryKnownRole()
    {
        // CK_AnalysisParticipants_Role (072) admits exactly ParticipantRoles.Known; a role added there
        // without a rule here would be a row whose FiadorId nothing constrains.
        var roles = Regex.Matches(
                Regex.Match(ReadDdl077(), @"\[CK_AnalysisParticipants_FiadorId_Role\]\s+CHECK\s*\((?<expr>[^;]*)\);").Groups["expr"].Value,
                @"\[Role\]\s*<>\s*N'(?<role>[^']*)'")
            .Select(m => m.Groups["role"].Value)
            .OrderBy(r => r, StringComparer.Ordinal);

        Assert.Equal(ParticipantRoles.Known.OrderBy(r => r, StringComparer.Ordinal), roles);
    }

    [Fact]
    public void Ddl077_GuaranteesGivenAmount_IsNeverNegative()
    {
        Assert.Matches(
            @"ADD\s+CONSTRAINT\s+\[CK_AnalysisParticipants_GuaranteesGivenAmount\]\s+" +
            @"CHECK\s*\(\[GuaranteesGivenAmount\]\s+IS\s+NULL\s+OR\s+\[GuaranteesGivenAmount\]\s*>=\s*0\);",
            ReadDdl077());
    }

    [Fact]
    public void Ddl077_DoesNotConstrainGuaranteeTypeValues()
    {
        // Its vocabulary belongs to the phase that writes it (validated in code there, like Origen).
        Assert.DoesNotMatch(@"CHECK\s*\(\s*\[GuaranteeType\]", ReadDdl077());
    }

    // ── FK ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl077_DeclaresTheFiadorForeignKey_NoAction()
    {
        var ddl = ReadDdl077();

        Assert.Matches(
            @"ADD\s+CONSTRAINT\s+\[FK_AnalysisParticipants_Fiadores\]\s+FOREIGN\s+KEY\s*\(\[FiadorId\]\)\s+REFERENCES\s+\[dbo\]\.\[Fiadores\]\s*\(\[FiadorId\]\);",
            ddl);
        Assert.DoesNotMatch(@"ON\s+DELETE", ddl);
    }

    [Fact]
    public void ParticipantRelationships_AreStillNotModelled()
    {
        Assert.Empty(TypeOf<AnalysisParticipant>().GetForeignKeys());
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl077_IsIdempotent_SetsTheSessionOptions_AndRestoresExecution()
    {
        var ddl = ReadDdl077();

        Assert.StartsWith("SET ANSI_NULLS ON;", ddl.TrimStart());
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", ddl);
        Assert.Equal(5, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.columns").Count);
        Assert.Single(Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.foreign_keys"));
        Assert.Equal(2, Regex.Matches(ddl, @"IF\s+NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+sys\.check_constraints").Count);
        Assert.DoesNotContain("BEGIN TRANSACTION", ddl);
        Assert.DoesNotMatch(@"\bDROP\b", ddl);

        // Each precondition stops every later batch, and the script turns NOEXEC back off at the end.
        Assert.Equal(2, Regex.Matches(ddl, @"RAISERROR[^;]*;\s*SET\s+NOEXEC\s+ON;").Count);
        Assert.Matches(@"SET\s+NOEXEC\s+OFF;\s*GO\s*$", ddl.TrimEnd());
    }

    [Fact]
    public void Ddl077_StopsWhenAParticipantIsAlreadyAFiador()
    {
        Assert.Matches(
            @"IF\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+\[dbo\]\.\[AnalysisParticipants\]\s+WHERE\s+\[Role\]\s*<>\s*N'" +
            ParticipantRoles.Solicitante + @"'\s*\)\s*BEGIN\s+RAISERROR",
            ReadDdl077());
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

    private static string StoreType(IProperty property)
        => property.GetColumnType().Replace(" ", string.Empty).ToLowerInvariant();

    private static string ReadDdl077Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl077FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl077FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl077()
    {
        var text = Regex.Replace(ReadDdl077Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }
}
