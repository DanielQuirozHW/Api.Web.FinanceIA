// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using System.Globalization;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Pdf;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using iText.Kernel.Colors;
using iText.Kernel.Events;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging;
using ITextDocument = iText.Layout.Document;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The persona humana report (M10 Phase 4b): the frozen evaluation of the solicitante, printed in the order
/// the Portal shows it, his fiadores with their aggregate counter-guarantee coverage, then the decision and the
/// credit line it originated. It is a separate document from
/// the balance report — its own section keys, its own header — sharing only the brand pieces: the fonts
/// (<see cref="ResolveFonts"/>, one bundle per export), the running footer, the section headings, the
/// palette and the closing footer.
/// <para>
/// Nothing here computes: every figure is the engine's frozen value, every word comes from
/// <see cref="PersonLabels"/>, and the number formats are a port of the Portal's person-format.ts (es-AR,
/// America/Argentina/Buenos_Aires). Unlike the balance header, this one names the person and the CUIT, and
/// persona_fiadores names each fiador with his — it is the analyst's own report of that operation. The logs
/// keep ids only.
/// </para>
/// </summary>
public sealed partial class PdfExportService
{
    private const string Dash = "—";

    // The engine's literal for an input the legajo cannot supply (person-format.ts NOT_IN_LEGAJO).
    private const string NotInLegajo = "no disponible en el legajo";

    private const string NoScoreNote = "Sin puntaje: Financia propone y una persona decide.";

    private const string PendingDecision = "Pendiente de decisión";

    private static readonly Regex InvariantNumber = new(@"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant);

    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    // person-format.ts INPUT_KINDS: how each engine input reads. A key missing here is text, or an es-AR
    // plain number when it parses as one.
    private enum InputKind { Money, Fraction, Multiple, Months, MonthOfYear, Integer, Date, Code }

    private static readonly IReadOnlyDictionary<string, InputKind> InputKinds = BuildInputKinds();

    private static IReadOnlyDictionary<string, InputKind> BuildInputKinds()
    {
        var kinds = new Dictionary<string, InputKind>(StringComparer.Ordinal);
        void Add(InputKind kind, params string[] keys)
        {
            foreach (var key in keys) kinds[key] = kind;
        }

        Add(InputKind.Money,
            "activo", "activoFiscal", "activoMercado", "bienesActividad", "bienesSinValorMercado", "consumoAnual",
            "consumoDeclaradoMensual", "consumoMensual", "consumoMensualPiso", "cuota", "cuotasVigentesEstimadas",
            "flujoMensual", "ingresoMensual", "montoConsumido", "montoTotalImpago", "pasivo", "pasivoMercado",
            "patrimonioNeto", "renta3ra", "resultadoActividad", "servicioMensual", "topeAnual", "ventas12Meses",
            "ventas12MesesNominal", "ventas12MesesReal", "ventasAcreedor", "ventasFisco", "ventasPrevios12Nominal",
            "ventasPrevios12Real", "ventasUltimos12Nominal", "ventasUltimos12Real", "cuotasVigentes", "consumoPisoBase");
        Add(InputKind.Fraction,
            "corteAlto", "corteBajo", "corteMedio", "pesoSobreActivo", "tolerancia", "umbralAlerta", "variacionNominal",
            "cuotaEstimadaPctSaldo");
        Add(InputKind.Multiple, "dscrRojo", "dscrVerde");
        Add(InputKind.Months,
            "meses", "mesesDisponibles", "mesesInformados", "minimoMeses", "plazoEstimadoDeudaMeses", "rechazoMeses",
            "ventanaMeses", "ventanaPagadosMeses");
        Add(InputKind.MonthOfYear, "alertaDesdeMes", "alertaHastaMes");
        Add(InputKind.Integer,
            "impagosComputados", "totalImpagos", "totalRechazados", "periodosCompar", "periodosFueraTol",
            "defectosFormales", "peorSituacionVentana", "situacionActual", "umbralRechazo");
        Add(InputKind.Date, "desde", "hasta", "inicio", "fechaUltimoRechazo", "tablaVigenteDesde", "fechaConsulta", "consultadoEl");
        Add(InputKind.Code, "periodo", "periodoConsultado", "mesReferenciaIpc", "consumoMesBase");
        return kinds;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<byte[]> GeneratePersonAsync(
        PersonPdfReport       report,
        IReadOnlyList<string> sections,
        CancellationToken     ct,
        PdfTheme              theme = PdfTheme.Light)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(sections);

        IReadOnlyList<string> activeSections = sections.Count == 0 ? PersonaSections : sections;

        byte[] bytes = BuildPersonPdf(report, activeSections, theme);
        return Task.FromResult(bytes);
    }

    // ── PDF construction ──────────────────────────────────────────────────────

    private byte[] BuildPersonPdf(PersonPdfReport report, IReadOnlyList<string> sections, PdfTheme theme)
    {
        // One palette for the whole render, page furniture and doc.Close included (see BuildPdf).
        using PdfRenderScope.Scope scope = PdfRenderScope.Enter(PdfPalette.For(theme, report.Branding.Accent));

        using var ms     = new MemoryStream();
        PdfWriter writer = new PdfWriter(ms);
        writer.SetCloseStream(false);
        PdfDocument pdf  = new PdfDocument(writer);
        var doc          = new ITextDocument(pdf, PageSize.A4);
        ApplyPageSetup(doc);
        ApplyThemeDefaults(doc);

        Fonts fonts = ResolveFonts();

        string  shortId    = report.ResultId.ToString()[..8].ToUpperInvariant();
        string  exportedAt = FormatExportTimestamp();
        string? personName = string.IsNullOrWhiteSpace(report.EntityName) ? null : report.EntityName.Trim();
        PersonEvaluationResponse? evaluation = report.Evaluation;

        // The shared pieces: document information, the header band / running header and the running footer.
        ApplyDocumentInfo(
            pdf,
            personName is null ? ReportTitlePerson : $"{ReportTitlePerson} — {personName}",
            ReportTitlePerson,
            report.TenantDisplayName);

        var runningFooter = new PageFurnitureHandler(
            pdf, fonts, ReportTitlePerson, personName, shortId, exportedAt, theme, ClientBrandOf(report.Branding));
        pdf.AddEventHandler(PdfDocumentEvent.END_PAGE, runningFooter);

        // Room for the full-bleed band on page 1.
        doc.Add(new Div().SetHeight(Page1Spacer).SetMargin(0));

        AddDocumentHeader(doc, fonts, "Informe de análisis · Persona humana",
        [
            ("Nombre", string.IsNullOrWhiteSpace(report.EntityName) ? Dash : report.EntityName, true),
            ("CUIT", FormatCuit(report.EntityCuit), false),
            ("Fecha de evaluación", evaluation is null ? Dash : ArDate(evaluation.CreatedAt), false),
            ("Versión del motor", string.IsNullOrWhiteSpace(evaluation?.EvaluationVersion) ? Dash : evaluation.EvaluationVersion, false),
            ("ID análisis", shortId, false)
        ]);

        // The evaluation sections need the frozen evaluation; the decision does not. Without one, the
        // evaluation sections say so ONCE and the decision still prints.
        bool evaluationSectionRequested = sections.Any(k => k != "persona_decision" && PersonaSections.Contains(k));
        if (evaluation is null && evaluationSectionRequested)
        {
            doc.Add(new Paragraph("El análisis no trae la evaluación de la persona.")
                .SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorMuted).SetMarginBottom(8));
        }

        // Fixed order — the Portal's: the suggestion and its summary, the evaluation sections (the fiadores
        // right after the solicitante's solvency), then the sources and the decision. The caller's order does
        // not change it.
        if (evaluation is not null)
        {
            if (sections.Contains("persona_resumen"))      AddPersonSummarySection(doc, fonts, evaluation);
            if (sections.Contains("persona_elegibilidad")) AddPersonEligibilitySection(doc, fonts, evaluation, report.EntityCuit);
            if (sections.Contains("persona_filtros"))      AddPersonFiltersSection(doc, fonts, evaluation);
            if (sections.Contains("persona_capacidad"))    AddPersonCapacitySection(doc, fonts, evaluation, report.Analysis);
            if (sections.Contains("persona_solvencia"))    AddPersonSolvencySection(doc, fonts, evaluation);
            if (sections.Contains("persona_fiadores"))     AddPersonFiadoresSection(doc, fonts, evaluation, report.ResultId);
            if (sections.Contains("persona_coherencia"))   AddPersonCoherenceSection(doc, fonts, evaluation);
            if (sections.Contains("persona_cobertura"))    AddPersonCoverageSection(doc, fonts, evaluation);
            if (sections.Contains("persona_fuentes"))      AddPersonSourcesSection(doc, fonts, evaluation);
        }

        if (sections.Contains("persona_decision"))
            AddPersonDecisionSection(doc, fonts, report);

        // The closing block is part of every persona report, not a section key.
        AddFooterSection(doc, fonts, shortId, exportedAt);

        foreach (string key in sections)
        {
            if (PersonaSections.Contains(key)) continue;

            _logger.LogWarning(
                "PDF export: section key '{SectionKey}' reached the persona report but has no renderer there for analysis {AnalysisId}.",
                key, report.ResultId);
        }

        runningFooter.WriteTotalPages(pdf);

        doc.Close();
        ms.Position = 0;
        return ms.ToArray();
    }

    // ── 0. Resumen ────────────────────────────────────────────────────────────

    /// <summary>One group of the summary's filter counts.</summary>
    internal sealed record PersonFilterCount(string Label, int Count);

    // The summary order and the chip each result renders as (person-labels filterOutcomeChip). "Alerta fuerte"
    // is an Alerta with severity Fuerte and is NOT also counted as an Alerta; an outcome this build does not
    // know counts as Sin datos, exactly as the Portal's summary counts it.
    private static readonly string[] SummaryOutcomeGroups = ["Rechazo", "Alerta fuerte", "Alerta", "Ok", "Sin datos"];

    /// <summary>The filter counts of the summary, all five groups in order, zeros included.</summary>
    internal static IReadOnlyList<PersonFilterCount> CountFilterOutcomes(IReadOnlyList<PersonFilterResult> filters)
    {
        var labels = filters
            .Select(f => PersonLabels.FilterOutcome(f.Outcome, f.Severity))
            .Select(label => SummaryOutcomeGroups.Contains(label) ? label : PersonLabels.SinDatos)
            .ToList();

        return SummaryOutcomeGroups
            .Select(group => new PersonFilterCount(group, labels.Count(l => l == group)))
            .ToList();
    }

    private static readonly string[] SummaryIndicatorIds = ["cobertura_servicio_deuda", "cuota_ingreso", "solvencia_pn"];

    private static void AddPersonSummarySection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation)
    {
        AddSectionHeading(doc, fonts, "Resumen del resultado");

        bool isRejection = evaluation.Suggestion == PersonSuggestions.RechazoSugerido;

        // The suggestion banner: the only conclusion the engine draws, and the note that it is not a score.
        Table banner = new Table(UnitValue.CreatePercentArray([100f])).UseAllAvailableWidth().SetMarginBottom(10);
        banner.AddCell(new Cell()
            .SetBorder(new SolidBorder(isRejection ? Danger : SurfaceBorder, 1f))
            .SetBackgroundColor(isRejection ? DangerSoft : Surface)
            .SetPadding(10)
            .Add(new Paragraph(PersonLabels.Suggestion(evaluation.Suggestion))
                .SetFont(fonts.HeadingBold).SetFontSize(12).SetFontColor(isRejection ? Danger : Label).SetMargin(0))
            .Add(new Paragraph(NoScoreNote)
                .SetFont(fonts.Body).SetFontSize(9).SetFontColor(LabelSecondary).SetMarginTop(3).SetMarginBottom(0)));
        doc.Add(banner);

        // Filters: one cell per result, zeros shown muted so the reader sees "0 rechazo" said out loud.
        AddSubHeading(doc, fonts, "Filtros");
        IReadOnlyList<PersonFilterCount> counts = CountFilterOutcomes(evaluation.Filters);
        Table countTable = new Table(UnitValue.CreatePercentArray(counts.Count)).UseAllAvailableWidth().SetMarginBottom(8);
        foreach (PersonFilterCount count in counts)
        {
            DeviceRgb color = count.Count == 0 ? LabelTertiary : OutcomeColor(count.Label);
            countTable.AddCell(new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(count.Count == 0 ? SurfaceBorder : color, 1.5f))
                .SetPadding(5)
                .Add(new Paragraph()
                    .SetMargin(0)
                    .Add(new Text(count.Count.ToString(EsAr)).SetFont(fonts.Mono).SetFontSize(11).SetFontColor(color))
                    .Add(new Text(" " + count.Label.ToLower(EsAr)).SetFont(fonts.BodyMedium).SetFontSize(9).SetFontColor(color))));
        }
        doc.Add(countTable);

        // The three key indicators.
        AddSubHeading(doc, fonts, "Indicadores clave");
        Table indicators = PersonTable(fonts, ["Indicador", "Valor", "Banda"], [46f, 24f, 30f], rightAligned: [1]);
        bool alt = false;
        foreach (string id in SummaryIndicatorIds)
        {
            PersonIndicatorResult? indicator = evaluation.Indicators.FirstOrDefault(i => i.Id == id);
            DeviceRgb? bg = alt ? Surface : null;
            indicators.AddCell(BodyCell(PersonLabels.Indicator(id), fonts.Body, 8.5f, bg));
            indicators.AddCell(BodyCell(indicator is not null && HasIndicatorValue(indicator) ? IndicatorValue(indicator) : Dash,
                fonts.Mono, 8.5f, bg, TextAlignment.RIGHT));
            indicators.AddCell(ChipCell(fonts, IndicatorChip(indicator), bg,
                indicator is not null && indicator.Provisional && HasIndicatorValue(indicator) ? PersonLabels.ProvisionalLabel : null));
            alt = !alt;
        }
        doc.Add(indicators);

        // Documentary coverage, the count only — the table is in its own section.
        if (evaluation.Coverage is { } coverage)
        {
            int stale = coverage.Items.Count(i => i.Present && i.IsStale);
            string text = $"{coverage.RequiredPresent.ToString(EsAr)} de {coverage.RequiredTotal.ToString(EsAr)} documentos obligatorios"
                        + (stale > 0 ? $" · {stale.ToString(EsAr)} {(stale == 1 ? "desactualizado" : "desactualizados")}" : string.Empty);
            AddSubHeading(doc, fonts, "Cobertura documental");
            doc.Add(new Paragraph(text).SetFont(fonts.Body).SetFontSize(9).SetFontColor(Label).SetMarginBottom(4));
        }
    }

    // ── 1. Elegibilidad e identidad ───────────────────────────────────────────

    private static void AddPersonEligibilitySection(
        ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation, string entityCuit)
    {
        AddSectionHeading(doc, fonts, "Elegibilidad e identidad");

        PersonFilterResult? certificado = FilterById(evaluation, "certificado_mipyme");
        PersonFilterResult? monotributo = FilterById(evaluation, "monotributo_tope");
        PersonFilterResult? cuit        = FilterById(evaluation, "arca_cuit_activo");

        string? desde = Input(certificado?.Inputs, "desde");
        string? hasta = Input(certificado?.Inputs, "hasta");
        string vigencia = desde is null && hasta is null
            ? PersonLabels.SinDatos
            : $"{(desde is null ? Dash : FormatDateInput(desde))} al {(hasta is null ? Dash : FormatDateInput(hasta))}";

        Table table = KeyValueTable();
        AddKeyValue(table, fonts, "Certificado MiPyME", FilterResultText(certificado), FilterColor(certificado));
        AddKeyValue(table, fonts, "Vigencia del certificado", vigencia);
        if (CertificateAlertWindow(certificado?.Inputs) is { } window)
            AddKeyValue(table, fonts, "Ventana de alerta", window);
        AddKeyValue(table, fonts, "Categoría MiPyME", Input(certificado?.Inputs, "categoria") ?? PersonLabels.SinDatos);
        AddKeyValue(table, fonts, "CUIT", FormatCuit(entityCuit));

        string cuitStatus = FilterResultText(cuit);
        if (Input(cuit?.Inputs, "estadoClave") is { } estado)
            cuitStatus += $" · {PersonLabels.Input("estadoClave")}: {estado}";
        if (Input(cuit?.Inputs, "consultadoEl") is { } consultado)
            cuitStatus += $" · {PersonLabels.Input("consultadoEl").ToLower(EsAr)} {FormatDateInput(consultado)}";
        AddKeyValue(table, fonts, "Estado de la CUIT en ARCA", cuitStatus, FilterColor(cuit));

        AddKeyValue(table, fonts, "Régimen fiscal",
            PersonLabels.FiscalRegime(evaluation.Coverage?.FiscalRegime, Input(monotributo?.Inputs, "categoria")));
        doc.Add(table);

        if (!string.IsNullOrWhiteSpace(certificado?.Evidence))
            doc.Add(MutedParagraph(fonts, certificado.Evidence));
        if (!string.IsNullOrWhiteSpace(cuit?.Evidence))
            doc.Add(MutedParagraph(fonts, cuit.Evidence));
    }

    // ── 2. Filtros previos ────────────────────────────────────────────────────

    /// <summary>
    /// The consultation date printed for a filter: its own <c>fechaConsulta</c> input when it has one (the
    /// date the registry was queried), otherwise the UpdatedAt of the legajo snapshot of its source (the
    /// source's BCRA / ARCA / LUFE prefix, matched to the coverage snapshot, then to the frozen source stamp).
    /// Null for a filter that reads the legajo's documents rather than a registry.
    /// </summary>
    internal static string? FilterConsultedAt(PersonFilterResult filter, PersonEvaluationResponse evaluation)
        => FilterConsultedAt(filter, evaluation.Coverage?.Snapshots, evaluation.Sources);

    // The same date for any participant: the solicitante has coverage snapshots and sources, a fiador only
    // the sources frozen on his row.
    private static string? FilterConsultedAt(
        PersonFilterResult filter, IReadOnlyList<PersonCoverageSnapshot>? snapshots, PersonEvaluationSources? sources)
    {
        if (Input(filter.Inputs, "fechaConsulta") is { } fecha)
            return FormatDateInput(fecha);

        string source = filter.Source?.Trim().ToUpperInvariant() ?? string.Empty;
        string? registry = source.StartsWith("BCRA", StringComparison.Ordinal) ? "BCRA"
                         : source.StartsWith("ARCA", StringComparison.Ordinal) ? "ARCA"
                         : source.StartsWith("LUFE", StringComparison.Ordinal) ? "LUFE"
                         : null;
        if (registry is null)
            return null;

        DateTime? at = snapshots?
                           .FirstOrDefault(s => string.Equals(s.Source?.Trim(), registry, StringComparison.OrdinalIgnoreCase))
                           ?.UpdatedAt
                       ?? SourceStamp(sources, registry);

        return at is { } d ? ArDate(d) : null;
    }

    private static void AddPersonFiltersSection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation)
    {
        AddSectionHeading(doc, fonts, "Filtros previos");

        if (evaluation.Filters.Count == 0)
        {
            doc.Add(MutedParagraph(fonts, "El análisis no trae filtros."));
            return;
        }

        doc.Add(FiltersTable(fonts, evaluation.Filters, f => FilterConsultedAt(f, evaluation)));

        doc.Add(MutedParagraph(fonts,
            "Fecha de consulta: la que el filtro informa cuando la trae; si no, la de la última actualización " +
            "del informe del legajo de esa fuente. Verificado cuando todo surge de declaraciones presentadas " +
            "ante ARCA o de registros; Declarado cuando incluye lo que la persona declara al acreedor."));
    }

    // The filters table of a participant — the solicitante's persona_filtros and each fiador's block.
    private static Table FiltersTable(
        Fonts fonts, IReadOnlyList<PersonFilterResult> filters, Func<PersonFilterResult, string?> consultedAt)
    {
        Table table = PersonTable(fonts,
            ["Filtro", "Resultado", "Verificado / Declarado", "Dato / evidencia", "Fuente", "Fecha de consulta"],
            [17f, 11f, 11f, 32f, 16f, 13f], rightAligned: [5]);

        bool alt = false;
        foreach (PersonFilterResult filter in filters)
        {
            DeviceRgb? bg = alt ? Surface : null;
            table.AddCell(BodyCell(PersonLabels.Filter(filter.Id), fonts.BodyMedium, 7.5f, bg));
            table.AddCell(ChipCell(fonts, (PersonLabels.FilterOutcome(filter.Outcome, filter.Severity), FilterColor(filter)), bg));
            table.AddCell(BodyCell(FilterEvidenceKindText(filter), fonts.Body, 7.5f, bg));
            table.AddCell(BodyCell(string.IsNullOrWhiteSpace(filter.Evidence) ? Dash : filter.Evidence, fonts.Body, 7.5f, bg));
            table.AddCell(BodyCell(string.IsNullOrWhiteSpace(filter.Source) ? Dash : filter.Source, fonts.Body, 7.5f, bg));
            table.AddCell(BodyCell(consultedAt(filter) ?? Dash, fonts.Mono, 7.5f, bg, TextAlignment.RIGHT));
            alt = !alt;
        }

        return table;
    }

    // ── 3. Capacidad de pago ──────────────────────────────────────────────────

    private static void AddPersonCapacitySection(
        ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation, Analysis analysis)
    {
        AddSectionHeading(doc, fonts, "Capacidad de pago");

        // The operation the capacity is measured against.
        Table operation = KeyValueTable();
        AddKeyValue(operation, fonts, "Monto solicitado", Amount(analysis.RequestedAmount ?? evaluation.AmountAtCharge, evaluation.Currency));
        AddKeyValue(operation, fonts, "Moneda", string.IsNullOrWhiteSpace(evaluation.Currency) ? Dash : evaluation.Currency);
        AddKeyValue(operation, fonts, PersonLabels.Input("cuota"),
            evaluation.InstallmentAmount is { } cuota ? Amount(cuota, evaluation.Currency) : "No informada");
        doc.Add(operation);

        PersonIndicatorResult? cobertura    = IndicatorById(evaluation, "cobertura_servicio_deuda");
        PersonIndicatorResult? cuotaIngreso = IndicatorById(evaluation, "cuota_ingreso");
        PersonIndicatorResult? flujo        = IndicatorById(evaluation, "flujo_actividad");
        string? consumoBase = Input(flujo?.Inputs, "consumoBase");

        // Cobertura del servicio de deuda — a multiple of the service, with a red and a green cut.
        string? dscrCuts = null;
        decimal? dscrRojo = InputNumber(cobertura?.Inputs, "dscrRojo"), dscrVerde = InputNumber(cobertura?.Inputs, "dscrVerde");
        if (dscrRojo is not null || dscrVerde is not null)
        {
            dscrCuts = string.Join(" · ", new[]
            {
                dscrRojo is { } r ? $"Rojo por debajo de {Cut(r)}" : null,
                dscrVerde is { } v ? $"Verde desde {Cut(v)}" : null
            }.Where(x => x is not null));
        }

        string? dscrFormula = cobertura is null ? null
            : $"{Term(cobertura.Inputs, "flujoMensual", "Flujo mensual")} ÷ servicio de deuda ("
              + $"{Term(cobertura.Inputs, "cuota", "cuota")} + {Term(cobertura.Inputs, "cuotasVigentesEstimadas", "cuotas vigentes", Estimated(cobertura))})"
              + (consumoBase is not null ? $" · consumo según {consumoBase}" : string.Empty)
              + (IsNotInLegajo(Input(cobertura.Inputs, "impuestos")) ? " · impuestos no disponibles en el legajo" : string.Empty);

        AddIndicatorBlock(doc, fonts, cobertura, "cobertura_servicio_deuda", dscrCuts, dscrFormula, ["dscrRojo", "dscrVerde"]);

        // Cuota sobre ingreso — a share, with three cuts.
        decimal[] cortes = new[] { "corteBajo", "corteMedio", "corteAlto" }
            .Select(k => InputNumber(cuotaIngreso?.Inputs, k))
            .OfType<decimal>()
            .ToArray();
        string? cuotaCuts = cortes.Length == 0 ? null : "Cortes: " + string.Join(", ", cortes.Select(Fraction));

        string? cuotaFormula = cuotaIngreso is null ? null
            : $"({Term(cuotaIngreso.Inputs, "cuota", "Cuota")} + {Term(cuotaIngreso.Inputs, "cuotasVigentesEstimadas", "cuotas vigentes", Estimated(cuotaIngreso))})"
              + $" ÷ {Term(cuotaIngreso.Inputs, "ingresoMensual", "ingreso mensual")}"
              + (Input(cuotaIngreso.Inputs, "baseIngreso") is { } baseIngreso ? $" · base: {baseIngreso}" : string.Empty);

        AddIndicatorBlock(doc, fonts, cuotaIngreso, "cuota_ingreso", cuotaCuts, cuotaFormula, ["corteBajo", "corteMedio", "corteAlto"]);

        // Flujo de la actividad — the consumption it deducts, and whether the floor applied.
        string? flujoFormula = flujo is null || consumoBase is null ? null
            : $"Consumo: {Term(flujo.Inputs, "consumoMensual", null)} por mes ({consumoBase})"
              + (InputFlag(flujo.Inputs, "pisoAplicado") ? " · se aplicó el piso de consumo" : string.Empty);

        AddIndicatorBlock(doc, fonts, flujo, "flujo_actividad", null, flujoFormula, []);

        // Exactly one of the two exists: the category cap in monotributo, the declared consumption outside it.
        PersonFilterResult? tope = FilterById(evaluation, "monotributo_tope");
        AddFilterBlock(doc, fonts, tope ?? FilterById(evaluation, "consumo_declarado"), tope is not null ? "monotributo_tope" : "consumo_declarado");
    }

    // ── 4. Solvencia ──────────────────────────────────────────────────────────

    private static void AddPersonSolvencySection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation)
    {
        AddSectionHeading(doc, fonts, "Solvencia");

        AddIndicatorBlock(doc, fonts, IndicatorById(evaluation, "solvencia_pn"), "solvencia_pn", null, null, []);
        AddIndicatorBlock(doc, fonts, IndicatorById(evaluation, "endeudamiento"), "endeudamiento", null, null, []);

        PersonIndicatorResult? operativo = IndicatorById(evaluation, "patrimonio_operativo");
        string? formula = InputNumber(operativo?.Inputs, "umbralAlerta") is { } umbral
            ? $"Bienes de la actividad sobre el patrimonio neto · alerta desde {Fraction(umbral)}"
            : null;
        AddIndicatorBlock(doc, fonts, operativo, "patrimonio_operativo", null, formula, []);
    }

    // ── 4b. Fiadores y contragarantía ─────────────────────────────────────────

    private const string NoFiadores = "La operación no tiene fiadores evaluados.";

    private const string UnreadableFiador = "No se pudo leer la evaluación de este fiador.";

    private static readonly string[] FiadorIndicatorIds = ["cobertura_patrimonial_fiador", "cuota_ingreso_estres"];

    /// <summary>
    /// The fiadores of the operation, every figure as the detail returns it (<see cref="PersonEvaluationResponse.Fiadores"/>
    /// and <see cref="PersonEvaluationResponse.CounterGuaranteeCoverage"/>, the one reader): the aggregate coverage
    /// first, then one block per fiador. A fiador that cannot be read or drawn prints one line and the rest of the
    /// report goes on; the log names ids only, never the fiador's name or CUIT.
    /// </summary>
    private void AddPersonFiadoresSection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation, Guid resultId)
    {
        AddSectionHeading(doc, fonts, "Fiadores y contragarantía");

        IReadOnlyList<PersonFiadorEvaluationResponse> fiadores = evaluation.Fiadores ?? [];
        if (fiadores.Count == 0)
        {
            doc.Add(new Paragraph(NoFiadores).SetFont(fonts.Body).SetFontSize(9).SetFontColor(LabelSecondary).SetMarginBottom(6));
            return;
        }

        CounterGuaranteeCoverage? coverage = evaluation.CounterGuaranteeCoverage;
        if (coverage is not null)
            AddCounterGuaranteeHeadline(doc, fonts, coverage);

        foreach (PersonFiadorEvaluationResponse fiador in fiadores)
        {
            CounterGuaranteeContribution? contribution = coverage?.Contributions
                .FirstOrDefault(c => c.ParticipantId == fiador.ParticipantId);

            Div block;
            if (IsUnreadable(fiador))
            {
                _logger.LogWarning(
                    "PDF export: fiador participant {ParticipantId} (fiador {FiadorId}) of result {AnalysisId} has an unreadable evaluation; printed as such.",
                    fiador.ParticipantId, fiador.FiadorId, resultId);
                block = UnreadableFiadorBlock(fonts, fiador);
            }
            else
            {
                try
                {
                    block = FiadorBlock(fonts, fiador, contribution);
                }
                catch (Exception ex)
                {
                    // Nothing of the failed block reached the document: it is added only once complete.
                    _logger.LogWarning(ex,
                        "PDF export: fiador participant {ParticipantId} (fiador {FiadorId}) of result {AnalysisId} could not be drawn; printed as unreadable.",
                        fiador.ParticipantId, fiador.FiadorId, resultId);
                    block = UnreadableFiadorBlock(fonts, fiador);
                }
            }

            doc.Add(block);
        }
    }

    /// <summary>
    /// The aggregate coverage, as lines: the ratio against the minimum (or why there is none), the executable
    /// total against the requested amount, and what the fiadores with a suggested rejection would add.
    /// </summary>
    internal static IReadOnlyList<(string Label, string Value)> CounterGuaranteeHeadline(CounterGuaranteeCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        var rows = new List<(string Label, string Value)>();
        string minimum = $"mínimo {coverage.MinPct.ToString(EsAr)} %";

        if (coverage.Ratio is { } ratio)
        {
            rows.Add(("Cobertura", $"{(ratio * 100m).ToString("0.#", EsAr)} % del monto solicitado · {minimum}"));
            string verdict = coverage.MeetsMin ? "Cumple el mínimo" : "No cumple el mínimo";
            rows.Add(("Resultado", PersonLabels.Band(coverage.Band) is { } band ? $"{verdict} · {band}" : verdict));
        }
        else if (!string.Equals(coverage.Currency?.Trim(), "ARS", StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(("Cobertura",
                $"La contragarantía no se calcula: la operación es en {coverage.Currency} y no hay conversión de moneda."));
        }
        else
        {
            rows.Add(("Cobertura", $"No calculable: no hay monto solicitado ({minimum})."));
        }

        rows.Add(("Total ejecutable", Amount(coverage.ExecutableTotal, "ARS")));
        rows.Add(("Monto solicitado", Amount(coverage.RequestedAmount, coverage.Currency)));
        rows.Add(("Fiadores evaluados", coverage.FiadoresCount.ToString(EsAr)));

        if (coverage.FlaggedFiadoresCount > 0)
        {
            int n = coverage.FlaggedFiadoresCount;
            decimal extra = coverage.ExecutableTotalIncludingFlagged - coverage.ExecutableTotal;
            rows.Add(("Rechazo sugerido", n == 1
                ? $"1 fiador con rechazo sugerido fuera del total (sumaría {Amount(extra, "ARS")})"
                : $"{n.ToString(EsAr)} fiadores con rechazo sugerido fuera del total (sumarían {Amount(extra, "ARS")})"));
        }

        return rows;
    }

    private static void AddCounterGuaranteeHeadline(ITextDocument doc, Fonts fonts, CounterGuaranteeCoverage coverage)
    {
        AddSubHeading(doc, fonts, "Cobertura de contragarantías");
        Table table = KeyValueTable();
        foreach ((string label, string value) in CounterGuaranteeHeadline(coverage))
        {
            DeviceRgb? color = label switch
            {
                "Resultado"        => coverage.MeetsMin ? Success : Danger,
                "Rechazo sugerido" => Orange,
                "Cobertura" when coverage.Ratio is null => Warning,
                _                  => null
            };
            AddKeyValue(table, fonts, label, value, color);
        }
        doc.Add(table);
    }

    /// <summary>
    /// A fiador row whose frozen JSON did not read back: the reader turns an unreadable column into a null
    /// coverage or an empty list, and a real evaluation always has its coverage, five filters and two indicators.
    /// </summary>
    internal static bool IsUnreadable(PersonFiadorEvaluationResponse fiador)
        => fiador.Coverage is null || fiador.Filters.Count == 0 || fiador.Indicators.Count == 0;

    private static Div UnreadableFiadorBlock(Fonts fonts, PersonFiadorEvaluationResponse fiador)
        => KeptTogether().SetMarginBottom(10)
            .Add(FiadorTitle(fonts, fiador))
            .Add(new Paragraph(UnreadableFiador)
                .SetFont(fonts.BodyMedium).SetFontSize(9).SetFontColor(Danger).SetMarginTop(2).SetMarginBottom(0));

    private static Paragraph FiadorTitle(Fonts fonts, PersonFiadorEvaluationResponse fiador)
        => new Paragraph()
            .SetMarginTop(10).SetMarginBottom(4).SetPaddingTop(6)
            .SetBorderTop(new SolidBorder(SurfaceBorder, 0.75f))
            .Add(new Text(string.IsNullOrWhiteSpace(fiador.Name) ? "Fiador sin denominación" : fiador.Name)
                .SetFont(fonts.HeadingBold).SetFontSize(10.5f).SetFontColor(Label))
            .Add(new Text("   CUIT " + FormatCuit(fiador.Cuit)).SetFont(fonts.Mono).SetFontSize(9).SetFontColor(LabelSecondary));

    // One fiador, built whole before it reaches the document. A fiador rarely fits one page, so each part — his
    // identity and terms, his filters, his indicators, his assets with their totals — is kept on one page where it
    // fits, and the block breaks only between parts: never a heading left alone at the foot of a page.
    private static Div FiadorBlock(Fonts fonts, PersonFiadorEvaluationResponse fiador, CounterGuaranteeContribution? contribution)
    {
        FiadorCoverageResult coverage = fiador.Coverage!;
        Div block = new Div().SetMarginBottom(10);
        Div identity = KeptTogether().Add(FiadorTitle(fonts, fiador));

        // The guarantee.
        Table terms = KeyValueTable();
        AddKeyValue(terms, fonts, "Tipo de garantía",
            string.IsNullOrWhiteSpace(fiador.GuaranteeType) ? Dash : PersonLabels.GuaranteeType(fiador.GuaranteeType));
        AddKeyValue(terms, fonts, "Monto a su cargo", Amount(fiador.AmountAtCharge, fiador.Currency));
        if (fiador.SpouseConsent is { } consent)
            AddKeyValue(terms, fonts, "Asentimiento conyugal", consent ? "Sí" : "No");
        if (fiador.HousingInopposable is { } inopposable)
            AddKeyValue(terms, fonts, "Inoponibilidad de la vivienda", inopposable ? "Sí" : "No");
        if (fiador.GuaranteesGivenAmount is { } given)
            AddKeyValue(terms, fonts, "Garantías ya otorgadas", Amount(given, "ARS"));
        if (contribution is not null)
        {
            string aporte = contribution.Counted
                ? Amount(contribution.Contribution, "ARS")
                : $"Fuera del total: rechazo sugerido (sumaría {Amount(contribution.ContributionIncludingFlagged, "ARS")})";
            if (contribution.DuplicateItemIds.Count > 0)
                aporte += $" · {contribution.DuplicateItemIds.Count.ToString(EsAr)} {(contribution.DuplicateItemIds.Count == 1 ? "bien ya computado" : "bienes ya computados")} para otro fiador";
            AddKeyValue(terms, fonts, "Aporte a la contragarantía", aporte, contribution.Counted ? null : Orange);
        }
        AddKeyValue(terms, fonts, "Sugerencia", PersonLabels.Suggestion(fiador.Suggestion),
            fiador.Suggestion == PersonSuggestions.RechazoSugerido ? Danger : Label);
        block.Add(identity.Add(terms));

        // His filters, in the solicitante's table.
        block.Add(KeptTogether()
            .Add(FiadorSubHeading(fonts, "Filtros"))
            .Add(FiltersTable(fonts, fiador.Filters, f => FilterConsultedAt(f, null, fiador.Sources))));

        // His two indicators.
        Table indicators = PersonTable(fonts, ["Indicador", "Valor", "Banda", "Verificado / Declarado"], [40f, 18f, 24f, 18f], rightAligned: [1]);
        bool alt = false;
        foreach (string id in FiadorIndicatorIds)
        {
            PersonIndicatorResult? indicator = fiador.Indicators.FirstOrDefault(i => i.Id == id);
            bool hasValue = indicator is not null && HasIndicatorValue(indicator);
            DeviceRgb? bg = alt ? Surface : null;
            indicators.AddCell(BodyCell(PersonLabels.Indicator(id), fonts.Body, 8f, bg));
            indicators.AddCell(BodyCell(hasValue ? IndicatorValue(indicator!) : Dash, fonts.Mono, 8f, bg, TextAlignment.RIGHT));
            indicators.AddCell(ChipCell(fonts, IndicatorChip(indicator),
                bg, hasValue && indicator!.Provisional ? PersonLabels.ProvisionalLabel : null));
            indicators.AddCell(BodyCell(hasValue ? PersonLabels.EvidenceKind(indicator!.EvidenceKind) ?? Dash : Dash, fonts.Body, 8f, bg));
            alt = !alt;
        }
        block.Add(KeptTogether().Add(FiadorSubHeading(fonts, "Indicadores")).Add(indicators));

        // His assets, as the evaluation counted them, and the executable patrimony they add up to.
        Div patrimony = KeptTogether().Add(FiadorSubHeading(fonts, "Bienes"));
        IReadOnlyCollection<Guid> duplicates = contribution?.DuplicateItemIds ?? [];
        if (coverage.Assets.Count == 0)
        {
            patrimony.Add(MutedParagraph(fonts, "Sin bienes informados."));
        }
        else
        {
            Table assets = PersonTable(fonts,
                ["Bien", "Valor usado", "Descuento", "Valor ejecutable", "Tratamiento", "Motivo", "Verificado / Declarado", "Alertas"],
                [14f, 13f, 8f, 13f, 10f, 22f, 10f, 10f], rightAligned: [1, 2, 3], bodyPadding: TightCellPadding);
            alt = false;
            foreach (FiadorAssetResult asset in coverage.Assets)
            {
                DeviceRgb? bg = alt ? Surface : null;
                AddAssetRow(assets, fonts, asset, duplicates.Contains(asset.ItemId), bg);
                alt = !alt;
            }
            patrimony.Add(assets);
        }

        Table totals = KeyValueTable();
        AddKeyValue(totals, fonts, "Patrimonio ejecutable bruto", Amount(coverage.GrossExecutable, "ARS"));
        AddKeyValue(totals, fonts, "Pasivos",
            $"{Amount(coverage.Liabilities, "ARS")} · {PersonLabels.LiabilitiesSource(coverage.LiabilitiesSource)}");
        AddKeyValue(totals, fonts, "Garantías otorgadas", Amount(coverage.GuaranteesGivenAmount, "ARS"));
        AddKeyValue(totals, fonts, "Patrimonio ejecutable", Amount(coverage.ExecutablePatrimony, "ARS"),
            coverage.ExecutablePatrimony < 0m ? Danger : Label);
        block.Add(patrimony.Add(totals));

        return block;
    }

    private static Div KeptTogether() => new Div().SetKeepTogether(true);

    private static void AddAssetRow(Table table, Fonts fonts, FiadorAssetResult asset, bool isDuplicate, DeviceRgb? bg)
    {
        string bien = PersonLabels.AssetKind(asset.Kind, asset.InvestmentClass)
                    + (string.IsNullOrWhiteSpace(asset.Description) ? string.Empty : "\n" + asset.Description.Trim())
                    + (asset.IsHousing ? "\nVivienda" : string.Empty)
                    + (asset.Origin == FiadorAssetOrigins.Solicitante ? "\nDel inventario del solicitante" : string.Empty);

        string valor = asset.ValueUsed is { } used
            ? Amount(used, "ARS")
              + (string.IsNullOrWhiteSpace(asset.ValueSource) ? string.Empty : "\n" + PersonLabels.AssetValueSource(asset.ValueSource))
            : Dash;

        string descuento = asset.Haircut is { } haircut
            ? Fraction(haircut) + (asset.CurrencyHaircut is { } extra ? $"\n+ {Fraction(extra)} (moneda)" : string.Empty)
            : Dash;

        DeviceRgb treatmentColor = asset.Treatment switch
        {
            FiadorAssetTreatments.Computado => Success,
            FiadorAssetTreatments.Excluido  => Danger,
            _                               => LabelSecondary
        };

        string reason = string.IsNullOrWhiteSpace(asset.ReasonText) ? PersonLabels.AssetReason(asset.Reason) : asset.ReasonText;

        var alerts = asset.Alerts.Select(PersonLabels.AssetAlert).ToList();
        if (isDuplicate)
            alerts.Add("Ya computado para otro fiador");

        table.AddCell(TightCell(bien, fonts.BodyMedium, bg));
        table.AddCell(TightCell(valor, fonts.Mono, bg, TextAlignment.RIGHT));
        table.AddCell(TightCell(descuento, fonts.Mono, bg, TextAlignment.RIGHT));
        table.AddCell(TightCell(Amount(asset.ExecutableValue, "ARS"), fonts.Mono, bg, TextAlignment.RIGHT));
        table.AddCell(ChipCell(fonts, (PersonLabels.AssetTreatment(asset.Treatment), treatmentColor), bg));
        table.AddCell(TightCell(reason, fonts.Body, bg));
        table.AddCell(TightCell(PersonLabels.EvidenceKind(asset.EvidenceKind) ?? Dash, fonts.Body, bg));
        table.AddCell(TightCell(alerts.Count == 0 ? Dash : string.Join("\n", alerts), fonts.Body, bg));
    }

    private const float TightCellPadding = 3f;

    // A narrow table cell: the eight-column asset table does not fit BodyCell's padding.
    private static Cell TightCell(string text, PdfFont font, DeviceRgb? bg, TextAlignment align = TextAlignment.LEFT)
    {
        Cell cell = new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
            .SetPadding(TightCellPadding)
            .SetTextAlignment(align)
            .Add(new Paragraph(text).SetFont(font).SetFontSize(7f).SetFontColor(Label).SetMargin(0));
        if (bg is not null)
            cell.SetBackgroundColor(bg);
        return cell;
    }

    private static Paragraph FiadorSubHeading(Fonts fonts, string text)
        => new Paragraph(text).SetFont(fonts.HeadingBold).SetFontSize(9).SetFontColor(Label).SetMarginTop(4).SetMarginBottom(3);

    // ── 5. Coherencia entre fuentes ───────────────────────────────────────────

    private static void AddPersonCoherenceSection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation)
    {
        AddSectionHeading(doc, fonts, "Coherencia entre fuentes");

        PersonIndicatorResult? nivel = IndicatorById(evaluation, "ventas_nivel");
        string? formula = nivel is null ? null
            : InputNumber(nivel.Inputs, "ventas12MesesReal") is { } real
                ? $"En pesos constantes: {Pesos((double)real)}"
                : "Sin serie de IPC: sólo valores nominales.";

        AddIndicatorBlock(doc, fonts, nivel, "ventas_nivel", null, formula, []);
        AddIndicatorBlock(doc, fonts, IndicatorById(evaluation, "ventas_tendencia"), "ventas_tendencia", null, null, []);
        AddIndicatorBlock(doc, fonts, IndicatorById(evaluation, "ventas_coherencia"), "ventas_coherencia", null, null, []);
        AddIndicatorBlock(doc, fonts, IndicatorById(evaluation, "ventas_acreedor_vs_fisco"), "ventas_acreedor_vs_fisco", null, null, []);
    }

    // ── 6. Cobertura documental ───────────────────────────────────────────────

    /// <summary>The status of one coverage item (the Portal's itemStatus).</summary>
    internal static string CoverageItemStatus(PersonCoverageItem item)
    {
        if (item.Present && item.IsStale) return "Desactualizado";
        if (item.Present) return "Presente";
        if (item.PendingDocumentIds.Count > 0) return "En proceso";
        return "Falta";
    }

    private static void AddPersonCoverageSection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation)
    {
        AddSectionHeading(doc, fonts, "Cobertura documental");

        if (evaluation.Coverage is not { } coverage)
        {
            doc.Add(NotAvailableParagraph(fonts.Body));
            return;
        }

        doc.Add(new Paragraph()
            .SetMarginBottom(6)
            .Add(new Text($"{coverage.RequiredPresent.ToString(EsAr)} de {coverage.RequiredTotal.ToString(EsAr)}")
                .SetFont(fonts.Mono).SetFontSize(9).SetFontColor(Label))
            .Add(new Text(" documentos obligatorios presentes para el régimen "
                          + PersonLabels.FiscalRegime(coverage.FiscalRegime, null).ToLower(EsAr) + ".")
                .SetFont(fonts.Body).SetFontSize(9).SetFontColor(Label)));

        if (coverage.Items.Count > 0)
        {
            Table table = PersonTable(fonts,
                ["Documento", "Obligatorio", "Estado", "Último período", "Antigüedad"],
                [32f, 11f, 15f, 15f, 27f], rightAligned: [3, 4]);

            bool alt = false;
            foreach (PersonCoverageItem item in coverage.Items)
            {
                DeviceRgb? bg = alt ? Surface : null;

                string document = PersonLabels.DocumentType(item.DocumentType);
                if (item.Alternatives.Count > 0)
                    document += "\nTambién vale: " + string.Join(", ", item.Alternatives.Select(PersonLabels.DocumentType));

                string status = CoverageItemStatus(item);
                DeviceRgb statusColor = status switch
                {
                    "Desactualizado" => Warning,
                    "Presente"       => Success,
                    "En proceso"     => Accent,
                    _                => item.Required ? Danger : LabelSecondary
                };

                string age = (item.AgeMonths is { } a ? Months(a) : Dash)
                           + (item.MaxAgeMonths is { } max ? $" (máx. {Months(max)})" : string.Empty);

                table.AddCell(BodyCell(document, fonts.BodyMedium, 7.5f, bg));
                table.AddCell(BodyCell(item.Required ? "Sí" : "No", fonts.Body, 7.5f, bg));
                table.AddCell(ChipCell(fonts, (status, statusColor), bg));
                table.AddCell(BodyCell(item.LatestPeriodEnd is { } end ? end.ToString("dd/MM/yyyy", EsAr) : Dash,
                    fonts.Mono, 7.5f, bg, TextAlignment.RIGHT));
                table.AddCell(BodyCell(age, fonts.Mono, 7.5f, bg, TextAlignment.RIGHT));
                alt = !alt;
            }
            doc.Add(table);
        }

        if (coverage.CannotConclude.Count > 0)
        {
            AddSubHeading(doc, fonts, "No se puede concluir");
            foreach (PersonCannotConclude statement in coverage.CannotConclude)
            {
                doc.Add(new Paragraph("• " + PersonLabels.CannotConclude(statement.Code, statement.Text))
                    .SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(Label).SetMarginTop(0).SetMarginBottom(3));
            }
        }
    }

    // ── 7. Fuentes ────────────────────────────────────────────────────────────

    private static readonly string[] Registries = ["BCRA", "ARCA", "LUFE"];

    private static void AddPersonSourcesSection(ITextDocument doc, Fonts fonts, PersonEvaluationResponse evaluation)
    {
        AddSectionHeading(doc, fonts, "Fuentes consultadas");

        Table table = PersonTable(fonts, ["Fuente", "Consultado el", "Antigüedad", "Estado"], [32f, 18f, 32f, 18f], rightAligned: [1, 2]);
        bool alt = false;
        foreach (string registry in Registries)
        {
            DeviceRgb? bg = alt ? Surface : null;
            PersonCoverageSnapshot? snapshot = evaluation.Coverage?.Snapshots
                .FirstOrDefault(s => string.Equals(s.Source?.Trim(), registry, StringComparison.OrdinalIgnoreCase));
            DateTime? stamp = SourceStamp(evaluation.Sources, registry) ?? snapshot?.UpdatedAt;

            // Calendar days in Buenos Aires from the stamp to the evaluation — the snapshot's AgeDays basis.
            int? ageDays = stamp is { } st ? ArgentineDaysBetween(st, evaluation.CreatedAt) ?? snapshot?.AgeDays : null;
            string age = ageDays is { } days ? AgeDays(days) : Dash;
            if (snapshot is not null)
                age += $" (máx. {snapshot.MaxAgeDays.ToString(EsAr)} días)";

            (string Text, DeviceRgb Color) state = stamp is null
                ? ("Sin consulta", LabelSecondary)
                : snapshot?.IsStale == true ? ("Desactualizado", Warning) : ("Vigente", Success);

            table.AddCell(BodyCell(PersonLabels.SnapshotSource(registry), fonts.BodyMedium, 8f, bg));
            table.AddCell(BodyCell(stamp is { } at ? ArDate(at) : "sin consulta", fonts.Mono, 8f, bg, TextAlignment.RIGHT));
            table.AddCell(BodyCell(age, fonts.Mono, 8f, bg, TextAlignment.RIGHT));
            table.AddCell(ChipCell(fonts, state, bg));
            alt = !alt;
        }
        doc.Add(table);

        PersonEvaluationSources? sources = evaluation.Sources;
        Table facts = KeyValueTable();
        string macro = sources?.MacroSource switch
        {
            "legajo" => "Snapshot del BCRA del legajo",
            "bcra"   => "Consulta directa al BCRA",
            null     => "Sin serie de IPC",
            var other => other
        };
        if (sources?.MacroFetchedAt is { } fetched)
            macro += $" · obtenida el {ArDate(fetched)}";
        AddKeyValue(facts, fonts, "Serie de IPC (macro)", macro);
        AddKeyValue(facts, fonts, "Documentos leídos", sources is null ? Dash : sources.Documents.Count.ToString(EsAr));
        doc.Add(facts);
    }

    // ── 8. Decisión ───────────────────────────────────────────────────────────

    private static void AddPersonDecisionSection(ITextDocument doc, Fonts fonts, PersonPdfReport report)
    {
        AddSectionHeading(doc, fonts, "Decisión");

        Analysis analysis = report.Analysis;
        bool isPreApproved = analysis.Status == AnalysisStatuses.PreApproved;
        bool isApproved    = analysis.Status == AnalysisStatuses.Approved;
        bool isRejected    = analysis.Status == AnalysisStatuses.Rejected;
        bool isDecided     = isApproved || isRejected;

        Table status = KeyValueTable();
        AddKeyValue(status, fonts, "Estado",
            isDecided || isPreApproved ? PersonLabels.AnalysisStatus(analysis.Status) : PendingDecision,
            isApproved ? Success : isRejected ? Danger : isPreApproved ? Orange : Warning);

        PersonDecisionRules? rules = report.Evaluation?.DecisionRules;
        if (rules is not null)
        {
            AddKeyValue(status, fonts, "Requisitos de aprobación",
                (rules.RequiresReason ? "Motivo obligatorio" : "Motivo opcional")
                + (rules.RequiresPreApproval ? " · requiere preaprobación" : string.Empty)
                + $" · contragarantía mínima {rules.MinCounterGuaranteeCoveragePct.ToString(EsAr)} %");

            // The aggregate coverage of the fiadores, the very value the detail and the decision read.
            AddKeyValue(status, fonts, "Cobertura de contragarantías", CounterGuaranteeCoverageText(rules.CounterGuaranteeCoverage),
                rules.CounterGuaranteeBelowMin ? Danger : null);
        }

        // A re-evaluation overwrote the evaluation in place: say so, so the date of the evaluation above is
        // not read as the date the analysis was opened.
        if (report.RescoreCount > 0 && report.RescoredAt is { } rescoredAt)
        {
            AddKeyValue(status, fonts, "Reevaluación",
                $"Reevaluado el {ArDate(rescoredAt)} ({report.RescoreCount.ToString(EsAr)} {(report.RescoreCount == 1 ? "vez" : "veces")})");
        }
        doc.Add(status);

        int coveragePct = rules?.MinCounterGuaranteeCoveragePct
                          ?? (int)PersonEvaluationDefaults.Get("pf_contragarantia_cobertura_min_pct");

        // The pre-approval record: only while it stands (pre-approved) or once it led to the approval. A
        // pre-approval returned for rework sends the analysis back to PendingReview, and is not printed as live.
        bool hadPreApproval = (isPreApproved || isApproved)
                              && (analysis.PreApprovedAt is not null || report.PreApprovalChecks is not null);
        if (hadPreApproval)
        {
            AddSubHeading(doc, fonts, PersonLabels.PreApprovalTitle);
            Table pre = KeyValueTable();
            AddKeyValue(pre, fonts, "Preaprobado por", UserName(report, analysis.PreApprovedBy ?? report.PreApprovalChecks?.By));
            AddKeyValue(pre, fonts, "Fecha", ArDateTime(analysis.PreApprovedAt ?? report.PreApprovalChecks?.At));
            AddKeyValue(pre, fonts, "Monto", Amount(analysis.PreApprovedAmount, analysis.PreApprovedCurrency));
            AddChecksRows(pre, fonts, report.PreApprovalChecks, coveragePct);
            doc.Add(pre);
        }

        // The final act.
        AddSubHeading(doc, fonts, isRejected ? "Rechazo" : PersonLabels.ConfirmationTitle(hadPreApproval));
        if (!isDecided)
        {
            doc.Add(new Paragraph(PendingDecision)
                .SetFont(fonts.BodyMedium).SetFontSize(10).SetFontColor(Warning).SetMarginBottom(6));
        }
        else
        {
            Table final = KeyValueTable();
            AddKeyValue(final, fonts, isRejected ? "Rechazado por" : "Aprobado por",
                UserName(report, analysis.ReviewedBy ?? report.ConfirmationChecks?.By));
            AddKeyValue(final, fonts, "Fecha", ArDateTime(analysis.ReviewedAt ?? report.ConfirmationChecks?.At));
            if (isApproved)
            {
                AddKeyValue(final, fonts, "Monto", Amount(analysis.DecisionAmount, analysis.DecisionCurrency));
                AddChecksRows(final, fonts, report.ConfirmationChecks, coveragePct);
            }

            // The review notes, when they say something the reason does not already say.
            string? notes  = analysis.ReviewNotes?.Trim();
            string? reason = report.ConfirmationChecks?.Reason?.Trim();
            if (!string.IsNullOrEmpty(notes) && !string.Equals(notes, reason, StringComparison.Ordinal))
                AddKeyValue(final, fonts, "Notas de revisión", notes);
            doc.Add(final);
        }

        // The credit line the approval originated.
        if (report.CreditLines.Count > 0 || isApproved)
        {
            AddSubHeading(doc, fonts, "Línea de crédito originada");
            if (report.CreditLines.Count == 0)
            {
                doc.Add(MutedParagraph(fonts, "Sin línea de crédito originada."));
            }
            else
            {
                Table lines = PersonTable(fonts, ["Monto", "Moneda", "Estado"], [40f, 25f, 35f], rightAligned: [0]);
                foreach (CreditLine line in report.CreditLines)
                {
                    lines.AddCell(BodyCell(Amount(line.Amount, line.Currency), fonts.Mono, 8.5f, null, TextAlignment.RIGHT));
                    lines.AddCell(BodyCell(string.IsNullOrWhiteSpace(line.Currency) ? Dash : line.Currency, fonts.Body, 8.5f));
                    lines.AddCell(BodyCell(PersonLabels.CreditLineStatus(line.Status), fonts.Body, 8.5f));
                }
                doc.Add(lines);
            }
        }
    }

    /// <summary>
    /// The aggregate counter-guarantee coverage of the fiadores, one line: the ratio against the minimum, or why
    /// there is none. The fiadores themselves print in persona_fiadores.
    /// </summary>
    internal static string CounterGuaranteeCoverageText(CounterGuaranteeCoverage? coverage)
    {
        if (coverage is null)
            return "Sin fiadores evaluados";

        var count = coverage.FiadoresCount == 1 ? "1 fiador" : $"{coverage.FiadoresCount.ToString(EsAr)} fiadores";
        var flagged = coverage.FlaggedFiadoresCount > 0
            ? $" · {coverage.FlaggedFiadoresCount.ToString(EsAr)} con rechazo sugerido, no computado(s)"
            : string.Empty;

        return coverage.Ratio is { } ratio
            ? $"{(ratio * 100m).ToString("0.#", EsAr)} % del monto solicitado (mínimo {coverage.MinPct.ToString(EsAr)} %, " +
              $"{(coverage.MeetsMin ? "cumple" : "no cumple")}) · {count}{flagged}"
            : $"No calculable: la operación no está en pesos (mínimo {coverage.MinPct.ToString(EsAr)} %) · {count}{flagged}";
    }

    private static void AddChecksRows(Table table, Fonts fonts, AnalysisDecisionChecks? checks, int coveragePct)
    {
        if (checks is null)
            return;

        AddKeyValue(table, fonts, "Motivo",
            string.IsNullOrWhiteSpace(checks.Reason) ? PersonLabels.NoReasonRecorded : checks.Reason);
        AddKeyValue(table, fonts, "Contragarantías",
            PersonLabels.CounterGuaranteesConfirmation(coveragePct) + (checks.CounterGuaranteesConfirmed ? " (confirmado)" : " (no confirmado)"),
            checks.CounterGuaranteesConfirmed ? Success : Danger);
        AddKeyValue(table, fonts, "Fondo de Riesgo",
            PersonLabels.FdrLimitConfirmation + (checks.FdrLimitConfirmed ? " (confirmado)" : " (no confirmado)"),
            checks.FdrLimitConfirmed ? Success : Danger);
    }

    private static string UserName(PersonPdfReport report, Guid? userId)
        => userId is { } id && report.UserNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : Dash;

    // ── Blocks ────────────────────────────────────────────────────────────────

    // One indicator as a card: label, value with its band and evidence kind, the provisional mark, the cuts,
    // the formula in words, the engine's evidence and the inputs not already shown.
    private static void AddIndicatorBlock(
        ITextDocument doc, Fonts fonts, PersonIndicatorResult? indicator, string id,
        string? cuts, string? formula, IReadOnlyCollection<string> omitInputs)
    {
        Cell card = CardCell();
        card.Add(new Paragraph(PersonLabels.Indicator(id))
            .SetFont(fonts.HeadingBold).SetFontSize(9.5f).SetFontColor(Label).SetMargin(0));

        if (indicator is null)
        {
            card.Add(SmallParagraph(fonts, "El análisis no trae este indicador.", LabelSecondary));
            doc.Add(CardTable(card));
            return;
        }

        bool hasValue = HasIndicatorValue(indicator);
        (string chipText, DeviceRgb chipColor) = IndicatorChip(indicator);

        // The value, then its tags joined by " · ": the band (or the alert, or Sin datos), the evidence kind
        // and the provisional mark — each only when it applies.
        var tags = new List<Text>();
        if (!string.IsNullOrEmpty(chipText))
            tags.Add(new Text(chipText).SetFont(fonts.BodyMedium).SetFontSize(8.5f).SetFontColor(chipColor));
        if (hasValue && PersonLabels.EvidenceKind(indicator.EvidenceKind) is { } kind)
            tags.Add(new Text(kind).SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(LabelSecondary));
        if (indicator.Provisional)
            tags.Add(new Text(PersonLabels.ProvisionalLabel).SetFont(fonts.BodyMedium).SetFontSize(8.5f).SetFontColor(Warning));

        Paragraph headline = new Paragraph().SetMarginTop(3).SetMarginBottom(0);
        if (hasValue)
            headline.Add(new Text(IndicatorValue(indicator) + "   ").SetFont(fonts.Mono).SetFontSize(11).SetFontColor(Label));
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0)
                headline.Add(new Text(" · ").SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(LabelSecondary));
            headline.Add(tags[i]);
        }
        card.Add(headline);

        if (hasValue && !string.IsNullOrWhiteSpace(cuts))
            card.Add(SmallParagraph(fonts, cuts + (indicator.Provisional ? " (provisorios)" : string.Empty), LabelSecondary));
        if (!string.IsNullOrWhiteSpace(formula))
            card.Add(SmallParagraph(fonts, "Fórmula: " + formula, Label));
        if (!string.IsNullOrWhiteSpace(indicator.Evidence))
            card.Add(SmallParagraph(fonts, indicator.Evidence, LabelSecondary));

        AddInputsTable(card, fonts, indicator.Inputs, omitInputs);
        doc.Add(CardTable(card));
    }

    // One filter as a card (the capacity section's monotributo cap or declared consumption).
    private static void AddFilterBlock(ITextDocument doc, Fonts fonts, PersonFilterResult? filter, string id)
    {
        Cell card = CardCell();
        card.Add(new Paragraph(PersonLabels.Filter(id))
            .SetFont(fonts.HeadingBold).SetFontSize(9.5f).SetFontColor(Label).SetMargin(0));

        if (filter is null)
        {
            card.Add(SmallParagraph(fonts, "El análisis no trae este filtro.", LabelSecondary));
            doc.Add(CardTable(card));
            return;
        }

        Paragraph headline = new Paragraph().SetMarginTop(3).SetMarginBottom(0)
            .Add(new Text(PersonLabels.FilterOutcome(filter.Outcome, filter.Severity))
                .SetFont(fonts.BodyMedium).SetFontSize(8.5f).SetFontColor(FilterColor(filter)));
        if (FilterHasResult(filter) && PersonLabels.EvidenceKind(filter.EvidenceKind) is { } kind)
            headline.Add(new Text(" · " + kind).SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(LabelSecondary));
        // The monotributo category table awaits its official update: its cap is a provisional threshold.
        if (InputFlag(filter.Inputs, "tablaProvisional"))
            headline.Add(new Text(" · " + PersonLabels.ProvisionalLabel).SetFont(fonts.BodyMedium).SetFontSize(8.5f).SetFontColor(Warning));
        card.Add(headline);

        if (!string.IsNullOrWhiteSpace(filter.Evidence))
            card.Add(SmallParagraph(fonts, filter.Evidence, LabelSecondary));

        AddInputsTable(card, fonts, filter.Inputs, []);
        doc.Add(CardTable(card));
    }

    private static void AddInputsTable(
        Cell card, Fonts fonts, IReadOnlyDictionary<string, string?>? inputs, IReadOnlyCollection<string> omit)
    {
        if (inputs is null)
            return;

        var rows = inputs.Where(kv => !omit.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).ToList();
        if (rows.Count == 0)
            return;

        Table table = new Table(UnitValue.CreatePercentArray([55f, 45f])).UseAllAvailableWidth().SetMarginTop(4);
        foreach (KeyValuePair<string, string?> row in rows)
        {
            table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(SurfaceBorder, 0.5f))
                .SetPadding(2.5f)
                .Add(new Paragraph(PersonLabels.Input(row.Key)).SetFont(fonts.Body).SetFontSize(7.5f).SetFontColor(LabelSecondary).SetMargin(0)));
            table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(SurfaceBorder, 0.5f))
                .SetPadding(2.5f).SetTextAlignment(TextAlignment.RIGHT)
                .Add(new Paragraph(FormatInputValue(row.Key, row.Value)).SetFont(fonts.Mono).SetFontSize(7.5f).SetFontColor(Label).SetMargin(0)));
        }
        card.Add(table);
    }

    private static Cell CardCell()
        => new Cell()
            .SetBorder(new SolidBorder(SurfaceBorder, 0.75f))
            .SetBackgroundColor(Surface)
            .SetPadding(8);

    private static Table CardTable(Cell card)
    {
        Table table = new Table(UnitValue.CreatePercentArray([100f])).UseAllAvailableWidth().SetMarginBottom(6);
        table.AddCell(card);
        return table;
    }

    // A header row in the deep-base treatment. The columns in rightAligned hold right-aligned figures (dates, amounts,
    // ages): their headers align right too, on the body cells' right padding (BodyCell's, or TightCell's for the
    // asset table) so the edges coincide. Chip columns stay left, like their chips.
    private static Table PersonTable(
        Fonts fonts, string[] headers, float[] widths, int[]? rightAligned = null, float bodyPadding = BodyCellPadding)
    {
        Table table = new Table(UnitValue.CreatePercentArray(widths)).UseAllAvailableWidth().SetMarginBottom(8);
        for (int i = 0; i < headers.Length; i++)
        {
            TextAlignment align = rightAligned?.Contains(i) == true ? TextAlignment.RIGHT : TextAlignment.LEFT;
            table.AddHeaderCell(AlignHeader(new Cell()
                .SetBackgroundColor(ColorTableHead)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(5)
                .Add(new Paragraph(headers[i]).SetFont(fonts.HeadingBold).SetFontSize(7.5f).SetFontColor(ColorTableHeadFg)),
                align, bodyPadding));
        }

        return table;
    }

    private static Table KeyValueTable()
        => new Table(UnitValue.CreatePercentArray([32f, 68f])).UseAllAvailableWidth().SetMarginBottom(8);

    private static void AddKeyValue(Table table, Fonts fonts, string label, string value, DeviceRgb? valueColor = null)
    {
        table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(ColorBorder, 0.5f)).SetPadding(4)
            .Add(new Paragraph(label).SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(LabelSecondary).SetMargin(0)));
        table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(ColorBorder, 0.5f)).SetPadding(4)
            .Add(new Paragraph(value).SetFont(valueColor is null ? fonts.Body : fonts.BodyMedium).SetFontSize(8.5f)
                .SetFontColor(valueColor ?? Label).SetMargin(0)));
    }

    private static Cell ChipCell(Fonts fonts, (string Text, DeviceRgb Color) chip, DeviceRgb? bg, string? note = null)
    {
        Paragraph p = new Paragraph().SetMargin(0)
            .Add(new Text(chip.Text).SetFont(fonts.BodyMedium).SetFontSize(7.5f).SetFontColor(chip.Color));
        if (note is not null)
            p.Add(new Text("\n" + note).SetFont(fonts.Body).SetFontSize(6.5f).SetFontColor(Warning));

        Cell cell = new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
            .SetPadding(6)
            .Add(p);
        if (bg is not null)
            cell.SetBackgroundColor(bg);
        return cell;
    }

    private static Paragraph SmallParagraph(Fonts fonts, string text, DeviceRgb color)
        => new Paragraph(text).SetFont(fonts.Body).SetFontSize(8f).SetFontColor(color).SetMarginTop(3).SetMarginBottom(0);

    private static Paragraph MutedParagraph(Fonts fonts, string text)
        => new Paragraph(text).SetFont(fonts.Body).SetFontSize(7.5f).SetFontColor(LabelSecondary).SetMarginTop(0).SetMarginBottom(8);

    // ── Reading the evaluation ────────────────────────────────────────────────

    private static PersonFilterResult? FilterById(PersonEvaluationResponse evaluation, string id)
        => evaluation.Filters.FirstOrDefault(f => f.Id == id);

    private static PersonIndicatorResult? IndicatorById(PersonEvaluationResponse evaluation, string id)
        => evaluation.Indicators.FirstOrDefault(i => i.Id == id);

    /// <summary>A filter that reached a result; SinDatos (or no outcome) has nothing to attribute.</summary>
    private static bool FilterHasResult(PersonFilterResult? filter)
        => !string.IsNullOrWhiteSpace(filter?.Outcome) && filter.Outcome != PersonFilterOutcomes.SinDatos;

    private static string FilterResultText(PersonFilterResult? filter)
    {
        string outcome = PersonLabels.FilterOutcome(filter?.Outcome, filter?.Severity);
        return FilterHasResult(filter) && PersonLabels.EvidenceKind(filter!.EvidenceKind) is { } kind
            ? $"{outcome} · {kind}"
            : outcome;
    }

    /// <summary>
    /// The Verificado / Declarado cell of a filter. A filter with no result (SinDatos) has nothing to
    /// attribute, so it prints a dash, never a kind.
    /// </summary>
    internal static string FilterEvidenceKindText(PersonFilterResult filter)
        => FilterHasResult(filter) ? PersonLabels.EvidenceKind(filter.EvidenceKind) ?? Dash : Dash;

    private static DeviceRgb FilterColor(PersonFilterResult? filter)
        => OutcomeColor(PersonLabels.FilterOutcome(filter?.Outcome, filter?.Severity));

    private static DeviceRgb OutcomeColor(string label) => label switch
    {
        "Rechazo"       => Danger,
        "Alerta fuerte" => Orange,
        "Alerta"        => Warning,
        "Ok"            => Success,
        _               => LabelSecondary
    };

    /// <summary>Ok with a value. Anything else reads "Sin datos".</summary>
    private static bool HasIndicatorValue(PersonIndicatorResult indicator)
        => indicator.Status == PersonIndicatorStatuses.Ok && indicator.Value is not null;

    // The Portal's indicatorDisplay: no value → Sin datos; else the band, else the alert it raises.
    private static (string Text, DeviceRgb Color) IndicatorChip(PersonIndicatorResult? indicator)
    {
        if (indicator is null || !HasIndicatorValue(indicator))
            return (PersonLabels.SinDatos, LabelSecondary);

        if (PersonLabels.Band(indicator.Band) is { } band)
        {
            DeviceRgb color = indicator.Band switch
            {
                PersonIndicatorBands.Verde    => Success,
                PersonIndicatorBands.Amarillo => Warning,
                PersonIndicatorBands.Naranja  => Orange,
                PersonIndicatorBands.Rojo     => Danger,
                _                             => LabelSecondary
            };
            return (band, color);
        }

        return PersonLabels.IndicatorSeverity(indicator.Severity) is { } alert
            ? (alert, indicator.Severity == PersonSeverities.Fuerte ? Orange : Warning)
            : (string.Empty, LabelSecondary);
    }

    private static bool Estimated(PersonIndicatorResult indicator) => InputFlag(indicator.Inputs, "cuotasVigentesEsEstimacion");

    private static DateTime? SourceStamp(PersonEvaluationSources? sources, string registry) => registry switch
    {
        "BCRA" => sources?.BcraDataUpdatedAt,
        "ARCA" => sources?.ArcaDataUpdatedAt,
        "LUFE" => sources?.LufeDataUpdatedAt,
        _      => null
    };

    private static string? CertificateAlertWindow(IReadOnlyDictionary<string, string?>? inputs)
    {
        decimal? from = InputNumber(inputs, "alertaDesdeMes");
        decimal? to   = InputNumber(inputs, "alertaHastaMes");
        if (from is { } f && to is { } t) return $"{MonthOfYear(f)} a {MonthOfYear(t)}";
        if (from is { } f2) return $"desde {MonthOfYear(f2)}";
        if (to is { } t2) return $"hasta {MonthOfYear(t2)}";
        return null;
    }

    // ── Formats (a port of person-format.ts) ──────────────────────────────────

    private static string? Input(IReadOnlyDictionary<string, string?>? inputs, string key)
        => inputs is not null && inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static decimal? InputNumber(IReadOnlyDictionary<string, string?>? inputs, string key)
        => ParseInvariant(Input(inputs, key));

    private static bool InputFlag(IReadOnlyDictionary<string, string?>? inputs, string key)
        => Input(inputs, key) == "true";

    private static bool IsNotInLegajo(string? raw)
        => raw is not null && string.Equals(raw.Trim(), NotInLegajo, StringComparison.OrdinalIgnoreCase);

    /// <summary>A strict invariant decimal ("0.####"), or null — never a locale-formatted string.</summary>
    private static decimal? ParseInvariant(string? raw)
    {
        string? trimmed = raw?.Trim();
        if (trimmed is null || !InvariantNumber.IsMatch(trimmed))
            return null;

        return decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out decimal value) ? value : null;
    }

    // One money term of a formula: its label and figure, or the engine's "no disponible en el legajo" as text —
    // never as $ 0.
    private static string Term(IReadOnlyDictionary<string, string?>? inputs, string key, string? label, bool estimated = false)
    {
        string prefix = label is null ? string.Empty : label + " ";
        string? raw = Input(inputs, key);
        if (IsNotInLegajo(raw))
            return prefix + NotInLegajo;

        decimal? value = ParseInvariant(raw);
        return prefix + (value is { } v ? Pesos((double)v) + (estimated ? " (estimadas)" : string.Empty) : Dash);
    }

    /// <summary>The value of an indicator: pesos, a share, a multiple (debt service, fiador patrimony), or a plain number.</summary>
    private static string IndicatorValue(PersonIndicatorResult indicator)
    {
        if (indicator.Value is not { } value)
            return Dash;

        return indicator.Unit switch
        {
            "ARS"   => Pesos((double)value),
            "ratio" => indicator.Id is "cobertura_servicio_deuda" or "cobertura_patrimonial_fiador" ? Multiple(value) : Fraction(value),
            _       => value.ToString("#,##0.##", EsAr)
        };
    }

    /// <summary>One input value, es-AR (person-format.ts formatInputValue). Text is returned as text.</summary>
    private static string FormatInputValue(string key, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Dash;
        if (raw == "true")  return "Sí";
        if (raw == "false") return "No";

        InputKinds.TryGetValue(key, out InputKind kind);
        bool known = InputKinds.ContainsKey(key);
        if (known && kind == InputKind.Date) return FormatDateInput(raw);

        decimal? parsed = ParseInvariant(raw);
        if (parsed is not { } v) return PersonLabels.InputTextValue(raw);
        if (!known) return v.ToString("#,##0.####", EsAr);

        return kind switch
        {
            InputKind.Money       => Pesos((double)v),
            InputKind.Fraction    => Fraction(v),
            InputKind.Multiple    => Cut(v),
            InputKind.Months      => Months(v),
            InputKind.MonthOfYear => MonthOfYear(v),
            InputKind.Integer     => v.ToString("#,##0", EsAr),
            _                     => raw
        };
    }

    /// <summary>
    /// A date input: a bare "yyyy-MM-dd" is a calendar day read as written; a timestamp is an instant shown in
    /// Buenos Aires; anything else is printed as it came.
    /// </summary>
    private static string FormatDateInput(string raw)
    {
        string trimmed = raw.Trim();
        if (IsoDate.IsMatch(trimmed)
            && DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            return day.ToString("dd/MM/yyyy", EsAr);
        }

        if (trimmed.Length > 10 && trimmed[10] == 'T'
            && DateTime.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime instant))
        {
            return ArDate(instant);
        }

        return raw;
    }

    /// <summary>A share as an es-AR percentage: 0.355 → "35,5%".</summary>
    private static string Fraction(decimal value) => (value * 100m).ToString("#,##0.0", EsAr) + "%";

    /// <summary>A multiple: 1.25 → "1,25×".</summary>
    private static string Multiple(decimal value) => value.ToString("#,##0.00", EsAr) + "×";

    /// <summary>A cut, short: 1 → "1,0", 1.3 → "1,3".</summary>
    private static string Cut(decimal value) => value.ToString("#,##0.0#", EsAr);

    private static string Months(decimal value)
        => value.ToString("#,##0", EsAr) + (Math.Abs(value) == 1m ? " mes" : " meses");

    private static string Months(int value) => Months((decimal)value);

    /// <summary>A month of the year by name: 5 → "mayo".</summary>
    private static string MonthOfYear(decimal value)
        => value is >= 1 and <= 12 && value == decimal.Truncate(value)
            ? EsAr.DateTimeFormat.GetMonthName((int)value).ToLower(EsAr)
            : value.ToString("#,##0.####", EsAr);

    private static string AgeDays(int days)
        => $"hace {days.ToString("#,##0", EsAr)} {(days == 1 ? "día" : "días")}";

    /// <summary>Calendar days in Buenos Aires from <paramref name="from"/> to <paramref name="to"/>; null when negative.</summary>
    private static int? ArgentineDaysBetween(DateTime from, DateTime to)
    {
        int days = DateOnly.FromDateTime(ArgentineTime.ToLocal(to)).DayNumber
                 - DateOnly.FromDateTime(ArgentineTime.ToLocal(from)).DayNumber;
        return days >= 0 ? days : null;
    }

    /// <summary>An instant as an es-AR date in Buenos Aires.</summary>
    private static string ArDate(DateTime utc) => ArgentineTime.ToLocal(utc).ToString("dd/MM/yyyy", EsAr);

    private static string ArDateTime(DateTime? utc)
        => utc is { } at ? ArgentineTime.ToLocal(at).ToString("dd/MM/yyyy HH:mm", EsAr) : Dash;

    /// <summary>An amount in its currency: "$ 1.234.567" for pesos, "US$ 1.234" for dollars, else the code.</summary>
    private static string Amount(decimal? amount, string? currency)
    {
        if (amount is not { } value)
            return Dash;

        string body = Math.Abs(Math.Round(value, MidpointRounding.AwayFromZero)).ToString("N0", EsAr);
        string symbol = currency?.Trim().ToUpperInvariant() switch
        {
            null or "" or "ARS" => "$",
            "USD"               => "US$",
            var code            => code
        };
        return (value < 0 ? PdfText.Minus.ToString() : string.Empty) + symbol + " " + body;
    }

    /// <summary>"20123456789" → "20-12345678-9"; anything that is not eleven digits prints as stored.</summary>
    private static string FormatCuit(string? cuit)
    {
        if (string.IsNullOrWhiteSpace(cuit))
            return Dash;

        string digits = new(cuit.Where(char.IsDigit).ToArray());
        return digits.Length == 11 ? $"{digits[..2]}-{digits[2..10]}-{digits[10..]}" : cuit;
    }
}
