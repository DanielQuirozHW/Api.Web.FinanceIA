// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// The rules the review of PR #254 added: the MiPyME certificate, the consumo floor, the debt-service
/// coverage, the cheque causals, the cuotas vigentes in cuota/ingreso, evidenceKind and the ARCA caps
/// table in force from 2026-08-01.
/// </summary>
public sealed class PersonEvaluationReviewRulesTests
{
    private static PersonFilterResult FilterOf(PersonEvaluationInput input, string id)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Filters, f => f.Id == id);

    private static PersonIndicatorResult IndicatorOf(PersonEvaluationInput input, string id)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Indicators, i => i.Id == id);

    private static LufeData Lufe(string? hasta, bool withCertificate = true)
        => new()
        {
            Entidad = new LufeEntidad
            {
                CertificadoPyme = withCertificate
                    ? new LufeCertificadoPyme { Categoria = "Micro", Desde = "2025-05-01", Hasta = hasta }
                    : null
            }
        };

    // ── certificado_mipyme ─────────────────────────────────────────────────────────

    [Fact]
    public void Certificado_IsTheFirstFilter()
    {
        Assert.Equal("certificado_mipyme", PersonEvaluationService.Evaluate(Empty()).Filters[0].Id);
    }

    [Fact]
    public void Certificado_NoLufeData_IsSinDatos()
    {
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(Empty(), "certificado_mipyme").Outcome);
        Assert.Equal(PersonFilterOutcomes.SinDatos,
            FilterOf(Empty() with { Lufe = new LufeData() }, "certificado_mipyme").Outcome);
    }

    [Fact]
    public void Certificado_Valid_IsOk()
    {
        var filter = FilterOf(Empty() with { Lufe = Lufe("2027-04-30") }, "certificado_mipyme");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
    }

    [Fact]
    public void Certificado_Missing_IsRechazo_OrAlertaWhenNotRequired()
    {
        var input = Empty() with { Lufe = Lufe(null, withCertificate: false) };

        Assert.Equal(PersonFilterOutcomes.Rechazo, FilterOf(input, "certificado_mipyme").Outcome);
        Assert.Equal(PersonFilterOutcomes.Alerta, FilterOf(
            input with { Parameters = With(p => p["pf_requiere_certificado_mipyme"] = 0m) }, "certificado_mipyme").Outcome);
    }

    [Theory]
    [InlineData(5, 1)]     // the first day of desde_mes (default 5)
    [InlineData(5, 15)]
    [InlineData(7, 10)]
    [InlineData(8, 31)]    // the last day of hasta_mes (default 8)
    public void Certificado_ExpiredOn30AprilOfTheYear_InsideTheWindow_IsARenewalAlert(int month, int day)
    {
        var input = Empty() with { AsOfUtc = On(month, day), Lufe = Lufe("30/04/2026") };

        var filter = FilterOf(input, "certificado_mipyme");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Normal, filter.Severity);
        Assert.Contains("renovación pendiente", filter.Evidence);
        Assert.Contains("meses 5 a 8", filter.Evidence);
        Assert.Equal("5", filter.Inputs["alertaDesdeMes"]);
        Assert.Equal("8", filter.Inputs["alertaHastaMes"]);
    }

    [Theory]
    [InlineData(9, 1)]     // the day after the window
    [InlineData(10, 20)]
    [InlineData(12, 31)]
    public void Certificado_ExpiredOn30AprilOfTheYear_AfterTheWindow_IsExpired(int month, int day)
    {
        var input = Empty() with { AsOfUtc = On(month, day), Lufe = Lufe("2026-04-30") };

        var filter = FilterOf(input, "certificado_mipyme");

        Assert.Equal(PersonFilterOutcomes.Rechazo, filter.Outcome);
        Assert.Contains("fuera de la ventana de renovación: meses 5 a 8", filter.Evidence);
    }

    [Fact]
    public void Certificado_AfterTheWindow_IsAnAlert_WhenTheCertificateIsNotRequired()
    {
        var input = Empty() with
        {
            AsOfUtc    = On(9, 1),
            Lufe       = Lufe("2026-04-30"),
            Parameters = With(p => p["pf_requiere_certificado_mipyme"] = 0m)
        };

        var filter = FilterOf(input, "certificado_mipyme");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.DoesNotContain("renovación pendiente", filter.Evidence);
    }

    [Fact]
    public void Certificado_StillValidOn30April_IsOk()
    {
        var input = Empty() with { AsOfUtc = On(4, 30), Lufe = Lufe("2026-04-30") };
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "certificado_mipyme").Outcome);
    }

    [Theory]
    [InlineData(5, 31, PersonFilterOutcomes.Rechazo)]   // before desde_mes = 6
    [InlineData(6, 1, PersonFilterOutcomes.Alerta)]     // the first day of desde_mes = 6
    [InlineData(9, 30, PersonFilterOutcomes.Alerta)]    // the last day of hasta_mes = 9
    [InlineData(10, 1, PersonFilterOutcomes.Rechazo)]   // after it
    public void Certificado_TheWindowIsATenantParameter(int month, int day, string expected)
    {
        var input = Empty() with
        {
            AsOfUtc    = On(month, day),
            Lufe       = Lufe("2026-04-30"),
            Parameters = With(p =>
            {
                p["pf_certificado_alerta_desde_mes"] = 6m;
                p["pf_certificado_alerta_hasta_mes"] = 9m;
            })
        };

        Assert.Equal(expected, FilterOf(input, "certificado_mipyme").Outcome);
    }

    [Fact]
    public void Certificado_ExpiredTheAprilOfThePreviousYear_IsExpired_EvenInTheWindowMonths()
    {
        var input = Empty() with { AsOfUtc = On(5, 15), Lufe = Lufe("2025-04-30") };
        Assert.Equal(PersonFilterOutcomes.Rechazo, FilterOf(input, "certificado_mipyme").Outcome);
    }

    [Fact]
    public void Certificado_ParametersFrozenBeforeTheUpperBound_KeepTheUnboundedWindow()
    {
        // Parameters read back from a blob that predates the key carry no hasta_mes: the old rule.
        var input = Empty() with
        {
            AsOfUtc    = On(11, 15),
            Lufe       = Lufe("2026-04-30"),
            Parameters = PersonEvaluationParameters.Defaults with { CertificadoAlertaHastaMes = null }
        };

        var filter = FilterOf(input, "certificado_mipyme");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal("12", filter.Inputs["alertaHastaMes"]);
    }

    [Fact]
    public void FrozenSources_WrittenBeforeTheUpperBound_StillDeserialize()
    {
        // A DocumentVersionsJson as Phase 4a froze it: every parameter but the new one.
        var current = PersonEvaluationJson.Serialize(new PersonEvaluationSources
        {
            Documents  = [],
            Parameters = PersonEvaluationParameters.Defaults
        });
        var node = System.Text.Json.Nodes.JsonNode.Parse(current)!;
        Assert.True(node["parameters"]!.AsObject().Remove("certificadoAlertaHastaMes"));

        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(node.ToJsonString());

        Assert.NotNull(sources);
        Assert.Null(sources!.Parameters.CertificadoAlertaHastaMes);
        Assert.Equal(5, sources.Parameters.CertificadoAlertaDesdeMes);
    }

    [Fact]
    public void Parameters_ResolveTheUpperBound_FromItsDefault()
    {
        Assert.Equal(8, PersonEvaluationParameters.Defaults.CertificadoAlertaHastaMes);
    }

    [Fact]
    public void Certificado_ExpiredOnTheLatest30April_BeforeTheAlertMonth_IsExpired()
    {
        var input = Empty() with
        {
            Lufe       = Lufe("2026-04-30"),
            Parameters = With(p => p["pf_certificado_alerta_desde_mes"] = 11m)
        };

        Assert.Equal(PersonFilterOutcomes.Rechazo, FilterOf(input, "certificado_mipyme").Outcome);
    }

    [Fact]
    public void Certificado_ExpiredAYearEarlier_IsRechazo()
    {
        Assert.Equal(PersonFilterOutcomes.Rechazo,
            FilterOf(Empty() with { Lufe = Lufe("2025-04-30T00:00:00") }, "certificado_mipyme").Outcome);
    }

    [Fact]
    public void Certificado_UnreadableDate_IsSinDatos()
    {
        Assert.Equal(PersonFilterOutcomes.SinDatos,
            FilterOf(Empty() with { Lufe = Lufe("abril 2027") }, "certificado_mipyme").Outcome);
    }

    [Theory]
    [InlineData("2027-04-30", "2027-04-30")]
    [InlineData("30/04/2027", "2027-04-30")]
    [InlineData("2027-04-30T00:00:00", "2027-04-30")]
    [InlineData("2027-04-30T00:00:00-03:00", "2027-04-30")]
    public void LufeDates_AreParsedInvariantly(string raw, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), PersonEvaluationService.ParseLufeDate(raw));
    }

    // ── consumo_declarado (Responsable inscripto) ──────────────────────────────────

    [Fact]
    public void ConsumoDeclarado_BelowTheReferenceBasket_IsAnAlert()
    {
        var input = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 12_000_000m))
        };

        var filter = FilterOf(input, "consumo_declarado");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Contains("canasta de referencia", filter.Evidence);
        Assert.Equal("1000000", filter.Inputs["consumoDeclaradoMensual"]);
        Assert.Equal("2450000", filter.Inputs["consumoMensualPiso"]);
    }

    [Fact]
    public void ConsumoDeclarado_AboveTheBasket_IsOk_AndNotEmittedForAMonotributista()
    {
        var input = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m))
        };

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "consumo_declarado").Outcome);
        Assert.DoesNotContain(PersonEvaluationService.Evaluate(Empty(FiscalRegimes.Monotributo)).Filters,
            f => f.Id == "consumo_declarado");
    }

    [Fact]
    public void ConsumoDeclarado_WithoutF711_IsSinDatos_AndFloorZeroDoesNotCompare()
    {
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(Empty(), "consumo_declarado").Outcome);

        var input = Empty() with
        {
            Incomes    = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 1m)),
            Parameters = With(p => p["pf_consumo_mensual_minimo_miles"] = 0m)
        };
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "consumo_declarado").Outcome);
    }

    [Fact]
    public void ConsumoFloor_IsInThousands()
    {
        Assert.Equal(2_450_000m, PersonEvaluationParameters.Defaults.ConsumoMensualMinimo);
        Assert.Equal(1_000_000m, With(p => p["pf_consumo_mensual_minimo_miles"] = 1000m).ConsumoMensualMinimo);
    }

    // ── cuota_ingreso with the cuotas vigentes ─────────────────────────────────────

    [Fact]
    public void CuotaIngreso_AddsTheEstimatedCuotasVigentes()
    {
        // Central de Deudores: 2.400 thousand pesos = $ 2.400.000; 5 % of it = 120.000 a month.
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = 200_000m,
            Bcra              = Bcra(Deuda(montoTotal: 2_400m))
        };

        var indicator = IndicatorOf(input, "cuota_ingreso");

        Assert.Equal(0.32m, indicator.Value);
        Assert.Equal("200000", indicator.Inputs["cuota"]);
        Assert.Equal("120000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Equal("0.05", indicator.Inputs["cuotaEstimadaPctSaldo"]);
        Assert.False(indicator.Inputs.ContainsKey("plazoEstimadoDeudaMeses"));
        Assert.Contains("cuotas vigentes estimadas $ 120.000 (5 % del saldo de la Central de Deudores)", indicator.Evidence);
        Assert.Equal("true", indicator.Inputs["cuotasVigentesEsEstimacion"]);
        Assert.Equal(PersonIndicatorBands.Amarillo, indicator.Band);
        Assert.Equal(PersonEvidenceKinds.Verificado, indicator.EvidenceKind);
    }

    [Fact]
    public void CuotaIngreso_WithoutTheCentralDeDeudores_IsSinDatos()
    {
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = 200_000m
        };

        Assert.Equal(PersonIndicatorStatuses.SinDatos, IndicatorOf(input, "cuota_ingreso").Status);
    }

    // ── cobertura_servicio_deuda ───────────────────────────────────────────────────

    private static PersonEvaluationInput DscrInput(decimal renta3ra, decimal cuota, decimal bcraMiles) => Empty() with
    {
        Incomes = Incomes(
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, renta3ra),
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m)),
        InstallmentAmount = cuota,
        Bcra              = Bcra(Deuda(montoTotal: bcraMiles))
    };

    [Theory]
    // flujo = renta3ra − 36M; servicio = cuota + bcraMiles×1000×0,05 (pf_cuota_estimada_pct_saldo).
    [InlineData(60_000_000, 1_000_000, 20_000, 1.0, PersonIndicatorBands.Amarillo)]    // 2M / 2M, on the rojo cut
    [InlineData(60_000_000, 1_500_000, 10_000, 1.0, PersonIndicatorBands.Amarillo)]
    [InlineData(59_976_000, 1_000_000, 20_000, 0.999, PersonIndicatorBands.Rojo)]      // 1,998M / 2M, just below rojo
    [InlineData(60_000_000, 1_000_000, 30_000, 0.8, PersonIndicatorBands.Rojo)]        // 2M / 2.5M
    [InlineData(84_000_000, 1_000_000, 10_000, 2.6667, PersonIndicatorBands.Verde)]    // 4M / 1.5M
    [InlineData(58_500_000, 1_000_000, 10_000, 1.25, PersonIndicatorBands.Verde)]      // 1,875M / 1.5M, on the verde cut
    [InlineData(58_498_200, 1_000_000, 10_000, 1.2499, PersonIndicatorBands.Amarillo)] // just below verde
    public void Dscr_IsBandedByRojoAndVerde(int renta3ra, int cuota, int bcraMiles, double expected, string band)
    {
        var indicator = IndicatorOf(DscrInput(renta3ra, cuota, bcraMiles), "cobertura_servicio_deuda");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(Math.Round((decimal)expected, 4), indicator.Value);
        Assert.Equal(band, indicator.Band);
        Assert.Equal("no disponible en el legajo", indicator.Inputs["impuestos"]);
        Assert.Equal("true", indicator.Inputs["cuotasVigentesEsEstimacion"]);
    }

    [Fact]
    public void Dscr_NoDebtReported_MeansNoCuotasVigentes()
    {
        var input = DscrInput(60_000_000m, 1_000_000m, 0m) with
        {
            Bcra = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null))
        };

        var indicator = IndicatorOf(input, "cobertura_servicio_deuda");

        Assert.Equal(2m, indicator.Value);
        Assert.Equal("0", indicator.Inputs["cuotasVigentesEstimadas"]);
        // Zero is what the BCRA said, not an estimate.
        Assert.Equal("false", indicator.Inputs["cuotasVigentesEsEstimacion"]);
        Assert.Contains("no registra deudas en la Central de Deudores", indicator.Evidence);
    }

    [Fact]
    public void CuotaIngreso_NoDebtReported_IsOk_WithZeroCuotasVigentes_NotSinDatos()
    {
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = 200_000m,
            Bcra              = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null, montoTotal: null))
        };

        var indicator = IndicatorOf(input, "cuota_ingreso");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(0.2m, indicator.Value);
        Assert.Equal("0", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Equal("false", indicator.Inputs["cuotasVigentesEsEstimacion"]);
        Assert.Contains("cuotas vigentes $ 0 (no registra deudas en la Central de Deudores)", indicator.Evidence);
    }

    [Fact]
    public void CuotaIngreso_LookupFailed_IsSinDatos_AndSaysTheQueryFailed()
    {
        var input = Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            InstallmentAmount = 200_000m,
            Bcra              = Bcra(Deuda(BcraDeudaConsultaEstado.LookupFailed))
        };

        var indicator = IndicatorOf(input, "cuota_ingreso");

        Assert.Equal(PersonIndicatorStatuses.SinDatos, indicator.Status);
        Assert.Contains("falló", indicator.Evidence);
        Assert.Contains("Las cuotas vigentes no se pueden estimar.", indicator.Evidence);
    }

    [Fact]
    public void Dscr_WithoutInstallmentFlujoOrBcra_IsSinDatos()
    {
        var full = DscrInput(60_000_000m, 1_000_000m, 12_000m);

        Assert.Equal(PersonIndicatorStatuses.SinDatos,
            IndicatorOf(full with { InstallmentAmount = null }, "cobertura_servicio_deuda").Status);
        Assert.Equal(PersonIndicatorStatuses.SinDatos,
            IndicatorOf(full with { Incomes = Incomes() }, "cobertura_servicio_deuda").Status);
        Assert.Equal(PersonIndicatorStatuses.SinDatos,
            IndicatorOf(full with { Bcra = Bcra(Deuda(BcraDeudaConsultaEstado.LookupFailed)) }, "cobertura_servicio_deuda").Status);
    }

    [Fact]
    public void Dscr_BandsAreTenantParameters()
    {
        var input = DscrInput(60_000_000m, 1_000_000m, 24_000m) with
        {
            Parameters = With(p => { p["pf_dscr_rojo"] = 1.2m; p["pf_dscr_verde"] = 1.5m; })
        };

        Assert.Equal(PersonIndicatorBands.Rojo, IndicatorOf(input, "cobertura_servicio_deuda").Band);
    }

    // ── Cheques by causal ──────────────────────────────────────────────────────────

    private static PersonEvaluationInput WithCheques(params BcraChequeDetalle[] detalle) => Empty() with
    {
        Bcra = Bcra(cheques: new BcraChequesData
        {
            Estado          = BcraChequesConsultaEstado.ChequesFound,
            TotalRechazados = detalle.Length,
            TotalImpagos    = detalle.Count(d => !d.Pagado && d.FechaPago is null),
            Detalle         = detalle.ToList()
        })
    };

    private static BcraChequeDetalle Cheque(string causal, DateTime rechazo, bool pagado, decimal monto = 50_000m)
        => new() { Causal = causal, FechaRechazo = rechazo, Pagado = pagado, FechaPago = pagado ? rechazo.AddDays(10) : null, Monto = monto };

    [Fact]
    public void Cheques_UnpaidSinFondos_IsRechazo()
    {
        Assert.Equal(PersonFilterOutcomes.Rechazo, FilterOf(
            WithCheques(Cheque("SIN FONDOS SUFICIENTES", new DateTime(2024, 1, 10), pagado: false)), "bcra_cheques_impagos").Outcome);
    }

    [Fact]
    public void Cheques_SinFondosPaidWithinTheWindow_IsAnAlert()
    {
        var filter = FilterOf(
            WithCheques(Cheque("SIN FONDOS SUFICIENTES", new DateTime(2026, 3, 10), pagado: true)), "bcra_cheques_impagos");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal("1", filter.Inputs["sinFondosPagadosRecientes"]);
    }

    [Fact]
    public void Cheques_SinFondosPaidLongAgo_IsOk()
    {
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(
            WithCheques(Cheque("SIN FONDOS SUFICIENTES", new DateTime(2024, 3, 10), pagado: true)), "bcra_cheques_impagos").Outcome);
    }

    [Fact]
    public void Cheques_FormalDefect_IsAnInformativeOk_EvenUnpaid()
    {
        var filter = FilterOf(
            WithCheques(Cheque("DEFECTOS FORMALES", new DateTime(2026, 9, 1), pagado: false)), "bcra_cheques_impagos");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Contains("defectos formales", filter.Evidence);
        Assert.Equal("1", filter.Inputs["defectosFormales"]);
    }

    [Fact]
    public void Cheques_PaidWindowIsATenantParameter()
    {
        var input = WithCheques(Cheque("SIN FONDOS SUFICIENTES", new DateTime(2026, 3, 10), pagado: true)) with
        {
            Parameters = With(p => p["pf_cheques_pagados_alerta_meses"] = 3m)
        };

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "bcra_cheques_impagos").Outcome);
    }

    // ── evidenceKind ───────────────────────────────────────────────────────────────

    [Fact]
    public void EvidenceKind_FollowsTheSources()
    {
        var verified = Empty() with { Incomes = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m)) };
        var declared = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 1_000_000m))
        };

        Assert.Equal(PersonEvidenceKinds.Verificado, IndicatorOf(verified, "ventas_nivel").EvidenceKind);
        Assert.Equal(PersonEvidenceKinds.Declarado, IndicatorOf(declared, "ventas_nivel").EvidenceKind);
        Assert.Equal(PersonEvidenceKinds.Verificado, FilterOf(Empty() with { Bcra = Bcra(Deuda()) }, "bcra_situacion").EvidenceKind);
        Assert.Equal(PersonEvidenceKinds.Declarado, IndicatorOf(Empty(), "solvencia_pn").EvidenceKind);
    }

    [Fact]
    public void EvidenceKind_MixedInputsAreDeclarado()
    {
        Assert.Equal(PersonEvidenceKinds.Declarado,
            PersonEvidenceKinds.Combine([PersonEvidenceKinds.Verificado, PersonEvidenceKinds.Declarado]));
        Assert.Equal(PersonEvidenceKinds.Verificado,
            PersonEvidenceKinds.Combine([PersonEvidenceKinds.Verificado, PersonEvidenceKinds.Verificado]));
        Assert.Equal(PersonEvidenceKinds.Declarado, PersonEvidenceKinds.Combine([]));

        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 120_000_000m),
                Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };
        Assert.Equal(PersonEvidenceKinds.Declarado, IndicatorOf(input, "ventas_coherencia").EvidenceKind);
    }

    [Fact]
    public void EvidenceKind_IsOnEveryFilterAndIndicator()
    {
        var result = PersonEvaluationService.Evaluate(Empty());

        Assert.All(result.Filters, f => Assert.Contains(f.EvidenceKind, PersonEvidenceKinds.Known));
        Assert.All(result.Indicators, i => Assert.Contains(i.EvidenceKind, PersonEvidenceKinds.Known));
    }

    // ── ARCA caps table ────────────────────────────────────────────────────────────

    [Fact]
    public void MonotributoCaps_AreTheArcaTableInForceFromAugust2026()
    {
        Assert.Equal(new DateOnly(2026, 8, 1), MonotributoCategoryCaps.VigenteDesde);
        Assert.Equal(12_009_410.45m, MonotributoCategoryCaps.AnnualCaps["A"]);
        Assert.Equal(17_595_182.74m, MonotributoCategoryCaps.AnnualCaps["B"]);
        Assert.Equal(24_670_494.31m, MonotributoCategoryCaps.AnnualCaps["C"]);
        Assert.Equal(30_628_651.43m, MonotributoCategoryCaps.AnnualCaps["D"]);
        Assert.Equal(36_028_231.33m, MonotributoCategoryCaps.AnnualCaps["E"]);
        Assert.Equal(45_151_659.41m, MonotributoCategoryCaps.AnnualCaps["F"]);
        Assert.Equal(53_995_798.87m, MonotributoCategoryCaps.AnnualCaps["G"]);
        Assert.Equal(81_924_660.37m, MonotributoCategoryCaps.AnnualCaps["H"]);
        Assert.Equal(91_699_761.90m, MonotributoCategoryCaps.AnnualCaps["I"]);
        Assert.Equal(105_012_519.20m, MonotributoCategoryCaps.AnnualCaps["J"]);
        Assert.Equal(126_610_838.75m, MonotributoCategoryCaps.AnnualCaps["K"]);
        Assert.Equal(11, MonotributoCategoryCaps.AnnualCaps.Count);
    }

    // ── Decision rules ─────────────────────────────────────────────────────────────

    [Fact]
    public void DecisionRules_FollowTheParametersAndTheSuggestion()
    {
        var defaults = PersonEvaluationParameters.Defaults;

        var rechazo = PersonDecisionRules.From(defaults, PersonSuggestions.RechazoSugerido);
        Assert.True(rechazo.RequiresReason);
        Assert.True(rechazo.RequiresPreApproval);
        Assert.Equal(["counterGuaranteesConfirmed", "fdrLimitConfirmed"], rechazo.RequiredConfirmations);

        Assert.False(PersonDecisionRules.From(defaults, PersonSuggestions.ParaDecisionDelAnalista).RequiresPreApproval);

        var off = With(p => { p["pf_aprobacion_requiere_motivo"] = 0m; p["pf_rechazo_sugerido_requiere_preaprobacion"] = 0m; });
        Assert.False(PersonDecisionRules.From(off, PersonSuggestions.RechazoSugerido).RequiresReason);
        Assert.False(PersonDecisionRules.From(off, PersonSuggestions.RechazoSugerido).RequiresPreApproval);
    }

    [Fact]
    public void DecisionRules_ACounterGuaranteeCoverage_RulesOutASolaFirma()
    {
        var byDefault = PersonDecisionRules.From(PersonEvaluationParameters.Defaults, PersonSuggestions.ParaDecisionDelAnalista);
        Assert.Equal(100, byDefault.MinCounterGuaranteeCoveragePct);
        Assert.False(byDefault.SolaFirmaAllowed);

        var partial = PersonDecisionRules.From(
            With(p => p["pf_contragarantia_cobertura_min_pct"] = 30m), PersonSuggestions.ParaDecisionDelAnalista);
        Assert.Equal(30, partial.MinCounterGuaranteeCoveragePct);
        Assert.False(partial.SolaFirmaAllowed);

        var none = PersonDecisionRules.From(
            With(p => p["pf_contragarantia_cobertura_min_pct"] = 0m), PersonSuggestions.ParaDecisionDelAnalista);
        Assert.Equal(0, none.MinCounterGuaranteeCoveragePct);
        Assert.True(none.SolaFirmaAllowed);
    }

    [Fact]
    public void DecisionRules_ParametersFrozenBeforeTheCoverageKey_FallBackToTheDefault()
    {
        // An evaluation frozen before the key existed has no member for it: the blob must stay readable,
        // and the rule resolves to the default rather than to "no coverage".
        var blob = System.Text.Json.Nodes.JsonNode.Parse(PersonEvaluationJson.Serialize(PersonEvaluationParameters.Defaults))!.AsObject();
        Assert.True(blob.Remove("contragarantiaCoberturaMinPct"));
        var older = PersonEvaluationJson.TryDeserialize<PersonEvaluationParameters>(blob.ToJsonString());

        Assert.NotNull(older);
        Assert.Null(older!.ContragarantiaCoberturaMinPct);
        Assert.False(PersonDecisionRules.From(older, PersonSuggestions.ParaDecisionDelAnalista).SolaFirmaAllowed);
    }
}
