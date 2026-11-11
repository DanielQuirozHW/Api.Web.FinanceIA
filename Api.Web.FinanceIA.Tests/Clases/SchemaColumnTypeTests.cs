// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Clases.SchemaVerification;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The deploy gate's TYPE check (<see cref="SchemaExpectation.ColumnTypes"/>): the one case a name-only
/// comparison misses — [AnalysisEdits] exists since 001 with every mapped column, but its TenantId is a
/// uniqueidentifier until DDL 081 runs. The gate must refuse that database.
/// </summary>
public sealed class SchemaColumnTypeTests
{
    private static readonly ExpectedTable AnalysisEdits = new("dbo", "AnalysisEdits",
        ["AnalysisEditId", "AnalysisId", "TenantId", "EditedBy", "FieldPath", "OldValue", "NewValue", "EditReason", "EditedAt"]);

    private static readonly ExpectedColumnType TenantIdType = new("dbo", "AnalysisEdits", "TenantId", "nvarchar");

    [Fact]
    public void Before_081_the_uniqueidentifier_TenantId_fails_the_gate_although_every_name_matches()
    {
        var result = SchemaComparer.Compare(Expectation(), Snapshot("uniqueidentifier"), "financia-movil");

        Assert.False(result.IsSatisfied);
        Assert.Empty(result.MissingTables);
        Assert.Empty(result.MissingColumns);
        var wrong = Assert.Single(result.WrongTypes);
        Assert.Equal("[dbo].[AnalysisEdits].[TenantId]", wrong.QualifiedName);
        Assert.Equal("uniqueidentifier", wrong.ActualType);
    }

    [Theory]
    [InlineData("nvarchar")]
    [InlineData("NVARCHAR")]
    public void After_081_the_nvarchar_TenantId_passes(string type)
    {
        Assert.True(SchemaComparer.Compare(Expectation(), Snapshot(type), "financia-movil").IsSatisfied);
    }

    [Fact]
    public void A_snapshot_without_types_skips_the_check()
    {
        var rows = AnalysisEdits.Columns.Select(c => ("dbo", "AnalysisEdits", c));

        Assert.True(SchemaComparer.Compare(Expectation(), new SchemaSnapshot(rows), "financia-movil").IsSatisfied);
    }

    [Fact]
    public void A_missing_table_is_reported_once_as_missing_not_as_a_wrong_type()
    {
        var result = SchemaComparer.Compare(Expectation(), SchemaSnapshot.WithTypes([]), "financia-new");

        Assert.Single(result.MissingTables);
        Assert.Empty(result.WrongTypes);
    }

    [Fact]
    public void The_type_list_names_only_AnalysisEdits_TenantId()
    {
        Assert.Equal([TenantIdType], SchemaExpectation.RequiredColumnTypes);
    }

    private static SchemaExpectation Expectation()
        => SchemaExpectation.FromTables([AnalysisEdits], [TenantIdType]);

    private static SchemaSnapshot Snapshot(string tenantIdType)
        => SchemaSnapshot.WithTypes(AnalysisEdits.Columns
            .Select(c => ("dbo", "AnalysisEdits", c, c == "TenantId" ? tenantIdType : "nvarchar"))
            .ToList());
}
