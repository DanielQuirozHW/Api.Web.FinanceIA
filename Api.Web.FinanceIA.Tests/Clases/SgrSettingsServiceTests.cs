// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The SGR Fondo de Riesgo settings: <see cref="SgrSettingsService"/> over the REAL TenantSettings repository on
/// an in-memory database — validation, the round trip, clearing the exchange rate, the audit trail — and
/// GET/PUT /api/bff/settings/sgr on top of it.
/// </summary>
public sealed class SgrSettingsServiceTests
{
    private const string Tenant = DashboardFixture.Tenant;

    private static readonly CancellationToken Ct      = CancellationToken.None;
    private static readonly DateTime          Now     = DashboardFixture.Now;     // 23/10/2026 in Buenos Aires
    private static readonly DateOnly          TodayAr = new(2026, 10, 23);
    private static readonly Guid              UserId  = Guid.NewGuid();

    private static UpdateSgrSettingsRequest Valid() => new()
    {
        FondoRiesgoDisponible = 1_500_000_000.50m,
        FondoRiesgoFecha      = new DateOnly(2026, 9, 30),
        TipoCambioUsd         = 1450.5m,
        TipoCambioFecha       = TodayAr
    };

    private static async Task<Dictionary<string, string>> RowsAsync(DashboardFixture f, string tenant = Tenant)
    {
        f.Context.ChangeTracker.Clear();
        return await f.Context.TenantSettings.AsNoTracking()
            .Where(s => s.TenantId == tenant)
            .ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue);
    }

    [Fact]
    public async Task Update_ThenGet_RoundTrips_StoringInvariantText()
    {
        await using var f = DashboardFixture.Create(nameof(Update_ThenGet_RoundTrips_StoringInvariantText));
        var audit = new RecordingAuditService();

        var saved = await f.SgrSettings(audit).UpdateAsync(UserId, Tenant, Valid(), Now, "1.2.3.4", "req", Ct);
        var read  = await f.SgrSettings().GetAsync(Tenant, Ct);

        Assert.Equal(saved, read);
        Assert.Equal(new SgrFundSettings(1_500_000_000.50m, new DateOnly(2026, 9, 30), 1450.5m, TodayAr), read);
        Assert.Equal(new Dictionary<string, string>
        {
            [TenantSettingKeys.SgrFondoRiesgoDisponible] = "1500000000.50",
            [TenantSettingKeys.SgrFondoRiesgoFecha]      = "2026-09-30",
            [TenantSettingKeys.SgrTipoCambioUsd]         = "1450.5",
            [TenantSettingKeys.SgrTipoCambioFecha]       = "2026-10-23"
        }, await RowsAsync(f));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal((Tenant, (Guid?)UserId, "Settings.UpdateSgrFundSettings", "TenantSettings", "sgr"),
            (entry.TenantId, entry.UserId, entry.Action, entry.EntityType, entry.EntityId));
        Assert.Contains("\"fondoRiesgoDisponible\":null", entry.OldValueJson);
        Assert.Contains("\"fondoRiesgoDisponible\":1500000000.50", entry.NewValueJson);
    }

    [Fact]
    public async Task Update_WithoutARate_RemovesTheStoredOne_AndAuditsTheChange()
    {
        await using var f = DashboardFixture.Create(nameof(Update_WithoutARate_RemovesTheStoredOne_AndAuditsTheChange));
        await f.SgrSettings().UpdateAsync(UserId, Tenant, Valid(), Now, null, null, Ct);
        var audit = new RecordingAuditService();

        await f.SgrSettings(audit).UpdateAsync(UserId, Tenant,
            Valid() with { FondoRiesgoDisponible = 2_000m, TipoCambioUsd = null, TipoCambioFecha = null }, Now, null, null, Ct);

        Assert.Equal(new SgrFundSettings(2_000m, new DateOnly(2026, 9, 30), null, null), await f.SgrSettings().GetAsync(Tenant, Ct));
        Assert.Equal(
            [TenantSettingKeys.SgrFondoRiesgoDisponible, TenantSettingKeys.SgrFondoRiesgoFecha],
            (await RowsAsync(f)).Keys.Order(StringComparer.Ordinal));
        Assert.Contains("\"tipoCambioUsd\":1450.5", Assert.Single(audit.Entries).OldValueJson);
    }

    [Fact]
    public async Task Update_LeavesOtherSettingsAndOtherTenantsAlone()
    {
        await using var f = DashboardFixture.Create(nameof(Update_LeavesOtherSettingsAndOtherTenantsAlone));
        f.AddSetting(TenantSettingKeys.CreditLineMode, "sgr");
        f.AddSetting(TenantSettingKeys.SgrTipoCambioUsd, "99", DashboardFixture.Other);
        await f.SaveAsync();

        await f.SgrSettings().UpdateAsync(UserId, Tenant, Valid() with { TipoCambioUsd = null, TipoCambioFecha = null }, Now, null, null, Ct);

        Assert.Equal("sgr", (await RowsAsync(f))[TenantSettingKeys.CreditLineMode]);
        Assert.Equal("99", (await RowsAsync(f, DashboardFixture.Other))[TenantSettingKeys.SgrTipoCambioUsd]);
        Assert.Null((await f.SgrSettings().GetAsync(Tenant, Ct)).TipoCambioUsd);
    }

    public static TheoryData<string, UpdateSgrSettingsRequest> Invalid => new()
    {
        { "fondo missing",          Valid() with { FondoRiesgoDisponible = null } },
        { "fondo zero",             Valid() with { FondoRiesgoDisponible = 0m } },
        { "fondo negative",         Valid() with { FondoRiesgoDisponible = -1m } },
        { "fondo date missing",     Valid() with { FondoRiesgoFecha = null } },
        { "fondo date tomorrow",    Valid() with { FondoRiesgoFecha = TodayAr.AddDays(1) } },
        { "rate without its date",  Valid() with { TipoCambioFecha = null } },
        { "date without its rate",  Valid() with { TipoCambioUsd = null } },
        { "rate zero",              Valid() with { TipoCambioUsd = 0m } },
        { "rate date tomorrow",     Valid() with { TipoCambioFecha = TodayAr.AddDays(1) } }
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Update_RejectsAnInvalidRequest_WritingAndAuditingNothing(string because, UpdateSgrSettingsRequest request)
    {
        await using var f = DashboardFixture.Create(nameof(Update_RejectsAnInvalidRequest_WritingAndAuditingNothing) + because);
        var audit = new RecordingAuditService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => f.SgrSettings(audit).UpdateAsync(UserId, Tenant, request, Now, null, null, Ct));

        Assert.DoesNotContain("Parameter", ex.Message);
        Assert.Empty(await RowsAsync(f));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Update_TodayIsTheArgentineDate()
    {
        await using var f = DashboardFixture.Create(nameof(Update_TodayIsTheArgentineDate));
        // 02:00 UTC on 24/10 is still 23/10 in Buenos Aires: a fund stated on 24/10 is in the future.
        var lateNight = new DateTime(2026, 10, 24, 2, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<ArgumentException>(() => f.SgrSettings().UpdateAsync(
            UserId, Tenant, Valid() with { FondoRiesgoFecha = new DateOnly(2026, 10, 24) }, lateNight, null, null, Ct));

        var saved = await f.SgrSettings().UpdateAsync(
            UserId, Tenant, Valid() with { FondoRiesgoFecha = TodayAr }, lateNight, null, null, Ct);
        Assert.Equal(TodayAr, saved.FondoRiesgoFecha);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1,500")]
    [InlineData("abc")]
    public async Task Get_AnUnusableStoredAmount_IsNotConfigured(string stored)
    {
        await using var f = DashboardFixture.Create(nameof(Get_AnUnusableStoredAmount_IsNotConfigured) + stored);
        f.AddSetting(TenantSettingKeys.SgrFondoRiesgoDisponible, stored);
        f.AddSetting(TenantSettingKeys.SgrFondoRiesgoFecha, "30/09/2026");   // not ISO: not configured either
        await f.SaveAsync();

        Assert.Equal(SgrFundSettings.Empty, await f.SgrSettings().GetAsync(Tenant, Ct));
    }

    // ── GET/PUT /api/bff/settings/sgr ─────────────────────────────────────────

    private static SettingsController Controller(DashboardFixture f)
    {
        var controller = new SettingsController(
            null!,    // ISettingsService
            null!,    // IUserInvitationService
            new TenantContextAccessor(),
            null!,    // IUserRepository
            null!,    // IUserApprovalLimitRepository
            null!,    // ITeamRepository
            f.SgrSettings(),
            NullLogger<SettingsController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("userId", UserId.ToString()), new Claim("tenantId", Tenant)], "Internal"))
            }
        };
        return controller;
    }

    [Fact]
    public async Task Endpoints_PutThenGet_RoundTrip()
    {
        await using var f = DashboardFixture.Create(nameof(Endpoints_PutThenGet_RoundTrip));
        var request = Valid() with { FondoRiesgoFecha = new DateOnly(2026, 1, 31), TipoCambioFecha = new DateOnly(2026, 1, 31) };

        var put = Assert.IsType<OkObjectResult>(await Controller(f).UpdateSgrSettings(request, Ct));
        var get = Assert.IsType<OkObjectResult>(await Controller(f).GetSgrSettings(Ct));

        var expected = new SgrSettingsDto
        {
            FondoRiesgoDisponible = request.FondoRiesgoDisponible,
            FondoRiesgoFecha      = request.FondoRiesgoFecha,
            TipoCambioUsd         = request.TipoCambioUsd,
            TipoCambioFecha       = request.TipoCambioFecha
        };
        Assert.Equal(expected, put.Value);
        Assert.Equal(expected, get.Value);
    }

    [Fact]
    public async Task Endpoints_GetWithNothingStored_IsAllNull()
    {
        await using var f = DashboardFixture.Create(nameof(Endpoints_GetWithNothingStored_IsAllNull));

        var get = Assert.IsType<OkObjectResult>(await Controller(f).GetSgrSettings(Ct));

        Assert.Equal(SgrFundSettings.Empty.ToDto(), get.Value);
    }

    [Fact]
    public async Task Endpoints_AnInvalidPut_IsA400WithTheSpanishDetail()
    {
        await using var f = DashboardFixture.Create(nameof(Endpoints_AnInvalidPut_IsA400WithTheSpanishDetail));

        var result = Assert.IsType<ObjectResult>(
            await Controller(f).UpdateSgrSettings(Valid() with { FondoRiesgoDisponible = 0m, TipoCambioUsd = null }, Ct));

        Assert.Equal(400, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(
            "fondoRiesgoDisponible es obligatorio y debe ser mayor a cero. " +
            "tipoCambioUsd y tipoCambioFecha se informan juntos; envialos ambos vacíos para quitar el tipo de cambio.",
            problem.Detail);
        Assert.Empty(await RowsAsync(f));
    }
}
