// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Engine 0.6: the cuotas vigentes as pf_cuota_estimada_pct_saldo of the Central de Deudores balance (reported in
/// THOUSANDS of pesos), the consumo floor re-expressed by CPI from pf_consumo_mes_base to the series' reference
/// month — and nominal, saying so, when it cannot be — and the parameters frozen before 0.6 still reading.
/// </summary>
public sealed class PersonEvaluationV06Tests
{
    // ── Cuotas vigentes estimadas ─────────────────────────────────────────────────

    [Theory]
    [InlineData(2_400, 120_000)]    // $ 2.400.000 × 5 %
    [InlineData(1, 50)]             // one thousand pesos: the ×1000 is what makes it 50, not 0,05
    [InlineData(0, 0)]
    public void CuotasVigentes_AreFivePercentOfTheBalance_InPesos(int miles, int expected)
    {
        var input = Empty() with { Bcra = Bcra(Deuda(montoTotal: miles)) };

        Assert.Equal((decimal?)expected, PersonEvaluationService.CuotasVigentesEstimadas(input));
    }

    [Fact]
    public void CuotasVigentes_FollowTheTenantShare()
    {
        var input = Empty() with
        {
            Bcra       = Bcra(Deuda(montoTotal: 2_400m)),
            Parameters = With(r => r["pf_cuota_estimada_pct_saldo"] = 0.10m)
        };

        Assert.Equal(240_000m, PersonEvaluationService.CuotasVigentesEstimadas(input));
    }

    [Fact]
    public void CuotasVigentes_FeedTheDebtServiceCoverage()
    {
        // flujo (60M − 36M) / 12 = 2M a month; servicio = 1M + 2.400 miles × 1000 × 0,05 = 1,12M.
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m),
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m)),
            InstallmentAmount = 1_000_000m,
            Bcra              = Bcra(Deuda(montoTotal: 2_400m))
        };

        var indicator = Assert.Single(PersonEvaluationService.Evaluate(input).Indicators, i => i.Id == "cobertura_servicio_deuda");

        Assert.Equal(Math.Round(2_000_000m / 1_120_000m, 4), indicator.Value);
        Assert.Equal("120000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Equal("1120000", indicator.Inputs["servicioMensual"]);
        Assert.Equal("0.05", indicator.Inputs["cuotaEstimadaPctSaldo"]);
        Assert.False(indicator.Inputs.ContainsKey("plazoEstimadoDeudaMeses"));
        Assert.Contains("cuotas vigentes estimadas $ 120.000 (5 % del saldo de la Central de Deudores)", indicator.Evidence);
    }

    // ── Consumo floor, CPI-indexed ────────────────────────────────────────────────

    private static PersonEvaluationInput BelowTheFloor(Dictionary<string, double>? cpi, int baseMonth = 202609) => Empty() with
    {
        // F.711: 1.200.000 a year = 100.000 a month, below any floor here.
        Incomes = Incomes(
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m),
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 1_200_000m)),
        InflationSeries = cpi,
        Parameters      = With(r => r["pf_consumo_mes_base"] = baseMonth)
    };

    private static PersonIndicatorResult Flujo(PersonEvaluationInput input)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Indicators, i => i.Id == "flujo_actividad");

    [Fact]
    public void ConsumoFloor_IsIndexedByCpi_FromTheBaseMonthToTheReference()
    {
        // The series ends 2026-09 at 2 % a month: a peso of 2026-07 is 1,02² = 1,0404 pesos of 2026-09.
        var indicator = Flujo(BelowTheFloor(Cpi(rate: 2d), baseMonth: 202607));

        const decimal indexed = 2_450_000m * 1.0404m;
        Assert.Equal(60_000_000m - indexed * 12m, indicator.Value);
        Assert.Equal("2548980", indicator.Inputs["consumoMensualPiso"]);
        Assert.Equal("2450000", indicator.Inputs["consumoPisoBase"]);
        Assert.Equal("2026-07", indicator.Inputs["consumoMesBase"]);
        Assert.Equal("2026-09", indicator.Inputs["mesReferenciaIpc"]);
        Assert.Equal("1.0404", indicator.Inputs["factorIpcConsumo"]);
        Assert.Equal("true", indicator.Inputs["pisoIndexado"]);
        Assert.Equal("true", indicator.Inputs["pisoAplicado"]);
        Assert.Contains("$ 2.548.980 ($ 2.450.000 de 2026-07 × 1,0404 por IPC a 2026-09)", indicator.Evidence);
    }

    [Fact]
    public void ConsumoFloor_OnTheReferenceMonth_HasAFactorOfOne()
    {
        var indicator = Flujo(BelowTheFloor(Cpi(rate: 2d)));

        Assert.Equal("2450000", indicator.Inputs["consumoMensualPiso"]);
        Assert.Equal("1", indicator.Inputs["factorIpcConsumo"]);
        Assert.Equal("true", indicator.Inputs["pisoIndexado"]);
    }

    [Fact]
    public void ConsumoFloor_WithoutACpiSeries_IsNominal_AndSaysSo()
    {
        var indicator = Flujo(BelowTheFloor(cpi: null));

        Assert.Equal(60_000_000m - 2_450_000m * 12m, indicator.Value);
        Assert.Equal("2450000", indicator.Inputs["consumoMensualPiso"]);
        Assert.Null(indicator.Inputs["factorIpcConsumo"]);
        Assert.Equal("false", indicator.Inputs["pisoIndexado"]);
        Assert.Contains("sin serie de IPC: piso nominal", indicator.Evidence);
    }

    [Fact]
    public void ConsumoFloor_WithABaseMonthOutsideTheSeries_IsNominal_AndSaysSo()
    {
        // Cpi(months: 6) covers 2026-04 to 2026-09 only.
        var indicator = Flujo(BelowTheFloor(Cpi(months: 6, rate: 2d), baseMonth: 202501));

        Assert.Equal("2450000", indicator.Inputs["consumoMensualPiso"]);
        Assert.Equal("false", indicator.Inputs["pisoIndexado"]);
        Assert.Contains("sin serie de IPC para 2025-01: piso nominal", indicator.Evidence);
    }

    [Fact]
    public void ConsumoFloor_WithABaseMonthAfterTheSeries_IsNominal_AndSaysSo()
    {
        var indicator = Flujo(BelowTheFloor(Cpi(rate: 2d), baseMonth: 202610));

        Assert.Equal("2450000", indicator.Inputs["consumoMensualPiso"]);
        Assert.Equal("false", indicator.Inputs["pisoIndexado"]);
        Assert.Contains("mes base posterior al último IPC de la serie (2026-09): piso nominal", indicator.Evidence);
    }

    [Fact]
    public void ConsumoDeclarado_IsComparedWithTheIndexedFloor()
    {
        // 2.500.000 a month declared: above the nominal 2.450.000, below the indexed 2.548.980.
        var input = BelowTheFloor(Cpi(rate: 2d), baseMonth: 202607) with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 30_000_000m))
        };

        var filter = Assert.Single(PersonEvaluationService.Evaluate(input).Filters, f => f.Id == "consumo_declarado");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal("2548980", filter.Inputs["consumoMensualPiso"]);
        Assert.Equal("2450000", filter.Inputs["consumoPisoBase"]);
        Assert.Contains("1,0404 por IPC a 2026-09", filter.Evidence);

        var nominal = input with { InflationSeries = null };
        Assert.Equal(PersonFilterOutcomes.Ok,
            Assert.Single(PersonEvaluationService.Evaluate(nominal).Filters, f => f.Id == "consumo_declarado").Outcome);
    }

    [Fact]
    public void ConsumoFloor_Monotributo_UsesTheIndexedFloor()
    {
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            InflationSeries = Cpi(rate: 2d),
            Parameters      = With(r => r["pf_consumo_mes_base"] = 202607)
        };

        var consumo = PersonEvaluationService.ResolveConsumo(input, IncomeFacts.From(input));

        Assert.Equal(2_548_980m, consumo.Monthly);
        Assert.True(consumo.FloorApplied);
        Assert.Contains("× 1,0404 por IPC a 2026-09", consumo.Basis);
    }

    // ── Parameters frozen before 0.6 ──────────────────────────────────────────────

    [Fact]
    public void ParametersFrozenBefore06_StillDeserialize_WithTheirPlazo_AndResolveTheNewKeysToDefaults()
    {
        // A 0.5 blob: the retired plazo, none of the 0.6 members.
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(PersonEvaluationParameters.Defaults))!.AsObject();
        foreach (var key in new[]
                 {
                     "cuotaEstimadaPctSaldo", "consumoMesBase", "haircutDeposito", "haircutInversion", "haircutCredito",
                     "haircutOtro"
                 })
        {
            Assert.True(json.Remove(key), key);
        }

        json["plazoEstimadoDeudaMeses"] = 24;
        json["dscrVerde"]               = 1.3m;

        var parameters = PersonEvaluationJson.TryDeserialize<PersonEvaluationParameters>(json.ToJsonString());

        Assert.NotNull(parameters);
        Assert.Equal(24, parameters.PlazoEstimadoDeudaMeses);
        Assert.Equal(1.3m, parameters.DscrVerde);
        Assert.Null(parameters.CuotaEstimadaPctSaldo);
        Assert.Equal(0.05m, parameters.ResolvedCuotaEstimadaPctSaldo);
        Assert.Equal(202609, parameters.ResolvedConsumoMesBase);
        Assert.Equal(0m, parameters.ResolvedHaircutDeposito);
        Assert.Equal(0.3m, parameters.ResolvedHaircutInversion);
        Assert.Equal(1m, parameters.ResolvedHaircutCredito);
        Assert.Equal(1m, parameters.ResolvedHaircutOtro);

        // Re-serialised, the frozen plazo is still there: an old analysis keeps showing the term it used.
        var again = JsonNode.Parse(PersonEvaluationJson.Serialize(parameters))!;
        Assert.Equal(24, again["plazoEstimadoDeudaMeses"]!.GetValue<int>());
    }

    [Fact]
    public void FreshParameters_CarryNoPlazo_AndEveryNewKey()
    {
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(PersonEvaluationParameters.Defaults))!;

        Assert.Null(json["plazoEstimadoDeudaMeses"]);
        Assert.Equal(0.05m, json["cuotaEstimadaPctSaldo"]!.GetValue<decimal>());
        Assert.Equal(202609, json["consumoMesBase"]!.GetValue<int>());
        Assert.Equal(1.25m, json["dscrVerde"]!.GetValue<decimal>());
        Assert.Equal(0.3m, json["haircutInversion"]!.GetValue<decimal>());
    }
}
