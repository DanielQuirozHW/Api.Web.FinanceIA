// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The persona humana Settings contract: GET scoring-config returns every pf_ key in its own personaHumana
/// array (effective value, default, override flag, kind, bounds, unit, group), a pf_ item can be reset to
/// its code default, a pf-only PUT validates only the persona config, the pf_ messages are es-AR, and the
/// audit entry carries the old and new values.
/// </summary>
public sealed partial class SettingsServicePersonEvaluationTests
{
    // The order of PersonEvaluationDefaults.Entries, which the GET must keep.
    private static readonly string[] ExpectedOrder =
    [
        "pf_requiere_certificado_mipyme", "pf_certificado_alerta_desde_mes", "pf_certificado_alerta_hasta_mes",
        "pf_bcra_sit_rechazo", "pf_bcra_ventana_meses", "pf_bcra_eventos_ventana_meses",
        "pf_cheques_pagados_alerta_meses", "pf_antiguedad_rechazo_meses", "pf_antiguedad_min_meses",
        "pf_ventas_acreedor_fisco_tolerancia", "pf_consumo_mensual_minimo_miles", "pf_consumo_mes_base",
        "pf_cuota_ingreso_bajo", "pf_cuota_ingreso_medio", "pf_cuota_ingreso_alto", "pf_cuota_estimada_pct_saldo", "pf_dscr_rojo",
        "pf_dscr_verde", "pf_agro_modo_anual", "pf_agro_cuotas_por_anio", "pf_agro_mes_inicio_campania",
        "pf_patrimonio_operativo_alerta_pct", "pf_doc_antiguedad_max_meses",
        "pf_ddjj_anual_antiguedad_max_meses", "pf_informe_max_dias", "pf_aprobacion_requiere_motivo",
        "pf_rechazo_sugerido_requiere_preaprobacion", "pf_contragarantia_cobertura_min_pct",
        "pf_cobertura_rojo", "pf_cobertura_verde", "pf_cuota_ingreso_estres_max", "pf_haircut_inmueble",
        "pf_haircut_inmueble_vivienda", "pf_haircut_rodado", "pf_haircut_participacion", "pf_haircut_deposito",
        "pf_haircut_inversion", "pf_haircut_titulo_publico", "pf_haircut_accion_on", "pf_haircut_fci",
        "pf_haircut_credito", "pf_haircut_otro", "pf_haircut_descalce_moneda",
        "pf_ratio_mercado_fiscal_alerta",
    ];

    // ── GET ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_EmptyTenant_ReturnsEveryPfKey_InOrder_OnItsDefault()
    {
        var (sut, _) = Build();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        Assert.Equal(ExpectedOrder, dto.PersonaHumana.Select(i => i.Key));
        Assert.Equal(
            PersonEvaluationDefaults.Values.Keys.OrderBy(k => k, StringComparer.Ordinal),
            dto.PersonaHumana.Select(i => i.Key).OrderBy(k => k, StringComparer.Ordinal));

        Assert.All(dto.PersonaHumana, item =>
        {
            Assert.Equal(PersonEvaluationDefaults.Get(item.Key), item.DefaultValue);
            Assert.Equal(item.DefaultValue, item.Value);
            Assert.False(item.IsOverride);
            Assert.StartsWith("PROVISIONAL", item.Description);
        });
    }

    [Theory]
    [InlineData("pf_requiere_certificado_mipyme",             "flag",     "0", "1",      null,        "elegibilidad")]
    [InlineData("pf_certificado_alerta_desde_mes",            "integer",  "1", "12",     null,        "elegibilidad")]
    [InlineData("pf_certificado_alerta_hasta_mes",            "integer",  "1", "12",     null,        "elegibilidad")]
    [InlineData("pf_bcra_sit_rechazo",                        "integer",  "1", "5",      null,        "bcra")]
    [InlineData("pf_bcra_ventana_meses",                      "integer",  "1", "12",     "meses",     "bcra")]
    [InlineData("pf_bcra_eventos_ventana_meses",              "integer",  "1", "24",     "meses",     "bcra")]
    [InlineData("pf_cheques_pagados_alerta_meses",            "integer",  "0", "60",     "meses",     "bcra")]
    [InlineData("pf_antiguedad_rechazo_meses",                "integer",  "0", "240",    "meses",     "elegibilidad")]
    [InlineData("pf_antiguedad_min_meses",                    "integer",  "0", "240",    "meses",     "elegibilidad")]
    [InlineData("pf_ventas_acreedor_fisco_tolerancia",        "fraction", "0", "1",      "fraccion",  "elegibilidad")]
    [InlineData("pf_consumo_mensual_minimo_miles",            "integer",  "0", "999999", "miles_ars", "capacidad")]
    [InlineData("pf_cuota_ingreso_bajo",                      "fraction", "0", "1",      "fraccion",  "capacidad")]
    [InlineData("pf_cuota_ingreso_medio",                     "fraction", "0", "1",      "fraccion",  "capacidad")]
    [InlineData("pf_cuota_ingreso_alto",                      "fraction", "0", "1",      "fraccion",  "capacidad")]
    [InlineData("pf_cuota_estimada_pct_saldo",                "fraction", "0.01", "0.2", "fraccion",  "capacidad")]
    [InlineData("pf_dscr_rojo",                               "ratio",    "0", "10",     "veces",     "capacidad")]
    [InlineData("pf_dscr_verde",                              "ratio",    "0", "10",     "veces",     "capacidad")]
    [InlineData("pf_agro_modo_anual",                         "flag",     "0", "1",      null,        "capacidad")]
    [InlineData("pf_agro_cuotas_por_anio",                    "integer",  "1", "12",     null,        "capacidad")]
    [InlineData("pf_agro_mes_inicio_campania",                "integer",  "1", "12",     null,        "capacidad")]
    [InlineData("pf_patrimonio_operativo_alerta_pct",         "fraction", "0", "1",      "fraccion",  "solvencia")]
    [InlineData("pf_doc_antiguedad_max_meses",                "integer",  "1", "60",     "meses",     "documentacion")]
    [InlineData("pf_ddjj_anual_antiguedad_max_meses",         "integer",  "1", "60",     "meses",     "documentacion")]
    [InlineData("pf_informe_max_dias",                        "integer",  "1", "365",    "dias",      "documentacion")]
    [InlineData("pf_aprobacion_requiere_motivo",              "flag",     "0", "1",      null,        "decision")]
    [InlineData("pf_rechazo_sugerido_requiere_preaprobacion", "flag",     "0", "1",      null,        "decision")]
    [InlineData("pf_contragarantia_cobertura_min_pct",        "integer",  "0", "100",    null,        "decision")]
    [InlineData("pf_cobertura_rojo",                          "ratio",    "0", "10",     "veces",     "garantias")]
    [InlineData("pf_cobertura_verde",                         "ratio",    "0", "10",     "veces",     "garantias")]
    [InlineData("pf_cuota_ingreso_estres_max",                "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_inmueble",                        "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_inmueble_vivienda",               "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_rodado",                          "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_participacion",                   "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_deposito",                        "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_inversion",                       "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_titulo_publico",                  "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_accion_on",                       "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_fci",                             "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_credito",                         "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_otro",                            "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_haircut_descalce_moneda",                 "fraction", "0", "1",      "fraccion",  "garantias")]
    [InlineData("pf_ratio_mercado_fiscal_alerta",             "integer",  "1", "1000",   "veces",     "garantias")]
    public async Task Get_EmptyTenant_CarriesKindBoundsUnitAndGroup(
        string key, string kind, string min, string max, string? unit, string group)
    {
        var (sut, _) = Build();

        var dto = await sut.GetScoringConfigAsync(Tenant, CancellationToken.None);

        var item = Assert.Single(dto.PersonaHumana, i => i.Key == key);
        Assert.Equal(kind, item.Kind);
        Assert.Equal(decimal.Parse(min, System.Globalization.CultureInfo.InvariantCulture), item.Min);
        Assert.Equal(decimal.Parse(max, System.Globalization.CultureInfo.InvariantCulture), item.Max);
        Assert.Equal(unit, item.Unit);
        Assert.Equal(group, item.Group);
    }

    [Fact]
    public void ScoringConfigDto_SerializesThePersonaHumanaArray_InCamelCase()
    {
        var item = new PersonEvaluationConfigItemDto
        {
            Key = "pf_dscr_verde", Value = 1.5m, DefaultValue = 1.3m, IsOverride = true, Kind = "ratio",
            Min = 0m, Max = 10m, Unit = "veces", Group = "capacidad", Description = "d"
        };
        var dto = new ScoringConfigDto
        {
            RatioWeights = [], RatioThresholds = [], ComponentWeights = [], ScoreThresholds = [], Macro = [], FloorRules = [],
            PersonaHumana = [item]
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var first = json.RootElement.GetProperty("personaHumana")[0];
        foreach (var name in new[] { "key", "value", "defaultValue", "isOverride", "kind", "min", "max", "unit", "group" })
            Assert.True(first.TryGetProperty(name, out _), name);
    }

    // ── PUT body: wire compatibility and reset ──────────────────────────────────

    [Fact]
    public void UpdateRequest_BindsTodaysKeyValueItems_AndAResetItemWithoutValue()
    {
        const string body = """
            { "items": [
                { "key": "weight_roe", "value": 0.2, "description": "ignored" },
                { "key": "pf_dscr_verde", "reset": true }
            ] }
            """;

        var request = JsonSerializer.Deserialize<UpdateScoringConfigRequest>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(0.2m, request.Items[0].Value);
        Assert.Null(request.Items[0].Reset);
        Assert.Null(request.Items[1].Value);
        Assert.True(request.Items[1].Reset);
    }

    [Fact]
    public async Task Put_ThenGet_ShowsTheValueAsAnOverride()
    {
        var (sut, _) = Build();

        var dto = await Update(sut, ("pf_dscr_verde", 1.5m));

        var item = Assert.Single(dto!.PersonaHumana, i => i.Key == "pf_dscr_verde");
        Assert.Equal(1.5m, item.Value);
        Assert.Equal(1.25m, item.DefaultValue);
        Assert.True(item.IsOverride);
        Assert.All(dto.PersonaHumana.Where(i => i.Key != "pf_dscr_verde"), i => Assert.False(i.IsOverride));
    }

    [Fact]
    public async Task Reset_DeletesTheRow_AndTheKeyGoesBackToItsDefault()
    {
        var (sut, db) = Build(SeedRow("pf_dscr_verde", 1.5m), SeedRow("pf_bcra_sit_rechazo", 4m));

        var dto = await Send(sut, Reset("pf_dscr_verde"));

        Assert.DoesNotContain(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_dscr_verde");
        Assert.Contains(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_bcra_sit_rechazo");

        var item = Assert.Single(dto!.PersonaHumana, i => i.Key == "pf_dscr_verde");
        Assert.Equal(item.DefaultValue, item.Value);
        Assert.False(item.IsOverride);
    }

    [Fact]
    public async Task Reset_WithNoRow_IsANoOp()
    {
        var (sut, db) = Build();

        var dto = await Send(sut, Reset("pf_informe_max_dias"));

        Assert.NotNull(dto);
        Assert.Empty(db.ScoringConfigs.AsNoTracking());
    }

    [Fact]
    public async Task Reset_IgnoresAValueSentAlongside()
    {
        var (sut, db) = Build(SeedRow("pf_informe_max_dias", 30m));

        var dto = await Send(sut, new ScoringConfigUpdateItem { Key = "pf_informe_max_dias", Value = 60m, Reset = true });

        Assert.Empty(db.ScoringConfigs.AsNoTracking());
        Assert.Equal(90m, Assert.Single(dto!.PersonaHumana, i => i.Key == "pf_informe_max_dias").Value);
    }

    [Fact]
    public async Task Reset_IsValidatedLikeAnyChange()
    {
        // rojo 0.5 / verde 0.9 is coherent; resetting rojo to its 1.0 default puts it above verde.
        var (sut, db) = Build(SeedRow("pf_dscr_rojo", 0.5m), SeedRow("pf_dscr_verde", 0.9m));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(sut, Reset("pf_dscr_rojo")));

        Assert.Equal("El valor de pf_dscr_verde (0,9) tiene que ser mayor que pf_dscr_rojo (1).", ex.Message);
        Assert.Equal(2, db.ScoringConfigs.AsNoTracking().Count());
    }

    [Fact]
    public async Task Reset_OnABalanceKey_IsRefused()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Send(sut, Reset("weight_component_macro")));

        Assert.Equal("reset solo se admite para las claves pf_ (recibido: weight_component_macro).", ex.Message);
    }

    [Fact]
    public async Task AnItemWithoutValueOrReset_IsRefused()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Send(sut, new ScoringConfigUpdateItem { Key = "pf_dscr_verde" }));

        Assert.Equal("Falta el valor de pf_dscr_verde.", ex.Message);
    }

    [Fact]
    public async Task ThePfKeyTwiceInOneRequest_IsRefused()
    {
        var (sut, db) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Send(sut, new ScoringConfigUpdateItem { Key = "pf_dscr_verde", Value = 1.5m }, Reset("pf_dscr_verde")));

        Assert.Equal("La clave pf_dscr_verde aparece más de una vez en la solicitud.", ex.Message);
        Assert.Empty(db.ScoringConfigs.AsNoTracking());
    }

    // ── Persona-only validation scope ───────────────────────────────────────────

    // Component weights summing to 0.90 and an inverted leverage triplet (warn above danger): a stored
    // balance config the full validation refuses.
    private static ScoringConfig[] InvalidBalanceSeed() =>
    [
        SeedRow("weight_component_ratios", 0.40m),
        SeedRow("threshold_leverage_warn", 6.00m),
    ];

    [Fact]
    public async Task PfOnlyPut_Succeeds_WhenTheStoredBalanceConfigIsInvalid()
    {
        var (sut, db) = Build(InvalidBalanceSeed());

        var dto = await Update(sut, ("pf_dscr_verde", 1.5m));

        Assert.NotNull(dto);
        Assert.Contains(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_dscr_verde" && r.ConfigValue == 1.5m);
    }

    [Fact]
    public async Task PfOnlyReset_Succeeds_WhenTheStoredBalanceConfigIsInvalid()
    {
        var (sut, db) = Build([.. InvalidBalanceSeed(), SeedRow("pf_dscr_verde", 1.5m)]);

        await Send(sut, Reset("pf_dscr_verde"));

        Assert.DoesNotContain(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_dscr_verde");
    }

    [Fact]
    public async Task PfOnlyPut_StillValidatesThePersonConfig_MergedWithTheStoredValues()
    {
        // The stored verde is 1.1; a lone rojo of 1.2 crosses it.
        var (sut, _) = Build([.. InvalidBalanceSeed(), SeedRow("pf_dscr_verde", 1.1m)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_dscr_rojo", 1.2m)));

        Assert.Equal("El valor de pf_dscr_verde (1,1) tiene que ser mayor que pf_dscr_rojo (1,2).", ex.Message);
    }

    [Fact]
    public async Task MixedPut_StillValidatesEverything()
    {
        var (sut, db) = Build(InvalidBalanceSeed());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, ("pf_dscr_verde", 1.5m), ("weight_component_macro", 0.15m)));

        Assert.StartsWith("Component weights must sum to 1.0", ex.Message);
        Assert.DoesNotContain(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_dscr_verde");
    }

    // ── es-AR messages ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pf_bcra_sit_rechazo", "6",
        "El valor de pf_bcra_sit_rechazo tiene que ser un número entero entre 1 y 5 (recibido: 6).")]
    [InlineData("pf_bcra_sit_rechazo", "2.5",
        "El valor de pf_bcra_sit_rechazo tiene que ser un número entero entre 1 y 5 (recibido: 2,5).")]
    [InlineData("pf_consumo_mensual_minimo_miles", "2450.5",
        "El valor de pf_consumo_mensual_minimo_miles tiene que ser un número entero entre 0 y 999999 (recibido: 2450,5).")]
    [InlineData("pf_requiere_certificado_mipyme", "2",
        "El valor de pf_requiere_certificado_mipyme tiene que ser 0 (desactivado) o 1 (activado) (recibido: 2).")]
    [InlineData("pf_cuota_ingreso_alto", "1.5",
        "El valor de pf_cuota_ingreso_alto tiene que estar entre 0 y 1 (recibido: 1,5).")]
    [InlineData("pf_dscr_rojo", "11",
        "El valor de pf_dscr_rojo tiene que estar entre 0 y 10 (recibido: 11).")]
    [InlineData("pf_dscr_verde", "0.9",
        "El valor de pf_dscr_verde (0,9) tiene que ser mayor que pf_dscr_rojo (1).")]
    [InlineData("pf_cuota_ingreso_medio", "0.2",
        "El valor de pf_cuota_ingreso_medio (0,2) tiene que ser mayor que pf_cuota_ingreso_bajo (0,25): las bandas de cuota/ingreso suben de verde a roja.")]
    [InlineData("pf_cuota_ingreso_alto", "0.3",
        "El valor de pf_cuota_ingreso_alto (0,3) tiene que ser mayor que pf_cuota_ingreso_medio (0,35): las bandas de cuota/ingreso suben de verde a roja.")]
    [InlineData("pf_antiguedad_rechazo_meses", "30",
        "El valor de pf_antiguedad_rechazo_meses (30) no puede superar pf_antiguedad_min_meses (24): por debajo del primero se sugiere rechazo, por debajo del segundo es alerta.")]
    [InlineData("pf_certificado_alerta_hasta_mes", "4",
        "El valor de pf_certificado_alerta_hasta_mes (4) tiene que ser mayor o igual que pf_certificado_alerta_desde_mes (5): juntos acotan la ventana de renovación de un certificado vencido el 30/04.")]
    [InlineData("pf_cobertura_verde", "10.5",
        "El valor de pf_cobertura_verde tiene que estar entre 0 y 10 (recibido: 10,5).")]
    [InlineData("pf_cobertura_verde", "0.8",
        "El valor de pf_cobertura_verde (0,8) tiene que ser mayor que pf_cobertura_rojo (1): las bandas de cobertura del fiador suben de roja a verde.")]
    [InlineData("pf_cobertura_rojo", "2",
        "El valor de pf_cobertura_verde (2) tiene que ser mayor que pf_cobertura_rojo (2): las bandas de cobertura del fiador suben de roja a verde.")]
    [InlineData("pf_haircut_rodado", "1.2",
        "El valor de pf_haircut_rodado tiene que estar entre 0 y 1 (recibido: 1,2).")]
    [InlineData("pf_cuota_ingreso_estres_max", "-0.1",
        "El valor de pf_cuota_ingreso_estres_max tiene que estar entre 0 y 1 (recibido: -0,1).")]
    [InlineData("pf_ratio_mercado_fiscal_alerta", "0",
        "El valor de pf_ratio_mercado_fiscal_alerta tiene que ser un número entero entre 1 y 1000 (recibido: 0).")]
    [InlineData("pf_ratio_mercado_fiscal_alerta", "30.5",
        "El valor de pf_ratio_mercado_fiscal_alerta tiene que ser un número entero entre 1 y 1000 (recibido: 30,5).")]
    [InlineData("pf_cuota_estimada_pct_saldo", "0.005",
        "El valor de pf_cuota_estimada_pct_saldo tiene que estar entre 0,01 y 0,2 (recibido: 0,005).")]
    [InlineData("pf_cuota_estimada_pct_saldo", "0.25",
        "El valor de pf_cuota_estimada_pct_saldo tiene que estar entre 0,01 y 0,2 (recibido: 0,25).")]
    [InlineData("pf_haircut_credito", "1.5",
        "El valor de pf_haircut_credito tiene que estar entre 0 y 1 (recibido: 1,5).")]
    public async Task InvalidPfValue_IsRefused_WithAnEsArMessage(string key, string value, string expected)
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, (key, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(expected, ex.Message);
    }

    // ── Audit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_CarriesTheOldAndNewValueOfEachPfKey()
    {
        var (sut, _, audit) = BuildAudited(SeedRow("pf_bcra_sit_rechazo", 4m));

        await Update(sut, ("pf_dscr_verde", 1.5m), ("pf_bcra_sit_rechazo", 5m));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Settings.UpdateScoringConfig", entry.Action);
        Assert.Equal(
            new Dictionary<string, decimal> { ["pf_dscr_verde"] = 1.25m, ["pf_bcra_sit_rechazo"] = 4m },
            JsonSerializer.Deserialize<Dictionary<string, decimal>>(entry.OldValueJson!));
        Assert.Equal(
            new Dictionary<string, decimal> { ["pf_dscr_verde"] = 1.5m, ["pf_bcra_sit_rechazo"] = 5m },
            JsonSerializer.Deserialize<Dictionary<string, decimal>>(entry.NewValueJson!));
    }

    [Fact]
    public async Task Audit_OfAReset_ShowsTheDefaultAsTheNewValue()
    {
        var (sut, _, audit) = BuildAudited(SeedRow("pf_dscr_verde", 1.5m));

        await Send(sut, Reset("pf_dscr_verde"));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(1.5m, JsonSerializer.Deserialize<Dictionary<string, decimal>>(entry.OldValueJson!)!["pf_dscr_verde"]);
        Assert.Equal(1.25m, JsonSerializer.Deserialize<Dictionary<string, decimal>>(entry.NewValueJson!)!["pf_dscr_verde"]);
    }

    [Fact]
    public async Task Audit_OfAMixedPut_RecordsOnlyThePfKeys()
    {
        var (sut, _, audit) = BuildAudited();

        await Update(sut, ("pf_dscr_verde", 1.5m), ("macro_penalizacion_puntos", ScoringDefaults.Get("macro_penalizacion_puntos")));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(["pf_dscr_verde"], JsonSerializer.Deserialize<Dictionary<string, decimal>>(entry.NewValueJson!)!.Keys);
    }

    [Fact]
    public async Task Audit_OfABalanceOnlyPut_StaysWithoutValues()
    {
        var (sut, _, audit) = BuildAudited();

        await Update(sut, ("macro_penalizacion_puntos", ScoringDefaults.Get("macro_penalizacion_puntos")));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Settings.UpdateScoringConfig", entry.Action);
        Assert.Null(entry.OldValueJson);
        Assert.Null(entry.NewValueJson);
    }
}
