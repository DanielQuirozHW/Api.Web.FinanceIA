// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios.NosisReport;
using UglyToad.PdfPig;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>
/// <see cref="NosisManagerReportParser"/> over SYNTHETIC reports (<see cref="NosisReportPdfBuilder"/>):
/// recognition, every section, US-style numbers, "miles de $", section isolation, the NOSIS RC4
/// owner-password encryption, and — the point of the feature — that nothing personal reaches the JSON.
/// </summary>
public sealed class NosisManagerReportParserTests
{
    private static readonly NosisManagerReportParser Parser = new();

    private static NosisReportParseResult Parse(NosisReportPdfBuilder builder)
        => Parser.Parse(builder.Build(), CancellationToken.None);

    private static NosisManagerReport Parsed(NosisReportPdfBuilder builder)
    {
        var result = Parse(builder);
        Assert.Equal(NosisReportParseOutcome.Parsed, result.Outcome);
        return result.Report!;
    }

    private static string Json(NosisManagerReport report) => JsonSerializer.Serialize(report, NosisManagerReport.JsonOptions);

    // ── Recognition ───────────────────────────────────────────────────────────

    [Fact]
    public void A_full_report_parses_every_section_ok()
    {
        var result = Parse(new NosisReportPdfBuilder());

        Assert.Equal(NosisReportParseOutcome.Parsed, result.Outcome);
        Assert.Equal("30000000001", result.SubjectCuitDigits);
        Assert.Equal(new DateTime(2026, 11, 3, 14, 36, 0), result.ReportDate);
        Assert.True(result.PageCount >= 2);

        var report = result.Report!;
        Assert.Equal("2026-11-03T14:36:00", report.ReportDate);
        Assert.Equal(NosisManagerReportParser.ParserVersion, report.ParserVersion);
        Assert.Equal(NosisReportSectionKeys.All, report.Sections.Keys);
        foreach (var (key, status) in report.Sections)
            Assert.True(status == NosisReportSectionKeys.StatusOk, $"{key} was {status}");
    }

    [Fact]
    public void The_nosis_rc4_owner_password_encryption_opens_with_the_empty_user_password()
    {
        var bytes = new NosisReportPdfBuilder { Encrypt = true }.Build();

        using (var document = PdfDocument.Open(bytes))
            Assert.True(document.IsEncrypted);

        var result = Parser.Parse(bytes, CancellationToken.None);
        Assert.Equal(NosisReportParseOutcome.Parsed, result.Outcome);
        Assert.All(result.Report!.Sections.Values, s => Assert.Equal(NosisReportSectionKeys.StatusOk, s));
    }

    [Fact]
    public void A_pdf_that_is_not_a_nosis_report_is_not_recognized()
    {
        var result = Parser.Parse(NosisReportPdfBuilder.PlainPdf("Estado de situación patrimonial", "Activo 1,000"), CancellationToken.None);

        Assert.Equal(NosisReportParseOutcome.NotRecognized, result.Outcome);
        Assert.Equal(NosisReportNotRecognizedReason.NoTitle, result.Reason);
        Assert.Null(result.Report);
    }

    [Fact]
    public void Bytes_that_are_not_a_pdf_are_unopenable()
    {
        var result = Parser.Parse("%PDF-1.7 not really"u8.ToArray(), CancellationToken.None);

        Assert.Equal(NosisReportParseOutcome.NotRecognized, result.Outcome);
        Assert.Equal(NosisReportNotRecognizedReason.Unopenable, result.Reason);
    }

    [Fact]
    public void Footers_naming_another_cuit_are_two_reports_and_not_recognized()
    {
        var result = Parse(new NosisReportPdfBuilder { LastPageFooterCuit = "30000000002" });

        Assert.Equal(NosisReportParseOutcome.NotRecognized, result.Outcome);
        Assert.Equal(NosisReportNotRecognizedReason.CuitDisagreement, result.Reason);
        Assert.Null(result.SubjectCuitDigits);
    }

    [Fact]
    public void A_footer_without_its_date_is_not_recognized()
    {
        var result = Parse(new NosisReportPdfBuilder { FooterDate = "fecha" });

        Assert.Equal(NosisReportParseOutcome.NotRecognized, result.Outcome);
        Assert.Equal(NosisReportNotRecognizedReason.NoReportDate, result.Reason);
    }

    [Fact]
    public void The_report_cuit_is_returned_for_comparison_whatever_its_format()
    {
        var result = Parse(new NosisReportPdfBuilder { TitleCuit = "30000000002", FooterCuit = "30-00000000-2" });

        Assert.Equal(NosisReportParseOutcome.Parsed, result.Outcome);
        Assert.Equal("30000000002", result.SubjectCuitDigits);
    }

    // ── Section isolation ─────────────────────────────────────────────────────

    [Fact]
    public void A_missing_section_is_absent_and_the_others_stay_ok()
    {
        var report = Parsed(new NosisReportPdfBuilder().Without("evolucion"));

        Assert.Equal(NosisReportSectionKeys.StatusAbsent, report.Sections[NosisReportSectionKeys.Evolucion24m]);
        Assert.Null(report.Evolucion24m);
        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.DeudaPorEntidad]);
        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.ChequesRechazadosPorMes]);
    }

    [Theory]
    [InlineData("evolucion", NosisReportSectionKeys.Evolucion24m)]
    [InlineData("bureau", NosisReportSectionKeys.DeudaPorEntidad)]
    [InlineData("atrasos", NosisReportSectionKeys.Atrasos)]
    [InlineData("aportes", NosisReportSectionKeys.AportesPatronales)]
    [InlineData("cheques", NosisReportSectionKeys.ChequesRechazadosPorMes)]
    [InlineData("criterios", NosisReportSectionKeys.CriteriosAceptacion)]
    [InlineData("juicios", NosisReportSectionKeys.Juicios)]
    [InlineData("sisa", NosisReportSectionKeys.Sisa)]
    [InlineData("srt", NosisReportSectionKeys.Srt)]
    [InlineData("referencias", NosisReportSectionKeys.ReferenciasComerciales)]
    public void A_garbled_section_is_unreadable_and_only_that_one(string builderKey, string sectionKey)
    {
        var builder = new NosisReportPdfBuilder();
        if (builderKey == "cheques")
            builder.Replace("cheques", [[("lorem ipsum dolor", 40f)]]);
        else
            builder.Garble(builderKey);

        var report = Parsed(builder);

        Assert.Equal(NosisReportSectionKeys.StatusUnreadable, report.Sections[sectionKey]);
        foreach (var (key, status) in report.Sections.Where(s => s.Key != sectionKey))
            Assert.True(status == NosisReportSectionKeys.StatusOk, $"{key} was {status}");
    }

    [Fact]
    public void A_report_without_any_summary_heading_has_an_absent_summary()
    {
        var report = Parsed(new NosisReportPdfBuilder().Without("cabecera").Without("perfil").Without("arca").Without("mipyme").Without("resumen"));

        Assert.Equal(NosisReportSectionKeys.StatusAbsent, report.Sections[NosisReportSectionKeys.Summary]);
        Assert.Null(report.Summary);
    }

    [Fact]
    public void A_section_that_says_none_is_ok_and_empty()
    {
        var report = Parsed(new NosisReportPdfBuilder()
            .Replace("cheques", [[("No registra cheques rechazados", 40f)]])
            .Replace("juicios", [[("No registra juicios como demandado", 40f)]]));

        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.ChequesRechazadosPorMes]);
        Assert.Empty(report.ChequesRechazadosPorMes!);
        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.Juicios]);
        Assert.Equal(0, report.Juicios!.ComoDemandadoCantidad);
        Assert.Empty(report.Juicios.Items);
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_reads_the_whitelisted_labels_with_us_numbers()
    {
        var s = Parsed(new NosisReportPdfBuilder()).Summary!;

        // From the header row (742, 3), not the "Score: 750" / "Situación BCRA: 1" label lines.
        Assert.Equal(742, s.Score);
        Assert.Equal(3, s.SituacionBcra);
        Assert.Equal(7_258_316_409m, s.EndeudamientoTotal);
        Assert.Equal(31_472.6m, s.CompromisosMensuales);
        Assert.Equal(100_000_000m, s.FacturacionEstimadaMin);
        Assert.Equal(250_000_000m, s.FacturacionEstimadaMax);
        Assert.True(s.TieneChequesRechazados);
        Assert.Equal(2, s.ReferenciasComercialesCantidad);
        Assert.False(s.AportesImpagos12m);
        Assert.Equal(4, s.Consultas3m);
        Assert.Equal(11, s.Consultas12m);
        Assert.Equal(23, s.AntiguedadAnios);
        Assert.Equal("10 a 49", s.EmpleadosRango);
        Assert.Equal("Fabricación de piezas metálicas", s.ActividadPrincipal);
        Assert.Equal(new NosisReportCertificadoMiPyme { Vigente = true, Desde = new DateOnly(2026, 1, 1), Hasta = new DateOnly(2026, 12, 31) }, s.CertificadoMiPyme);
        Assert.Equal(new DateOnly(2003, 3, 15), s.Arca!.Inscripcion);
        Assert.Equal("Inscripto", s.Arca.Ganancias);
        Assert.Equal("Responsable Inscripto", s.Arca.Iva);
    }

    [Fact]
    public void Evolucion_reads_24_months_under_their_years_in_ascending_order()
    {
        var months = Parsed(new NosisReportPdfBuilder()).Evolucion24m!;

        Assert.Equal(24, months.Count);
        Assert.Equal("2024-10", months[0].Periodo);
        Assert.Equal("2025-01", months[3].Periodo);
        Assert.Equal("2026-09", months[^1].Periodo);
        Assert.Equal(months.Select(m => m.Periodo).Order(StringComparer.Ordinal), months.Select(m => m.Periodo));

        Assert.Equal(700, months[0].Score);
        Assert.Equal(723, months[^1].Score);
        Assert.Equal(3, months[0].CantidadBancos);
        Assert.Equal(2, months[5].Situacion);
        Assert.Null(months[0].CantidadTarjetas);          // "-"
        Assert.Equal(5, months[^1].ChequesRechazados);
        Assert.Equal(0, months[^1].ChequesRecuperados);
        Assert.Equal(5, months[^1].ChequesNoRecuperados);
        Assert.Equal(0, months[^1].ChequesNoPagoMulta);
        Assert.Equal(1, months[^1].ChequesPositivos);
        Assert.Equal("P", months[0].AportesEmpleador);
        Assert.Equal("PP", months[^2].AportesEmpleador);
        Assert.Equal("I", months[^1].AportesEmpleador);
        Assert.Null(months[0].ReferenciasComerciales);
        Assert.Equal(2, months[^1].ConsultasRecibidas);
    }

    [Fact]
    public void Evolucion_with_numeric_months_places_every_value_by_its_column_and_leaves_blanks_null()
    {
        var months = Parsed(new NosisReportPdfBuilder().Replace("evolucion", NosisReportPdfBuilder.NumericEvolucionRows())).Evolucion24m!;

        Assert.Equal(24, months.Count);
        Assert.Equal("2024-12", months[0].Periodo);
        Assert.Equal("2025-01", months[1].Periodo);
        Assert.Equal("2025-12", months[12].Periodo);
        Assert.Equal("2026-01", months[13].Periodo);
        Assert.Equal("2026-11", months[23].Periodo);

        for (var i = 0; i < 24; i++)
        {
            Assert.Equal(800 + i, months[i].Score);
            Assert.Equal(i is 3 or 10 ? null : 1, months[i].Situacion);
            Assert.Equal(i == 0 ? null : 4, months[i].CantidadBancos);
        }
    }

    [Fact]
    public void The_header_score_ignores_the_gauge_and_the_two_situaciones_are_kept_as_printed()
    {
        var report = Parsed(new NosisReportPdfBuilder());

        Assert.Equal(742, report.Summary!.Score);          // not the needle 735, nor the scale's 1 / 999
        Assert.Equal(3, report.Summary.SituacionBcra);       // the header's
        Assert.Equal(1, report.Evolucion24m![^1].Situacion); // the evolution's last month, unchanged
    }

    [Fact]
    public void Without_a_header_row_the_score_label_line_is_used()
    {
        var report = Parsed(new NosisReportPdfBuilder().Replace("cabecera", [[("Razón social: x", 40f)]]));

        Assert.Equal(750, report.Summary!.Score);
        Assert.Equal(1, report.Summary.SituacionBcra);
    }

    [Fact]
    public void Deuda_por_entidad_is_in_pesos_when_the_group_says_miles_de_pesos()
    {
        var deuda = Parsed(new NosisReportPdfBuilder()).DeudaPorEntidad!;

        Assert.Equal(["Banco Ficticio Uno", "Banco Ficticio Dos"], deuda.Select(d => d.Entidad));
        var uno = deuda[0].Periodos;
        Assert.Equal(["2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09"], uno.Select(p => p.Periodo));
        Assert.Equal(1_200_000m, uno[0].Monto);
        Assert.Equal(1, uno[0].Situacion);
        Assert.Equal(2, uno[3].Situacion);
        Assert.Equal(950_500m, uno[5].Monto);

        var dos = deuda[1].Periodos;
        Assert.Null(dos[0].Situacion);
        Assert.Null(dos[0].Monto);
        Assert.Equal(300_000m, dos[1].Monto);
    }

    [Fact]
    public void Without_the_miles_note_amounts_are_taken_as_printed()
    {
        var report = Parsed(new NosisReportPdfBuilder { Thousands = false });

        Assert.Equal(1_200m, report.DeudaPorEntidad![0].Periodos[0].Monto);
        Assert.Equal(1_000m, report.Atrasos![0].Normal);
    }

    [Fact]
    public void Atrasos_reads_each_bucket_per_month_in_pesos()
    {
        var atrasos = Parsed(new NosisReportPdfBuilder()).Atrasos!;

        Assert.Equal(["2026-07", "2026-08", "2026-09"], atrasos.Select(a => a.Periodo));
        Assert.Equal(1_000_000m, atrasos[0].Normal);
        Assert.Equal(10_500m, atrasos[0].D31a90);
        Assert.Null(atrasos[1].D31a90);
        Assert.Equal(0m, atrasos[2].D31a90);
        Assert.Null(atrasos[2].D91a180);
        Assert.Null(atrasos[2].D181a365);
        Assert.Equal(2_000m, atrasos[2].Mayor365);
    }

    [Fact]
    public void Atrasos_also_reads_a_row_per_period_under_named_bucket_columns()
    {
        var report = Parsed(new NosisReportPdfBuilder().Replace("atrasos",
        [
            [("Período", 40f), ("Normal", 120f), ("31 a 90", 200f), ("91 a 180", 280f), ("181 a 365", 360f), ("Mayor a 365", 440f)],
            [("09/2026", 40f), ("1,200,000", 120f), ("-", 200f), ("-", 280f), ("-", 360f), ("2", 440f)],
            [("08/2026", 40f), ("1,100", 120f), ("5", 200f), ("-", 280f), ("-", 360f), ("-", 440f)],
        ]));

        var atrasos = report.Atrasos!;
        Assert.Equal(["2026-08", "2026-09"], atrasos.Select(a => a.Periodo));
        Assert.Equal(1_100_000m, atrasos[0].Normal);
        Assert.Equal(5_000m, atrasos[0].D31a90);
        Assert.Equal(2_000m, atrasos[1].Mayor365);
        Assert.Equal(1_200_000_000m, atrasos[1].Normal);   // its unambiguous US shape sets the section's style
    }

    [Fact]
    public void Month_columns_without_a_year_line_take_their_years_from_the_report_date()
    {
        // Report of 03.11.2026: "Nov Dic Ene" ends in January, so it is Nov 2025 → Jan 2026.
        var report = Parsed(new NosisReportPdfBuilder().Replace("atrasos",
        [
            [("Nov", 195f), ("Dic", 255f), ("Ene", 315f)],
            [("Normal", 40f), ("1", 198f), ("2", 258f), ("3", 318f)],
        ]));

        Assert.Equal(["2025-11", "2025-12", "2026-01"], report.Atrasos!.Select(a => a.Periodo));
        Assert.Equal(3_000m, report.Atrasos![2].Normal);
    }

    [Fact]
    public void Month_labels_printed_without_a_gap_are_split_back()
    {
        var words = NosisReportMonthColumns.ExpandGluedMonths([new NosisReportWord("MayJun", 300, 330), new NosisReportWord("Jul", 331, 345)]);

        Assert.Equal(["May", "Jun", "Jul"], words.Select(w => w.Text));
        Assert.Equal(315d, words[0].Right);
        Assert.Equal(315d, words[1].Left);
    }

    [Fact]
    public void An_ambiguous_figure_in_a_section_without_a_style_reads_null_not_a_guess()
    {
        var report = Parsed(new NosisReportPdfBuilder().Replace("atrasos",
        [
            [("Período", 40f), ("Normal", 120f), ("31 a 90", 200f), ("91 a 180", 280f), ("181 a 365", 360f), ("Mayor a 365", 440f)],
            [("09/2026", 40f), ("1,200", 120f), ("-", 200f), ("-", 280f), ("-", 360f), ("2", 440f)],
        ]));

        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.Atrasos]);
        Assert.Null(report.Atrasos![0].Normal);
        Assert.Equal(2_000m, report.Atrasos[0].Mayor365);
    }

    [Fact]
    public void Aportes_patronales_keeps_each_item_as_worded()
    {
        var aportes = Parsed(new NosisReportPdfBuilder()).AportesPatronales!;

        Assert.Equal(2, aportes.Count);
        Assert.Equal(new NosisReportAportesMes { Periodo = "2026-07", SeguridadSocial = "Pagado", ObraSocial = "Pagado", ContribucionObraSocial = "Pagado" }, aportes[0]);
        Assert.Equal(new NosisReportAportesMes { Periodo = "2026-08", SeguridadSocial = "Impago", ObraSocial = "Pagado", ContribucionObraSocial = null }, aportes[1]);
    }

    [Fact]
    public void Cheques_keeps_only_the_month_totals_however_many_pages_of_detail()
    {
        var builder = new NosisReportPdfBuilder { ChequesPerMonth = 150 };
        var result = Parse(builder);
        var cheques = result.Report!.ChequesRechazadosPorMes!;

        Assert.True(result.PageCount >= 5, $"only {result.PageCount} pages");
        Assert.Equal(2, cheques.Count);
        Assert.Equal(new NosisReportChequesMes { Periodo = "2026-09", Cantidad = 150, Monto = 150_075m }, cheques[0]);
        Assert.Equal(new NosisReportChequesMes { Periodo = "2026-10", Cantidad = 150, Monto = 37_500_000m }, cheques[1]);

        // The model has no per-cheque field at all; the JSON carries none of the detail rows.
        var json = Json(result.Report);
        Assert.DoesNotContain("1,000.50", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1000.5,", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0000123", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Cheques_whose_total_line_disagrees_is_unreadable()
    {
        var report = Parsed(new NosisReportPdfBuilder().Replace("cheques",
        [
            [("Octubre 2026", 40f), ("2", 160f), ("1,000.00", 300f)],
            [("Total = 2", 40f), ("9,999.00", 300f)],
        ]));

        Assert.Equal(NosisReportSectionKeys.StatusUnreadable, report.Sections[NosisReportSectionKeys.ChequesRechazadosPorMes]);
        Assert.Null(report.ChequesRechazadosPorMes);
    }

    [Fact]
    public void Criterios_reads_the_policy_the_verdicts_and_the_test_banner()
    {
        var c = Parsed(new NosisReportPdfBuilder()).CriteriosAceptacion!;

        Assert.Equal("Política PyME Ficticia", c.Nombre);
        Assert.Equal("Aprobado", c.Resultado);
        Assert.True(c.EnPrueba);
        Assert.Equal(
            [new NosisReportCriterio { Nombre = "Situación BCRA máxima", Resultado = "Cumple" },
             new NosisReportCriterio { Nombre = "Sin cheques sin fondos", Resultado = "No cumple" }],
            c.Criterios);
    }

    [Fact]
    public void Juicios_keeps_date_type_and_jurisdiction_only()
    {
        var j = Parsed(new NosisReportPdfBuilder()).Juicios!;

        Assert.Equal(2, j.ComoDemandadoCantidad);
        Assert.Equal(
            [new NosisReportJuicio { Fecha = new DateOnly(2024, 3, 12), Tipo = "Ejecutivo", Jurisdiccion = "CABA" },
             new NosisReportJuicio { Fecha = new DateOnly(2025, 8, 5), Tipo = "Laboral", Jurisdiccion = "Córdoba" }],
            j.Items);
    }

    [Fact]
    public void Sisa_srt_and_referencias_read_their_fields()
    {
        var report = Parsed(new NosisReportPdfBuilder());

        Assert.Equal("Activo", report.Sisa!.Estado);
        Assert.Equal(["Productor", "Acopiador"], report.Sisa.Categorias);
        Assert.Equal(new NosisReportSrt { Aseguradora = "ART Ficticia S.A.", Vigente = true, ExtinguidoPorFaltaPago = false }, report.Srt);
        Assert.Equal(2, report.ReferenciasComerciales!.Cantidad);
        Assert.Equal(["Proveedora Uno S.A.", "Distribuidora Dos SRL"], report.ReferenciasComerciales.Fuentes);
    }

    // ── Sections added in nm-1.1 ──────────────────────────────────────────────

    [Fact]
    public void Balances_reducidos_keeps_the_three_newest_closings_and_ignores_the_ratios_block()
    {
        var balances = Parsed(new NosisReportPdfBuilder()).BalancesReducidos!;

        Assert.Equal([new DateOnly(2023, 12, 31), new DateOnly(2024, 12, 31), new DateOnly(2025, 12, 31)], balances.Select(b => b.Cierre));
        var last = balances[^1];
        Assert.Equal(1_200_000m, last.ActivoCorriente);
        Assert.Equal(700_000m, last.ActivoNoCorriente);
        Assert.Equal(1_900_000m, last.TotalActivo);
        Assert.Equal(600_000m, last.PasivoCorriente);
        Assert.Equal(100_000m, last.PasivoNoCorriente);
        Assert.Equal(700_000m, last.TotalPasivo);
        Assert.Equal(1_200_000m, last.PatrimonioNeto);
        Assert.Equal(3_500_000m, last.VentasNetas);           // not the size table's 999,999,999
        Assert.Equal(-50_000m, last.ResultadoEjercicio);      // label printed cut short: "Resultado del ejer…"
        Assert.Null(balances[1].ResultadoEjercicio);          // "-"
    }

    [Fact]
    public void Mav_reads_instruments_and_currencies_each_number_by_its_own_shape()
    {
        var mav = Parsed(new NosisReportPdfBuilder()).Mav!;

        Assert.Equal(
            [new NosisReportMavInstrumento { Instrumento = "Cheques de pago diferido", Monto = 1_527_384_960m },
             new NosisReportMavInstrumento { Instrumento = "Pagarés", Monto = 412_000_000m },
             new NosisReportMavInstrumento { Instrumento = "Facturas de crédito", Monto = null }],   // "1.234" in a mixed block
            mav.PorInstrumento);
        Assert.Equal(
            [new NosisReportMavMoneda { Moneda = "Pesos", Monto = 1_939_384_960m },
             new NosisReportMavMoneda { Moneda = "Dólares", Monto = 0m }],
            mav.PorMoneda);
    }

    [Fact]
    public void Antecedentes_fiscales_keep_organism_type_rate_and_dates_never_the_cuit()
    {
        var fiscales = Parsed(new NosisReportPdfBuilder()).AntecedentesFiscales!;

        Assert.Equal(
            [new NosisReportAntecedenteFiscal { Organismo = "AGIP", Tipo = "Percepción IIBB", Alicuota = 5.00m,
                VigenciaDesde = new DateOnly(2026, 11, 1), VigenciaHasta = new DateOnly(2026, 11, 30) },
             new NosisReportAntecedenteFiscal { Organismo = "ARBA", Tipo = "Retención IIBB", Alicuota = 1.75m,
                VigenciaDesde = new DateOnly(2026, 10, 1), VigenciaHasta = new DateOnly(2026, 12, 31) }],
            fiscales);
    }

    [Fact]
    public void Comunicados_keep_only_their_count_and_dates()
    {
        var c = Parsed(new NosisReportPdfBuilder()).ComunicadosBcra!;

        Assert.Equal(2, c.Cantidad);
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 15)], c.Fechas);
    }

    [Fact]
    public void Comercio_exterior_and_marcas_keep_flags_years_and_a_count()
    {
        var report = Parsed(new NosisReportPdfBuilder());

        Assert.True(report.ComercioExterior!.Importador);
        Assert.False(report.ComercioExterior.Exportador);
        Assert.Equal([2025, 2026], report.ComercioExterior.Anios);
        Assert.Equal(3, report.MarcasCantidad);
    }

    [Theory]
    [InlineData("balances", NosisReportSectionKeys.BalancesReducidos)]
    [InlineData("mav", NosisReportSectionKeys.Mav)]
    [InlineData("fiscales", NosisReportSectionKeys.AntecedentesFiscales)]
    [InlineData("comunicados", NosisReportSectionKeys.ComunicadosBcra)]
    [InlineData("marcas", NosisReportSectionKeys.MarcasCantidad)]
    public void A_new_section_the_report_does_not_carry_is_absent(string builderKey, string sectionKey)
    {
        var report = Parsed(new NosisReportPdfBuilder().Without(builderKey));

        Assert.Equal(NosisReportSectionKeys.StatusAbsent, report.Sections[sectionKey]);
    }

    [Fact]
    public void Without_import_or_export_headings_comercio_exterior_is_absent()
    {
        var report = Parsed(new NosisReportPdfBuilder().Without("importaciones").Without("exportaciones"));

        Assert.Equal(NosisReportSectionKeys.StatusAbsent, report.Sections[NosisReportSectionKeys.ComercioExterior]);
        Assert.Null(report.ComercioExterior);
    }

    // ── Privacy ───────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_personal_reaches_the_json()
    {
        var json = Json(Parsed(new NosisReportPdfBuilder()));

        foreach (var decoy in NosisReportPdfBuilder.DecoyValues)
            Assert.DoesNotContain(decoy, json, StringComparison.OrdinalIgnoreCase);

        // No CUIT/CUIL-shaped string (the subject's included), no DNI-, phone- or CBU-shaped run of
        // digits inside a string, no e-mail.
        foreach (var value in StringValues(json))
        {
            Assert.False(Regex.IsMatch(value, @"\d{2}-?\d{8}-?\d"), $"CUIT-shaped: {value}");
            Assert.False(Regex.IsMatch(value, @"\d(?:[\s.\-()]*\d){6,}"), $"digit run: {value}");
            Assert.DoesNotContain("@", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sections_followed_by_skipped_or_unknown_headings_keep_only_their_own_rows()
    {
        // In the real reports CHEQUES POSITIVOS leaked into Referencias, COMUNICADOS into SRT and
        // SOCIEDADES into Juicios; the builder puts each right after them, and an UNKNOWN heading after SISA.
        var report = Parsed(new NosisReportPdfBuilder());

        Assert.Equal(["Proveedora Uno S.A.", "Distribuidora Dos SRL"], report.ReferenciasComerciales!.Fuentes);
        Assert.Equal(new NosisReportSrt { Aseguradora = "ART Ficticia S.A.", Vigente = true, ExtinguidoPorFaltaPago = false }, report.Srt);
        Assert.Equal(["Productor", "Acopiador"], report.Sisa!.Categorias);
        Assert.Equal(2, report.Juicios!.Items.Count);
        Assert.Equal(2, report.Juicios.ComoDemandadoCantidad);
    }

    [Fact]
    public void A_bold_caps_column_header_does_not_cut_its_table()
    {
        var mark = NosisReportPdfBuilder.HeadingMark;
        var report = Parsed(new NosisReportPdfBuilder().Replace("referencias",
        [
            [(mark + "FUENTE", 40f), (mark + "FECHA", 200f), (mark + "CONTACTO", 280f)],
            [("Proveedora Uno S.A.", 40f), ("01/05/2026", 200f), ("Contacto Señuelo", 280f)],
        ]));

        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.ReferenciasComerciales]);
        Assert.Equal(["Proveedora Uno S.A."], report.ReferenciasComerciales!.Fuentes);
    }

    [Fact]
    public void The_privacy_guard_drops_identifier_shaped_strings_and_counts_them()
    {
        var report = Parsed(new NosisReportPdfBuilder().Replace("referencias",
        [
            [("Fuente", 40f), ("Fecha", 200f)],
            [("Proveedora Uno S.A.", 40f), ("01/05/2026", 200f)],
            [("Llamar al 011 4444 5555", 40f), ("02/05/2026", 200f)],
            [("ventas@proveedora.example", 40f), ("03/05/2026", 200f)],
        ]));

        Assert.Equal(["Proveedora Uno S.A."], report.ReferenciasComerciales!.Fuentes);

        var result = Parse(new NosisReportPdfBuilder().Replace("referencias",
        [
            [("Fuente", 40f), ("Fecha", 200f)],
            [("ventas@proveedora.example", 40f), ("03/05/2026", 200f)],
        ]));
        Assert.Equal(1, result.DroppedStrings);
    }

    [Theory]
    [InlineData("20-30111222-3", true)]
    [InlineData("DNI 30.111.222", true)]
    [InlineData("011 4444-5555", true)]
    [InlineData("0000000000000000000001", true)]
    [InlineData("alguien@example.com", true)]
    [InlineData("CUIT del firmante", true)]
    [InlineData("Banco de la Nación Argentina", false)]
    [InlineData("Ejecutivo 12/03/2024", false)]
    [InlineData("10 a 49", false)]
    public void LooksPersonal(string value, bool expected)
        => Assert.Equal(expected, NosisReportPrivacyGuard.LooksPersonal(value));

    // ── Numbers and periods ───────────────────────────────────────────────────

    [Theory]
    // Unambiguous by their own shape, whatever the section says.
    [InlineData("7,258,316,409", "Unknown", "7258316409")]
    [InlineData("1,939,384,960", "Ar", "1939384960")]
    [InlineData("1.527.384.960", "Us", "1527384960")]
    [InlineData("31,472.6", "Unknown", "31472.6")]
    [InlineData("1.234,56", "Unknown", "1234.56")]
    [InlineData("950.5", "Unknown", "950.5")]
    [InlineData("12,34", "Unknown", "12.34")]
    [InlineData("5,00", "Unknown", "5.00")]
    [InlineData("5,00%", "Unknown", "5.00")]
    [InlineData("0", "Unknown", "0")]
    [InlineData("$ 1,000.50", "Unknown", "1000.50")]
    // Ambiguous: the section's style decides; without one (or mixed) it reads null.
    [InlineData("1,234", "Us", "1234")]
    [InlineData("1,234", "Ar", "1.234")]
    [InlineData("1.234", "Ar", "1234")]
    [InlineData("1.234", "Us", "1.234")]
    [InlineData("1,234", "Unknown", null)]
    [InlineData("1.234", "Mixed", null)]
    [InlineData("-", "Us", null)]
    public void Numbers_are_read_by_their_own_shape(string token, string style, string? expected)
    {
        Assert.True(NosisReportNumbers.TryAmount(token, Enum.Parse<NosisNumberStyle>(style), out var value));
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,23,4")]
    [InlineData("1.2.3")]
    [InlineData("1,234.567,8")]
    public void Malformed_numbers_are_refused(string token)
        => Assert.False(NosisReportNumbers.TryAmount(token, NosisNumberStyle.Us, out _));

    [Fact]
    public void A_section_style_comes_from_its_unambiguous_tokens()
    {
        static NosisReportLine L(params string[] words)
            => new(1, 0d, words.Select((w, i) => new NosisReportWord(w, i * 40d, (i * 40d) + 30d)).ToList());

        Assert.Equal(NosisNumberStyle.Us, NosisReportNumbers.InferStyle([L("Total", "1,939,384,960"), L("x", "1,234")]));
        Assert.Equal(NosisNumberStyle.Ar, NosisReportNumbers.InferStyle([L("AGIP", "5,00", "%"), L("y", "1.234")]));
        Assert.Equal(NosisNumberStyle.Mixed, NosisReportNumbers.InferStyle([L("Pagarés", "1.527.384.960"), L("Total", "1,939,384,960")]));
        Assert.Equal(NosisNumberStyle.Unknown, NosisReportNumbers.InferStyle([L("x", "1,234"), L("y", "12")]));
    }

    [Fact]
    public void A_label_cut_short_with_an_ellipsis_matches_by_prefix()
    {
        var map = new Dictionary<string, int> { ["CUENTAS PERSONAS JURIDICAS"] = 1, ["CUENTAS PERSONAS FISICAS"] = 2 };

        Assert.True(NosisReportText.TryMatchLabel("Cuentas Personas Juríd…", map, out var value));
        Assert.Equal(1, value);
        Assert.True(NosisReportText.TryMatchLabel("Cuentas Personas Fís...", map, out value));
        Assert.Equal(2, value);
        Assert.False(NosisReportText.TryMatchLabel("Cuentas Pers…", map, out _));   // both start so: ambiguous
        Assert.False(NosisReportText.TryMatchLabel("Cuentas Personas Juríd", map, out _));   // no ellipsis: not a cut label

        var line = new NosisReportLine(1, 0d, [new("Cantidad", 0, 30), new("de", 32, 40), new("ban…", 42, 60), new("3", 200, 205)]);
        Assert.Equal("3", NosisReportText.ValueAfter(line, "CANTIDAD DE BANCOS"));
    }

    [Fact]
    public void A_truncated_row_label_still_lands_in_its_row()
    {
        var rows = NosisReportPdfBuilder.NumericEvolucionRows();
        rows[4][0] = ("Cantidad de ban…", 40f);

        var months = Parsed(new NosisReportPdfBuilder().Replace("evolucion", rows)).Evolucion24m!;

        Assert.Equal(4, months[^1].CantidadBancos);
    }

    [Theory]
    [InlineData("09/2026", "2026-09")]
    [InlineData("9/26", "2026-09")]
    [InlineData("2026-09", "2026-09")]
    [InlineData("Sep-26", "2026-09")]
    [InlineData("Set/2026", "2026-09")]
    [InlineData("Septiembre 2026", "2026-09")]
    [InlineData("Octubre 2026", "2026-10")]
    [InlineData("Banco", null)]
    public void Periods(string text, string? expected)
        => Assert.Equal(expected, NosisReportPeriods.Parse(text));

    [Fact]
    public void Cancellation_is_honoured()
    {
        var bytes = new NosisReportPdfBuilder().Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Parser.Parse(bytes, cts.Token));
    }

    private static IEnumerable<string> StringValues(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var stack = new Stack<JsonElement>([doc.RootElement]);
        var values = new List<string>();
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        // fileSha256 is hex and reportDate/importedAt are dates: not free text.
                        if (p.Name is "fileSha256" or "reportDate" or "importedAt")
                            continue;
                        stack.Push(p.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        stack.Push(item);
                    break;
                case JsonValueKind.String:
                    // ISO dates ("2025-08-05") and periods ("2026-09") are structured values, not text.
                    var text = e.GetString()!;
                    if (!Regex.IsMatch(text, @"^\d{4}-\d{2}(-\d{2})?$"))
                        values.Add(text);
                    break;
            }
        }

        return values;
    }
}
