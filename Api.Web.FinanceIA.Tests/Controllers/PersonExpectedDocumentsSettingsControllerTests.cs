// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Api.Web.FinanceIA.Tests.Clases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.ExpectedDocumentsTestKit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// GET/PUT /api/bff/settings/person-expected-documents on the REAL service and repositories over an in-memory
/// database: the bodies, the reset, the 400 shape, the audit, and who may read and who may write.
/// </summary>
public sealed class PersonExpectedDocumentsSettingsControllerTests
{
    private const string Tenant = DashboardFixture.Tenant;

    private static readonly CancellationToken Ct     = CancellationToken.None;
    private static readonly Guid              UserId = Guid.NewGuid();

    private static PersonExpectedDocumentsSettingsController Controller(DashboardFixture f, RecordingAuditService? audit = null)
        => new(RealService(f.Context, audit), new TenantContextAccessor(), NullLogger<PersonExpectedDocumentsSettingsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("userId", UserId.ToString()), new Claim("tenantId", Tenant)], "Internal"))
                }
            }
        };

    private static PersonExpectedDocumentsSettingsDto OkBody(IActionResult result)
        => Assert.IsType<PersonExpectedDocumentsSettingsDto>(Assert.IsType<OkObjectResult>(result).Value);

    private static List<(string Field, string Message)> ValidationErrors(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("validation_error", problem.Extensions["errorCode"]);

        var errors = Assert.IsAssignableFrom<System.Collections.IEnumerable>(problem.Extensions["errors"]);
        return errors.Cast<object>()
            .Select(e => ((string)e.GetType().GetProperty("field")!.GetValue(e)!, (string)e.GetType().GetProperty("message")!.GetValue(e)!))
            .ToList();
    }

    // ── GET ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_WithoutARow_IsTheDefaults()
    {
        await using var f = DashboardFixture.Create(nameof(Get_WithoutARow_IsTheDefaults));

        var body = OkBody(await Controller(f).GetAsync(Ct));

        Assert.Equal(ExpectedDocumentRegimes.All, body.Regimes.Select(r => r.Regime));
        Assert.All(body.Regimes, r => Assert.Equal("default", r.Source));
        Assert.Equal(body.Defaults, body.Regimes, new RegimeComparer());
        Assert.Null(body.UpdatedAt);
        Assert.Equal(10, body.DocumentTypes.Count);
    }

    // ── PUT ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_ThenGet_RoundTrips_AndAudits()
    {
        await using var f = DashboardFixture.Create(nameof(Put_ThenGet_RoundTrips_AndAudits));
        var audit   = new RecordingAuditService();
        var request = new UpdatePersonExpectedDocumentsRequest
        {
            Regimes = new() { [ExpectedDocumentRegimes.Monotributo] = CustomMonotributo() }
        };

        var put = OkBody(await Controller(f, audit).UpdateAsync(request, Ct));
        var get = OkBody(await Controller(f).GetAsync(Ct));

        Assert.Equal(put.Regimes, get.Regimes, new RegimeComparer());
        var mono = get.Regimes[0];
        Assert.Equal("tenant", mono.Source);
        Assert.DoesNotContain(mono.Lines, l => l.DocumentType == DocumentTypes.DdjjIibb);
        Assert.Equal((3, 3), (mono.Lines[2].MaxAgeMonths, mono.Lines[2].EffectiveMaxAgeMonths));
        Assert.Equal((null, 6), (mono.Lines[1].MaxAgeMonths, mono.Lines[1].EffectiveMaxAgeMonths));   // inherits pf_doc
        Assert.Equal("default", get.Regimes[1].Source);
        Assert.NotNull(get.UpdatedAt);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Settings.UpdatePersonExpectedDocuments", entry.Action);
        Assert.Equal(Tenant, entry.TenantId);
    }

    [Fact]
    public async Task Put_Reset_DeletesTheRow()
    {
        await using var f = DashboardFixture.Create(nameof(Put_Reset_DeletesTheRow));
        Seed(f.Context, Tenant, CustomMonotributoJson());
        var audit = new RecordingAuditService();

        var body = OkBody(await Controller(f, audit).UpdateAsync(new UpdatePersonExpectedDocumentsRequest { Reset = true }, Ct));

        Assert.All(body.Regimes, r => Assert.Equal("default", r.Source));
        Assert.False(await f.Context.TenantSettings.AnyAsync(s => s.SettingKey == TenantSettingKeys.PersonExpectedDocuments));
        Assert.Equal("Settings.UpdatePersonExpectedDocuments", Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task Put_Invalid_Is400ValidationError_WithEveryPath_AndWritesNothing()
    {
        await using var f = DashboardFixture.Create(nameof(Put_Invalid_Is400ValidationError_WithEveryPath_AndWritesNothing));
        var audit   = new RecordingAuditService();
        var request = new UpdatePersonExpectedDocumentsRequest
        {
            Regimes = new()
            {
                [ExpectedDocumentRegimes.Monotributo] =
                [
                    Line(DocumentTypes.AutorizacionDatos),
                    Line(DocumentTypes.ManifestacionBienes),
                    Line(DocumentTypes.Otro)
                ],
                ["Exento"] = [Line(DocumentTypes.AutorizacionDatos)]
            }
        };

        var errors = ValidationErrors(await Controller(f, audit).UpdateAsync(request, Ct));

        Assert.Equal(
            [
                ("regimes.Monotributo[2].documentType", "El tipo de documento no es uno de los tipos configurables del legajo."),
                ("regimes.Exento", "Régimen desconocido. Los regímenes válidos son Monotributo, ResponsableInscripto y ExentoOrDesconocido.")
            ],
            errors);
        Assert.Empty(audit.Entries);
        Assert.False(await f.Context.TenantSettings.AnyAsync());
    }

    [Fact]
    public async Task Put_ResetTogetherWithRegimes_IsRefused()
    {
        await using var f = DashboardFixture.Create(nameof(Put_ResetTogetherWithRegimes_IsRefused));
        var request = new UpdatePersonExpectedDocumentsRequest
        {
            Reset   = true,
            Regimes = new() { [ExpectedDocumentRegimes.Monotributo] = CustomMonotributo() }
        };

        Assert.Equal([("reset", "Enviá reset o regimes, no ambos.")], ValidationErrors(await Controller(f).UpdateAsync(request, Ct)));
    }

    [Fact]
    public async Task Put_WithoutABody_OrRegimes_IsRefused()
    {
        await using var f = DashboardFixture.Create(nameof(Put_WithoutABody_OrRegimes_IsRefused));

        Assert.Equal("regimes", Assert.Single(ValidationErrors(await Controller(f).UpdateAsync(null, Ct))).Field);
        Assert.Equal(
            [("regimes", "Se requiere al menos un régimen.")],
            ValidationErrors(await Controller(f).UpdateAsync(new UpdatePersonExpectedDocumentsRequest { Reset = false }, Ct)));
    }

    // ── Who ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.RiskManager, true)]
    [InlineData(Roles.Auditor, true)]
    [InlineData(Roles.SuperAdmin, true)]
    [InlineData(Roles.Analyst, false)]
    public void Get_IsForTheTenantConfigReaders(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(PersonExpectedDocumentsSettingsController), nameof(PersonExpectedDocumentsSettingsController.GetAsync), role));
    }

    [Theory]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.RiskManager, false)]
    [InlineData(Roles.Auditor, false)]
    [InlineData(Roles.SuperAdmin, false)]
    [InlineData(Roles.Analyst, false)]
    public void Put_IsAdminOnly(string role, bool allowed)
    {
        Assert.Equal(allowed, AuthorizationTestHelpers.RoleAllowed(
            typeof(PersonExpectedDocumentsSettingsController), nameof(PersonExpectedDocumentsSettingsController.UpdateAsync), role));
    }

    [Fact]
    public void TheRoute_SitsUnderTheSettingsPrefix()
    {
        var route = Assert.Single(typeof(PersonExpectedDocumentsSettingsController).GetCustomAttributes(typeof(RouteAttribute), false));
        Assert.Equal("api/bff/settings/person-expected-documents", ((RouteAttribute)route).Template);
    }

    private sealed class RegimeComparer : IEqualityComparer<PersonExpectedDocumentsRegimeDto>
    {
        public bool Equals(PersonExpectedDocumentsRegimeDto? x, PersonExpectedDocumentsRegimeDto? y)
            => x is not null && y is not null && x.Regime == y.Regime && x.Source == y.Source
            && x.Lines.Count == y.Lines.Count
            && x.Lines.Zip(y.Lines).All(p =>
                p.First.DocumentType == p.Second.DocumentType && p.First.Requirement == p.Second.Requirement
                && p.First.MaxAgeMonths == p.Second.MaxAgeMonths && p.First.EffectiveMaxAgeMonths == p.Second.EffectiveMaxAgeMonths
                && p.First.AgeClass == p.Second.AgeClass && p.First.Alternatives.SequenceEqual(p.Second.Alternatives));

        public int GetHashCode(PersonExpectedDocumentsRegimeDto obj) => obj.Regime.GetHashCode(StringComparison.Ordinal);
    }
}
