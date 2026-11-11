// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Servicios.Pdf;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Layout;
using iText.Layout.Properties;
using iText.Layout.Renderer;
using Microsoft.Extensions.Logging;
using ITextDocument = iText.Layout.Document;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Page 1 of the company report: the fact sheet. Always rendered, whatever sections were chosen, and always
/// ONE page — identity, the strip, the score card and its composition are never dropped; when the page would
/// overflow it drops "Vigencia de los datos", then "Comparación", then trims "Puntos de atención" to three
/// bullets (<see cref="FactSheetFit"/>). Every item is built in isolation: one that cannot be built is
/// omitted with a Warning (ids only) and the export goes on.
/// </summary>
public sealed partial class PdfExportService
{
    private const float FactSheetSafety = 6f;

    // The strip's right padding between columns, and the room a measured value keeps for iText's own rounding.
    private const float StripGutter = 8f;
    private const float StripSlack  = 1f;

    /// <summary>The figures and facts the first page reads, parsed once.</summary>
    private sealed record FactSheetData
    {
        public required AnalysisResult Result { get; init; }
        public required Analysis Analysis { get; init; }
        public required CompanyPdfReport Report { get; init; }
        public ScoreReliabilityResponse? Reliability { get; init; }
        public ScoreBreakdownWrapper? Breakdown { get; init; }
        public Dictionary<string, double?>? Ratios { get; init; }
        public BcraData? Bcra { get; init; }
        public LufeData? Lufe { get; init; }
        public ScoreAnatomy.Model? Anatomy { get; init; }
        public int? ClosingMonth { get; init; }
        public required DateTime NowUtc { get; init; }

        /// <summary>The deterministic alert channel, read once per export (the risk section reads the same).</summary>
        public CatalogueAlertChannel.Result Catalogue { get; init; } = CatalogueAlertChannel.Result.Absent;
    }

    private void AddFactSheet(
        ITextDocument doc, Fonts fonts, AnalysisResult result, Analysis analysis, CompanyPdfReport report,
        ScoreReliabilityResponse? reliability, CatalogueAlertChannel.Result catalogue)
    {
        FactSheetData data = ReadFactSheetData(result, analysis, report, reliability) with { Catalogue = catalogue };

        // Room for the full-bleed band drawn by the page furniture.
        doc.Add(new Div().SetHeight(Page1Spacer).SetMargin(0));

        Div?  identity    = Build("identity",    () => BuildIdentity(fonts, data));
        Div?  strip       = Build("strip",       () => BuildStrip(fonts, data));
        Div?  scoreCard   = Build("score_card",  () => BuildScoreCard(doc, fonts, data));
        Div?  composition = Build("composition", () => BuildComposition(doc, fonts, data));
        Div?  decision    = Build("decision",    () => BuildDecision(fonts, data));
        Div?  vigencia    = Build("vigencia",    () => BuildVigencia(fonts, data));
        Div?  comparacion = Build("comparacion", () => BuildComparacion(fonts, data));

        // With both present, the two short blocks sit side by side; each still drops on its own, in order.
        Div?  vigenciaYComparacion = vigencia is not null && comparacion is not null
            ? Build("vigencia_comparacion", () => SideBySide(BuildVigencia(fonts, data)!, BuildComparacion(fonts, data)!))
            : null;

        AttentionPoints.Selection? selection = Build("attention", () => AttentionPoints.Evaluate(AttentionFacts(data)));
        IReadOnlyList<string> points = selection?.Bullets ?? [];
        Div attentionFull    = BuildAttention(fonts, points);
        Div attentionTrimmed = BuildAttention(fonts, points.Take(FactSheetFit.TrimmedAttentionCap).ToList());

        // Measure at the content width, against the room page 1 has under the band.
        Rectangle page   = doc.GetPdfDocument().GetDefaultPageSize();
        float width      = page.GetWidth() - (2 * SideMargin);
        float available  = page.GetHeight() - TopMargin - BottomMargin - Page1Spacer - FactSheetSafety;

        float Height(Div? block) => block is null ? 0f : MeasureHeight(doc, block, width);

        FactSheetFit.Plan plan = FactSheetFit.Choose(
            new FactSheetFit.Heights
            {
                Fixed            = Height(identity) + Height(strip) + Height(scoreCard) + Height(composition) + Height(decision),
                AttentionFull    = Height(attentionFull),
                AttentionTrimmed = Height(attentionTrimmed),
                Vigencia         = Height(vigencia),
                Comparacion      = Height(comparacion),
                Both             = vigenciaYComparacion is null ? null : Height(vigenciaYComparacion),
            },
            available);

        if (!plan.Fits)
        {
            _logger.LogWarning(
                "PDF export: the first page of analysis {AnalysisId} does not fit even after trimming; its last items continue on page 2",
                result.AnalysisId);
        }

        // The bookmark for the fact sheet sits on its first line.
        Paragraph anchor = new Paragraph(string.Empty).SetMargin(0).SetFontSize(1f);
        AddOutlineEntry(doc, anchor, "Ficha del análisis");
        doc.Add(anchor);

        foreach (Div? block in new[] { identity, strip, scoreCard, composition })
        {
            if (block is not null) doc.Add(block);
        }

        doc.Add(plan.TrimAttention ? attentionTrimmed : attentionFull);

        if (selection is not null)
            LogUnlistedOverlap(selection, plan.TrimAttention ? Math.Min(points.Count, FactSheetFit.TrimmedAttentionCap) : points.Count, result);

        if (decision is not null) doc.Add(decision);

        if (plan.IncludeVigencia && plan.IncludeComparacion && vigenciaYComparacion is not null)
        {
            doc.Add(vigenciaYComparacion);
        }
        else
        {
            if (plan.IncludeVigencia && vigencia is not null) doc.Add(vigencia);
            if (plan.IncludeComparacion && comparacion is not null) doc.Add(comparacion);
        }

        doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));

        // Any first-page item that throws is omitted with a Warning (ids only) — never a failed export.
        T? Build<T>(string item, Func<T?> build) where T : class
        {
            try
            {
                return build();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PDF export: first-page item {Item} omitted for analysis {AnalysisId} result {ResultId}",
                    item, result.AnalysisId, result.ResultId);
                return null;
            }
        }
    }

    // ONE Information line when the printed bullets hold a catalogue alert beside a model alert whose code no
    // AttentionOverlaps list names — a duplicate under a new spelling would look exactly like this. Codes only.
    private void LogUnlistedOverlap(AttentionPoints.Selection selection, int printed, AnalysisResult result)
    {
        try
        {
            if (AttentionPoints.UnlistedOverlap(selection, printed) is not { } codes)
                return;

            _logger.LogInformation(
                "PDF export: page 1 of analysis {AnalysisId} prints catalogue alerts {CatalogueCodes} beside unlisted model alerts {ModelCodes}",
                result.AnalysisId, string.Join(",", codes.CatalogueCodes), string.Join(",", codes.ModelCodes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PDF export: the attention-overlap check failed for analysis {AnalysisId} result {ResultId}",
                result.AnalysisId, result.ResultId);
        }
    }

    private static Div SideBySide(Div left, Div right)
    {
        Table table = new Table(UnitValue.CreatePercentArray([62f, 38f])).UseAllAvailableWidth();
        table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPadding(0).SetPaddingRight(12).Add(left));
        table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPadding(0).Add(right));
        return new Div().SetMargin(0).SetKeepTogether(true).Add(table);
    }

    private static float MeasureHeight(ITextDocument doc, IBlockElement element, float width)
    {
        IRenderer renderer = element.CreateRendererSubTree().SetParent(doc.GetRenderer());
        LayoutResult layout = renderer.Layout(new LayoutContext(new LayoutArea(1, new Rectangle(0, 0, width, 100_000f))));
        return layout.GetOccupiedArea()?.GetBBox().GetHeight() ?? 0f;
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private FactSheetData ReadFactSheetData(
        AnalysisResult result, Analysis analysis, CompanyPdfReport report, ScoreReliabilityResponse? reliability)
    {
        ScoreBreakdownWrapper? breakdown = TryRead<ScoreBreakdownWrapper>(result.ScoreBreakdownJson, JsonOpts);
        LufeData? lufe = TryRead<LufeData>(result.LufeDataJson, SnapshotJsonOpts);

        ScoreAnatomy.Model? anatomy = breakdown is not null && HasCompleteAnatomy(breakdown, result.AiSuggestedScore)
            ? BuildAnatomy(breakdown, result.AiSuggestedScore)
            : null;

        return new FactSheetData
        {
            Result       = result,
            Analysis     = analysis,
            Report       = report,
            Reliability  = reliability,
            Breakdown    = breakdown,
            Ratios       = TryRead<Dictionary<string, double?>>(result.RatiosJson, JsonOpts),
            Bcra         = TryRead<BcraData>(result.BcraDataJson, JsonOpts),
            Lufe         = lufe,
            Anatomy      = anatomy,
            ClosingMonth = ReadClosingMonth(lufe, result.FiscalDataJson),
            NowUtc       = UtcNow(),
        };
    }

    private static T? TryRead<T>(string? json, JsonSerializerOptions options) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    // No closing DATE is stored for a company analysis. The closing MONTH is, when the entity was enriched: LUFE's
    // mes_cierre in the analysis's own snapshot first, then ARCA's MesCierre. Nothing is inferred beyond that.
    internal static int? ReadClosingMonth(LufeData? lufe, string? fiscalDataJson)
    {
        if (lufe?.Entidad?.MesCierre is >= 1 and <= 12 and var lufeMonth)
            return lufeMonth;

        if (string.IsNullOrWhiteSpace(fiscalDataJson)) return null;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(fiscalDataJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "mesCierre", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int month) && month is >= 1 and <= 12)
                    return month;
            }
        }
        catch (JsonException)
        {
            // An unreadable ARCA snapshot simply has no closing month.
        }

        return null;
    }

    // ── 1. Identity ───────────────────────────────────────────────────────────

    private enum IdentityFace
    {
        Figure,
        Word,
        Joiner,
    }

    private static Div BuildIdentity(Fonts fonts, FactSheetData data)
    {
        Rectangle page = PageSize.A4;
        float width    = page.GetWidth() - (2 * SideMargin);

        string name = string.IsNullOrWhiteSpace(data.Report.EntityName) ? "—" : data.Report.EntityName.Trim();

        // Sora 30, shrunk (never truncated) so a long razón social takes at most two lines.
        float size = 30f;
        while (size > 18f && fonts.HeadingBold.GetWidth(name, size) > width * 1.85f)
            size -= 1f;

        Div div = new Div().SetMargin(0).SetMarginBottom(8);
        div.Add(new Paragraph("PyME analizada".ToUpperInvariant())
            .SetFont(fonts.BodyMedium).SetFontSize(7.5f).SetFontColor(Accent).SetCharacterSpacing(0.8f)
            .SetMargin(0).SetMarginBottom(2));
        div.Add(new Paragraph(name)
            .SetFont(fonts.HeadingBold).SetFontSize(size).SetFontColor(Label).SetCharacterSpacing(-0.02f * size)
            .SetMultipliedLeading(1.0f).SetMargin(0).SetMarginBottom(3));

        var line = new Paragraph().SetMargin(0).SetFontSize(9.5f).SetFontColor(Theme.InkSecondary);
        bool first = true;
        // A part is a label and its value's runs, each in its own face: a figure in Mono, a word value in the medium
        // face, joining words in the body face — so "Ejercicio 2025, cierre 11/2025" sets only "2025" and "11/2025"
        // in Mono and ", cierre " like the labels.
        void Part(string label, params (string Text, IdentityFace Face)[] runs)
        {
            if (!first) line.Add(new Text("  ·  ").SetFont(fonts.Body).SetFontColor(LabelSecondary));
            first = false;
            if (label.Length > 0) line.Add(new Text(label + " ").SetFont(fonts.Body));
            foreach ((string text, IdentityFace face) in runs)
            {
                line.Add(face switch
                {
                    IdentityFace.Figure => new Text(text).SetFont(fonts.Mono).SetFontColor(Label),
                    IdentityFace.Word   => new Text(text).SetFont(fonts.BodyMedium).SetFontColor(Label),
                    _                   => new Text(text).SetFont(fonts.Body),
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(data.Report.EntityCuit))
            Part("CUIT", (FormatCuit(data.Report.EntityCuit), IdentityFace.Figure));

        if (data.Breakdown is not null)
            Part("Perfil", (PdfLabels.SectorProfile(data.Breakdown.SectorProfile), IdentityFace.Word));

        string ejercicio = data.Analysis.FiscalYear.ToString(CultureInfo.InvariantCulture);
        if (data.ClosingMonth is { } month)
        {
            Part("Ejercicio",
                (ejercicio, IdentityFace.Figure),
                (", cierre ", IdentityFace.Joiner),
                ($"{month.ToString("00", CultureInfo.InvariantCulture)}/{ejercicio}", IdentityFace.Figure));
        }
        else
        {
            Part("Ejercicio", (ejercicio, IdentityFace.Figure));
        }

        div.Add(line);
        return div;
    }

    // ── 2. Strip ──────────────────────────────────────────────────────────────

    private static Div BuildStrip(Fonts fonts, FactSheetData data)
    {
        Analysis analysis = data.Analysis;

        string amount = analysis.RequestedAmount is { } requested ? Amount(requested, "ARS") : Dash;
        string analyst = data.Report.UserNames.TryGetValue(analysis.RequestedBy, out string? requester)
                         && !string.IsNullOrWhiteSpace(requester)
            ? PdfText.CollapseWhitespace(requester)
            : Dash;

        bool isTrialBalance = false;
        bool hasCorrections = false;
        if (!string.IsNullOrWhiteSpace(data.Result.BalanceJson))
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(data.Result.BalanceJson);
                isTrialBalance = parsed.RootElement.ValueKind == JsonValueKind.Object
                    && parsed.RootElement.TryGetProperty("document_kind", out JsonElement kind)
                    && kind.ValueKind == JsonValueKind.String
                    && TrialBalanceNormalizer.IsTrialBalance(kind.GetString());
            }
            catch (JsonException)
            {
                // Unreadable balance: the strip says "Estados contables", the default document.
            }

            hasCorrections = ManualCorrectionsReader.Read(data.Result.BalanceJson) is not null;
        }

        string status   = PdfLabels.AnalysisStatus(analysis.Status);
        string document = PdfLabels.DocumentKind(isTrialBalance, hasCorrections);

        // Each column as wide as its own label and value need (plus the 8 pt gutter), the slack shared equally, so
        // "Pendiente de revisión" never wraps while Analista has room; the fixed shares when the four do not fit.
        (string Label, string Value, PdfFont Font)[] columns =
        [
            ("Monto solicitado", amount,   fonts.Mono),
            ("Analista",         analyst,  fonts.BodyMedium),
            ("Estado",           status,   fonts.BodyMedium),
            ("Documento",        document, fonts.BodyMedium),
        ];
        float total = PageSize.A4.GetWidth() - (2 * SideMargin);
        float[] widths = StripColumns.Fit(
            columns.Select(c => Math.Max(fonts.Body.GetWidth(c.Label, 7.5f), c.Font.GetWidth(c.Value, 10f)) + StripGutter + StripSlack).ToArray(),
            total,
            StripColumns.FallbackPercents);

        Table table = new Table(UnitValue.CreatePercentArray(widths.Select(w => w / total * 100f).ToArray()))
            .UseAllAvailableWidth()
            .SetMarginBottom(10);

        void Cell(string label, string value, PdfFont font, DeviceRgb color)
        {
            table.AddCell(new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderTop(new SolidBorder(SurfaceBorder, 0.75f))
                .SetBorderBottom(new SolidBorder(SurfaceBorder, 0.75f))
                .SetPaddingTop(5).SetPaddingBottom(5).SetPaddingRight(StripGutter).SetPaddingLeft(0)
                .Add(new Paragraph(label).SetFont(fonts.Body).SetFontSize(7.5f).SetFontColor(LabelSecondary).SetMargin(0))
                .Add(new Paragraph(value).SetFont(font).SetFontSize(10).SetFontColor(color).SetMarginTop(1).SetMarginBottom(0)));
        }

        Cell("Monto solicitado", amount, fonts.Mono, Label);
        Cell("Analista", analyst, fonts.BodyMedium, Label);
        Cell("Estado", status, fonts.BodyMedium, ToneColor(PdfLabels.StatusTone(analysis.Status)));
        Cell("Documento", document, fonts.BodyMedium, Label);

        return new Div().SetMargin(0).Add(table);
    }

    // ── 3. Score card ─────────────────────────────────────────────────────────

    private static Div BuildScoreCard(ITextDocument doc, Fonts fonts, FactSheetData data)
    {
        AnalysisResult result = data.Result;

        Div card = new Div()
            .SetBackgroundColor(Surface)
            .SetBorderRadius(new BorderRadius(16f))
            .SetPaddingTop(10f).SetPaddingBottom(10f).SetPaddingLeft(12f).SetPaddingRight(12f)
            .SetMarginBottom(10)
            .SetKeepTogether(true);

        if (result.AiSuggestedScore is not { } score)
        {
            string why = data.Reliability?.Reasons
                             .FirstOrDefault(r => r.Code == ScoreReliabilityCodes.ScoreUnavailable)?.Message
                         ?? ScoreReliabilityMessages.ScoreUnavailable;

            card.Add(new Paragraph("Sin score").SetFont(fonts.HeadingBold).SetFontSize(18).SetFontColor(ColorMuted).SetMargin(0));
            card.Add(new Paragraph(why).SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorBodyText).SetMarginTop(4).SetMarginBottom(0));
            return card;
        }

        Table layout = new Table(UnitValue.CreatePercentArray([40f, 60f])).UseAllAvailableWidth();

        // Left: gauge, level pill, reliability line.
        Cell left = new Cell().SetBorder(Border.NO_BORDER).SetPadding(0).SetVerticalAlignment(VerticalAlignment.MIDDLE);

        Image gauge = BuildScoreGauge(doc, fonts, score, result.AiSuggestedLevel);
        gauge.SetHeight(gauge.GetImageHeight() * 0.8f).SetWidth(gauge.GetImageWidth() * 0.8f);
        gauge.SetHorizontalAlignment(HorizontalAlignment.CENTER).SetMarginBottom(5);
        left.Add(gauge);

        string level = PdfLabels.RiskLevel(result.AiSuggestedLevel);
        PdfTone levelTone = PdfLabels.RiskTone(result.AiSuggestedLevel);
        float pillWidth = fonts.BodyMedium.GetWidth(level, 9.5f) + 20f;
        left.Add(new Div()
            .SetWidth(pillWidth)
            .SetHorizontalAlignment(HorizontalAlignment.CENTER)
            .SetBackgroundColor(levelTone == PdfTone.Caution ? Theme.Paper : ToneSoft(levelTone))
            .SetBorder(new SolidBorder(ToneColor(levelTone), 0.75f))
            .SetBorderRadius(new BorderRadius(10f))
            .SetPaddingTop(3).SetPaddingBottom(4)
            .SetMarginBottom(5)
            .Add(new Paragraph(level).SetFont(fonts.BodyMedium).SetFontSize(9.5f).SetFontColor(ToneColor(levelTone))
                .SetTextAlignment(TextAlignment.CENTER).SetMargin(0)));

        if (data.Reliability is { } reliability)
        {
            (string label, PdfTone tone, PdfReliabilityIcon icon) = PdfLabels.Reliability(reliability.Level);
            left.Add(new Paragraph()
                .SetTextAlignment(TextAlignment.CENTER).SetMargin(0)
                .Add(DrawReliabilityIcon(doc, icon, ToneColor(tone)).SetMarginRight(4))
                .Add(new Text(label).SetFont(fonts.BodyMedium).SetFontSize(9).SetFontColor(ToneColor(tone))));
        }

        layout.AddCell(left);

        // Right: four KPI tiles.
        Table tiles = new Table(UnitValue.CreatePercentArray([50f, 50f])).UseAllAvailableWidth();
        foreach ((string label, string value, string status, PdfTone tone) in KpiTiles(data))
        {
            tiles.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPadding(3)
                .Add(new Div()
                    .SetBackgroundColor(Theme.Paper)
                    .SetBorder(new SolidBorder(SurfaceBorder, 0.75f))
                    .SetBorderRadius(new BorderRadius(10f))
                    .SetPaddingLeft(10).SetPaddingRight(8).SetPaddingTop(4).SetPaddingBottom(4)
                    .Add(new Paragraph(label).SetFont(fonts.Body).SetFontSize(7.5f).SetFontColor(LabelSecondary).SetMargin(0))
                    .Add(new Paragraph(value).SetFont(fonts.Mono).SetFontSize(13f).SetFontColor(Label).SetMarginTop(0).SetMarginBottom(0)
                        .SetMultipliedLeading(1.1f))
                    .Add(new Paragraph(status).SetFont(fonts.BodyMedium).SetFontSize(8).SetFontColor(ToneColor(tone)).SetMargin(0))));
        }

        layout.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPadding(0).SetPaddingLeft(6)
            .SetVerticalAlignment(VerticalAlignment.MIDDLE).Add(tiles));

        card.Add(layout);
        return card;
    }

    // Liquidez corriente, Endeudamiento, ROE — value in the Portal's unit, status from the breakdown — and the
    // BCRA situación with the Portal's own words.
    private static IEnumerable<(string Label, string Value, string Status, PdfTone Tone)> KpiTiles(FactSheetData data)
    {
        (string, string, string, PdfTone) Ratio(string label, string ratioKey)
        {
            string breakdownKey = RatioToBreakdownKey[ratioKey];
            RatioBreakdownEntry? entry = data.Breakdown?.Breakdown is { } map && map.TryGetValue(breakdownKey, out var e) ? e : null;

            if (data.Ratios is { } ratios && ratios.TryGetValue(ratioKey, out double? value) && value is { } v)
            {
                (string status, PdfTone tone) = entry is null ? ("—", PdfTone.Muted) : PdfLabels.RatioStatus(entry.Status);
                return (label, FormatRatio(ratioKey, v), status, tone);
            }

            if (entry is not null && IsNoCurrentLiabilities(entry))
                return (label, "Sin pasivo corriente", "Liquidez máxima", PdfTone.Good);

            return (label, "—", NotMeasurableLabel(ratioKey, entry), PdfTone.Muted);
        }

        yield return Ratio("Liquidez corriente", "current_ratio");
        yield return Ratio("Endeudamiento", "debt_ratio");
        yield return Ratio("ROE", "roe");

        if (data.Breakdown?.BcraExcluded == true)
            yield return ("Situación BCRA", "—", "BCRA excluido", PdfTone.Muted);
        else if (data.Bcra?.Deuda?.SituacionMaxima is { } sit)
            yield return ("Situación BCRA", sit.ToString(CultureInfo.InvariantCulture), PdfLabels.BcraSituacion(sit), PdfLabels.BcraSituacionTone(sit));
        else if (data.Bcra?.Deuda?.Estado == BcraDeudaConsultaEstado.NoDebtReported)
            yield return ("Situación BCRA", "—", "Sin deuda registrada", PdfTone.Good);
        else
            yield return ("Situación BCRA", "—", "BCRA no consultado", PdfTone.Muted);
    }

    // A 9 pt round icon in the reliability tone: a check, an "i" or an "!" — vector, so no font needs the glyph.
    private static Image DrawReliabilityIcon(ITextDocument doc, PdfReliabilityIcon icon, DeviceRgb color) =>
        DrawVector(doc, 9f, 9f, canvas =>
        {
            canvas.SetFillColor(color).Circle(4.5f, 4.5f, 4.5f).Fill();
            canvas.SetStrokeColor(White).SetFillColor(White).SetLineWidth(1.1f).SetLineCapStyle(RoundCap);
            switch (icon)
            {
                case PdfReliabilityIcon.Check:
                    canvas.MoveTo(2.5f, 4.6f).LineTo(4f, 3.1f).LineTo(6.6f, 5.9f).Stroke();
                    break;
                case PdfReliabilityIcon.Info:
                    canvas.MoveTo(4.5f, 2.3f).LineTo(4.5f, 4.9f).Stroke();
                    canvas.Circle(4.5f, 6.5f, 0.65f).Fill();
                    break;
                default:
                    canvas.MoveTo(4.5f, 4.1f).LineTo(4.5f, 6.8f).Stroke();
                    canvas.Circle(4.5f, 2.4f, 0.65f).Fill();
                    break;
            }
        }).SetMarginBottom(-1f);

    // ── 4. Composition ────────────────────────────────────────────────────────

    private static Div? BuildComposition(ITextDocument doc, Fonts fonts, FactSheetData data)
    {
        ScoreBreakdownWrapper? wrapper = data.Breakdown;
        if (wrapper is null || (wrapper.ScoreRatios is null && wrapper.ScoreBcra is null && wrapper.ScoreMacro is null))
            return null;

        Div div = new Div().SetMargin(0).SetMarginBottom(9).SetKeepTogether(true);
        div.Add(FactSheetHeading(fonts, "Cómo se compone el score"));

        (decimal Ratios, decimal Bcra, decimal Macro) weights = ScoreAnatomy.EffectiveWeights(new ScoreAnatomy.Input
        {
            BcraExcluded          = wrapper.BcraExcluded,
            EffectiveWeightRatios = wrapper.EffectiveWeightRatios,
            EffectiveWeightBcra   = wrapper.EffectiveWeightBcra,
            EffectiveWeightMacro  = wrapper.EffectiveWeightMacro,
        });

        Table table = new Table(UnitValue.CreatePercentArray([30f, 12f, 14f, 44f])).UseAllAvailableWidth();

        void Row(string label, decimal? score, decimal weight, bool excluded)
        {
            Cell Plain(string text, PdfFont font, TextAlignment align, DeviceRgb color) =>
                new Cell().SetBorder(Border.NO_BORDER).SetPaddingTop(1.5f).SetPaddingBottom(1.5f).SetPaddingLeft(0)
                    .SetTextAlignment(align).SetVerticalAlignment(VerticalAlignment.MIDDLE)
                    .Add(new Paragraph(text).SetFont(font).SetFontSize(9).SetFontColor(color).SetMargin(0));

            int? nosis = !excluded && score is { } s ? ScoreScale.ToNosis(s) : null;
            table.AddCell(Plain(label, fonts.Body, TextAlignment.LEFT, ColorBodyText));
            table.AddCell(Plain(nosis?.ToString(CultureInfo.InvariantCulture) ?? "—", fonts.Mono, TextAlignment.RIGHT, Label));
            table.AddCell(Plain($"peso {(excluded ? 0m : weight * 100m).ToString("0.##", EsAr)} %", fonts.Body, TextAlignment.RIGHT, LabelSecondary));

            DeviceRgb fill = nosis is null ? ColorMuted : ToneColor(PdfLabels.ComponentTone(score!.Value, wrapper.LevelCutoffs));
            table.AddCell(new Cell().SetBorder(Border.NO_BORDER).SetPaddingLeft(10).SetVerticalAlignment(VerticalAlignment.MIDDLE)
                .Add(DrawBar(doc, nosis is { } n ? n / 1000.0 : null, fill, 190f)));
        }

        Row("Ratios financieros", wrapper.ScoreRatios, weights.Ratios, excluded: false);
        Row("Contexto BCRA", wrapper.ScoreBcra, weights.Bcra, excluded: wrapper.BcraExcluded == true);
        Row("Variables macro", wrapper.ScoreMacro, weights.Macro, excluded: false);
        div.Add(table);

        if (data.Anatomy is { } anatomy)
        {
            ScoreAdjustmentLine.Line line = ScoreAdjustmentLine.Build(anatomy);
            div.Add(new Paragraph(line.Text)
                .SetFont(fonts.Body).SetFontSize(8.5f).SetFontColor(Theme.InkSecondary).SetMarginTop(3).SetMarginBottom(0));
        }

        return div;
    }

    // ── 5. Puntos de atención ─────────────────────────────────────────────────

    private static AttentionPoints.Facts AttentionFacts(FactSheetData data)
    {
        ScoreBreakdownWrapper? wrapper = data.Breakdown;
        ScoreAnatomy.Model? anatomy = data.Anatomy;

        ScoreAnatomy.Adjustment? floor = anatomy?.Adjustments.FirstOrDefault(a => a.Key == ScoreAnatomy.AdjustmentKey.Floor);
        bool floorApplied = floor is { DeltaNosis: not 0m };

        var criticalLabels = (wrapper?.CriticalRatios ?? [])
            .Select(key => RatioToBreakdownKey.FirstOrDefault(pair => pair.Value == key).Key is { } ratioKey
                           && RatioMetaMap.TryGetValue(ratioKey, out RatioMeta? meta) ? meta.Label : key)
            .ToList();

        var adjustments = (anatomy?.Adjustments ?? [])
            .Where(a => a.Key != ScoreAnatomy.AdjustmentKey.Floor)
            .Select(a => (a.Key, a.Key == ScoreAnatomy.AdjustmentKey.Ventas ? "Ventas declaradas (LUFE)" : "Garantías SGR en mora (CASFOG)", a.DeltaNosis))
            .ToList();

        List<AlertItem> alerts = TryRead<List<AlertItem>>(data.Result.RiskAlerts, JsonOpts) ?? [];

        BcraChequesData? cheques = data.Bcra?.Cheques is { Estado: BcraChequesConsultaEstado.ChequesFound } found ? found : null;

        return new AttentionPoints.Facts
        {
            ReliabilityLevel    = data.Reliability?.Level,
            ReliabilityReasons  = (data.Reliability?.Reasons ?? [])
                .Select(r => new AttentionPoints.ReliabilityReason(r.Severity, r.Message, r.Code, r.Args?.RatioKeys))
                .ToList(),
            FloorAppliedTo      = floorApplied ? anatomy!.AfterFloorDisplay : null,
            CriticalRatioLabels = criticalLabels,
            CriticalRatioKeys   = wrapper?.CriticalRatios ?? [],
            Adjustments         = adjustments,
            BcraSituacion       = wrapper?.BcraExcluded == true ? null : data.Bcra?.Deuda?.SituacionMaxima,
            BcraTrendDelta      = wrapper?.BcraTrendDelta,
            ChequesRechazados   = cheques?.TotalRechazados,
            ChequesImpagos      = cheques?.TotalImpagos,
            Alerts              = alerts.Where(a => a is not null)
                .Select(a => new AttentionPoints.Alert(a.Level, a.Code, a.Title))
                .ToList(),
            CatalogueAlerts     = data.Catalogue.Alerts
                .Select(a => new AttentionPoints.Alert(a.Level, a.Code, a.Title, CatalogueMetricFormat.Inline(a)))
                .ToList(),
            AlertDisplayNames   = data.Report.AlertDisplayNames,
        };
    }

    private static Div BuildAttention(Fonts fonts, IReadOnlyList<string> points)
    {
        Div div = new Div().SetMargin(0).SetMarginBottom(9).SetKeepTogether(true);
        div.Add(FactSheetHeading(fonts, "Puntos de atención"));

        if (points.Count == 0)
        {
            div.Add(new Paragraph(AttentionPoints.None).SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorBodyText).SetMargin(0));
            return div;
        }

        foreach (string point in points)
        {
            div.Add(new Paragraph()
                .SetMargin(0).SetMarginBottom(1).SetPaddingLeft(10).SetFirstLineIndent(-10).SetMultipliedLeading(1.15f)
                .Add(new Text("•  ").SetFont(fonts.Body).SetFontSize(9).SetFontColor(Accent))
                .Add(new Text(point).SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorBodyText)));
        }

        return div;
    }

    // ── 6. Decisión ───────────────────────────────────────────────────────────

    private static Div BuildDecision(Fonts fonts, FactSheetData data)
    {
        Analysis analysis = data.Analysis;
        IReadOnlyDictionary<Guid, string> names = data.Report.UserNames;

        string NameOf(Guid? userId) =>
            userId is { } id && names.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name)
                ? PdfText.CollapseWhitespace(name)
                : Dash;

        Div div = new Div().SetMargin(0).SetMarginBottom(9).SetKeepTogether(true);
        div.Add(FactSheetHeading(fonts, "Decisión"));

        var lines = new List<string>();
        bool isDecided = analysis.Status is AnalysisStatuses.Approved or AnalysisStatuses.Rejected or AnalysisStatuses.PreApproved;

        if (!isDecided)
        {
            lines.Add("Pendiente de revisión.");
        }
        else
        {
            if (analysis.PreApprovedBy is not null || analysis.PreApprovedAt is not null)
            {
                string when   = analysis.PreApprovedAt is { } at ? $" el {ArDate(at)}" : string.Empty;
                string amount = analysis.PreApprovedAmount is { } pre ? $" · {Amount(pre, analysis.PreApprovedCurrency)}" : string.Empty;
                string pending = analysis.Status == AnalysisStatuses.PreApproved ? " — falta confirmar" : string.Empty;
                lines.Add($"Pre-aprobado por {NameOf(analysis.PreApprovedBy)}{when}{amount}{pending}.");
            }

            string reviewedWhen = analysis.ReviewedAt is { } reviewed ? $" el {ArDate(reviewed)}" : string.Empty;
            if (analysis.Status == AnalysisStatuses.Approved)
            {
                string amount = analysis.DecisionAmount is { } approved ? $" · {Amount(approved, analysis.DecisionCurrency)}" : string.Empty;
                lines.Add($"Aprobado por {NameOf(analysis.ReviewedBy)}{reviewedWhen}{amount}.");
            }
            else if (analysis.Status == AnalysisStatuses.Rejected)
            {
                lines.Add($"Rechazado por {NameOf(analysis.ReviewedBy)}{reviewedWhen}.");
            }

            // The operation conditions on the analysis: the confirmed terms, else the proposed ones.
            List<AnalysisDecisionOperation> operations =
                TryRead<List<AnalysisDecisionOperation>>(analysis.ConfirmedOperationsJson, JsonOpts)
                ?? TryRead<List<AnalysisDecisionOperation>>(analysis.ProposedOperationsJson, JsonOpts)
                ?? [];

            IEnumerable<string> conditions = operations.Where(o => o is not null).Select(operation =>
            {
                string type = PdfLabels.CreditLineType(operation.Type) ?? "Operación";
                var details = new List<string>();
                if (operation.ValidUntil is { } until) details.Add($"vigencia hasta {ArDate(until)}");
                if (operation.ASolaFirma) details.Add("a sola firma");
                if (operation.RequiereContragarantiaReal) details.Add("con contragarantía real");
                string suffix = details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})";
                return $"{type} {Amount(operation.Amount, operation.Currency ?? analysis.DecisionCurrency)}{suffix}";
            });

            string conditionsText = string.Join(" · ", conditions);
            if (conditionsText.Length > 0)
                lines.Add($"Condiciones: {conditionsText}.");

            foreach (CreditLine line in data.Report.CreditLines)
                lines.Add($"Línea de crédito originada: {Amount(line.Amount, line.Currency)} · {PdfLabels.CreditLineStatus(line.Status)}.");
        }

        foreach (string line in lines)
            div.Add(new Paragraph(line).SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorBodyText).SetMargin(0).SetMarginBottom(1)
                .SetMultipliedLeading(1.15f));

        return div;
    }

    // ── 7. Vigencia de los datos ──────────────────────────────────────────────

    private static Div? BuildVigencia(Fonts fonts, FactSheetData data)
    {
        var lines = new List<(string Label, string Value)>();

        if (data.ClosingMonth is { } month)
        {
            int year = data.Analysis.FiscalYear;
            DateTime now = ArgentineTime.ToLocal(data.NowUtc);
            string ago = BalanceAge.Suffix(year, month, now);
            lines.Add(("Balance", $"cierre {month.ToString("00", CultureInfo.InvariantCulture)}/{year.ToString(CultureInfo.InvariantCulture)}{ago}"));
        }

        if (data.Bcra?.Deuda?.PeriodoConsultado is { Length: > 0 } periodo)
            lines.Add(("BCRA", $"período {FormatPeriodo(periodo)}"));

        if (VentanaReproducible(data.Breakdown?.VentasBase, data.Lufe?.VentasComprasMensual, data.Bcra?.Macro?.InflacionMensualSerie)
            is { } ventana)
            lines.Add(("LUFE", ventana));

        if (lines.Count == 0) return null;

        Div div = new Div().SetMargin(0).SetMarginBottom(9).SetKeepTogether(true);
        div.Add(FactSheetHeading(fonts, "Vigencia de los datos"));

        foreach ((string label, string value) in lines)
        {
            div.Add(new Paragraph()
                .SetMargin(0).SetMarginBottom(1).SetMultipliedLeading(1.15f)
                .Add(new Text(label + ": ").SetFont(fonts.BodyMedium).SetFontSize(9).SetFontColor(Label))
                .Add(new Text(value).SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorBodyText)));
        }

        return div;
    }

    // ── 8. Comparación ────────────────────────────────────────────────────────

    private static Div? BuildComparacion(Fonts fonts, FactSheetData data)
    {
        if (data.Report.Previous is not { Score: { } previous } side) return null;

        int? current = data.Report.Current?.Score
                       ?? (data.Result.AiSuggestedScore is { } score ? ScoreScale.ToNosis(score) : null);
        if (current is null) return null;

        int delta = current.Value - previous;
        string signed = delta > 0 ? $"+{delta}" : delta < 0 ? $"−{Math.Abs(delta)}" : "sin cambios";

        string text = $"Anterior: {previous.ToString(CultureInfo.InvariantCulture)} · Ejercicio "
                      + $"{side.FiscalYear.ToString(CultureInfo.InvariantCulture)} → {current.Value.ToString(CultureInfo.InvariantCulture)} ({signed})";

        Div div = new Div().SetMargin(0).SetMarginBottom(6).SetKeepTogether(true);
        div.Add(FactSheetHeading(fonts, "Comparación"));
        div.Add(new Paragraph(text).SetFont(fonts.Body).SetFontSize(9).SetFontColor(ColorBodyText).SetMargin(0));
        return div;
    }

    private static Paragraph FactSheetHeading(Fonts fonts, string title) =>
        new Paragraph(title)
            .SetFont(fonts.HeadingBold).SetFontSize(10.5f).SetFontColor(Label)
            .SetMargin(0).SetMarginBottom(3)
            .SetKeepWithNext(true);
}
