// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The engine 0.6 persona humana keys through the Settings contract: the new defaults, the cuota estimate's
/// narrower band, the consumo base month bounded by the current month, the four new haircuts, and the retired
/// pf_plazo_estimado_deuda_meses — refused on a PUT and inert when a tenant still has a row of it.
/// </summary>
public sealed partial class SettingsServicePersonEvaluationTests
{
    private static int CurrentMonth()
        => PersonEvaluationDefaults.ToYearMonth(DateOnly.FromDateTime(ArgentineTime.ToLocal(DateTime.UtcNow)));

    private static int NextMonth()
        => PersonEvaluationDefaults.ToYearMonth(
            DateOnly.FromDateTime(ArgentineTime.ToLocal(DateTime.UtcNow)).AddMonths(1));

    [Theory]
    [InlineData("pf_dscr_rojo",                1.0)]
    [InlineData("pf_dscr_verde",               1.25)]
    [InlineData("pf_cuota_estimada_pct_saldo", 0.05)]
    [InlineData("pf_consumo_mes_base",         202609)]
    [InlineData("pf_haircut_deposito",         0.0)]
    [InlineData("pf_haircut_inversion",        0.3)]
    [InlineData("pf_haircut_credito",          1.0)]
    [InlineData("pf_haircut_otro",             1.0)]
    public async Task V06Defaults_AreWhatGetReturns(string key, double expected)
    {
        var (sut, _) = Build();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        var item = Assert.Single(dto.PersonaHumana, i => i.Key == key);
        Assert.Equal((decimal)expected, item.DefaultValue);
        Assert.Equal((decimal)expected, item.Value);
    }

    [Fact]
    public async Task ConsumoBaseMonth_IsAnIntegerMonth_UpToTheCurrentOne()
    {
        var (sut, _) = Build();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        var item = Assert.Single(dto.PersonaHumana, i => i.Key == "pf_consumo_mes_base");
        Assert.Equal("integer", item.Kind);
        Assert.Equal(202001m, item.Min);
        Assert.Equal(CurrentMonth(), item.Max);
        Assert.Equal("yyyymm", item.Unit);
        Assert.Equal("capacidad", item.Group);
    }

    [Fact]
    public async Task ConsumoBaseMonth_SitsRightAfterTheFloor_AndTheCuotaEstimateWhereThePlazoWas()
    {
        var (sut, _) = Build();

        var keys = (await sut.GetScoringConfigAsync(Tenant, CancellationToken.None)).PersonaHumana.Select(i => i.Key).ToList();

        Assert.Equal(keys.IndexOf("pf_consumo_mensual_minimo_miles") + 1, keys.IndexOf("pf_consumo_mes_base"));
        Assert.Equal(keys.IndexOf("pf_cuota_ingreso_alto") + 1, keys.IndexOf("pf_cuota_estimada_pct_saldo"));
        Assert.Equal(keys.IndexOf("pf_cuota_estimada_pct_saldo") + 1, keys.IndexOf("pf_dscr_rojo"));
        Assert.Equal(keys.IndexOf("pf_haircut_otro") + 1, keys.IndexOf("pf_haircut_descalce_moneda"));
    }

    [Theory]
    [InlineData(202001)]
    [InlineData(202512)]
    public async Task ConsumoBaseMonth_InsideItsBounds_IsAccepted(int value)
    {
        var (sut, db) = Build();

        Assert.NotNull(await Update(sut, ("pf_consumo_mes_base", value)));

        var row = Assert.Single(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_consumo_mes_base");
        Assert.Equal(value, row.ConfigValue);
    }

    [Fact]
    public async Task ConsumoBaseMonth_TheCurrentMonth_IsAccepted()
    {
        var (sut, _) = Build();

        Assert.NotNull(await Update(sut, ("pf_consumo_mes_base", CurrentMonth())));
    }

    [Fact]
    public async Task ConsumoBaseMonth_NextMonth_IsRefused_WithAnEsArMessage()
    {
        var (sut, _) = Build();
        var next = NextMonth();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_consumo_mes_base", next)));

        Assert.Equal(
            $"El valor de pf_consumo_mes_base tiene que ser un mes AAAAMM entre 202001 y {CurrentMonth()} (recibido: {next}).",
            ex.Message);
    }

    [Theory]
    [InlineData("201912")]
    [InlineData("202613")]
    [InlineData("202609.5")]
    public async Task ConsumoBaseMonth_NotAMonthInRange_IsRefused_WithAnEsArMessage(string value)
    {
        var (sut, _) = Build();
        var parsed = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_consumo_mes_base", parsed)));

        Assert.StartsWith(
            $"El valor de pf_consumo_mes_base tiene que ser un mes AAAAMM entre 202001 y {CurrentMonth()} (recibido: ",
            ex.Message);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.2)]
    public async Task CuotaEstimate_AtTheEdgesOfItsBand_IsAccepted(double value)
    {
        var (sut, _) = Build();

        Assert.NotNull(await Update(sut, ("pf_cuota_estimada_pct_saldo", (decimal)value)));
    }

    [Fact]
    public async Task NewHaircuts_InsideTheirBounds_AreAccepted()
    {
        var (sut, _) = Build();

        var dto = await Update(sut,
            ("pf_haircut_deposito", 0.1m), ("pf_haircut_inversion", 0m), ("pf_haircut_credito", 0.5m), ("pf_haircut_otro", 1m));

        Assert.NotNull(dto);
        Assert.Equal(0.5m, Assert.Single(dto.PersonaHumana, i => i.Key == "pf_haircut_credito").Value);
    }

    // ── The retired pf_plazo_estimado_deuda_meses ───────────────────────────────

    [Fact]
    public async Task TheRetiredPlazoKey_IsNoLongerAccepted()
    {
        var (sut, _) = Build();

        Assert.Null(await Update(sut, ("pf_plazo_estimado_deuda_meses", 24m)));
    }

    [Fact]
    public async Task AStoredRowOfTheRetiredPlazoKey_IsIgnored_ByGet()
    {
        var (sut, _) = Build(SeedRow("pf_plazo_estimado_deuda_meses", 36m));

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        Assert.DoesNotContain(dto.PersonaHumana, i => i.Key == "pf_plazo_estimado_deuda_meses");
        Assert.All(dto.PersonaHumana, i => Assert.False(i.IsOverride));
    }

    [Fact]
    public async Task AStoredRowOfTheRetiredPlazoKey_DoesNotBlockAPfPut()
    {
        var (sut, db) = Build(SeedRow("pf_plazo_estimado_deuda_meses", 36m));

        var dto = await Update(sut, ("pf_dscr_verde", 1.4m));

        Assert.NotNull(dto);
        Assert.Equal(1.4m, Assert.Single(dto.PersonaHumana, i => i.Key == "pf_dscr_verde").Value);
        // The row stays as it was: inert, never rewritten.
        Assert.Equal(36m, Assert.Single(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_plazo_estimado_deuda_meses").ConfigValue);
    }

    [Fact]
    public void AStoredRowOfTheRetiredPlazoKey_IsIgnored_ByTheEngineParameters()
    {
        var parameters = PersonEvaluationParameters.From(new Dictionary<string, decimal>
        {
            ["pf_plazo_estimado_deuda_meses"] = 36m
        });

        Assert.Null(parameters.PlazoEstimadoDeudaMeses);
        Assert.Empty(parameters.TenantOverrides);
        Assert.Equal(0.05m, parameters.ResolvedCuotaEstimadaPctSaldo);
    }
}
