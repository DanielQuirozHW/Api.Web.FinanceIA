// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;
using System.Runtime.CompilerServices;
using Api.Web.FinanceIA.Models.Nosis;
using UglyToad.PdfPig;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <see cref="INosisManagerReportParser"/>: opens the PDF with PdfPig's plain <c>Open(bytes)</c> — the
/// NOSIS file is RC4-encrypted with an owner password only, which opens with the empty user password
/// like the CPCE fixtures — recognises the template by its title and footer, cuts the body into
/// sections at the fixed headings, and runs each section's parser on its own: one unreadable section
/// never fails the import, it is reported as "unreadable".
/// <para>
/// Never logs and never throws for a bad file: every failure is an outcome. Only cancellation escapes.
/// </para>
/// </summary>
public sealed class NosisManagerReportParser : INosisManagerReportParser
{
    /// <summary>The parser version stamped on every report.</summary>
    public const string ParserVersion = "nm-1.2";

    /// <summary>
    /// Largest report read, in pages. The sample with ~145 pages of rejected cheques has 174; the
    /// cheque list is what grows.
    /// </summary>
    public const int MaxPages = 1000;

    /// <inheritdoc />
    public NosisReportParseResult Parse(byte[] pdf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return NotRecognized(NosisReportNotRecognizedReason.Unopenable, 0);
        }

        using (document)
        {
            int pageCount;
            try
            {
                pageCount = NosisReportDocument.CountPages(document);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return NotRecognized(NosisReportNotRecognizedReason.Unopenable, 0);
            }

            if (pageCount > MaxPages)
                return new NosisReportParseResult { Outcome = NosisReportParseOutcome.TooManyPages, PageCount = pageCount };

            NosisReportDocument text;
            try
            {
                text = NosisReportDocument.Read(document, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return NotRecognized(NosisReportNotRecognizedReason.Unreadable, pageCount);
            }

            if (!text.FirstPageHasTitle || text.TitleCuit is null)
                return NotRecognized(NosisReportNotRecognizedReason.NoTitle, pageCount);
            if (!text.FirstPageHasFooter)
                return NotRecognized(NosisReportNotRecognizedReason.NoFooter, pageCount);
            if (text.ReportDate is null)
                return NotRecognized(NosisReportNotRecognizedReason.NoReportDate, pageCount);
            if (text.FooterCuits.Any(c => !string.Equals(c, text.TitleCuit, StringComparison.Ordinal)))
                return NotRecognized(NosisReportNotRecognizedReason.CuitDisagreement, pageCount);

            var report = Build(text.Lines, text.ReportDate.Value, ct);
            var (scrubbed, dropped) = NosisReportPrivacyGuard.Apply(report);

            return new NosisReportParseResult
            {
                Outcome           = NosisReportParseOutcome.Parsed,
                Report            = scrubbed,
                ReportDate        = text.ReportDate,
                SubjectCuitDigits = text.TitleCuit,
                PageCount         = pageCount,
                DroppedStrings    = dropped,
            };
        }
    }

    /// <summary>
    /// The body lines of each data section, for the LOCAL harness's masked shape dump only. Null when
    /// the file is not a recognisable report. Never used by the API.
    /// </summary>
    internal static NosisReportSplit? SectionsForDiagnostics(byte[] pdf, CancellationToken ct)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var text = NosisReportDocument.Read(document, ct);
            return text.FirstPageHasTitle ? NosisReportSectionSplitter.Split(text.Lines) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static NosisManagerReport Build(IReadOnlyList<NosisReportLine> lines, DateTime reportDate, CancellationToken ct)
    {
        var split = NosisReportSectionSplitter.Split(lines);
        var sections = split.Sections;
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);

        T? Run<T>(string key, NosisReportHeadingKind kind, Func<NosisReportSection, T> parse) where T : class
        {
            ct.ThrowIfCancellationRequested();

            if (!sections.TryGetValue(kind, out var section))
            {
                statuses[key] = NosisReportSectionKeys.StatusAbsent;
                return null;
            }

            try
            {
                var value = parse(section);
                statuses[key] = NosisReportSectionKeys.StatusOk;
                return value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A layout this parser does not understand — or any fault reading it — costs this
                // section only.
                statuses[key] = NosisReportSectionKeys.StatusUnreadable;
                return null;
            }
        }

        NosisReportSummary? summary;
        try
        {
            (summary, statuses[NosisReportSectionKeys.Summary]) = SummarySectionParser.Parse(split, reportDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary = null;
            statuses[NosisReportSectionKeys.Summary] = NosisReportSectionKeys.StatusUnreadable;
        }

        var report = new NosisManagerReport
        {
            ReportDate    = reportDate.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ParserVersion = ParserVersion,
            Sections      = statuses,
            Summary       = summary,
            Evolucion24m            = Run(NosisReportSectionKeys.Evolucion24m, NosisReportHeadingKind.Evolucion, s => EvolucionSectionParser.Parse(s, reportDate)),
            DeudaPorEntidad         = Run(NosisReportSectionKeys.DeudaPorEntidad, NosisReportHeadingKind.BureauCredito, s => DeudaPorEntidadSectionParser.Parse(s, reportDate)),
            Atrasos                 = Run(NosisReportSectionKeys.Atrasos, NosisReportHeadingKind.Atrasos, s => AtrasosSectionParser.Parse(s, reportDate)),
            AportesPatronales       = Run(NosisReportSectionKeys.AportesPatronales, NosisReportHeadingKind.AportesPatronales, s => AportesPatronalesSectionParser.Parse(s, reportDate)),
            ChequesRechazadosPorMes = Run(NosisReportSectionKeys.ChequesRechazadosPorMes, NosisReportHeadingKind.ChequesRechazados, ChequesRechazadosSectionParser.Parse),
            CriteriosAceptacion     = Run(NosisReportSectionKeys.CriteriosAceptacion, NosisReportHeadingKind.CriteriosAceptacion, CriteriosAceptacionSectionParser.Parse),
            Juicios                 = Run(NosisReportSectionKeys.Juicios, NosisReportHeadingKind.Juicios, JuiciosSectionParser.Parse),
            Sisa                    = Run(NosisReportSectionKeys.Sisa, NosisReportHeadingKind.Sisa, SisaSectionParser.Parse),
            Srt                     = Run(NosisReportSectionKeys.Srt, NosisReportHeadingKind.Srt, SrtSectionParser.Parse),
            ReferenciasComerciales  = Run(NosisReportSectionKeys.ReferenciasComerciales, NosisReportHeadingKind.ReferenciasComerciales, ReferenciasComercialesSectionParser.Parse),
            BalancesReducidos       = Run(NosisReportSectionKeys.BalancesReducidos, NosisReportHeadingKind.BalancesReducidos, BalancesReducidosSectionParser.Parse),
            Mav                     = Run(NosisReportSectionKeys.Mav, NosisReportHeadingKind.Mav, MavSectionParser.Parse),
            AntecedentesFiscales    = Run(NosisReportSectionKeys.AntecedentesFiscales, NosisReportHeadingKind.AntecedentesFiscales, AntecedentesFiscalesSectionParser.Parse),
            ComunicadosBcra         = Run(NosisReportSectionKeys.ComunicadosBcra, NosisReportHeadingKind.ComunicadosBcra, ComunicadosBcraSectionParser.Parse),
            MarcasCantidad          = Run(NosisReportSectionKeys.MarcasCantidad, NosisReportHeadingKind.Marcas, s => new StrongBox<int>(MarcasSectionParser.Parse(s)))?.Value,
        };

        // comercioExterior reads three headings, like the summary reads four.
        NosisReportComercioExterior? comercio;
        try
        {
            (comercio, statuses[NosisReportSectionKeys.ComercioExterior]) = ComercioExteriorSectionParser.Parse(sections);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            comercio = null;
            statuses[NosisReportSectionKeys.ComercioExterior] = NosisReportSectionKeys.StatusUnreadable;
        }

        report = report with { ComercioExterior = comercio };

        // Contract order, every key present.
        var ordered = NosisReportSectionKeys.All.ToDictionary(k => k, k => statuses[k], StringComparer.Ordinal);
        return report with { Sections = ordered };
    }

    private static NosisReportParseResult NotRecognized(NosisReportNotRecognizedReason reason, int pages)
        => new() { Outcome = NosisReportParseOutcome.NotRecognized, Reason = reason, PageCount = pages };
}
