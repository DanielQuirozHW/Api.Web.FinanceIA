// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Engine 0.7, the agro annual mode: detection by the main ARCA activity (CLAE 01/02/03) and by the analyst's
/// override in both directions; the cuota read by its periodicity (cobertura_servicio_deuda and cuota_ingreso on
/// an annual base); a monthly cuota outside agro byte for byte as in 0.6; the sales window aligned to the
/// campaign, and its fallback; no stale monthly DDJJ across the off-season in agro mode only; fiadores unchanged.
/// </summary>
public sealed class PersonEvaluationAgroTests
{
    private static readonly PersonOperationTerms Monthly = new(1_000_000m, "ARS", 100_000m);

    // ── Detection ─────────────────────────────────────────────────────────────────

    private static ArcaFiscalData ArcaWith(params (string Id, int? Orden)[] actividades)
        => Arca() with
        {
            Actividades = actividades.Select(a => new ArcaActividad(a.Id, "actividad", "883", a.Orden, "201901")).ToList()
        };

    [Theory]
    [InlineData("011111", "011111")]
    [InlineData("11111",  "011111")]   // ARCA sends the code as a number: the leading zero is restored
    [InlineData("021000", "021000")]
    [InlineData("031100", "031100")]
    public void AgroClae_TurnsTheAnnualModeOn_WithTheTenantDefaults(string id, string clae)
    {
        var agro = PersonAgroModeResolver.Resolve(ArcaWith((id, 1)), Monthly with { InstallmentAmount = 1m }, PersonEvaluationParameters.Defaults);

        Assert.True(agro.Active);
        Assert.Equal(PersonAgroModeOrigins.Actividad, agro.Origin);
        Assert.Null(agro.Override);
        Assert.Equal(clae, agro.Clae);
        Assert.Equal(2, agro.CuotasPorAnio);
        Assert.Equal(7, agro.MesInicioCampania);
        Assert.Equal($"Actividad agropecuaria (CLAE {clae}): evaluación anual", agro.Evidence);
        Assert.True(agro.Annualized);
    }

    [Fact]
    public void NonAgroClae_KeepsTheMonthlyCuota()
    {
        var agro = PersonAgroModeResolver.Resolve(ArcaWith(("620100", 1)), Monthly, PersonEvaluationParameters.Defaults);

        Assert.False(agro.Active);
        Assert.Equal(PersonAgroModeOrigins.Actividad, agro.Origin);
        Assert.Equal("620100", agro.Clae);
        Assert.Equal(12, agro.CuotasPorAnio);
        Assert.Null(agro.Evidence);
        Assert.False(agro.Annualized);
    }

    [Fact]
    public void TheMainActivity_IsOrdenOne_NotTheFirstListed()
    {
        var agro = PersonAgroModeResolver.Resolve(
            ArcaWith(("620100", 2), ("011111", 1)), Monthly, PersonEvaluationParameters.Defaults);

        Assert.True(agro.Active);
        Assert.Equal("011111", agro.Clae);
    }

    [Fact]
    public void NoArcaActivity_IsNotAgro()
    {
        Assert.False(PersonAgroModeResolver.Resolve(null, Monthly, PersonEvaluationParameters.Defaults).Active);
        Assert.False(PersonAgroModeResolver.Resolve(Arca(), Monthly, PersonEvaluationParameters.Defaults).Active);
        Assert.Null(PersonAgroModeResolver.Resolve(Arca(), Monthly, PersonEvaluationParameters.Defaults).Clae);
    }

    [Fact]
    public void TheAnalyst_ForcesItOn_ForANonAgroActivity_AndWithoutArca()
    {
        foreach (var arca in new[] { ArcaWith(("620100", 1)), null })
        {
            var agro = PersonAgroModeResolver.Resolve(arca, Monthly with { AgroModeOverride = true }, PersonEvaluationParameters.Defaults);

            Assert.True(agro.Active);
            Assert.Equal(PersonAgroModeOrigins.Analista, agro.Origin);
            Assert.True(agro.Override);
            Assert.Equal(2, agro.CuotasPorAnio);
            Assert.Equal("Modo anual activado por el analista", agro.Evidence);
        }
    }

    [Fact]
    public void TheAnalyst_ForcesItOff_ForAnAgroActivity()
    {
        var agro = PersonAgroModeResolver.Resolve(
            ArcaWith(("011111", 1)), Monthly with { AgroModeOverride = false }, PersonEvaluationParameters.Defaults);

        Assert.False(agro.Active);
        Assert.Equal(PersonAgroModeOrigins.Analista, agro.Origin);
        Assert.False(agro.Override);
        Assert.Equal(12, agro.CuotasPorAnio);
        Assert.Equal("Modo anual desactivado por el analista", agro.Evidence);
    }

    [Fact]
    public void TheTenantFlagOff_KeepsAnAgroActivityMonthly_UnlessTheAnalystForcesIt()
    {
        var off = With(p => p["pf_agro_modo_anual"] = 0m);

        var detected = PersonAgroModeResolver.Resolve(ArcaWith(("011111", 1)), Monthly, off);
        Assert.False(detected.Active);
        Assert.Equal(12, detected.CuotasPorAnio);
        Assert.Contains("pf_agro_modo_anual", detected.Evidence);

        Assert.True(PersonAgroModeResolver.Resolve(ArcaWith(("011111", 1)), Monthly with { AgroModeOverride = true }, off).Active);
    }

    [Fact]
    public void StatedCuotasPorAnio_WinOverTheTenantDefault()
    {
        var parameters = With(p => p["pf_agro_cuotas_por_anio"] = 4m);

        Assert.Equal(4, PersonAgroModeResolver.Resolve(ArcaWith(("011111", 1)), Monthly, parameters).CuotasPorAnio);
        Assert.Equal(1, PersonAgroModeResolver.Resolve(ArcaWith(("011111", 1)), Monthly with { CuotasPorAnio = 1 }, parameters).CuotasPorAnio);
        // Outside agro the stated periodicity still applies (a quarterly operation is quarterly).
        var quarterly = PersonAgroModeResolver.Resolve(ArcaWith(("620100", 1)), Monthly with { CuotasPorAnio = 4 }, parameters);
        Assert.False(quarterly.Active);
        Assert.Equal(4, quarterly.CuotasPorAnio);
        Assert.True(quarterly.Annualized);
    }

    // ── cobertura_servicio_deuda on an annual base ────────────────────────────────

    private static PersonAgroMode Mode(bool active, int cuotasPorAnio, int mesInicio = 7) => new()
    {
        Active            = active,
        Origin            = PersonAgroModeOrigins.Analista,
        Override          = active,
        CuotasPorAnio     = cuotasPorAnio,
        MesInicioCampania = mesInicio,
        Evidence          = active ? "Modo anual activado por el analista" : null
    };

    /// <summary>Flujo 60M − 36M consumo = 24M a year; cuotas vigentes 5 % of 20.000 thousand = 1M a month.</summary>
    private static PersonEvaluationInput DscrInput(decimal cuota, PersonAgroMode? agro) => Empty() with
    {
        Incomes = Incomes(
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m),
            Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m)),
        InstallmentAmount = cuota,
        Bcra              = Bcra(Deuda(montoTotal: 20_000m)),
        Agro              = agro
    };

    private static PersonIndicatorResult IndicatorOf(PersonEvaluationInput input, string id)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Indicators, i => i.Id == id);

    [Fact]
    public void Dscr_Semiannual_ComparesAYearOfFlujoWithAYearOfService()
    {
        // 24M / (1M × 12 + 6M × 2) = 1.0 — monthly it would read 2M / 7M = 0.2857, a false rojo.
        var indicator = IndicatorOf(DscrInput(6_000_000m, Mode(true, 2)), "cobertura_servicio_deuda");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(1m, indicator.Value);
        Assert.Equal(PersonIndicatorBands.Amarillo, indicator.Band);
        Assert.Equal("24000000", indicator.Inputs["flujoAnual"]);
        Assert.Equal("24000000", indicator.Inputs["servicioAnual"]);
        Assert.Equal("2", indicator.Inputs["cuotasPorAnio"]);
        Assert.Equal("true", indicator.Inputs["modoAgro"]);
        Assert.Equal(PersonAgroModeOrigins.Analista, indicator.Inputs["origenModoAgro"]);
        Assert.Equal("7", indicator.Inputs["mesInicioCampania"]);
        Assert.Equal("no disponible en el legajo", indicator.Inputs["impuestos"]);
        Assert.False(indicator.Inputs.ContainsKey("flujoMensual"));
        Assert.Contains("Flujo anual $ 24.000.000 sobre servicio de deuda anual $ 24.000.000", indicator.Evidence);
        Assert.Contains("× 2 cuotas por año", indicator.Evidence);
        Assert.EndsWith("Modo anual activado por el analista.", indicator.Evidence);

        var monthly = IndicatorOf(DscrInput(6_000_000m, null), "cobertura_servicio_deuda");
        Assert.Equal(0.2857m, monthly.Value);
        Assert.Equal(PersonIndicatorBands.Rojo, monthly.Band);
    }

    [Fact]
    public void Dscr_SinglePayment_CountsTheCuotaOnce()
    {
        // 24M / (12M + 6M × 1) = 1.3333.
        var indicator = IndicatorOf(DscrInput(6_000_000m, Mode(true, 1)), "cobertura_servicio_deuda");

        Assert.Equal(1.3333m, indicator.Value);
        Assert.Equal(PersonIndicatorBands.Verde, indicator.Band);
        Assert.Equal("18000000", indicator.Inputs["servicioAnual"]);
        Assert.Contains("× 1 pago por año", indicator.Evidence);
    }

    // ── cuota_ingreso on an annual base ───────────────────────────────────────────

    /// <summary>Ganancias 12M a year; cuotas vigentes 5 % of 2.400 thousand = 120.000 a month.</summary>
    private static PersonEvaluationInput CuotaInput(decimal cuota, PersonAgroMode? agro) => Empty() with
    {
        Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
        InstallmentAmount = cuota,
        Bcra              = Bcra(Deuda(montoTotal: 2_400m)),
        Agro              = agro
    };

    [Theory]
    // (120.000 × 12 + 600.000 × n) / 12.000.000; monthly it would be (600.000 + 120.000) / 1.000.000 = 0,72 (roja).
    [InlineData(2, 0.22, "2640000", PersonIndicatorBands.Verde)]
    [InlineData(1, 0.17, "2040000", PersonIndicatorBands.Verde)]
    [InlineData(4, 0.32, "3840000", PersonIndicatorBands.Amarillo)]
    public void CuotaIngreso_ReadsTheCuotaByItsPeriodicity(int cuotasPorAnio, double expected, string servicio, string band)
    {
        var indicator = IndicatorOf(CuotaInput(600_000m, Mode(true, cuotasPorAnio)), "cuota_ingreso");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal((decimal)expected, indicator.Value);
        Assert.Equal(band, indicator.Band);
        Assert.Equal(servicio, indicator.Inputs["servicioAnual"]);
        Assert.Equal("12000000", indicator.Inputs["ingresoAnual"]);
        Assert.Equal(cuotasPorAnio.ToString(), indicator.Inputs["cuotasPorAnio"]);
        Assert.False(indicator.Inputs.ContainsKey("ingresoMensual"));
        Assert.Contains("sobre ingreso anual $ 12.000.000", indicator.Evidence);

        Assert.Equal(0.72m, IndicatorOf(CuotaInput(600_000m, null), "cuota_ingreso").Value);
    }

    [Fact]
    public void AQuarterlyCuota_OutsideAgro_IsAnnualisedToo()
    {
        var indicator = IndicatorOf(CuotaInput(600_000m, Mode(false, 4)), "cuota_ingreso");

        Assert.Equal(0.32m, indicator.Value);
        Assert.Equal("false", indicator.Inputs["modoAgro"]);
        Assert.Equal("4", indicator.Inputs["cuotasPorAnio"]);
    }

    // ── A monthly cuota outside agro: 0.6, byte for byte ─────────────────────────

    /// <summary>
    /// A legajo that exercises every reader of the time base: 24 months of IVA sales, Ganancias, the Central de
    /// Deudores, a CPI series, a cuota and a monthly DDJJ old enough to be stale.
    /// </summary>
    private static PersonEvaluationInput RichInput(PersonAgroMode? agro) => Empty() with
    {
        Incomes = Incomes(MonthlyIva(new DateOnly(2024, 7, 1), 24, i => i % 12 < 6 ? 0m : 2_000_000m)
            .Append(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 60_000_000m))
            .Append(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.MontoConsumido, 2025, 36_000_000m))),
        InflationSeries   = Cpi(26, 2d),
        Bcra              = Bcra(Deuda(montoTotal: 20_000m), Cheques(BcraChequesConsultaEstado.NoChequesReported)),
        Arca              = ArcaWith(("620100", 1)),
        InstallmentAmount = 600_000m,
        Documents         = [Doc(DocumentTypes.DdjjIva, periodEnd: new DateOnly(2026, 2, 28)),
                             Doc(DocumentTypes.Ganancias, periodEnd: new DateOnly(2025, 12, 31))],
        Agro              = agro
    };

    [Fact]
    public void AMonthlyCuotaOutsideAgro_IsByteIdenticalTo06()
    {
        var resolved = PersonAgroModeResolver.Resolve(ArcaWith(("620100", 1)), Monthly, PersonEvaluationParameters.Defaults);
        Assert.False(resolved.Annualized);

        var input = RichInput(null);
        var v06 = PersonEvaluationService.Evaluate(input);
        var v07 = PersonEvaluationService.Evaluate(input with { Agro = resolved });

        Assert.Equal(PersonEvaluationJson.Serialize(v06.Filters), PersonEvaluationJson.Serialize(v07.Filters));
        Assert.Equal(PersonEvaluationJson.Serialize(v06.Indicators), PersonEvaluationJson.Serialize(v07.Indicators));
        Assert.Equal(PersonEvaluationJson.Serialize(v06.Coverage), PersonEvaluationJson.Serialize(v07.Coverage));
        Assert.DoesNotContain("seasonalGap", PersonEvaluationJson.Serialize(v07.Coverage));
        Assert.Equal(v06.Suggestion, v07.Suggestion);
    }

    // ── The campaign window ───────────────────────────────────────────────────────

    /// <summary>Monthly IVA from 2024-07 to 2026-09 (27 months), each month's amount its own calendar month × 1M.</summary>
    private static PersonEvaluationInput SalesInput(PersonAgroMode? agro, DateOnly? first = null, int count = 27) => Empty() with
    {
        Incomes         = Incomes(MonthlyIva(first ?? new DateOnly(2024, 7, 1), count,
                              i => (first ?? new DateOnly(2024, 7, 1)).AddMonths(i).Month * 1_000_000m)),
        InflationSeries = Cpi(30),
        Agro            = agro
    };

    [Fact]
    public void Agro_AlignsTheSalesWindowToTheLatestCompleteCampaign()
    {
        var level = IndicatorOf(SalesInput(Mode(true, 2)), "ventas_nivel");

        // Campaign July 2025 – June 2026: every calendar month once, 1M..12M → 78M, never annualised.
        Assert.Equal(78_000_000m, level.Value);
        Assert.Equal("2025-07-01", level.Inputs["desde"]);
        Assert.Equal("2026-06-30", level.Inputs["hasta"]);
        Assert.Equal("false", level.Inputs["anualizado"]);
        Assert.Equal("campaña 2025-07 a 2026-06", level.Inputs["ventanaCampania"]);
        Assert.Equal("true", level.Inputs["campaniaAlineada"]);
        Assert.StartsWith("Ventas de 12 meses (campaña 2025-07 a 2026-06): ", level.Evidence);

        var trend = IndicatorOf(SalesInput(Mode(true, 2)), "ventas_tendencia");
        Assert.Equal("2024-07-01", trend.Inputs["desde"]);
        Assert.Equal("2026-06-30", trend.Inputs["hasta"]);
        Assert.Equal(0m, trend.Value);   // the same campaign twice, no inflation
    }

    [Fact]
    public void Agro_ACalendarYearCampaign_EndsInDecember()
    {
        var level = IndicatorOf(SalesInput(Mode(true, 2, mesInicio: 1)), "ventas_nivel");

        Assert.Equal("2025-01-01", level.Inputs["desde"]);
        Assert.Equal("2025-12-31", level.Inputs["hasta"]);
        Assert.Equal("campaña 2025-01 a 2025-12", level.Inputs["ventanaCampania"]);
    }

    [Fact]
    public void OutsideAgro_TheWindowIsTheLast12Months_AsIn06()
    {
        var level = IndicatorOf(SalesInput(null), "ventas_nivel");

        Assert.Equal("2025-10-01", level.Inputs["desde"]);
        Assert.Equal("2026-09-30", level.Inputs["hasta"]);
        Assert.False(level.Inputs.ContainsKey("ventanaCampania"));
    }

    [Fact]
    public void Agro_WhenTheLegajoDoesNotCoverTheCampaign_FallsBackToTheLast12Months_AndSaysSo()
    {
        // From 2025-08: the campaign July 2025 – June 2026 lacks July.
        var level = IndicatorOf(SalesInput(Mode(true, 2), new DateOnly(2025, 8, 1), 14), "ventas_nivel");

        Assert.Equal("2025-10-01", level.Inputs["desde"]);
        Assert.Equal("2026-09-30", level.Inputs["hasta"]);
        Assert.Equal("últimos 12 meses (el legajo no cubre la campaña 2025-07 a 2026-06)", level.Inputs["ventanaCampania"]);
        Assert.Equal("false", level.Inputs["campaniaAlineada"]);

        var trend = IndicatorOf(SalesInput(Mode(true, 2), new DateOnly(2025, 8, 1), 14), "ventas_tendencia");
        Assert.Equal(PersonIndicatorStatuses.SinDatos, trend.Status);
    }

    [Fact]
    public void Agro_TheTrendNeedsTwoCompleteCampaigns()
    {
        // From 2025-01: the latest campaign is complete, the one before is not.
        var trend = IndicatorOf(SalesInput(Mode(true, 2), new DateOnly(2025, 1, 1), 21), "ventas_tendencia");

        Assert.Equal(PersonIndicatorStatuses.SinDatos, trend.Status);
        Assert.Contains("dos campañas completas", trend.Evidence);
        Assert.Equal("campaña 2025-07 a 2026-06", trend.Inputs["ventanaCampania"]);
    }

    // ── Seasonal recency of the monthly DDJJ ──────────────────────────────────────

    /// <summary>
    /// Monthly IVA from 2024-07 to 2026-02, zero from March to September (the off-season), and the latest DDJJ IVA
    /// ending 2026-02-28: seven months old on 2026-10-20, above the 6-month cut.
    /// </summary>
    private static PersonEvaluationInput SeasonalInput(PersonAgroMode? agro, bool marchHasSales = false) => Empty() with
    {
        Incomes = Incomes(MonthlyIva(new DateOnly(2024, 7, 1), 20, i =>
        {
            var month = new DateOnly(2024, 7, 1).AddMonths(i).Month;
            return month is >= 3 and <= 9 && !(marchHasSales && month == 3) ? 0m : 5_000_000m;
        })),
        Documents = [Doc(DocumentTypes.DdjjIva, periodEnd: new DateOnly(2026, 2, 28))],
        Agro      = agro
    };

    private static PersonCoverageItem DdjjIva(PersonEvaluationInput input)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Coverage.Items, i => i.DocumentType == DocumentTypes.DdjjIva);

    [Fact]
    public void Agro_AMonthlyDdjjGapInsideTheOffSeason_IsNotStale()
    {
        var item = DdjjIva(SeasonalInput(Mode(true, 2)));

        Assert.Equal(7, item.AgeMonths);
        Assert.Equal(6, item.MaxAgeMonths);
        Assert.False(item.IsStale);
        Assert.True(item.SeasonalGap);
    }

    [Fact]
    public void Agro_TheSeasonalRule_StillAppliesUnderATenantCut()
    {
        // Engine 0.10: the tenant cuts the DDJJ IVA at 4 months; the 7-month gap is still all off-season.
        var input = SeasonalInput(Mode(true, 2)) with
        {
            ExpectedDocuments =
            [
                new(DocumentTypes.AutorizacionDatos, true, []),
                new(DocumentTypes.DdjjIva, true, [], MaxAgeMonths: 4)
            ]
        };

        var item = DdjjIva(input);

        Assert.Equal(4, item.MaxAgeMonths);
        Assert.False(item.IsStale);
        Assert.True(item.SeasonalGap);
    }

    [Fact]
    public void OutsideAgro_TheSameGap_IsStale_AsIn06()
    {
        var item = DdjjIva(SeasonalInput(null));

        Assert.True(item.IsStale);
        Assert.Null(item.SeasonalGap);
    }

    [Fact]
    public void Agro_AGapThatCrossesAMonthWithSales_IsStillStale()
    {
        var item = DdjjIva(SeasonalInput(Mode(true, 2), marchHasSales: true));

        Assert.True(item.IsStale);
        Assert.Null(item.SeasonalGap);
    }

    [Fact]
    public void Agro_ZeroSalesMonths_RaiseNoFilterOrAlert()
    {
        var input = SeasonalInput(null);
        var agro  = PersonEvaluationService.Evaluate(input with { Agro = Mode(true, 2) });
        var v06   = PersonEvaluationService.Evaluate(input);

        // The seasonal months change neither the filters nor the suggestion: no check reads a month without sales.
        Assert.Equal(PersonEvaluationJson.Serialize(v06.Filters), PersonEvaluationJson.Serialize(agro.Filters));
        Assert.Equal(v06.Suggestion, agro.Suggestion);
    }

    // ── Fiadores ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AFiador_WithAnAgroActivity_IsEvaluatedAsIn06()
    {
        var person = CuotaInput(600_000m, null) with { Arca = ArcaWith(("011111", 1)) };

        var agroActivity = PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador([], person: person));
        // The same legajo rows (and so the same document ids), only the ARCA activity removed.
        var noActivity   = PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador([], person: person with { Arca = Arca() }));

        Assert.Equal(PersonEvaluationJson.Serialize(noActivity.Indicators), PersonEvaluationJson.Serialize(agroActivity.Indicators));
        Assert.DoesNotContain(agroActivity.Indicators, i => i.Inputs.ContainsKey("modoAgro"));
    }

    // ── Pre-0.7 frozen sources ────────────────────────────────────────────────────

    [Fact]
    public void Pre07Sources_WithoutAgro_StillDeserialize_AndCarryNoMode()
    {
        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(PersonEvaluationPre06Fixtures.SourcesJson());

        Assert.NotNull(sources);
        Assert.Null(sources.Agro);
        Assert.Null(sources.Parameters.AgroCuotasPorAnio);
        Assert.Equal(2, sources.Parameters.ResolvedAgroCuotasPorAnio);
    }

    [Fact]
    public void APre07Row_ReadsBackWithoutPeriodicityOrMode()
    {
        var response = PersonEvaluationJson.ToResponse(new AnalysisParticipant
        {
            Role                 = ParticipantRoles.Solicitante,
            Currency             = "ARS",
            EvaluationVersion    = "0.6",
            DocumentVersionsJson = PersonEvaluationPre06Fixtures.SourcesJson()
        });

        Assert.Null(response.CuotasPorAnio);
        Assert.Null(response.AgroMode);
        Assert.Equal("0.6", response.EvaluationVersion);
    }
}
