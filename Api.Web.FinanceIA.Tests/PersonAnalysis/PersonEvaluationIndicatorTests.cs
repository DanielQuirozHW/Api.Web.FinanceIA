// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Every indicator of the persona humana evaluation, fed through the #253 read model
/// (LegajoReconciliationReadModel.BuildIncomes / BuildInventory) exactly as production feeds it.
/// </summary>
public sealed class PersonEvaluationIndicatorTests
{
    private static PersonIndicatorResult IndicatorOf(PersonEvaluationInput input, string id)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Indicators, i => i.Id == id);

    // ── ventas_nivel ───────────────────────────────────────────────────────────────

    [Fact]
    public void VentasNivel_TwelveMonths_IsTheSum_AndNominalOnlyWithoutCpi()
    {
        var doc = Guid.NewGuid();
        var input = Empty() with { Incomes = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m, doc)) };

        var indicator = IndicatorOf(input, "ventas_nivel");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(12_000_000m, indicator.Value);
        Assert.Null(indicator.Inputs["ventas12MesesReal"]);
        Assert.Equal("false", indicator.Inputs["anualizado"]);
        Assert.Equal("true", indicator.Inputs["verificado"]);
        Assert.Equal([doc], indicator.SourceDocumentIds);
        Assert.True(indicator.Provisional);
    }

    [Fact]
    public void VentasNivel_IsDeflatedToTheLatestCpiMonth()
    {
        var input = Empty() with
        {
            Incomes         = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m)),
            InflationSeries = Cpi(rate: 2d)
        };

        var indicator = IndicatorOf(input, "ventas_nivel");

        var real = decimal.Parse(indicator.Inputs["ventas12MesesReal"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(real > indicator.Value);   // older pesos are worth more in pesos of 2026-09
        Assert.Equal("2026-09", indicator.Inputs["mesReferenciaIpc"]);

        // The oldest month (2025-10) carries 11 months of 2 % to 2026-09.
        var expected = Enumerable.Range(0, 12).Sum(k => 1_000_000m * (decimal)Math.Pow(1.02, k));
        Assert.Equal(Math.Round(expected, 0), Math.Round(real, 0));
    }

    [Fact]
    public void VentasNivel_PartialYear_IsAnnualised()
    {
        var input = Empty() with { Incomes = Incomes(MonthlyIva(new DateOnly(2026, 2, 1), 8, _ => 1_000_000m)) };

        var indicator = IndicatorOf(input, "ventas_nivel");

        Assert.Equal(12_000_000m, indicator.Value);
        Assert.Equal("true", indicator.Inputs["anualizado"]);
        Assert.Equal("8", indicator.Inputs["mesesDisponibles"]);
    }

    [Fact]
    public void VentasNivel_TooFewMonthsAndNoAnnualReturn_IsSinDatos()
    {
        var input = Empty() with { Incomes = Incomes(MonthlyIva(new DateOnly(2026, 6, 1), 3, _ => 1_000_000m)) };
        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "ventas_nivel").Status);
    }

    [Fact]
    public void VentasNivel_FallsBackToAnAnnualReturn()
    {
        var input = Empty() with { Incomes = Incomes(Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 50_000_000m)) };

        var indicator = IndicatorOf(input, "ventas_nivel");

        Assert.Equal(50_000_000m, indicator.Value);
        Assert.Equal("year", indicator.Inputs["granularidad"]);
    }

    [Fact]
    public void VentasNivel_CertifiedOnly_IsDeclaredNotVerified()
    {
        var input = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 30_000_000m))
        };

        Assert.Equal("false", IndicatorOf(input, "ventas_nivel").Inputs["verificado"]);
    }

    // ── ventas_tendencia ───────────────────────────────────────────────────────────

    [Fact]
    public void VentasTendencia_RealChangeOverTheTwoWindows()
    {
        var input = Empty() with
        {
            Incomes         = Incomes(MonthlyIva(new DateOnly(2024, 10, 1), 24, i => i < 12 ? 1_000_000m : 1_200_000m)),
            InflationSeries = Cpi(rate: 0d)
        };

        var indicator = IndicatorOf(input, "ventas_tendencia");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(0.2m, indicator.Value);
    }

    [Fact]
    public void VentasTendencia_WithoutCpi_IsSinDatos_ButCarriesTheNominalChange()
    {
        var input = Empty() with
        {
            Incomes = Incomes(MonthlyIva(new DateOnly(2024, 10, 1), 24, i => i < 12 ? 1_000_000m : 1_200_000m))
        };

        var indicator = IndicatorOf(input, "ventas_tendencia");

        Assert.Equal(PersonIndicatorStatuses.SinDatos, indicator.Status);
        Assert.Equal("0.2", indicator.Inputs["variacionNominal"]);
    }

    [Fact]
    public void VentasTendencia_OneWindowOnly_IsSinDatos()
    {
        var input = Empty() with { Incomes = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m)) };
        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "ventas_tendencia").Status);
    }

    // ── flujo_actividad ────────────────────────────────────────────────────────────

    [Fact]
    public void Flujo_ResponsableInscripto_IsRenta3raLessMontoConsumido()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 10_000_000m),
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 4_000_000m)),
            Parameters = With(p => p["pf_consumo_mensual_minimo_miles"] = 0m)   // no floor: the F.711 stands
        };

        var indicator = IndicatorOf(input, "flujo_actividad");

        Assert.Equal(6_000_000m, indicator.Value);
        Assert.Equal(PersonEvidenceKinds.Verificado, indicator.EvidenceKind);
        Assert.Equal("no disponible en el legajo", indicator.Inputs["amortizaciones"]);
        Assert.Equal("no disponible en el legajo", indicator.Inputs["intereses"]);
    }

    [Fact]
    public void Flujo_WithoutMontoConsumido_IsSinDatos()
    {
        var input = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 10_000_000m))
        };

        var indicator = IndicatorOf(input, "flujo_actividad");

        Assert.Equal(PersonIndicatorStatuses.SinDatos, indicator.Status);
        Assert.Equal("10000000", indicator.Inputs["renta3ra"]);
    }

    [Fact]
    public void Flujo_WithoutGanancias_IsSinDatos()
    {
        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(Empty(), "flujo_actividad").Status);
    }

    [Fact]
    public void Flujo_ResponsableInscripto_BelowTheFloor_UsesTheFloor()
    {
        // F.711 declares 1.200.000 a year = 100.000 a month, below the default 2.450.000 floor.
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m),
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 1_200_000m))
        };

        var indicator = IndicatorOf(input, "flujo_actividad");

        Assert.Equal(60_000_000m - 2_450_000m * 12m, indicator.Value);
        Assert.Equal("true", indicator.Inputs["pisoAplicado"]);
        Assert.Equal(PersonEvidenceKinds.Declarado, indicator.EvidenceKind);   // a parameter stands in
    }

    [Fact]
    public void Flujo_ResponsableInscripto_AboveTheFloor_KeepsTheDeclaredConsumo()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m),
                Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m))
        };

        var indicator = IndicatorOf(input, "flujo_actividad");

        Assert.Equal(24_000_000m, indicator.Value);
        Assert.Equal("false", indicator.Inputs["pisoAplicado"]);
    }

    [Fact]
    public void Flujo_Monotributo_IsTheManifestacionResultadoLessTheFloor()
    {
        var doc = Guid.NewGuid();
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Manifestacion, LegajoIncomeConcepts.Ventas, 2025, 90_000_000m, doc),
                Year(LegajoIncomeSources.Manifestacion, LegajoIncomeConcepts.Otro, 2025, 40_000_000m, doc))
        };

        var indicator = IndicatorOf(input, "flujo_actividad");

        Assert.Equal(40_000_000m - 2_450_000m * 12m, indicator.Value);
        Assert.Equal("40000000", indicator.Inputs["resultadoActividad"]);
        Assert.Equal(PersonEvidenceKinds.Declarado, indicator.EvidenceKind);
        Assert.Contains("solo declarado", indicator.Evidence);
        Assert.Equal([doc], indicator.SourceDocumentIds);
    }

    [Fact]
    public void Flujo_Monotributo_GrossIncomeIsNeverAFlujo()
    {
        // Certified gross income and sales, but no cuadro de resultados.
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 50_000_000m),
                Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 50_000_000m))
        };

        var indicator = IndicatorOf(input, "flujo_actividad");

        Assert.Equal(PersonIndicatorStatuses.SinDatos, indicator.Status);
        Assert.Equal("Monotributista sin cuadro de resultados: el flujo no se puede estimar.", indicator.Evidence);
    }

    [Fact]
    public void Flujo_Monotributo_WithTheFloorUndefined_IsSinDatos()
    {
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            Incomes    = Incomes(Year(LegajoIncomeSources.Manifestacion, LegajoIncomeConcepts.Otro, 2025, 40_000_000m)),
            Parameters = With(p => p["pf_consumo_mensual_minimo_miles"] = 0m)
        };

        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "flujo_actividad").Status);
    }

    // ── cuota_ingreso ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200_000, 0.2, PersonIndicatorBands.Verde)]
    [InlineData(250_000, 0.25, PersonIndicatorBands.Verde)]      // on the cut: inside it
    [InlineData(300_000, 0.3, PersonIndicatorBands.Amarillo)]
    [InlineData(450_000, 0.45, PersonIndicatorBands.Naranja)]
    [InlineData(600_000, 0.6, PersonIndicatorBands.Rojo)]
    public void CuotaIngreso_IsBandedAt25_35_50(int cuota, double ratio, string band)
    {
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = cuota,
            Bcra              = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null))   // no cuotas vigentes
        };

        var indicator = IndicatorOf(input, "cuota_ingreso");

        Assert.Equal((decimal)ratio, indicator.Value);
        Assert.Equal(band, indicator.Band);
    }

    [Fact]
    public void CuotaIngreso_WithoutInstallment_IsSinDatos()
    {
        var input = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m))
        };

        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "cuota_ingreso").Status);
    }

    [Fact]
    public void CuotaIngreso_InAnotherCurrency_IsSinDatos_NoConversion()
    {
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = 1_000m,
            Currency          = "USD"
        };

        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "cuota_ingreso").Status);
    }

    [Fact]
    public void CuotaIngreso_FallsBackToGrossSales_AndSaysSo()
    {
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            Incomes           = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m)),
            InstallmentAmount = 100_000m,
            Bcra              = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null))
        };

        var indicator = IndicatorOf(input, "cuota_ingreso");

        Assert.Equal(0.1m, indicator.Value);
        Assert.Contains("bruto", indicator.Inputs["baseIngreso"]);
    }

    [Fact]
    public void CuotaIngreso_CutsAreTenantParameters()
    {
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = 200_000m,
            Bcra              = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null)),
            Parameters        = With(p => p["pf_cuota_ingreso_bajo"] = 0.10m)
        };

        Assert.Equal(PersonIndicatorBands.Amarillo, IndicatorOf(input, "cuota_ingreso").Band);
    }

    // ── solvencia, endeudamiento, patrimonio operativo ─────────────────────────────

    private static LegajoInventoryResponse Patrimony() => Inventory(
        Item(LegajoItemSides.Activo, LegajoItemKinds.Inmueble, 70_000_000m),
        Item(LegajoItemSides.Activo, LegajoItemKinds.Rodado, 30_000_000m, activity: true),
        Item(LegajoItemSides.Pasivo, LegajoItemKinds.Deuda, 20_000_000m));

    [Fact]
    public void Solvencia_IsThePatrimonioNetoAtMarket()
    {
        var input = Empty() with { Inventory = Patrimony() };
        Assert.Equal(80_000_000m, IndicatorOf(input, "solvencia_pn").Value);
    }

    [Fact]
    public void Solvencia_EmptyInventory_IsSinDatos()
    {
        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(Empty(), "solvencia_pn").Status);
    }

    [Fact]
    public void Endeudamiento_IsPasivoOverActivo()
    {
        var input = Empty() with { Inventory = Patrimony() };

        var indicator = IndicatorOf(input, "endeudamiento");

        Assert.Equal(0.2m, indicator.Value);
        Assert.Equal("manifestación de bienes", indicator.Inputs["pasivoFuente"]);
    }

    [Fact]
    public void Endeudamiento_ManifestacionWithoutDebts_UsesTheCentralDeDeudores()
    {
        var input = Empty() with
        {
            Inventory = Inventory(Item(LegajoItemSides.Activo, LegajoItemKinds.Inmueble, 100_000_000m)),
            Bcra      = Bcra(Deuda(montoTotal: 5_000m))   // thousands of pesos
        };

        var indicator = IndicatorOf(input, "endeudamiento");

        Assert.Equal(0.05m, indicator.Value);
        Assert.Contains("Central de Deudores", indicator.Inputs["pasivoFuente"]);
    }

    [Fact]
    public void PatrimonioOperativo_IsTheActivityShareOfThePatrimonioNeto()
    {
        // 30M of activity assets over a PN of 80M.
        var indicator = IndicatorOf(Empty() with { Inventory = Patrimony() }, "patrimonio_operativo");

        Assert.Equal(0.375m, indicator.Value);
        Assert.Null(indicator.Severity);
        Assert.Equal("0.3", indicator.Inputs["pesoSobreActivo"]);
    }

    [Fact]
    public void PatrimonioOperativo_AtSeventyPercentOfPn_IsAnAlert()
    {
        var input = Empty() with
        {
            Inventory = Inventory(
                Item(LegajoItemSides.Activo, LegajoItemKinds.Inmueble, 30_000_000m),
                Item(LegajoItemSides.Activo, LegajoItemKinds.Rodado, 70_000_000m, activity: true))
        };

        var indicator = IndicatorOf(input, "patrimonio_operativo");

        Assert.Equal(0.7m, indicator.Value);
        Assert.Equal(PersonSeverities.Normal, indicator.Severity);
    }

    [Fact]
    public void PatrimonioOperativo_NonPositivePn_IsSinDatos()
    {
        var input = Empty() with
        {
            Inventory = Inventory(
                Item(LegajoItemSides.Activo, LegajoItemKinds.Rodado, 10_000_000m, activity: true),
                Item(LegajoItemSides.Pasivo, LegajoItemKinds.Deuda, 20_000_000m))
        };

        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "patrimonio_operativo").Status);
    }

    // ── ventas_coherencia / ventas_acreedor_vs_fisco ───────────────────────────────

    [Fact]
    public void Coherencia_ReadsThe253CoherenceSummary()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 120_000_000m),
                Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };

        var indicator = IndicatorOf(input, "ventas_coherencia");

        Assert.Equal(0.1667m, indicator.Value);
        Assert.Equal(LegajoIncomeSources.Certificacion, indicator.Inputs["fuenteAlta"]);
        Assert.Equal(LegajoIncomeSources.Iibb, indicator.Inputs["fuenteBaja"]);
    }

    [Fact]
    public void Coherencia_SingleSource_IsSinDatos()
    {
        var input = Empty() with { Incomes = Incomes(Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m)) };
        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "ventas_coherencia").Status);
    }

    [Fact]
    public void AcreedorVsFisco_Indicator_BandsRojoWhenItFires()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Manifestacion, LegajoIncomeConcepts.Ventas, 2025, 130_000_000m),
                Year(LegajoIncomeSources.Iva, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };

        var indicator = IndicatorOf(input, "ventas_acreedor_vs_fisco");

        Assert.Equal(PersonIndicatorBands.Rojo, indicator.Band);
        Assert.Equal(PersonSeverities.Fuerte, indicator.Severity);
        Assert.Equal(LegajoIncomeSources.Manifestacion, indicator.Inputs["fuenteAcreedor"]);
    }

    [Fact]
    public void Evaluate_ProducesNoScoreAndNoLevel()
    {
        // The output type has no score or level member at all; the only conclusion is the flag.
        var properties = typeof(PersonEvaluationResult).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(properties, p => p.Contains("Score", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Contains("Level", StringComparison.OrdinalIgnoreCase));
    }
}
