// Project : Api.Web.FinanceIA
// Created : 2026-05-09

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios.Pdf;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Events;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Logging;
using ITextDocument = iText.Layout.Document;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// iText7-based implementation of <see cref="IPdfExportService"/>.
/// The balance report header does not print the entity name or CUIT. (The persona humana report, in
/// PdfExportService.Persona.cs, is a different document: its header names the person and the CUIT by design.) Note, however, that model-authored
/// prose sections (e.g. the executive summary) are rendered verbatim and may name the entity — the
/// document must not claim otherwise.
/// All generation is synchronous (CPU-bound) and wrapped in Task.FromResult.
/// </summary>
public sealed partial class PdfExportService : IPdfExportService
{
    // ── Injected dependencies ─────────────────────────────────────────────────
    // The cash-flow reading is computed at read time (never persisted), which is why the PDF has
    // never contained it; the reconciliation result is likewise recomputed, exactly as the read
    // path (AnalysisController.GetById) does. The logger makes unrecognised section keys non-silent.

    private readonly ICashFlowNarrativeService      _cashFlowNarrativeService;
    private readonly ICashFlowReconciliationService _cashFlowReconciliationService;
    private readonly IAlertRatioReconciliationService _alertRatioReconciliationService;
    private readonly ILogger<PdfExportService>      _logger;

    /// <summary>Initializes a new instance of <see cref="PdfExportService"/>.</summary>
    public PdfExportService(
        ICashFlowNarrativeService        cashFlowNarrativeService,
        ICashFlowReconciliationService   cashFlowReconciliationService,
        IAlertRatioReconciliationService alertRatioReconciliationService,
        ILogger<PdfExportService>        logger)
    {
        _cashFlowNarrativeService        = cashFlowNarrativeService;
        _cashFlowReconciliationService   = cashFlowReconciliationService;
        _alertRatioReconciliationService = alertRatioReconciliationService;
        _logger                          = logger;
    }

    /// <summary>
    /// The score-reliability evaluation the report uses — the ONE adapter every surface goes through. A seam
    /// for tests only (to prove a failing evaluation never fails the export); production never sets it.
    /// </summary>
    internal Func<ScoreReliabilityRow, string?, ScoreReliabilityVerdict> ReliabilityEvaluator { get; init; }
        = ScoreReliabilityAdapter.Evaluate;

    /// <summary>
    /// The clock every printed "now" reads (the export timestamp, the balance's age). A seam for tests only, so a
    /// render is reproducible and its content streams can be fingerprinted; production never sets it.
    /// </summary>
    internal Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;

    // ── Palette and brand ─────────────────────────────────────────────────────
    // Every colour comes from ONE PdfPalette record and every brand mark from ONE IPdfBrand; nothing below
    // holds a colour literal. The palette is per export (PdfTheme.Light by default, or Dark): BuildPdf and
    // BuildPersonPdf make it current for the whole render through PdfRenderScope, so concurrent exports of
    // different themes never mix, and outside a render it reads Light. The names below are the semantic
    // aliases the renderers have always used, resolved through the current record on every read.
    private static PdfPalette Theme => PdfRenderScope.Current;
    private static readonly IPdfBrand  Brand = FinanciaPdfBrand.Instance;

    private static DeviceRgb Accent         => Theme.Accent;
    private static DeviceRgb AccentSoft     => Theme.AccentSoft;
    private static DeviceRgb Label          => Theme.Ink;            // body text
    private static DeviceRgb LabelSecondary => Theme.InkMuted;       // captions, units
    private static DeviceRgb LabelTertiary  => Theme.InkMuted;       // running header / footer
    private static DeviceRgb Success        => Theme.Success;
    private static DeviceRgb SuccessSoft    => Theme.SuccessSoft;
    private static DeviceRgb Warning        => Theme.Caution;        // amber in both themes
    private static DeviceRgb WarningSoft    => Theme.CautionSoft;
    private static DeviceRgb Danger         => Theme.Danger;
    private static DeviceRgb DangerSoft     => Theme.DangerSoft;
    private static DeviceRgb Surface        => Theme.Surface;        // zebra rows / cards
    private static DeviceRgb SurfaceBorder  => Theme.Hairline;       // hairlines
    private static DeviceRgb White          => Theme.Paper;          // text on a filled bar segment
    private static DeviceRgb CashOutflow    => Theme.CashOutflow;
    private static DeviceRgb Equity         => Theme.Equity;
    private static DeviceRgb Orange         => Theme.Orange;

    private static DeviceRgb ColorAccent      => Accent;
    private static DeviceRgb ColorSectionBg   => Surface;
    private static DeviceRgb ColorTableHead   => Surface;            // table header row: surface tint, ink text
    private static DeviceRgb ColorTableHeadFg => Theme.InkSecondary;
    private static DeviceRgb ColorOk          => Success;
    private static DeviceRgb ColorWarn        => Warning;
    private static DeviceRgb ColorDanger      => Danger;
    private static DeviceRgb ColorBorder      => SurfaceBorder;
    private static DeviceRgb ColorBodyText    => Label;
    private static DeviceRgb ColorMuted       => LabelSecondary;

    private static DeviceRgb ToneColor(PdfTone tone) => tone switch
    {
        PdfTone.Good    => Theme.Success,
        PdfTone.Caution => Theme.Caution,
        PdfTone.Bad     => Theme.Danger,
        PdfTone.Neutral => Theme.InkSecondary,
        _               => Theme.InkMuted
    };

    private static DeviceRgb ToneSoft(PdfTone tone) => tone switch
    {
        PdfTone.Good => Theme.SuccessSoft,
        PdfTone.Bad  => Theme.DangerSoft,
        _            => Theme.Surface
    };

    // ── JSON options ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    // A stored vendor snapshot (AnalysisResults.CasfogDataJson) is a verbatim copy of the entity's
    // column, so it is read with the SAME options the rest of the project reads stored snapshots
    // with — including AllowReadingFromString, so a numeric leaf persisted as a string still parses.
    // See EntitiesController.SnapshotJsonOpts and AnalysisController.SnapshotJsonOpts; keeping a
    // separate instance leaves JsonOpts (which serves every other section) untouched.
    private static readonly JsonSerializerOptions SnapshotJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString
    };

    // ── Culture ───────────────────────────────────────────────────────────────
    // Every numeric/date format call in this file passes EsAr so output never follows the
    // (currently en-US) server culture. This is formatting only — no value or rounding changes.
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    // ── All valid section keys ────────────────────────────────────────────────
    // Keys the service can render — exactly the set ExportController.ValidSectionKeys accepts. Two reports,
    // two disjoint key sets: the balance report (BuildPdf) and the persona humana report (BuildPersonPdf).

    // Internal rather than private (InternalsVisibleTo: Api.Web.FinanceIA.Tests) so a test can pin it
    // against ExportController.ValidSectionKeys — the drift these two lists can develop is otherwise
    // only visible as the runtime Warning logged at the bottom of BuildPdf.
    internal static readonly IReadOnlyList<string> BalanceSections =
        ["balance", "ratios", "ratio_breakdown", "executive_summary", "risk", "bcra", "lufe", "casfog", "anomalies", "cash_flow", "footer"];

    // The persona humana report, in the order the Portal shows the result (and the order it renders in).
    internal static readonly IReadOnlyList<string> PersonaSections =
        ["persona_resumen", "persona_elegibilidad", "persona_filtros", "persona_capacidad", "persona_solvencia",
         "persona_fiadores", "persona_coherencia", "persona_cobertura", "persona_fuentes", "persona_decision"];

    internal static readonly IReadOnlyList<string> AllSections = [.. BalanceSections, .. PersonaSections];

    // ── Ratio key → ScoreBreakdown key mapping ────────────────────────────────

    private static readonly Dictionary<string, string> RatioToBreakdownKey = new()
    {
        ["current_ratio"]     = "liquidez_corriente",
        ["acid_test"]         = "prueba_acida",
        ["debt_ratio"]        = "endeudamiento",
        ["leverage"]          = "leverage",
        ["roe"]               = "roe",
        ["roa"]               = "roa",
        ["net_margin"]        = "margen_neto",
        ["interest_coverage"] = "cobertura_intereses",
    };

    // ── Ratio display metadata ────────────────────────────────────────────────

    private sealed record RatioMeta(string Label);

    private static readonly Dictionary<string, RatioMeta> RatioMetaMap = new()
    {
        ["current_ratio"]      = new("Liquidez corriente"),
        ["acid_test"]          = new("Prueba \u00e1cida"),
        ["debt_ratio"]         = new("Endeudamiento"),
        ["leverage"]           = new("Apalancamiento"),
        ["roe"]                = new("ROE"),
        ["roa"]                = new("ROA"),
        ["net_margin"]         = new("Margen neto"),
        ["interest_coverage"]  = new("Cobertura de intereses"),
    };

    // ── Private DTOs for JSON deserialization ────────────────────────────────

    private sealed class BalanceSheet
    {
        [JsonPropertyName("assets")]                   public decimal? TotalAssets            { get; set; }
        [JsonPropertyName("liabilities")]              public decimal? TotalLiabilities       { get; set; }
        [JsonPropertyName("equity")]                   public decimal? TotalEquity            { get; set; }
        [JsonPropertyName("current_assets")]           public decimal? CurrentAssets          { get; set; }
        [JsonPropertyName("non_current_assets")]       public decimal? NonCurrentAssets       { get; set; }
        [JsonPropertyName("current_liabilities")]      public decimal? CurrentLiabilities     { get; set; }
        [JsonPropertyName("non_current_liabilities")]  public decimal? NonCurrentLiabilities  { get; set; }
        [JsonPropertyName("net_revenue")]              public decimal? NetRevenue             { get; set; }
        [JsonPropertyName("net_profit")]               public decimal? NetProfit              { get; set; }
    }

    private sealed class ScoreBreakdownWrapper
    {
        [JsonPropertyName("breakdown")]
        public Dictionary<string, RatioBreakdownEntry>? Breakdown { get; set; }

        [JsonPropertyName("scoreRatios")]
        public decimal? ScoreRatios { get; set; }

        [JsonPropertyName("scoreBcra")]
        public decimal? ScoreBcra { get; set; }

        [JsonPropertyName("scoreMacro")]
        public decimal? ScoreMacro { get; set; }

        [JsonPropertyName("appliedFloor")]
        public decimal? AppliedFloor { get; set; }

        [JsonPropertyName("scoreBeforeFloor")]
        public decimal? ScoreBeforeFloor { get; set; }

        [JsonPropertyName("criticalRatios")]
        public List<string>? CriticalRatios { get; set; }

        // Scoring algorithm 10. Absent on every earlier row, which floored any loss.
        [JsonPropertyName("profitabilityFloorWaived")]
        public bool? ProfitabilityFloorWaived { get; set; }

        // Scoring algorithm 11. Absent on every earlier row, which floored any critical acid test.
        [JsonPropertyName("acidTestFloorWaived")]
        public bool? AcidTestFloorWaived { get; set; }

        [JsonPropertyName("sectorProfile")]
        public string? SectorProfile { get; set; }

        [JsonPropertyName("bcraTrendDelta")]
        public int? BcraTrendDelta { get; set; }

        [JsonPropertyName("bcraTrendAdj")]
        public decimal? BcraTrendAdj { get; set; }

        [JsonPropertyName("chequesAdj")]
        public decimal? ChequesAdj { get; set; }

        [JsonPropertyName("chequesProporcionRecuperada")]
        public decimal? ChequesProporcionRecuperada { get; set; }

        [JsonPropertyName("ventasAdj")]
        public decimal? VentasAdj { get; set; }

        [JsonPropertyName("ventasVariacion")]
        public decimal? VentasVariacion { get; set; }

        [JsonPropertyName("ventasBase")]
        public string? VentasBase { get; set; }

        // Nullable on purpose: absent means the row was scored before §7.3b and must keep the
        // wording it was written under.
        [JsonPropertyName("ventasVariacionEsReal")]
        public bool? VentasVariacionEsReal { get; set; }

        [JsonPropertyName("casfogMoraAdj")]
        public decimal? CasfogMoraAdj { get; set; }

        [JsonPropertyName("casfogProporcionMora")]
        public decimal? CasfogProporcionMora { get; set; }

        [JsonPropertyName("bcraExcluded")]
        public bool? BcraExcluded { get; set; }

        [JsonPropertyName("effectiveWeightRatios")]
        public decimal? EffectiveWeightRatios { get; set; }

        [JsonPropertyName("effectiveWeightBcra")]
        public decimal? EffectiveWeightBcra { get; set; }

        [JsonPropertyName("effectiveWeightMacro")]
        public decimal? EffectiveWeightMacro { get; set; }

        // The cutoffs the engine classified this score with (ScoreLevelCutoffs). Absent on rows scored before
        // they were recorded; those fall back to the engine defaults.
        [JsonPropertyName("levelCutoffs")]
        public ScoreLevelCutoffs? LevelCutoffs { get; set; }
    }

    private sealed class AlertItem
    {
        [JsonPropertyName("level")]       public string? Level       { get; set; }
        [JsonPropertyName("code")]        public string? Code        { get; set; }
        [JsonPropertyName("title")]       public string? Title       { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<byte[]> GenerateAsync(
        AnalysisResult        result,
        Analysis              analysis,
        string                tenantId,
        IReadOnlyList<string> sections,
        CancellationToken     ct,
        CompanyPdfReport?     report = null,
        PdfTheme              theme  = PdfTheme.Light)
    {
        IReadOnlyList<string> activeSections =
            sections.Count == 0 ? BalanceSections : sections;

        byte[] bytes = BuildPdf(result, analysis, report ?? new CompanyPdfReport(), activeSections, theme);
        return Task.FromResult(bytes);
    }

    // ── Brand fonts ───────────────────────────────────────────────────────────
    // The three brand faces (Sora / DM Sans / JetBrains Mono, all OFL) are embedded with IDENTITY_H,
    // PREFER_EMBEDDED and subsetting on. Resolved ONCE per export and threaded through every renderer
    // as a single Fonts value — exactly as the two Helvetica fonts were threaded before. A missing or
    // unreadable asset must never fail an export: each face falls back to the standard-14 Helvetica
    // pair (Mono → Helvetica) and the failure is logged once as a warning.

    private readonly record struct Fonts(
        PdfFont Heading,      // Sora Regular    — display figures, "/1000"
        PdfFont HeadingBold,  // Sora Bold       — headings, table headers, the score figure, wordmark
        PdfFont Body,         // DM Sans Regular — body prose
        PdfFont BodyMedium,   // DM Sans Medium  — emphasis / status labels
        PdfFont Mono);        // JetBrains Mono  — every number in a table cell, plus codes and ids

    private const string FontDirRelative = "Assets/Fonts";

    private const string SoraRegularFile   = "Sora-Regular.ttf";
    private const string SoraBoldFile      = "Sora-Bold.ttf";
    private const string DmSansRegularFile = "DMSans-Regular.ttf";
    private const string DmSansMediumFile  = "DMSans-Medium.ttf";
    private const string MonoFile          = "JetBrainsMono-Regular.ttf";

    // One brand TTF as read from disk. Bytes is null when the file is missing (Error null) or unreadable
    // (Error set); either way the face falls back to Helvetica. Never thrown — a font never fails an export.
    private sealed record BrandFontFile(string File, string Path, byte[]? Bytes, Exception? Error);

    // The raw TTF bytes, read ONCE per process. A byte[] is never written after this, so sharing it across
    // concurrent exports is safe — unlike the FontProgram / PdfFont built from it (see ResolveFonts).
    private static readonly Lazy<IReadOnlyDictionary<string, BrandFontFile>> BrandFontFiles =
        new(ReadBrandFontFiles, LazyThreadSafetyMode.ExecutionAndPublication);

    // 0 until the first export has reported any missing/unreadable brand font, so that report is logged
    // once per process instead of once per export. A static Lazy has no instance logger to do it itself.
    private static int _brandFontLoadReported;

    private static IReadOnlyDictionary<string, BrandFontFile> ReadBrandFontFiles()
    {
        // Resolve against the app base dir (Assets/Fonts is copied there on publish) so the path is
        // correct on the Windows App Service, not just under the dev working directory.
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, FontDirRelative);

        var files = new Dictionary<string, BrandFontFile>(StringComparer.Ordinal);
        foreach (string file in new[] { SoraRegularFile, SoraBoldFile, DmSansRegularFile, DmSansMediumFile, MonoFile })
        {
            string path = System.IO.Path.Combine(dir, file);
            try
            {
                files[file] = File.Exists(path)
                    ? new BrandFontFile(file, path, File.ReadAllBytes(path), null)
                    : new BrandFontFile(file, path, null, null);
            }
            catch (Exception ex)
            {
                files[file] = new BrandFontFile(file, path, null, ex);
            }
        }

        return files;
    }

    // WHY THE FONT CACHE IS OFF — do not "optimise" this back to PdfFontFactory.CreateFont(path, ...).
    // That overload caches (iText 8.0.5: DEFAULT_CACHED = true), so every export in the process shared ONE
    // file-backed TrueTypeFont per face. Subsetting at Document.Close() reads glyph data back through that
    // shared parser, and its lazy thread-safety wrapper (RandomAccessFileOrArray.EnsureByteSourceIsThreadSafe)
    // is an unsynchronised check-then-set: the first exports to close concurrently could each lock a
    // different wrapper over the same FileStream and corrupt each other's reads — IndexOutOfRangeException
    // in TrueTypeFontSubset.CheckGlyphComposite, ArgumentOutOfRangeException in FileStream reads. It throws
    // at Close, so the load-time try/catch below could never catch it. Now each export builds its own
    // uncached FontProgram from the shared, immutable bytes; FontProgram / PdfFont are never shared between
    // documents and never stored in a static.
    private Fonts ResolveFonts()
    {
        // Standard-14 Helvetica is the universal fallback; if even that fails there is nothing to fall
        // back to, so let it surface rather than return an unusable bundle.
        PdfFont helv     = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        PdfFont helvBold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);

        IReadOnlyDictionary<string, BrandFontFile> files = BrandFontFiles.Value;

        if (Interlocked.Exchange(ref _brandFontLoadReported, 1) == 0)
        {
            foreach (BrandFontFile missing in files.Values.Where(f => f.Bytes is null))
            {
                if (missing.Error is null)
                    _logger.LogWarning(
                        "PDF export: brand font '{Font}' not found at {Path}; falling back to Helvetica.",
                        missing.File, missing.Path);
                else
                    _logger.LogWarning(missing.Error,
                        "PDF export: brand font '{Font}' failed to load; falling back to Helvetica.", missing.File);
            }
        }

        PdfFont Load(string file, PdfFont fallback)
        {
            byte[]? bytes = files[file].Bytes;
            if (bytes is null)
                return fallback;   // already reported once above

            try
            {
                FontProgram program = FontProgramFactory.CreateFont(bytes, false);   // cached: false — a fresh, unshared program
                PdfFont? f = program is null
                    ? null
                    : PdfFontFactory.CreateFont(
                        program, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                if (f is null)
                {
                    _logger.LogWarning(
                        "PDF export: brand font '{Font}' was not recognised as a font; falling back to Helvetica.", file);
                    return fallback;
                }

                f.SetSubset(true);   // embed only the glyphs actually used, so the file does not balloon
                return f;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF export: brand font '{Font}' failed to load; falling back to Helvetica.", file);
                return fallback;
            }
        }

        return new Fonts(
            Heading:     Load(SoraRegularFile,   helv),
            HeadingBold: Load(SoraBoldFile,      helvBold),
            Body:        Load(DmSansRegularFile, helv),
            BodyMedium:  Load(DmSansMediumFile,  helvBold),
            Mono:        Load(MonoFile,          helv));
    }

    // ── Vector-drawing helper ─────────────────────────────────────────────────
    // Every graphic in the document goes through here. The layout is flowing, so absolute page
    // positions are unknown when a section renders; each graphic is drawn into an off-page XObject of
    // a fixed size (caller draws in the XObject's own space, origin bottom-left) and returned as an
    // Image ready to add to the flowing Document. Never draw on the page canvas directly.
    private static Image DrawVector(ITextDocument doc, float width, float height, Action<PdfCanvas> draw)
    {
        var xobj   = new PdfFormXObject(new Rectangle(width, height));
        var canvas = new PdfCanvas(xobj, doc.GetPdfDocument());
        draw(canvas);
        return new Image(xobj).SetWidth(width).SetHeight(height);
    }

    // Flat-print translation of a translucent surface: composite foreground over a KNOWN solid
    // background at the given alpha. Print has no real transparency, and every background we draw over
    // here is a solid palette fill, so pre-blending is exact.
    private static DeviceRgb Over(DeviceRgb fg, DeviceRgb bg, double alpha)
    {
        float[] f = fg.GetColorValue();
        float[] b = bg.GetColorValue();
        float Mix(int i) => (float)(f[i] * alpha + b[i] * (1.0 - alpha));
        return new DeviceRgb(Mix(0), Mix(1), Mix(2));
    }

    // ── PDF construction ──────────────────────────────────────────────────────

    // The header's brand slot: the client's logo only when the tenant has a usable one, else Financia (null).
    private static ClientPdfBrand? ClientBrandOf(TenantPdfBranding? branding) =>
        branding is { IsCoBranded: true } ? new ClientPdfBrand(branding) : null;

    private byte[] BuildPdf(
        AnalysisResult        result,
        Analysis              analysis,
        CompanyPdfReport      report,
        IReadOnlyList<string> sections,
        PdfTheme              theme)
    {
        // Everything below — every doc.Add, the END_PAGE furniture, the page count and doc.Close — runs
        // synchronously inside this scope, so the whole document reads ONE palette.
        // A client accent (co-branded exports only, already contrast-checked) replaces the Financia one.
        using PdfRenderScope.Scope scope = PdfRenderScope.Enter(PdfPalette.For(theme, report.Branding.Accent));

        using var ms          = new MemoryStream();
        PdfWriter writer      = new PdfWriter(ms);
        writer.SetCloseStream(false);
        PdfDocument pdf       = new PdfDocument(writer);
        var doc          = new ITextDocument(pdf, PageSize.A4);
        ApplyPageSetup(doc);
        ApplyThemeDefaults(doc);

        Fonts fonts = ResolveFonts();

        string  shortId     = result.ResultId.ToString()[..8].ToUpperInvariant();
        string  exportedAt  = FormatExportTimestamp();
        int     fiscalYear  = analysis.FiscalYear;
        string? entityName  = string.IsNullOrWhiteSpace(report.EntityName) ? null : report.EntityName.Trim();

        ApplyDocumentInfo(
            pdf,
            entityName is null
                ? $"{ReportTitleCompany} — Ejercicio {fiscalYear.ToString(CultureInfo.InvariantCulture)}"
                : $"{ReportTitleCompany} — {entityName} — Ejercicio {fiscalYear.ToString(CultureInfo.InvariantCulture)}",
            ReportTitleCompany,
            report.TenantDisplayName);

        // Header band (page 1), slim running header (pages 2+) and the running footer, on every page. The
        // total page count is stamped into each page's footer form at close (never a second render pass).
        var furniture = new PageFurnitureHandler(
            pdf, fonts, ReportTitleCompany, entityName, shortId, exportedAt, theme, ClientBrandOf(report.Branding));
        pdf.AddEventHandler(PdfDocumentEvent.END_PAGE, furniture);

        // The score-reliability verdict is evaluated ONCE and read by the first page and the risk section.
        ScoreReliabilityResponse? reliability = EvaluateReliability(result, analysis, out bool reliabilityFailed);

        // The deterministic alert channel, read ONCE: page 1 and the risk section print the same rows, and an
        // unreadable payload is logged once per export.
        CatalogueAlertChannel.Result catalogue = ReadCatalogueAlerts(result);

        // ── Page 1: the fact sheet (always rendered, whatever sections were chosen) ──────────────
        AddFactSheet(doc, fonts, result, analysis, report, reliability, catalogue);

        // The full reliability verdict is always in the report: inside the risk section when it is rendered,
        // otherwise right after the fact sheet, before anything that rests on the score.
        if (!sections.Contains("risk"))
            AddReliabilityBox(doc, fonts, reliability, reliabilityFailed);

        // ── Sections ──────────────────────────────────────────────────────────
        // Order = how a credit analyst reads: the decision first, then the company's own figures, then the
        // external evidence, then the caveats about the source document. This is only the ORDER of the
        // dispatch — the section keys, contents, AllSections and the export contract are unchanged.
        //   executive_summary → risk → balance → ratios (+ ratio_breakdown, one table) → cash_flow
        //   → bcra (cheques, macro, tipo de cambio) → lufe → casfog → anomalies → footer
        // The executive summary opens page 2, right after the fact sheet. The statements come next: the
        // balance, the ratios read from it and the cash flow. bcra opens the external evidence; lufe follows
        // it (the §7.3 penalty, its CPI read from the same BCRA snapshot); casfog closes that run (§7.4).
        if (sections.Contains("executive_summary"))
            AddExecutiveSummarySection(doc, fonts, result);

        if (sections.Contains("risk"))
            AddRiskSection(doc, fonts, result, reliability, reliabilityFailed, report.AlertDisplayNames, catalogue);

        if (sections.Contains("balance"))
            AddBalanceSection(doc, fonts, result.BalanceJson);

        bool ratioValues = sections.Contains("ratios");
        bool ratioScores = sections.Contains("ratio_breakdown");
        if (ratioValues || ratioScores)
            AddRatiosTable(doc, fonts, result.RatiosJson, result.ScoreBreakdownJson, ratioValues, ratioScores);

        if (sections.Contains("cash_flow"))
            AddCashFlowSection(doc, fonts, result.BalanceJson);

        if (sections.Contains("bcra"))
            AddBcraSection(doc, fonts, result.BcraDataJson, result.ScoreBreakdownJson, fiscalYear);

        if (sections.Contains("lufe"))
            AddLufeVentasSection(doc, fonts, result.LufeDataJson, result.BcraDataJson, result.ScoreBreakdownJson, result.AiSuggestedScore);

        if (sections.Contains("casfog"))
            AddCasfogSection(doc, fonts, result.CasfogDataJson);

        if (sections.Contains("anomalies"))
            AddAnomaliesSection(doc, fonts, result);

        if (sections.Contains("footer"))
            AddFooterSection(doc, fonts, shortId, exportedAt);

        // ── Non-silent contract: name any requested key this service did not render ─────────────
        // A key that passed the controller yet has no renderer is logged at Warning with the analysis
        // id. That is a real defect made visible: the controller whitelist and this service's
        // AllSections are two lists that can drift apart.
        foreach (string key in sections)
        {
            if (BalanceSections.Contains(key)) continue;

            _logger.LogWarning(
                "PDF export: section key '{SectionKey}' passed controller validation but has no renderer for analysis {AnalysisId}; the controller whitelist and the service section list have drifted.",
                key, result.ResultId);
        }

        // Stamp every page's "Página X de Y" once, now that the total is known, then close.
        furniture.WriteTotalPages(pdf);

        doc.Close();
        ms.Position = 0;
        return ms.ToArray();
    }

    // ── Confiabilidad del score ─────────────────────────────────────────────────
    // Score credibility 1b-3. The verdict every other surface shows for this row (same adapter, same inputs).
    // Evaluated once per export; a failure is logged and reported as "could not be determined" — the export
    // never fails because of it. A persona humana analysis has no verdict.
    private ScoreReliabilityResponse? EvaluateReliability(AnalysisResult result, Analysis analysis, out bool failed)
    {
        failed = false;
        try
        {
            ScoreReliabilityVerdict verdict = ReliabilityEvaluator(
                new ScoreReliabilityRow(
                    AiSuggestedScore: result.AiSuggestedScore,
                    EnrichmentStatus: result.EnrichmentStatus,
                    AnalysisKind:     analysis.AnalysisKind,
                    FiscalYear:       analysis.FiscalYear,
                    GeneratedAt:      result.GeneratedAt,
                    RescoredAt:       result.RescoredAt),
                result.ScoreBreakdownJson);

            if (verdict.BreakdownUnreadable)
                _logger.LogWarning(
                    "PDF export: score breakdown of analysis {AnalysisId} could not be read; its score reliability is reported as facts_unavailable",
                    result.AnalysisId);

            return verdict.Reliability;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PDF export: the score reliability of analysis {AnalysisId} could not be determined; the report says so and continues",
                result.AnalysisId);

            failed = true;
            return null;
        }
    }

    // The full verdict, in the Portal's words ("Datos completos" / "Con notas" / "Score provisorio" / "Sin
    // score") and every reason's MESSAGE: the report is the analyst's own document, so the full text — sector
    // included — belongs here, unlike in a Claude prompt. One tinted box; a complete verdict still gets one
    // short line, never an empty box.
    private static void AddReliabilityBox(
        ITextDocument doc, Fonts fonts, ScoreReliabilityResponse? reliability, bool failed)
    {
        if (failed)
        {
            doc.Add(ReliabilityBox(fonts, label: null, ColorMuted, Surface,
                ["No se pudo determinar la confiabilidad del score."]));
            return;
        }

        if (reliability is null) return;

        (string label, PdfTone tone, _) = PdfLabels.Reliability(reliability.Level);

        IReadOnlyList<string> lines = reliability.Reasons.Count == 0
            ? ["Datos completos y verificados."]
            : reliability.Reasons.Select(r => "\u2022 " + r.Message).ToList();

        doc.Add(ReliabilityBox(fonts, label, ToneColor(tone), ToneSoft(tone), lines));
    }

    private static Table ReliabilityBox(
        Fonts fonts, string? label, DeviceRgb accent, DeviceRgb tint, IReadOnlyList<string> lines)
    {
        Paragraph title = new Paragraph()
            .SetMargin(0)
            .Add(new Text("Confiabilidad del score").SetFont(fonts.HeadingBold).SetFontSize(10).SetFontColor(Label));
        if (label is not null)
            title.Add(new Text(": " + label).SetFont(fonts.HeadingBold).SetFontSize(10).SetFontColor(accent));

        Cell cell = new Cell()
            .SetBackgroundColor(tint)
            .SetBorder(Border.NO_BORDER)
            .SetBorderLeft(new SolidBorder(accent, 3))
            .SetPaddingLeft(10).SetPaddingRight(8).SetPaddingTop(6).SetPaddingBottom(6)
            .Add(title);

        foreach (string line in lines)
        {
            cell.Add(new Paragraph(line)
                .SetFont(fonts.Body)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMarginTop(3)
                .SetMarginBottom(0));
        }

        Table box = new Table(UnitValue.CreatePercentArray([1f]))
            .UseAllAvailableWidth()
            .SetMarginBottom(12)
            .SetKeepTogether(true);
        box.AddCell(cell);
        return box;
    }

    // ── Balance section ───────────────────────────────────────────────────────

    private static void AddBalanceSection(
        ITextDocument doc, Fonts fonts, string? balanceJson)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono, med = fonts.BodyMedium;
        AddSectionHeading(doc, fonts, "Estado patrimonial");

        if (string.IsNullOrWhiteSpace(balanceJson))
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        BalanceSheet? balance;
        try { balance = JsonSerializer.Deserialize<BalanceSheet>(balanceJson, JsonOpts); }
        catch { balance = null; }

        if (balance is null)
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        Table table = BuildTwoColTable(["Concepto", "Importe (ARS)"], bold);

        // Phase 10-F3a: a figure an analyst corrected by hand after the reading is marked as such.
        var corrections = BalanceCorrections.ManualCorrectionsReader.Read(balanceJson);

        void AddRow(string label, string key, decimal? value, bool highlight = false)
        {
            if (corrections?.Paths.Contains(key) == true)
                label += " (corregido)";

            // Money tables render present amounts through Pesos() — the Portal's formatPesos
            // ("$ 1.234.567") — so every table in the document uses one currency treatment. A figure the
            // reading did not produce says so in the ratios' own words: "No se pudo leer".
            string displayValue = value.HasValue
                ? Pesos((double)value.Value)
                : UnreadFigure;

            Cell labelCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                .SetPadding(6)
                .Add(new Paragraph(label).SetFont(highlight ? med : regular).SetFontSize(9));

            Cell valueCell = new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                .SetPadding(6)
                .SetTextAlignment(TextAlignment.RIGHT)
                .Add(new Paragraph(displayValue).SetFont(mono).SetFontSize(9));

            if (highlight)
            {
                labelCell.SetBackgroundColor(ColorSectionBg);
                valueCell.SetBackgroundColor(ColorSectionBg);
            }

            table.AddCell(labelCell);
            table.AddCell(valueCell);
        }

        AddRow("Activo corriente",    "current_assets",          balance.CurrentAssets);
        AddRow("Activo no corriente", "non_current_assets",      balance.NonCurrentAssets);
        AddRow("Activo total",        "assets",                  balance.TotalAssets,      highlight: true);
        AddRow("Pasivo corriente",    "current_liabilities",     balance.CurrentLiabilities);
        AddRow("Pasivo no corriente", "non_current_liabilities", balance.NonCurrentLiabilities);
        AddRow("Pasivo total",        "liabilities",             balance.TotalLiabilities, highlight: true);
        AddRow("Patrimonio neto",     "equity",                  balance.TotalEquity,      highlight: true);
        AddRow("Ingresos netos",      "net_revenue",             balance.NetRevenue);
        AddRow("Resultado neto",      "net_profit",              balance.NetProfit);

        doc.Add(table);

        if (corrections is not null)
        {
            // One line, under the table: how many figures (rubro lines included) and the latest reason.
            doc.Add(new Paragraph(ManualCorrectionsLine(corrections))
                .SetFont(regular)
                .SetFontSize(8)
                .SetFontColor(Label)
                .SetMarginTop(4));
        }

        // Stacked bars of the patrimonial composition, drawn from the figures already in the table.
        AddPatrimonialComposition(doc, fonts, balance);
    }

    // The words the report prints for a figure the reading did not produce — the balance and the ratios alike.
    internal const string UnreadFigure = "No se pudo leer";

    /// <summary>"N cifras corregidas manualmente; motivo: …" — the line under the balance table. Internal for tests.</summary>
    internal static string ManualCorrectionsLine(BalanceCorrections.ManualCorrectionsSummary corrections)
    {
        var figures = corrections.Count == 1
            ? "1 cifra corregida manualmente"
            : $"{corrections.Count.ToString(CultureInfo.InvariantCulture)} cifras corregidas manualmente";

        return string.IsNullOrWhiteSpace(corrections.LastReason)
            ? figures + "."
            : $"{figures}; motivo: {corrections.LastReason.Trim()}";
    }

    // ── Ratios financieros: one table for the two ratio keys ──────────────────
    // "ratios" contributes the Valor column, "ratio_breakdown" the Puntaje column and the bar; with both chosen they
    // print as ONE table (Indicador | Valor | Puntaje | Estado | bar) under one heading and one bookmark, with only one
    // chosen that half's columns alone. Every cell keeps the rule the two separate tables had: the value as the ratios
    // table printed it, the score and bar as the breakdown printed them, the Estado of the half that is shown — and
    // with both, the breakdown's (it matches the bar), except a ratio with no current liabilities, whose Valor already
    // says "Sin pasivo corriente" and whose Estado reads "Liquidez máxima", as the ratios table said it.

    private static void AddRatiosTable(
        ITextDocument doc, Fonts fonts, string? ratiosJson, string? scoreBreakdownJson, bool showValue, bool showScore)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono, med = fonts.BodyMedium;
        AddSectionHeading(doc, fonts, "Ratios financieros");

        Dictionary<string, double?>? ratiosDict = null;
        if (!string.IsNullOrWhiteSpace(ratiosJson))
        {
            try { ratiosDict = JsonSerializer.Deserialize<Dictionary<string, double?>>(ratiosJson, JsonOpts); }
            catch { /* ratios unavailable */ }
        }

        Dictionary<string, RatioBreakdownEntry>? breakdown = null;
        if (!string.IsNullOrWhiteSpace(scoreBreakdownJson))
        {
            try { breakdown = JsonSerializer.Deserialize<ScoreBreakdownWrapper>(scoreBreakdownJson, JsonOpts)?.Breakdown; }
            catch { /* breakdown unavailable */ }
        }

        // A half prints only with its own data: the values need the ratios, the scores a non-empty breakdown.
        showValue &= ratiosDict is { Count: > 0 };
        showScore &= breakdown is { Count: > 0 };
        if (!showValue && !showScore)
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        // Each header aligns like its column: the figures right, the status labels centred.
        const TextAlignment Left = TextAlignment.LEFT, Right = TextAlignment.RIGHT, Centre = TextAlignment.CENTER;
        (string Heading, float Width, TextAlignment Align)[] columns = (showValue, showScore) switch
        {
            (true, true)  => [("Indicador", 25f, Left), ("Valor", 27f, Right), ("Puntaje", 11f, Right), ("Estado", 17f, Centre), ("", 20f, Left)],
            (true, false) => [("Indicador", 50f, Left), ("Valor", 25f, Right), ("Estado", 25f, Centre)],
            _             => [("Indicador", 38f, Left), ("Puntaje", 18f, Right), ("Estado", 24f, Centre), ("", 20f, Left)],
        };

        Table table = new Table(UnitValue.CreatePercentArray(columns.Select(c => c.Width).ToArray()))
            .UseAllAvailableWidth()
            .SetMarginBottom(8)
            .SetKeepTogether(true);

        foreach ((string heading, _, TextAlignment align) in columns)
        {
            table.AddHeaderCell(AlignHeader(new Cell()
                .SetBackgroundColor(ColorTableHead)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(7)
                .Add(new Paragraph(heading)
                    .SetFont(bold)
                    .SetFontSize(9)
                    .SetFontColor(ColorTableHeadFg)), align, BodyCellPadding));
        }

        // The canonical RatioToBreakdownKey order (identical to RatioMetaMap's). Every canonical indicator is rendered:
        // an indicator with no value or no score is shown considered-but-not-measured, never dropped.
        bool altRow = false;
        foreach (KeyValuePair<string, string> mapping in RatioToBreakdownKey)
        {
            if (!RatioMetaMap.TryGetValue(mapping.Key, out RatioMeta? meta)) continue;

            RatioBreakdownEntry? entry = breakdown is not null && breakdown.TryGetValue(mapping.Value, out var found) ? found : null;
            bool noCurrentLiabilities = entry is not null && IsNoCurrentLiabilities(entry);
            bool hasValue = ratiosDict is not null && ratiosDict.TryGetValue(mapping.Key, out double? ratioValue) && ratioValue.HasValue;

            // ── The Valor half (the ratios table's rule) ──
            string    valueText;
            string    valueStatus;
            DeviceRgb valueColor;
            if (!hasValue && noCurrentLiabilities)
            {
                // Scoring algorithm 10: no current liabilities — the value is null, the liquidity is the best.
                valueText   = "Sin pasivo corriente";
                valueStatus = "Liquidez máxima";
                valueColor  = ColorOk;
            }
            else if (!hasValue)
            {
                valueText   = "—";
                valueStatus = NotMeasurableLabel(mapping.Key, entry);
                valueColor  = ColorMuted;
            }
            else
            {
                valueText = FormatRatio(mapping.Key, ratiosDict![mapping.Key]!.Value);
                (valueStatus, valueColor) = entry?.Status switch
                {
                    null        => ("N/D",            ColorMuted),
                    "Saludable" => ("Saludable",      ColorOk),
                    "Atencion"  => ("Atención",  ColorWarn),
                    "Critico"   => ("Crítico",   ColorDanger),
                    _           => ("N/D",            ColorMuted)
                };
            }

            // ── The Puntaje half (the breakdown table's rule) ──
            // An excluded ratio (weight 0) and a measured:false one (the low-coverage neutral fallback) carry a
            // placeholder score, never a measurement — "—" even when RatiosJson is unavailable to prove the ratio null
            // (score credibility 1b-3). A ratio scored at the best band for having no current liabilities is measured
            // although its value is null.
            bool scored = entry is not null
                && !entry.Excluded
                && entry.Measured != false
                && (noCurrentLiabilities || ratiosDict is null || hasValue);

            string    scoreText   = "—";
            string    scoreStatus = NotMeasurableLabel(mapping.Key, entry);
            DeviceRgb scoreColor  = ColorMuted;
            double?   barFraction = null;   // empty track — considered but not measured
            if (scored)
            {
                barFraction = ScoreScale.ToNosis(entry!.Score) / 1000.0;
                scoreText   = ScoreScale.ToNosis(entry.Score).ToString(EsAr);
                scoreColor  = entry.Status switch
                {
                    "Saludable" => ColorOk,
                    "Atencion"  => ColorWarn,
                    "Critico"   => ColorDanger,
                    _           => ColorMuted
                };
                scoreStatus = noCurrentLiabilities ? ScoringNoticeTexts.NoCurrentLiabilities : entry.Status switch
                {
                    "Saludable" => "Saludable",
                    "Atencion"  => "Atención",
                    "Critico"   => "Crítico",
                    _           => entry.Status ?? "N/D"
                };
            }

            (string statusLabel, DeviceRgb statusColor) = (showValue, showScore) switch
            {
                (true, false)                         => (valueStatus, valueColor),
                (false, true)                         => (scoreStatus, scoreColor),
                _ when !hasValue && noCurrentLiabilities => ("Liquidez máxima", ColorOk),
                _                                     => (scoreStatus, scoreColor),
            };

            DeviceRgb? rowBg = altRow ? ColorSectionBg : null;

            table.AddCell(BodyCell(meta.Label, regular, 9, rowBg));
            if (showValue)
                table.AddCell(BodyCell(valueText, mono, 9, rowBg, TextAlignment.RIGHT));
            if (showScore)
                table.AddCell(BodyCell(scoreText, mono, 9, rowBg, TextAlignment.RIGHT));
            table.AddCell(StatusCell(statusLabel, med, 8, statusColor, rowBg, mono, 9));

            // Drawn score bar; an unscored indicator renders an EMPTY TRACK, the same honest unmeasured treatment as
            // the excluded-BCRA component bar.
            if (showScore)
            {
                table.AddCell(new Cell()
                    .SetBorder(Border.NO_BORDER)
                    .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                    .SetBackgroundColor(rowBg)
                    .SetPadding(6)
                    .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                    .Add(DrawBar(doc, barFraction, scoreColor)));
            }

            altRow = !altRow;
        }

        doc.Add(table);
    }

    // ── Risk section ──────────────────────────────────────────────────────────

    private void AddRiskSection(
        ITextDocument doc, Fonts fonts, AnalysisResult result, ScoreReliabilityResponse? reliability,
        bool reliabilityFailed, IReadOnlyDictionary<string, string> alertDisplayNames, CatalogueAlertChannel.Result catalogue)
    {
        PdfFont regular = fonts.Body, med = fonts.BodyMedium;
        AddSectionHeading(doc, fonts, "An\u00e1lisis de riesgo");

        // The gauge itself lives on the first page; here the full reliability verdict, and — with no score —
        // "Sin score" and why, in the verdict's own words.
        AddReliabilityBox(doc, fonts, reliability, reliabilityFailed);
        if (result.AiSuggestedScore is null)
            AddNoScoreNote(doc, fonts, reliability);

        // Parse ScoreBreakdownJson once for sector profile, component bars, and floor callout
        ScoreBreakdownWrapper? wrapper = null;
        if (!string.IsNullOrWhiteSpace(result.ScoreBreakdownJson))
        {
            try { wrapper = JsonSerializer.Deserialize<ScoreBreakdownWrapper>(result.ScoreBreakdownJson, JsonOpts); }
            catch { /* old data without extended fields — skip gracefully */ }
        }

        // Sector profile label. It introduces the component table, so it opens the composition block below.
        Paragraph? profile = string.IsNullOrWhiteSpace(wrapper?.SectorProfile)
            ? null
            : new Paragraph($"Perfil de scoring: {PdfLabels.SectorProfile(wrapper.SectorProfile)}")
                .SetFont(regular)
                .SetFontSize(9)
                .SetFontColor(ColorMuted)
                .SetMarginBottom(8);

        // The profile line, the score component bars and, right under them, the anatomy line that sums them: ONE
        // keep-together block, so neither line prints on another page than the table it introduces or adds up.
        Div composition = new Div().SetKeepTogether(true);
        if (profile is not null)
            composition.Add(profile);
        bool hasComposition = false;
        if (wrapper is { ScoreRatios: not null } or { ScoreBcra: not null } or { ScoreMacro: not null })
        {
            composition.Add(ScoreComponentBars(doc, fonts, wrapper));
            hasComposition = true;
        }

        // ── Score anatomy on the 1000 scale ─────────────────────────────────────────────────────
        // The same model the Portal's "Anatomía del score" renders (ScoreAnatomy is a rule-for-rule
        // port of its buildScoreAnatomy), so the report and the screen print the same figures:
        // Ratios + BCRA + Macro → Score compuesto, the floor / LUFE / CASFOG adjustments in engine
        // order as signed 1000-scale points, then the Score final the gauge above shows. toNosis'
        // rounding difference rides on the last adjustment, so the printed lines add up exactly.
        ScoreAnatomy.Model? anatomy = wrapper is null ? null : BuildAnatomy(wrapper, result.AiSuggestedScore);
        bool hasAnatomy = wrapper is not null && HasCompleteAnatomy(wrapper, result.AiSuggestedScore);

        if (hasAnatomy)
        {
            IEnumerable<string> terms = anatomy!.Contributions
                .Where(part => part.Key != ScoreAnatomy.ComponentKey.Bcra || wrapper!.BcraExcluded != true)
                .Select(part => $"{ComponentShortLabel(part.Key)} {ScoreAnatomy.FormatFigure(part.Value)}");

            // The components' own rounding to the composite, stated on its own line — never hidden.
            string componentsRounding = anatomy.ComponentsRoundingLine is { } partsRounding
                ? $" + redondeo {ScoreAnatomy.FormatDelta(partsRounding)}"
                : string.Empty;

            composition.Add(new Paragraph(
                    $"{string.Join(" + ", terms)}{componentsRounding} \u2192 Score compuesto {ScoreAnatomy.FormatFigure(anatomy.CompositeDisplay)}")
                .SetFont(med)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMarginBottom(8));
            hasComposition = true;
        }

        if (hasComposition)
            doc.Add(composition);
        else if (profile is not null)
            doc.Add(profile);

        // Floor callout — only when the floor was actually APPLIED.
        // Was `AppliedFloor: > 0`, which also fired when a ratio was critical but the composite
        // already exceeded its floor, printing "se redujo a X" for a score that never moved.
        // ScoreBeforeFloor < AppliedFloor is ScoringService's own guard, recovered from the persisted
        // blob, so old rows render correctly too. An ABSENT before-score keeps the old, more
        // permissive test: we cannot tell whether the floor bound, and dropping the callout from a
        // row where it did would be a silent narrowing. The CriticalRatios conjunct matches the
        // narrative prompt and the chat context, which both already required it.
        bool showFloorCallout = wrapper is not null
            && (wrapper.ScoreBeforeFloor is null
                    ? wrapper.AppliedFloor > 0
                    : wrapper.ScoreBeforeFloor < wrapper.AppliedFloor)
            && wrapper.CriticalRatios is { Count: > 0 };

        ScoreAnatomy.Adjustment? floorAdjustment = hasAnatomy
            ? anatomy!.Adjustments.FirstOrDefault(adjustment => adjustment.Key == ScoreAnatomy.AdjustmentKey.Floor)
            : null;

        if (showFloorCallout)
        {
            string criticalList = string.Join(", ", wrapper!.CriticalRatios!);

            string floorText = floorAdjustment is not null
                ? $"Score compuesto: {ScoreAnatomy.FormatFigure(anatomy!.CompositeDisplay)} \u2192 se redujo a {ScoreAnatomy.FormatFigure(anatomy.AfterFloorDisplay)} por ratio(s) cr\u00edtico(s): {criticalList} \u2014 {ScoreAnatomy.FormatDelta(floorAdjustment.DeltaNosis)} pts"
                : $"Score compuesto: {ScoreScale.ToNosis(wrapper.ScoreBeforeFloor ?? result.AiSuggestedScore ?? 0m).ToString(EsAr)} \u2192 se redujo a {ScoreScale.ToNosis(result.AiSuggestedScore ?? 0m).ToString(EsAr)} por ratio(s) cr\u00edtico(s): {criticalList}";

            doc.Add(AdjustmentRow(floorText, regular, AccentSoft, marginTop: 0));
        }

        // The profitability floor waived on a positive operating result (scoring algorithm 10): a ratio
        // shows "Crítico" in the tables and yet floored nothing — the sentence says why. Independent of
        // the floor callout above, which lists only the ratios that did floor the score.
        if (wrapper?.ProfitabilityFloorWaived == true)
        {
            doc.Add(AdjustmentRow(ScoringNoticeTexts.ProfitabilityFloorWaived, regular, AccentSoft, marginTop: 0));
        }

        // The acid-test floor waived on a current ratio at or above its warn threshold (scoring algorithm 11):
        // prueba ácida shows "Crítico" and floored nothing — same treatment as the profitability row above.
        if (wrapper?.AcidTestFloorWaived == true)
        {
            doc.Add(AdjustmentRow(ScoringNoticeTexts.AcidTestFloorWaived, regular, AccentSoft, marginTop: 0));
        }

        // Caída de ventas LUFE (scoring algorithm 6, §7.3a; real terms since 7, §7.3b). Belongs in this
        // general score section rather than beside the cheques row in "Datos BCRA": it is not a BCRA
        // figure, and unlike the cheques penalty it is not folded into any component — it moves the
        // FINAL score, as the critical-floor callout above it does. Rendered only when it actually moved
        // that score (ventasAdj is the REALISED movement), restated on the 1000 scale: a penalty is a
        // negative delta on a danger tint, an improvement a positive one on a success tint. Rows scored
        // before this signal existed carry no such key and render nothing.
        if (wrapper?.VentasAdj is { } ventasAdj && ventasAdj != 0m)
        {
            decimal delta = AdjustmentDeltaNosis(anatomy, hasAnatomy, ScoreAnatomy.AdjustmentKey.Ventas, ventasAdj);

            string ventasText =
                $"Ventas declaradas (LUFE): {VentasVariacionTexto(wrapper)} \u2014 {ScoreAnatomy.FormatDelta(delta)} pts";

            doc.Add(AdjustmentRow(ventasText, regular, delta > 0m ? SuccessSoft : DangerSoft, marginTop: 0));
        }

        // Garantías SGR en mora (scoring algorithm 8, §7.4). Belongs in this general score section for
        // the same reason the ventas row above does, and emphatically not beside the cheques row in
        // "Datos BCRA": it is not a BCRA figure — it is one vendor's view of the SGR guarantee circuit
        // — and it is not folded into any component, it moves the FINAL score. Rendered only when it
        // actually moved that score (casfogMoraAdj is the REALISED movement), on the 1000 scale with
        // the same sign and tint convention as the ventas row. Rows scored before this signal existed
        // carry no such key and render nothing.
        if (wrapper?.CasfogMoraAdj is { } casfogMoraAdj && casfogMoraAdj != 0m)
        {
            decimal delta = AdjustmentDeltaNosis(anatomy, hasAnatomy, ScoreAnatomy.AdjustmentKey.Casfog, casfogMoraAdj);

            string moraTexto = wrapper.CasfogProporcionMora is { } proporcionMora
                ? $"{(proporcionMora * 100m).ToString("F1", EsAr)} % del saldo vigente en mora"
                : "mora informada sobre el saldo vigente";

            string casfogText =
                $"Garantías SGR (CASFOG): {moraTexto} \u2014 {ScoreAnatomy.FormatDelta(delta)} pts";

            doc.Add(AdjustmentRow(casfogText, regular, delta > 0m ? SuccessSoft : DangerSoft, marginTop: 0));
        }

        if (hasAnatomy)
        {
            // ToNosis' rounding, ALWAYS on its own line: it is never attributed to an adjustment (a full
            // LUFE penalty reads −100, and the residual is stated as what it is).
            if (anatomy!.RoundingLine is { } rounding)
            {
                doc.Add(new Paragraph($"Redondeo a la escala del score \u2014 {ScoreAnatomy.FormatDelta(rounding)} pts")
                    .SetFont(regular)
                    .SetFontSize(8)
                    .SetFontColor(ColorMuted)
                    .SetMarginBottom(6));
            }

            doc.Add(new Paragraph($"Score final {anatomy.FinalDisplay.ToString(CultureInfo.InvariantCulture)}")
                .SetFont(fonts.HeadingBold)
                .SetFontSize(10)
                .SetFontColor(ColorBodyText)
                .SetMarginBottom(12));
        }

        // Scoring justification
        if (!string.IsNullOrWhiteSpace(result.ScoringNarrative))
        {
            AddSubHeading(doc, fonts, "Justificaci\u00f3n del scoring");

            string narrativeText = StripMarkdown(StripLeadingMarkdownHeading(result.ScoringNarrative));

            // De-duplicate: drop first line if it mirrors the section heading
            const string SectionHeading = "Justificaci\u00f3n del scoring";
            string[] lines = narrativeText.Split('\n');
            int firstNonEmpty = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
            if (firstNonEmpty >= 0 &&
                NormalizeForCompare(lines[firstNonEmpty]) == NormalizeForCompare(SectionHeading))
            {
                lines[firstNonEmpty] = string.Empty;
                narrativeText = string.Join('\n', lines).TrimStart();
            }

            doc.Add(new Paragraph(narrativeText)
                .SetFont(regular)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMarginBottom(12)
                .SetTextAlignment(TextAlignment.JUSTIFIED));
        }

        // Findings — the Portal's "Hallazgos del balance": the catalogue first, the model's reading after it.
        AddFindings(doc, fonts, result, catalogue, alertDisplayNames);
    }

    // The clause that states the sales variation the score used — "caída 20,0 % (interanual 3 meses,
    // real — deflactado por IPC)". ONE builder for the two places that print it, the risk-section row and
    // the LUFE section's scoring line, so the two can never describe the same breakdown differently.
    // Reads the breakdown only: the PDF never recomputes the figure the score took.
    //
    // The risk row renders only when the penalty moved the score, which the ramp allows only for a
    // variation at or below zero, so it only ever reaches the "caída" arms — which are exactly the text
    // that row printed before this was extracted. The "suba" arm exists for the LUFE section, which also
    // states a variation that earned no penalty.
    private static string VentasVariacionTexto(ScoreBreakdownWrapper wrapper)
    {
        string baseTexto = wrapper.VentasBase switch
        {
            "interanual_3m" => "interanual 3 meses",
            "interanual" => "interanual",
            "trimestral" => "trimestral",
            _            => "base n/d"
        };

        // Three states. The null arm keeps §7.3a's exact parenthetical, so a report re-exported
        // from an analysis scored before §7.3b comes out byte-identical to the one the analyst
        // already has — that row was nominal by design, not for want of inflation data.
        string terminos = wrapper.VentasVariacionEsReal switch
        {
            true  => "real — deflactado por IPC",
            false => "nominal — sin datos de inflación",
            null  => "nominal"
        };

        return wrapper.VentasVariacion switch
        {
            { } suba when suba > 0m
                => $"suba {(suba * 100m).ToString("F1", EsAr)} % ({baseTexto}, {terminos})",
            { } variacion
                => $"caída {(Math.Abs(variacion) * 100m).ToString("F1", EsAr)} % ({baseTexto}, {terminos})",
            null
                => $"caída registrada ({baseTexto}, {terminos})"
        };
    }

    // ── Score component bars ───────────────────────────────────────────────

    private static Table ScoreComponentBars(
        ITextDocument doc, Fonts fonts, ScoreBreakdownWrapper wrapper)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono, med = fonts.BodyMedium;
        Table table = new Table(UnitValue.CreatePercentArray([34f, 12f, 14f, 16f, 24f]))
            .UseAllAvailableWidth()
            .SetMarginBottom(10)
            .SetKeepTogether(true);

        // Header \u2014 "Peso" lets the partner reproduce the composite from the component scores.
        // Each header aligns like its column: the figures right, the status labels centred.
        (string Heading, TextAlignment Align)[] headings =
        [
            ("Componente", TextAlignment.LEFT), ("Puntaje", TextAlignment.RIGHT), ("Peso", TextAlignment.RIGHT),
            ("Estado", TextAlignment.CENTER), ("", TextAlignment.LEFT),
        ];
        foreach ((string heading, TextAlignment align) in headings)
        {
            table.AddHeaderCell(AlignHeader(new Cell()
                .SetBackgroundColor(ColorTableHead)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(6)
                .Add(new Paragraph(heading)
                    .SetFont(bold)
                    .SetFontSize(8)
                    .SetFontColor(ColorTableHeadFg)), align, BodyCellPadding));
        }

        // Classified with the cutoffs this analysis was scored with (breakdown levelCutoffs), as page 1's bars are.
        (string label, DeviceRgb color) ComponentStatus(decimal score) =>
            PdfLabels.ComponentTone(score, wrapper.LevelCutoffs) switch
            {
                PdfTone.Good    => ("Saludable", ColorOk),
                PdfTone.Caution => ("Atenci\u00f3n", ColorWarn),
                _               => ("Cr\u00edtico",  ColorDanger),
            };

        // Effective weight is a fraction (0\u20131); render it as a percentage. "\u2014" when absent
        // (older data). The redistributed effective weights already reflect BcraExcluded.
        static string PesoText(decimal? weight) =>
            weight.HasValue ? (weight.Value * 100m).ToString("0.##", EsAr) + " %" : "\u2014";

        Cell PlainCell(string text, PdfFont font, float size, DeviceRgb color, TextAlignment align = TextAlignment.LEFT) =>
            new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                .SetPadding(6)
                .SetTextAlignment(align)
                .Add(new Paragraph(text).SetFont(font).SetFontSize(size).SetFontColor(color));

        void AddComponentRow(string label, decimal? value, decimal? weight)
        {
            if (!value.HasValue) return;

            decimal v = value.Value;
            var (statusLabel, statusColor) = ComponentStatus(v);

            double barFraction = Math.Clamp(ScoreScale.ToNosis(v) / 1000.0, 0.0, 1.0);

            table.AddCell(PlainCell(label, regular, 9, ColorBodyText));
            table.AddCell(PlainCell(ScoreScale.ToNosis(v).ToString(EsAr), mono, 9, ColorBodyText, TextAlignment.RIGHT));
            table.AddCell(PlainCell(PesoText(weight), mono, 9, ColorBodyText, TextAlignment.RIGHT));
            table.AddCell(StatusCell(statusLabel, med, 8, statusColor, null, mono, 9));
            table.AddCell(new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                .SetPadding(6)
                .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                .Add(DrawBar(doc, barFraction, statusColor)));
        }

        AddComponentRow("Ratios financieros", wrapper.ScoreRatios, wrapper.EffectiveWeightRatios);

        if (wrapper.BcraExcluded == true)
        {
            // Absent measurement shown as absent \u2014 score em-dash, weight 0 %, empty bar.
            table.AddCell(PlainCell("Contexto BCRA", regular, 9, ColorBodyText));
            table.AddCell(PlainCell("\u2014", mono, 9, ColorBodyText, TextAlignment.RIGHT));
            table.AddCell(PlainCell((0m).ToString("0.##", EsAr) + " %", mono, 9, ColorBodyText, TextAlignment.RIGHT));
            table.AddCell(StatusCell("No disponible", med, 8, ColorBodyText, null, mono, 9));
            // Empty track, no fill element at all -- the excluded component reads as unmeasured, not zero.
            table.AddCell(new Cell()
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                .SetPadding(6)
                .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                .Add(DrawBar(doc, null, ColorMuted)));
        }
        else
        {
            AddComponentRow("Contexto BCRA", wrapper.ScoreBcra, wrapper.EffectiveWeightBcra);
        }

        AddComponentRow("Variables macro", wrapper.ScoreMacro, wrapper.EffectiveWeightMacro);

        return table;
    }

    // ── Score anatomy helpers ─────────────────────────────────────────────────

    private static ScoreAnatomy.Model BuildAnatomy(ScoreBreakdownWrapper wrapper, decimal? aiSuggestedScore) =>
        ScoreAnatomy.Build(
            new ScoreAnatomy.Input
            {
                ScoreRatios                 = wrapper.ScoreRatios,
                ScoreBcra                   = wrapper.ScoreBcra,
                ScoreMacro                  = wrapper.ScoreMacro,
                BcraExcluded                = wrapper.BcraExcluded,
                EffectiveWeightRatios       = wrapper.EffectiveWeightRatios,
                EffectiveWeightBcra         = wrapper.EffectiveWeightBcra,
                EffectiveWeightMacro        = wrapper.EffectiveWeightMacro,
                AppliedFloor                = wrapper.AppliedFloor,
                ScoreBeforeFloor            = wrapper.ScoreBeforeFloor,
                CriticalRatioCount          = wrapper.CriticalRatios?.Count ?? 0,
                ChequesAdj                  = wrapper.ChequesAdj,
                ChequesProporcionRecuperada = wrapper.ChequesProporcionRecuperada,
                VentasAdj                   = wrapper.VentasAdj,
                VentasVariacion             = wrapper.VentasVariacion,
                VentasVariacionEsReal       = wrapper.VentasVariacionEsReal,
                VentasBase                  = wrapper.VentasBase,
                CasfogMoraAdj               = wrapper.CasfogMoraAdj,
                CasfogProporcionMora        = wrapper.CasfogProporcionMora,
            },
            aiSuggestedScore);

    // The composite → final lines are printed only when every figure they sum is on file. A blob
    // missing a component (or a result without a score) would otherwise read the gap as 0 — the
    // Portal's fallback, fine on screen, not something a report should assert as arithmetic.
    private static bool HasCompleteAnatomy(ScoreBreakdownWrapper wrapper, decimal? aiSuggestedScore) =>
        aiSuggestedScore is not null
        && wrapper.ScoreRatios is not null
        && wrapper.ScoreMacro is not null
        && (wrapper.ScoreBcra is not null || wrapper.BcraExcluded == true);

    // The 1000-scale points one adjustment row prints: the anatomy's figure (rounding residual
    // included when it is the last line), or the bare conversion when the anatomy is incomplete.
    private static decimal AdjustmentDeltaNosis(
        ScoreAnatomy.Model? anatomy, bool hasAnatomy, ScoreAnatomy.AdjustmentKey key, decimal internalAdj) =>
        hasAnatomy && anatomy?.Adjustments.FirstOrDefault(adjustment => adjustment.Key == key) is { } line
            ? line.DeltaNosis
            : ScoreAnatomy.ToNosisDelta(internalAdj);

    private static string ComponentShortLabel(ScoreAnatomy.ComponentKey key) => key switch
    {
        ScoreAnatomy.ComponentKey.Ratios => "Ratios",
        ScoreAnatomy.ComponentKey.Bcra   => "BCRA",
        _                                => "Macro"
    };

    // One tinted full-width row — the shape every score adjustment callout in this report uses.
    private static Table AdjustmentRow(string text, PdfFont font, DeviceRgb background, float marginTop)
    {
        Table row = new Table(UnitValue.CreatePercentArray([1f]))
            .UseAllAvailableWidth()
            .SetMarginTop(marginTop)
            .SetMarginBottom(10);

        row.AddCell(new Cell()
            .SetBackgroundColor(background)
            .SetBorder(Border.NO_BORDER)
            .SetPaddingLeft(8)
            .SetPaddingTop(6)
            .SetPaddingBottom(6)
            .Add(new Paragraph(text)
                .SetFont(font)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMargin(0)));

        return row;
    }

    // Scoring algorithm 10: a measured ratio whose value is null because current liabilities are zero.
    private static bool IsNoCurrentLiabilities(RatioBreakdownEntry entry)
        => !entry.Excluded
           && string.Equals(entry.ExclusionReason, RatioExclusionReasons.NoCurrentLiabilities, StringComparison.Ordinal);

    /// <summary>
    /// The one wording both ratio tables (and the first page) print for an indicator with no measurement.
    /// Interest coverage always reads "No determinable" — the usual cause is an income statement that does not
    /// show financial expenses — so the same indicator never reads two ways in one report.
    /// </summary>
    internal static string NotMeasurableLabel(string ratioKey, RatioBreakdownEntry? entry) =>
        ratioKey == "interest_coverage" ? "No determinable" : NotDeterminableLabel(entry);

    /// <summary>
    /// A ratio as the Portal shows it (lib/ratio-display.ts): "x" for liquidity, solvency and coverage —
    /// endeudamiento included, never as % — and "%" for ROE, ROA and net margin, which arrive already in
    /// percent. Two es-AR decimals so the table matches the narrative.
    /// </summary>
    internal static string FormatRatio(string ratioKey, double value) => ratioKey switch
    {
        "roe" or "roa" or "net_margin" => SignedFigure(value) + "%",
        _                              => SignedFigure(value) + "x"
    };

    // Two es-AR decimals with the typographic minus (U+2212) for a negative figure.
    private static string SignedFigure(double value) =>
        value < 0 ? "\u2212" + Math.Abs(value).ToString("F2", EsAr) : value.ToString("F2", EsAr);

    // Why a ratio-breakdown indicator has no score (score credibility 1b-3), from its own breakdown entry:
    // an operand was not read, the formula does not apply to these figures, or the low-coverage fallback
    // scored it neutral. Anything else — no entry, or an exclusion of unknown cause — stays "No determinable".
    private static string NotDeterminableLabel(RatioBreakdownEntry? entry) => entry switch
    {
        { Excluded: true, ExclusionReason: RatioExclusionReasons.Unread }        => UnreadFigure,
        { Excluded: true, ExclusionReason: RatioExclusionReasons.NotMeaningful } => "No aplica",
        { Excluded: false, Measured: false }                                     => "Puntaje neutro",
        _                                                                        => "No determinable",
    };

    // ── BCRA section ────────────────────────────────────────────────────────────

    private static void AddBcraSection(
        ITextDocument doc, Fonts fonts,
        string? bcraDataJson, string? scoreBreakdownJson, int fiscalYear)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono;
        AddSectionHeading(doc, fonts, "Datos BCRA");

        if (string.IsNullOrWhiteSpace(bcraDataJson))
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        BcraData? bcra;
        try { bcra = JsonSerializer.Deserialize<BcraData>(bcraDataJson, JsonOpts); }
        catch { bcra = null; }

        if (bcra is null)
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        // Check if BCRA was excluded from scoring
        ScoreBreakdownWrapper? sbWrapper = null;
        if (!string.IsNullOrWhiteSpace(scoreBreakdownJson))
        {
            try { sbWrapper = JsonSerializer.Deserialize<ScoreBreakdownWrapper>(scoreBreakdownJson, JsonOpts); }
            catch { /* ignore */ }
        }

        bool bcraExcluded = sbWrapper?.BcraExcluded == true;

        // Deuda sub-section
        if (bcra.Deuda is not null)
        {
            Div deudaBlock = SubSection(fonts, "Central de Deudores");

            Table deudaTable = BuildTwoColTable(["Concepto", "Valor"], bold);

            // Situacion Maxima
            if (bcra.Deuda.SituacionMaxima.HasValue)
            {
                int sit = bcra.Deuda.SituacionMaxima.Value;
                DeviceRgb sitColor = ToneColor(PdfLabels.BcraSituacionTone(sit));

                // The value with the Portal's own label (lib/bcra-situacion.ts), so the table does not
                // depend on the prose being read.
                Paragraph sitParagraph = new Paragraph()
                    .SetMargin(0)
                    .Add(new Text($"{sit}").SetFont(mono).SetFontSize(9).SetFontColor(sitColor))
                    .Add(new Text(" \u2014 " + PdfLabels.BcraSituacion(sit)).SetFont(regular).SetFontSize(9).SetFontColor(ColorBodyText));

                deudaTable.AddCell(BodyCell("Situaci\u00f3n m\u00e1xima", regular, 9));
                deudaTable.AddCell(new Cell()
                    .SetBorder(Border.NO_BORDER)
                    .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
                    .SetPadding(6)
                    .SetTextAlignment(TextAlignment.RIGHT)
                    .Add(sitParagraph));
            }

            // Monto Total
            if (bcra.Deuda.MontoTotal.HasValue)
            {
                // Render the peso equivalent (× 1000) beside the thousands figure, using the unified
                // Pesos() treatment, so a figure in miles cannot be misread in a document where every
                // other amount is in whole pesos.
                string montoMiles = bcra.Deuda.MontoTotal.Value.ToString("N0", EsAr);
                string montoPesos = Pesos((double)(bcra.Deuda.MontoTotal.Value * 1000m));
                deudaTable.AddCell(BodyCell("Monto total (miles ARS)", regular, 9));
                deudaTable.AddCell(BodyCell($"{montoMiles} ({montoPesos})", mono, 9, align: TextAlignment.RIGHT));
            }

            // Entidades Informantes
            if (bcra.Deuda.EntidadesInformantes is { Count: > 0 } entidades)
            {
                string entLabel = entidades.Count <= 3
                    ? $"{entidades.Count} ({string.Join(", ", entidades)})"
                    : $"{entidades.Count} entidades";

                deudaTable.AddCell(BodyCell("Entidades informantes", regular, 9));
                deudaTable.AddCell(BodyCell(entLabel, regular, 9, align: TextAlignment.RIGHT));
            }

            // Periodo Consultado
            if (!string.IsNullOrWhiteSpace(bcra.Deuda.PeriodoConsultado))
            {
                deudaTable.AddCell(BodyCell("Per\u00edodo consultado", regular, 9));
                deudaTable.AddCell(BodyCell(FormatPeriodo(bcra.Deuda.PeriodoConsultado), mono, 9, align: TextAlignment.RIGHT));
            }

            // The situación trend the score used, read off the breakdown — the same figure the adjustment row
            // below states with its points.
            if (sbWrapper?.BcraTrendDelta is { } trendDelta)
            {
                string trend = trendDelta switch
                {
                    0   => "sin cambios",
                    < 0 => $"mejorando {Math.Abs(trendDelta).ToString(EsAr)} {(Math.Abs(trendDelta) == 1 ? "nivel" : "niveles")}",
                    _   => $"empeorando {trendDelta.ToString(EsAr)} {(trendDelta == 1 ? "nivel" : "niveles")}"
                };
                deudaTable.AddCell(BodyCell("Tendencia usada por el score", regular, 9));
                deudaTable.AddCell(BodyCell(trend, regular, 9, align: TextAlignment.RIGHT));
            }

            deudaBlock.Add(deudaTable);

            // Bureau data may correspond to a period AFTER the balance sheet date. Analysis exposes
            // only the fiscal YEAR (no closing date), so compare at year granularity and flag only when
            // the queried period's year is strictly later. Never assert a gap we cannot compute: a
            // period that is not a parseable YYYYMM renders no note at all.
            string? periodo = bcra.Deuda.PeriodoConsultado;
            if (periodo is { Length: 6 }
                && int.TryParse(periodo.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int periodoYear)
                && int.TryParse(periodo.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int periodoMonth)
                && periodoMonth is >= 1 and <= 12
                && periodoYear > fiscalYear)
            {
                deudaBlock.Add(new Paragraph(
                        $"Los datos de la Central de Deudores corresponden al período {periodoMonth:00}/{periodoYear}, posterior al cierre del ejercicio {fiscalYear}.")
                    .SetFont(regular)
                    .SetFontSize(8)
                    .SetFontColor(ColorMuted)
                    .SetMarginTop(4)
                    .SetMarginBottom(8));
            }

            doc.Add(deudaBlock);
        }
        else if (bcraExcluded)
        {
            doc.Add(new Paragraph("No se pudo consultar \u2014 dato excluido del score.")
                .SetFont(regular)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMarginBottom(8));
        }

        // Cheques rechazados — the counts and amounts of the analysis's own frozen snapshot (no new call). The
        // recovered share is by amount, rechazado − impago, the same reading the score's cheques signal uses.
        AddBcraChequesTable(doc, fonts, bcra.Cheques);

        // Macro sub-section
        if (bcra.Macro is not null)
        {
            Div macroBlock = SubSection(fonts, "Variables macroecon\u00f3micas");

            Table macroTable = BuildTwoColTable(["Variable", "Valor"], bold);

            if (bcra.Macro.InflacionMensual.HasValue)
            {
                macroTable.AddCell(BodyCell("Inflaci\u00f3n mensual", regular, 9));
                macroTable.AddCell(BodyCell(bcra.Macro.InflacionMensual.Value.ToString("F2", EsAr) + "%", mono, 9, align: TextAlignment.RIGHT));
            }

            if (bcra.Macro.TasaReferencia.HasValue)
            {
                macroTable.AddCell(BodyCell("Tasa TAMAR (TNA)", regular, 9));
                macroTable.AddCell(BodyCell(bcra.Macro.TasaReferencia.Value.ToString("F2", EsAr) + "%", mono, 9, align: TextAlignment.RIGHT));
            }

            doc.Add(macroBlock.Add(macroTable));
        }

        // Cambiario sub-section
        if (bcra.Cambiario?.TipoCambioOficial is not null)
        {
            Div tcBlock = SubSection(fonts, "Tipo de cambio");

            Table tcTable = BuildTwoColTable(["Variable", "Valor"], bold);
            tcTable.AddCell(BodyCell("TC oficial USD/ARS", regular, 9));
            tcTable.AddCell(BodyCell(bcra.Cambiario.TipoCambioOficial.Value.ToString("N2", EsAr), mono, 9, align: TextAlignment.RIGHT));
            doc.Add(tcBlock.Add(tcTable));
        }

        // Trend and cheques blocks — the two bounded adjustments that make scoreBcra differ from the
        // raw situación, rendered off the same breakdown blob.
        if (!string.IsNullOrWhiteSpace(scoreBreakdownJson))
        {
            ScoreBreakdownWrapper? wrapper;
            try { wrapper = JsonSerializer.Deserialize<ScoreBreakdownWrapper>(scoreBreakdownJson, JsonOpts); }
            catch { wrapper = null; }

            // Situación trend, on the 1000 DISPLAY scale with the display sign (+ = better), like the BCRA
            // component bar printed right above it: the engine's bcraTrendAdj is internal 0–100 points with
            // + = more risk, and is already the movement it actually applied (after the clamp), so
            // ScoreAnatomy.ToNosisDelta restates it exactly (internal a → display −10·a). The Portal's anatomy and
            // ScoreExplainer word it the same way since its matching PR. Rows scored without a bcraTrendAdj, or
            // where it moved nothing, state the movement alone.
            if (wrapper?.BcraTrendDelta is not null && wrapper.BcraTrendDelta.Value != 0)
            {
                int delta = wrapper.BcraTrendDelta.Value;
                bool improved = delta < 0;
                int steps = Math.Abs(delta);
                string levels = steps == 1 ? "1 nivel" : $"{steps.ToString(EsAr)} niveles";

                decimal? display = wrapper.BcraTrendAdj is { } adj ? ScoreAnatomy.ToNosisDelta(adj) : null;
                string adjText = display is { } points && points != 0m
                    ? $" \u2014 {ScoreAnatomy.FormatDelta(points)} pts en el componente BCRA"
                    : string.Empty;

                string trendText = improved
                    ? $"Situaci\u00f3n BCRA mejorando ({levels}){adjText}"
                    : $"Situaci\u00f3n BCRA empeorando ({levels}){adjText}";

                doc.Add(AdjustmentRow(trendText, regular, improved ? SuccessSoft : DangerSoft, marginTop: 8));
            }

            // Cheques rechazados (scoring algorithm 5). Rendered only when it actually moved the BCRA
            // component — chequesAdj is the REALISED movement — so a report never shows a penalty the
            // score never took. Restated on the 1000 scale as the Portal's anatomy restates it: the
            // engine adds it to scoreBcra BEFORE the weighted sum, so it is already inside the BCRA
            // contribution — information, never a term of the composite → final lines. Rows scored
            // before this signal existed carry no such key and render nothing.
            if (wrapper is not null
                && BuildAnatomy(wrapper, aiSuggestedScore: null).BcraNotes.FirstOrDefault() is { } chequesNote)
            {
                string recuperadoText = wrapper.ChequesProporcionRecuperada is { } proporcion
                    ? $"importe recuperado {(proporcion * 100m).ToString("F1", EsAr)} %"
                    : "importe recuperado n/d";

                string chequesText =
                    $"Cheques rechazados: {recuperadoText} \u2014 {ScoreAnatomy.FormatDelta(chequesNote.DeltaNosis)} pts (incluido en el score BCRA)";

                doc.Add(AdjustmentRow(chequesText, regular, chequesNote.DeltaNosis > 0m ? SuccessSoft : DangerSoft, marginTop: 8));
            }
        }
    }

    private static void AddBcraChequesTable(ITextDocument doc, Fonts fonts, BcraChequesData? cheques)
    {
        if (cheques is null) return;

        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono;
        Div block = SubSection(fonts, "Cheques rechazados");

        if (cheques.Estado != BcraChequesConsultaEstado.ChequesFound || cheques.TotalRechazados <= 0)
        {
            string estado = cheques.Estado == BcraChequesConsultaEstado.LookupFailed
                ? "No se pudo consultar el BCRA."
                : "Sin cheques rechazados.";
            doc.Add(block.Add(new Paragraph(estado).SetFont(regular).SetFontSize(9).SetFontColor(ColorBodyText).SetMarginBottom(8)));
            return;
        }

        Table table = BuildTwoColTable(["Concepto", "Valor"], bold);

        void Row(string label, string value)
        {
            table.AddCell(BodyCell(label, regular, 9));
            table.AddCell(BodyCell(value, mono, 9, align: TextAlignment.RIGHT));
        }

        Row("Cheques rechazados", cheques.TotalRechazados.ToString("N0", EsAr));
        Row("Impagos a la fecha", cheques.TotalImpagos.ToString("N0", EsAr));
        Row("Monto rechazado", Pesos((double)cheques.MontoTotalRechazado));

        decimal recuperado = Math.Max(0m, cheques.MontoTotalRechazado - cheques.MontoTotalImpago);
        string share = cheques.MontoTotalRechazado > 0m
            ? $" ({(recuperado / cheques.MontoTotalRechazado * 100m).ToString("F1", EsAr)} %)"
            : string.Empty;
        Row("Recuperado a la fecha", Pesos((double)recuperado) + share);

        doc.Add(block.Add(table));
    }

    /// <summary>"202609" → "09/2026"; anything else as received.</summary>
    internal static string FormatPeriodo(string? periodo) =>
        periodo is { Length: 6 } && periodo.All(char.IsAsciiDigit) && periodo[4..6] is var mm
            && int.Parse(mm, CultureInfo.InvariantCulture) is >= 1 and <= 12
            ? $"{mm}/{periodo[..4]}"
            : periodo ?? "\u2014";

    // ── LUFE declared-sales section ─────────────────────────────────────────────
    // The documentary counterpart to the §7.3 sales-drop row AddRiskSection states as a verdict: the
    // monthly sales and purchases the company declared, read from the write-once snapshot the score was
    // computed from (AnalysisResults.LufeDataJson), deflated with the CPI series frozen in the same
    // analysis's BCRA snapshot — the one ScoringService read — through the SAME arithmetic
    // (DeflactorIpc), never a second copy of it.
    //
    // Two different figures appear here and they are kept apart on purpose. The section's own 12-month
    // comparison describes the series; the variation the score USED is read verbatim off the breakdown
    // and rendered with the risk row's own clause builder, so the PDF and the score cannot tell two
    // stories about the same number.
    //
    // Unlike BCRA and CASFOG, which print a heading and "Datos no disponibles.", a missing or unusable
    // snapshot renders NOTHING — not even the heading. Most analyses predate this data or belong to an
    // entity never enriched from LUFE, and an empty section adds noise to every one of them.
    //
    // Deliberately NOT rendered: LufeData.Entidad and LufeData.Autoridades — names, CUITs and addresses
    // that are identity, not sales evidence.

    private const int LufeVentanaMeses = 12;

    private static void AddLufeVentasSection(
        ITextDocument doc, Fonts fonts,
        string? lufeDataJson, string? bcraDataJson, string? scoreBreakdownJson, decimal? aiSuggestedScore)
    {
        if (string.IsNullOrWhiteSpace(lufeDataJson)) return;

        LufeData? lufe;
        try { lufe = JsonSerializer.Deserialize<LufeData>(lufeDataJson, SnapshotJsonOpts); }
        catch { lufe = null; }

        if (lufe?.VentasComprasMensual is not { Count: > 0 } serie) return;

        // Keyed by the validated period, de-duplicated last-wins — the same defensive reading
        // ScoringService gives the stored series, which nothing re-sorts or re-validates on read.
        var meses = new SortedDictionary<string, LufeVentaCompraMes>(StringComparer.Ordinal);
        foreach (LufeVentaCompraMes? mes in serie)
        {
            if (mes is null) continue;
            if (DeflactorIpc.ShiftPeriodoMonths(mes.Periodo, 0) is not { } periodo) continue;
            meses[periodo] = mes;
        }

        // The anchor is the most recent month carrying SALES — the data, never the clock, and the same
        // "ultimo" the score anchors on. No month with sales: nothing to report, so no section at all.
        string? ancla = meses.LastOrDefault(kv => kv.Value.Ventas is not null).Key;
        if (ancla is null) return;

        // The window: the twelve CALENDAR months ending at the anchor, most recent first. A month absent
        // from the ragged series and a month present with Ventas null are the same fact — no declaration.
        var ventana = new List<string>(LufeVentanaMeses);
        for (int k = 0; k < LufeVentanaMeses; k++)
        {
            if (DeflactorIpc.ShiftPeriodoMonths(ancla, -k) is { } periodo)
                ventana.Add(periodo);
        }

        decimal? VentasDe(string periodo) =>
            meses.TryGetValue(periodo, out LufeVentaCompraMes? m) ? m.Ventas : null;

        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono;
        AddSectionHeading(doc, fonts, "Ventas declaradas (LUFE)");

        // ── Summary ─────────────────────────────────────────────────────────────
        var resumen = new List<string>(4);

        int     declarados   = ventana.Count(p => VentasDe(p) is not null);
        int     sinDeclarar  = LufeVentanaMeses - declarados;
        decimal? totalVentana = SumarVentas(ventana.Select(VentasDe));

        // ONE window is the score's: its variation verbatim from the breakdown, with the months it compared and
        // the companion nominal/real figure of that SAME window. The twelve-month total below is context only.
        resumen.Add(VentasScoreTexto(scoreBreakdownJson, aiSuggestedScore, serie, LeerIpc(bcraDataJson)));
        resumen.Add(
            $"Contexto (no usado por el score): ventas declaradas en los últimos 12 meses ({MesAnio(ventana[^1])} – {MesAnio(ancla)}) " +
            $"{(totalVentana is { } total ? Pesos((double)total) : "—")}, sobre {declarados} " +
            $"{(declarados == 1 ? "mes" : "meses")} con declaración; {sinDeclarar} de {LufeVentanaMeses} sin declaración.");

        Cell resumenCell = new Cell()
            .SetBackgroundColor(ColorSectionBg)
            .SetBorder(Border.NO_BORDER)
            .SetPaddingLeft(8)
            .SetPaddingRight(8)
            .SetPaddingTop(6)
            .SetPaddingBottom(6);

        foreach (string linea in resumen)
        {
            resumenCell.Add(new Paragraph(linea)
                .SetFont(regular)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMargin(0)
                .SetMarginBottom(2));
        }

        // Short by construction (two or three lines): never split. Kept together, the section heading's
        // keep-with-next carries the heading along with it, so heading and box always share a page.
        doc.Add(new Table(UnitValue.CreatePercentArray([1f]))
            .UseAllAvailableWidth()
            .SetMarginBottom(10)
            .SetKeepTogether(true)
            .AddCell(resumenCell));

        // ── Monthly table ───────────────────────────────────────────────────────
        // Twelve rows: it may split, never leaving fewer than two rows on either side (AddRowBrokenTable).
        var mensualRows = new List<Func<Cell[]>>(ventana.Count);
        for (int i = 0; i < ventana.Count; i++)
        {
            string periodo = ventana[i];
            meses.TryGetValue(periodo, out LufeVentaCompraMes? mes);
            DeviceRgb? rowBg = i % 2 == 1 ? ColorSectionBg : null;

            string ratio = mes is { Ventas: { } v, Compras: { } c } && c > 0m
                ? (v / c).ToString("F2", EsAr) + "x"
                : "—";

            mensualRows.Add(() =>
            [
                BodyCell(MesAnio(periodo), mono, 9, rowBg),
                MontoLufeCell(mes?.Ventas,  mono, regular, rowBg),
                MontoLufeCell(mes?.Compras, mono, regular, rowBg),
                BodyCell(ratio, mono, 9, rowBg, TextAlignment.RIGHT),
            ]);
        }

        AddRowBrokenTable(doc, fonts, "Ventas y compras mensuales",
            () => LufeTable(["Período", "Ventas", "Compras", "Ventas/Compras"], [22f, 29f, 29f, 20f], bold),
            mensualRows,
            () => LufeCaption("Importes nominales en pesos, tal como los declara LUFE (sin deflactar).", regular));

        // ── Annual indicators (per balance period) ──────────────────────────────
        LufeIndicador[] anuales = (lufe.IndicadoresHistoricos ?? [])
            .Where(i => i is not null && (i.VentasPb is not null || i.ComprasPb is not null || i.VtasMensualesProm is not null))
            .OrderByDescending(i => i.Periodo ?? decimal.MinValue)
            .ToArray();

        if (anuales.Length == 0) return;

        // Compras 0 on an annual row is LUFE's way of saying "no figure", not a year without purchases — the
        // Portal's LUFE card reads it the same way. A column empty on EVERY row is not printed at all.
        static decimal? ComprasDe(LufeIndicador ind) => ind.ComprasPb is 0m ? null : ind.ComprasPb;

        var columnas = new List<(string Heading, Func<LufeIndicador, decimal?> Value)>
        {
            ("Ventas",                 ind => ind.VentasPb),
            ("Compras",                ComprasDe),
            ("Venta mensual promedio", ind => ind.VtasMensualesProm),
        };
        columnas.RemoveAll(col => anuales.All(ind => col.Value(ind) is null));
        if (columnas.Count == 0) return;

        // One row per balance: kept whole up to six rows, otherwise split with two rows or more on each side.
        float valueWidth = 78f / columnas.Count;
        var anualRows = new List<Func<Cell[]>>(anuales.Length);
        for (int i = 0; i < anuales.Length; i++)
        {
            LufeIndicador ind   = anuales[i];
            DeviceRgb?    rowBg = i % 2 == 1 ? ColorSectionBg : null;

            anualRows.Add(() =>
            [
                BodyCell(PeriodoBalance(ind.Periodo), mono, 9, rowBg),
                .. columnas.Select(col =>
                    BodyCell(col.Value(ind) is { } monto ? Pesos((double)monto) : "—", mono, 9, rowBg, TextAlignment.RIGHT)),
            ]);
        }

        AddRowBrokenTable(doc, fonts, "Indicadores anuales (balances LUFE)",
            () => LufeTable(
                ["Período", .. columnas.Select(col => col.Heading)],
                [22f, .. columnas.Select(_ => valueWidth)], bold),
            anualRows,
            () => LufeCaption("Importes tal como los informa LUFE para cada balance presentado.", regular));
    }

    /// <summary>
    /// A titled multi-row table that may cross a page, with its caption. iText has no row widow/orphan control, so
    /// the breaks are measured and placed here (<see cref="TableRowBreaks"/>): up to six body rows the table is kept
    /// whole; a longer one never leaves fewer than two body rows on either side of a break. Every chunk is a fresh
    /// table from <paramref name="newTable"/>, so a continued page repeats the header exactly as iText's own split
    /// would. The sub-heading travels with the first chunk and the caption with the last. Rows are factories: an
    /// iText cell cannot belong to two tables, and measuring builds the same rows more than once.
    /// </summary>
    private static void AddRowBrokenTable(
        ITextDocument doc, Fonts fonts, string subHeading, Func<Table> newTable,
        IReadOnlyList<Func<Cell[]>> rows, Func<Paragraph> caption)
    {
        Div Block(int start, int end, bool first, bool last)
        {
            Div block = new Div().SetKeepTogether(true);
            if (first) block.Add(SubHeadingParagraph(fonts, subHeading));

            Table table = newTable();
            for (int i = start; i < end; i++)
            {
                foreach (Cell cell in rows[i]())
                    table.AddCell(cell);
            }

            block.Add(table);
            if (last) block.Add(caption());
            return block;
        }

        Rectangle page          = doc.GetPageEffectiveArea(PageSize.A4);
        Rectangle? current      = (doc.GetRenderer() as iText.Layout.Renderer.RootRenderer)?.GetCurrentArea()?.GetBBox();
        float      width        = current?.GetWidth() ?? page.GetWidth();
        float      available    = current?.GetHeight() ?? page.GetHeight();

        IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(
            rows.Count, available, page.GetHeight(),
            (start, end, first, last) => MeasureHeight(doc, Block(start, end, first, last), width));

        foreach (TableRowBreaks.Chunk chunk in chunks)
        {
            if (chunk.BreakBefore)
                doc.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));

            doc.Add(Block(chunk.Start, chunk.End, chunk.Start == 0, chunk.End == rows.Count));
        }
    }

    // The variation the score USED, verbatim from the breakdown — never recomputed here. Rendered with
    // VentasVariacionTexto, the risk row's own clause, and with the risk row's own 1000-scale delta
    // (the anatomy's, rounding residual included), so the two sections cannot disagree. Stated even
    // when it earned no penalty: growth, or a drop inside the dead-band, is a real reading too.
    private static string VentasScoreTexto(
        string?                              scoreBreakdownJson,
        decimal?                             aiSuggestedScore,
        List<LufeVentaCompraMes>?            serie,
        IReadOnlyDictionary<string, double>? ipcMensual)
    {
        const string NoEvaluada = "El score de este análisis no evaluó la variación de ventas LUFE.";

        if (string.IsNullOrWhiteSpace(scoreBreakdownJson)) return NoEvaluada;

        ScoreBreakdownWrapper? wrapper;
        try { wrapper = JsonSerializer.Deserialize<ScoreBreakdownWrapper>(scoreBreakdownJson, JsonOpts); }
        catch { wrapper = null; }

        bool hasAdjustment = wrapper?.VentasAdj is { } ventasAdj && ventasAdj != 0m;
        if (wrapper is null || (wrapper.VentasVariacion is null && !hasAdjustment))
            return NoEvaluada;

        string ajuste = "sin penalidad";
        if (hasAdjustment)
        {
            decimal delta = AdjustmentDeltaNosis(
                BuildAnatomy(wrapper, aiSuggestedScore),
                HasCompleteAnatomy(wrapper, aiSuggestedScore),
                ScoreAnatomy.AdjustmentKey.Ventas,
                wrapper.VentasAdj!.Value);
            ajuste = $"{ScoreAnatomy.FormatDelta(delta)} pts";
        }

        string texto = $"Variación usada por el score: {VentasVariacionTexto(wrapper)} — {ajuste}.";

        // The months that variation compared, and the companion figure of the SAME window (nominal beside a
        // real one, real beside a nominal one when the CPI covers it) — through ScoringService's own
        // arithmetic, and only when that arithmetic lands on the basis the breakdown recorded: a snapshot that
        // no longer reproduces it prints the score's figure alone.
        try
        {
            if (VentanaReproducible(wrapper.VentasBase, serie, ipcMensual) is { } ventanaTexto)
            {
                texto += $" Ventana: {ventanaTexto}.";

                ScoringService.VentasVariacionResult? companion = wrapper.VentasVariacionEsReal == true
                    ? ScoringService.ComputeVentasVariacion(serie, null)
                    : ScoringService.ComputeVentasVariacion(serie, ipcMensual);

                if (companion is { } otra && otra.Base == wrapper.VentasBase && otra.EsReal != (wrapper.VentasVariacionEsReal == true))
                    texto += $" Variación {(otra.EsReal ? "real" : "nominal")} de la misma ventana: {Porcentaje(otra.Valor)}.";
            }
        }
        catch (Exception)
        {
            // Context only: the score's own figure above stands without it.
        }

        return texto;
    }

    // The window text only when ScoringService's own arithmetic, run on this snapshot, lands on the basis the
    // breakdown recorded — the months then ARE the ones the score compared. Otherwise null: nothing is guessed.
    private static string? VentanaReproducible(
        string? basis, List<LufeVentaCompraMes>? serie, IReadOnlyDictionary<string, double>? ipcMensual)
    {
        if (string.IsNullOrWhiteSpace(basis)) return null;
        if (ScoringService.ComputeVentasVariacion(serie, ipcMensual) is not { } check || check.Base != basis) return null;
        return VentanaVentasTexto(serie, basis);
    }

    // The months the score's basis compared, from the same snapshot and the same anchor (the newest month
    // carrying sales): interanual_3m → the three months to the anchor against the same three a year earlier;
    // interanual → the anchor against its month a year earlier; trimestral → the three most recent months
    // present against the three before them.
    private static string? VentanaVentasTexto(List<LufeVentaCompraMes>? serie, string basis)
    {
        if (serie is null) return null;

        var meses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (LufeVentaCompraMes? mes in serie)
        {
            if (mes is not { Ventas: not null }) continue;
            if (DeflactorIpc.ShiftPeriodoMonths(mes.Periodo, 0) is { } periodo) meses.Add(periodo);
        }

        if (meses.Count == 0) return null;
        string ancla = meses.Max!;

        // "07–09/2026" within a year, "11/2025–01/2026" across one.
        string Rango(string desde, string hasta) =>
            desde == hasta ? MesAnio(desde)
            : desde[..4] == hasta[..4] ? $"{desde[4..6]}–{MesAnio(hasta)}"
            : $"{MesAnio(desde)}–{MesAnio(hasta)}";

        switch (basis)
        {
            case "interanual_3m"
                when DeflactorIpc.ShiftPeriodoMonths(ancla, -2) is { } desde
                  && DeflactorIpc.ShiftPeriodoMonths(ancla, -12) is { } hastaPrevio
                  && DeflactorIpc.ShiftPeriodoMonths(ancla, -14) is { } desdePrevio:
                return $"{Rango(desde, ancla)} vs. {Rango(desdePrevio, hastaPrevio)} (interanual, 3 meses)";

            case "interanual" when DeflactorIpc.ShiftPeriodoMonths(ancla, -12) is { } previo:
                return $"{MesAnio(ancla)} vs. {MesAnio(previo)} (interanual)";

            case "trimestral" when meses.Count >= 6:
                string[] p = meses.ToArray();
                return $"{Rango(p[^3], p[^1])} vs. {Rango(p[^6], p[^4])} (trimestral)";

            default:
                return null;
        }
    }

    // The monthly CPI series off the analysis's own frozen BCRA snapshot — the object ScoringService
    // received as macroData. Null when absent or unreadable, which the caller reports as nominal-only.
    private static IReadOnlyDictionary<string, double>? LeerIpc(string? bcraDataJson)
    {
        if (string.IsNullOrWhiteSpace(bcraDataJson)) return null;

        try { return JsonSerializer.Deserialize<BcraData>(bcraDataJson, JsonOpts)?.Macro?.InflacionMensualSerie; }
        catch { return null; }
    }

    // Null when no month carries a figure, so an all-undeclared window reads "—", never "$ 0".
    private static decimal? SumarVentas(IEnumerable<decimal?> valores)
    {
        decimal? total = null;
        try
        {
            foreach (decimal? v in valores)
            {
                if (v is { } monto) total = (total ?? 0m) + monto;
            }
        }
        catch (OverflowException)
        {
            return null;
        }

        return total;
    }

    // "YYYYMM" → "MM/AAAA" by slicing the validated key: no DateTime, so no calendar, culture or
    // timezone can move the month.
    private static string MesAnio(string periodo) => periodo[4..6] + "/" + periodo[..4];

    // A LUFE balance period arrives as a number: "YYYY00" for an annual balance, "YYYYMM" when it
    // carries the closing month, occasionally a bare year. Anything else is printed as received.
    private static string PeriodoBalance(decimal? periodo)
    {
        if (periodo is not { } p || p != decimal.Truncate(p) || p <= 0m) return "—";

        string texto = ((long)p).ToString(CultureInfo.InvariantCulture);

        if (texto.Length == 4) return texto;

        if (texto.Length == 6 &&
            int.TryParse(texto.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mes))
        {
            if (mes == 0)             return texto[..4];
            if (mes is >= 1 and <= 12) return MesAnio(texto);
        }

        return texto;
    }

    // "$ 1.234.567", or a muted "Sin declaración" — never "$ 0": LUFE's null means no declaration was
    // filed for the month, not that the company sold or bought nothing.
    private static Cell MontoLufeCell(decimal? monto, PdfFont mono, PdfFont regular, DeviceRgb? rowBg)
    {
        if (monto is null)
            return StatusCell("Sin declaración", regular, 9, ColorMuted, rowBg, mono, 9, TextAlignment.RIGHT);

        Cell cell = new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
            .SetPadding(6)
            .SetTextAlignment(TextAlignment.RIGHT);

        if (rowBg is not null)
            cell.SetBackgroundColor(rowBg);

        return cell.Add(new Paragraph(Pesos((double)monto.Value)).SetFont(mono).SetFontSize(9).SetFontColor(ColorBodyText));
    }

    // Header row in the deep-base treatment every other table in the document uses.
    private static Table LufeTable(string[] headings, float[] widths, PdfFont bold)
    {
        Table table = new Table(UnitValue.CreatePercentArray(widths))
            .UseAllAvailableWidth()
            .SetMarginBottom(4);

        for (int i = 0; i < headings.Length; i++)
        {
            table.AddHeaderCell(AlignHeader(new Cell()
                .SetBackgroundColor(ColorTableHead)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(7)
                .Add(new Paragraph(headings[i])
                    .SetFont(bold)
                    .SetFontSize(9)
                    .SetFontColor(ColorTableHeadFg)), i == 0 ? TextAlignment.LEFT : TextAlignment.RIGHT, BodyCellPadding));
        }

        return table;
    }

    private static Paragraph LufeCaption(string text, PdfFont regular) =>
        new Paragraph(text)
            .SetFont(regular)
            .SetFontSize(8)
            .SetFontColor(ColorMuted)
            .SetMarginBottom(8);

    // A signed fraction as an es-AR percentage with one decimal: +12,3 % / -4,5 %.
    private static string Porcentaje(decimal fraccion) =>
        (fraccion * 100m).ToString("+0.0;\u22120.0;0.0", EsAr) + "\u00a0%";

    // ── CASFOG section ──────────────────────────────────────────────────────────
    // The documentary counterpart to the BCRA section, and the evidence behind the §7.4
    // guarantee-arrears penalty that AddRiskSection states as a one-line verdict. Reads the
    // write-once snapshot the score itself was computed from (AnalysisResults.CasfogDataJson), so the
    // figures printed here and the penalty printed there can never disagree.
    //
    // Deliberately NOT rendered: CasfogData.Certificados and CasfogData.Socios. Those are entity
    // metadata rather than credit evidence, and Socios carries partner names and CUITs this report
    // has no reason to reproduce.

    private static void AddCasfogSection(
        ITextDocument doc, Fonts fonts, string? casfogDataJson)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono, med = fonts.BodyMedium;
        AddSectionHeading(doc, fonts, "Datos CASFOG");

        if (string.IsNullOrWhiteSpace(casfogDataJson))
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        CasfogData? casfog;
        try { casfog = JsonSerializer.Deserialize<CasfogData>(casfogDataJson, SnapshotJsonOpts); }
        catch { casfog = null; }

        if (casfog is null)
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        // Tri-state, mirroring the vendor's own contract (CasfogConsultaEstado). "Nothing on file" and
        // "the call failed" are different facts and must not collapse into one another — the same
        // honesty the BCRA section shows for its excluded case. Neither renders an empty table.
        if (casfog.Estado is CasfogConsultaEstado.NoDataReported or CasfogConsultaEstado.LookupFailed)
        {
            string estadoTexto = casfog.Estado == CasfogConsultaEstado.NoDataReported
                ? "Sin datos informados por CASFOG."
                : "No se pudo consultar CASFOG.";

            doc.Add(new Paragraph(estadoTexto)
                .SetFont(regular)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMarginBottom(8));
            return;
        }

        CasfogGarantias garantias = casfog.Garantias;

        // ── Totals (Central de garantías) ───────────────────────────────────────
        // One row per aggregate, each rendered ONLY when it has a value — an absent metric renders no
        // row at all, exactly as the BCRA deuda sub-table does. A missing figure is never shown as 0.
        Table totalesTable  = BuildTwoColTable(["Concepto", "Valor"], bold);
        bool  hasTotalesRow = false;

        if (garantias.SaldoBrutoGarantiasVigentes.HasValue)
        {
            totalesTable.AddCell(BodyCell("Saldo bruto de garantías vigentes", regular, 9));
            totalesTable.AddCell(BodyCell(
                Pesos((double)garantias.SaldoBrutoGarantiasVigentes.Value), mono, 9, align: TextAlignment.RIGHT));
            hasTotalesRow = true;
        }

        if (garantias.CantidadGarantiasOtorgadas.HasValue)
        {
            totalesTable.AddCell(BodyCell("Garantías otorgadas (cantidad)", regular, 9));
            totalesTable.AddCell(BodyCell(
                garantias.CantidadGarantiasOtorgadas.Value.ToString("N0", EsAr), mono, 9, align: TextAlignment.RIGHT));
            hasTotalesRow = true;
        }

        if (garantias.MontoGarantiasOtorgadas.HasValue)
        {
            totalesTable.AddCell(BodyCell("Monto de garantías otorgadas", regular, 9));
            totalesTable.AddCell(BodyCell(
                Pesos((double)garantias.MontoGarantiasOtorgadas.Value), mono, 9, align: TextAlignment.RIGHT));
            hasTotalesRow = true;
        }

        if (garantias.CantidadGarantiasVigentes.HasValue)
        {
            totalesTable.AddCell(BodyCell("Garantías vigentes (cantidad)", regular, 9));
            totalesTable.AddCell(BodyCell(
                garantias.CantidadGarantiasVigentes.Value.ToString("N0", EsAr), mono, 9, align: TextAlignment.RIGHT));
            hasTotalesRow = true;
        }

        if (hasTotalesRow)
        {
            doc.Add(SubSection(fonts, "Central de garantías").Add(totalesTable));
        }

        // ── Per-SGR breakdown (Reparto por SGR) ─────────────────────────────────
        List<CasfogGarantiaSgr> filas = garantias.PorSgr;

        if (filas.Count > 0)
        {
            Div sgrBlock = SubSection(fonts, "Reparto por SGR");

            // A column the vendor left empty on EVERY row is not printed: a column of dashes says nothing. One
            // reported value keeps the column, the rest of it as dashes.
            bool showSituacion = filas.Any(f => f.SituacionDeuda is not null);
            bool showAtraso    = filas.Any(f => f.DiasAtraso is not null);

            // Each header aligns like its column: the figures right, the status labels centred.
            var sgrColumns = new List<(string Heading, float Width, TextAlignment Align)>
            {
                ("SGR", 30f, TextAlignment.LEFT), ("Saldo bruto vigente", 22f, TextAlignment.RIGHT),
            };
            if (showSituacion) sgrColumns.Add(("Situación CASFOG (0-10)", 18f, TextAlignment.RIGHT));
            if (showAtraso)    sgrColumns.Add(("Días de atraso", 15f, TextAlignment.RIGHT));
            sgrColumns.Add(("Estado", 15f, TextAlignment.CENTER));

            Table sgrTable = new Table(UnitValue.CreatePercentArray(sgrColumns.Select(c => c.Width).ToArray()))
                .UseAllAvailableWidth()
                .SetMarginBottom(8)
                .SetKeepTogether(true);

            foreach ((string heading, _, TextAlignment align) in sgrColumns)
            {
                sgrTable.AddHeaderCell(AlignHeader(new Cell()
                    .SetBackgroundColor(ColorTableHead)
                    .SetBorder(Border.NO_BORDER)
                    .SetPadding(7)
                    .Add(new Paragraph(heading)
                        .SetFont(bold)
                        .SetFontSize(9)
                        .SetFontColor(ColorTableHeadFg)), align, BodyCellPadding));
            }

            // Largest exposure first — how an analyst reads it. A row whose balance the vendor did not
            // report sorts LAST: it is the least informative row, and must not sort as if it were zero.
            bool altRow = false;

            foreach (CasfogGarantiaSgr fila in filas
                         .OrderByDescending(f => f.SaldoBrutoVigente ?? decimal.MinValue))
            {
                DeviceRgb? rowBg = altRow ? ColorSectionBg : null;

                string sgrLabel = string.IsNullOrWhiteSpace(fila.Sgr) ? "—" : fila.Sgr;

                // "N/D", never 0. The vendor's "-" sentinel became null in CasfogService.ParseString,
                // and "not reported" is not "no exposure" — the distinction §7.4 relies on too, since a
                // null balance joins neither side of its proportion.
                string saldoLabel = fila.SaldoBrutoVigente is { } saldo
                    ? Pesos((double)saldo)
                    : "N/D";

                // The vendor's own 0-10 debt classification, shown but NOT interpreted: it is a
                // different scale from the BCRA 1-6 situación that AddBcraSection colours with
                // ColorOk/Warn/Danger, and colouring it against those thresholds would mis-state the
                // row. Body colour, no status treatment — the column header carries the scale.
                string situacionLabel = fila.SituacionDeuda?.ToString("N0", EsAr) ?? "—";
                string atrasoLabel    = fila.DiasAtraso?.ToString("N0", EsAr)     ?? "—";

                // Arrears is CantidadEnMora > 0 — the exact fact the score uses
                // (ScoringService.ComputeProporcionEnMora), never DiasAtraso and never SituacionDeuda.
                // A null count is "al día", matching the engine, which counts such a row into the
                // denominator only.
                bool      enMora      = fila.CantidadEnMora is > 0;
                string    moraLabel   = enMora ? "en mora" : "al día";
                DeviceRgb moraColor   = enMora ? ColorDanger : ColorMuted;

                sgrTable.AddCell(BodyCell(sgrLabel,        regular, 9, rowBg));
                sgrTable.AddCell(BodyCell(saldoLabel,      mono,    9, rowBg, TextAlignment.RIGHT));
                if (showSituacion)
                    sgrTable.AddCell(BodyCell(situacionLabel, mono, 9, rowBg, TextAlignment.RIGHT));
                if (showAtraso)
                    sgrTable.AddCell(BodyCell(atrasoLabel,    mono, 9, rowBg, TextAlignment.RIGHT));
                sgrTable.AddCell(StatusCell(moraLabel, med, 8, moraColor, rowBg, mono, 9));

                altRow = !altRow;
            }

            doc.Add(sgrBlock.Add(sgrTable));
        }

        // DataFound, yet nothing to print: say so rather than leaving a bare heading.
        if (!hasTotalesRow && filas.Count == 0)
            doc.Add(NotAvailableParagraph(regular));
    }

    // ── Anomalies section ───────────────────────────────────────────────────────

    private void AddAnomaliesSection(
        ITextDocument doc, Fonts fonts, AnalysisResult result)
    {
        PdfFont regular = fonts.Body;
        AddSectionHeading(doc, fonts, "Anomal\u00edas detectadas");

        string? anomaliesJson = result.Anomalies;

        if (string.IsNullOrWhiteSpace(anomaliesJson))
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        List<string>? anomalies;
        try { anomalies = JsonSerializer.Deserialize<List<string>>(anomaliesJson, JsonOpts); }
        catch { anomalies = null; }

        if (anomalies is null || anomalies.Count == 0)
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        // Percentage reconciliation (NOT persisted \u2014 recomputed at export time, same fix and same
        // fault-isolation shape as AddExecutiveSummarySection above): repairs already-persisted
        // anomalies without a backfill. Rewriting an anomaly STRING'S TEXT in place is not a third
        // writer into the array (see the two-writer invariant note in AnalysisController) \u2014 no
        // entry is added or removed, only an existing entry's wording is corrected.
        try
        {
            // InPrintedUnits: the prose quotes amounts as printed, BalanceJson persists pesos.
            var accounts = string.IsNullOrWhiteSpace(result.BalanceJson)
                ? null
                : JsonSerializer.Deserialize<BalanceAccounts>(result.BalanceJson, JsonOpts)?.InPrintedUnits();

            if (accounts is not null)
            {
                var allUnresolved = new List<string>();

                for (int i = 0; i < anomalies.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(anomalies[i])) continue;

                    var narrative = _alertRatioReconciliationService.ReconcileNarrative(anomalies[i], accounts);

                    if (narrative.Corrections.Count > 0)
                    {
                        anomalies[i] = narrative.Text;
                        foreach (var c in narrative.Corrections)
                            _logger.LogWarning(
                                "PDF export: anomaly percentage reconciled result {ResultId} analysis {AnalysisId} " +
                                "tenant {TenantId}: clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                result.ResultId, result.AnalysisId, result.TenantId,
                                c.Clause, c.StatedPercent, c.ExpectedPercent, c.Denominator);
                    }

                    if (narrative.UnresolvedClauses.Count > 0)
                        allUnresolved.AddRange(narrative.UnresolvedClauses);
                }

                if (allUnresolved.Count > 0)
                    _logger.LogWarning(
                        "PDF export: anomalies have {Count} unverifiable percentage clause(s) result {ResultId} " +
                        "analysis {AnalysisId} tenant {TenantId}: {Clauses}",
                        allUnresolved.Count, result.ResultId, result.AnalysisId, result.TenantId,
                        string.Join(" | ", allUnresolved));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PDF export: anomaly reconciliation failed for result {ResultId}; rendering original text.",
                result.ResultId);
        }

        DeviceRgb anomalyBg = WarningSoft; // flat warning tint

        foreach (string rawAnomaly in anomalies)
        {
            if (string.IsNullOrWhiteSpace(rawAnomaly)) continue;

            // Compatibility shim for anomalies persisted before the es-AR source fix in
            // AnalysisController (the SectionsVsNetChange / BeginningPlusChangeVsEnd arms). Newly
            // persisted anomalies already arrive formatted and are left untouched.
            string anomaly = ReformatReconciliationAnomaly(rawAnomaly);

            Table row = new Table(UnitValue.CreatePercentArray([1f]))
                .UseAllAvailableWidth()
                .SetMarginBottom(4);

            row.AddCell(new Cell()
                .SetBackgroundColor(anomalyBg)
                .SetBorder(Border.NO_BORDER)
                .SetPaddingLeft(8)
                .SetPaddingTop(6)
                .SetPaddingBottom(6)
                .Add(new Paragraph($"\u2022 {anomaly}")
                    .SetFont(regular)
                    .SetFontSize(9)
                    .SetFontColor(ColorBodyText)
                    .SetMargin(0)));

            doc.Add(row);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // ── Chart primitives ──────────────────────────────────────────────────────

    private const int RoundCap = iText.Kernel.Pdf.Canvas.PdfCanvasConstants.LineCapStyle.ROUND;

    // A horizontal progress bar: rounded track in surfaceBorder, fill in the status colour, width =
    // fraction of the track. A NULL fraction draws the track ONLY, with no fill element at all — the
    // honest "unmeasured" state (excluded BCRA / non-computable indicator), distinct from a 0% fill.
    private static Image DrawBar(ITextDocument doc, double? fraction, DeviceRgb fill, float W = 88f)
    {
        const float H = 7f;
        float r = H / 2f;
        return DrawVector(doc, W, H, canvas =>
        {
            canvas.SetFillColor(SurfaceBorder).RoundRectangle(0, 0, W, H, r).Fill();
            if (fraction is { } f && f > 0)
            {
                float w = (float)Math.Clamp(f, 0.0, 1.0) * W;
                if (w < H) w = H;   // keep the rounded cap legible for a tiny value
                canvas.SetFillColor(fill).RoundRectangle(0, 0, w, H, r).Fill();
            }
        });
    }

    // A small rounded colour swatch for the composition legends.
    private static Image DrawSwatch(ITextDocument doc, DeviceRgb color)
    {
        const float S = 8f;
        return DrawVector(doc, S, S, canvas => canvas.SetFillColor(color).RoundRectangle(0, 0, S, S, 2f).Fill());
    }

    // ── Score gauge ────────────────────────────────────────────────────────────
    // Drawn only when a score exists (never an empty or zero arc). The colour keys on the RAW level value
    // (Low/Medium/High/Critical), never on the translated label. No numeric range caption: the 1000-scale
    // bands are per-tenant configurable, so printing one here would be inventing a figure.

    /// <summary>"Sin score" and why, in the verdict's own words (score credibility 1b-3).</summary>
    private static void AddNoScoreNote(ITextDocument doc, Fonts fonts, ScoreReliabilityResponse? reliability)
    {
        string why = reliability?.Reasons
                         .FirstOrDefault(r => r.Code == ScoreReliabilityCodes.ScoreUnavailable)?.Message
                     ?? ScoreReliabilityMessages.ScoreUnavailable;

        doc.Add(new Paragraph("Sin score")
            .SetFont(fonts.HeadingBold)
            .SetFontSize(14)
            .SetFontColor(ColorMuted)
            .SetMarginBottom(2));
        doc.Add(new Paragraph(why)
            .SetFont(fonts.Body)
            .SetFontSize(9)
            .SetFontColor(ColorMuted)
            .SetMarginBottom(10));
    }

    /// <summary>The 0–1000 gauge: arc track + fill, the score numeral inside the ring and "/1000" to its right.</summary>
    private static Image BuildScoreGauge(ITextDocument doc, Fonts fonts, decimal score, string? rawLevel)
    {
        DeviceRgb scoreColor = ToneColor(PdfLabels.RiskTone(rawLevel));
        int    nosis    = ScoreScale.ToNosis(score);
        double fraction = Math.Clamp(nosis / 1000.0, 0.0, 1.0);

        // ONE XObject, so no layout engine can misalign the pieces. The arc is the top semicircle (angles
        // 180→0); its ring centreline is at R with stroke lw, so the inner edge sits at R - lw/2.
        const float W = 150f;
        float cx = W / 2f, cy = 6f, R = 62f, lw = 11f;
        float innerR = R - lw / 2f;
        float H = cy + R + lw / 2f + 1f;

        float HalfChord(float dy) => dy >= innerR ? 0f : (float)Math.Sqrt(innerR * innerR - dy * dy);

        string       numeralStr = nosis.ToString(EsAr);
        const string slashStr   = " /1000";
        const float  slashSize  = 9f;
        float        numCy      = cy + 0.34f * R;

        // Size the numeral from the inner radius; shrink until it and "/1000" clear the ring — never truncate.
        float numSize = 32f, numW = 0f, slashW = 0f, numBaseline = 0f;
        while (true)
        {
            numW        = fonts.HeadingBold.GetWidth(numeralStr, numSize);
            slashW      = fonts.Heading.GetWidth(slashStr, slashSize);
            numBaseline = numCy - 0.36f * numSize;
            float numTop   = numBaseline + 0.72f * numSize;
            float slashTop = numBaseline + 0.72f * slashSize;
            bool numFits   = numW / 2f          <= 0.95f * HalfChord(numTop   - cy);
            bool slashFits = numW / 2f + slashW <= 0.95f * HalfChord(slashTop - cy);
            if ((numFits && slashFits) || numSize <= 14f) break;
            numSize -= 1f;
        }

        return DrawVector(doc, W, H, canvas =>
        {
            canvas.SetLineCapStyle(RoundCap).SetLineWidth(lw);
            canvas.SetStrokeColor(SurfaceBorder)
                  .Arc(cx - R, cy - R, cx + R, cy + R, 180, -180).Stroke();
            if (fraction > 0)
                canvas.SetStrokeColor(scoreColor)
                      .Arc(cx - R, cy - R, cx + R, cy + R, 180, -(180.0 * fraction)).Stroke();

            float numX = cx - numW / 2f;
            canvas.BeginText().SetFontAndSize(fonts.HeadingBold, numSize).SetFillColor(Theme.ScoreFigure)
                  .MoveText(numX, numBaseline).ShowText(numeralStr).EndText();
            canvas.BeginText().SetFontAndSize(fonts.Heading, slashSize).SetFillColor(LabelSecondary)
                  .MoveText(numX + numW, numBaseline).ShowText(slashStr).EndText();
        });
    }

    // ── Patrimonial composition — two stacked bars ─────────────────────────────
    // Colour mapping — three DISJOINT hue families so no colour ever carries two meanings across the
    // pair: the ASSET bar uses accent shades (corriente = accent, no corriente = a darker accent); the
    // LIABILITIES-AND-EQUITY bar uses amber for the two liability tranches (corriente = warning, no
    // corriente = a darker warning) and VIOLET for equity (its own family — assets keep accent). These
    // hues are chosen ONLY to separate the segments; they carry NO good/bad valence — a liability is
    // not a bad thing — and red/green are deliberately avoided so nothing reads as "bad"/"good".
    private static void AddPatrimonialComposition(ITextDocument doc, Fonts fonts, BalanceSheet b)
    {
        bool activoOk = b.CurrentAssets.HasValue && b.NonCurrentAssets.HasValue;
        bool pasivoOk = b.CurrentLiabilities.HasValue && b.NonCurrentLiabilities.HasValue && b.TotalEquity.HasValue;
        if (!activoOk && !pasivoOk) return;   // never infer a missing figure by subtraction

        DeviceRgb accentDark    = Over(Accent,          Label, 0.42);
        DeviceRgb liability     = Theme.Liability;
        DeviceRgb liabilityDark = Over(Theme.Liability, Label, 0.42);

        List<(string Label, double Value, DeviceRgb Color)>? assetSegs = null;
        if (activoOk)
            assetSegs = new()
            {
                ("Activo corriente",    (double)b.CurrentAssets!.Value,    Accent),
                ("Activo no corriente", (double)b.NonCurrentAssets!.Value, accentDark),
            };

        List<(string Label, double Value, DeviceRgb Color)>? finSegs = null;
        if (pasivoOk)
            finSegs = new()
            {
                ("Pasivo corriente",    (double)b.CurrentLiabilities!.Value,    liability),
                ("Pasivo no corriente", (double)b.NonCurrentLiabilities!.Value, liabilityDark),
                ("Patrimonio neto",     (double)b.TotalEquity!.Value,           Equity),
            };

        // Decide what will ACTUALLY be drawn before wording the caption — a bar with any negative segment
        // is skipped (StackedBarRenders is the one predicate both this caption and AddStackedBar consult),
        // so the caption must never announce an exhibit the reader won't find (negative equity is the real
        // case: the financing bar is dropped, only the asset bar draws).
        bool drawAssets = assetSegs is not null && StackedBarRenders(assetSegs);
        bool drawFin    = finSegs   is not null && StackedBarRenders(finSegs);

        string? caption =
            drawAssets && drawFin ? "Composición del activo y de la financiación (pasivo + patrimonio neto)"
          : drawAssets            ? "Composición del activo"
          : drawFin               ? "Composición de la financiación (pasivo + patrimonio neto)"
          :                         null;

        if (caption is null) return;   // neither bar renders → no caption, no empty exhibit

        // A short muted caption so the pair reads as an exhibit, not decoration under the table. Caption,
        // bars and legends are ONE keep-together block: the exhibit is never split across two pages.
        Div exhibit = new Div().SetKeepTogether(true);
        exhibit.Add(new Paragraph(caption)
            .SetFont(fonts.Body).SetFontSize(8).SetFontColor(LabelSecondary).SetMarginBottom(2));

        if (drawAssets) AddStackedBar(doc, fonts, assetSegs!, exhibit);
        if (drawFin)    AddStackedBar(doc, fonts, finSegs!, exhibit);

        doc.Add(exhibit);
    }

    // A stacked bar can honestly render iff every segment is non-negative AND they sum to a positive total.
    // This is the ONE place that rule lives: AddStackedBar consults it to decide whether to draw, and
    // AddPatrimonialComposition consults it to word the caption from the bars that will actually appear — a
    // second copy is how the caption and the drawing would drift apart later. A negative segment (e.g.
    // negative equity) means the whole bar and its legend are not drawn at all; the table above already
    // shows the figure with its sign, and a graphic that cannot show a negative share must not invent a
    // positive one. (Introduced by f82b064; there is deliberately no Math.Abs anywhere in the sizing path.)
    private static bool StackedBarRenders(IReadOnlyList<(string Label, double Value, DeviceRgb Color)> segs)
    {
        double total = 0;
        foreach (var s in segs) { if (s.Value < 0) return false; total += s.Value; }
        return total > 0;
    }

    // One stacked bar plus a legend. Each segment is a proportion of THIS bar's own total; its percentage
    // is drawn inline (mono) when the segment is wide enough (>= 4%), otherwise it is carried only in the
    // legend. Whether the bar renders at all is decided by StackedBarRenders above (negative segment or
    // non-positive total → skip) — the same predicate the caption keys on.
    private static void AddStackedBar(
        ITextDocument doc, Fonts fonts, List<(string Label, double Value, DeviceRgb Color)> segs, Div exhibit)
    {
        if (!StackedBarRenders(segs)) return;

        double total = 0;
        foreach (var s in segs) total += s.Value;   // every value non-negative here — deliberately no Math.Abs

        const float W = 470f, H = 20f;

        Image bar = DrawVector(doc, W, H, canvas =>
        {
            canvas.RoundRectangle(0, 0, W, H, H / 2f).Clip().EndPath();
            float x = 0;
            foreach (var s in segs)
            {
                float segW = (float)(s.Value / total) * W;
                canvas.SetFillColor(s.Color).Rectangle(x, 0, segW, H).Fill();
                double pct = s.Value / total * 100.0;
                if (pct >= 4.0)
                {
                    // One percentage format for BOTH the inline label and the legend (the one-decimal
                    // form, matching the legend below) so a segment can never read "92%" inline and
                    // "91,9%" in its own legend.
                    string t = pct.ToString("0.#", EsAr) + "%";
                    float tw = fonts.Mono.GetWidth(t, 8f);
                    canvas.BeginText().SetFontAndSize(fonts.Mono, 8f).SetFillColor(White)
                          .MoveText(x + (segW - tw) / 2f, H / 2f - 3f).ShowText(t).EndText();
                }
                x += segW;
            }
        });
        exhibit.Add(new Paragraph().Add(bar).SetMarginTop(4).SetMarginBottom(2));

        Paragraph legend = new Paragraph().SetMarginTop(0).SetMarginBottom(8);
        foreach (var s in segs)
        {
            double pct = s.Value / total * 100.0;
            legend.Add(DrawSwatch(doc, s.Color))
                  .Add(new Text($" {s.Label} {pct.ToString("0.#", EsAr)}%    ")
                      .SetFont(fonts.Body).SetFontSize(8).SetFontColor(LabelSecondary));
        }
        exhibit.Add(legend);
    }

    // ── Cash-flow waterfall ────────────────────────────────────────────────────
    // Opening cash as a full column from zero, the three section subtotals as floating columns
    // (positive rising, negative falling), closing cash as a full column from zero. Three-way colour
    // exactly as the Portal: inflow = accent, outflow = terracotta, exactly-zero = neutral — never a
    // two-way positive/negative shortcut. Each column is labelled beneath the axis (Inicio, Operativo,
    // Inversión, Financiación, Cierre) and carries its amount above/below, formatted identically to the
    // bridge table. A thin dotted connector links each running total to the next. When the reconciliation
    // FAILED the last floating column does not land on the closing column; the gap is left visible and
    // unannotated — the gap IS the finding, and the verdict paragraph above already states it in words.
    // The axis is never rebased off zero (a non-zero baseline would exaggerate small moves). Fault-
    // isolated by the caller. Returns the chart, or null when an anchor or subtotal is missing.
    private static Image? CashFlowWaterfall(ITextDocument doc, Fonts fonts, CashFlowData cf)
    {
        if (cf.CashBeginning is not { } open || cf.CashEnd is not { } close
            || cf.Operating is not { } op || cf.Investing is not { } inv || cf.Financing is not { } fin)
            return null;   // draw only when all anchors and subtotals are present; never infer by subtraction

        const float W = 470f, H = 140f;
        float pad = 10f, baseY = 26f, usableH = H - baseY - 12f;   // 12 above for value labels, band below
        int cols = 5;
        float slot = (W - 2 * pad) / cols;
        float colW = slot * 0.6f;

        // Cumulative levels the columns reach.
        double afterOp  = open + op;
        double afterInv = afterOp + inv;
        double afterFin = afterInv + fin;

        double hi = Math.Max(Math.Max(open, close), Math.Max(Math.Max(afterOp, afterInv), Math.Max(afterFin, 0)));
        double lo = Math.Min(Math.Min(open, close), Math.Min(Math.Min(afterOp, afterInv), Math.Min(afterFin, 0)));
        if (hi <= lo) hi = lo + 1;
        double span = hi - lo;
        float Y(double v) => baseY + (float)((v - lo) / span) * usableH;   // zero baseline kept (never rebased)

        DeviceRgb Flow(double delta) => delta > 0 ? Accent : delta < 0 ? CashOutflow : LabelTertiary;

        // Per-column data. The value strings reuse the SAME source fields and helpers as the bridge table
        // above (Pesos for the two anchors, PesosSigned for the three flows), so they agree with it
        // character for character — no second rounding.
        string[]    names     = { "Inicio", "Operativo", "Inversión", "Financiación", "Cierre" };
        string[]    amounts   = { Pesos(cf.CashBeginning), PesosSigned(cf.Operating), PesosSigned(cf.Investing),
                                  PesosSigned(cf.Financing), Pesos(cf.CashEnd) };
        double[]    amountVal = { open, op, inv, fin, close };
        double[]    lvl0      = { 0,    open,    afterOp,  afterInv, 0 };
        double[]    lvl1      = { open, afterOp, afterInv, afterFin, close };
        DeviceRgb[] colColor  = { LabelSecondary, Flow(op), Flow(inv), Flow(fin), LabelSecondary };

        // Two INDEPENDENT fit loops with their own floors, so a long amount never drags the short names
        // down (nor a name the amounts). Names: one size for all five, shrunk (never abbreviated) if the
        // widest overflows its slot.
        float labelSize = 7.5f;
        while (labelSize > 5f)
        {
            bool allFit = true;
            foreach (string n in names)
                if (fonts.Body.GetWidth(n, labelSize) > slot - 4f) { allFit = false; break; }
            if (allFit) break;
            labelSize -= 0.5f;
        }

        // Amounts: same discipline on their own size and floor — a twelve-digit figure at a fixed 7 pt
        // would overlap its neighbour; shrink until the widest fits its slot, never truncating a figure.
        float amountSize = 7f;
        while (amountSize > 5.5f)
        {
            bool allFit = true;
            foreach (string a in amounts)
                if (fonts.Mono.GetWidth(a, amountSize) > slot - 4f) { allFit = false; break; }
            if (allFit) break;
            amountSize -= 0.5f;
        }

        return DrawVector(doc, W, H, canvas =>
        {
            // zero baseline
            canvas.SetStrokeColor(SurfaceBorder).SetLineWidth(0.7f)
                  .MoveTo(pad, Y(0)).LineTo(W - pad, Y(0)).Stroke();

            // Columns. A subtotal that maps to less than ~1.5 pt (a section reporting exactly zero, or a
            // move imperceptible against the range) renders as a labelled TICK at its level — never a
            // hairline lost in noise; the printed amount below/above carries the true figure.
            for (int i = 0; i < cols; i++)
            {
                float x = pad + i * slot + (slot - colW) / 2f;
                float top = Math.Max(Y(lvl0[i]), Y(lvl1[i])), bot = Math.Min(Y(lvl0[i]), Y(lvl1[i]));
                float h = top - bot;
                canvas.SetFillColor(colColor[i]);
                if (h < 1.5f)
                    canvas.Rectangle(x, Y(lvl1[i]) - 0.75f, colW, 1.5f).Fill();
                else
                    canvas.Rectangle(x, bot, colW, h).Fill();
            }

            // Value labels: amount above (>= 0) / below (< 0) each column, centred on its axis.
            for (int i = 0; i < cols; i++)
            {
                float cxi = pad + i * slot + slot / 2f;
                float top = Math.Max(Y(lvl0[i]), Y(lvl1[i])), bot = Math.Min(Y(lvl0[i]), Y(lvl1[i]));
                float tw  = fonts.Mono.GetWidth(amounts[i], amountSize);
                float baseline = amountVal[i] >= 0 ? top + 2.5f : bot - 2.5f - 0.72f * amountSize;
                canvas.BeginText().SetFontAndSize(fonts.Mono, amountSize).SetFillColor(Label)
                      .MoveText(cxi - tw / 2f, baseline).ShowText(amounts[i]).EndText();
            }

            // Column labels beneath the axis, centred in each slot.
            for (int i = 0; i < cols; i++)
            {
                float cxi = pad + i * slot + slot / 2f;
                float tw  = fonts.Body.GetWidth(names[i], labelSize);
                canvas.BeginText().SetFontAndSize(fonts.Body, labelSize).SetFillColor(LabelSecondary)
                      .MoveText(cxi - tw / 2f, 4f).ShowText(names[i]).EndText();
            }

            // Waterfall connectors — a thin dotted line at each running-total level from one column's top
            // to the next column's start. Drawn LAST so the dash pattern touches nothing else. The last
            // connector sits at Y(afterFin); the closing column's top is Y(close) — when reconciliation
            // failed they differ and the connector exposes the gap (left unannotated: the verdict says it).
            canvas.SetStrokeColor(SurfaceBorder).SetLineWidth(0.6f).SetLineDash(1.5f, 2f);
            for (int i = 0; i < cols - 1; i++)
            {
                float rightEdge = pad + i * slot + (slot - colW) / 2f + colW;
                float leftNext  = pad + (i + 1) * slot + (slot - colW) / 2f;
                float yLvl      = Y(lvl1[i]);
                canvas.MoveTo(rightEdge, yLvl).LineTo(leftNext, yLvl).Stroke();
            }
        });
    }

    private static Table BuildTwoColTable(string[] headers, PdfFont headerFont)
    {
        // Two-column tables are short (a handful of rows): kept on one page.
        Table table = new Table(UnitValue.CreatePercentArray([55f, 45f]))
            .UseAllAvailableWidth()
            .SetMarginBottom(8)
            .SetKeepTogether(true);

        // The label column reads left; every caller right-aligns the second column's figures, so its header does too.
        for (int i = 0; i < headers.Length; i++)
        {
            table.AddHeaderCell(AlignHeader(new Cell()
                .SetBackgroundColor(ColorTableHead)
                .SetBorder(Border.NO_BORDER)
                .SetPadding(7)
                .Add(new Paragraph(headers[i])
                    .SetFont(headerFont)
                    .SetFontSize(9)
                    .SetFontColor(ColorTableHeadFg)), i == 0 ? TextAlignment.LEFT : TextAlignment.RIGHT, BodyCellPadding));
        }

        return table;
    }

    // The padding every BodyCell carries (and the cells built alongside it).
    private const float BodyCellPadding = 6f;

    /// <summary>
    /// A header cell aligned like the cells under it: right over right-aligned figures, centred over centred labels.
    /// A right-aligned header takes the body cells' right padding, so its right edge is exactly theirs.
    /// </summary>
    private static Cell AlignHeader(Cell header, TextAlignment align, float bodyPaddingRight)
    {
        header.SetTextAlignment(align);
        if (align == TextAlignment.RIGHT)
            header.SetPaddingRight(bodyPaddingRight);
        return header;
    }

    private static Cell BodyCell(
        string            text,
        PdfFont           font,
        float             fontSize,
        DeviceRgb?        bg        = null,
        TextAlignment     align     = TextAlignment.LEFT)
    {
        Cell cell = new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
            .SetPadding(BodyCellPadding)
            .SetTextAlignment(align)
            .Add(new Paragraph(text)
                .SetFont(font)
                .SetFontSize(fontSize)
                .SetFontColor(ColorBodyText));

        if (bg is not null)
            cell.SetBackgroundColor(bg);

        return cell;
    }

    /// <summary>
    /// A status label (or a muted word standing in for a figure) on the baseline of its row's figures: the label is
    /// smaller or in another face than the <paramref name="figureSize"/> pt <paramref name="figureFont"/> figures,
    /// so under the same top padding it would sit higher. The top padding takes the difference of the two line
    /// ascents (<see cref="PdfBaseline.Shift"/>) and the bottom padding gives it back, so the row keeps its height.
    /// </summary>
    private static Cell StatusCell(
        string        text,
        PdfFont       font,
        float         fontSize,
        DeviceRgb     color,
        DeviceRgb?    bg,
        PdfFont       figureFont,
        float         figureSize,
        TextAlignment align = TextAlignment.CENTER)
    {
        float shift = PdfBaseline.Shift(figureFont, figureSize, font, fontSize);

        Cell cell = new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBorderBottom(new SolidBorder(ColorBorder, 0.5f))
            .SetPadding(BodyCellPadding)
            .SetPaddingTop(BodyCellPadding + shift)
            .SetPaddingBottom(Math.Max(0f, BodyCellPadding - shift))
            .SetTextAlignment(align)
            .Add(new Paragraph(text)
                .SetFont(font)
                .SetFontSize(fontSize)
                .SetFontColor(color));

        if (bg is not null)
            cell.SetBackgroundColor(bg);

        return cell;
    }

    private static Paragraph NotAvailableParagraph(PdfFont regular) =>
        new Paragraph("Datos no disponibles.")
            .SetFont(regular)
            .SetFontSize(9)
            .SetFontColor(ColorMuted)
            .SetMarginBottom(8);

    /// <summary>
    /// Converts a markdown-flavoured narrative string to plain prose
    /// suitable for an iText7 PDF paragraph.
    /// Conservative — not a full parser. Handles the patterns Claude emits.
    /// </summary>
    private static string StripMarkdown(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 1. Strip ATX headers: leading '#'s followed by a space — keep the heading text
        //    e.g. "# Foo" → "Foo",  "## Bar" → "Bar"
        string result = System.Text.RegularExpressions.Regex.Replace(
            raw, @"^#{1,6}\s+", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // 2. Remove bold/italic markers (keep inner text; preserve snake_case names)
        //    Double markers: safe literal replace — ** and __ never appear in ratio names.
        //    Single * and _: regex anchored so word-internal underscores are untouched.
        //      *text*  pattern: opening * NOT preceded by *, closing * NOT followed by *
        //      _text_  pattern: opening _ NOT preceded by word char, closing _ NOT followed by word char
        result = result.Replace("**", string.Empty)
                       .Replace("__", string.Empty);
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"\*(?=\S)(.+?)(?<=\S)\*", "$1");
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"(?<!\w)_(?=\S)(.+?)(?<=\S)_(?!\w)", "$1");

        // 3. Remove inline code backticks
        result = result.Replace("`", string.Empty);

        // 4. Collapse 3+ consecutive newlines → max 2
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"\n{3,}", "\n\n");

        return result.Trim();
    }

    /// <summary>
    /// Drops a FIRST non-empty line that is a Markdown heading ("# …" to "###### …"): the model titles its
    /// narrative ("## Justificación del score", "# Resumen ejecutivo"), and the report already prints the
    /// section's own heading above it. Any later heading is kept (as text) by <see cref="StripMarkdown"/>.
    /// </summary>
    internal static string StripLeadingMarkdownHeading(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string[] lines = raw.Replace("\r\n", "\n").Split('\n');
        int first = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (first < 0 || !Regex.IsMatch(lines[first], @"^\s{0,3}#{1,6}\s+\S")) return raw;

        return string.Join('\n', lines.Skip(first + 1)).TrimStart('\n', '\r', ' ');
    }

    private static string NormalizeForCompare(string s) =>
        new string(
            s.Normalize(System.Text.NormalizationForm.FormD)
             .Where(c => c < 128)
             .ToArray())
        .ToLowerInvariant()
        .Trim();

    // ── Anomaly reconciliation-figure shim ────────────────────────────────────
    // Compatibility shim for cash-flow reconciliation anomalies persisted BEFORE the es-AR source
    // fix in AnalysisController (the SectionsVsNetChange / BeginningPlusChangeVsEnd arms). It is
    // ANCHORED to the exact literal prefixes our own code emits and captures ONLY the two raw
    // invariant decimals (\d+\.\d{2}) that follow them — it never scans the anomaly text for numbers
    // generally, so it cannot touch the analysis id, a percentage, or any other anomaly. Already-es-AR
    // figures (with a decimal comma / grouping dots) do not match and are left untouched. Removable
    // once no pre-fix rows remain. Pairs with the fix at AnalysisController.cs.

    private static readonly Regex[] ReconAnomalyPatterns =
    [
        new(@"^(El estado de flujo de efectivo no concilia: suma de secciones )(-?\d+\.\d{2})( vs variación informada )(-?\d+\.\d{2})(\.)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"^(El estado de flujo de efectivo no concilia: efectivo inicial más variación )(-?\d+\.\d{2})( vs efectivo final informado )(-?\d+\.\d{2})(\.)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

    internal static string ReformatReconciliationAnomaly(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            foreach (Regex rx in ReconAnomalyPatterns)
            {
                if (!rx.IsMatch(text)) continue;

                return rx.Replace(text, static m =>
                    m.Groups[1].Value
                    + ReformatInvariantDecimal(m.Groups[2].Value)
                    + m.Groups[3].Value
                    + ReformatInvariantDecimal(m.Groups[4].Value)
                    + m.Groups[5].Value,
                    1);
            }
        }
        catch { /* formatting must never abort the export */ }

        return text;
    }

    private static string ReformatInvariantDecimal(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? SignedN2(v)
            : raw;

    // ── Executive summary section ─────────────────────────────────────────────

    private void AddExecutiveSummarySection(
        ITextDocument doc, Fonts fonts, AnalysisResult result)
    {
        PdfFont regular = fonts.Body;
        AddSectionHeading(doc, fonts, "Resumen ejecutivo");

        string? executiveSummary = result.ExecutiveSummary;

        // Percentage reconciliation (NOT persisted — recomputed at export time, same fix and same
        // fault-isolation shape as the cash-flow reading above): repairs already-persisted analyses
        // without a backfill, and keeps the PDF consistent with the Portal's own read-time repair
        // in AnalysisController.GetById.
        if (!string.IsNullOrWhiteSpace(executiveSummary))
        {
            try
            {
                // InPrintedUnits: the prose quotes amounts as printed, BalanceJson persists pesos.
                var accounts = string.IsNullOrWhiteSpace(result.BalanceJson)
                    ? null
                    : JsonSerializer.Deserialize<BalanceAccounts>(result.BalanceJson, JsonOpts)?.InPrintedUnits();

                if (accounts is not null)
                {
                    var narrative = _alertRatioReconciliationService.ReconcileNarrative(executiveSummary, accounts);
                    if (narrative.Corrections.Count > 0)
                    {
                        executiveSummary = narrative.Text;
                        foreach (var c in narrative.Corrections)
                            _logger.LogWarning(
                                "PDF export: executive summary percentage reconciled result {ResultId} analysis {AnalysisId} " +
                                "tenant {TenantId}: clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                result.ResultId, result.AnalysisId, result.TenantId,
                                c.Clause, c.StatedPercent, c.ExpectedPercent, c.Denominator);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PDF export: executive summary reconciliation failed for result {ResultId}; rendering original text.",
                    result.ResultId);
            }
        }

        if (string.IsNullOrWhiteSpace(executiveSummary))
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        string text = StripMarkdown(StripLeadingMarkdownHeading(executiveSummary));

        // De-duplicate: drop first line if it mirrors the section heading (same guard as the narrative).
        const string SectionHeading = "Resumen ejecutivo";
        string[] lines = text.Split('\n');
        int firstNonEmpty = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
        if (firstNonEmpty >= 0 &&
            NormalizeForCompare(lines[firstNonEmpty]) == NormalizeForCompare(SectionHeading))
        {
            lines[firstNonEmpty] = string.Empty;
            text = string.Join('\n', lines).TrimStart();
        }

        // K20: the summary was written from the ORIGINAL reading; a manual correction re-scores the analysis and
        // rewrites the justification, never this text. Same condition as the detail's balanceCorrections
        // (AnalysisController.BuildBalanceCorrectionsAsync): ManualCorrectionsReader over the same BalanceJson.
        bool corrected = BalanceCorrections.ManualCorrectionsReader.Read(result.BalanceJson) is not null;

        Paragraph summary = new Paragraph(text)
            .SetFont(regular)
            .SetFontSize(9)
            .SetFontColor(ColorBodyText)
            .SetMarginBottom(corrected ? 4 : 12)
            .SetTextAlignment(TextAlignment.JUSTIFIED);

        if (!corrected)
        {
            doc.Add(summary);
            return;
        }

        // Kept with the summary, so the note never opens a page on its own.
        doc.Add(summary.SetKeepWithNext(true));
        doc.Add(new Paragraph(PdfLabels.ExecutiveSummaryOriginalReadingNote)
            .SetFont(regular)
            .SetFontSize(8.5f)
            .SetFontColor(ColorMuted)
            .SetMarginBottom(12));
    }

    // ── Cash-flow section ─────────────────────────────────────────────────────
    // Rendered from the cash_flow node persisted inside BalanceJson, plus the reading computed at
    // read time by ICashFlowNarrativeService (never persisted — which is why the PDF has never
    // contained it). Wording mirrors the Portal verbatim so the partner sees identical text.
    // Fault-isolated: any failure renders the heading + "Datos no disponibles." and continues.

    private void AddCashFlowSection(
        ITextDocument doc, Fonts fonts, string? balanceJson)
    {
        PdfFont regular = fonts.Body;
        AddSectionHeading(doc, fonts, "Flujo de efectivo");

        CashFlowData? cashFlow = TryExtractCashFlow(balanceJson);
        if (cashFlow is null || !cashFlow.Present)
        {
            doc.Add(NotAvailableParagraph(regular));
            return;
        }

        try
        {
            CashFlowReconciliationResult reconciliation = _cashFlowReconciliationService.Reconcile(cashFlow);

            // The verdict and the bridge TABLE travel as ONE block: the section heading keeps with it, so the
            // heading never ends a page alone and the verdict never sits apart from the table. The bridge chart
            // is its own keep-together block right after: it never splits, and when it does not fit it moves to
            // the next page alone instead of taking the whole opening with it (which left half a page blank
            // under the ratios table).
            Div opening = new Div().SetKeepTogether(true);
            opening.Add(CashFlowVerdict(fonts, reconciliation));
            opening.Add(CashFlowBridge(fonts, cashFlow));
            doc.Add(opening);

            if (CashFlowWaterfall(doc, fonts, cashFlow) is { } chart)
                doc.Add(new Div().SetKeepTogether(true).Add(chart));
            AddCashFlowComposition(doc, fonts, cashFlow);

            // The reading is rendered VERBATIM, one sentence per row, in order — never re-derived,
            // re-ordered, summarised or reworded, and with no lead sentence of our own.
            IReadOnlyList<string> reading = _cashFlowNarrativeService.Read(cashFlow, reconciliation);
            AddCashFlowReading(doc, fonts, reading);

            doc.Add(new Paragraph(
                    "Estas cifras son contextuales: no se utilizaron para calcular el score de riesgo ni se mencionan en su justificación.")
                .SetFont(regular)
                .SetFontSize(8)
                .SetFontColor(ColorMuted)
                .SetMarginTop(8)
                .SetMarginBottom(8));
        }
        catch (Exception ex)
        {
            // A cash-flow failure must never abort the export.
            _logger.LogWarning(ex, "PDF export: cash-flow section failed to render; showing placeholder.");
            doc.Add(NotAvailableParagraph(regular));
        }
    }

    private static CashFlowData? TryExtractCashFlow(string? balanceJson)
    {
        if (string.IsNullOrWhiteSpace(balanceJson)) return null;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(balanceJson);
            if (!parsed.RootElement.TryGetProperty("cash_flow", out JsonElement cf) ||
                cf.ValueKind != JsonValueKind.Object)
                return null;

            return cf.Deserialize<CashFlowData>(JsonOpts);
        }
        catch { return null; }
    }

    private static Div CashFlowVerdict(
        Fonts fonts, CashFlowReconciliationResult recon)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body;
        CashFlowCheckResult sections = recon.SectionsVsNetChange;
        CashFlowCheckResult continuity = recon.BeginningPlusChangeVsEnd;

        bool anyFailed = sections.State == ReconciliationState.Failed
                      || continuity.State == ReconciliationState.Failed;
        bool anyNotEvaluated = sections.State == ReconciliationState.NotEvaluated
                            || continuity.State == ReconciliationState.NotEvaluated;

        // The verdict is ONE block, so the section heading keeps with all of it.
        Div verdict = new Div().SetKeepTogether(true);
        void Line(string text, DeviceRgb color, PdfFont font, float size = 9) =>
            verdict.Add(new Paragraph(text).SetFont(font).SetFontSize(size).SetFontColor(color).SetMarginBottom(3));

        if (anyFailed)
        {
            Line("El flujo de efectivo no concilia", ColorDanger, bold, 10);
            if (sections.State == ReconciliationState.Failed)
                Line($"La suma de las secciones ({ReconAmount(sections.Expected)}) no coincide con la variación neta informada ({ReconAmount(sections.Reported)}).", ColorBodyText, regular);
            if (continuity.State == ReconciliationState.Failed)
                Line($"El efectivo al cierre calculado ({ReconAmount(continuity.Expected)}) no coincide con el efectivo al cierre informado ({ReconAmount(continuity.Reported)}).", ColorBodyText, regular);
            Line("Las cifras se muestran exactamente como fueron extraídas del documento; no se ajustó ningún valor. La discrepancia apunta al balance de origen, no a un error de cálculo.", ColorMuted, regular, 8);
        }
        else if (anyNotEvaluated)
        {
            Line("Conciliación no evaluada", ColorMuted, bold, 10);
            if (sections.State == ReconciliationState.NotEvaluated)
                Line("No se pudo comparar la suma de las secciones con la variación neta: falta al menos una de las tres secciones.", ColorBodyText, regular);
            if (continuity.State == ReconciliationState.NotEvaluated)
                Line("No se pudo comparar el efectivo al inicio más la variación contra el cierre: falta uno de los dos saldos.", ColorBodyText, regular);
            Line("No es lo mismo que un chequeo aprobado: acá no hay comparación posible.", ColorMuted, regular, 8);
        }
        else
        {
            // Both checks passed. The Portal is deliberately silent on pass; the PDF states it
            // explicitly so a partner reconciling the report gets a positive signal.
            Line("El estado de flujo de efectivo concilia internamente.", ColorOk, regular);
        }

        return verdict;
    }

    private static Div CashFlowBridge(Fonts fonts, CashFlowData cf)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono;
        Div bridge = SubSection(fonts, "De dónde salió y a dónde fue");

        Table table = BuildTwoColTable(["Movimiento", "Importe (ARS)"], bold);

        void Row(string label, string value)
        {
            table.AddCell(BodyCell(label, regular, 9));
            table.AddCell(BodyCell(value, mono, 9, align: TextAlignment.RIGHT));
        }

        Row("Efectivo al inicio", Pesos(cf.CashBeginning));
        Row("Operativo",          PesosSigned(cf.Operating));
        Row("Inversión",          PesosSigned(cf.Investing));
        Row("Financiación",       PesosSigned(cf.Financing));
        // The statement's OWN reported net change (not our arithmetic), so the reader can see it
        // against the sum of the three sections above without returning to the verdict paragraph.
        // Omitted — not a placeholder — when the statement reports no net change.
        if (cf.NetChange is not null)
            Row("Variación informada", PesosSigned(cf.NetChange));
        Row("Efectivo al cierre", Pesos(cf.CashEnd));

        bridge.Add(table);

        // The waterfall of the same figures follows as its own block (AddCashFlowSection).
        return bridge;
    }

    private static void AddCashFlowComposition(
        ITextDocument doc, Fonts fonts, CashFlowData cf)
    {
        PdfFont regular = fonts.Body;
        CashFlowComponents? c = cf.Components;

        // Component → Portal es-AR label, split by section exactly as the catalogue maps them
        // (operating maps to none of the eleven keys). Only non-zero (rounds to ≥ 1 peso) lines are shown.
        // The two financial-placement keys are section-neutral: they go where the statement printed them
        // (CashFlowPlacementSection), Investing unless the residuals prove Financing.
        var operating = new List<(string Label, double Value)>();  // no catalogue key maps here

        bool placementsInFinancing = CashFlowPlacementSection.Resolve(cf) == CashFlowPlacementSide.Financing;
        List<(string Label, double Value)> investing = CashFlowCompositionRows.Investing(c, withPlacements: !placementsInFinancing);
        List<(string Label, double Value)> financing = CashFlowCompositionRows.Financing(c, withPlacements: placementsInFinancing);

        bool anything = cf.Operating.HasValue || cf.Investing.HasValue || cf.Financing.HasValue
                        || operating.Count > 0 || investing.Count > 0 || financing.Count > 0;
        if (!anything) return;


        // RECPAM-on-cash (RT6) is a top-level figure attributed to a section by
        // MonetaryResultOnCashSection. When it sits INSIDE a section its amount is already in that
        // section's subtotal, so it is rendered as its own row AND subtracted from that section's
        // residual — otherwise the leftover silently absorbs it and is mislabelled "Resto sin
        // identificar" (asserting the figure is unidentified when we identify it perfectly well).
        // "outside"/null placement is not part of any section subtotal, so it is not attributed to a
        // section here — matching CashFlowReconciliationService.SeparateMonetaryResultTerm.
        double? recpam        = cf.MonetaryResultOnCash;
        string? recpamSection = cf.MonetaryResultOnCashSection;

        // All three sections are rendered, in order — a "Composición de cada sección" block that
        // listed only two of three would read as if the omitted section was not analysed. Each section's
        // label, note and table are ONE keep-together block, so a label never ends a page while its table
        // starts the next; the sub-heading travels inside the first block. (A block taller than a page —
        // more rows than the catalogue has keys — would be moved to a fresh page and split there, its label,
        // note and first rows still together.)
        doc.Add(CompositionSection(fonts, "Operativo", "operating", cf.Operating, operating, recpam, recpamSection,
            lead: SubHeadingParagraph(fonts, "Composición de cada sección")));
        doc.Add(CompositionSection(fonts, "Inversión",    "investing", cf.Investing, investing, recpam, recpamSection));
        doc.Add(CompositionSection(fonts, "Financiación", "financing", cf.Financing, financing, recpam, recpamSection));

        // Without the RECPAM-on-cash line we cannot separate the inflation effect from the real move.
        if (cf.MonetaryResultOnCash is null)
            doc.Add(new Paragraph(
                    "El balance no informa el RECPAM sobre el efectivo. Sin ese dato no se puede separar el efecto de la inflación del movimiento real de caja.")
                .SetFont(regular).SetFontSize(8).SetFontColor(ColorMuted).SetMarginTop(4).SetMarginBottom(4));
    }

    private static Div CompositionSection(
        Fonts fonts,
        string sectionLabel, string sectionKey, double? subtotal,
        List<(string Label, double Value)> components, double? recpam, string? recpamSection,
        Paragraph? lead = null)
    {
        PdfFont bold = fonts.HeadingBold, regular = fonts.Body, mono = fonts.Mono, med = fonts.BodyMedium;
        bool recpamHere = recpam.HasValue && string.Equals(recpamSection, sectionKey, StringComparison.Ordinal);
        bool missing    = subtotal is null;
        Div block       = new Div().SetKeepTogether(true);
        if (lead is not null)
            block.Add(lead);

        // Section header: label + subtotal, or "sin cifra" when the statement reports no subtotal.
        block.Add(new Paragraph($"{sectionLabel}: {(missing ? "sin cifra" : PesosSigned(subtotal))}")
            .SetFont(med).SetFontSize(9).SetFontColor(ColorBodyText).SetMarginTop(6).SetMarginBottom(2));

        // Identified rows: RECPAM-on-cash first (when attributed here), then the catalogue components.
        var rows = new List<(string Label, double Value)>();
        if (recpamHere)
            rows.Add(("RECPAM sobre el efectivo (Ajuste por inflación)", recpam!.Value));
        rows.AddRange(components);

        if (rows.Count == 0)
        {
            // Subtotal present but nothing identified — state exactly that; never leave the section
            // silent (an omitted section reads as "not analysed"). Missing + nothing → just "sin cifra".
            if (!missing)
                block.Add(CompositionNote(regular,
                    "El balance informa el subtotal de la sección; esta lectura no identifica ninguno de sus conceptos."));
            return block;
        }

        if (missing)
            block.Add(CompositionNote(regular,
                "El balance no informa el subtotal de esta sección; lo que sigue son los movimientos que sí quedaron identificados."));

        Table table = BuildTwoColTable(["Concepto", "Importe (ARS)"], bold);
        foreach ((string label, double value) in rows)
        {
            table.AddCell(BodyCell(label, regular, 9));
            table.AddCell(BodyCell(PesosSigned(value), mono, 9, align: TextAlignment.RIGHT));
        }

        // Residual = subtotal − Σ identified (components + RECPAM when attributed here). Only when the
        // subtotal is reported; shown only when it survives rounding. It is what no identified line
        // accounts for — never a claim the statement failed to detail it.
        if (CashFlowCompositionRows.Residual(subtotal, components, recpam, recpamSection, sectionKey) is { } residual
            && CashFlowCompositionRows.Survives(residual))
        {
            table.AddCell(BodyCell("Resto sin identificar", regular, 9));
            table.AddCell(BodyCell(PesosSigned(residual), mono, 9, align: TextAlignment.RIGHT));
        }

        block.Add(table);
        return block;
    }

    private static Paragraph CompositionNote(PdfFont regular, string text) =>
        new Paragraph(text).SetFont(regular).SetFontSize(8).SetFontColor(ColorMuted).SetMarginBottom(4);

    private static void AddCashFlowReading(
        ITextDocument doc, Fonts fonts, IReadOnlyList<string> reading)
    {
        PdfFont regular = fonts.Body;
        if (reading is null || reading.Count == 0) return;

        AddSubHeading(doc, fonts, "Lectura del flujo");

        foreach (string sentence in reading)
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            // Verbatim, but for the sign glyph: the reading writes the screen's "-$", the report prints "−$".
            doc.Add(new Paragraph($"• {PdfText.TypographicMinus(sentence)}")
                .SetFont(regular)
                .SetFontSize(9)
                .SetFontColor(ColorBodyText)
                .SetMarginBottom(3)
                .SetTextAlignment(TextAlignment.JUSTIFIED));
        }
    }

    // es-AR pesos, whole pesos, "$ 1.234.567" / "−$ 1.234.567" — the Portal's formatPesos, with the typographic minus.
    private static string Pesos(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "—";

        double rounded = Math.Round(value.Value, MidpointRounding.AwayFromZero);
        string body = Math.Abs(rounded).ToString("N0", EsAr);
        return rounded < 0 ? $"\u2212$ {body}" : $"$ {body}";   // typographic minus for a negative figure
    }

    // As Pesos, but prepends "+" for strictly positive values (matches the Portal's formatPesosSigned).
    private static string PesosSigned(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "—";

        double rounded = Math.Round(value.Value, MidpointRounding.AwayFromZero);
        return rounded > 0 ? "+" + Pesos(value) : Pesos(value);
    }

    // Reconciliation figures render with two decimals and es-AR grouping — byte-identical to the
    // anomaly shim's "N2" rendering (ReformatInvariantDecimal). A reconciliation discrepancy is the
    // one place where the decimals carry meaning, and the same figure must read identically in the
    // verdict and in the anomaly bullet. Deliberately separate from Pesos()/PesosSigned(), which stay
    // whole-peso for every other amount; no currency symbol, matching the anomaly text.
    private static string ReconAmount(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "—";

        return SignedN2(value.Value);
    }

    // Two es-AR decimals with the typographic minus for a negative figure — the reconciliation figures' one form.
    private static string SignedN2(double value) =>
        value < 0 ? PdfText.Minus + Math.Abs(value).ToString("N2", EsAr) : value.ToString("N2", EsAr);

}
