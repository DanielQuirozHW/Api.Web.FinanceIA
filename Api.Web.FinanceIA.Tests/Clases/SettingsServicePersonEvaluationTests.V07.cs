// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The engine 0.7 persona humana keys through the Settings contract: the agro annual mode flag, the agro cuotas
/// a year (a closed set: 1, 2, 4, 12) and the campaign's first month — their defaults, group, place in the
/// layout, the es-AR refusals, and how the engine parameters resolve them.
/// </summary>
public sealed partial class SettingsServicePersonEvaluationTests
{
    [Theory]
    [InlineData("pf_agro_modo_anual",          1)]
    [InlineData("pf_agro_cuotas_por_anio",     2)]
    [InlineData("pf_agro_mes_inicio_campania", 7)]
    public async Task V07Defaults_AreWhatGetReturns_InTheCapacidadGroup(string key, int expected)
    {
        var (sut, _) = Build();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        var item = Assert.Single(dto.PersonaHumana, i => i.Key == key);
        Assert.Equal(expected, item.DefaultValue);
        Assert.Equal(expected, item.Value);
        Assert.False(item.IsOverride);
        Assert.Equal("capacidad", item.Group);
        Assert.Null(item.Unit);
        Assert.StartsWith("PROVISIONAL", item.Description);
    }

    [Fact]
    public async Task V07Keys_SitRightAfterTheDscrBands_InThisOrder()
    {
        var (sut, _) = Build();

        var keys = (await sut.GetScoringConfigAsync(Tenant, CancellationToken.None)).PersonaHumana.Select(i => i.Key).ToList();

        Assert.Equal(keys.IndexOf("pf_dscr_verde") + 1, keys.IndexOf("pf_agro_modo_anual"));
        Assert.Equal(keys.IndexOf("pf_agro_modo_anual") + 1, keys.IndexOf("pf_agro_cuotas_por_anio"));
        Assert.Equal(keys.IndexOf("pf_agro_cuotas_por_anio") + 1, keys.IndexOf("pf_agro_mes_inicio_campania"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(12)]
    public async Task AgroCuotasPorAnio_OfTheAllowedSet_IsAccepted(int value)
    {
        var (sut, db) = Build();

        Assert.NotNull(await Update(sut, ("pf_agro_cuotas_por_anio", value)));

        var row = Assert.Single(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_agro_cuotas_por_anio");
        Assert.Equal(value, row.ConfigValue);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("6")]
    [InlineData("11")]
    public async Task AgroCuotasPorAnio_InsideTheBoundsButOutsideTheSet_IsRefused_WithAnEsArMessage(string value)
    {
        var (sut, db) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Update(sut, ("pf_agro_cuotas_por_anio", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(
            $"El valor de pf_agro_cuotas_por_anio tiene que ser 1, 2, 4 o 12 (cuotas por año) (recibido: {value}).",
            ex.Message);
        Assert.Empty(db.ScoringConfigs.AsNoTracking());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("2.5")]
    public async Task AgroCuotasPorAnio_OutsideTheIntegerBounds_IsRefused_AsAnInteger(string value)
    {
        var (sut, _) = Build();
        var parsed = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_agro_cuotas_por_anio", parsed)));

        Assert.StartsWith("El valor de pf_agro_cuotas_por_anio tiene que ser un número entero entre 1 y 12 (recibido: ", ex.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public async Task CampaignStartMonth_InsideItsBounds_IsAccepted(int value)
    {
        var (sut, _) = Build();

        Assert.NotNull(await Update(sut, ("pf_agro_mes_inicio_campania", value)));
    }

    [Theory]
    [InlineData("0",   "0")]
    [InlineData("13",  "13")]
    [InlineData("7.5", "7,5")]
    public async Task CampaignStartMonth_NotAMonth_IsRefused_WithAnEsArMessage(string value, string printed)
    {
        var (sut, _) = Build();
        var parsed = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_agro_mes_inicio_campania", parsed)));

        Assert.Equal(
            $"El valor de pf_agro_mes_inicio_campania tiene que ser un número entero entre 1 y 12 (recibido: {printed}).",
            ex.Message);
    }

    [Fact]
    public async Task AgroFlag_OtherThanZeroOrOne_IsRefused_WithAnEsArMessage()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_agro_modo_anual", 2m)));

        Assert.Equal("El valor de pf_agro_modo_anual tiene que ser 0 (desactivado) o 1 (activado) (recibido: 2).", ex.Message);
    }

    [Fact]
    public async Task AgroFlag_Off_IsAccepted_AndReadByTheEngineParameters()
    {
        var (sut, db) = Build();

        Assert.NotNull(await Update(sut, ("pf_agro_modo_anual", 0m)));

        var rows = db.ScoringConfigs.AsNoTracking().ToDictionary(r => r.ConfigKey, r => r.ConfigValue);
        Assert.False(PersonEvaluationParameters.From(rows).ResolvedAgroModoAnual);
    }

    [Fact]
    public void V07Parameters_ResolveTheTenantRows_AndTheDefaults()
    {
        var tenant = PersonEvaluationParameters.From(new Dictionary<string, decimal>
        {
            ["pf_agro_cuotas_por_anio"]     = 4m,
            ["pf_agro_mes_inicio_campania"] = 4m
        });

        Assert.True(tenant.ResolvedAgroModoAnual);
        Assert.Equal(4, tenant.ResolvedAgroCuotasPorAnio);
        Assert.Equal(4, tenant.ResolvedAgroMesInicioCampania);
        Assert.Equal(["pf_agro_cuotas_por_anio", "pf_agro_mes_inicio_campania"], tenant.TenantOverrides);

        var defaults = PersonEvaluationParameters.Defaults;
        Assert.True(defaults.AgroModoAnual);
        Assert.Equal(2, defaults.AgroCuotasPorAnio);
        Assert.Equal(7, defaults.AgroMesInicioCampania);
    }

    [Fact]
    public void Pre07Parameters_WithoutTheAgroMembers_ResolveToTheDefaults()
    {
        var pre07 = PersonEvaluationParameters.Defaults with
        {
            AgroModoAnual = null, AgroCuotasPorAnio = null, AgroMesInicioCampania = null
        };

        Assert.True(pre07.ResolvedAgroModoAnual);
        Assert.Equal(2, pre07.ResolvedAgroCuotasPorAnio);
        Assert.Equal(7, pre07.ResolvedAgroMesInicioCampania);
    }
}
