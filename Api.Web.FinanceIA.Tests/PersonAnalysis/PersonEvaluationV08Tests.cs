// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Engine 0.8: the cuotas vigentes declared by the analyst replace the Central de Deudores estimate in
/// cuota_ingreso, cobertura_servicio_deuda (monthly and agro annual) and the fiador's cuota_ingreso_estres — zero
/// included, BCRA or not — with the estimate kept beside them for comparison; absent, everything reads as 0.7.
/// Cash at hand (Efectivo) never counts toward a fiador's coverage, whatever the haircuts.
/// </summary>
public sealed class PersonEvaluationV08Tests
{
    private static PersonIndicatorResult IndicatorOf(PersonEvaluationInput input, string id)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Indicators, i => i.Id == id);

    private static PersonAgroMode Mode(int cuotasPorAnio) => new()
    {
        Active            = true,
        Origin            = PersonAgroModeOrigins.Analista,
        Override          = true,
        CuotasPorAnio     = cuotasPorAnio,
        MesInicioCampania = 7,
        Evidence          = "Modo anual activado por el analista"
    };

    /// <summary>Ganancias 12M a year = 1M a month; cuota 400.000; cuotas vigentes estimated at 5 % of 2.400 thousand = 120.000.</summary>
    private static PersonEvaluationInput CuotaInput(decimal? declaradas, BcraDeudaData? deuda = null) => Empty() with
    {
        Incomes                  = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
        InstallmentAmount        = 400_000m,
        Bcra                     = Bcra(deuda ?? Deuda(montoTotal: 2_400m)),
        CuotasVigentesDeclaradas = declaradas
    };

    /// <summary>Flujo 60M − 36M = 24M a year (2M a month); cuota 1M; cuotas vigentes estimated at 5 % of 20.000 thousand = 1M.</summary>
    private static PersonEvaluationInput DscrInput(decimal cuota, decimal? declaradas, PersonAgroMode? agro = null) => Empty() with
    {
        Incomes = Incomes(
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m),
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m)),
        InstallmentAmount        = cuota,
        Bcra                     = Bcra(Deuda(montoTotal: 20_000m)),
        Agro                     = agro,
        CuotasVigentesDeclaradas = declaradas
    };

    // ── cuota_ingreso ─────────────────────────────────────────────────────────────

    [Fact]
    public void CuotaIngreso_DeclaredInstallments_ReplaceTheEstimate()
    {
        // (400.000 + 50.000) / 1.000.000 = 0,45; on the estimate it is (400.000 + 120.000) / 1.000.000 = 0,52.
        var indicator = IndicatorOf(CuotaInput(50_000m), "cuota_ingreso");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(0.45m, indicator.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, indicator.Inputs["cuotasVigentesOrigen"]);
        Assert.Equal("50000", indicator.Inputs["cuotasVigentesDeclaradas"]);
        Assert.Equal("120000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Equal("false", indicator.Inputs["cuotasVigentesEsEstimacion"]);
        Assert.Contains(
            "Cuota $ 400.000 más cuotas vigentes declaradas por el analista $ 50.000 (la estimación por la Central de " +
            "Deudores era $ 120.000) sobre ingreso mensual $ 1.000.000",
            indicator.Evidence);
        Assert.Equal(PersonEvidenceKinds.Combine([PersonEvidenceKinds.Verificado, PersonEvidenceKinds.Declarado]), indicator.EvidenceKind);

        Assert.Equal(0.52m, IndicatorOf(CuotaInput(null), "cuota_ingreso").Value);
    }

    [Fact]
    public void CuotaIngreso_WithoutADeclaration_ReadsAsBefore_AndSaysItEstimated()
    {
        var indicator = IndicatorOf(CuotaInput(null), "cuota_ingreso");

        Assert.Equal(0.52m, indicator.Value);
        Assert.Equal(CuotasVigentesOrigenes.Estimada, indicator.Inputs["cuotasVigentesOrigen"]);
        Assert.False(indicator.Inputs.ContainsKey("cuotasVigentesDeclaradas"));
        Assert.Equal("120000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Equal("true", indicator.Inputs["cuotasVigentesEsEstimacion"]);
        Assert.Contains("cuotas vigentes estimadas $ 120.000 (5 % del saldo de la Central de Deudores)", indicator.Evidence);
        Assert.Equal(PersonEvidenceKinds.Verificado, indicator.EvidenceKind);
    }

    [Fact]
    public void Zero_IsADeclaration_NotTheAbsenceOfOne()
    {
        var zero   = IndicatorOf(CuotaInput(0m), "cuota_ingreso");
        var absent = IndicatorOf(CuotaInput(null), "cuota_ingreso");

        Assert.Equal(0.40m, zero.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, zero.Inputs["cuotasVigentesOrigen"]);
        Assert.Equal("0", zero.Inputs["cuotasVigentesDeclaradas"]);
        Assert.Contains("cuotas vigentes declaradas por el analista $ 0 (la estimación", zero.Evidence);

        Assert.Equal(0.52m, absent.Value);
        Assert.Equal(CuotasVigentesOrigenes.Estimada, absent.Inputs["cuotasVigentesOrigen"]);
    }

    [Fact]
    public void ADeclaration_NeedsNoBcra_AndSaysTheEstimateWasUnavailable()
    {
        var failed = Deuda(BcraDeudaConsultaEstado.LookupFailed, null, montoTotal: null);

        var declared = IndicatorOf(CuotaInput(50_000m, failed), "cuota_ingreso");
        Assert.Equal(PersonIndicatorStatuses.Ok, declared.Status);
        Assert.Equal(0.45m, declared.Value);
        Assert.False(declared.Inputs.ContainsKey("cuotasVigentesEstimadas"));
        Assert.Contains("(la estimación por la Central de Deudores no estaba disponible)", declared.Evidence);

        // Without the declaration the same legajo cannot conclude, exactly as in 0.7.
        var estimated = IndicatorOf(CuotaInput(null, failed), "cuota_ingreso");
        Assert.Equal(PersonIndicatorStatuses.SinDatos, estimated.Status);
        Assert.Contains("Las cuotas vigentes no se pueden estimar.", estimated.Evidence);
    }

    [Fact]
    public void ADeclaration_WithNoDebtReported_ComparesWithAZeroEstimate()
    {
        var indicator = IndicatorOf(
            CuotaInput(50_000m, Deuda(BcraDeudaConsultaEstado.NoDebtReported, null, montoTotal: null)), "cuota_ingreso");

        Assert.Equal(0.45m, indicator.Value);
        Assert.Equal("0", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Contains("(la estimación por la Central de Deudores era $ 0)", indicator.Evidence);
    }

    // ── cobertura_servicio_deuda ──────────────────────────────────────────────────

    [Fact]
    public void Dscr_DeclaredInstallments_ReplaceTheEstimate()
    {
        // 2M / (1M + 250.000) = 1,6; on the estimate 2M / (1M + 1M) = 1.
        var indicator = IndicatorOf(DscrInput(1_000_000m, 250_000m), "cobertura_servicio_deuda");

        Assert.Equal(1.6m, indicator.Value);
        Assert.Equal("1250000", indicator.Inputs["servicioMensual"]);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, indicator.Inputs["cuotasVigentesOrigen"]);
        Assert.Equal("250000", indicator.Inputs["cuotasVigentesDeclaradas"]);
        Assert.Equal("1000000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Contains("más cuotas vigentes declaradas por el analista $ 250.000 (la estimación por la Central de Deudores era $ 1.000.000))", indicator.Evidence);
        Assert.Contains(PersonEvidenceKinds.Declarado, indicator.EvidenceKind);

        Assert.Equal(1m, IndicatorOf(DscrInput(1_000_000m, null), "cobertura_servicio_deuda").Value);
    }

    // ── Agro annualisation ────────────────────────────────────────────────────────

    [Fact]
    public void AgroDscr_AnnualisesTheDeclaredInstallments()
    {
        // 24M / (250.000 × 12 + 6M × 2) = 24M / 15M = 1,6; on the estimate 24M / (1M × 12 + 12M) = 1.
        var indicator = IndicatorOf(DscrInput(6_000_000m, 250_000m, Mode(2)), "cobertura_servicio_deuda");

        Assert.Equal(1.6m, indicator.Value);
        Assert.Equal("15000000", indicator.Inputs["servicioAnual"]);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, indicator.Inputs["cuotasVigentesOrigen"]);
        Assert.Contains("más cuotas vigentes declaradas por el analista $ 250.000", indicator.Evidence);
        Assert.Contains("× 12)", indicator.Evidence);

        Assert.Equal(1m, IndicatorOf(DscrInput(6_000_000m, null, Mode(2)), "cobertura_servicio_deuda").Value);
    }

    [Fact]
    public void AgroCuotaIngreso_AnnualisesTheDeclaredInstallments()
    {
        // (0 × 12 + 600.000 × 2) / 12M = 0,1; on the estimate (120.000 × 12 + 1,2M) / 12M = 0,22.
        var declared = CuotaInput(0m) with { InstallmentAmount = 600_000m, Agro = Mode(2) };
        var indicator = IndicatorOf(declared, "cuota_ingreso");

        Assert.Equal(0.1m, indicator.Value);
        Assert.Equal("1200000", indicator.Inputs["servicioAnual"]);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, indicator.Inputs["cuotasVigentesOrigen"]);

        Assert.Equal(0.22m, IndicatorOf(declared with { CuotasVigentesDeclaradas = null }, "cuota_ingreso").Value);
    }

    // ── Fiador ────────────────────────────────────────────────────────────────────

    /// <summary>Ganancias 12M a year; full cuota 400.000; cuotas vigentes estimated at 120.000.</summary>
    private static PersonEvaluationInput FiadorPerson(decimal? declaradas) => Empty() with
    {
        Incomes                  = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
        Bcra                     = Bcra(Deuda(montoTotal: 2_400m)),
        InstallmentAmount        = 400_000m,
        CuotasVigentesDeclaradas = declaradas
    };

    private static PersonIndicatorResult StressOf(PersonEvaluationInput person)
        => Assert.Single(
            PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador([], person: person)).Indicators,
            i => i.Id == "cuota_ingreso_estres");

    [Fact]
    public void FiadorStress_DeclaredInstallments_ReplaceTheEstimate()
    {
        // (400.000 + 30.000) / 1.000.000 = 0,43; on the estimate 0,52.
        var indicator = StressOf(FiadorPerson(30_000m));

        Assert.Equal(0.43m, indicator.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, indicator.Inputs["cuotasVigentesOrigen"]);
        Assert.Equal("30000", indicator.Inputs["cuotasVigentesDeclaradas"]);
        Assert.Equal("120000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Contains("Estrés: cuota completa de la operación $ 400.000 más cuotas vigentes declaradas por el analista $ 30.000", indicator.Evidence);
        Assert.Equal(PersonEvidenceKinds.Combine([PersonEvidenceKinds.Verificado, PersonEvidenceKinds.Declarado]), indicator.EvidenceKind);

        var estimated = StressOf(FiadorPerson(null));
        Assert.Equal(0.52m, estimated.Value);
        Assert.Equal(CuotasVigentesOrigenes.Estimada, estimated.Inputs["cuotasVigentesOrigen"]);
    }

    // ── Cash at hand ──────────────────────────────────────────────────────────────

    private static FiadorAssetResult AssetOf(LegajoItem item, Action<Dictionary<string, decimal>>? set = null)
        => Assert.Single(PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador(
            [item], person: Empty() with { Parameters = With(set ?? (_ => { })) })).Coverage.Assets);

    [Fact]
    public void Cash_IsNeverCounted_EvenWithAZeroOtroHaircut()
    {
        var cash = AssetOf(PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Efectivo, null, fiscal: 500_000m),
            r => r["pf_haircut_otro"] = 0m);

        Assert.Equal(FiadorAssetTreatments.Excluido, cash.Treatment);
        Assert.Equal(FiadorAssetReasons.EfectivoSinRespaldo, cash.Reason);
        Assert.Equal("Efectivo declarado sin respaldo documental: no se computa.", cash.ReasonText);
        Assert.Equal(0m, cash.ExecutableValue);
        Assert.Null(cash.Haircut);

        // An Otro item at the same haircut does count: the cash rule is the kind's, not the haircut's.
        var otro = AssetOf(PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Otro, null, fiscal: 500_000m),
            r => r["pf_haircut_otro"] = 0m);
        Assert.Equal(FiadorAssetTreatments.Computado, otro.Treatment);
    }

    [Fact]
    public void ABankDeposit_KeepsItsHaircut()
    {
        var deposit = AssetOf(PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Deposito, 1_000_000m),
            r => r["pf_haircut_deposito"] = 0.10m);

        Assert.Equal(FiadorAssetTreatments.Computado, deposit.Treatment);
        Assert.Equal(0.10m, deposit.Haircut);
        Assert.Equal(900_000m, deposit.ExecutableValue);
    }

    // ── Labels ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheNewCodes_HaveTheirEsArLabels()
    {
        Assert.Equal("Dinero en efectivo", PersonLabels.AssetKind(LegajoItemKinds.Efectivo));
        Assert.Equal("Efectivo declarado sin respaldo documental: no se computa.",
            PersonLabels.AssetReason(FiadorAssetReasons.EfectivoSinRespaldo));
        Assert.Equal("Cuotas vigentes declaradas", PersonLabels.Input("cuotasVigentesDeclaradas"));
        Assert.Equal("Origen de las cuotas vigentes", PersonLabels.Input("cuotasVigentesOrigen"));
        Assert.Equal("Declaradas por el analista", PersonLabels.InputTextValue(CuotasVigentesOrigenes.Declarada));
        Assert.Equal("Estimadas por la Central de Deudores", PersonLabels.InputTextValue(CuotasVigentesOrigenes.Estimada));
    }
}
