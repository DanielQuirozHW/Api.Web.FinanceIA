// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Model-metadata tests locking <see cref="Fiador"/>'s Fluent mapping to the DDL in
/// docs/database/046_fiadores_table.sql.
///
/// Why this exists: the project is Database First, so nothing validates the mapping at build time.
/// A typo in a HasColumnName, a max length that drifts from the column, or a lost index filter
/// compiles cleanly and only fails in Azure, at runtime, as "Invalid column name" on the first query
/// that touches the table. This is the cheapest place to catch that drift, and it is worth having
/// before any repository exists: this delivery ships the mapping with no query exercising it.
///
/// The context is built on the SQL Server provider rather than the in-memory one the repository
/// tests use, because GetTableName/GetColumnName/index filters are relational-only concepts — the
/// in-memory provider would report nothing and the assertions would be vacuous. Reading
/// <see cref="DbContext.Model"/> builds the model in process; it never opens a connection, so the
/// bogus connection string is never dialled and no physical table has to exist.
/// </summary>
public sealed class FiadorMappingTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static IEntityType FiadorType()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer("Server=none;Database=none;")
            .Options;
        using var context = new FinanciaDbContext(options);
        return context.Model.FindEntityType(typeof(Fiador))!;
    }

    private static IProperty Property(string name) => FiadorType().FindProperty(name)!;

    // ── Table ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Fiador_IsMappedToDboFiadores()
    {
        var entityType = FiadorType();

        Assert.NotNull(entityType);
        Assert.Equal("Fiadores", entityType.GetTableName());
        Assert.Equal("dbo", entityType.GetSchema());
    }

    [Fact]
    public void Fiador_PrimaryKey_IsFiadorId_AndIsDatabaseGenerated()
    {
        var key = FiadorType().FindPrimaryKey()!;

        Assert.Equal(nameof(Fiador.FiadorId), Assert.Single(key.Properties).Name);
        Assert.Equal(ValueGenerated.OnAdd, Property(nameof(Fiador.FiadorId)).ValueGenerated);
    }

    // ── Columns ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Fiador.FiadorId), "FiadorId")]
    [InlineData(nameof(Fiador.TenantId), "TenantId")]
    [InlineData(nameof(Fiador.EntityId), "EntityId")]
    [InlineData(nameof(Fiador.CUIT), "CUIT")]
    [InlineData(nameof(Fiador.Denominacion), "Denominacion")]
    [InlineData(nameof(Fiador.Cargo), "Cargo")]
    [InlineData(nameof(Fiador.PorcAccionista), "PorcAccionista")]
    [InlineData(nameof(Fiador.Origen), "Origen")]
    [InlineData(nameof(Fiador.NosisDataJson), "NosisDataJson")]
    [InlineData(nameof(Fiador.NosisFetchedAt), "NosisFetchedAt")]
    [InlineData(nameof(Fiador.Status), "Status")]
    [InlineData(nameof(Fiador.CreatedByUserId), "CreatedByUserId")]
    [InlineData(nameof(Fiador.CreatedAt), "CreatedAt")]
    [InlineData(nameof(Fiador.UpdatedAt), "UpdatedAt")]                // DDL 074
    [InlineData(nameof(Fiador.UpdatedByUserId), "UpdatedByUserId")]    // DDL 074
    public void Fiador_Property_MapsToItsDdlColumn(string propertyName, string columnName)
    {
        var property = Property(propertyName);

        Assert.NotNull(property);
        Assert.Equal(columnName, property.GetColumnName());
    }

    [Fact]
    public void Fiador_MapsExactlyTheSixteenDdlColumns()
    {
        // Guards against a property added to the entity without a matching column in 046 (and 069,
        // which added PersonLegajoId, and 074, which added UpdatedAt/UpdatedByUserId) — EF would
        // happily emit it and every query against Fiadores would break in Azure.
        var mapped = FiadorType().GetProperties().Select(p => p.GetColumnName()).OrderBy(n => n);

        Assert.Equal(
            new[]
            {
                "Cargo", "CreatedAt", "CreatedByUserId", "CUIT", "Denominacion", "EntityId",
                "FiadorId", "NosisDataJson", "NosisFetchedAt", "Origen", "PersonLegajoId",
                "PorcAccionista", "Status", "TenantId", "UpdatedAt", "UpdatedByUserId",
            }.OrderBy(n => n),
            mapped);
    }

    [Theory]
    [InlineData(nameof(Fiador.TenantId), 100)]
    [InlineData(nameof(Fiador.CUIT), 13)]
    [InlineData(nameof(Fiador.Denominacion), 300)]
    [InlineData(nameof(Fiador.Cargo), 200)]
    [InlineData(nameof(Fiador.Origen), 20)]
    [InlineData(nameof(Fiador.Status), 20)]
    public void Fiador_StringColumn_HasTheDdlMaxLength(string propertyName, int maxLength)
    {
        Assert.Equal(maxLength, Property(propertyName).GetMaxLength());
    }

    [Theory]
    [InlineData(nameof(Fiador.TenantId))]
    [InlineData(nameof(Fiador.EntityId))]
    [InlineData(nameof(Fiador.CUIT))]
    [InlineData(nameof(Fiador.Origen))]
    [InlineData(nameof(Fiador.Status))]
    [InlineData(nameof(Fiador.CreatedByUserId))]
    [InlineData(nameof(Fiador.CreatedAt))]
    public void Fiador_RequiredColumn_IsNotNullable(string propertyName)
    {
        Assert.False(Property(propertyName).IsNullable);
    }

    [Theory]
    [InlineData(nameof(Fiador.Denominacion))]
    [InlineData(nameof(Fiador.Cargo))]
    [InlineData(nameof(Fiador.PorcAccionista))]
    [InlineData(nameof(Fiador.NosisDataJson))]
    [InlineData(nameof(Fiador.NosisFetchedAt))]
    [InlineData(nameof(Fiador.UpdatedAt))]
    [InlineData(nameof(Fiador.UpdatedByUserId))]
    public void Fiador_OptionalColumn_IsNullable(string propertyName)
    {
        // The NOSIS pair in particular ships null and stays null: it is populated by a later
        // delivery, blocked on the vendor widening the VR variable group.
        Assert.True(Property(propertyName).IsNullable);
    }

    [Theory]
    [InlineData(nameof(Fiador.CUIT), "nvarchar(13)")]
    [InlineData(nameof(Fiador.NosisDataJson), "nvarchar(max)")]
    [InlineData(nameof(Fiador.NosisFetchedAt), "datetime2")]
    [InlineData(nameof(Fiador.CreatedAt), "datetime2")]
    [InlineData(nameof(Fiador.UpdatedAt), "datetime2")]
    public void Fiador_Column_HasTheDdlColumnType(string propertyName, string columnType)
    {
        Assert.Equal(columnType, Property(propertyName).GetColumnType());
    }

    [Fact]
    public void Fiador_PorcAccionista_ResolvesToDecimalFiveTwo()
    {
        // Expected without the space the configuration is written with: EF parses the configured
        // "decimal(5, 2)" through the SQL Server type mapping and regenerates the store type, so
        // this is the string EF will actually emit for DECIMAL(5, 2) in script 046.
        // GetPrecision/GetScale are deliberately not used — they stay null unless the facets are
        // set through HasPrecision, which this codebase does not do (see CreditLineConfiguration).
        Assert.Equal("decimal(5,2)", Property(nameof(Fiador.PorcAccionista)).GetColumnType());
    }

    [Fact]
    public void Fiador_NosisDataJson_IsAPlainString_WithNoValueConversion()
    {
        // The blob is deserialized to NosisData at the service boundary with JsonSerializer,
        // exactly as Entities.NosisDataJson is handled — never by an EF value conversion.
        var property = Property(nameof(Fiador.NosisDataJson));

        Assert.Null(property.GetValueConverter());
        Assert.Equal(typeof(string), property.ClrType);
    }

    [Fact]
    public void Fiador_CreatedAt_DefaultsToGetUtcDate()
    {
        Assert.Equal("GETUTCDATE()", Property(nameof(Fiador.CreatedAt)).GetDefaultValueSql());
    }

    // ── Relationships ───────────────────────────────────────────────────────────

    [Fact]
    public void Fiador_HasNoNavigationProperties()
    {
        // Bare scalar FKs, matching CreditLine and Entity.PersonLegajoId: no navigation either way.
        Assert.Empty(FiadorType().GetNavigations());
        Assert.Empty(FiadorType().GetSkipNavigations());
    }

    [Fact]
    public void Fiador_HasExactlyOneForeignKey_ToPersonLegajo_Restrict_Optional()
    {
        // The fiador legajo link inserts a new legajo and points the fiador at it in ONE SaveChanges.
        // EF orders those commands by modelled FKs only; without this one it would fall back to table
        // name order, "Fiadores" before "PersonLegajos", and fail FK_Fiadores_PersonLegajos. The FK to
        // Entities stays in the DDL only (no row is ever inserted together with its entity).
        var foreignKey = Assert.Single(FiadorType().GetForeignKeys());

        Assert.Equal(typeof(PersonLegajo), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(Fiador.PersonLegajoId), Assert.Single(foreignKey.Properties).Name);
        Assert.Equal(nameof(PersonLegajo.PersonLegajoId), Assert.Single(foreignKey.PrincipalKey.Properties).Name);
        Assert.Equal("FK_Fiadores_PersonLegajos", foreignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.False(foreignKey.IsRequired);
        Assert.False(foreignKey.IsUnique);
        Assert.Null(foreignKey.DependentToPrincipal);
        Assert.Null(foreignKey.PrincipalToDependent);
    }

    [Fact]
    public void Fiador_PersonLegajoId_IsCoveredOnlyByTheFilteredIndex()
    {
        // An FK with no covering index makes EF add a conventional IX_Fiadores_PersonLegajoId of its
        // own; the DDL index already covers it, so exactly one index may touch the column.
        var index = Assert.Single(
            FiadorType().GetIndexes(),
            i => i.Properties.Any(p => p.Name == nameof(Fiador.PersonLegajoId)));

        Assert.Equal("IX_Fiadores_PersonLegajoId", index.GetDatabaseName());
        Assert.Equal(3, FiadorType().GetIndexes().Count());
    }

    // ── Indexes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Fiador_ActivePerEntityCuit_IsUniqueAndFilteredToActive()
    {
        var index = Assert.Single(
            FiadorType().GetIndexes(),
            i => i.GetDatabaseName() == "UX_Fiadores_ActivePerEntityCuit");

        Assert.True(index.IsUnique);
        Assert.Equal("[Status] = 'Active'", index.GetFilter());
        Assert.Equal(
            new[] { nameof(Fiador.TenantId), nameof(Fiador.EntityId), nameof(Fiador.CUIT) },
            index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Fiador_TenantIdEntityId_IsANonUniqueUnfilteredIndex()
    {
        var index = Assert.Single(
            FiadorType().GetIndexes(),
            i => i.GetDatabaseName() == "IX_Fiadores_TenantId_EntityId");

        Assert.False(index.IsUnique);
        Assert.Null(index.GetFilter());
        Assert.Equal(
            new[] { nameof(Fiador.TenantId), nameof(Fiador.EntityId) },
            index.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Fiador_PersonLegajoId_IsANonUniqueIndexFilteredToLinkedRows()
    {
        // DDL 069. NULL on every fiador nobody linked to a legajo — hence the filter. Not unique: one
        // legajo may back the fiadores of several PyMEs.
        var index = Assert.Single(
            FiadorType().GetIndexes(),
            i => i.GetDatabaseName() == "IX_Fiadores_PersonLegajoId");

        Assert.False(index.IsUnique);
        Assert.Equal("[PersonLegajoId] IS NOT NULL", index.GetFilter());
        Assert.Equal(
            new[] { nameof(Fiador.PersonLegajoId) },
            index.Properties.Select(p => p.Name));
    }
}
