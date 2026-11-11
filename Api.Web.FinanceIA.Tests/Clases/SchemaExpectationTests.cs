// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-06

using Api.Web.FinanceIA.Clases.SchemaVerification;
using Api.Web.FinanceIA.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="SchemaExpectation.FromModel"/> against the REAL
/// <see cref="FinanciaDbContext"/> and <see cref="AppDbContext"/> models.
///
/// <para>
/// This is the half of the deploy gate that has to be right for the check to mean anything:
/// if the expectation does not include a column, the gate cannot notice the column is missing
/// from the database, and the "verified" deploy still breaks with "Invalid column name".
/// </para>
///
/// <para>
/// Building an EF model does not open a connection — it is assembled from the configuration and
/// the Fluent API alone — so these tests need no SQL Server and the placeholder connection
/// string below is never reached.
/// </para>
/// </summary>
public sealed class SchemaExpectationTests
{
    private const string ModelOnly = "Server=(model-only);Database=(model-only);";

    /// <summary>
    /// The columns added by the most recent pending DDL scripts. Each is mapped by a Fluent
    /// configuration and read or written as part of a full entity, which is exactly why
    /// publishing before the script runs breaks the endpoint. If the gate does not expect
    /// these, it does not gate anything.
    /// </summary>
    [Theory]
    [InlineData("ChatMessages", "HadEphemeralAttachment")]  // DDL 053
    [InlineData("ChatMessages", "AttachmentMarkdown")]      // DDL 055
    [InlineData("ChatMessages", "AttachmentFileName")]      // DDL 056
    [InlineData("CreditLineOperations", "ASolaFirma")]      // DDL 057
    [InlineData("CreditLineOperations", "RequiereContragarantiaReal")]  // DDL 057
    [InlineData("Analyses", "PreApprovedBy")]               // DDL 058
    [InlineData("Analyses", "DecisionAmount")]              // DDL 058
    [InlineData("Analyses", "ProposedOperationsJson")]      // DDL 061
    [InlineData("Analyses", "ConfirmedOperationsJson")]     // DDL 061
    [InlineData("TenantBillingConfig", "ChatEnabledModels")] // DDL 052
    [InlineData("Documents", "ExtractionAttempts")]          // DDL 050
    [InlineData("Users", "TeamId")]                          // DDL 063
    [InlineData("Teams", "ApprovalQuorum")]                  // DDL 065
    [InlineData("Entities", "PersonType")]                   // DDL 069
    [InlineData("Entities", "FiscalRegime")]                 // DDL 069
    [InlineData("Entities", "PersonLegajoId")]               // DDL 069
    [InlineData("Fiadores", "PersonLegajoId")]               // DDL 069
    [InlineData("LegajoExtractions", "ReviewOverride")]      // DDL 071
    [InlineData("LegajoExtractions", "ReviewReason")]        // DDL 071
    [InlineData("Analyses", "AnalysisKind")]                 // DDL 072
    [InlineData("Analyses", "PreApprovalChecksJson")]        // DDL 072
    [InlineData("Analyses", "ConfirmationChecksJson")]       // DDL 072
    [InlineData("Fiadores", "UpdatedAt")]                    // DDL 074
    [InlineData("Fiadores", "UpdatedByUserId")]              // DDL 074
    [InlineData("AnalysisParticipants", "FiadorId")]              // DDL 077
    [InlineData("AnalysisParticipants", "GuaranteeType")]         // DDL 077
    [InlineData("AnalysisParticipants", "SpouseConsent")]         // DDL 077
    [InlineData("AnalysisParticipants", "HousingInopposable")]    // DDL 077
    [InlineData("AnalysisParticipants", "GuaranteesGivenAmount")] // DDL 077
    [InlineData("AnalysisParticipants", "CuotasPorAnio")]         // DDL 078
    [InlineData("AnalysisParticipants", "AgroModeOverride")]      // DDL 078
    public void FromModel_Financia_ExpectsColumnsAddedByPendingDdl(string table, string column)
    {
        var expectation = SchemaExpectation.FromModel(BuildFinanciaModel());

        var expectedTable = expectation.Tables.SingleOrDefault(
            candidate => string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(expectedTable);
        Assert.Contains(
            column,
            expectedTable!.Columns,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tables created by pending scripts must be expected as whole tables, so their absence is
    /// reported as "Invalid object name" rather than going unnoticed.
    /// </summary>
    /// <remarks>
    /// [dbo].[AnalysisApprovalVotes] (DDL 067) IS listed now. It was provisioned ahead of the quorum
    /// engine and mapped by nobody, so until Phase 2 it was absent from the model by design and
    /// expecting it here would have failed the gate against a build that was correct. Phase 2 added
    /// the entity, so from this release the gate covers it — and a tenant whose 067 was skipped fails
    /// the deploy instead of failing the decision endpoint with "Invalid object name".
    /// </remarks>
    [Theory]
    [InlineData("MfaBackupCodes")]         // DDL 054
    [InlineData("Notifications")]          // DDL 049
    [InlineData("UserApprovalLimits")]     // DDL 059
    [InlineData("Teams")]                  // DDL 062
    [InlineData("TeamManagers")]           // DDL 064
    [InlineData("AnalysisApprovalVotes")]  // DDL 067
    [InlineData("PersonLegajos")]          // DDL 069
    [InlineData("LegajoExtractions")]      // DDL 070
    [InlineData("LegajoCorrections")]      // DDL 070
    [InlineData("LegajoItems")]            // DDL 071
    [InlineData("LegajoIncomes")]          // DDL 071
    [InlineData("AnalysisParticipants")]   // DDL 072
    public void FromModel_Financia_ExpectsTablesCreatedByPendingDdl(string table)
    {
        var expectation = SchemaExpectation.FromModel(BuildFinanciaModel());

        Assert.Contains(
            expectation.Tables,
            candidate => string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The shared Default database is a separate context with its own pending script
    /// (default-db/002), so it is verified separately and must not be conflated with the
    /// tenant business model.
    /// </summary>
    [Fact]
    public void FromModel_App_ExpectsTheSharedAuthTables()
    {
        var expectation = SchemaExpectation.FromModel(BuildAppModel());
        var names = expectation.Tables.Select(table => table.Name).ToArray();

        Assert.Contains("Credenciales", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TenantFirebaseConfig", names, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The two models describe two different databases. Business tables must not leak into the
    /// shared-database expectation, or verifying the Default DB would demand tables that
    /// correctly do not live there.
    /// </summary>
    [Fact]
    public void FromModel_App_DoesNotExpectTenantBusinessTables()
    {
        var expectation = SchemaExpectation.FromModel(BuildAppModel());
        var names = expectation.Tables.Select(table => table.Name).ToArray();

        Assert.DoesNotContain("Analyses", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChatMessages", names, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every mapped table lands under a schema, defaulting to dbo.</summary>
    [Fact]
    public void FromModel_AssignsDboWhenTheModelStatesNoSchema()
    {
        var expectation = SchemaExpectation.FromModel(BuildFinanciaModel());

        Assert.All(expectation.Tables, table => Assert.False(string.IsNullOrWhiteSpace(table.Schema)));
        Assert.Contains(expectation.Tables, table => table.Schema == "dbo");
    }

    /// <summary>
    /// The tenant business model must map EXACTLY these tables — the 28 applied one by one in
    /// <c>FinanciaDbContext.OnModelCreating</c>, each with an explicit <c>ToTable</c>.
    ///
    /// <para>
    /// This is a guard against a FALSE POSITIVE in the deploy gate, which is the failure mode
    /// that would hurt most: if EF ever discovers an entity type by convention — a navigation
    /// target, a keyless projection given a DbSet — that type gets a table name from its name
    /// alone, the gate demands a table no DDL script ever creates, and every deploy is blocked
    /// by a table that is not supposed to exist. A new table here is fine and expected; add it
    /// to this list deliberately, together with its numbered DDL script.
    /// </para>
    /// </summary>
    [Fact]
    public void FromModel_Financia_MapsExactlyTheKnownTables()
    {
        string[] expected =
        [
            "AlertRuleCatalogue", "Alerts", "Analyses", "AnalysisApprovalVotes", "AnalysisEdits", "AnalysisParticipants", "AnalysisRescores", "AnalysisResults",
            "ApiCallLogs", "AuditLogs", "ChatConversations", "ChatMessages", "CreditLineOperations",
            "CreditLines", "Documents", "Entities", "ExportLogs", "Fiadores", "LegajoCorrections",
            "LegajoExtractions", "LegajoIncomes", "LegajoItems", "MfaBackupCodes",
            "Notes", "Notifications", "PersonLegajos", "ScoringConfig", "SessionLogs", "TeamManagers", "Teams",
            "TenantBillingConfig", "TenantSettings", "UserApprovalLimits", "UserInvitations",
            "Users",
        ];

        var actual = SchemaExpectation.FromModel(BuildFinanciaModel())
            .Tables
            .Select(table => table.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), actual);
    }

    /// <summary>Same guard for the shared database, which maps only three tables.</summary>
    [Fact]
    public void FromModel_App_MapsExactlyTheKnownTables()
    {
        string[] expected = ["Credenciales", "ErrorLogs", "TenantFirebaseConfig"];

        var actual = SchemaExpectation.FromModel(BuildAppModel())
            .Tables
            .Select(table => table.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), actual);
    }

    /// <summary>A table with no columns would silently verify as present-and-correct.</summary>
    [Fact]
    public void FromModel_EveryExpectedTableCarriesColumns()
    {
        var expectation = SchemaExpectation.FromModel(BuildFinanciaModel());

        Assert.NotEmpty(expectation.Tables);
        Assert.All(expectation.Tables, table => Assert.NotEmpty(table.Columns));
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildFinanciaModel()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer(ModelOnly)
            .Options;

        using var context = new FinanciaDbContext(options);
        return context.Model;
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildAppModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ModelOnly)
            .Options;

        using var context = new AppDbContext(options);
        return context.Model;
    }
}
