// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios.NosisReport;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>
/// Opt-in LOCAL harness, never a CI test: runs <see cref="NosisManagerReportParser"/> over a folder of
/// REAL NOSIS Manager "Informe Individual" PDFs and prints, per file, how each section read. No model
/// call, no cost, nothing written.
///
/// <para>
/// Gated by <see cref="EnvironmentFactAttribute"/> on FINANCIA_NOSIS_REPORTS_DIR, a local folder of
/// <c>*.pdf</c>: unset, it reports as skipped and reads nothing.
/// </para>
///
/// <para>
/// ⚠ The PDFs are clients' credit reports: the folder (and the expected file below) must be OUTSIDE the
/// repository (enforced), and a real report is never committed. Each file is named by its INDEX in
/// sorted order, never by file name. Per file it prints ONLY: the page count, the encryption
/// parameters, the elapsed time, the outcome, the subject's person type (from the CUIT prefix — the
/// CUIT is never printed), how many headings were found, and each section's status and row counts.
/// </para>
///
/// <para>
/// FINANCIA_NOSIS_REPORTS_EXPECTED, optional, is a hand-written ground-truth JSON (shape in the guide).
/// Entries are matched to files by SHA-256 and compared field by field with
/// <see cref="NosisReportGroundTruth"/>; the harness prints per field only OK / DIFF / MISSING — never
/// either value — and names entries by their index in the file, never by their alias.
/// </para>
///
/// <para>
/// FINANCIA_NOSIS_REPORTS_SHAPES=1 adds, for each section that reads "unreadable", the SHAPE of its lines;
/// =all adds every section's and the header block's. Every letter becomes <c>a</c>/<c>A</c>, every digit
/// <c>9</c>, punctuation stays; each line carries its printed style (<c>[9.0 B #1f4e79]</c>) and each word
/// its left edge (<c>Aaaaa@40</c>). It also lists the headings found, known ones by kind and unknown ones
/// masked. No value survives the mask.
/// </para>
/// </summary>
public sealed class NosisManagerReportHarnessTests
{
    private const string DirVariable      = "FINANCIA_NOSIS_REPORTS_DIR";
    private const string ExpectedVariable = "FINANCIA_NOSIS_REPORTS_EXPECTED";
    private const string ShapesVariable   = "FINANCIA_NOSIS_REPORTS_SHAPES";
    private const string SolutionFileName = "Api.Web.ServiciosFinancieros.slnx";
    private const int MaxShapeLines = 80;

    private readonly ITestOutputHelper _output;

    public NosisManagerReportHarnessTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(DirVariable)]
    public void Parse_RealReports_PrintsSectionStatusesAndRowCounts()
    {
        var inputDir = Path.GetFullPath(Environment.GetEnvironmentVariable(DirVariable)!);
        if (!Directory.Exists(inputDir))
            throw new InvalidOperationException($"{DirVariable} does not point to an existing folder.");

        GuardOutsideRepository(inputDir, DirVariable);

        var shapesMode = Environment.GetEnvironmentVariable(ShapesVariable)?.Trim();
        var expected = LoadExpected();

        var files = Directory.GetFiles(inputDir, "*.pdf", SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.Ordinal)
                             .ToList();
        Assert.NotEmpty(files);

        var parser = new NosisManagerReportParser();
        _output.WriteLine($"{files.Count} file(s). Parser {NosisManagerReportParser.ParserVersion}." +
                          (expected is null ? string.Empty : $" Ground truth: {expected.Count} entr(ies)."));

        var matchedEntries = new HashSet<int>();

        for (var i = 0; i < files.Count; i++)
        {
            var bytes = File.ReadAllBytes(files[i]);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var stopwatch = Stopwatch.StartNew();
            var result = parser.Parse(bytes, CancellationToken.None);
            stopwatch.Stop();

            var split = NosisManagerReportParser.SectionsForDiagnostics(bytes, CancellationToken.None);
            var subject = CuitHelper.PersonTypeFromPrefix(result.SubjectCuitDigits) ?? "?";

            _output.WriteLine(string.Empty);
            _output.WriteLine(
                $"#{i + 1}: pages={result.PageCount}, encryption={Encryption(bytes)}, elapsedMs={stopwatch.ElapsedMilliseconds}, " +
                $"outcome={result.Outcome}{(result.Reason is null ? string.Empty : $" ({result.Reason})")}, " +
                $"subject={subject}, droppedStrings={result.DroppedStrings}" +
                (split is null ? string.Empty
                    : $", headings={split.Headings.Count(h => h.Known)} known + {split.Headings.Count(h => !h.Known)} unknown, " +
                      $"styleCalibrated={split.StyleCalibrated}"));

            if (expected is not null)
                CompareWithGroundTruth(result, sha, expected, matchedEntries);

            if (result.Report is not { } report)
                continue;

            foreach (var (key, status) in report.Sections)
                _output.WriteLine($"    {key,-24} {status,-10} {Counts(key, report)}");

            if (shapesMode is "1" or "all" && split is not null)
                PrintShapes(split, report, all: shapesMode == "all");
        }

        if (expected is not null)
        {
            var unmatched = Enumerable.Range(0, expected.Count).Where(k => !matchedEntries.Contains(k)).ToList();
            _output.WriteLine(string.Empty);
            _output.WriteLine(unmatched.Count == 0
                ? "Every ground-truth entry matched a file."
                : $"Ground-truth entries with no file of that SHA-256: {string.Join(", ", unmatched.Select(k => $"[{k}]"))}");
        }
    }

    // ── Ground truth ──────────────────────────────────────────────────────────

    private List<JsonElement>? LoadExpected()
    {
        var path = Environment.GetEnvironmentVariable(ExpectedVariable);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new InvalidOperationException($"{ExpectedVariable} does not point to an existing file.");

        GuardOutsideRepository(Path.GetDirectoryName(full)!, ExpectedVariable);

        using var doc = JsonDocument.Parse(File.ReadAllText(full));
        return doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private void CompareWithGroundTruth(NosisReportParseResult result, string sha, List<JsonElement> expected, HashSet<int> matched)
    {
        var index = expected.FindIndex(e =>
            e.TryGetProperty("sha256", out var s) && string.Equals(s.GetString(), sha, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            _output.WriteLine("    ground truth: no entry with this file's SHA-256");
            return;
        }

        matched.Add(index);
        var fields = NosisReportGroundTruth.Compare(result, expected[index]);
        _output.WriteLine(
            $"    ground truth [{index}]: OK {fields.Count(f => f.Verdict == GroundTruthVerdict.Ok)}, " +
            $"DIFF {fields.Count(f => f.Verdict == GroundTruthVerdict.Diff)}, " +
            $"MISSING {fields.Count(f => f.Verdict == GroundTruthVerdict.Missing)}");

        foreach (var field in fields)
            _output.WriteLine($"      {field.Path,-48} {field.Verdict.ToString().ToUpperInvariant()}");
    }

    // ── Counts ────────────────────────────────────────────────────────────────

    /// <summary>Row counts only — never a value.</summary>
    private static string Counts(string key, NosisManagerReport r) => key switch
    {
        NosisReportSectionKeys.Summary                 => r.Summary is null ? "-" : $"fields={SummaryFields(r.Summary)}",
        NosisReportSectionKeys.Evolucion24m            => r.Evolucion24m is null ? "-" : $"months={r.Evolucion24m.Count}",
        NosisReportSectionKeys.DeudaPorEntidad         => r.DeudaPorEntidad is null ? "-" : $"entities={r.DeudaPorEntidad.Count}, cells={r.DeudaPorEntidad.Sum(d => d.Periodos.Count)}",
        NosisReportSectionKeys.Atrasos                 => r.Atrasos is null ? "-" : $"months={r.Atrasos.Count}",
        NosisReportSectionKeys.AportesPatronales       => r.AportesPatronales is null ? "-" : $"months={r.AportesPatronales.Count}",
        NosisReportSectionKeys.ChequesRechazadosPorMes => r.ChequesRechazadosPorMes is null ? "-" : $"months={r.ChequesRechazadosPorMes.Count}",
        NosisReportSectionKeys.CriteriosAceptacion     => r.CriteriosAceptacion is null ? "-" : $"criteria={r.CriteriosAceptacion.Criterios.Count}, enPrueba={r.CriteriosAceptacion.EnPrueba}",
        NosisReportSectionKeys.Juicios                 => r.Juicios is null ? "-" : $"items={r.Juicios.Items.Count}",
        NosisReportSectionKeys.Sisa                    => r.Sisa is null ? "-" : $"categories={r.Sisa.Categorias.Count}",
        NosisReportSectionKeys.Srt                     => r.Srt is null ? "-" : $"fields={new object?[] { r.Srt.Aseguradora, r.Srt.Vigente, r.Srt.ExtinguidoPorFaltaPago }.Count(v => v is not null)}",
        NosisReportSectionKeys.ReferenciasComerciales  => r.ReferenciasComerciales is null ? "-" : $"sources={r.ReferenciasComerciales.Fuentes.Count}",
        NosisReportSectionKeys.BalancesReducidos       => r.BalancesReducidos is null ? "-" : $"closings={r.BalancesReducidos.Count}",
        NosisReportSectionKeys.Mav                     => r.Mav is null ? "-" : $"instruments={r.Mav.PorInstrumento.Count}, currencies={r.Mav.PorMoneda.Count}, nullAmounts={r.Mav.PorInstrumento.Count(x => x.Monto is null) + r.Mav.PorMoneda.Count(x => x.Monto is null)}",
        NosisReportSectionKeys.AntecedentesFiscales    => r.AntecedentesFiscales is null ? "-" : $"regimes={r.AntecedentesFiscales.Count}",
        NosisReportSectionKeys.ComunicadosBcra         => r.ComunicadosBcra is null ? "-" : $"dates={r.ComunicadosBcra.Fechas.Count}",
        NosisReportSectionKeys.ComercioExterior        => r.ComercioExterior is null ? "-" : $"years={r.ComercioExterior.Anios.Count}",
        NosisReportSectionKeys.MarcasCantidad          => r.MarcasCantidad is null ? "-" : "count read",
        _ => "-",
    };

    private static int SummaryFields(NosisReportSummary s)
        => new object?[]
        {
            s.Score, s.SituacionBcra, s.EndeudamientoTotal, s.CompromisosMensuales, s.FacturacionEstimadaMin,
            s.FacturacionEstimadaMax, s.TieneChequesRechazados, s.ReferenciasComercialesCantidad, s.AportesImpagos12m,
            s.Consultas3m, s.Consultas12m, s.AntiguedadAnios, s.EmpleadosRango, s.ActividadPrincipal,
            s.CertificadoMiPyme, s.Arca,
        }.Count(v => v is not null);

    // ── Shapes ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, NosisReportHeadingKind[]> KindsOf = new(StringComparer.Ordinal)
    {
        [NosisReportSectionKeys.Summary]                 = [NosisReportHeadingKind.Profile, NosisReportHeadingKind.Arca, NosisReportHeadingKind.MiPyme, NosisReportHeadingKind.Resumen],
        [NosisReportSectionKeys.Evolucion24m]            = [NosisReportHeadingKind.Evolucion],
        [NosisReportSectionKeys.DeudaPorEntidad]         = [NosisReportHeadingKind.BureauCredito],
        [NosisReportSectionKeys.Atrasos]                 = [NosisReportHeadingKind.Atrasos],
        [NosisReportSectionKeys.AportesPatronales]       = [NosisReportHeadingKind.AportesPatronales],
        [NosisReportSectionKeys.ChequesRechazadosPorMes] = [NosisReportHeadingKind.ChequesRechazados],
        [NosisReportSectionKeys.CriteriosAceptacion]     = [NosisReportHeadingKind.CriteriosAceptacion],
        [NosisReportSectionKeys.Juicios]                 = [NosisReportHeadingKind.Juicios],
        [NosisReportSectionKeys.Sisa]                    = [NosisReportHeadingKind.Sisa],
        [NosisReportSectionKeys.Srt]                     = [NosisReportHeadingKind.Srt],
        [NosisReportSectionKeys.ReferenciasComerciales]  = [NosisReportHeadingKind.ReferenciasComerciales],
        [NosisReportSectionKeys.BalancesReducidos]       = [NosisReportHeadingKind.BalancesReducidos],
        [NosisReportSectionKeys.Mav]                     = [NosisReportHeadingKind.Mav],
        [NosisReportSectionKeys.AntecedentesFiscales]    = [NosisReportHeadingKind.AntecedentesFiscales],
        [NosisReportSectionKeys.ComunicadosBcra]         = [NosisReportHeadingKind.ComunicadosBcra],
        [NosisReportSectionKeys.ComercioExterior]        = [NosisReportHeadingKind.ComercioExterior, NosisReportHeadingKind.Importaciones, NosisReportHeadingKind.Exportaciones],
        [NosisReportSectionKeys.MarcasCantidad]          = [NosisReportHeadingKind.Marcas],
    };

    private void PrintShapes(NosisReportSplit split, NosisManagerReport report, bool all)
    {
        _output.WriteLine("    headings, in order:");
        foreach (var heading in split.Headings)
        {
            var name = heading.Known ? heading.Kind.ToString() : $"UNKNOWN \"{Mask(heading.Line.Text)}\"";
            _output.WriteLine($"      p{heading.Line.Page} {heading.Line.Style} {name}");
        }

        if (all)
            PrintLines("header block (preamble)", split.Preamble);

        foreach (var (key, status) in report.Sections)
        {
            if (!all && status != NosisReportSectionKeys.StatusUnreadable)
                continue;

            foreach (var kind in KindsOf[key])
            {
                if (split.Sections.TryGetValue(kind, out var section))
                    PrintLines($"{key} / {kind} (inThousands={section.InThousands}, numbers={section.NumberStyle})", section.Lines);
            }
        }
    }

    private void PrintLines(string title, IReadOnlyList<NosisReportLine> lines)
    {
        _output.WriteLine($"    shapes of {title}, first {MaxShapeLines} lines:");
        foreach (var line in lines.Take(MaxShapeLines))
        {
            _output.WriteLine(
                $"      p{line.Page} {line.Style} " +
                string.Join(' ', line.Words.Select(w => $"{Mask(w.Text)}@{w.Left.ToString("0", CultureInfo.InvariantCulture)}")));
        }
    }

    /// <summary>Letters → a/A, digits → 9; everything else (punctuation) as is.</summary>
    private static string Mask(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsDigit(c) ? '9' : char.IsLetter(c) ? (char.IsUpper(c) ? 'A' : 'a') : c);
        return builder.ToString();
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static string Encryption(byte[] bytes)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            if (!doc.IsEncrypted)
                return "none";

            var scanner = doc.Structure.TokenScanner;
            var token   = doc.Structure.Trailer.EncryptionToken;
            var encrypt = token is IndirectReferenceToken reference
                ? scanner.Get(reference.Data)?.Data as DictionaryToken
                : token as DictionaryToken;

            if (encrypt is null)
                return "encrypted";

            var v = encrypt.TryGet<NumericToken>(NameToken.Create("V"), scanner, out var vToken) ? vToken.Int.ToString(CultureInfo.InvariantCulture) : "?";
            var r = encrypt.TryGet<NumericToken>(NameToken.Create("R"), scanner, out var rToken) ? rToken.Int.ToString(CultureInfo.InvariantCulture) : "?";
            return $"V{v}/R{r}";
        }
        catch (Exception ex)
        {
            return $"open failed ({ex.GetType().Name})";
        }
    }

    private static void GuardOutsideRepository(string path, string variable)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            dir = dir.Parent;

        if (dir is null)
            return;

        var root       = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate  = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (candidate.StartsWith(root, comparison))
            throw new InvalidOperationException($"{variable} is inside the repository. Clients' credit reports must stay outside it.");
    }
}
