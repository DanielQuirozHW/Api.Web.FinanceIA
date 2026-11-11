// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// sgr: the Fondo de Riesgo against the saldo de garantías vigentes, for a tenant in creditline.mode "sgr".
/// Vigente and Vencida are CreditLineRules.EffectiveStatus's; USD counts only through the tenant's own rate.
/// </summary>
public sealed partial class DashboardServicePortfolioRiskTests
{
    private static readonly DateTime Future = Now.Date.AddDays(30);
    private static readonly DateTime Past   = Now.Date.AddDays(-1);

    private static void Fondo(DashboardFixture f, string amount = "1000000", string fecha = "2026-09-30", string tenant = Tenant)
    {
        f.AddSetting(TenantSettingKeys.SgrFondoRiesgoDisponible, amount, tenant);
        f.AddSetting(TenantSettingKeys.SgrFondoRiesgoFecha, fecha, tenant);
    }

    private static void Rate(DashboardFixture f, string rate = "1000", string fecha = "2026-10-22")
    {
        f.AddSetting(TenantSettingKeys.SgrTipoCambioUsd, rate);
        f.AddSetting(TenantSettingKeys.SgrTipoCambioFecha, fecha);
    }

    [Fact]
    public async Task Sgr_InSimpleMode_IsNull_AndNotDegraded()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_InSimpleMode_IsNull_AndNotDegraded));
        f.AddSetting(TenantSettingKeys.CreditLineMode, TenantSettingKeys.CreditLineModeSimple);
        Fondo(f);
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId), 100m, validUntil: Future);
        await f.SaveAsync();

        var response = await PortfolioAsync(f);

        Assert.Null(response.Sgr);
        Assert.Empty(response.DegradedSections);
    }

    [Fact]
    public async Task Sgr_WithNoModeRow_IsTheSgrDefault()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_WithNoModeRow_IsTheSgrDefault));
        await f.SaveAsync();

        Assert.NotNull((await PortfolioAsync(f)).Sgr);
    }

    [Fact]
    public async Task Sgr_NotConfigured_GivesTheSaldoAndVencidas_AndNullsEveryFondoFigure()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_NotConfigured_GivesTheSaldoAndVencidas_AndNullsEveryFondoFigure));
        var line = f.AddCreditLine(f.AddEntity().EntityId, amount: 10_000_000m);
        f.AddOperation(line, 2_000_000m, validUntil: Future);
        f.AddOperation(line, 300m, validUntil: Past);
        f.AddSetting(TenantSettingKeys.SgrFondoRiesgoDisponible, "not-a-number");   // unusable: not configured
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.False(sgr.Configured);
        Assert.Null(sgr.FondoRiesgoDisponible);
        Assert.Null(sgr.FondoRiesgoFecha);
        Assert.Null(sgr.GradoUtilizacion);
        Assert.Null(sgr.LimitePorSocio);
        Assert.Null(sgr.OverLimitCount);
        Assert.Null(sgr.OverLimit);
        Assert.Equal(2_000_000m, sgr.SaldoGarantiasVigentesArs);
        Assert.Equal((1, 300m), (sgr.VencidasSinCerrarCount, sgr.VencidasSinCerrarArs));
        Assert.False(sgr.MoraDisponible);
    }

    [Fact]
    public async Task Sgr_ArsOnly_CountsEffectiveVigente_ExcludesAprobadaAndCancelada_AndReportsVencidaApart()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_ArsOnly_CountsEffectiveVigente_ExcludesAprobadaAndCancelada_AndReportsVencidaApart));
        Fondo(f);
        var line = f.AddCreditLine(f.AddEntity().EntityId, amount: 10_000_000m);
        f.AddOperation(line, 100_000m, validUntil: Future);                                   // Vigente
        f.AddOperation(line, 50_000m);                                                        // Vigente, open-ended
        f.AddOperation(line, 10_000m, validUntil: Now.Date);                                  // Vigente on its last day
        f.AddOperation(line, 70_000m, status: CreditLineRules.Aprobada, validUntil: Future);  // not issued
        f.AddOperation(line, 80_000m, status: CreditLineRules.Aprobada, validUntil: Past);    // Aprobada never expires
        f.AddOperation(line, 20_000m, validUntil: Past);                                      // Vencida
        f.AddOperation(line, 999_999m, status: CreditLineRules.Cancelada, validUntil: Future);
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, status: "Suspended"), 5_000m, validUntil: Future); // not Active
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.True(sgr.Configured);
        Assert.Equal((1_000_000m, new DateOnly(2026, 9, 30)), (sgr.FondoRiesgoDisponible!.Value, sgr.FondoRiesgoFecha!.Value));
        Assert.Equal(160_000m, sgr.SaldoGarantiasVigentesArs);
        Assert.Equal(0.16m, sgr.GradoUtilizacion);
        Assert.Equal(50_000m, sgr.LimitePorSocio);
        Assert.Equal((1, 20_000m), (sgr.VencidasSinCerrarCount, sgr.VencidasSinCerrarArs));
        Assert.True(sgr.UsdIncluded is false && sgr.UsdExcludedAmount == 0m);
        Assert.Equal(1, sgr.OverLimitCount);
        Assert.Equal((160_000m, 16.0m), (sgr.OverLimit![0].ExposicionArs, sgr.OverLimit[0].PctDelFondo));
    }

    [Fact]
    public async Task Sgr_UsdWithARate_IsConvertedIntoEveryArsFigure()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_UsdWithARate_IsConvertedIntoEveryArsFigure));
        Fondo(f);
        Rate(f, "1000.50");
        var entity = f.AddEntity();
        f.AddOperation(f.AddCreditLine(entity.EntityId, "ARS", 10_000_000m), 30_000m, validUntil: Future);
        var usd = f.AddCreditLine(entity.EntityId, "USD", 1_000m);
        f.AddOperation(usd, 20m, validUntil: Future);
        f.AddOperation(usd, 2m, validUntil: Past);
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.True(sgr.UsdIncluded);
        Assert.Null(sgr.UsdExcludedAmount);
        Assert.Equal((1000.50m, new DateOnly(2026, 10, 22)), (sgr.TipoCambioUsd!.Value, sgr.TipoCambioFecha!.Value));
        Assert.Equal(50_010m, sgr.SaldoGarantiasVigentesArs);
        Assert.Equal((1, 2_001m), (sgr.VencidasSinCerrarCount, sgr.VencidasSinCerrarArs));
        // One socio: its ARS and its converted USD guarantees are one exposure.
        var socio = Assert.Single(sgr.OverLimit!);
        Assert.Equal((entity.EntityId, 50_010m, 5.0m), (socio.EntityId, socio.ExposicionArs, socio.PctDelFondo));
    }

    [Fact]
    public async Task Sgr_UsdWithoutARate_IsExcluded_AndReportedInUsd()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_UsdWithoutARate_IsExcluded_AndReportedInUsd));
        Fondo(f);
        var entity = f.AddEntity();
        f.AddOperation(f.AddCreditLine(entity.EntityId, "ARS", 10_000_000m), 30_000m, validUntil: Future);
        var usd = f.AddCreditLine(entity.EntityId, "USD", 1_000m);
        f.AddOperation(usd, 20m, validUntil: Future);
        f.AddOperation(usd, 5m, status: CreditLineRules.Aprobada);
        f.AddOperation(usd, 2m, validUntil: Past);
        var eur = f.AddCreditLine(entity.EntityId, "EUR", 1_000m);
        f.AddOperation(eur, 9m, validUntil: Future);
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.False(sgr.UsdIncluded);
        Assert.Null(sgr.TipoCambioUsd);
        Assert.Equal(20m, sgr.UsdExcludedAmount);
        Assert.Equal(30_000m, sgr.SaldoGarantiasVigentesArs);
        Assert.Equal((0, 0m), (sgr.VencidasSinCerrarCount, sgr.VencidasSinCerrarArs));
        Assert.Equal(0, sgr.OverLimitCount);
        Assert.Empty(sgr.OverLimit!);
    }

    [Fact]
    public async Task Sgr_ExactlyFivePercent_IsNotOver_ACentMoreIs()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_ExactlyFivePercent_IsNotOver_ACentMoreIs));
        Fondo(f);   // 1,000,000 → limit 50,000
        var atLimit = f.AddEntity();
        var over    = f.AddEntity();
        f.AddOperation(f.AddCreditLine(atLimit.EntityId, amount: 10_000_000m), 50_000m, validUntil: Future);
        f.AddOperation(f.AddCreditLine(over.EntityId, amount: 10_000_000m), 50_000.01m, validUntil: Future);
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.Equal(1, sgr.OverLimitCount);
        var item = Assert.Single(sgr.OverLimit!);
        Assert.Equal((over.EntityId, over.BusinessName, over.CUIT, 50_000.01m, 5.0m),
            (item.EntityId, item.EntityName, item.EntityCuit, item.ExposicionArs, item.PctDelFondo));
    }

    [Fact]
    public async Task Sgr_OverLimit_IsCappedAtTen_ByExposureDescending_WithTheWholeCount()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_OverLimit_IsCappedAtTen_ByExposureDescending_WithTheWholeCount));
        Fondo(f);
        for (var i = 1; i <= 12; i++)
            f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, amount: 10_000_000m), 60_000m + i * 1_000m, validUntil: Future);
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.Equal(12, sgr.OverLimitCount);
        Assert.Equal(
            Enumerable.Range(3, 10).Reverse().Select(i => 60_000m + i * 1_000m),
            sgr.OverLimit!.Select(o => o.ExposicionArs));
        Assert.Equal(7.2m, sgr.OverLimit![0].PctDelFondo);
    }

    [Fact]
    public async Task Sgr_IsTenantIsolated_OnOperationsAndSettings()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_IsTenantIsolated_OnOperationsAndSettings));
        Fondo(f, amount: "5000000", tenant: Other);   // another tenant's fund is not mine
        var line = f.AddCreditLine(f.AddEntity().EntityId, amount: 10_000_000m);
        f.AddOperation(line, 100m, validUntil: Future);
        f.AddOperation(line, 9_000m, validUntil: Future, tenant: Other);              // tenant-2 row, tenant-1 cupo id
        var theirs = f.AddCreditLine(f.AddEntity(tenant: Other).EntityId, amount: 1_000m, tenant: Other);
        f.AddOperation(theirs, 700m, validUntil: Future);
        await f.SaveAsync();

        var mine  = (await PortfolioAsync(f)).Sgr!;
        var other = (await f.Service().GetPortfolioAsync(Other, Now, Ct)).Sgr!;

        Assert.False(mine.Configured);
        Assert.Equal(100m, mine.SaldoGarantiasVigentesArs);
        Assert.True(other.Configured);
        Assert.Equal(700m, other.SaldoGarantiasVigentesArs);
    }

    [Fact]
    public async Task Sgr_RoundsOnlyTheShownFigures()
    {
        await using var f = DashboardFixture.Create(nameof(Sgr_RoundsOnlyTheShownFigures));
        Fondo(f, amount: "3000000");
        Rate(f, "1234.567");
        f.AddOperation(f.AddCreditLine(f.AddEntity().EntityId, "USD", 1_000m), 0.5m, validUntil: Future);
        await f.SaveAsync();

        var sgr = (await PortfolioAsync(f)).Sgr!;

        Assert.Equal(617.28m, sgr.SaldoGarantiasVigentesArs);   // 617.2835
        Assert.Equal(0.00m, sgr.GradoUtilizacion);
        Assert.Equal(150_000m, sgr.LimitePorSocio);
    }
}
