// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Globalization;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Servicios.Pdf;
using iText.Kernel.Colors;
using iText.Kernel.Events;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Pdf.Xobject;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using ITextDocument = iText.Layout.Document;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The page furniture both reports share: margins, the full-bleed header band on page 1, the slim running
/// header on every later page, the running footer with its page count, the document information, the
/// outline, the section headings and the closing block. Colours come from the palette record and the mark
/// from the brand slot only, so a later theme or client branding does not touch this layout.
/// </summary>
public sealed partial class PdfExportService
{
    // 17 mm side margins; the bottom band holds the running footer; pages 2+ start under the slim header.
    private const float MmToPt       = 72f / 25.4f;
    private const float SideMargin   = 17f * MmToPt;
    private const float TopMargin    = 40f;
    private const float BottomMargin = 54f;

    // The page-1 header band: ~20 mm, full bleed, a 6 pt accent rule at the very top.
    private const float BandHeight     = 20f * MmToPt;
    private const float BandAccentRule = 6f;
    private const float BandGap        = 16f;

    /// <summary>The space page 1 reserves above its content so the band never overlaps it.</summary>
    private static float Page1Spacer => BandHeight + BandGap - TopMargin;

    private const string ReportTitleCompany = "Informe crediticio";
    private const string ReportTitlePerson  = "Evaluación crediticia";

    private static void ApplyPageSetup(ITextDocument doc) =>
        doc.SetMargins(TopMargin, SideMargin, BottomMargin, SideMargin);

    // Both themes set the document's default text colour to their ink: a paragraph that names no colour would
    // otherwise print in iText's default black — invisible on the dark theme's navy paper, and off-palette on the
    // light one, whose body text is Ink (#0B1222), not #000.
    // The palette current for this render (PdfRenderScope) already is the theme's.
    private static void ApplyThemeDefaults(ITextDocument doc) => doc.SetFontColor(Theme.Ink);

    // ── Export timestamps (America/Argentina/Buenos_Aires) ────────────────────
    // ArgentineTime keeps the IANA id → Windows id → fixed UTC-03:00 chain and never throws.

    /// <summary>"8 de noviembre de 2026 · 17:05" — the long es-AR form the header and the closing block print.</summary>
    internal static string FormatLongTimestamp(DateTime utc)
    {
        DateTime local = ArgentineTime.ToLocal(utc);
        return local.ToString("d 'de' MMMM 'de' yyyy", EsAr) + " · " + local.ToString("HH:mm", EsAr);
    }

    private string FormatExportTimestamp() => FormatLongTimestamp(UtcNow());

    // ── Document information ──────────────────────────────────────────────────
    // Title, Author, Subject and Creator only. The Producer entry is iText's own and is NEVER set, hidden or
    // overridden here (iText is AGPL; its licensing is handled separately).
    private static void ApplyDocumentInfo(PdfDocument pdf, string title, string subject, string? tenantDisplayName)
    {
        PdfDocumentInfo info = pdf.GetDocumentInfo();
        info.SetTitle(title);
        info.SetAuthor(string.IsNullOrWhiteSpace(tenantDisplayName) ? Brand.ProductName : tenantDisplayName.Trim());
        info.SetSubject(subject);
        info.SetCreator(Brand.ProductName);

        // Open with the bookmarks panel: one entry per rendered section.
        pdf.GetCatalog().SetPageMode(PdfName.UseOutlines);
    }

    // ── Outline ───────────────────────────────────────────────────────────────

    /// <summary>Adds a bookmark that jumps to <paramref name="target"/> when it is rendered.</summary>
    private static void AddOutlineEntry(ITextDocument doc, Paragraph target, string title)
    {
        PdfDocument pdf  = doc.GetPdfDocument();
        PdfOutline  root = pdf.GetOutlines(false);
        string      name = "sec-" + (root.GetAllChildren().Count + 1).ToString(CultureInfo.InvariantCulture);

        target.SetDestination(name);
        root.AddOutline(title).AddDestination(PdfDestination.MakeDestination(new PdfString(name)));
    }

    // ── Section headings ──────────────────────────────────────────────────────
    // A Sora Bold title on a full-width hairline. Kept with the block that follows, so a heading never
    // ends a page on its own, and bookmarked. Every section routes through this one helper.
    private static void AddSectionHeading(ITextDocument doc, Fonts fonts, string title)
    {
        Paragraph heading = SectionHeadingParagraph(fonts, title);
        AddOutlineEntry(doc, heading, title);
        doc.Add(heading);
    }

    private static Paragraph SectionHeadingParagraph(Fonts fonts, string title) =>
        new Paragraph(title)
            .SetFont(fonts.HeadingBold)
            .SetFontSize(13)
            .SetFontColor(Label)
            .SetMarginTop(18)
            .SetMarginBottom(10)
            .SetPaddingBottom(4)
            .SetBorderBottom(new SolidBorder(SurfaceBorder, 0.75f))
            .SetKeepWithNext(true);

    // One sub-heading treatment, also kept with what follows it.
    private static void AddSubHeading(ITextDocument doc, Fonts fonts, string text) =>
        doc.Add(SubHeadingParagraph(fonts, text).SetKeepWithNext(true));

    private static Paragraph SubHeadingParagraph(Fonts fonts, string text) =>
        new Paragraph(text)
            .SetFont(fonts.HeadingBold)
            .SetFontSize(10)
            .SetFontColor(Label)
            .SetMarginTop(8)
            .SetMarginBottom(4);

    /// <summary>
    /// A sub-heading and the short block it introduces as ONE keep-together unit. iText's keep-with-next does
    /// not chain (section heading → sub-heading → kept-together table), so a sub-section whose block must not
    /// split is grouped instead; the section heading above it then keeps with the whole unit.
    /// </summary>
    private static Div SubSection(Fonts fonts, string title) =>
        new Div().SetKeepTogether(true).Add(SubHeadingParagraph(fonts, title));

    // ── Persona title + meta row ──────────────────────────────────────────────
    // The persona report keeps its identification row under the shared band: a title, then label-over-value
    // cells, in Mono unless IsText (a name reads in the body face).
    private static void AddDocumentHeader(
        ITextDocument doc,
        Fonts    fonts,
        string   reportTitle,
        IReadOnlyList<(string Label, string Value, bool IsText)> meta)
    {
        doc.Add(new Paragraph(reportTitle)
            .SetFont(fonts.HeadingBold)
            .SetFontSize(18)
            .SetFontColor(Label)
            .SetMarginTop(0)
            .SetMarginBottom(8));

        Table metaTable = new Table(UnitValue.CreatePercentArray(meta.Count))
            .UseAllAvailableWidth()
            .SetMarginBottom(16);

        foreach ((string label, string value, bool isText) in meta)
            AddMetaCell(metaTable, fonts, label, value, isText);

        doc.Add(metaTable);
    }

    private static void AddMetaCell(Table table, Fonts fonts, string label, string value, bool isText = false)
    {
        table.AddCell(new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBorderBottom(new SolidBorder(SurfaceBorder, 0.75f))
            .SetPaddingBottom(6)
            .SetPaddingRight(8)
            .Add(new Paragraph(label)
                .SetFont(fonts.Body)
                .SetFontSize(8)
                .SetFontColor(LabelSecondary)
                .SetMargin(0))
            .Add(new Paragraph(value)
                .SetFont(isText ? fonts.BodyMedium : fonts.Mono)
                .SetFontSize(10)
                .SetFontColor(Label)
                .SetMarginTop(2)));
    }

    // ── Cierre del informe ────────────────────────────────────────────────────
    // The closing block: heading, disclaimer, export date and id — ONE keep-together block, so the last page
    // is never two orphan lines and the heading never sits alone at the foot of a page.
    private static void AddFooterSection(ITextDocument doc, Fonts fonts, string shortId, string exportedAt)
    {
        const string Title = "Cierre del informe";

        Paragraph heading = SectionHeadingParagraph(fonts, Title);
        AddOutlineEntry(doc, heading, Title);

        Div closing = new Div().SetKeepTogether(true);
        closing.Add(heading);
        closing.Add(new Paragraph(
                "Este informe es un insumo para la decisión crediticia y no la reemplaza. Las cifras provienen de los " +
                "estados contables presentados y de las fuentes consultadas en las fechas indicadas.")
            .SetFont(fonts.Body)
            .SetFontSize(9)
            .SetFontColor(ColorBodyText)
            .SetMarginBottom(6));
        closing.Add(new Paragraph($"{Brand.ProductName} · Exportado el {exportedAt} · ID {shortId}")
            .SetFont(fonts.Body)
            .SetFontSize(8)
            .SetFontColor(ColorMuted)
            .SetMargin(0));

        doc.Add(closing);
    }

    // ── Running furniture: header band, slim header, footer ───────────────────
    // Painted on END_PAGE of every page, outside the content box. Page 1 gets the full-bleed band; pages 2+
    // the slim running header. The total page count is unknown until the document closes, so each page's
    // "Página X de Y" is a small PdfFormXObject of its own, placed with its right edge on the margin and
    // stamped once the total is known (WriteTotalPages), just before Close — the document is never rendered
    // twice, and the line ends on the margin for any total.
    private sealed class PageFurnitureHandler : IEventHandler
    {
        private const float FooterSize      = 7f;
        private const float HeaderTitleSize = 10.5f;
        private const float HeaderDateSize  = 8f;
        private const float RunningSize     = 7.5f;
        private const float FooterY         = 30f;

        // The title and date baselines, from the band's middle. They are where the title/date paragraphs landed when
        // iText placed them (middle + 1.5 and middle − 10.5, bottom on the win descender), kept to the hundredth so
        // the right-hand block does not move; drawing them on explicit baselines makes the lockup arithmetic exact.
        private const float HeaderTitleBaselineAboveMiddle = 4.86f;
        private const float HeaderDateBaselineBelowMiddle  = 8.02f;

        // The Financia lockup: the symbol's square and the wordmark, in the Portal's FLogo proportion (the wordmark
        // ≈ 0.62 of the square), tracked −0.045 em.
        private const float SymbolSize       = 26f;
        private const float WordmarkSize     = 16f;
        private const float WordmarkTracking = -0.045f;

        private readonly PdfDocument    _pdf;
        private readonly Fonts          _fonts;
        private readonly string         _reportTitle;
        private readonly string?        _runningName;
        private readonly string         _shortId;
        private readonly string         _exportedAt;
        private readonly float          _pageNumberWidth;
        private readonly Dictionary<int, PdfFormXObject> _pageNumbers = [];
        private readonly bool           _paintsPaper;
        private readonly ClientPdfBrand? _clientBrand;

        /// <param name="clientBrand">
        /// The client's logo slot for page 1's header, or null for the Financia symbol + wordmark. The footer, the
        /// running header and every other piece of furniture are the same either way.
        /// </param>
        public PageFurnitureHandler(
            PdfDocument pdf, Fonts fonts, string reportTitle, string? runningName, string shortId, string exportedAt,
            PdfTheme theme, ClientPdfBrand? clientBrand)
        {
            _pdf              = pdf;
            _fonts            = fonts;
            _reportTitle      = reportTitle;
            _runningName      = string.IsNullOrWhiteSpace(runningName) ? null : runningName.Trim();
            _shortId          = shortId;
            _exportedAt       = exportedAt;
            _pageNumberWidth  = fonts.Body.GetWidth("Página 99999 de 99999", FooterSize);

            // White paper is the page itself, so the light theme paints nothing (and stays byte-identical);
            // the dark theme paints its paper under everything on every page. Decided by the theme, not by the
            // palette instance: a light palette carrying a client accent is a different record, still on white.
            _paintsPaper      = theme == PdfTheme.Dark;
            _clientBrand      = clientBrand;
        }

        public void HandleEvent(Event @event)
        {
            var       docEvent = (PdfDocumentEvent)@event;
            PdfPage   page     = docEvent.GetPage();
            Rectangle size     = page.GetPageSize();
            int       pageNum  = _pdf.GetPageNumber(page);

            if (_paintsPaper)
                PaintPaper(page, size);

            var pdfCanvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), _pdf);
            var canvas    = new Canvas(pdfCanvas, size);

            if (pageNum == 1)
                DrawHeaderBand(pdfCanvas, canvas, size);
            else
                DrawRunningHeader(pdfCanvas, canvas, size);

            DrawFooter(pdfCanvas, canvas, size, pageNum);
            canvas.Close();
        }

        // The full page in the palette's paper, in a content stream placed BEFORE the page's own, so every
        // fill, rule and glyph of the page draws over it.
        private void PaintPaper(PdfPage page, Rectangle size)
        {
            new PdfCanvas(page.NewContentStreamBefore(), page.GetResources(), _pdf)
                .SaveState()
                .SetFillColor(Theme.Paper)
                .Rectangle(size.GetLeft(), size.GetBottom(), size.GetWidth(), size.GetHeight())
                .Fill()
                .RestoreState()
                .Release();
        }

        // Full bleed, no box: a 6 pt accent rule at the very top, the brand slot left, the report title and
        // the export date right, and one hairline under the band from edge to edge. The brand slot is the
        // Financia symbol + wordmark, or — only when the tenant has a usable logo — the client's logo and name.
        private void DrawHeaderBand(PdfCanvas pdfCanvas, Canvas canvas, Rectangle size)
        {
            float top    = size.GetTop();
            float left   = size.GetLeft() + SideMargin;
            float right  = size.GetRight() - SideMargin;
            float middle = top - BandAccentRule - ((BandHeight - BandAccentRule) / 2f);

            pdfCanvas.SaveState()
                .SetFillColor(Accent)
                .Rectangle(size.GetLeft(), top - BandAccentRule, size.GetWidth(), BandAccentRule)
                .Fill()
                .RestoreState();

            float titleBaseline = middle + HeaderTitleBaselineAboveMiddle;
            float dateBaseline  = middle - HeaderDateBaselineBelowMiddle;

            if (_clientBrand is null)
            {
                // The lockup's optical centre (the wordmark's cap midline, which the symbol's ink shares) goes on
                // the centre of the title/date block, from the title's cap top to the date's baseline.
                float blockCentre = HeaderLockup.BlockCentre(
                    titleBaseline, CapHeight(_fonts.HeadingBold, HeaderTitleSize), dateBaseline);
                DrawFinanciaBrand(pdfCanvas, left, blockCentre);
            }
            else
            {
                float limit = ClientPdfBrand.BrandRightLimit(
                    right,
                    _fonts.HeadingBold.GetWidth(_reportTitle, HeaderTitleSize),
                    _fonts.Body.GetWidth(_exportedAt, HeaderDateSize));
                _clientBrand.Draw(pdfCanvas, canvas, _fonts.HeadingBold, Label, left, middle, limit,
                    onPlate: _paintsPaper);
            }

            ShowText(pdfCanvas, _fonts.HeadingBold, HeaderTitleSize, Label, _reportTitle,
                right - _fonts.HeadingBold.GetWidth(_reportTitle, HeaderTitleSize), titleBaseline);
            ShowText(pdfCanvas, _fonts.Body, HeaderDateSize, LabelSecondary, _exportedAt,
                right - _fonts.Body.GetWidth(_exportedAt, HeaderDateSize), dateBaseline);

            pdfCanvas.SaveState()
                .SetStrokeColor(SurfaceBorder).SetLineWidth(0.75f)
                .MoveTo(size.GetLeft(), top - BandHeight).LineTo(size.GetRight(), top - BandHeight).Stroke()
                .RestoreState();
        }

        // The Financia brand slot: the symbol in the accent and the wordmark (Sora Bold, one ink), laid out by the
        // FLogo rule (HeaderLockup): the symbol's ink on the wordmark's cap midline, half a cap from the wordmark's
        // first stroke, its left edge on the margin — and that midline on `midline`. Metrics come from the face
        // itself (its cap height, the first glyph's side bearing), so a fallback face still lines up.
        private void DrawFinanciaBrand(PdfCanvas pdfCanvas, float left, float midline)
        {
            string wordmark = Brand.Wordmark;
            HeaderLockup.Placement placement = HeaderLockup.Layout(
                left, midline, Brand.SymbolInk(SymbolSize),
                CapHeight(_fonts.HeadingBold, WordmarkSize),
                LeftBearing(_fonts.HeadingBold, wordmark, WordmarkSize));

            Brand.DrawSymbol(pdfCanvas, placement.SymbolX, placement.SymbolY, SymbolSize, Accent);
            ShowText(pdfCanvas, _fonts.HeadingBold, WordmarkSize, Label, wordmark,
                placement.WordmarkX, placement.WordmarkBaseline, WordmarkTracking * WordmarkSize);
        }

        // One run of text with its baseline at (x, baseline) — no layout, so where it lands is exactly what was asked.
        private static void ShowText(
            PdfCanvas pdfCanvas, PdfFont font, float size, Color color, string text, float x, float baseline,
            float characterSpacing = 0f)
        {
            pdfCanvas.SaveState()
                .BeginText()
                .SetFontAndSize(font, size)
                .SetFillColor(color)
                .SetCharacterSpacing(characterSpacing)
                .MoveText(x, baseline)
                .ShowText(text)
                .EndText()
                .RestoreState();
        }

        // The face's cap height at `size` points.
        private static float CapHeight(PdfFont font, float size) =>
            font.GetFontProgram().GetFontMetrics().GetCapHeight() * size / 1000f;

        // The left side bearing of the first glyph of `text` at `size` points (0 when the face reports no box).
        private static float LeftBearing(PdfFont font, string text, float size) =>
            text.Length > 0 && font.GetFontProgram().GetGlyph(text[0])?.GetBbox() is { Length: 4 } box
                ? box[0] * size / 1000f
                : 0f;

        // Pages 2+: the company (or person) left, the report title right, small, over a hairline.
        private void DrawRunningHeader(PdfCanvas pdfCanvas, Canvas canvas, Rectangle size)
        {
            float top   = size.GetTop();
            float left  = size.GetLeft() + SideMargin;
            float right = size.GetRight() - SideMargin;
            float y     = top - 24f;

            if (_runningName is not null)
            {
                canvas.ShowTextAligned(
                    new Paragraph(_runningName).SetFont(_fonts.BodyMedium).SetFontSize(RunningSize)
                        .SetFontColor(LabelSecondary).SetMargin(0),
                    left, y, TextAlignment.LEFT);
            }

            canvas.ShowTextAligned(
                new Paragraph(_reportTitle).SetFont(_fonts.Body).SetFontSize(RunningSize)
                    .SetFontColor(LabelSecondary).SetMargin(0),
                right, y, TextAlignment.RIGHT);

            pdfCanvas.SaveState()
                .SetStrokeColor(SurfaceBorder).SetLineWidth(0.5f)
                .MoveTo(left, y - 5f).LineTo(right, y - 5f).Stroke()
                .RestoreState();
        }

        // Left: "Elaborado con Financia · Confidencial · ID …", text only — no symbol: at footer size any version
        // of it sits below the brand manual's minimum. Right: "Página X de Y" as ONE run, one space before the
        // total, ending on the right margin like the header's title and date. The page's form is placed now and
        // filled by WriteTotalPages, when Y is known.
        private void DrawFooter(PdfCanvas pdfCanvas, Canvas canvas, Rectangle size, int pageNum)
        {
            float left  = size.GetLeft() + SideMargin;
            float right = size.GetRight() - SideMargin;

            canvas.ShowTextAligned(
                new Paragraph($"Elaborado con {Brand.Wordmark} · Confidencial · ID {_shortId}")
                    .SetFont(_fonts.Body).SetFontSize(FooterSize).SetFontColor(LabelTertiary).SetMargin(0),
                left, FooterY, TextAlignment.LEFT);

            pdfCanvas.AddXObjectAt(PageNumberForm(pageNum), right - _pageNumberWidth, FooterY);
        }

        // The form that carries page `pageNum`'s "Página X de Y", created on first use.
        private PdfFormXObject PageNumberForm(int pageNum)
        {
            if (!_pageNumbers.TryGetValue(pageNum, out PdfFormXObject? form))
            {
                form = new PdfFormXObject(new Rectangle(0, 0, _pageNumberWidth, FooterSize + 2f));
                _pageNumbers[pageNum] = form;
            }

            return form;
        }

        /// <summary>
        /// Stamps every page's "Página X de Y", right-aligned on the margin, once every page exists. The last page
        /// ends (END_PAGE) only when the document closes, after this: its form is stamped here and placed then.
        /// </summary>
        public void WriteTotalPages(PdfDocument pdf)
        {
            int pages    = pdf.GetNumberOfPages();
            string total = pages.ToString(EsAr);
            for (int page = 1; page <= pages; page++)
            {
                var canvas = new Canvas(PageNumberForm(page), pdf);
                canvas.ShowTextAligned(
                    new Paragraph("Página " + page.ToString(EsAr) + " de " + total)
                        .SetFont(_fonts.Body).SetFontSize(FooterSize).SetFontColor(LabelTertiary).SetMargin(0),
                    _pageNumberWidth, 0, TextAlignment.RIGHT);
                canvas.Close();
            }
        }
    }
}
