// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The engine 0.9 persona humana keys through the Settings contract: the three investment-class haircuts
/// (pf_haircut_titulo_publico, pf_haircut_accion_on, pf_haircut_fci) — their defaults, group, unit, bounds and place
/// in the layout, their es-AR descriptions, GET/PUT — and pf_haircut_inversion, kept as the haircut of an
/// unclassified investment with a new description and its key, default and validation unchanged.
/// </summary>
public sealed partial class SettingsServicePersonEvaluationTests
{
    private static readonly string[] V09Keys = ["pf_haircut_titulo_publico", "pf_haircut_accion_on", "pf_haircut_fci"];

    [Theory]
    [InlineData("pf_haircut_titulo_publico", 0.15)]
    [InlineData("pf_haircut_accion_on",      0.30)]
    [InlineData("pf_haircut_fci",            0.30)]
    [InlineData("pf_haircut_inversion",      0.30)]
    public async Task V09Defaults_AreWhatGetReturns_InTheGarantiasGroup_AsFractions(string key, double expected)
    {
        var (sut, _) = Build();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        var item = Assert.Single(dto.PersonaHumana, i => i.Key == key);
        Assert.Equal((decimal)expected, item.DefaultValue);
        Assert.Equal((decimal)expected, item.Value);
        Assert.False(item.IsOverride);
        Assert.Equal("garantias", item.Group);
        Assert.Equal("fraccion", item.Unit);
        Assert.Equal("fraction", item.Kind);
        Assert.Equal(0m, item.Min);
        Assert.Equal(1m, item.Max);
        Assert.StartsWith("PROVISIONAL", item.Description);
    }

    [Fact]
    public async Task V09Keys_SitRightAfterTheUnclassifiedInvestmentHaircut_InThisOrder()
    {
        var (sut, _) = Build();

        var keys = (await sut.GetScoringConfigAsync(Tenant, CancellationToken.None)).PersonaHumana.Select(i => i.Key).ToList();

        var at = keys.IndexOf("pf_haircut_inversion");
        Assert.Equal(["pf_haircut_inversion", .. V09Keys, "pf_haircut_credito"], keys.Skip(at).Take(5));
        Assert.Equal(PersonEvaluationDefaults.Layout.Select(l => l.Key), keys);
    }

    [Fact]
    public void V09Keys_AreFractionKeys_InTheLayout()
    {
        Assert.All(V09Keys, key =>
        {
            Assert.Contains(key, PersonEvaluationDefaults.FractionKeys);
            Assert.Contains(("garantias", "fraccion"), PersonEvaluationDefaults.Layout.Where(l => l.Key == key).Select(l => (l.Group, l.Unit!)));
            Assert.Equal(("fraction", 0m, 1m), PersonEvaluationDefaults.BoundsOf(key, new DateOnly(2026, 10, 31)));
        });
    }

    [Fact]
    public void V09Descriptions_AreEsAr_AndNameWhatEachKeyCovers()
    {
        Assert.Equal(
            "PROVISIONAL. Descuento sobre inversiones sin clasificar (cuando el documento no permite saber si son títulos " +
            "públicos, acciones, obligaciones negociables o FCI)",
            PersonEvaluationDefaults.Descriptions["pf_haircut_inversion"]);
        Assert.Contains("títulos públicos (bonos, letras del Tesoro, títulos provinciales o municipales)",
            PersonEvaluationDefaults.Descriptions["pf_haircut_titulo_publico"]);
        Assert.Contains("acciones con cotización y obligaciones negociables con cotización",
            PersonEvaluationDefaults.Descriptions["pf_haircut_accion_on"]);
        Assert.Contains("cuotapartes de fondos comunes de inversión", PersonEvaluationDefaults.Descriptions["pf_haircut_fci"]);
    }

    [Theory]
    [InlineData("pf_haircut_titulo_publico", 0.1)]
    [InlineData("pf_haircut_accion_on",      0.35)]
    [InlineData("pf_haircut_fci",            0.25)]
    [InlineData("pf_haircut_inversion",      0.4)]
    public async Task V09Keys_ArePut_ReadBack_AndReachTheEngineParameters(string key, double value)
    {
        var (sut, db) = Build();

        Assert.NotNull(await Update(sut, (key, (decimal)value)));

        var row = Assert.Single(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == key);
        Assert.Equal((decimal)value, row.ConfigValue);

        var item = Assert.Single((await sut.GetScoringConfigAsync(Tenant, CancellationToken.None)).PersonaHumana, i => i.Key == key);
        Assert.Equal((decimal)value, item.Value);
        Assert.True(item.IsOverride);

        var parameters = PersonEvaluationParameters.From(db.ScoringConfigs.AsNoTracking().ToDictionary(r => r.ConfigKey, r => r.ConfigValue));
        var resolved = key switch
        {
            "pf_haircut_titulo_publico" => parameters.ResolvedHaircutTituloPublico,
            "pf_haircut_accion_on"      => parameters.ResolvedHaircutAccionOn,
            "pf_haircut_fci"            => parameters.ResolvedHaircutFci,
            _                           => parameters.ResolvedHaircutInversion
        };
        Assert.Equal((decimal)value, resolved);
        Assert.Contains(key, parameters.TenantOverrides);
    }

    [Theory]
    [InlineData("pf_haircut_titulo_publico", "1.2", "1,2")]
    [InlineData("pf_haircut_accion_on",      "-0.1", "-0,1")]
    [InlineData("pf_haircut_fci",            "2", "2")]
    public async Task V09Keys_OutsideZeroToOne_AreRefused_WithAnEsArMessage(string key, string value, string printed)
    {
        var (sut, db) = Build();
        var parsed = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, (key, parsed)));

        Assert.Equal($"El valor de {key} tiene que estar entre 0 y 1 (recibido: {printed}).", ex.Message);
        Assert.Empty(db.ScoringConfigs.AsNoTracking());
    }
}
