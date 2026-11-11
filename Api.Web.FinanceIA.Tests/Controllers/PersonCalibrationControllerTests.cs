// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Clases;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// GET /api/dashboard/person-calibration and .../export at the controller: the role gate (by reflection, the
/// AuthorizationMatrixTests precedent — there is no test host), the query validation, and the export's file
/// and audit row.
/// </summary>
public sealed class PersonCalibrationControllerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string[] Actions =
        [nameof(PersonCalibrationController.GetAsync), nameof(PersonCalibrationController.ExportAsync)];

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = DashboardFixture.Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static PersonCalibrationController Sut(PersonCalibrationFixture f, string? firebaseUid, RecordingAuditService? audit = null)
        => new(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            new UserRepository(f.Dashboard.Context, NullLogger<UserRepository>.Instance),
            new TeamRepository(f.Dashboard.Context, NullLogger<TeamRepository>.Instance),
            f.Service(),
            audit ?? new RecordingAuditService(),
            NullLogger<PersonCalibrationController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        firebaseUid is null ? [] : [new Claim("uid", firebaseUid)], "Test"))
                }
            }
        };

    // ── role gate ─────────────────────────────────────────────────────────────

    [Fact]
    public void CalibrationReaders_IsAdminAndRiskManager_Exactly()
        => Assert.Equal(Roles.Admin + "," + Roles.RiskManager, Roles.CalibrationReaders);

    [Theory]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.RiskManager, true)]
    [InlineData(Roles.Analyst, false)]
    [InlineData(Roles.Auditor, false)]
    [InlineData(Roles.SuperAdmin, false)]
    public void BothActions_AdmitOnlyTheCalibrationReaders(string role, bool allowed)
    {
        foreach (var action in Actions)
        {
            Assert.Equal(Roles.CalibrationReaders, AuthorizationTestHelpers.DeclaredRolesOf(typeof(PersonCalibrationController), action));
            Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(typeof(PersonCalibrationController), action, role));
        }
    }

    [Fact]
    public void Controller_IsReadOnly_AndRequiresTheInternalScheme()
    {
        var methods = typeof(PersonCalibrationController).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), true).Length > 0)
            .ToList();

        Assert.Equal(2, methods.Count);
        Assert.All(methods, m => Assert.NotEmpty(m.GetCustomAttributes(typeof(HttpGetAttribute), true)));

        var scheme = typeof(PersonCalibrationController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .Single(a => a.AuthenticationSchemes is not null);
        Assert.Equal("Internal", scheme.AuthenticationSchemes);
    }

    // ── query validation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-24", "2026-10-01", null, "from")]
    [InlineData("2025-01-01", "2026-10-01", null, "to")]
    [InlineData("01/10/2026", null, null, "from")]
    [InlineData(null, null, "no-es-un-guid", "teamId")]
    public async Task Get_RefusesAMalformedQuery_WithTheValidationErrorShape(string? from, string? to, string? teamId, string field)
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Get_RefusesAMalformedQuery_WithTheValidationErrorShape) + field + from);

        var result = await Sut(f, null).GetAsync(from, to, teamId, Ct);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("validation_error", problem.Extensions["errorCode"]);
        var json = JsonSerializer.Serialize(problem.Extensions["errors"]);
        Assert.Contains($"\"field\":\"{field}\"", json);
    }

    [Fact]
    public async Task Get_AnUnknownTeam_Is404TeamNotFound_EvenOneOfAnotherTenant()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Get_AnUnknownTeam_Is404TeamNotFound_EvenOneOfAnotherTenant));
        var foreign = f.Dashboard.AddTeam(1, [], tenant: DashboardFixture.Other);
        await f.SaveAsync();

        foreach (var teamId in new[] { Guid.NewGuid(), foreign.TeamId })
        {
            var result  = await Sut(f, null).GetAsync(null, null, teamId.ToString(), Ct);
            var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
            Assert.Equal(404, problem.Status);
            Assert.Equal("team_not_found", problem.Extensions["errorCode"]);
        }
    }

    [Fact]
    public async Task Get_ReturnsTheBoard_ForAKnownTeam()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Get_ReturnsTheBoard_ForAKnownTeam));
        var team    = f.Dashboard.AddTeam(1, []);
        var analyst = f.Dashboard.AddUser(Roles.Analyst, teamId: team.TeamId);
        var (a, _)  = f.AddPersonAnalysis(analyst.UserId, createdAtUtc: DateTime.UtcNow.AddDays(-1));
        f.AddSolicitante(a.AnalysisId);
        await f.SaveAsync();

        var result = await Sut(f, null).GetAsync(null, null, team.TeamId.ToString(), Ct);

        var board = Assert.IsType<PersonCalibrationResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(team.TeamId, board.TeamId);
        Assert.Equal(1, board.Summary!.Total);
        Assert.Equal(PersonCalibrationPeriod.DefaultDays, board.To.DayNumber - board.From.DayNumber + 1);
    }

    // ── export ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ReturnsTheCsv_AndAuditsIdsPeriodAndTeamOnly()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Export_ReturnsTheCsv_AndAuditsIdsPeriodAndTeamOnly));
        var team    = f.Dashboard.AddTeam(1, []);
        var admin   = f.Dashboard.AddUser(Roles.Admin);
        var analyst = f.Dashboard.AddUser(Roles.Analyst, teamId: team.TeamId);
        var entity  = f.Dashboard.AddEntity(name: "Juana Pérez");
        var (a, _)  = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Rejected, entityId: entity.EntityId,
            analysis: x => x.ReviewNotes = "motivo confidencial");
        f.AddSolicitante(a.AnalysisId);
        await f.SaveAsync();

        var audit  = new RecordingAuditService();
        var result = await Sut(f, admin.FirebaseUid, audit).ExportAsync("2026-10-01", "2026-10-23", team.TeamId.ToString(), Ct);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal("calibracion_persona_2026-10-01_2026-10-23.csv", file.FileDownloadName);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.FileContents[..3]);
        Assert.Contains("Juana Pérez", System.Text.Encoding.UTF8.GetString(file.FileContents));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PersonCalibrationController.ExportAuditAction, entry.Action);
        Assert.Equal(PersonCalibrationController.ExportAuditEntityType, entry.EntityType);
        Assert.Equal(admin.UserId, entry.UserId);
        Assert.Equal(DashboardFixture.Tenant, entry.TenantId);

        using var payload = JsonDocument.Parse(entry.NewValueJson!);
        Assert.Equal("2026-10-01", payload.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-10-23", payload.RootElement.GetProperty("to").GetString());
        Assert.Equal(team.TeamId, payload.RootElement.GetProperty("teamId").GetGuid());
        Assert.Equal(a.AnalysisId, Assert.Single(payload.RootElement.GetProperty("analysisIds").EnumerateArray()).GetGuid());
        Assert.DoesNotContain("Juana", entry.NewValueJson);
        Assert.DoesNotContain("motivo", entry.NewValueJson);
        Assert.DoesNotContain(entity.CUIT, entry.NewValueJson);
    }

    [Fact]
    public async Task Export_WithoutAUserRowOfThisTenant_Is401_AndAuditsNothing()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Export_WithoutAUserRowOfThisTenant_Is401_AndAuditsNothing));
        var foreignAdmin = f.Dashboard.AddUser(Roles.Admin, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var audit = new RecordingAuditService();

        Assert.IsType<UnauthorizedResult>(await Sut(f, null, audit).ExportAsync(null, null, null, Ct));
        Assert.IsType<UnauthorizedResult>(await Sut(f, foreignAdmin.FirebaseUid, audit).ExportAsync(null, null, null, Ct));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Export_ValidatesTheQuery_BeforeResolvingTheUser()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Export_ValidatesTheQuery_BeforeResolvingTheUser));

        var result  = await Sut(f, null).ExportAsync("2026-10-24", "2026-10-01", null, Ct);
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(400, problem.Status);
    }
}
