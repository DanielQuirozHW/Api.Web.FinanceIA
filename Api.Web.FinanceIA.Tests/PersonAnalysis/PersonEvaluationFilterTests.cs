// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Every pre-filter of the persona humana evaluation, including SinDatos: a missing or failed source is
/// never read as Ok.
/// </summary>
public sealed class PersonEvaluationFilterTests
{
    private static PersonFilterResult FilterOf(PersonEvaluationInput input, string id)
        => Assert.Single(PersonEvaluationService.Evaluate(input).Filters, f => f.Id == id);

    // ── bcra_situacion ─────────────────────────────────────────────────────────────

    [Fact]
    public void BcraSituacion_NoSnapshot_IsSinDatos()
    {
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(Empty(), "bcra_situacion").Outcome);
    }

    [Fact]
    public void BcraSituacion_LookupFailed_IsSinDatos_NotOk()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(BcraDeudaConsultaEstado.LookupFailed, situacion: null)) };
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "bcra_situacion").Outcome);
    }

    [Fact]
    public void BcraSituacion_NoDebtReported_IsOk_ButSaysSinHistorial()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null)) };

        var filter = FilterOf(input, "bcra_situacion");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal("sin_historial", filter.Inputs["estado"]);
        Assert.Contains("Sin historial", filter.Evidence);
    }

    // ── BCRA "no registra" vs a failed or missing query (go-live) ─────────────────

    /// <summary>A clean person: the BCRA answered 18/10/2026 (15:00 UTC = 12:00 in Argentina) that there is no debt.</summary>
    private static PersonEvaluationInput CleanPerson()
    {
        var deuda = Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null, montoTotal: null);
        deuda.FetchedAt = new DateTime(2026, 10, 18, 15, 0, 0, DateTimeKind.Utc);
        return Empty() with
        {
            Bcra          = Bcra(deuda, Cheques(BcraChequesConsultaEstado.NoChequesReported)),
            BcraUpdatedAt = new DateTime(2026, 10, 18, 15, 0, 5, DateTimeKind.Utc)
        };
    }

    [Theory]
    [InlineData("bcra_situacion")]
    [InlineData("bcra_eventos_graves")]
    public void NoDebtReported_IsAVerifiedOk_WithTheQueryDate(string id)
    {
        var filter = FilterOf(CleanPerson(), id);

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
        Assert.StartsWith("No registra deudas en el sistema financiero (Central de Deudores, consulta del 18/10/2026).",
            filter.Evidence);
        Assert.Equal("2026-10-18", filter.Inputs["fechaConsulta"]);
        Assert.DoesNotContain("falló", filter.Evidence);
    }

    [Fact]
    public void NoDebtReported_SituacionSaysCuotasVigentesZero()
    {
        Assert.Equal("0", FilterOf(CleanPerson(), "bcra_situacion").Inputs["cuotasVigentes"]);
    }

    [Fact]
    public void NoDebtReported_WithoutFetchedAt_DatesTheAnswerByTheSnapshot_InArgentineTime()
    {
        // A block stored without FetchedAt: the legajo's BcraDataUpdatedAt, 01:00 UTC = 22:00 of the day before.
        var input = Empty() with
        {
            Bcra          = Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, situacion: null)),
            BcraUpdatedAt = new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc)
        };

        var filter = FilterOf(input, "bcra_situacion");

        Assert.Contains("consulta del 30/09/2026", filter.Evidence);
        Assert.Equal("2026-09-30", filter.Inputs["fechaConsulta"]);
    }

    [Fact]
    public void NoDebtReported_RaisesNoBcraCannotConclude()
    {
        var codes = PersonEvaluationService.Evaluate(CleanPerson()).Coverage.CannotConclude.Select(c => c.Code).ToList();

        Assert.DoesNotContain("bcra_no_disponible", codes);
        Assert.DoesNotContain("cheques_no_disponibles", codes);
    }

    [Theory]
    [InlineData("bcra_situacion")]
    [InlineData("bcra_eventos_graves")]
    public void NoSnapshot_IsSinDatos_AndSaysTheLegajoHasNoQuery(string id)
    {
        var filter = FilterOf(Empty(), id);

        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        Assert.StartsWith("El legajo no tiene consulta a la Central de Deudores del BCRA.", filter.Evidence);
        Assert.Contains("Actualizá BCRA en la entidad.", filter.Evidence);
        Assert.DoesNotContain("falló", filter.Evidence);
    }

    [Theory]
    [InlineData("bcra_situacion")]
    [InlineData("bcra_eventos_graves")]
    public void LookupFailed_IsSinDatos_AndSaysWhichQueryFailed(string id)
    {
        var deuda = Deuda(BcraDeudaConsultaEstado.LookupFailed, situacion: null);
        deuda.FetchedAt = new DateTime(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);
        var input = Empty() with { Bcra = Bcra(deuda) };

        var filter = FilterOf(input, id);

        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        Assert.StartsWith("La consulta a la Central de Deudores del BCRA del 19/10/2026 falló.", filter.Evidence);
        Assert.Equal("2026-10-19", filter.Inputs["fechaConsulta"]);
    }

    [Fact]
    public void Cheques_NoSnapshotAndFailed_AreSinDatos_WithDistinctReasons()
    {
        var failed = Cheques(BcraChequesConsultaEstado.LookupFailed);
        failed.FetchedAt = new DateTime(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);

        var missing = FilterOf(Empty(), "bcra_cheques_impagos");
        var broken  = FilterOf(Empty() with { Bcra = Bcra(cheques: failed) }, "bcra_cheques_impagos");

        Assert.Equal(PersonFilterOutcomes.SinDatos, missing.Outcome);
        Assert.Equal(PersonFilterOutcomes.SinDatos, broken.Outcome);
        Assert.StartsWith("El legajo no tiene consulta de cheques rechazados del BCRA.", missing.Evidence);
        Assert.StartsWith("La consulta de cheques rechazados del BCRA del 19/10/2026 falló.", broken.Evidence);
    }

    [Fact]
    public void CannotConclude_NamesTheSameCauseAsTheFilters()
    {
        var missing = Assert.Single(
            PersonEvaluationService.Evaluate(Empty()).Coverage.CannotConclude, c => c.Code == "bcra_no_disponible");
        var failed = Assert.Single(
            PersonEvaluationService.Evaluate(Empty() with { Bcra = Bcra(Deuda(BcraDeudaConsultaEstado.LookupFailed)) })
                .Coverage.CannotConclude, c => c.Code == "bcra_no_disponible");

        Assert.StartsWith("El legajo no tiene consulta", missing.Text);
        Assert.StartsWith("La consulta a la Central de Deudores del BCRA", failed.Text);
        Assert.Contains("falló", failed.Text);
    }

    [Fact]
    public void BcraSituacion_SituacionThreeInsideTheWindow_IsRechazo()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(situacion: 1, history: [(0, 1), (5, 3), (11, 2)])) };

        var filter = FilterOf(input, "bcra_situacion");

        Assert.Equal(PersonFilterOutcomes.Rechazo, filter.Outcome);
        Assert.Equal("3", filter.Inputs["peorSituacionVentana"]);
    }

    [Fact]
    public void BcraSituacion_SituacionThreeOutsideTheWindow_IsOk()
    {
        // 12 months back is the 13th period: outside a 12-month window.
        var input = Empty() with { Bcra = Bcra(Deuda(situacion: 1, history: [(0, 1), (12, 3)])) };

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "bcra_situacion").Outcome);
    }

    [Fact]
    public void BcraSituacion_CurrentSituacion_CountsEvenWithoutHistory()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(situacion: 4)) };
        Assert.Equal(PersonFilterOutcomes.Rechazo, FilterOf(input, "bcra_situacion").Outcome);
    }

    [Fact]
    public void BcraSituacion_ThresholdIsATenantParameter()
    {
        var input = Empty() with
        {
            Bcra       = Bcra(Deuda(situacion: 3)),
            Parameters = With(p => p["pf_bcra_sit_rechazo"] = 4m)
        };

        var filter = FilterOf(input, "bcra_situacion");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal("4", filter.Inputs["umbralRechazo"]);
    }

    // ── bcra_eventos_graves ────────────────────────────────────────────────────────

    [Fact]
    public void EventosGraves_LookupFailed_IsSinDatos()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(BcraDeudaConsultaEstado.LookupFailed)) };
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "bcra_eventos_graves").Outcome);
    }

    [Fact]
    public void EventosGraves_Refinanciacion_IsStrongAlert()
    {
        var input = Empty() with
        {
            Bcra = Bcra(Deuda(detalle: [new BcraEntidadDeudaDetalle { Situacion = 1, Refinanciaciones = true }]))
        };

        var filter = FilterOf(input, "bcra_eventos_graves");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Contains("refinanciación", filter.Evidence);
        Assert.Contains("último período", filter.Evidence);   // states its real coverage
    }

    [Fact]
    public void EventosGraves_SituacionFiveInHistory_IsStrongAlert()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(situacion: 1, history: [(0, 1), (8, 5)])) };

        var filter = FilterOf(input, "bcra_eventos_graves");

        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Equal("true", filter.Inputs["situacion5"]);
    }

    [Fact]
    public void EventosGraves_OnlyUnderReview_IsAPlainAlert()
    {
        var input = Empty() with
        {
            Bcra = Bcra(Deuda(detalle: [new BcraEntidadDeudaDetalle { Situacion = 1, EnRevision = true }]))
        };

        var filter = FilterOf(input, "bcra_eventos_graves");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Normal, filter.Severity);
        Assert.Contains("impugnados", filter.Evidence);
    }

    [Fact]
    public void EventosGraves_Clean_IsOk()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(history: [(0, 1), (3, 1)])) };

        var filter = FilterOf(input, "bcra_eventos_graves");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Null(filter.Severity);
    }

    // ── bcra_cheques_impagos ───────────────────────────────────────────────────────

    [Fact]
    public void Cheques_Missing_IsSinDatos()
    {
        var input = Empty() with { Bcra = Bcra(Deuda()) };
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "bcra_cheques_impagos").Outcome);
    }

    [Fact]
    public void Cheques_LookupFailed_IsSinDatos()
    {
        var input = Empty() with { Bcra = Bcra(cheques: Cheques(BcraChequesConsultaEstado.LookupFailed)) };
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "bcra_cheques_impagos").Outcome);
    }

    [Fact]
    public void Cheques_Unpaid_IsRechazo()
    {
        var input = Empty() with { Bcra = Bcra(cheques: Cheques(rechazados: 3, impagos: 1, montoImpago: 250_000m)) };

        var filter = FilterOf(input, "bcra_cheques_impagos");

        Assert.Equal(PersonFilterOutcomes.Rechazo, filter.Outcome);
        Assert.Equal("1", filter.Inputs["totalImpagos"]);
    }

    [Fact]
    public void Cheques_AllPaid_IsOk_AndReportsTheCount()
    {
        var input = Empty() with { Bcra = Bcra(cheques: Cheques(rechazados: 2, impagos: 0)) };

        var filter = FilterOf(input, "bcra_cheques_impagos");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Contains("ninguno impago", filter.Evidence);
    }

    [Fact]
    public void Cheques_NoneReported_IsOk()
    {
        var input = Empty() with { Bcra = Bcra(cheques: Cheques(BcraChequesConsultaEstado.NoChequesReported)) };
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "bcra_cheques_impagos").Outcome);
    }

    // ── ARCA ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, PersonFilterOutcomes.Rechazo)]
    [InlineData(false, PersonFilterOutcomes.Ok)]
    [InlineData(null, PersonFilterOutcomes.SinDatos)]
    public void Sucesion_FollowsTheSnapshot(bool? esSucesion, string expected)
    {
        var input = Empty() with { Arca = Arca(esSucesion) };
        Assert.Equal(expected, FilterOf(input, "arca_sucesion").Outcome);
    }

    [Fact]
    public void Sucesion_NoSnapshot_IsSinDatos()
    {
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(Empty(), "arca_sucesion").Outcome);
    }

    [Theory]
    [InlineData("ACTIVO")]
    [InlineData(" activo ")]
    public void CuitActivo_Activo_IsOk(string estadoClave)
    {
        var filter = FilterOf(Empty() with { Arca = ArcaWithKey(estadoClave) }, "arca_cuit_activo");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
        Assert.Equal("2026-10-10", filter.Inputs["consultadoEl"]);
        Assert.Contains("10/10/2026", filter.Evidence);
    }

    [Theory]
    [InlineData("INACTIVO")]
    [InlineData("LIMITADO")]
    [InlineData("BAJA DEFINITIVA")]
    public void CuitActivo_AnyOtherStatus_IsRechazo_WithTheRawStatus(string estadoClave)
    {
        var filter = FilterOf(Empty() with { Arca = ArcaWithKey(estadoClave) }, "arca_cuit_activo");

        Assert.Equal(PersonFilterOutcomes.Rechazo, filter.Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
        Assert.Equal(estadoClave, filter.Inputs["estadoClave"]);
        Assert.Contains($"\"{estadoClave}\"", filter.Evidence);
    }

    [Fact]
    public void CuitActivo_LongStatus_IsCappedInTheEvidence()
    {
        var filter = FilterOf(Empty() with { Arca = ArcaWithKey(new string('X', 200)) }, "arca_cuit_activo");

        Assert.Equal(PersonFilterOutcomes.Rechazo, filter.Outcome);
        Assert.Equal(40, filter.Inputs["estadoClave"]!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CuitActivo_NoStatusInTheSnapshot_IsSinDatos(string? estadoClave)
    {
        var filter = FilterOf(Empty() with { Arca = ArcaWithKey(estadoClave) }, "arca_cuit_activo");

        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        // The analyst reads Spanish, not the ARCA field name.
        Assert.DoesNotContain("estadoClave", filter.Evidence);
        Assert.StartsWith(
            "El snapshot de ARCA del legajo no tiene el estado de la clave: actualizá ARCA o verificá en ARCA",
            filter.Evidence);
    }

    [Fact]
    public void CuitActivo_NoSnapshot_IsSinDatos()
    {
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(Empty(), "arca_cuit_activo").Outcome);
    }

    [Fact]
    public void CuitActivo_SnapshotStoredBeforeTheKeyStatusWasKept_IsSinDatos()
    {
        // An ArcaFiscalData blob written before the field existed: System.Text.Json leaves it null.
        var old = System.Text.Json.JsonSerializer.Deserialize<Api.Web.FinanceIA.Models.Arca.ArcaFiscalData>(
            """{"EsSucesion":false,"Actividades":[],"Impuestos":[],"Regimenes":[]}""");

        var filter = FilterOf(Empty() with { Arca = old }, "arca_cuit_activo");

        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
    }

    // ── antiguedad_actividad ───────────────────────────────────────────────────────

    [Fact]
    public void Antiguedad_LegajoDateBelowTheRejectionAge_IsCappedAtAStrongAlert()
    {
        // The legajo's date is the same earliest-periodo approximation as the snapshot proxy: ARCA re-dates
        // periods, so the real start may be earlier, and it never suggests rejection on its own.
        var input = Empty() with { ActivityStartDate = new DateTime(2025, 12, 1) };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Equal("10", filter.Inputs["meses"]);
        Assert.Equal("true", filter.Inputs["aproximado"]);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
        Assert.Equal("ARCA · período de alta más antiguo (aproximación, legajo)", filter.Source);
        Assert.Contains("aproximada", filter.Evidence);
        Assert.Contains("el inicio real puede ser anterior", filter.Evidence);
        Assert.Contains("no sugiere rechazo", filter.Evidence);
    }

    [Fact]
    public void Antiguedad_LegajoDateElevenMonthsBack_IsAStrongAlert_NotRechazo()
    {
        // 01/11/2025 → 20/10/2026 is 11 whole months, below the 12-month default.
        var input = Empty() with { ActivityStartDate = new DateTime(2025, 11, 1) };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Equal("11", filter.Inputs["meses"]);
    }

    [Theory]
    [InlineData(2025, 10, "12")]   // the first month at or above pf_antiguedad_rechazo_meses
    [InlineData(2024, 11, "23")]   // the last month below pf_antiguedad_min_meses
    public void Antiguedad_LegajoDateBetweenTheTwoAges_IsAPlainAlert(int year, int month, string expectedMonths)
    {
        var input = Empty() with { ActivityStartDate = new DateTime(year, month, 1) };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Normal, filter.Severity);
        Assert.Equal(expectedMonths, filter.Inputs["meses"]);
    }

    [Theory]
    [InlineData(2024, 10, "24")]   // pf_antiguedad_min_meses exactly
    [InlineData(2015, 1, "141")]
    public void Antiguedad_LegajoDateAtOrAboveTheMinimum_IsOk(int year, int month, string expectedMonths)
    {
        var input = Empty() with { ActivityStartDate = new DateTime(year, month, 1) };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal(expectedMonths, filter.Inputs["meses"]);
    }

    [Fact]
    public void Antiguedad_LegajoDate_IsPreferredOverTheProxy()
    {
        // The snapshot's periods would say 2019 (Ok); the date ARCA wrote on the legajo says 10 months.
        var input = Empty() with
        {
            ActivityStartDate = new DateTime(2025, 12, 1),
            Arca              = Arca(false, "201901")
        };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Equal("2025-12-01", filter.Inputs["inicio"]);
        Assert.Equal("ARCA · período de alta más antiguo (aproximación, legajo)", filter.Source);
    }

    [Fact]
    public void Antiguedad_NoDateReachesRechazo_WhateverItsSource()
    {
        // Nothing provides a true inscription date today, so no input yields a rejection.
        PersonEvaluationInput[] inputs =
        [
            Empty() with { ActivityStartDate = new DateTime(2026, 9, 1) },
            Empty() with { Arca = Arca(false, "202609") },
            Empty() with { ActivityStartDate = new DateTime(2026, 9, 1), Arca = Arca(false, "202609") }
        ];

        Assert.All(inputs, input =>
            Assert.NotEqual(PersonFilterOutcomes.Rechazo, FilterOf(input, "antiguedad_actividad").Outcome));
    }

    [Fact]
    public void Antiguedad_Proxy_ReadsTheParserStartAcrossBothSections()
    {
        // The lists say 2026-01; FechaInicioActividad (from a section the lists dropped) says 2019-03.
        var input = Empty() with
        {
            Arca = Arca(false, "202601") with { FechaInicioActividad = new DateTime(2019, 3, 1) }
        };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal("2019-03-01", filter.Inputs["inicio"]);
        Assert.Equal("true", filter.Inputs["aproximado"]);
    }

    [Fact]
    public void Antiguedad_ProxyBelowTheRejectionAge_IsCappedAtAStrongAlert()
    {
        var input = Empty() with { Arca = Arca(false, "202601") };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Equal("true", filter.Inputs["aproximado"]);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
        Assert.Equal("ARCA · período de alta más antiguo de impuestos y actividades (aproximación)", filter.Source);
    }

    [Fact]
    public void Antiguedad_RejectionAgeIsATenantParameter()
    {
        var input = Empty() with
        {
            ActivityStartDate = new DateTime(2025, 12, 1),
            Parameters        = With(p => p["pf_antiguedad_rechazo_meses"] = 6m)
        };

        // 10 months: above the tenant's 6, so no longer the strong alert — a plain one, below the 24 minimum.
        var filter = FilterOf(input, "antiguedad_actividad");
        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Normal, filter.Severity);
    }

    [Fact]
    public void Antiguedad_FallsBackToTheEarliestArcaPeriod_AsAnApproximation()
    {
        var input = Empty() with { Arca = Arca(false, "202301", "201901") };

        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
        Assert.Equal("2019-01-01", filter.Inputs["inicio"]);
        Assert.Equal("true", filter.Inputs["aproximado"]);
    }

    [Fact]
    public void Antiguedad_NoDate_IsSinDatos()
    {
        var input = Empty() with { Arca = Arca(false) };
        var filter = FilterOf(input, "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        // Same kind as every other branch of this filter (#255): the source is the ARCA registry.
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
    }

    [Fact]
    public void Antiguedad_NoArcaSnapshotAtAll_IsSinDatos_AndVerificado()
    {
        var filter = FilterOf(Empty(), "antiguedad_actividad");

        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, filter.EvidenceKind);
    }

    // ── monotributo_tope ───────────────────────────────────────────────────────────

    [Fact]
    public void MonotributoTope_NotEmittedOutsideMonotributo()
    {
        var result = PersonEvaluationService.Evaluate(Empty(FiscalRegimes.ResponsableInscripto));
        Assert.DoesNotContain(result.Filters, f => f.Id == "monotributo_tope");
    }

    [Fact]
    public void MonotributoTope_SalesAboveTheCategoryCap_IsStrongAlert()
    {
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            MonotributoCategory = "A",
            Incomes             = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_100_000m))
        };

        var filter = FilterOf(input, "monotributo_tope");

        // Cap A of the ARCA table in force from 2026-08-01 is 12.009.410,45.
        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
        Assert.Equal("13200000", filter.Inputs["ventas12Meses"]);
        Assert.Equal("12009410.45", filter.Inputs["topeAnual"]);
        Assert.Equal("2026-08-01", filter.Inputs["tablaVigenteDesde"]);
        Assert.Contains("01/08/2026", filter.Evidence);
    }

    [Fact]
    public void MonotributoTope_SalesWithinTheCap_IsOk()
    {
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            MonotributoCategory = "K",
            Incomes             = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m))
        };

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "monotributo_tope").Outcome);
    }

    [Fact]
    public void MonotributoTope_NoCategory_IsSinDatos()
    {
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            Incomes = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_000_000m))
        };

        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "monotributo_tope").Outcome);
    }

    [Fact]
    public void MonotributoTope_NoSales_IsSinDatos()
    {
        var input = Empty(FiscalRegimes.Monotributo) with { MonotributoCategory = "C" };
        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "monotributo_tope").Outcome);
    }

    // ── ventas_acreedor_vs_fisco (as a filter) ─────────────────────────────────────

    [Fact]
    public void AcreedorVsFisco_CreditorAboveFiscoBeyondTolerance_IsStrongAlert()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 120_000_000m),
                Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };

        var filter = FilterOf(input, "ventas_acreedor_vs_fisco");

        Assert.Equal(PersonFilterOutcomes.Alerta, filter.Outcome);
        Assert.Equal(PersonSeverities.Fuerte, filter.Severity);
    }

    [Fact]
    public void AcreedorVsFisco_WithinTolerance_IsOk()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 105_000_000m),
                Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "ventas_acreedor_vs_fisco").Outcome);
    }

    [Fact]
    public void AcreedorVsFisco_FiscoAboveCreditor_IsOk()
    {
        var input = Empty() with
        {
            Incomes = Incomes(
                Year(LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado, 2025, 80_000_000m),
                Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(input, "ventas_acreedor_vs_fisco").Outcome);
    }

    [Fact]
    public void AcreedorVsFisco_NoPeriodWithBoth_IsSinDatos()
    {
        var input = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas, 2025, 100_000_000m))
        };

        Assert.Equal(PersonFilterOutcomes.SinDatos, FilterOf(input, "ventas_acreedor_vs_fisco").Outcome);
    }

    // ── The flag ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Suggestion_AnyRechazo_IsRechazoSugerido()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(situacion: 5)) };
        Assert.Equal(PersonSuggestions.RechazoSugerido, PersonEvaluationService.Evaluate(input).Suggestion);
    }

    [Fact]
    public void Suggestion_AlertsAndSinDatos_LeaveItToTheAnalyst()
    {
        // Everything missing is SinDatos, and strong alerts do not suggest rejection either.
        var input = Empty() with
        {
            Bcra = Bcra(Deuda(detalle: [new BcraEntidadDeudaDetalle { Situacion = 1, SituacionJuridica = true }]))
        };

        var result = PersonEvaluationService.Evaluate(input);

        Assert.Equal(PersonSuggestions.ParaDecisionDelAnalista, result.Suggestion);
        Assert.DoesNotContain(result.Filters, f => f.Outcome == PersonFilterOutcomes.Rechazo);
    }

    [Fact]
    public void Evaluate_IsDeterministic_AndCarriesTheVersion()
    {
        var input = Empty() with { Bcra = Bcra(Deuda(history: [(0, 2)])), Arca = Arca(false, "201901") };

        var first  = PersonEvaluationJson.Serialize(PersonEvaluationService.Evaluate(input));
        var second = PersonEvaluationJson.Serialize(PersonEvaluationService.Evaluate(input));

        Assert.Equal(first, second);
        Assert.Equal("0.10", PersonEvaluationService.Evaluate(input).Version);
    }
}
