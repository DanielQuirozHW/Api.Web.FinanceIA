// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using System.Globalization;
using System.Text;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>
/// Builds a SYNTHETIC NOSIS Manager "Informe Individual": the fixed template's title, footer and section
/// headings, with each section's text laid out the way the parser reads it (label/value lines, month
/// columns under a year line, named columns, month lines among per-cheque rows). Every company, figure,
/// CUIT and name is invented.
///
/// <para>
/// The sections the import must NEVER keep are filled with decoys — "Señuelo" names, CUIT-, DNI-,
/// phone-, e-mail- and CBU-shaped values — so a test can assert none of them reaches the JSON. The
/// title line (on every page) carries a decoy business name for the same reason.
/// </para>
/// </summary>
internal sealed class NosisReportPdfBuilder
{
    public const string SubjectCuit    = "30-00000000-1";
    public const string OtherCuit      = "30-00000000-2";
    public const string BusinessName   = "EMPRESA FICTICIA SEÑUELO S.A.";
    public const string OwnerPassword  = "fake-owner";
    public const string Decoy          = "Señuelo";

    /// <summary>Decoy values planted in excluded sections; none may appear in the stored JSON.</summary>
    public static readonly string[] DecoyValues =
    [
        "Señuelo", "Senuelo", "20-30111222-3", "20301112223", "30.111.222", "30111222", "27-30555666-1",
        "contacto@senuelo.example", "4444-5555", "0000000000000000000001", "0000123", "Calle", "12345/2024",
        "EMPRESA FICTICIA", "DIRECTORIO CONVOCA", "Boletín", "12345678", "4455667", "Dato Señuelo",
        "Columna Señuelo", "Capítulo", "máquinas", "Marca Señuelo", "999999999",
    ];

    private const float FontSize = 7.5f;
    private const float HeadingSize = 9f;

    /// <summary>A run whose text starts with this is drawn in the heading style (bold 9 pt).</summary>
    public const string HeadingMark = "\u0001";
    private const float LineHeight = 11f;
    private const float Top = 790f;
    private const float Bottom = 60f;

    private readonly List<(string Key, string Heading, List<List<(string Text, float X)>> Rows)> _sections = [];
    private readonly HashSet<string> _replaced = [];

    public NosisReportPdfBuilder()
    {
        BuildDefault();
    }

    /// <summary>The CUIT the title prints.</summary>
    public string TitleCuit { get; set; } = SubjectCuit;

    /// <summary>The CUIT the footers print (digits or formatted).</summary>
    public string FooterCuit { get; set; } = "30000000001";

    /// <summary>When set, the LAST page's footer prints this CUIT instead (two reports merged).</summary>
    public string? LastPageFooterCuit { get; set; }

    /// <summary>"dd.MM.yyyy HH:mm" in the footer.</summary>
    public string FooterDate { get; set; } = "03.11.2026 14:36";

    /// <summary>RC4-128 with the empty user password and an owner password, print allowed — as NOSIS ships it.</summary>
    public bool Encrypt { get; set; }

    /// <summary>Writes the "Importes expresados en miles de $" note under INFORMACIÓN BANCARIA.</summary>
    public bool Thousands { get; set; } = true;

    /// <summary>Rejected-cheque detail rows per month (enough of them span pages).</summary>
    public int ChequesPerMonth { get; set; } = 3;

    /// <summary>Removes a section (heading and body) by key.</summary>
    public NosisReportPdfBuilder Without(string key)
    {
        _sections.RemoveAll(s => s.Key == key);
        return this;
    }

    /// <summary>Keeps a section's heading but replaces its body with text no parser understands.</summary>
    public NosisReportPdfBuilder Garble(string key)
    {
        _replaced.Add(key);
        var index = _sections.FindIndex(s => s.Key == key);
        var section = _sections[index];
        _sections[index] = (section.Key, section.Heading,
        [
            [("lorem ipsum dolor", 40f)],
            [("sit amet consectetur", 40f), ("adipiscing", 300f)],
        ]);
        return this;
    }

    /// <summary>Replaces a section's body.</summary>
    public NosisReportPdfBuilder Replace(string key, List<List<(string Text, float X)>> rows)
    {
        _replaced.Add(key);
        var index = _sections.FindIndex(s => s.Key == key);
        _sections[index] = (_sections[index].Key, _sections[index].Heading, rows);
        return this;
    }

    public byte[] Build()
    {
        // Rebuild the cheque section with the current ChequesPerMonth.
        var cheques = _sections.FindIndex(s => s.Key == "cheques");
        if (cheques >= 0 && !_replaced.Contains("cheques"))
            _sections[cheques] = (_sections[cheques].Key, _sections[cheques].Heading, ChequeRows());

        var bancaria = _sections.FindIndex(s => s.Key == "bancaria");
        if (bancaria >= 0 && !_replaced.Contains("bancaria"))
            _sections[bancaria] = (_sections[bancaria].Key, _sections[bancaria].Heading,
                Thousands ? [[("Importes expresados en miles de $", 40f)]] : [[("Información del sistema financiero", 40f)]]);

        var pages = Paginate();

        using var buffer = new MemoryStream();
        var properties = new WriterProperties();
        if (Encrypt)
        {
            properties.SetStandardEncryption(
                null, Encoding.ASCII.GetBytes(OwnerPassword), EncryptionConstants.ALLOW_PRINTING,
                EncryptionConstants.STANDARD_ENCRYPTION_128);
        }

        using (var pdf = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var bold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
            for (var p = 0; p < pages.Count; p++)
            {
                var page = pdf.AddNewPage(PageSize.A4);
                var canvas = new PdfCanvas(page);

                Write(canvas, font, $"Informe Individual > {TitleCuit} | {BusinessName}", 40f, 815f, 9f);

                var y = Top;
                foreach (var row in pages[p])
                {
                    foreach (var (text, x) in row)
                    {
                        // A heading-styled run: Helvetica-Bold 9 pt, the look the real template gives
                        // its section titles. Body text stays Helvetica 7.5 pt.
                        if (text.StartsWith(HeadingMark, StringComparison.Ordinal))
                            Write(canvas, bold, text[1..], x, y, HeadingSize);
                        else
                            Write(canvas, font, text, x, y, FontSize);
                    }
                    y -= LineHeight;
                }

                var footerCuit = p == pages.Count - 1 && LastPageFooterCuit is not null ? LastPageFooterCuit : FooterCuit;
                Write(canvas, font,
                    $"NOSIS.Manager | Información confidencial de uso exclusivo | Cli.SGR Ficticia | {FooterDate} | Doc.{footerCuit}",
                    40f, 30f, 6.5f);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A report whose sections are laid out the way the REAL template prints them (taken from masked
    /// shapes of real reports: line structure, label wording, x-positions, number formats). Every value
    /// is invented. Sections not yet reshaped keep the default layout.
    /// </summary>
    public static NosisReportPdfBuilder RealLayout()
    {
        var builder = new NosisReportPdfBuilder();

        // INFORMACIÓN GENERAL: a label row with its values underneath, then the headline figures.
        builder.Replace("perfil",
        [
            [("Tipo de sociedad", 48f), ("Estado", 157f), ("Antigüedad", 266f), ("Cantidad de empleados", 375f), ("Fecha de contrato", 485f)],
            [("Anónima", 48f), ("Activa", 157f), ("17 años", 266f), ("Entre 100 a 199", 375f), ("01/02/2008", 485f)],
            Line("Facturación estimada anual: Mayor a $ 13,579,246,801"),
            Line("Total de endeudamiento sistema financiero: $ 1,357,924,680"),
            Line("Total de compromisos mensuales sistema financiero: $ 98,765,432"),
            Line("Cheques rechazados: Existen cheques rechazados"),
            Line("Total de consultas en Nosis: Últimos 3 meses: 7 | Últimos 12 meses: 25"),
            Line("Actividad: 251100 - Fabricación de productos metálicos"),
            Line("Domicilio: Calle Señuelo 123"),
        ]);

        // CRITERIOS DE ACEPTACIÓN: the CDA name, the test banner, the config and version lines, then
        // "Resultado según configuración:" and the criteria, each with its verdict at a fixed column.
        builder.Replace("criterios",
        [
            Line("2025.101 - Modelo Pymes (Nueva Ver.)"),
            [("INFORMACIÓN SIN VALIDEZ - EN PROCESO DE PRUEBA", 38f)],
            Line("Criterio de Aceptación configurado por el cliente: 123456 - CDA FICTICIO"),
            Line("Bureau CDA: Modelo Pymes"),
            Line("Versión 2025.10.101 actualizada por el cliente el 01/10/2026"),
            [("Resultado según configuración:", 31f), ("Observado", 206f)],
            [("Bureau de Crédito del BCRA:", 31f), ("Rechazado", 206f)],
            [("Cheques Rechazados:", 31f), ("Cumple", 206f)],
            [("Concursos y Quiebras:", 31f), ("No Corresponde", 206f)],
        ]);

        // BUREAU DE CRÉDITO: "Antecedentes Crediticios", the "miles" note at the far right, a year row
        // over numeric months (11 12 | 1 … 10), the aggregate rows, entity rows (one name cut short with
        // "…", one name alone on its line), a line of another table that fits no column, a row whose
        // cell is not a figure — then the 12-month table again for the previous 12 months.
        builder.Replace("bureau", RealBureauRows());

        // EVOLUCIÓN DE ATRASOS: a header over three lines, rows "MM.YYYY", a "Situación 5" and a
        // "Total" column whose figures must not land in a bucket.
        builder.Replace("atrasos",
        [
            Line("Deudas por días de atraso"),
            [("Atraso en días", 272f)],
            [("Normal", 137f), ("Situación 5", 422f)],
            [("Período", 83f), ("Total", 493f)],
            [("31 a 90", 196f), ("91 a 180", 253f), ("181 a 365", 309f), ("Mayor a 365", 363f)],
            [("10.2026", 84f)],
            [("09.2026", 84f), ("1,234,567.8", 144f), ("12,345.6", 202f), ("-", 256f), ("0.0", 318f), ("2,345,678.9", 496f)],
            [("08.2026", 84f), ("1,000,000.5", 144f), ("2,000.0", 207f), ("9,876,543.2", 496f)],
        ]);

        // APORTES PATRONALES: "Como EMPLEADOR al …", the real column names, two-word values.
        builder.Replace("aportes",
        [
            [("Como EMPLEADOR al 31/10/2026", 29f)],
            [("Período", 51f), ("Aportes de seguro social", 134f), ("Aportes de obra social", 284f), ("Contrib. patronal obra social", 434f)],
            [("09/2026", 51f), ("Impago", 163f), ("Impago", 309f), ("Impago", 470f)],
            [("08/2026", 51f), ("Pago Parcial", 155f), ("Pago", 309f), ("Pago Parcial", 462f)],
            [("07/2026", 51f), ("Pago", 167f), ("Pago", 314f), ("Pago", 475f)],
            [("Fuente: AFIP", 29f)],
        ]);

        // CHEQUES RECHAZADOS: a Total for October, none for September, and August printed twice (page
        // break) before its Total.
        builder.Replace("cheques", RealChequeRows());

        // ÚLTIMOS BALANCES REDUCIDOS: "31 Dic 2023 | … | Variación", "Balance $ %" per year, each cell
        // "value  %", variation columns at the right, "Estado de resultados" with values only, a "#3"
        // footnote mark, then the PyME size table, which is not part of it.
        builder.Replace("balances", RealBalanceRows());
        builder.Without("ratios");

        // ANTECEDENTES FISCALES: one block per organism, label lines, two rates on one line.
        builder.Replace("fiscales",
        [
            [("AGIP - Regímenes General de Percepción y Retención (C.A.B.A.)", 29f)],
            [("Padrón Vigente", 29f)],
            [("Alícuota percepción: 3,00 %", 29f), ("Alícuota retención: 1,50 %", 239f)],
            [("Vigencia desde: 01.11.2026", 29f), ("Vigencia hasta: 30.11.2026", 239f)],
            [("Fecha emisión: 25.10.2026", 29f)],
            [("Fuente: AGIP | www.agip.example", 465f)],
        ]);

        // COMUNICADOS: "Fecha:" lines, each followed by a sender with a CUIT and free text.
        builder.Replace("comunicados",
        [
            Line("IMPORTANTE: Los comunicados provienen del BCRA y de fuentes directas."),
            Line("Fecha: 01/09/2026"),
            Line("Remitente: SEÑUELO SA CUIT: 30111222333"),
            Line("-- COMUNICADO Señuelo 01/09/26 : texto libre con plan P123456"),
            Line("Fecha: 15/10/2026"),
            Line("Remitente: SEÑUELO SA CUIT: 30111222333"),
            Line("-- COMUNICADO Señuelo 15/10/26 : otro texto libre"),
        ]);

        // IMPORTACIONES: a header naming the years, chapter rows; EXPORTACIONES absent.
        builder.Replace("importaciones",
        [
            [("Productos que importa", 235f)],
            [("Pos", 33f), ("Descripción", 210f), ("Año", 415f), ("2023", 438f), ("2024", 461f), ("2025", 484f)],
            [("84", 35f), ("máquinas Señuelo y aparatos", 52f)],
            [("85", 35f), ("material eléctrico Señuelo", 52f)],
        ]);
        builder.Without("exportaciones");

        // MARCAS REGISTRADAS: "Registra N marcas registradas", the trademarks, then a block of companies
        // with their CUITs that the parser never reads.
        builder.Replace("marcas",
        [
            Line("Registra 4 marcas registradas"),
            [("Acta: Señuelo", 51f), ("Clase: 9", 156f), ("Fecha concesión: 01/02/2020", 261f), ("Vigente", 482f)],
            Line("Sociedades con domicilio coincidente"),
            [("30-11122233-4", 40f), ("SEÑUELO DOMICILIO SA", 101f)],
        ]);

        // JUICIOS: one block per lawsuit.
        builder.Replace("juicios",
        [
            Line("IMPORTANTE: La información de juicios surge de fuentes públicas."),
            Line("Fecha Inicio: 12/03/2024"),
            Line("Juzgado, Fuero Civil, Juzgado Civil y Comercial 4 Ciudad Ficticia N° 7, Expte.:2024/55"),
            Line("Carátula:"),
            Line("PERSONA SEÑUELO ACTORA SA"),
            Line("Actor/es: SEÑUELO ACTOR SRL 30111222333"),
            Line("Objeto: Cobro Ejecutivo de Pesos (12345678-123456)"),
        ]);

        // MAV: a currency label that wraps, its amount alone on the next line, then the monthly
        // cross-table, which is not read.
        builder.Replace("mav",
        [
            Line("Por instrumento"),
            [("Instrumento", 35f), ("Monto", 113f)],
            [("Cheque", 31f), ("1.527.384.960", 117f)],
            [("Pagaré", 31f), ("412.000.000", 123f)],
            [("Total general", 31f), ("1,939,384,960", 117f)],
            Line("Por moneda"),
            [("Moneda", 86f), ("Monto", 198f)],
            [("Cheques en Pesos", 31f), ("1.939.384.960", 204f)],
            [("Cheques en USD CABLE (Expresado", 31f)],
            [("0", 240f)],
            [("en $)", 31f)],
            Line("Por instrumento"),
            [("Instrumento", 35f), ("Cant.", 106f), ("Monto", 126f), ("Cant.", 174f), ("Total", 524f)],
            [("10/2026", 44f), ("999.999.999", 124f), ("888.888.888", 205f)],
        ]);

        return builder;
    }

    private static List<List<(string Text, float X)>> RealBalanceRows()
    {
        List<(string Text, float X)> Row(string label, float labelX, params (string Value, string? Pct)[] years)
        {
            float[] valueX = [160f, 281f, 401f];
            float[] pctX   = [236f, 356f, 476f];
            var row = new List<(string Text, float X)> { (label, labelX) };
            for (var i = 0; i < years.Length; i++)
            {
                row.Add((years[i].Value, valueX[i]));
                if (years[i].Pct is { } pct)
                    row.Add((pct, pctX[i]));
            }

            row.Add(("12.5", 514f));   // variation columns
            row.Add(("-3.1", 551f));
            return row;
        }

        return
        [
            [("31", 170f), ("Dic", 183f), ("2023", 199f), ("31", 290f), ("Dic", 303f), ("2024", 319f), ("31", 410f), ("Dic", 423f), ("2025", 439f), ("Variación", 521f)],
            [("Moneda del Balance", 52f)],
            [("Balance", 162f), ("$", 187f), ("%", 234f), ("Balance", 282f), ("$", 307f), ("%", 354f), ("Balance", 403f), ("$", 427f), ("%", 474f), ("24-25", 508f), ("23-24", 546f)],
            Row("Activo Corriente", 42f, ("10,000,000,000", "60.0"), ("11,000,000,000", "61.0"), ("12,000,000,000", "62.0")),
            Row("Activo no Corriente", 42f, ("6,000,000,000", "40.0"), ("7,000,000,000", "39.0"), ("7,500,000,000", "38.0")),
            Row("Total Activo", 31f, ("16,000,000,000", "100.0"), ("18,000,000,000", "100.0"), ("19,500,000,000", "100.0")),
            Row("Pasivo Corriente", 42f, ("5,000,000,000", "31.3"), ("5,500,000,000", "30.6"), ("6,000,000,000", "30.8")),
            Row("Pasivo no Corriente", 42f, ("1,000,000,000", "6.3"), ("1,200,000,000", "6.7"), ("1,300,000,000", "6.7")),
            Row("Total Pasivo", 31f, ("6,000,000,000", "37.5"), ("6,700,000,000", "37.2"), ("7,300,000,000", "37.4")),
            Row("Patrimonio Neto", 31f, ("10,000,000,000", "62.5"), ("11,300,000,000", "62.8"), ("12,200,000,000", "62.6")),
            [("Estado de resultados", 31f)],
            Row("Ventas Netas", 42f, ("30,000,000,000", null), ("33,000,000,000", null), ("36,000,000,000", null)),
            Row("Result. del Ejercicio #3", 31f, ("2,000,000,000", null), ("-500,000,000", null), ("2,500,000,000", null)),
            Line("Información de los últimos balances (A. presentación: 01/05/2026)"),
            [("Tamaño / Sector", 37f), ("Activ.", 132f), ("Ventas", 324f), ("Categoría", 418f)],
            [("Ventas", 30f), ("999,999,999,999", 146f), ("999,999,999,999", 246f)],
        ];
    }

    private static List<List<(string Text, float X)>> RealBureauRows()
    {
        var centers = Enumerable.Range(0, 12).Select(i => 113f + (i * 40f)).ToArray();
        int[] months = [11, 12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        List<(string Text, float X)> Row(string? label, Func<int, string?> value, float labelX = 38f)
        {
            var row = new List<(string Text, float X)>();
            if (label is not null)
                row.Add((label, labelX));
            for (var i = 0; i < 12; i++)
            {
                if (value(i) is { } v)
                    row.Add((v, centers[i] - 8f));
            }

            return row;
        }

        List<List<(string Text, float X)>> Table(string firstYear, string secondYear, string uno)
        =>
        [
            [(firstYear, 125f), (secondYear, 362f)],
            months.Select((m, i) => (m.ToString(CultureInfo.InvariantCulture), centers[i] - 3f)).ToList(),
            Row("Situación", _ => "1", 32f),
            Row("Comp. mensuales", _ => "1,111,111", 32f),
            Row("Endeudamiento", _ => "22,222,222", 32f),
            Row("Bco Ficticio Uno", _ => uno),
            Row("Bco Ficticio Do…", _ => "123,456"),
            [("Cía Financiera Tres", 38f)],
            Row(null, _ => "45,678"),
            Row("Bco Cuatro", i => i >= 10 ? "9.5" : null),
        ];

        var rows = new List<List<(string Text, float X)>>();
        rows.Add([("Antecedentes Crediticios", 29f)]);
        rows.Add([("Importes expresados en miles de $", 400f)]);
        rows.AddRange(Table("2025", "2026", "1,234,567"));
        rows.Add([("Nota interna", 38f), ("123", 110f), ("999", 580f)]);   // fits no column: skipped
        rows.Add([("Bco Raro", 38f), ("abc", 110f)]);                      // not a figure: skipped
        rows.AddRange(Table("2024", "2025", "2,000,000"));
        return rows;
    }

    private static List<List<(string Text, float X)>> RealChequeRows()
    {
        List<(string Text, float X)> Month(string label, string count, string amount)
            => [(label, 47f), (count, 235f), (amount, 271f)];

        List<(string Text, float X)> Cheque(string date, string amount)
            => [("12345", 86f), (date, 121f), ("1", 239f), (amount, 278f), ("Sin fondos", 320f)];

        return
        [
            Line("Cheques personas jurídicas"),
            [("Fecha de Rechazo", 216f), ("Pago de Multa", 330f)],
            [("Número", 59f), ("Fecha", 121f), ("Importe", 271f), ("Causal", 388f)],
            Month("Octubre 2026", "3", "4,500,000.00"),
            Cheque("05/10/2026", "1,500,000.00"), Cheque("06/10/2026", "1,500,000.00"), Cheque("07/10/2026", "1,500,000.00"),
            [("Total = 3", 47f), ("4,500,000.00", 271f)],
            Month("Septiembre 2026", "2", "300,000.50"),
            Cheque("05/09/2026", "100,000.25"), Cheque("06/09/2026", "200,000.25"),
            Month("Agosto 2026", "2", "1,000,000.00"),
            Cheque("05/08/2026", "500,000.00"), Cheque("06/08/2026", "500,000.00"),
            Month("Agosto 2026", "1", "500,000.00"),
            Cheque("07/08/2026", "500,000.00"),
            [("Total = 3", 47f), ("1,500,000.00", 271f)],
        ];
    }

    /// <summary>A plain, non-NOSIS PDF with some text.</summary>
    public static byte[] PlainPdf(params string[] lines)
    {
        using var buffer = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(buffer)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var canvas = new PdfCanvas(pdf.AddNewPage(PageSize.A4));
            var y = 780f;
            foreach (var line in lines)
            {
                Write(canvas, font, line, 40f, y, 10f);
                y -= 14f;
            }
        }

        return buffer.ToArray();
    }

    private static void Write(PdfCanvas canvas, PdfFont font, string text, float x, float y, float size)
        => canvas.BeginText().SetFontAndSize(font, size).SetTextMatrix(1, 0, 0, 1, x, y).ShowText(text).EndText();

    private List<List<List<(string Text, float X)>>> Paginate()
    {
        var all = new List<List<(string Text, float X)>>();
        foreach (var (_, heading, rows) in _sections)
        {
            all.Add([("", 40f)]);
            if (heading.Length > 0)
            {
                // "A||B": two heading lines; a leading ">>" indents the heading.
                foreach (var part in heading.Split("||"))
                {
                    var indented = part.StartsWith(">>", StringComparison.Ordinal);
                    all.Add([(HeadingMark + (indented ? part[2..] : part), indented ? 90f : 40f)]);
                }
            }

            all.AddRange(rows);
        }

        var perPage = (int)((Top - Bottom) / LineHeight);
        var pages = new List<List<List<(string Text, float X)>>>();
        for (var i = 0; i < all.Count; i += perPage)
            pages.Add(all.Skip(i).Take(perPage).Where(r => r.Any(c => c.Text.Length > 0)).ToList());

        return pages;
    }

    private static List<(string Text, float X)> Line(string text) => [(text, 40f)];

    private void Add(string key, string heading, params List<(string Text, float X)>[] rows)
        => _sections.Add((key, heading, rows.ToList()));

    private void BuildDefault()
    {
        // Page 1 header block, before any heading: dropped by the splitter.
        // The header block: a label row, the score gauge (scale ends "1" and "999", needle value
        // off-axis), then the values aligned under their labels. Situación here (3) differs from the
        // evolution's last month (1) on purpose: each is stored as printed.
        Add("cabecera", "",
            Line($"Razón social: {BusinessName}"),
            Line($"CUIT: {SubjectCuit}"),
            [("SCORE", 300f), ("SITUACIÓN BCRA", 380f)],
            [("735", 230f), ("1", 270f), ("999", 345f)],
            [("742", 306f), ("3", 409f)]);

        Add("perfil", "PERFIL DEL CONSULTADO / INFORMACIÓN GENERAL",
            Line("Antigüedad: 23 años"),
            Line("Empleados: 10 a 49"),
            Line("Actividad principal: Fabricación de piezas metálicas"),
            Line("Domicilio: Calle Señuelo 123"),
            Line("Teléfono: 011 4444-5555"));

        Add("contacto", "INFORMACIÓN DE CONTACTO",
            Line("contacto@senuelo.example"),
            Line("Calle Señuelo 123, Ciudad Ficticia"),
            Line("011 4444-5555"));

        Add("arca", "ARCA",
            Line("Fecha de inscripción: 15/03/2003"),
            Line("Ganancias: Inscripto"),
            Line("IVA: Responsable Inscripto"));

        // Printed twice, the second time indented; a CUIT column the parser must drop.
        Add("fiscales", "ANTECEDENTES FISCALES||>>ANTECEDENTES FISCALES",
            [("Organismo", 40f), ("Régimen", 110f), ("Alícuota", 260f), ("Desde", 330f), ("Hasta", 400f), ("CUIT", 470f)],
            [("AGIP", 40f), ("Percepción IIBB", 110f), ("5,00 %", 260f), ("01/11/2026", 330f), ("30/11/2026", 400f), ("20-30111222-3", 470f)],
            [("ARBA", 40f), ("Retención IIBB", 110f), ("1,75 %", 260f), ("01/10/2026", 330f), ("31/12/2026", 400f), ("20-30111222-3", 470f)]);

        Add("mipyme", "MI PYME",
            Line("Estado: Vigente"),
            Line("Vigencia: 01/01/2026 al 31/12/2026"));

        Add("resumen", "RESUMEN CONFIGURABLE",
            Line("Score: 750"),
            Line("Situación BCRA: 1"),
            Line("Endeudamiento total: 7,258,316,409"),
            Line("Compromisos mensuales: 31,472.6"),
            Line("Facturación estimada: 100,000,000 a 250,000,000"),
            Line("Cheques rechazados: Sí"),
            Line("Referencias comerciales: 2"),
            Line("Aportes impagos últimos 12 meses: No"),
            Line("CONSULTAS"),   // a body-style caps line equal to a known heading: a label, not a heading
            Line("Consultas últimos 3 meses: 4"),
            Line("Consultas últimos 12 meses: 11"));

        Add("aportes", "APORTES PATRONALES",
            [("Período", 40f), ("Seguridad Social", 120f), ("Obra Social", 240f), ("Contribución Obra Social", 360f)],
            [("07/2026", 40f), ("Pagado", 120f), ("Pagado", 240f), ("Pagado", 360f)],
            [("08/2026", 40f), ("Impago", 120f), ("Pagado", 240f), ("-", 360f)]);

        Add("nomina", "NÓMINA DE EMPLEADOS",
            Line("Persona Señuelo Uno 20-30111222-3 DNI 30.111.222"),
            Line("Persona Señuelo Dos 20301112223"));

        Add("arbol", "RELACIONES / ÁRBOL",
            Line("Socio Señuelo 20-30111222-3 Presidente"));

        Add("firmantes", "FIRMANTES DE CHEQUES",
            Line("Firmante Señuelo 27-30555666-1"));

        // Four closings (the oldest must drop), then the ratios and the PyME size table under them,
        // whose "Ventas" must not overwrite the balance's.
        Add("balances", "BALANCES / ÚLTIMOS BALANCES REDUCIDOS",
            BalanceRow("Cierre", "31/12/2022", "31/12/2023", "31/12/2024", "31/12/2025"),
            BalanceRow("Activo corriente", "900,000", "1,000,000", "1,100,000", "1,200,000"),
            BalanceRow("Activo no corriente", "400,000", "500,000", "600,000", "700,000"),
            BalanceRow("Total activo", "1,300,000", "1,500,000", "1,700,000", "1,900,000"),
            BalanceRow("Pasivo corriente", "300,000", "400,000", "500,000", "600,000"),
            BalanceRow("Pasivo no corriente", "100,000", "100,000", "100,000", "100,000"),
            BalanceRow("Total pasivo", "400,000", "500,000", "600,000", "700,000"),
            BalanceRow("Patrimonio neto", "900,000", "1,000,000", "1,100,000", "1,200,000"),
            BalanceRow("Ventas netas", "2,000,000", "2,500,000", "3,000,000", "3,500,000"),
            BalanceRow("Resultado del ejer…", "100,000", "150,000", "-", "-50,000"));

        Add("ratios", "RATIOS ECONOMICOFINANCIEROS",
            Line("Liquidez corriente 2.0"),
            Line("Tamaño de empresa: Pequeña"),
            BalanceRow("Ventas", "999,999,999", "999,999,999", "999,999,999", "999,999,999"));

        Add("evolucion", "EVOLUCIÓN ANTECEDENTES", EvolucionRows().ToArray());

        Add("criterios", "CRITERIOS DE ACEPTACIÓN",
            Line("INFORMACIÓN SIN VALIDEZ - EN PROCESO DE PRUEBA"),
            Line("Criterio: Política PyME Ficticia"),
            Line("Resultado: Aprobado"),
            [("Situación BCRA máxima", 40f), ("Cumple", 300f)],
            [("Sin cheques sin fondos", 40f), ("No cumple", 300f)]);

        Add("bancaria", "INFORMACIÓN BANCARIA", Line("Importes expresados en miles de $"));

        Add("bureau", "BUREAU DE CRÉDITO",
            MonthRow(["2026"], [200f]),
            MonthRow(["Abr", "May", "Jun", "Jul", "Ago", "Sep"], DeudaCenters),
            DeudaRow("Banco Ficticio Uno", [("1", "1,200"), ("1", "1,150"), ("1", "1,100"), ("2", "1,050"), ("1", "1,000"), ("1", "950.5")]),
            DeudaRow("Banco Ficticio Dos", [("-", "-"), ("1", "300"), ("1", "290"), ("1", "280"), ("1", "270"), ("1", "260")]),
            DeudaRow("Total", [("", "1,200"), ("", "1,450"), ("", "1,390"), ("", "1,330"), ("", "1,270"), ("", "1,210.5")]));

        Add("ultimo", "ÚLTIMO INFORME", Line("Banco Ficticio Uno 1 950.5"));

        Add("atrasos", "EVOLUCIÓN DE ATRASOS",
            MonthRow(["2026"], [200f]),
            MonthRow(["Jul", "Ago", "Sep"], [200f, 260f, 320f]),
            [("Normal", 40f), ("1,000", 192f), ("1,100", 252f), ("1,200", 312f)],
            [("31 a 90", 40f), ("10.5", 192f), ("-", 258f), ("0", 318f)],
            [("91 a 180", 40f), ("-", 198f), ("-", 258f), ("-", 318f)],
            [("181 a 365", 40f), ("-", 198f), ("-", 258f), ("-", 318f)],
            [("Mayor a 365", 40f), ("-", 198f), ("-", 258f), ("2", 318f)]);

        Add("lineas", "LÍNEAS DE CRÉDITO", Line("Descubierto 500"));

        // MAV mixes formats: dot thousands in the body, comma thousands in the total. "1.234" is ambiguous here.
        Add("mav", "MERCADO ARGENTINO DE VALORES",
            Line("Por instrumento"),
            [("Cheques de pago diferido", 40f), ("1.527.384.960", 300f)],
            [("Pagarés", 40f), ("412.000.000", 300f)],
            [("Facturas de crédito", 40f), ("1.234", 300f)],
            Line("Por moneda"),
            [("Pesos", 40f), ("1.939.384.960", 300f)],
            [("Dólares", 40f), ("0", 300f)],
            [("Total", 40f), ("1,939,384,960", 300f)]);
        Add("compromisos", "COMPROMISOS MENSUALES", Line("Banco Ficticio Uno 120"));

        Add("cheques", "CHEQUES RECHAZADOS DEL BCRA", []);

        Add("refbancarias", "REFERENCIAS BANCARIAS", Line("Banco Señuelo cuenta 1234567"));

        Add("referencias", "REFERENCIAS COMERCIALES",
            Line("Cantidad: 2"),
            [("Fuente", 40f), ("Fecha", 200f), ("Contacto", 280f), ("Teléfono", 420f)],
            [("Proveedora Uno S.A.", 40f), ("01/05/2026", 200f), ("Contacto Señuelo", 280f), ("011 4444-5555", 420f)],
            [("Distribuidora Dos SRL", 40f), ("02/06/2026", 200f), ("Otra Señuelo", 280f), ("contacto@senuelo.example", 420f)]);

        Add("chequespositivos", "CHEQUES POSITIVOS",
            Line("Banco Señuelo 20-30111222-3 15/10/2026 50,000.00"));

        Add("srt", "OTROS ANTECEDENTES (SRT)",
            Line("Aseguradora: ART Ficticia S.A."),
            Line("Estado: Vigente"),
            Line("Extinguido por falta de pago: No"));

        Add("comunicados", "COMUNICADOS DEL BCRA Y DE FUENTES DIRECTAS",
            Line("01/09/2026 Comunicado Señuelo sobre 20-30111222-3 plan 4455667"),
            Line("Texto libre Señuelo del comunicado"),
            Line("15/10/2026 Otro comunicado Señuelo"));

        Add("sisa", "SISA",
            Line("Estado: Activo"),
            Line("Categorías: Productor, Acopiador"),
            Line("CBU: 0000000000000000000001"));

        // A heading the parser does NOT know: printed like the others, so it must close SISA and drop its lines.
        Add("desconocida", "SECCIÓN DESCONOCIDA DE PRUEBA",
            Line("Dato Señuelo 20-30111222-3"),
            [("Columna Señuelo", 40f), ("4444-5555", 300f)]);

        Add("importaciones", "IMPORTACIONES",
            [("2025", 40f), ("Capítulo Señuelo de máquinas", 100f)],
            [("2026", 40f), ("Capítulo Señuelo de repuestos", 100f)]);

        Add("exportaciones", "EXPORTACIONES", Line("No registra exportaciones"));

        Add("marcas", "MARCAS REGISTRADAS",
            Line("Cantidad: 3"),
            Line("Marca Señuelo Uno Clase 7"));

        Add("consultas", "CONSULTAS Y SEGUIMIENTOS",
            Line("01/10/2026 Banco Consultor Señuelo 20-30111222-3"));

        Add("juicios", "INFORMACIÓN POR RAZÓN SOCIAL / JUICIOS COMO DEMANDADO",
            Line("Cantidad: 2"),
            [("Fecha", 40f), ("Tipo", 110f), ("Jurisdicción", 220f), ("Actor", 330f), ("Expediente", 460f)],
            [("12/03/2024", 40f), ("Ejecutivo", 110f), ("CABA", 220f), ("Persona Señuelo Actora", 330f), ("12345/2024", 460f)],
            [("05/08/2025", 40f), ("Laboral", 110f), ("Córdoba", 220f), ("Otra Señuelo", 330f), ("777/2025", 460f)]);

        Add("sociedades", "SOCIEDADES",
            Line("EMPRESA SEÑUELO S.A. EL DIRECTORIO CONVOCA A ASAMBLEA"),
            Line("Edicto Señuelo 20-30111222-3 Boletín 12345678"));
    }

    private static readonly float[] DeudaCenters = [210f, 265f, 320f, 375f, 430f, 485f];

    private static List<(string Text, float X)> MonthRow(string[] labels, float[] centers)
        => labels.Select((l, i) => (l, centers[i] - 5f)).ToList();

    private static List<(string Text, float X)> DeudaRow(string name, (string Sit, string Monto)[] cells)
    {
        var row = new List<(string Text, float X)> { (name, 40f) };
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i].Sit.Length > 0)
                row.Add((cells[i].Sit, DeudaCenters[i] - 16f));
            row.Add((cells[i].Monto, DeudaCenters[i] - 2f));
        }

        return row;
    }

    private static readonly float[] BalanceColumns = [200f, 270f, 340f, 410f];

    private static List<(string Text, float X)> BalanceRow(string label, params string[] values)
    {
        var row = new List<(string Text, float X)> { (label, 40f) };
        for (var i = 0; i < values.Length; i++)
            row.Add((values[i], BalanceColumns[i]));
        return row;
    }

    /// <summary>24 months, Oct 2024 → Sep 2026, under the year line "2024 2025 2026".</summary>
    private static List<List<(string Text, float X)>> EvolucionRows()
    {
        string[] names = ["Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic"];
        var centers = Enumerable.Range(0, 24).Select(i => 200f + (i * 15f)).ToArray();
        var months = Enumerable.Range(0, 24).Select(i => names[(9 + i) % 12]).ToArray();

        List<(string Text, float X)> Values(string label, Func<int, string> value)
        {
            var row = new List<(string Text, float X)> { (label, 40f) };
            for (var i = 0; i < 24; i++)
                row.Add((value(i), centers[i] - 3f));
            return row;
        }

        return
        [
            [("2024", centers[0] - 12f), ("2025", centers[3] - 12f), ("2026", centers[15] - 12f)],
            months.Select((m, i) => (m, centers[i] - 5f)).ToList(),
            Values("Score", i => (700 + i).ToString(CultureInfo.InvariantCulture)),
            Values("Cantidad de bancos", _ => "3"),
            Values("Situación", i => i == 5 ? "2" : "1"),
            Values("Cantidad de tarjetas", _ => "-"),
            [("Cheques rechazados BCRA", 40f)],
            Values("Rechazados", i => i == 23 ? "5" : "0"),
            Values("Recuperados", _ => "0"),
            Values("No recuperados", i => i == 23 ? "5" : "0"),
            Values("No pago multa", _ => "0"),
            Values("Positivos", _ => "1"),
            Values("Aportes Patronales Empleador", i => i == 22 ? "PP" : i == 23 ? "I" : "P"),
            Values("Referencias Comerciales", _ => "-"),
            Values("Consultas recibidas", i => (i % 3).ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>
    /// The evolution table as the real template prints it: NUMERIC month headers ("12 | 1 … 12 | 1 … 11")
    /// under year labels centred over their months ("2024 2025 2026"), and rows with BLANK cells — Score
    /// has 24 values, Situación 22 (blank in columns 3 and 10), Cantidad de bancos 23 (blank in column 0).
    /// Score in column i is 800 + i.
    /// </summary>
    public static List<List<(string Text, float X)>> NumericEvolucionRows()
    {
        var centers = Enumerable.Range(0, 24).Select(i => 200f + (i * 15f)).ToArray();
        var months  = Enumerable.Range(0, 24).Select(i => ((11 + i) % 12) + 1).ToArray();   // 12, 1..12, 1..11

        List<(string Text, float X)> Values(string label, Func<int, string?> value)
        {
            var row = new List<(string Text, float X)> { (label, 40f) };
            for (var i = 0; i < 24; i++)
            {
                if (value(i) is { } v)
                    row.Add((v, centers[i] - 5f));
            }

            return row;
        }

        float Mid(int from, int to) => ((centers[from] + centers[to]) / 2f) - 8f;

        return
        [
            [("2024", Mid(0, 0)), ("2025", Mid(1, 12)), ("2026", Mid(13, 23))],
            months.Select((m, i) => (m.ToString(CultureInfo.InvariantCulture), centers[i] - 3f)).ToList(),
            Values("Score", i => (800 + i).ToString(CultureInfo.InvariantCulture)),
            Values("Situación", i => i is 3 or 10 ? null : "1"),
            Values("Cantidad de bancos", i => i == 0 ? null : "4"),
        ];
    }

    /// <summary>Two months under the heading, each with per-cheque detail rows, then the Total line.</summary>
    private List<List<(string Text, float X)>> ChequeRows()
    {
        var rows = new List<List<(string Text, float X)>>();
        rows.Add([("Fecha", 40f), ("Número", 100f), ("Entidad", 160f), ("Importe", 300f), ("Causal", 380f)]);

        void Month(string label, int year, decimal perCheque)
        {
            var total = perCheque * ChequesPerMonth;
            rows.Add([($"{label} {year}", 40f), (ChequesPerMonth.ToString(CultureInfo.InvariantCulture), 160f),
                      (total.ToString("#,##0.00", CultureInfo.InvariantCulture), 300f)]);
            for (var i = 0; i < ChequesPerMonth; i++)
            {
                rows.Add([($"15/10/{year}", 40f), ("0000123", 100f), ("Banco Ficticio Uno", 160f),
                          (perCheque.ToString("#,##0.00", CultureInfo.InvariantCulture), 300f), ("Sin fondos Señuelo", 380f)]);
            }
        }

        Month("Septiembre", 2026, 1_000.50m);
        Month("Octubre", 2026, 250_000m);

        var sum = (1_000.50m + 250_000m) * ChequesPerMonth;
        rows.Add([($"Total = {ChequesPerMonth * 2}", 40f), (sum.ToString("#,##0.00", CultureInfo.InvariantCulture), 300f)]);
        return rows;
    }
}
