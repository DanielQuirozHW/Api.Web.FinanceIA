// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios.NosisReport;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>
/// The parser over <see cref="NosisReportPdfBuilder.RealLayout"/>: each section laid out as the real
/// template prints it (from masked shapes of real reports), every value invented.
/// </summary>
public sealed class NosisManagerReportRealLayoutTests
{
    private static NosisManagerReport Parsed(NosisReportPdfBuilder builder)
    {
        var result = new NosisManagerReportParser().Parse(builder.Build(), CancellationToken.None);
        Assert.Equal(NosisReportParseOutcome.Parsed, result.Outcome);
        return result.Report!;
    }

    [Fact]
    public void Summary_reads_the_headline_figures_from_informacion_general()
    {
        var s = Parsed(NosisReportPdfBuilder.RealLayout()).Summary!;

        Assert.Equal(1_357_924_680m, s.EndeudamientoTotal);
        Assert.Equal(98_765_432m, s.CompromisosMensuales);
        Assert.True(s.TieneChequesRechazados);
        Assert.Equal(7, s.Consultas3m);
        Assert.Equal(25, s.Consultas12m);
        Assert.Equal(13_579_246_801m, s.FacturacionEstimadaMin);   // "Mayor a $ …"
        Assert.Null(s.FacturacionEstimadaMax);
        Assert.Equal(17, s.AntiguedadAnios);                      // under its label in the label row
        Assert.Equal("Entre 100 a 199", s.EmpleadosRango);
        Assert.Equal(742, s.Score);
    }

    [Fact]
    public void No_existen_cheques_rechazados_reads_false()
    {
        var builder = NosisReportPdfBuilder.RealLayout();
        builder.Replace("perfil", [[("Cheques rechazados: No existen cheques rechazados", 46f)]]);

        Assert.False(Parsed(builder).Summary!.TieneChequesRechazados);
    }

    [Fact]
    public void Criterios_result_is_the_resultado_segun_configuracion_line()
    {
        var c = Parsed(NosisReportPdfBuilder.RealLayout()).CriteriosAceptacion!;

        Assert.Equal("Observado", c.Resultado);
        Assert.True(c.EnPrueba);
        Assert.Equal(new NosisReportCriterio { Nombre = "Bureau de Crédito del BCRA", Resultado = "Rechazado" }, c.Criterios[0]);
        Assert.Equal(new NosisReportCriterio { Nombre = "Concursos y Quiebras", Resultado = "No Corresponde" }, c.Criterios[^1]);
        Assert.DoesNotContain(c.Criterios, x => x.Nombre.StartsWith("Resultado", StringComparison.Ordinal));
    }

    [Fact]
    public void Deuda_reads_entities_skips_aggregates_and_stray_lines_and_merges_both_tables()
    {
        var deuda = Parsed(NosisReportPdfBuilder.RealLayout()).DeudaPorEntidad!;

        Assert.Equal(["Bco Ficticio Uno", "Bco Ficticio Do…", "Cía Financiera Tres", "Bco Cuatro"], deuda.Select(d => d.Entidad));

        var uno = deuda[0].Periodos;
        Assert.Equal(24, uno.Count);
        Assert.Equal("2024-11", uno[0].Periodo);
        Assert.Equal("2026-10", uno[^1].Periodo);
        Assert.Equal(2_000_000_000m, uno[0].Monto);       // the earlier table, in miles
        Assert.Equal(1_234_567_000m, uno[^1].Monto);

        Assert.Equal(123_456_000m, deuda[1].Periodos[0].Monto);   // "123,456": US by the section's other figures
        Assert.Equal(45_678_000m, deuda[2].Periodos[0].Monto);    // the name sat alone on the line above
        Assert.Equal(9_500m, deuda[3].Periodos.Single(p => p.Periodo == "2026-09").Monto);
    }

    [Fact]
    public void Atrasos_reads_a_three_line_header_and_mm_dot_yyyy_rows()
    {
        var atrasos = Parsed(NosisReportPdfBuilder.RealLayout()).Atrasos!;

        Assert.Equal(["2026-08", "2026-09", "2026-10"], atrasos.Select(a => a.Periodo));
        Assert.Equal(new NosisReportAtrasosMes
        {
            Periodo = "2026-09", Normal = 1_234_567_800m, D31a90 = 12_345_600m, D91a180 = null, D181a365 = 0m, Mayor365 = null,
        }, atrasos[1]);
        Assert.Equal(2_000_000m, atrasos[0].D31a90);
        Assert.Null(atrasos[0].Mayor365);                    // the Total column is not a bucket
        Assert.Equal(new NosisReportAtrasosMes { Periodo = "2026-10" }, atrasos[2]);
    }

    [Fact]
    public void Aportes_reads_the_real_column_names_and_two_word_values()
    {
        var aportes = Parsed(NosisReportPdfBuilder.RealLayout()).AportesPatronales!;

        Assert.Equal(["2026-07", "2026-08", "2026-09"], aportes.Select(a => a.Periodo));
        Assert.Equal(new NosisReportAportesMes
        {
            Periodo = "2026-08", SeguridadSocial = "Pago Parcial", ObraSocial = "Pago", ContribucionObraSocial = "Pago Parcial",
        }, aportes[1]);
        Assert.Equal("Impago", aportes[2].ContribucionObraSocial);
    }

    [Fact]
    public void Cheques_checks_per_month_totals_and_sums_a_month_split_by_a_page_break()
    {
        var cheques = Parsed(NosisReportPdfBuilder.RealLayout()).ChequesRechazadosPorMes!;

        Assert.Equal(
            [new NosisReportChequesMes { Periodo = "2026-08", Cantidad = 3, Monto = 1_500_000m },
             new NosisReportChequesMes { Periodo = "2026-09", Cantidad = 2, Monto = 300_000.50m },
             new NosisReportChequesMes { Periodo = "2026-10", Cantidad = 3, Monto = 4_500_000m }],
            cheques);
    }

    [Fact]
    public void A_cheque_total_matching_neither_its_month_nor_the_section_is_unreadable()
    {
        var builder = NosisReportPdfBuilder.RealLayout();
        builder.Replace("cheques",
        [
            [("Octubre 2026", 47f), ("3", 235f), ("4,500,000.00", 271f)],
            [("Total = 3", 47f), ("4,000,000.00", 271f)],
        ]);

        var report = Parsed(builder);

        Assert.Equal(NosisReportSectionKeys.StatusUnreadable, report.Sections[NosisReportSectionKeys.ChequesRechazadosPorMes]);
    }

    [Fact]
    public void A_cheque_grand_total_is_accepted()
    {
        var builder = NosisReportPdfBuilder.RealLayout();
        builder.Replace("cheques",
        [
            [("Octubre 2026", 47f), ("3", 235f), ("4,500,000.00", 271f)],
            [("Septiembre 2026", 47f), ("2", 235f), ("300,000.50", 271f)],
            [("Total = 5", 47f), ("4,800,000.50", 271f)],
        ]);

        Assert.Equal(2, Parsed(builder).ChequesRechazadosPorMes!.Count);
    }

    [Fact]
    public void Balances_read_day_month_year_closings_and_the_value_of_each_value_pct_cell()
    {
        var balances = Parsed(NosisReportPdfBuilder.RealLayout()).BalancesReducidos!;

        Assert.Equal([new DateOnly(2023, 12, 31), new DateOnly(2024, 12, 31), new DateOnly(2025, 12, 31)], balances.Select(b => b.Cierre));
        Assert.Equal(new NosisReportBalanceReducido
        {
            Cierre = new DateOnly(2025, 12, 31),
            ActivoCorriente = 12_000_000_000m, ActivoNoCorriente = 7_500_000_000m, TotalActivo = 19_500_000_000m,
            PasivoCorriente = 6_000_000_000m, PasivoNoCorriente = 1_300_000_000m, TotalPasivo = 7_300_000_000m,
            PatrimonioNeto = 12_200_000_000m, VentasNetas = 36_000_000_000m, ResultadoEjercicio = 2_500_000_000m,
        }, balances[^1]);
        Assert.Equal(-500_000_000m, balances[1].ResultadoEjercicio);   // "Result. del Ejercicio #3"
    }

    [Fact]
    public void A_balances_heading_with_no_body_is_ok_and_empty()
    {
        var builder = NosisReportPdfBuilder.RealLayout();
        builder.Replace("balances", []);

        var report = Parsed(builder);

        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.BalancesReducidos]);
        Assert.Empty(report.BalancesReducidos!);
    }

    [Fact]
    public void Antecedentes_fiscales_give_one_entry_per_rate_of_a_block()
    {
        var fiscales = Parsed(NosisReportPdfBuilder.RealLayout()).AntecedentesFiscales!;

        Assert.Equal(
            [new NosisReportAntecedenteFiscal { Organismo = "AGIP", Tipo = "Percepción", Alicuota = 3.00m,
                VigenciaDesde = new DateOnly(2026, 11, 1), VigenciaHasta = new DateOnly(2026, 11, 30) },
             new NosisReportAntecedenteFiscal { Organismo = "AGIP", Tipo = "Retención", Alicuota = 1.50m,
                VigenciaDesde = new DateOnly(2026, 11, 1), VigenciaHasta = new DateOnly(2026, 11, 30) }],
            fiscales);
    }

    [Fact]
    public void Comunicados_count_the_fecha_lines_and_keep_only_their_dates()
    {
        var c = Parsed(NosisReportPdfBuilder.RealLayout()).ComunicadosBcra!;

        Assert.Equal(2, c.Cantidad);
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 15)], c.Fechas);
    }

    [Fact]
    public void Comercio_exterior_takes_the_years_of_the_header_and_marks_the_importer()
    {
        var report = Parsed(NosisReportPdfBuilder.RealLayout());

        Assert.Equal(NosisReportSectionKeys.StatusOk, report.Sections[NosisReportSectionKeys.ComercioExterior]);
        Assert.True(report.ComercioExterior!.Importador);
        Assert.Null(report.ComercioExterior.Exportador);
        Assert.Equal([2023, 2024, 2025], report.ComercioExterior.Anios);
    }

    [Fact]
    public void Marcas_reads_registra_n_marcas()
        => Assert.Equal(4, Parsed(NosisReportPdfBuilder.RealLayout()).MarcasCantidad);

    [Fact]
    public void Juicios_read_a_block_without_the_actor_or_the_case_number()
    {
        var j = Parsed(NosisReportPdfBuilder.RealLayout()).Juicios!;

        Assert.Equal(1, j.ComoDemandadoCantidad);
        var item = Assert.Single(j.Items);
        Assert.Equal(new DateOnly(2024, 3, 12), item.Fecha);
        Assert.Equal("Cobro Ejecutivo de Pesos", item.Tipo);
        Assert.Equal("Juzgado, Fuero Civil, Juzgado Civil y Comercial 4 Ciudad Ficticia", item.Jurisdiccion);
    }

    [Fact]
    public void Mav_joins_a_wrapped_label_to_its_amount_and_stops_at_the_cross_table()
    {
        var mav = Parsed(NosisReportPdfBuilder.RealLayout()).Mav!;

        Assert.Equal(["Cheque", "Pagaré"], mav.PorInstrumento.Select(i => i.Instrumento));
        Assert.Equal(1_527_384_960m, mav.PorInstrumento[0].Monto);
        Assert.Equal(
            [new NosisReportMavMoneda { Moneda = "Cheques en Pesos", Monto = 1_939_384_960m },
             new NosisReportMavMoneda { Moneda = "Cheques en USD CABLE (Expresado", Monto = 0m }],
            mav.PorMoneda);
    }

    [Fact]
    public void Every_section_of_the_real_layout_reads_and_nothing_personal_reaches_the_json()
    {
        var result = new NosisManagerReportParser().Parse(NosisReportPdfBuilder.RealLayout().Build(), CancellationToken.None);
        var report = result.Report!;

        foreach (var (key, status) in report.Sections.Where(s => s.Key is not NosisReportSectionKeys.Sisa))
            Assert.True(status == NosisReportSectionKeys.StatusOk, $"{key} was {status}");

        // Every string the JSON holds (invented amounts may contain a decoy's digits; strings may not).
        var strings = Strings(System.Text.Json.JsonSerializer.SerializeToNode(report, NosisManagerReport.JsonOptions)).ToList();
        foreach (var decoy in NosisReportPdfBuilder.DecoyValues.Concat(["30111222333", "P123456", "2024/55", "Expte", "SEÑUELO"]))
            Assert.DoesNotContain(strings, text => text.Contains(decoy, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, result.DroppedStrings);
    }

    private static IEnumerable<string> Strings(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        System.Text.Json.Nodes.JsonObject o => o.SelectMany(p => Strings(p.Value).Prepend(p.Key)),
        System.Text.Json.Nodes.JsonArray a  => a.SelectMany(Strings),
        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var text) => [text],
        _ => [],
    };
}
