// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-06

using Api.Web.FinanceIA.Clases.SchemaVerification;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="SchemaComparer"/>, the decision the deploy gate makes.
///
/// <para>
/// The comparison is deliberately a pure function over an expectation and a snapshot, so the
/// gate's verdict can be exercised without a SQL Server. These tests pin the two failure modes
/// the DDL-before-deploy rule exists to prevent — a missing table ("Invalid object name") and a
/// missing column on a table that does exist ("Invalid column name") — plus the reporting choice
/// that keeps a failure readable.
/// </para>
/// </summary>
public sealed class SchemaComparerTests
{
    [Fact]
    public void Compare_WhenEveryTableAndColumnExists_IsSatisfied()
    {
        var expectation = Expect(("dbo", "ChatMessages", ["ChatMessageId", "Content"]));
        var snapshot = Snapshot(
            ("dbo", "ChatMessages", "ChatMessageId"),
            ("dbo", "ChatMessages", "Content"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.True(result.IsSatisfied);
        Assert.Empty(result.MissingTables);
        Assert.Empty(result.MissingColumns);
        Assert.Equal("financia-dev", result.DatabaseName);
    }

    /// <summary>
    /// The 055 / 056 shape: the table is there, one newly mapped column is not. This is the
    /// case that breaks every chat turn with "Invalid column name" when the code ships first.
    /// </summary>
    [Fact]
    public void Compare_WhenAColumnIsMissing_ReportsThatColumnOnly()
    {
        var expectation = Expect(
            ("dbo", "ChatMessages", ["ChatMessageId", "AttachmentMarkdown", "AttachmentFileName"]));

        var snapshot = Snapshot(
            ("dbo", "ChatMessages", "ChatMessageId"),
            ("dbo", "ChatMessages", "AttachmentMarkdown"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.False(result.IsSatisfied);
        Assert.Empty(result.MissingTables);
        var missing = Assert.Single(result.MissingColumns);
        Assert.Equal("AttachmentFileName", missing.Column);
        Assert.Equal("[dbo].[ChatMessages].[AttachmentFileName]", missing.QualifiedName);
    }

    /// <summary>
    /// The 049 / 054 shape: the whole table is absent, which is "Invalid object name".
    /// </summary>
    [Fact]
    public void Compare_WhenATableIsMissing_ReportsTheTable()
    {
        var expectation = Expect(("dbo", "MfaBackupCodes", ["MfaBackupCodeId", "UserId"]));
        var snapshot = Snapshot(("dbo", "Users", "UserId"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.False(result.IsSatisfied);
        var missing = Assert.Single(result.MissingTables);
        Assert.Equal("[dbo].[MfaBackupCodes]", missing.QualifiedName);
    }

    /// <summary>
    /// A missing table must NOT also emit one row per column it would have carried: the useful
    /// fact is "run the script that creates it", and a 30-line column dump buries it.
    /// </summary>
    [Fact]
    public void Compare_WhenATableIsMissing_DoesNotAlsoListItsColumns()
    {
        var expectation = Expect(("dbo", "Notifications", ["NotificationId", "TenantId", "UserId"]));
        var snapshot = Snapshot(("dbo", "Users", "UserId"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.Single(result.MissingTables);
        Assert.Empty(result.MissingColumns);
    }

    /// <summary>
    /// SQL Server compares object names under a case-insensitive collation on every environment
    /// this project targets, so a case difference is not a missing column.
    /// </summary>
    [Fact]
    public void Compare_MatchesNamesCaseInsensitively()
    {
        var expectation = Expect(("dbo", "ChatMessages", ["AttachmentMarkdown"]));
        var snapshot = Snapshot(("DBO", "chatmessages", "attachmentmarkdown"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.True(result.IsSatisfied);
    }

    /// <summary>
    /// Extra tables in the database are not a failure. The gate asks "can the deployed code run",
    /// not "does the schema match exactly" — a tenant DB legitimately carries tables this model
    /// does not map.
    /// </summary>
    [Fact]
    public void Compare_IgnoresTablesTheModelDoesNotMap()
    {
        var expectation = Expect(("dbo", "Users", ["UserId"]));
        var snapshot = Snapshot(
            ("dbo", "Users", "UserId"),
            ("dbo", "SomeLegacyTable", "WhateverId"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.True(result.IsSatisfied);
    }

    [Fact]
    public void Compare_ReportsEveryMissingColumnAcrossTables()
    {
        var expectation = Expect(
            ("dbo", "Analyses", ["AnalysisId", "PreApprovedBy", "DecisionAmount"]),
            ("dbo", "CreditLineOperations", ["OperationId", "ASolaFirma"]));

        var snapshot = Snapshot(
            ("dbo", "Analyses", "AnalysisId"),
            ("dbo", "CreditLineOperations", "OperationId"));

        var result = SchemaComparer.Compare(expectation, snapshot, "financia-dev");

        Assert.False(result.IsSatisfied);
        Assert.Equal(3, result.MissingColumns.Count);
    }

    private static SchemaExpectation Expect(
        params (string Schema, string Table, string[] Columns)[] tables)
    {
        // Stated directly rather than read from a model: these tests cover the comparison.
        // That the real EF model is read correctly is SchemaExpectationTests' job.
        var expected = tables
            .Select(table => new ExpectedTable(table.Schema, table.Table, table.Columns))
            .ToArray();

        return SchemaExpectation.FromTables(expected);
    }

    private static SchemaSnapshot Snapshot(params (string Schema, string Table, string Column)[] rows)
    {
        return new SchemaSnapshot(rows);
    }
}
