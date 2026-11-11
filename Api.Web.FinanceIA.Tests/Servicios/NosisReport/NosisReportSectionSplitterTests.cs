// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using Api.Web.FinanceIA.Servicios.NosisReport;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>
/// <see cref="NosisReportSectionSplitter"/>: headings told by their printed style once the known ones
/// calibrate it, so a heading the parser does not know closes the section before it instead of leaking
/// its lines into it (in the real reports: CHEQUES POSITIVOS into Referencias, COMUNICADOS into SRT,
/// SOCIEDADES into Juicios).
/// </summary>
public sealed class NosisReportSectionSplitterTests
{
    private static readonly NosisReportLineStyle HeadingStyle = new(9d, true, "#000000");
    private static readonly NosisReportLineStyle BodyStyle    = new(7.5d, false, "#000000");

    private static NosisReportLine Line(string text, NosisReportLineStyle style, double x = 40d)
    {
        var words = new List<NosisReportWord>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new NosisReportWord(word, x, x + (word.Length * 4d)));
            x += (word.Length * 4d) + 3d;
        }

        return new NosisReportLine(1, 0d, words, style);
    }

    private static NosisReportLine H(string text) => Line(text, HeadingStyle);
    private static NosisReportLine B(string text) => Line(text, BodyStyle);

    [Fact]
    public void An_unknown_heading_in_the_heading_style_closes_the_section_and_drops_its_lines()
    {
        var split = NosisReportSectionSplitter.Split(
        [
            H("SISA"), B("Estado: Activo"),
            H("ARCA"), B("IVA: Responsable Inscripto"),
            H("SOCIEDADES SIN LISTAR"), B("Edicto decoy"), B("More decoy"),
            H("SRT"), B("Aseguradora: ART"),
        ]);

        Assert.True(split.StyleCalibrated);
        Assert.Equal(["Estado: Activo"], split.Sections[NosisReportHeadingKind.Sisa].Lines.Select(l => l.Text));
        Assert.Equal(["IVA: Responsable Inscripto"], split.Sections[NosisReportHeadingKind.Arca].Lines.Select(l => l.Text));
        Assert.Equal(["Aseguradora: ART"], split.Sections[NosisReportHeadingKind.Srt].Lines.Select(l => l.Text));
        Assert.Contains(split.Headings, h => !h.Known && h.Kind == NosisReportHeadingKind.Skip);
        Assert.DoesNotContain(split.Sections.Values.SelectMany(s => s.Lines), l => l.Text.Contains("decoy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("CHEQUES POSITIVOS")]
    [InlineData("COMUNICADOS DEL BCRA Y DE FUENTES DIRECTAS")]
    [InlineData("SOCIEDADES")]
    [InlineData("RATIOS ECONOMICOFINANCIEROS")]
    [InlineData("HOMÓNIMOS")]
    [InlineData("TRANSFERENCIAS FONDOS DE COMERCIO")]
    [InlineData("MIS SEGUIMIENTOS")]
    [InlineData("CONSULTAS")]
    [InlineData("FICHA PROFESIONAL")]
    [InlineData("SCORES")]
    [InlineData("RIESGO SECTORIAL")]
    [InlineData("VALIDACIÓN DE IDENTIDAD")]
    [InlineData("IMPORTACIONES")]
    [InlineData("EXPORTACIONES")]
    [InlineData("MARCAS REGISTRADAS")]
    public void The_listed_headings_close_the_section_before_them_even_uncalibrated(string heading)
    {
        // Unknown styles: the text alone decides.
        var plain = NosisReportLineStyle.Unknown;
        var split = NosisReportSectionSplitter.Split(
        [
            Line("REFERENCIAS COMERCIALES", plain), Line("Cantidad: 2", plain),
            Line(heading, plain), Line("Line that must not leak", plain),
        ]);

        Assert.False(split.StyleCalibrated);
        Assert.Equal(["Cantidad: 2"], split.Sections[NosisReportHeadingKind.ReferenciasComerciales].Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_known_text_printed_like_the_body_is_a_label_not_a_heading()
    {
        var split = NosisReportSectionSplitter.Split(
        [
            H("ARCA"), B("x"), H("SISA"), B("y"),
            H("RESUMEN CONFIGURABLE"), B("Score: 750"), B("CONSULTAS"), B("Consultas últimos 3 meses: 4"),
        ]);

        Assert.Equal(
            ["Score: 750", "CONSULTAS", "Consultas últimos 3 meses: 4"],
            split.Sections[NosisReportHeadingKind.Resumen].Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_bold_caps_column_header_row_is_not_a_heading()
    {
        var header = new NosisReportLine(1, 0d,
        [
            new NosisReportWord("FUENTE", 40, 70), new NosisReportWord("FECHA", 200, 225), new NosisReportWord("CONTACTO", 280, 320),
        ], HeadingStyle);

        var split = NosisReportSectionSplitter.Split(
        [
            H("ARCA"), B("x"), H("SISA"), B("y"),
            H("REFERENCIAS COMERCIALES"), header, B("Proveedora Uno"),
        ]);

        Assert.Equal(2, split.Sections[NosisReportHeadingKind.ReferenciasComerciales].Lines.Count);
    }

    [Fact]
    public void Too_few_known_headings_leave_the_text_to_decide()
    {
        var split = NosisReportSectionSplitter.Split([H("SISA"), B("Estado: Activo"), H("TEXTO EN MAYUSCULAS"), B("z")]);

        Assert.False(split.StyleCalibrated);
        Assert.Equal(3, split.Sections[NosisReportHeadingKind.Sisa].Lines.Count);
    }

    [Fact]
    public void A_doubled_or_indented_heading_counts_once()
    {
        var split = NosisReportSectionSplitter.Split(
        [
            H("ARCA"), B("x"), H("SISA"), B("y"),
            H("ANTECEDENTES FISCALES ANTECEDENTES FISCALES"), Line("ANTECEDENTES FISCALES", HeadingStyle, 90d), B("AGIP"),
        ]);

        Assert.Equal(["AGIP"], split.Sections[NosisReportHeadingKind.AntecedentesFiscales].Lines.Select(l => l.Text));
        Assert.Equal(2, split.Headings.Count(h => h.Kind == NosisReportHeadingKind.AntecedentesFiscales));
    }

    [Fact]
    public void Lines_before_the_first_heading_are_the_preamble_never_a_section()
    {
        var split = NosisReportSectionSplitter.Split([B("Razón social: decoy"), H("SISA"), B("Estado: Activo")]);

        Assert.Equal(["Razón social: decoy"], split.Preamble.Select(l => l.Text));
        Assert.DoesNotContain(split.Sections.Values.SelectMany(s => s.Lines), l => l.Text.Contains("decoy", StringComparison.Ordinal));
    }
}
