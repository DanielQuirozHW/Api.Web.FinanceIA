// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-01

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;
using S = Api.Web.FinanceIA.Servicios.LegajoIntegrityService;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoIntegrityService"/> over the Phase 3a types (Ganancias, DDJJ IVA, DDJJ IIBB,
/// certificación de ingresos, monotributo): every check passing on the clean synthetic documents, failing
/// when one figure is off, skipped when an operand is missing; which paths a failure flags; the tolerance,
/// the unit scaling and the message conventions. Synthetic figures only.
/// </summary>
public sealed class LegajoIntegrityServicePhase3Tests
{
    private static readonly S Sut = new();
    private static readonly DateOnly PeriodEnd2025 = new(2025, 12, 31);

    private static LegajoIntegrityReport Evaluate(string documentType, Action<JsonObject>? mutate = null, DateOnly? periodEnd = null)
        => Sut.Evaluate(documentType, LegajoExtractionFixtures.NormalizedClean(documentType, mutate), periodEnd ?? PeriodEnd2025);

    private static void Set(JsonObject input, string pointer, JsonNode? value) => Assert.True(LegajoJsonPointer.Set(input, pointer, value));

    private static JsonArray Array(JsonObject input, string name) => input[name]!.AsArray();

    // ── Clean documents: every check runs and passes ───────────────────────────────

    [Fact]
    public void Ganancias_Clean_EveryCheckRunsAndPasses()
    {
        var report = Evaluate(DocumentTypes.Ganancias);

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal([S.GanJustificationOrigenesCheck, S.GanJustificationAplicacionesCheck, S.GanPatrimonioVariacionCheck,
                      S.GanFiscalYearPeriodCheck], report.Checks.Select(c => c.Name));
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void DdjjIva_Clean_EveryCheckRunsAndPasses()
    {
        var report = Evaluate(DocumentTypes.DdjjIva);

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal(
        [
            (S.IvaAlicuotaLineCheck, "2025-11/21"), (S.IvaAlicuotaLineCheck, "2025-11/10.5"), (S.IvaAlicuotaLineCheck, "2025-12/21"),
            (S.IvaDebitoLinesCheck, "2025-11"), (S.IvaSaldoTecnicoCheck, "2025-11"),
            (S.IvaDebitoLinesCheck, "2025-12"), (S.IvaSaldoTecnicoCheck, "2025-12"),
            (S.IvaPeriodCheck, (string?)null)
        ], report.Checks.Select(c => (c.Name, c.Subject)));
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void DdjjIibb_Clean_EveryCheckRunsAndPasses()
    {
        var report = Evaluate(DocumentTypes.DdjjIibb);

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal(4, report.Checks.Count(c => c.Name == S.IibbRowTaxCheck));
        Assert.Equal(["2025-11", "2025-12"], report.Checks.Where(c => c.Name == S.IibbTotalTaxCheck).Select(c => c.Subject));
        Assert.Single(report.Checks, c => c.Name == S.IibbPeriodCheck);
        Assert.Contains(report.Checks, c => c.Subject == "2025-11/Buenos Aires");
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void CertificacionIngresos_Clean_InMiles_EveryCheckRunsAndPasses()
    {
        var report = Evaluate(DocumentTypes.CertificacionIngresos);

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal("Miles", report.AmountsUnit);
        Assert.Equal([S.CiRowsTotalCheck, S.CiPeriodCheck], report.Checks.Select(c => c.Name));
        Assert.Equal(3_500_500m, report.Checks[0].Actual);   // scaled to pesos
        Assert.Empty(report.Skipped);
    }

    [Fact]
    public void Monotributo_Clean_EveryCheckRunsAndPasses()
    {
        var report = Evaluate(DocumentTypes.Monotributo);

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal([S.MonoNonNegativeCheck, S.MonoPeriodCheck], report.Checks.Select(c => c.Name));
        Assert.Empty(report.Skipped);
    }

    // ── Ganancias ───────────────────────────────────────────────────────────────

    [Fact]
    public void Ganancias_OrigenesDoNotAddUp_Fails_FlaggingOnlyTheTotal()
    {
        var report = Evaluate(DocumentTypes.Ganancias, i => Set(i, "/justification/totalOrigenes/value", 12_600_000.50m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.GanJustificationOrigenesCheck, check.Name);
        Assert.Equal(12_500_000.50m, check.Expected);
        Assert.Equal(12_600_000.50m, check.Actual);
        Assert.Equal(["/justification/totalOrigenes", "/justification/lines/0/amount", "/justification/lines/1/amount"], check.FieldPaths);
        Assert.Equal(["/justification/totalOrigenes"], check.FlaggedPaths);
        Assert.Equal("La suma de las líneas de orígenes de la justificación patrimonial ($12.500.001) no coincide con el " +
                     "total de orígenes informado ($12.600.001).", check.Message);
    }

    [Fact]
    public void Ganancias_AplicacionesDoNotAddUp_Fails()
    {
        var report = Evaluate(DocumentTypes.Ganancias, i => Set(i, "/justification/lines/3/amount", 300_000m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.GanJustificationAplicacionesCheck, check.Name);
        Assert.Equal(["/justification/totalAplicaciones"], check.FlaggedPaths);
        Assert.Contains("/justification/lines/3/amount", check.FieldPaths);
    }

    [Fact]
    public void Ganancias_VariacionOff_Fails_FlaggingTheVariacion()
    {
        var report = Evaluate(DocumentTypes.Ganancias, i => Set(i, "/justification/variacionPatrimonial/value", 7_000_000m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.GanPatrimonioVariacionCheck, check.Name);
        Assert.Equal(["/justification/variacionPatrimonial"], check.FlaggedPaths);
        Assert.Equal("La variación patrimonial informada ($7.000.000) no coincide con el patrimonio al cierre menos el " +
                     "patrimonio al inicio ($6.000.000).", check.Message);
    }

    [Fact]
    public void Ganancias_NegativeCategoryResult_IsNotAnError()
    {
        var report = Evaluate(DocumentTypes.Ganancias, i => Set(i, "/categoryResults/2/amount", -900_000m));

        Assert.True(report.Passed);
    }

    [Fact]
    public void Ganancias_WithoutJustification_SkipsItsChecks_StillChecksTheFiscalYear()
    {
        var report = Evaluate(DocumentTypes.Ganancias, i => i["justification"] = null, periodEnd: new DateOnly(2024, 12, 31));

        Assert.Contains(new LegajoIntegritySkip(S.GanJustificationOrigenesCheck), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.GanJustificationAplicacionesCheck), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.GanPatrimonioVariacionCheck), report.Skipped);

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.GanFiscalYearPeriodCheck, check.Name);
        Assert.Equal("El período fiscal informado (2025) no coincide con el año de cierre del documento (2024).", check.Message);
    }

    [Fact]
    public void Ganancias_VariacionNotPrinted_OrTotalMissing_Skip()
    {
        var report = Evaluate(DocumentTypes.Ganancias, i =>
        {
            Set(i, "/justification/variacionPatrimonial/value", null);
            Set(i, "/justification/totalAplicaciones/value", null);
        });

        Assert.True(report.Passed);
        Assert.Contains(new LegajoIntegritySkip(S.GanPatrimonioVariacionCheck), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.GanJustificationAplicacionesCheck), report.Skipped);
    }

    [Fact]
    public void Ganancias_Tolerance_RelativeHalfPercent()
    {
        // 0.5 in a hundred of ~12.560.000 is ~62.800.
        Assert.True(Evaluate(DocumentTypes.Ganancias, i => Set(i, "/justification/totalOrigenes/value", 12_560_000m)).Passed);
        Assert.False(Evaluate(DocumentTypes.Ganancias, i => Set(i, "/justification/totalOrigenes/value", 12_570_000m)).Passed);
    }

    // ── DDJJ IVA ────────────────────────────────────────────────────────────────

    [Fact]
    public void Iva_AlicuotaLineOff_Fails_FlaggingTheWholeLine_WithinTheDebitoTolerance()
    {
        // 22.000 instead of 200.000 × 10,5 = 21.000: the line fails; the débito total (231.000 vs 230.000) is
        // within 0.5 in a hundred, so only the line is reported.
        var report = Evaluate(DocumentTypes.DdjjIva, i => Set(i, "/alicuotaLines/1/impuesto", 22_000m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.IvaAlicuotaLineCheck, check.Name);
        Assert.Equal("2025-11/10.5", check.Subject);
        Assert.Equal(21_000m, check.Expected);
        Assert.Null(check.FlaggedPaths);   // two peers: every operand is flagged
        Assert.Equal(["/alicuotaLines/1/impuesto", "/alicuotaLines/1/netoGravado", "/alicuotaLines/1/alicuotaPct"], check.FieldPaths);
        Assert.Equal("El IVA de la línea de débito con alícuota 10,5 del período 11/2025 ($22.000) no coincide con el neto " +
                     "gravado por la alícuota ($21.000).", check.Message);
    }

    [Fact]
    public void Iva_CreditoLines_AreNeverChecked()
    {
        // Prorrateo and crédito no computable legitimately break neto × alícuota on the crédito side.
        var report = Evaluate(DocumentTypes.DdjjIva, i => Set(i, "/alicuotaLines/3/impuesto", 90_000m));

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Checks, c => c.FieldPaths.Contains("/alicuotaLines/3/impuesto"));
    }

    [Fact]
    public void Iva_DebitoTotalOff_FailsBothTheLinesAndTheSaldo_FlaggingTheirTotals()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, i => Set(i, "/periods/0/debitoFiscal", 240_000m));

        Assert.Equal([S.IvaDebitoLinesCheck, S.IvaSaldoTecnicoCheck], report.Failed.Select(c => c.Name));
        var debito = report.Failed.First();
        Assert.Equal(["/periods/0/debitoFiscal"], debito.FlaggedPaths);
        Assert.Equal(["/periods/0/debitoFiscal", "/alicuotaLines/0/impuesto", "/alicuotaLines/1/impuesto", "/alicuotaLines/2/impuesto"],
                     debito.FieldPaths);
        Assert.Equal("La suma de las líneas de débito del período 11/2025 ($230.000) no coincide con el débito fiscal " +
                     "informado ($240.000).", debito.Message);

        var saldo = report.Failed.Last();
        Assert.Equal(["/periods/0/saldoTecnico/amount", "/periods/0/saldoTecnico/side"], saldo.FlaggedPaths);
    }

    [Fact]
    public void Iva_SaldoTecnico_OnTheWrongSide_Fails_InWords()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, i => Set(i, "/periods/0/saldoTecnico/side", "Contribuyente"));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.IvaSaldoTecnicoCheck, check.Name);
        Assert.Equal(100_000m, check.Expected);
        Assert.Equal(-100_000m, check.Actual);
        Assert.Equal("El saldo técnico informado para el período 11/2025 ($100.000 a favor del contribuyente) no coincide " +
                     "con el débito fiscal menos el crédito fiscal y el saldo técnico a favor del período anterior " +
                     "($100.000 a favor del fisco).", check.Message);
    }

    [Fact]
    public void Iva_SaldoTecnico_WithoutThePreviousSaldo_OrNotPrinted_IsSkipped()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, i =>
        {
            Set(i, "/periods/0/saldoTecnicoAnterior", null);
            Set(i, "/periods/1/saldoTecnico", null);
        });

        Assert.True(report.Passed);
        Assert.Contains(new LegajoIntegritySkip(S.IvaSaldoTecnicoCheck, "2025-11"), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.IvaSaldoTecnicoCheck, "2025-12"), report.Skipped);
    }

    [Fact]
    public void Iva_ARectificativa_DoesNotMixWithItsOriginal()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, i =>
        {
            var rectificativa = Array(i, "periods")[0]!.DeepClone().AsObject();
            rectificativa["rectificativaNumber"] = 1;
            rectificativa["debitoFiscal"]        = 249_900m;
            rectificativa["saldoTecnico"]        = null;
            Array(i, "periods").Add(rectificativa);

            var line = Array(i, "alicuotaLines")[0]!.DeepClone().AsObject();
            line["rectificativaNumber"] = 1;
            line["netoGravado"]         = 1_190_000m;
            line["impuesto"]            = 249_900m;
            Array(i, "alicuotaLines").Add(line);
        });

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal(["2025-11", "2025-12", "2025-11/R1"],
                     report.Checks.Where(c => c.Name == S.IvaDebitoLinesCheck).Select(c => c.Subject));
        Assert.Equal(["/periods/2/debitoFiscal", "/alicuotaLines/6/impuesto"],
                     report.Checks.Single(c => c is { Name: S.IvaDebitoLinesCheck, Subject: "2025-11/R1" }).FieldPaths);
    }

    [Fact]
    public void Iva_LinesWithoutTheirPeriod_DoNotJoinAnyDdjj()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, i => Set(i, "/alicuotaLines/4/period", null));

        Assert.Contains(new LegajoIntegritySkip(S.IvaDebitoLinesCheck, "2025-12"), report.Skipped);
        Assert.Contains(report.Checks, c => c is { Name: S.IvaAlicuotaLineCheck, Subject: "sin-periodo/21" });
        Assert.True(report.Passed);
    }

    [Fact]
    public void Iva_LatestPeriodOtherThanThePeriodEnd_Fails()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, periodEnd: new DateOnly(2026, 1, 31));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.IvaPeriodCheck, check.Name);
        Assert.Equal(202601m, check.Expected);
        Assert.Equal(202512m, check.Actual);
        Assert.Equal(1m, check.Diff);
        Assert.Equal(["/periods/1/period"], check.FieldPaths);
        Assert.Equal("El último período de IVA informado (12/2025) no coincide con el mes de cierre del documento (01/2026).",
                     check.Message);
    }

    [Fact]
    public void Iva_InMiles_ScalesTheAmounts_NeverTheAlicuota()
    {
        var report = Evaluate(DocumentTypes.DdjjIva, i => Set(i, "/header/amountsUnit/value", "Miles"));

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal(230_000_000m, report.Checks.First(c => c.Name == S.IvaDebitoLinesCheck).Actual);
        Assert.Equal(210_000_000m, report.Checks.First(c => c.Name == S.IvaAlicuotaLineCheck).Expected);
    }

    // ── DDJJ Ingresos Brutos ────────────────────────────────────────────────────

    [Fact]
    public void Iibb_RowTaxOff_FailsTheRow_AndItsDdjjTotal()
    {
        var report = Evaluate(DocumentTypes.DdjjIibb, i => Set(i, "/rows/0/impuestoDeterminado", 36_000m));

        var row = Assert.Single(report.Failed, c => c.Name == S.IibbRowTaxCheck);
        Assert.Equal("2025-11/Buenos Aires", row.Subject);
        Assert.Null(row.FlaggedPaths);
        Assert.Equal(["/rows/0/impuestoDeterminado", "/rows/0/baseImponible", "/rows/0/alicuotaPct"], row.FieldPaths);
        Assert.Equal("El impuesto determinado de Buenos Aires en el período 11/2025 ($36.000) no coincide con la base " +
                     "imponible por la alícuota 3,5 ($35.000).", row.Message);

        var total = Assert.Single(report.Failed, c => c.Name == S.IibbTotalTaxCheck);
        Assert.Equal(["/periods/0/impuestoTotal"], total.FlaggedPaths);
    }

    [Fact]
    public void Iibb_TotalOff_Fails_FlaggingOnlyTheTotal()
    {
        var report = Evaluate(DocumentTypes.DdjjIibb, i => Set(i, "/periods/1/impuestoTotal", 80_000m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.IibbTotalTaxCheck, check.Name);
        Assert.Equal("2025-12", check.Subject);
        Assert.Equal(["/periods/1/impuestoTotal"], check.FlaggedPaths);
        Assert.Equal("La suma del impuesto determinado por jurisdicción del período 12/2025 ($72.000) no coincide con el " +
                     "impuesto total informado ($80.000).", check.Message);
    }

    [Fact]
    public void Iibb_RowWithoutItsAlicuota_IsSkipped()
    {
        var report = Evaluate(DocumentTypes.DdjjIibb, i => Set(i, "/rows/1/alicuotaPct", null));

        Assert.Contains(new LegajoIntegritySkip(S.IibbRowTaxCheck, "2025-11/CABA"), report.Skipped);
        Assert.True(report.Passed);
    }

    private static void Annual(JsonObject input, int? fiscalYear)
    {
        Set(input, "/header/fiscalYear/value", fiscalYear);
        foreach (var name in new[] { "periods", "rows" })
            foreach (var row in Array(input, name))
                row!["period"] = null;

        // One annual DDJJ: its rows are the four jurisdiction lines.
        Array(input, "periods").RemoveAt(1);
        Set(input, "/periods/0/impuestoTotal", 132_000m);
    }

    [Fact]
    public void Iibb_AnnualDdjj_ChecksTheFiscalYear_AndItsTotal()
    {
        var report = Evaluate(DocumentTypes.DdjjIibb, i => Annual(i, 2025));

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal("anual", report.Checks.Single(c => c.Name == S.IibbTotalTaxCheck).Subject);
        Assert.Contains(report.Checks, c => c is { Name: S.IibbRowTaxCheck, Subject: "anual/CABA" });
        Assert.Equal(["/header/fiscalYear"], report.Checks.Single(c => c.Name == S.IibbPeriodCheck).FieldPaths);

        var wrongYear = Evaluate(DocumentTypes.DdjjIibb, i => Annual(i, 2024));
        Assert.Equal(S.IibbPeriodCheck, Assert.Single(wrongYear.Failed).Name);

        var noYear = Evaluate(DocumentTypes.DdjjIibb, i => Annual(i, null));
        Assert.Contains(new LegajoIntegritySkip(S.IibbPeriodCheck), noYear.Skipped);
    }

    // ── Certificación de ingresos ───────────────────────────────────────────────

    [Fact]
    public void Ci_RowsDoNotAddUp_Fails_FlaggingTheTotal_QuotedInMiles()
    {
        var report = Evaluate(DocumentTypes.CertificacionIngresos, i => Set(i, "/totals/totalIngresos/value", 3_600m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.CiRowsTotalCheck, check.Name);
        Assert.Equal(["/totals/totalIngresos"], check.FlaggedPaths);
        Assert.Equal(["/totals/totalIngresos", "/rows/0/amount", "/rows/1/amount", "/rows/2/amount"], check.FieldPaths);
        Assert.Equal("La suma de los ingresos certificados ($3.501, en miles) no coincide con el total de ingresos " +
                     "informado ($3.600, en miles).", check.Message);
    }

    [Fact]
    public void Ci_PeriodToOtherThanThePeriodEnd_Fails_AbsentSkips()
    {
        var report = Evaluate(DocumentTypes.CertificacionIngresos, i => Set(i, "/header/periodTo/value", "2025-11-30"));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.CiPeriodCheck, check.Name);
        Assert.Equal(["/header/periodTo"], check.FieldPaths);
        Assert.Equal("El fin del período certificado (11/2025) no coincide con el mes de cierre del documento (12/2025).",
                     check.Message);

        var absent = Sut.Evaluate(DocumentTypes.CertificacionIngresos,
            LegajoExtractionFixtures.NormalizedClean(DocumentTypes.CertificacionIngresos), periodEnd: null);
        Assert.Contains(new LegajoIntegritySkip(S.CiPeriodCheck), absent.Skipped);
    }

    [Fact]
    public void Ci_WithoutRows_SkipsTheSum()
    {
        var report = Evaluate(DocumentTypes.CertificacionIngresos, i => i["rows"] = new JsonArray());

        Assert.Contains(new LegajoIntegritySkip(S.CiRowsTotalCheck), report.Skipped);
        Assert.True(report.Passed);
    }

    [Fact]
    public void Ci_Tolerance_HalfPercentPasses_JustAboveFails()
    {
        // 0.5 in a hundred of ~3.518.000 pesos is ~17.590 (17,6 printed units, en miles).
        Assert.True(Evaluate(DocumentTypes.CertificacionIngresos, i => Set(i, "/totals/totalIngresos/value", 3_518m)).Passed);
        Assert.False(Evaluate(DocumentTypes.CertificacionIngresos, i => Set(i, "/totals/totalIngresos/value", 3_519m)).Passed);
    }

    // ── Monotributo ─────────────────────────────────────────────────────────────

    [Fact]
    public void Mono_NegativePayment_Fails_WithItsPath()
    {
        var report = Evaluate(DocumentTypes.Monotributo, i => Set(i, "/payments/1/amount", -42_000.50m));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.MonoNonNegativeCheck, check.Name);
        Assert.Equal(["/payments/1/amount"], check.FieldPaths);
        Assert.Equal("1 importe informado es negativo; la cuota y los pagos del monotributo no pueden ser negativos.", check.Message);
    }

    [Fact]
    public void Mono_LatestPaymentOtherThanThePeriodEnd_Fails()
    {
        var report = Evaluate(DocumentTypes.Monotributo, i => Array(i, "payments").RemoveAt(2));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.MonoPeriodCheck, check.Name);
        Assert.Equal(["/payments/1/period"], check.FieldPaths);
        Assert.Equal("El último pago informado corresponde a 11/2025 y no coincide con el mes de cierre del documento (12/2025).",
                     check.Message);
    }

    [Fact]
    public void Mono_ConstanciaWithoutPayments_SkipsThePeriod_StillChecksTheCuota()
    {
        var report = Evaluate(DocumentTypes.Monotributo, i => i["payments"] = new JsonArray());

        Assert.Contains(new LegajoIntegritySkip(S.MonoPeriodCheck), report.Skipped);
        Assert.Single(report.Checks, c => c.Name == S.MonoNonNegativeCheck);

        var nothing = Evaluate(DocumentTypes.Monotributo, i =>
        {
            i["payments"] = new JsonArray();
            Set(i, "/header/cuotaMensual/value", null);
        });
        Assert.Contains(new LegajoIntegritySkip(S.MonoNonNegativeCheck), nothing.Skipped);
    }

    // ── Conventions ─────────────────────────────────────────────────────────────

    [Fact]
    public void FailedMessages_NeverContainAPercentSign()
    {
        var reports = new[]
        {
            Evaluate(DocumentTypes.Ganancias, i =>
            {
                Set(i, "/justification/totalOrigenes/value", 1m);
                Set(i, "/justification/totalAplicaciones/value", 1m);
                Set(i, "/justification/variacionPatrimonial/value", 1m);
            }, new DateOnly(2020, 1, 1)),
            Evaluate(DocumentTypes.DdjjIva, i =>
            {
                Set(i, "/alicuotaLines/1/impuesto", 1m);
                Set(i, "/periods/0/debitoFiscal", 1m);
            }, new DateOnly(2020, 1, 1)),
            Evaluate(DocumentTypes.DdjjIibb, i => Set(i, "/rows/0/impuestoDeterminado", 1m), new DateOnly(2020, 1, 1)),
            Evaluate(DocumentTypes.CertificacionIngresos, i => Set(i, "/totals/totalIngresos/value", 1m), new DateOnly(2020, 1, 1)),
            Evaluate(DocumentTypes.Monotributo, i => Set(i, "/header/cuotaMensual/value", -1m), new DateOnly(2020, 1, 1))
        };

        var failed = reports.SelectMany(r => r.Failed).ToList();
        Assert.True(failed.Count >= 12, failed.Count.ToString());
        Assert.All(failed, c => Assert.DoesNotContain("%", c.Message));
        Assert.All(failed, c => Assert.False(string.IsNullOrWhiteSpace(c.Message)));
    }

    [Fact]
    public void TotalVsPartsChecks_FlagOnlyOperandsOfTheirOwn()
    {
        foreach (var type in new[] { DocumentTypes.Ganancias, DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb,
                                     DocumentTypes.CertificacionIngresos, DocumentTypes.Monotributo })
        {
            foreach (var check in Evaluate(type).Checks.Where(c => c.FlaggedPaths is not null))
            {
                Assert.NotEmpty(check.FlaggedPaths!);
                Assert.All(check.FlaggedPaths!, path => Assert.Contains(path, check.FieldPaths));
            }
        }
    }

    [Fact]
    public void Evaluate_NeverModifiesItsInput()
    {
        foreach (var type in LegajoExtractionFixtures.Schemas.Keys)
        {
            var tree   = LegajoExtractionFixtures.NormalizedClean(type);
            var before = tree.ToJsonString();

            Sut.Evaluate(type, tree, PeriodEnd2025);

            Assert.Equal(before, tree.ToJsonString());
        }
    }
}
