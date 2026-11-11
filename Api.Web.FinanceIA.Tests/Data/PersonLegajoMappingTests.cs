// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-27

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Model-metadata tests that lock the DDL 069 mapping to
/// docs/database/069_person_legajos_and_entity_person_type.sql, following the
/// <c>FiadorMappingTests</c> precedent. DDL 069 adds the new <see cref="PersonLegajo"/> table, and
/// three columns, two indexes and one foreign key on <see cref="Entity"/>.
///
/// <para>
/// Why this exists: the project is Database First, so nothing validates the mapping at build time.
/// A typo in a HasColumnName, or a max length that drifts from the column, compiles cleanly. It only
/// fails in Azure, at runtime, as "Invalid column name". DDL 069 adds three failure modes of its own
/// that no compile and no in-memory test can see:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The Entities → PersonLegajos foreign key is modelled for INSERT ORDER only. EF orders the
/// commands of one SaveChanges by the model's foreign keys. Without this edge it orders by table
/// name, "Entities" before "PersonLegajos", so every Humana create that inserts a new legajo fails
/// FK_Entities_PersonLegajos (error 547) while the in-memory tests stay green. A navigation on
/// either side would be a second trap, because Add/Update could pull a legajo read with
/// AsNoTracking into the graph.
/// </description></item>
/// <item><description>
/// The unique index names are load-bearing. The repositories recognise a lost race by the index name
/// in the provider message, so a renamed index turns a 409 into a 500.
/// </description></item>
/// <item><description>
/// The two vocabularies are CHECK constraints on every business database. The code's lists must be
/// exactly what the script admits, so they are compared with the script text itself, not with a copy
/// of it pasted into this file.
/// </description></item>
/// </list>
/// <para>
/// The context uses the SQL Server provider rather than the in-memory one, because table, column,
/// index and constraint names are relational-only concepts. Reading <see cref="DbContext.Model"/>
/// builds the model in process and never opens a connection.
/// </para>
/// </summary>
public sealed class PersonLegajoMappingTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────
    private const string Ddl069FileName = "069_person_legajos_and_entity_person_type.sql";

    private static IEntityType TypeOf<TEntity>()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(TEntity))!;
    }

    private static IEntityType LegajoType() => TypeOf<PersonLegajo>();

    private static IEntityType EntityType() => TypeOf<Entity>();

    private static IProperty LegajoProperty(string name) => LegajoType().FindProperty(name)!;

    private static IProperty EntityProperty(string name) => EntityType().FindProperty(name)!;

    // ── PersonLegajo: table and key ─────────────────────────────────────────────

    [Fact]
    public void PersonLegajo_IsMappedToDboPersonLegajos()
    {
        var entityType = LegajoType();

        Assert.NotNull(entityType);
        Assert.Equal("PersonLegajos", entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());
    }

    [Fact]
    public void PersonLegajo_PrimaryKey_IsPersonLegajoId_AndIsSuppliedByTheApplication()
    {
        // ValueGenerated.Never means EF sends the Guid the application set and never makes up its
        // own. The application sets Guid.NewGuid() so that Entity.PersonLegajoId is known before the
        // single SaveChanges that inserts both rows. The DDL's NEWSEQUENTIALID() default is only for
        // the backfill and for rows written by hand.
        var key = LegajoType().FindPrimaryKey()!;

        Assert.Equal(nameof(PersonLegajo.PersonLegajoId), Assert.Single(key.Properties).Name);
        Assert.Equal(ValueGenerated.Never, LegajoProperty(nameof(PersonLegajo.PersonLegajoId)).ValueGenerated);
    }

    // ── PersonLegajo: columns ───────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(PersonLegajo.PersonLegajoId), "PersonLegajoId")]
    [InlineData(nameof(PersonLegajo.TenantId), "TenantId")]
    [InlineData(nameof(PersonLegajo.CUIT), "CUIT")]
    [InlineData(nameof(PersonLegajo.FullName), "FullName")]
    [InlineData(nameof(PersonLegajo.FiscalRegime), "FiscalRegime")]
    [InlineData(nameof(PersonLegajo.MonotributoCategory), "MonotributoCategory")]
    [InlineData(nameof(PersonLegajo.ActivityStartDate), "ActivityStartDate")]
    [InlineData(nameof(PersonLegajo.MaritalStatus), "MaritalStatus")]
    [InlineData(nameof(PersonLegajo.PatrimonialRegime), "PatrimonialRegime")]
    [InlineData(nameof(PersonLegajo.ArcaSnapshotJson), "ArcaSnapshotJson")]
    [InlineData(nameof(PersonLegajo.ArcaDataUpdatedAt), "ArcaDataUpdatedAt")]
    [InlineData(nameof(PersonLegajo.BcraDataJson), "BcraDataJson")]
    [InlineData(nameof(PersonLegajo.BcraDataUpdatedAt), "BcraDataUpdatedAt")]
    [InlineData(nameof(PersonLegajo.NosisDataJson), "NosisDataJson")]
    [InlineData(nameof(PersonLegajo.NosisDataUpdatedAt), "NosisDataUpdatedAt")]
    [InlineData(nameof(PersonLegajo.CreatedBy), "CreatedBy")]
    [InlineData(nameof(PersonLegajo.CreatedAt), "CreatedAt")]
    [InlineData(nameof(PersonLegajo.UpdatedAt), "UpdatedAt")]
    public void PersonLegajo_Property_MapsToItsDdlColumn(string propertyName, string columnName)
    {
        var property = LegajoProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName());
    }

    /// <summary>The three consent columns DDL 070 added; pinned by LegajoDocumentsMappingTests.</summary>
    private static readonly string[] Ddl070Columns = ["ConsentDocumentId", "ConsentRecordedAt", "ConsentRecordedBy"];

    [Fact]
    public void PersonLegajo_MapsExactlyTheEighteenDdl069Columns_PlusTheThreeDdl070Ones()
    {
        // Guards against a property added to the entity without a matching column in 069 or 070. EF
        // would happily emit it, and every query against PersonLegajos would break in Azure.
        var mapped = LegajoType().GetProperties().Select(p => p.GetColumnName()).OrderBy(n => n);

        Assert.Equal(
            new[]
            {
                "ActivityStartDate", "ArcaDataUpdatedAt", "ArcaSnapshotJson", "BcraDataJson",
                "BcraDataUpdatedAt", "CreatedAt", "CreatedBy", "CUIT", "FiscalRegime", "FullName",
                "MaritalStatus", "MonotributoCategory", "NosisDataJson", "NosisDataUpdatedAt",
                "PatrimonialRegime", "PersonLegajoId", "TenantId", "UpdatedAt",
            }.Concat(Ddl070Columns).OrderBy(n => n),
            mapped);
    }

    [Theory]
    [InlineData(nameof(PersonLegajo.TenantId), 100)]
    [InlineData(nameof(PersonLegajo.CUIT), 13)]
    [InlineData(nameof(PersonLegajo.FullName), 300)]
    [InlineData(nameof(PersonLegajo.FiscalRegime), 30)]
    [InlineData(nameof(PersonLegajo.MonotributoCategory), 2)]
    [InlineData(nameof(PersonLegajo.MaritalStatus), 30)]
    [InlineData(nameof(PersonLegajo.PatrimonialRegime), 30)]
    public void PersonLegajo_StringColumn_HasTheDdlMaxLength(string propertyName, int maxLength)
    {
        Assert.Equal(maxLength, LegajoProperty(propertyName).GetMaxLength());
    }

    [Theory]
    [InlineData(nameof(PersonLegajo.PersonLegajoId))]
    [InlineData(nameof(PersonLegajo.TenantId))]
    [InlineData(nameof(PersonLegajo.CUIT))]
    [InlineData(nameof(PersonLegajo.FullName))]
    [InlineData(nameof(PersonLegajo.FiscalRegime))]
    [InlineData(nameof(PersonLegajo.CreatedAt))]
    [InlineData(nameof(PersonLegajo.UpdatedAt))]
    public void PersonLegajo_RequiredColumn_IsNotNullable(string propertyName)
    {
        Assert.False(LegajoProperty(propertyName).IsNullable);
    }

    [Theory]
    [InlineData(nameof(PersonLegajo.MonotributoCategory))]
    [InlineData(nameof(PersonLegajo.ActivityStartDate))]
    [InlineData(nameof(PersonLegajo.MaritalStatus))]
    [InlineData(nameof(PersonLegajo.PatrimonialRegime))]
    [InlineData(nameof(PersonLegajo.ArcaSnapshotJson))]
    [InlineData(nameof(PersonLegajo.ArcaDataUpdatedAt))]
    [InlineData(nameof(PersonLegajo.BcraDataJson))]
    [InlineData(nameof(PersonLegajo.BcraDataUpdatedAt))]
    [InlineData(nameof(PersonLegajo.NosisDataJson))]
    [InlineData(nameof(PersonLegajo.NosisDataUpdatedAt))]
    [InlineData(nameof(PersonLegajo.CreatedBy))] // NULL = the DDL 069 backfill wrote the row
    public void PersonLegajo_OptionalColumn_IsNullable(string propertyName)
    {
        Assert.True(LegajoProperty(propertyName).IsNullable);
    }

    [Theory]
    [InlineData(nameof(PersonLegajo.CUIT), "nvarchar(13)")]
    [InlineData(nameof(PersonLegajo.ActivityStartDate), "date")]
    [InlineData(nameof(PersonLegajo.ArcaSnapshotJson), "nvarchar(max)")]
    [InlineData(nameof(PersonLegajo.BcraDataJson), "nvarchar(max)")]
    [InlineData(nameof(PersonLegajo.NosisDataJson), "nvarchar(max)")]
    [InlineData(nameof(PersonLegajo.ArcaDataUpdatedAt), "datetime2")]
    [InlineData(nameof(PersonLegajo.BcraDataUpdatedAt), "datetime2")]
    [InlineData(nameof(PersonLegajo.NosisDataUpdatedAt), "datetime2")]
    [InlineData(nameof(PersonLegajo.CreatedAt), "datetime2")]
    [InlineData(nameof(PersonLegajo.UpdatedAt), "datetime2")]
    public void PersonLegajo_Column_HasTheDdlColumnType(string propertyName, string columnType)
    {
        Assert.Equal(columnType, LegajoProperty(propertyName).GetColumnType());
    }

    [Theory]
    [InlineData(nameof(PersonLegajo.ArcaSnapshotJson))]
    [InlineData(nameof(PersonLegajo.BcraDataJson))]
    [InlineData(nameof(PersonLegajo.NosisDataJson))]
    public void PersonLegajo_SnapshotJson_IsAPlainString_WithNoValueConversion(string propertyName)
    {
        // Serialized at the boundary with JsonSerializer, exactly as the Entities.*DataJson blobs are,
        // and never through an EF value conversion.
        var property = LegajoProperty(propertyName);

        Assert.Null(property.GetValueConverter());
        Assert.Equal(typeof(string), property.ClrType);
    }

    [Fact]
    public void PersonLegajo_FiscalRegime_IsRequired_AndDefaultsToDesconocido()
    {
        var property = LegajoProperty(nameof(PersonLegajo.FiscalRegime));

        Assert.False(property.IsNullable);
        Assert.Equal("Desconocido", property.GetDefaultValue());
    }

    [Fact]
    public void PersonLegajo_NewInstance_StartsAsDesconocido_NotAnEmptyString()
    {
        // EF only leaves a value out of the INSERT, for the column default to fill, when it equals
        // the CLR sentinel, which is null for a string. An "" initializer would be sent as-is and
        // fail CK_PersonLegajos_FiscalRegime on the first legajo ever created.
        Assert.Equal("Desconocido", new PersonLegajo().FiscalRegime);
    }

    [Theory]
    [InlineData(nameof(PersonLegajo.CreatedAt))]
    [InlineData(nameof(PersonLegajo.UpdatedAt))]
    public void PersonLegajo_Timestamp_DefaultsToGetUtcDate(string propertyName)
    {
        Assert.Equal("GETUTCDATE()", LegajoProperty(propertyName).GetDefaultValueSql());
    }

    // ── PersonLegajo: indexes and relationships ─────────────────────────────────

    [Fact]
    public void PersonLegajo_TenantIdCuit_IsTheOnlyIndex_UniqueAndUnfiltered()
    {
        // One legajo per person per tenant. The race handling in PersonLegajoRepository matches this
        // exact name in the provider message.
        var index = Assert.Single(LegajoType().GetIndexes());

        Assert.Equal("UX_PersonLegajos_TenantId_CUIT", index.GetDatabaseName());
        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.Equal(
            new[] { nameof(PersonLegajo.TenantId), nameof(PersonLegajo.CUIT) },
            index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void PersonLegajo_HasNoNavigations_AndIsReferencedOnlyByEntitiesAndFiadores()
    {
        var legajoType = LegajoType();

        Assert.Empty(legajoType.GetNavigations());
        Assert.Empty(legajoType.GetSkipNavigations());
        Assert.Empty(legajoType.GetForeignKeys());

        // Both are modelled for INSERT ORDER only (a new legajo precedes the row that points at it in
        // the same SaveChanges): Entities since DDL 069, Fiadores since the fiador legajo link. The
        // legajo-scoped tables (Documents, extraction runs, participants) keep their FKs in the DDL.
        var referencing = legajoType.GetReferencingForeignKeys()
            .Select(fk => fk.DeclaringEntityType.ClrType.Name)
            .OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { nameof(Entity), nameof(Fiador) }, referencing);
    }

    // ── Entity: DDL 069 columns ─────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Entity.PersonType), "PersonType")]
    [InlineData(nameof(Entity.FiscalRegime), "FiscalRegime")]
    [InlineData(nameof(Entity.PersonLegajoId), "PersonLegajoId")]
    public void Entity_Ddl069Property_MapsToItsDdlColumn(string propertyName, string columnName)
    {
        var property = EntityProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName());
    }

    [Fact]
    public void Entity_PersonType_IsRequiredMaxTen_DefaultingToJuridica_WithANullSentinel()
    {
        var property = EntityProperty(nameof(Entity.PersonType));

        Assert.Equal(10, property.GetMaxLength());
        Assert.False(property.IsNullable);
        Assert.Equal("Juridica", property.GetDefaultValue());

        // The sentinel is the value EF treats as "not set, let the column default apply". For a
        // string it is null. The initializer below never leaves the property null, so EF always
        // sends the value. It never silently relies on DF_Entities_PersonType, which would turn a
        // Humana into a Juridica if the two ever disagreed.
        Assert.Null(property.Sentinel);
    }

    [Fact]
    public void Entity_NewInstance_HasPersonTypeJuridica()
    {
        Assert.Equal("Juridica", new Entity().PersonType);
    }

    [Fact]
    public void Entity_FiscalRegime_IsNullable_WithMaxLengthThirty()
    {
        // NULL means "not determined yet": legacy rows, and a Juridica created without fiscal data.
        var property = EntityProperty(nameof(Entity.FiscalRegime));

        Assert.Equal(30, property.GetMaxLength());
        Assert.True(property.IsNullable);
        Assert.Null(property.GetDefaultValue());
    }

    [Fact]
    public void Entity_PersonLegajoId_IsANullableGuid()
    {
        // NULL for every Juridica (CK_Entities_PersonLegajo_Humana).
        var property = EntityProperty(nameof(Entity.PersonLegajoId));

        Assert.Equal(typeof(Guid?), property.ClrType);
        Assert.True(property.IsNullable);
    }

    [Theory]
    [InlineData(nameof(Entity.PersonType), "nvarchar(10)")]
    [InlineData(nameof(Entity.FiscalRegime), "nvarchar(30)")]
    [InlineData(nameof(Entity.PersonLegajoId), "uniqueidentifier")]
    public void Entity_Ddl069Column_HasTheDdlColumnType(string propertyName, string columnType)
    {
        Assert.Equal(columnType, EntityProperty(propertyName).GetColumnType());
    }

    // ── Entity: indexes ─────────────────────────────────────────────────────────

    [Fact]
    public void Entity_CuitUniqueness_IsPerTenant_UqEntitiesTenantIdCuit()
    {
        var index = Assert.Single(
            EntityType().GetIndexes(),
            i => i.GetDatabaseName() == "UQ_Entities_TenantId_CUIT");

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.Equal(
            new[] { nameof(Entity.TenantId), nameof(Entity.CUIT) },
            index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Entity_GlobalCuitUniqueness_IsGone()
    {
        // DDL 069 swapped the global key for the per-tenant one. Before the swap, a CUIT that already
        // existed in another tenant returned a 500. It is now a legitimate 201.
        var indexes = EntityType().GetIndexes().ToList();

        Assert.DoesNotContain(indexes, i => i.GetDatabaseName() == "UQ_Entities_CUIT");
        Assert.DoesNotContain(
            indexes,
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Entity.CUIT) }));
    }

    [Fact]
    public void Entity_PersonLegajoId_IsCoveredOnlyByTheFilteredUniqueIndex()
    {
        // At most one entity per legajo. The index is filtered because every Juridica holds NULL.
        // It is also the only index on the column. EF would add a conventional
        // IX_Entities_PersonLegajoId for the foreign key if this one ever stopped covering it, and
        // that index does not exist in the DDL.
        var index = Assert.Single(
            EntityType().GetIndexes(),
            i => i.Properties.Any(p => p.Name == nameof(Entity.PersonLegajoId)));

        Assert.Equal("UX_Entities_PersonLegajoId", index.GetDatabaseName());
        Assert.True(index.IsUnique);
        Assert.Equal("[PersonLegajoId] IS NOT NULL", index.GetFilter());
        Assert.Equal(
            new[] { nameof(Entity.PersonLegajoId) },
            index.Properties.Select(p => p.Name));
    }

    // ── Entity: relationships ───────────────────────────────────────────────────

    [Fact]
    public void Entity_HasExactlyOneForeignKey_ToPersonLegajo_Restrict_Optional()
    {
        var foreignKey = Assert.Single(EntityType().GetForeignKeys());

        Assert.Equal(typeof(PersonLegajo), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(Entity.PersonLegajoId), Assert.Single(foreignKey.Properties).Name);
        Assert.Equal(nameof(PersonLegajo.PersonLegajoId), Assert.Single(foreignKey.PrincipalKey.Properties).Name);
        Assert.Equal("FK_Entities_PersonLegajos", foreignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.False(foreignKey.IsRequired);

        // WithMany on purpose. The 1:1 is UX_Entities_PersonLegajoId's job in the database, and the
        // model needs only the edge.
        Assert.False(foreignKey.IsUnique);
        Assert.Null(foreignKey.DependentToPrincipal);
        Assert.Null(foreignKey.PrincipalToDependent);
    }

    [Fact]
    public void Entity_HasNoNavigationProperties()
    {
        var entityType = EntityType();

        Assert.Empty(entityType.GetNavigations());
        Assert.Empty(entityType.GetSkipNavigations());
    }

    // ── DDL 069 lock: the script text against the model and the vocabularies ──

    [Fact]
    public void Ddl069_FiscalRegimeChecks_AdmitExactlyFiscalRegimesKnown()
    {
        var ddl = ReadDdl069();

        var legajoCheck   = CheckConstraint(ddl, "CK_PersonLegajos_FiscalRegime");
        var entitiesCheck = CheckConstraint(ddl, "CK_Entities_FiscalRegime");

        Assert.Equal(Sorted(FiscalRegimes.Known), Sorted(legajoCheck.Values));
        Assert.Equal(Sorted(FiscalRegimes.Known), Sorted(entitiesCheck.Values));
    }

    [Fact]
    public void Ddl069_EntitiesFiscalRegimeCheck_AdmitsNull_UnlikeTheLegajoOne()
    {
        // The two CHECKs mirror the two properties' nullability: Entity.FiscalRegime is nullable
        // ("not determined yet"), PersonLegajo.FiscalRegime is not.
        var ddl = ReadDdl069();

        Assert.Contains(
            "[FiscalRegime] IS NULL",
            CheckConstraint(ddl, "CK_Entities_FiscalRegime").Expression,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IS NULL",
            CheckConstraint(ddl, "CK_PersonLegajos_FiscalRegime").Expression,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ddl069_PersonTypeCheck_AdmitsExactlyPersonTypesKnown()
    {
        var check = CheckConstraint(ReadDdl069(), "CK_Entities_PersonType");

        Assert.Equal(Sorted(PersonTypes.Known), Sorted(check.Values));
    }

    [Fact]
    public void Ddl069_ColumnDefaults_MatchTheModelDefaults()
    {
        var ddl = ReadDdl069();

        Assert.Equal(
            EntityProperty(nameof(Entity.PersonType)).GetDefaultValue(),
            DefaultConstraintLiteral(ddl, "DF_Entities_PersonType"));
        Assert.Equal(
            LegajoProperty(nameof(PersonLegajo.FiscalRegime)).GetDefaultValue(),
            DefaultConstraintLiteral(ddl, "DF_PersonLegajos_FiscalRegime"));
    }

    [Fact]
    public void Ddl069_PersonLegajosTable_DeclaresTheMappedColumns_WithTheirTypesAndNullability()
    {
        // Compares the model with the CREATE TABLE text itself, column by column. It checks the name,
        // the store type and NULL / NOT NULL, so a column the DBA creates is exactly the one EF writes.
        var declared = DeclaredColumns(CreateTableBody(ReadDdl069(), "PersonLegajos"));

        // The CREATE TABLE is 069's; the consent columns 070 ALTERs in are checked against 070.
        var mapped = LegajoType().GetProperties()
            .Select(p => (Name: p.GetColumnName(), Type: p.GetColumnType(), p.IsNullable))
            .Where(c => !Ddl070Columns.Contains(c.Name))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(18, declared.Count);
        Assert.Equal(declared.OrderBy(c => c.Name, StringComparer.Ordinal).ToList(), mapped);
    }

    [Fact]
    public void Ddl069_EntitiesAddedColumns_MatchTheModel_WithTheirTypesAndNullability()
    {
        var matches = Regex.Matches(
            ReadDdl069(),
            @"ALTER\s+TABLE\s+\[dbo\]\.\[Entities\]\s+ADD\s+\[(?<name>\w+)\]\s+(?<type>[A-Z0-9]+(?:\((?:\d+|MAX)\))?)\s+(?<null>NOT\s+NULL|NULL)",
            RegexOptions.Singleline);

        var declared = matches
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var mapped = new[] { nameof(Entity.FiscalRegime), nameof(Entity.PersonLegajoId), nameof(Entity.PersonType) }
            .Select(EntityProperty)
            .Select(p => (Name: p.GetColumnName(), Type: p.GetColumnType(), p.IsNullable))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(mapped, declared);
    }

    [Fact]
    public void Ddl069_BackfillHumanaPrefixes_AreExactlyTheOnesCuitHelperCallsHumana()
    {
        // The backfill classifies the rows that predate 069, and CuitHelper classifies every row
        // created after. If the two disagreed, the same CUIT would be a Humana or a Juridica
        // depending on when it was created.
        var match = Assert.Single(Regex.Matches(
            ReadDdl069(),
            @"LEFT\(\s*\[CUIT\]\s*,\s*2\s*\)\s+IN\s*\((?<list>[^)]*)\)",
            RegexOptions.Singleline));

        var backfill = Literals(match.Groups["list"].Value);

        var helper = Enumerable.Range(0, 100)
            .Select(n => n.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
            .Where(prefix => CuitHelper.PersonTypeFromPrefix($"{prefix}-11111111-2") == PersonTypes.Humana)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Sorted(helper), Sorted(backfill));
    }

    // ── DDL helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The DDL 069 script with its comments stripped, so that only live DDL is ever matched. Its
    /// commented pre-checks, post-checks and rollback name the same constraints. Found by walking up
    /// from the test binary to the checkout root.
    /// </summary>
    private static string ReadDdl069()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA", "docs", "database", Ddl069FileName);
            if (!File.Exists(candidate)) continue;

            var text = File.ReadAllText(candidate);
            text = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(text, @"--[^\r\n]*", string.Empty);
        }

        throw new FileNotFoundException(
            $"{Ddl069FileName} was not found above {AppContext.BaseDirectory}; these tests read the script from the checkout.");
    }

    /// <summary>
    /// The live definition of a CHECK constraint: the expression before its IN list, and the
    /// literals in that list. It must be defined exactly once in the script.
    /// </summary>
    private static (string Expression, HashSet<string> Values) CheckConstraint(string ddl, string name)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CONSTRAINT\s+\[" + Regex.Escape(name) + @"\]\s+CHECK\s*\((?<expr>[^;]*?)\bIN\s*\((?<list>[^)]*)\)",
            RegexOptions.Singleline));

        return (match.Groups["expr"].Value, Literals(match.Groups["list"].Value));
    }

    private static string DefaultConstraintLiteral(string ddl, string name)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CONSTRAINT\s+\[" + Regex.Escape(name) + @"\]\s+DEFAULT\s*\(\s*N?'(?<value>[^']*)'\s*\)",
            RegexOptions.Singleline));

        return match.Groups["value"].Value;
    }

    private static string CreateTableBody(string ddl, string table)
    {
        var match = Assert.Single(Regex.Matches(
            ddl,
            @"CREATE\s+TABLE\s+\[dbo\]\.\[" + Regex.Escape(table) + @"\]\s*\((?<body>.*?)\r?\n\s*\);",
            RegexOptions.Singleline));

        return match.Groups["body"].Value;
    }

    /// <summary>
    /// One (name, store type, nullability) per column line of a CREATE TABLE body. The store type is
    /// lower-cased, which is exactly how EF spells it (NVARCHAR(MAX) → nvarchar(max)).
    /// </summary>
    private static List<(string Name, string Type, bool IsNullable)> DeclaredColumns(string body)
        => Regex.Matches(
                body,
                @"^\s*\[(?<name>\w+)\]\s+(?<type>[A-Z0-9]+(?:\((?:\d+|MAX)\))?)\s+(?<null>NOT\s+NULL|NULL)",
                RegexOptions.Multiline)
            .Select(m => (Name: m.Groups["name"].Value,
                          Type: m.Groups["type"].Value.ToLowerInvariant(),
                          IsNullable: m.Groups["null"].Value == "NULL"))
            .ToList();

    private static HashSet<string> Literals(string list)
        => Regex.Matches(list, @"N?'(?<value>[^']*)'")
            .Select(m => m.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string[] Sorted(IEnumerable<string> values)
        => values.OrderBy(v => v, StringComparer.Ordinal).ToArray();
}
