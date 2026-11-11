// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Reflection;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Locks the DDL 070 mapping to docs/database/070_legajo_documents_and_extractions.sql, following
/// PersonLegajoMappingTests: every CHECK list against its C# constants class, every added column
/// against the model (name, store type, nullability), the defaults, the index names/filters, and the
/// deliberate absence of modelled FKs. EF maps what the model says whether or not the DBA created it,
/// so a drift here is an "Invalid column name" in Azure, not a compile error.
/// </summary>
public sealed class LegajoDocumentsMappingTests
{
    private const string Ddl070FileName = "070_legajo_documents_and_extractions.sql";

    // ── CHECK lists vs constants ────────────────────────────────────────────────

    [Fact]
    public void Ddl070_DocumentTypeCheck_AdmitsExactlyDocumentTypesKnown()
    {
        var check = CheckConstraint(ReadDdl070(), "CK_Documents_DocumentType");

        Assert.Equal(Sorted(DocumentTypes.Known), Sorted(check.Values));
    }

    [Fact]
    public void DocumentTypes_Known_IsTheLegacyFourPlusTheTenLegajoTypes()
    {
        Assert.Equal(["Balance", "Complemento", "Certificado", "Otro"], DocumentTypes.LegacyTypes);
        Assert.Equal(14, DocumentTypes.Known.Count);
        Assert.Contains(DocumentTypes.Otro, DocumentTypes.LegajoTypes);
        Assert.DoesNotContain(DocumentTypes.Balance, DocumentTypes.LegajoTypes);
    }

    [Fact]
    public void Ddl070_PdfTypeCheck_AdmitsNullAndExactlyPdfTypesKnown()
    {
        var check = CheckConstraint(ReadDdl070(), "CK_Documents_PdfType");

        Assert.Equal(Sorted(PdfTypes.Known), Sorted(check.Values));
        Assert.Matches(@"\[PdfType\]\s+IS\s+NULL", check.Expression);
    }

    [Fact]
    public void Ddl070_EngagementTypeCheck_AdmitsNullAndExactlyEngagementTypesKnown()
    {
        var check = CheckConstraint(ReadDdl070(), "CK_Documents_EngagementType");

        Assert.Equal(Sorted(EngagementTypes.Known), Sorted(check.Values));
        Assert.Matches(@"\[EngagementType\]\s+IS\s+NULL", check.Expression);
    }

    [Fact]
    public void Ddl070_LegajoStatusCheck_AdmitsNullAndExactlyLegajoStatusesKnown()
    {
        var check = CheckConstraint(ReadDdl070(), "CK_Documents_LegajoStatus");

        Assert.Equal(Sorted(LegajoStatuses.Known), Sorted(check.Values));
        Assert.Matches(@"\[LegajoStatus\]\s+IS\s+NULL", check.Expression);
    }

    [Theory]
    [InlineData("CK_LegajoExtractions_Kind")]
    [InlineData("CK_LegajoExtractions_ReadMode")]
    [InlineData("CK_LegajoExtractions_Status")]
    public void Ddl070_LegajoExtractionChecks_MatchTheirConstants(string constraint)
    {
        IReadOnlyList<string> expected = constraint switch
        {
            "CK_LegajoExtractions_Kind"     => LegajoExtractionKinds.Known,
            "CK_LegajoExtractions_ReadMode" => LegajoReadModes.Known,
            _                               => LegajoExtractionStatuses.Known
        };

        Assert.Equal(Sorted(expected), Sorted(CheckConstraint(ReadDdl070(), constraint).Values));
    }

    [Fact]
    public void Ddl070_OwnerCheck_TiesALegajoStatusToALegajo()
    {
        Assert.Matches(
            @"CONSTRAINT\s+\[CK_Documents_LegajoStatus_Owner\]\s+CHECK\s*\(\s*\[LegajoStatus\]\s+IS\s+NULL\s+OR\s+\[PersonLegajoId\]\s+IS\s+NOT\s+NULL\s*\)",
            ReadDdl070());
    }

    [Fact]
    public void Ddl070_CorrectionReasonCheck_RequiresFiveNonBlankCharacters()
    {
        Assert.Matches(
            @"CONSTRAINT\s+\[CK_LegajoCorrections_Reason\]\s+CHECK\s*\(\s*LEN\(LTRIM\(RTRIM\(\[Reason\]\)\)\)\s*>=\s*5\s*\)",
            ReadDdl070());
    }

    // ── CK_ApiCallLogs_Operation: a superset of what the code writes ────────────

    [Fact]
    public void Ddl070_OperationCheck_AdmitsNullAndEveryOperationTheCodeWrites()
    {
        var check = CheckConstraint(ReadDdl070(), "CK_ApiCallLogs_Operation");

        // SUBSET, not equality: the out-of-band CHECK also admits values another application writes.
        Assert.Subset(check.Values, ApiCallLogOperations.Known.ToHashSet(StringComparer.Ordinal));
        Assert.Matches(@"\[Operation\]\s+IS\s+NULL", check.Expression);
    }

    [Fact]
    public void Ddl070_OperationCheck_KeepsTheLegacyOutOfBandValues()
    {
        var check = CheckConstraint(ReadDdl070(), "CK_ApiCallLogs_Operation");

        // Written by no code in this repo but admitted by the out-of-band CHECK on Financia-db:
        // dropping them would reject another writer's rows.
        Assert.Contains("DynamicsSync", check.Values);
        Assert.Contains("BcraLookup", check.Values);
        Assert.DoesNotContain("DynamicsSync", ApiCallLogOperations.Known);
        Assert.DoesNotContain("BcraLookup", ApiCallLogOperations.Known);
    }

    [Fact]
    public void ApiCallLogOperations_Known_ListsEveryConstant()
    {
        var constants = typeof(ApiCallLogOperations)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(Sorted(constants), Sorted(ApiCallLogOperations.Known));
        Assert.Equal(ApiCallLogOperations.Known.Count, ApiCallLogOperations.Known.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ApiCallLogOperations.Known, v => Assert.InRange(v.Length, 1, 30)); // nvarchar(30)
    }

    [Fact]
    public void LegajoMessagesClient_Operations_AreTheApiCallLogOperations()
    {
        Assert.Equal(ApiCallLogOperations.LegajoClassification, LegajoMessagesClient.ClassificationOperation);
        Assert.Equal(ApiCallLogOperations.LegajoExtraction, LegajoMessagesClient.ExtractionOperation);
    }

    [Fact]
    public void ApiCallLogs_NoCallerPassesAnOperationLiteral()
    {
        // A literal would bypass ApiCallLogOperations.Known, and with it the subset lock above: the
        // CHECK would reject its rows and ApiCallLogService would swallow the failure.
        var separator = Path.DirectorySeparatorChar;
        var offenders = Directory
            .EnumerateFiles(ProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}") && !path.Contains($"{separator}obj{separator}"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\b(?:operation\s*:|Operation\s*=)\s*""")
                .Select(m => $"{Path.GetFileName(path)}: {m.Value}"))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── The drop-and-recreate loop: column-bound checks only ───────────────────

    [Theory]
    [InlineData("Documents", "DocumentType", "@DropDocumentType", "CK_Documents_DocumentType")]
    [InlineData("Documents", "PdfType", "@DropPdfType", "CK_Documents_PdfType")]
    [InlineData("ApiCallLogs", "Operation", "@DropOperation", "CK_ApiCallLogs_Operation")]
    public void Ddl070_WideningDropLoop_MatchesOnlyChecksBoundToItsColumn(
        string table, string column, string variable, string canonicalName)
    {
        var ddl = ReadDdl070();

        // The SELECT that builds the DROP statements for this column.
        var select = Assert.Single(Regex.Matches(
            ddl,
            @"SELECT\s+" + Regex.Escape(variable) + @"\s*=.*?(?=IF\s+" + Regex.Escape(variable) + ")",
            RegexOptions.Singleline)).Value;

        // Bound by parent_column_id (0 for a table-level check, which no sys.columns row matches) to
        // this column by name — or the canonical name. Never anything else.
        Assert.Contains("c.column_id = cc.parent_column_id", select);
        Assert.Contains($"cc.parent_object_id = OBJECT_ID(N'[dbo].[{table}]')", select);
        Assert.Contains($"c.name = N'{column}'", select);
        Assert.Contains($"cc.name = N'{canonicalName}'", select);
        Assert.Contains($"ALTER TABLE [dbo].[{table}] DROP CONSTRAINT ' + QUOTENAME(cc.name)", select);
        Assert.Contains("REPLACE(cc.name, N'''', N'''''')", select); // a quote in a name survives the PRINT
        Assert.DoesNotContain("LegajoStatus_Owner", select);
        Assert.DoesNotContain("LIKE", select);
    }

    [Fact]
    public void Ddl070_EachWidening_DropsAndRecreatesInOneTransaction()
    {
        var ddl = ReadDdl070();

        foreach (var name in new[] { "CK_Documents_DocumentType", "CK_Documents_PdfType", "CK_ApiCallLogs_Operation" })
        {
            var batch = Assert.Single(Regex.Matches(
                ddl,
                @"BEGIN\s+TRANSACTION;(?:(?!\bGO\b).)*?ADD\s+CONSTRAINT\s+\[" + Regex.Escape(name) + @"\](?:(?!\bGO\b).)*?COMMIT\s+TRANSACTION;",
                RegexOptions.Singleline)).Value;

            Assert.Contains("SET XACT_ABORT ON", ddl);
            Assert.Contains("WITH CHECK", batch);
        }
    }

    [Fact]
    public void Ddl070_OperationWidening_CreatesNothingWhereNoCheckExisted()
    {
        // A database that never had a CHECK on [Operation] stays without one: the ADD sits in the ELSE.
        Assert.Matches(
            @"IF\s+@DropOperation\s*=\s*N''\s*BEGIN\s*PRINT\s+'[^']*';\s*END\s*ELSE\s*BEGIN\s*EXEC\s+sys\.sp_executesql\s+@DropOperation;\s*ALTER\s+TABLE\s+\[dbo\]\.\[ApiCallLogs\]\s+WITH\s+CHECK\s+ADD\s+CONSTRAINT\s+\[CK_ApiCallLogs_Operation\]",
            ReadDdl070());
    }

    [Fact]
    public void Ddl070_OperationWidening_IsTheLastBatch_AndNoGuardRaises()
    {
        var ddl = ReadDdl070();

        Assert.DoesNotContain("RAISERROR", ddl);
        Assert.True(
            ddl.IndexOf("[CK_ApiCallLogs_Operation]", StringComparison.Ordinal)
                > ddl.LastIndexOf("ADD [DocumentId]", StringComparison.Ordinal),
            "The CK_ApiCallLogs_Operation widening must stay the last batch, after every column is added.");
        Assert.Matches(@"COMMIT\s+TRANSACTION;\s*GO\s*$", ddl);
    }

    // ── Script shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Ddl070_Header_SaysRunBeforeDeployingOnFinanciaDb()
    {
        Assert.Contains("RUN BEFORE DEPLOYING, once on Financia-db", ReadDdl070Raw());
    }

    [Fact]
    public void Ddl070_FirstStatements_TurnOnTheFilteredIndexSetOptions()
    {
        var ddl = ReadDdl070().TrimStart();

        Assert.Matches(@"^SET\s+ANSI_NULLS\s+ON;\s*SET\s+QUOTED_IDENTIFIER\s+ON;\s*GO", ddl);
    }

    // ── Columns vs model ────────────────────────────────────────────────────────

    public static TheoryData<string, string, string, bool> AlteredColumns => new()
    {
        { "Documents", nameof(Document.PersonLegajoId), "uniqueidentifier", true },
        { "Documents", nameof(Document.PeriodEnd), "date", true },
        { "Documents", nameof(Document.EngagementType), "nvarchar(20)", true },
        { "Documents", nameof(Document.SupersedesDocumentId), "uniqueidentifier", true },
        { "Documents", nameof(Document.HolderCuit), "nvarchar(13)", true },
        { "Documents", nameof(Document.LegajoStatus), "nvarchar(30)", true },
        { "Documents", nameof(Document.LegajoStatusUpdatedAt), "datetime2", true },
        { "Documents", nameof(Document.PageQualityJson), "nvarchar(max)", true },
        { "PersonLegajos", nameof(PersonLegajo.ConsentRecordedAt), "datetime2", true },
        { "PersonLegajos", nameof(PersonLegajo.ConsentDocumentId), "uniqueidentifier", true },
        { "PersonLegajos", nameof(PersonLegajo.ConsentRecordedBy), "uniqueidentifier", true },
        { "ApiCallLogs", nameof(ApiCallLog.DocumentId), "uniqueidentifier", true },
    };

    [Theory]
    [MemberData(nameof(AlteredColumns))]
    public void Ddl070_AddedColumn_MatchesTheModel(string table, string property, string storeType, bool nullable)
    {
        var declared = Assert.Single(Regex.Matches(
            ReadDdl070(),
            @"ALTER\s+TABLE\s+\[dbo\]\.\[" + table + @"\]\s+ADD\s+\[" + property + @"\]\s+(?<type>[A-Z0-9]+(?:\((?:\d+|MAX)\))?)\s+(?<null>NOT\s+NULL|NULL)",
            RegexOptions.Singleline));

        Assert.Equal(storeType, declared.Groups["type"].Value.ToLowerInvariant());
        Assert.Equal(nullable, declared.Groups["null"].Value == "NULL");

        var entityType = table switch
        {
            "Documents"     => TypeOf<Document>(),
            "PersonLegajos" => TypeOf<PersonLegajo>(),
            _               => TypeOf<ApiCallLog>()
        };
        var mapped = entityType.FindProperty(property)!;

        Assert.Equal(property, mapped.GetColumnName());
        Assert.Equal(nullable, mapped.IsNullable);
        Assert.Equal(storeType, StoreType(mapped));
    }

    /// <summary>The columns DDL 071 added to a table 070 created; 070's CREATE TABLE does not declare them.</summary>
    private static readonly HashSet<string> Ddl071Columns = ["ReviewOverride", "ReviewReason"];

    [Theory]
    [InlineData("LegajoExtractions", typeof(LegajoExtraction), 24)]
    [InlineData("LegajoCorrections", typeof(LegajoCorrection), 9)]
    public void Ddl070_NewTable_DeclaresExactlyTheMappedColumns(string table, Type clrType, int columnCount)
    {
        var declared = DeclaredColumns(CreateTableBody(ReadDdl070(), table))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var entityType = TypeOfClr(clrType);
        Assert.Equal(table, entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());

        // DDL 071 later ADDED two columns to LegajoExtractions (pinned by LegajoReconciliationMappingTests).
        var mapped = entityType.GetProperties()
            .Select(p => (Name: p.GetColumnName(), Type: StoreType(p), p.IsNullable))
            .Where(c => !Ddl071Columns.Contains(c.Name))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(columnCount, declared.Count);
        Assert.Equal(declared, mapped);
    }

    // ── Defaults and keys ───────────────────────────────────────────────────────

    [Fact]
    public void Ddl070_Defaults_MatchTheModel()
    {
        var ddl = ReadDdl070();

        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoExtractions_ExtractionId\]\s+DEFAULT\s*\(NEWSEQUENTIALID\(\)\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoExtractions_IsCurrent\]\s+DEFAULT\s*\(0\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoExtractions_CreatedAt\]\s+DEFAULT\s*\(GETUTCDATE\(\)\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoCorrections_CorrectionId\]\s+DEFAULT\s*\(NEWSEQUENTIALID\(\)\)", ddl);
        Assert.Matches(@"CONSTRAINT\s+\[DF_LegajoCorrections_CorrectedAt\]\s+DEFAULT\s*\(GETUTCDATE\(\)\)", ddl);

        // The application supplies both keys; the NEWSEQUENTIALID() defaults serve hand-written rows.
        Assert.Equal(ValueGenerated.Never, TypeOf<LegajoExtraction>().FindProperty(nameof(LegajoExtraction.ExtractionId))!.ValueGenerated);
        Assert.Equal(ValueGenerated.Never, TypeOf<LegajoCorrection>().FindProperty(nameof(LegajoCorrection.CorrectionId))!.ValueGenerated);
        Assert.Equal("GETUTCDATE()", TypeOf<LegajoExtraction>().FindProperty(nameof(LegajoExtraction.CreatedAt))!.GetDefaultValueSql());
        Assert.Equal("GETUTCDATE()", TypeOf<LegajoCorrection>().FindProperty(nameof(LegajoCorrection.CorrectedAt))!.GetDefaultValueSql());
    }

    // ── Indexes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UxLegajoExtractionsCurrent_IsModelledUniqueAndFiltered_LikeTheDdl()
    {
        // The IsCurrent swap writes the old current run and the new one in ONE SaveChanges; the index
        // must be modelled exactly as the DBA creates it.
        var index = Assert.Single(TypeOf<LegajoExtraction>().GetIndexes(),
            i => i.GetDatabaseName() == "UX_LegajoExtractions_Current");

        Assert.True(index.IsUnique);
        Assert.Equal("[IsCurrent] = 1", index.GetFilter());
        Assert.Equal([nameof(LegajoExtraction.DocumentId), nameof(LegajoExtraction.Kind)],
            index.Properties.Select(p => p.Name));

        Assert.Matches(
            @"CREATE\s+UNIQUE\s+NONCLUSTERED\s+INDEX\s+\[UX_LegajoExtractions_Current\]\s+ON\s+\[dbo\]\.\[LegajoExtractions\]\s*\(\[DocumentId\]\s+ASC,\s*\[Kind\]\s+ASC\)\s+WHERE\s+\[IsCurrent\]\s*=\s*1;",
            ReadDdl070());
    }

    [Fact]
    public void IxDocumentsTenantIdPersonLegajoId_IsModelledFiltered_LikeTheDdl()
    {
        var index = Assert.Single(TypeOf<Document>().GetIndexes(),
            i => i.GetDatabaseName() == "IX_Documents_TenantId_PersonLegajoId");

        Assert.False(index.IsUnique);
        Assert.Equal("[PersonLegajoId] IS NOT NULL", index.GetFilter());
        Assert.Equal([nameof(Document.TenantId), nameof(Document.PersonLegajoId)], index.Properties.Select(p => p.Name));

        Assert.Matches(
            @"CREATE\s+NONCLUSTERED\s+INDEX\s+\[IX_Documents_TenantId_PersonLegajoId\]\s+ON\s+\[dbo\]\.\[Documents\]\s*\(\[TenantId\]\s+ASC,\s*\[PersonLegajoId\]\s+ASC\)\s+WHERE\s+\[PersonLegajoId\]\s+IS\s+NOT\s+NULL;",
            ReadDdl070());
    }

    [Fact]
    public void UxDocumentsLegajoHash_IsModelledUniqueAndFiltered_LikeTheDdl()
    {
        var index = Assert.Single(TypeOf<Document>().GetIndexes(),
            i => i.GetDatabaseName() == "UX_Documents_Legajo_Hash");

        Assert.True(index.IsUnique);
        Assert.Equal("[PersonLegajoId] IS NOT NULL AND [IsDeleted] = 0", index.GetFilter());
        Assert.Equal([nameof(Document.PersonLegajoId), nameof(Document.FileHashSHA256)], index.Properties.Select(p => p.Name));

        Assert.Matches(
            @"CREATE\s+UNIQUE\s+NONCLUSTERED\s+INDEX\s+\[UX_Documents_Legajo_Hash\]\s+ON\s+\[dbo\]\.\[Documents\]\s*\(\[PersonLegajoId\]\s+ASC,\s*\[FileHashSHA256\]\s+ASC\)\s+WHERE\s+\[PersonLegajoId\]\s+IS\s+NOT\s+NULL\s+AND\s+\[IsDeleted\]\s*=\s*0;",
            ReadDdl070());

        // The name the repository matches the violation by is the modelled one.
        Assert.Equal(Api.Web.FinanceIA.Data.Repositories.UniqueViolation.DocumentLegajoHash, index.GetDatabaseName());
    }

    [Fact]
    public void Ddl070_HashIndex_IsCreatedAfterPersonLegajoIdExists()
    {
        var ddl = ReadDdl070();

        var column = ddl.IndexOf("ADD [PersonLegajoId] UNIQUEIDENTIFIER NULL", StringComparison.Ordinal);
        var index  = ddl.IndexOf("CREATE UNIQUE NONCLUSTERED INDEX [UX_Documents_Legajo_Hash]", StringComparison.Ordinal);

        Assert.True(column >= 0 && index >= 0, "both statements must be in the script");
        Assert.True(column < index);
    }

    [Theory]
    [InlineData(typeof(LegajoExtraction), "IX_LegajoExtractions_TenantId_PersonLegajoId")]
    [InlineData(typeof(LegajoCorrection), "IX_LegajoCorrections_ExtractionId")]
    public void SecondaryIndex_IsModelledAndCreatedByTheDdl(Type clrType, string name)
    {
        Assert.Single(TypeOfClr(clrType).GetIndexes(), i => i.GetDatabaseName() == name);
        Assert.Matches(@"CREATE\s+NONCLUSTERED\s+INDEX\s+\[" + name + @"\]", ReadDdl070());
    }

    // ── Foreign keys: in the DDL, deliberately NOT in the model ─────────────────

    [Theory]
    [InlineData("FK_Documents_PersonLegajos")]
    [InlineData("FK_Documents_SupersedesDocument")]
    [InlineData("FK_PersonLegajos_ConsentDocument")]
    [InlineData("FK_LegajoExtractions_PersonLegajos")]
    [InlineData("FK_LegajoExtractions_Documents")]
    [InlineData("FK_LegajoCorrections_LegajoExtractions")]
    public void Ddl070_DeclaresTheForeignKey(string name)
    {
        Assert.Matches(@"CONSTRAINT\s+\[" + name + @"\]\s+FOREIGN\s+KEY", ReadDdl070());
    }

    [Theory]
    [InlineData(typeof(Document))]
    [InlineData(typeof(PersonLegajo))]
    [InlineData(typeof(LegajoExtraction))]
    [InlineData(typeof(LegajoCorrection))]
    public void NewRelationships_AreNotModelled(Type clrType)
    {
        // Every insert in this phase targets rows whose parents were saved by an EARLIER SaveChanges,
        // and Documents <-> PersonLegajos would be a cycle. A future same-SaveChanges parent+child write
        // must model its FK first (see the configurations' comments) — and update this test.
        Assert.Empty(TypeOfClr(clrType).GetForeignKeys());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IEntityType TypeOf<TEntity>() => TypeOfClr(typeof(TEntity));

    private static IEntityType TypeOfClr(Type clrType)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(clrType)!;
    }

    /// <summary>The model's store type, lower-case and without spaces, as the DDL helpers spell it.</summary>
    private static string StoreType(IProperty property)
        => property.GetColumnType().Replace(" ", string.Empty).ToLowerInvariant();

    /// <summary>The business project's source directory, found the same way as the script.</summary>
    private static string ProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA");
            if (File.Exists(Path.Combine(candidate, "Api.Web.FinanceIA.csproj")))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Api.Web.FinanceIA/Api.Web.FinanceIA.csproj was not found above {AppContext.BaseDirectory}.");
    }

    private static string ReadDdl070Raw()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl070FileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{Ddl070FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>The script with comments stripped: the commented pre/post-checks and rollback name the same objects.</summary>
    private static string ReadDdl070()
    {
        var text = Regex.Replace(ReadDdl070Raw(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
    }

    private static (string Expression, HashSet<string> Values) CheckConstraint(string ddl, string name)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CONSTRAINT\s+\[" + Regex.Escape(name) + @"\]\s+CHECK\s*\((?<expr>[^;]*?)\bIN\s*\((?<list>[^)]*)\)",
            RegexOptions.Singleline));

        var values = Regex.Matches(match.Groups["list"].Value, @"N?'(?<value>[^']*)'")
            .Select(m => m.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);

        return (match.Groups["expr"].Value, values);
    }

    private static string CreateTableBody(string ddl, string table)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CREATE\s+TABLE\s+\[dbo\]\.\[" + Regex.Escape(table) + @"\]\s*\((?<body>.*?)\r?\n\s*\);",
            RegexOptions.Singleline));

        return match.Groups["body"].Value;
    }

    /// <summary>(name, store type, nullability) per column line; DECIMAL(p, s) normalised to decimal(p,s).</summary>
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
