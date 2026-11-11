// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-13

using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Render smoke tests for the drawn graphics added by the brand/charts PR (score gauge, drawn bars,
/// patrimonial stacked bars, cash-flow waterfall). A drawing change is exactly what can break a
/// flowing-layout export, so these guard the boundary conditions the graphics must survive:
///   · a null score renders NO gauge and the section still produces a valid PDF;
///   · partially-null balance figures render a partial (or no) composition graphic without aborting;
///   · a failed cash-flow reconciliation still produces a valid PDF (the waterfall shows the gap).
/// These assert on the produced PDF, never on any figure or wording, which this PR leaves unchanged.
/// </summary>
public sealed class PdfExportChartSmokeTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF");

    private static PdfExportService NewService() =>
        new(new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

    private static Analysis SampleAnalysis() => new() { FiscalYear = 2025 };

    private const string RatiosJson =
        """{"current_ratio":1.67,"acid_test":0.78,"debt_ratio":0.6,"leverage":1.5,"roe":20.0,"roa":8.0,"net_margin":10.0,"interest_coverage":null}""";

    private const string ScoreBreakdownJson =
        """
        {
          "breakdown": {
            "liquidez_corriente": {"score": 10, "status": "Saludable"},
            "endeudamiento":      {"score": 30, "status": "Atencion"}
          },
          "scoreRatios": 21, "scoreBcra": 45, "scoreMacro": 40,
          "bcraExcluded": false,
          "effectiveWeightRatios": 0.50, "effectiveWeightBcra": 0.35, "effectiveWeightMacro": 0.15,
          "sectorProfile": "General"
        }
        """;

    // Full balance including a reconciling cash-flow node; cashEnd is the only knob the tests turn.
    private static string BalanceJson(double cashEnd) =>
        $$"""
        {
          "assets": 500000000, "liabilities": 300000000, "equity": 200000000,
          "current_assets": 250000000, "non_current_assets": 250000000,
          "current_liabilities": 150000000, "non_current_liabilities": 150000000,
          "net_revenue": 400000000, "net_profit": 40000000,
          "cash_flow": {
            "present": true, "method": "indirect",
            "cash_beginning": 200000000, "cash_end": {{cashEnd.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "net_change": 25000000,
            "operating": 100000000, "investing": -45000000, "financing": -30000000,
            "components": { "capex": -40000000 }
          }
        }
        """;

    // Cash-flow with five DISTINCT column magnitudes (and a reconciling total) so each waterfall amount
    // can be counted unambiguously against its bridge-table twin.
    private const string WaterfallBalanceJson =
        """
        {
          "cash_flow": {
            "present": true, "method": "indirect",
            "cash_beginning": 111111111, "cash_end": 255555556, "net_change": 144444445,
            "operating": 222222222, "investing": -33333333, "financing": -44444444,
            "components": { "capex": -12121212 }
          }
        }
        """;

    private static AnalysisResult ResultWith(string? balanceJson, decimal? score, string? level) => new()
    {
        ResultId           = new Guid("22222222-2222-2222-2222-222222222222"),
        BalanceJson        = balanceJson,
        RatiosJson         = RatiosJson,
        ScoreBreakdownJson = ScoreBreakdownJson,
        AiSuggestedScore   = score,
        AiSuggestedLevel   = level,
        ScoringNarrative   = "El score refleja ratios sólidos.",
    };

    private static async Task<byte[]> RenderAsync(AnalysisResult result, params string[] sections) =>
        await NewService().GenerateAsync(result, SampleAnalysis(), "tenant-x",
            sections.Length == 0 ? Array.Empty<string>() : sections, CancellationToken.None);

    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1000, $"PDF suspiciously small: {bytes.Length} bytes");
        Assert.Equal(PdfMagic, bytes.AsSpan(0, 4).ToArray());
    }

    private static string RenderText(byte[] pdf)
    {
        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.Append(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    [Fact]
    public async Task Risk_ScorePresent_RendersGauge_WithLevelBesideIt()
    {
        byte[] pdf = await RenderAsync(ResultWith(BalanceJson(225000000), 30m, "High"), "risk");

        AssertIsPdf(pdf);
        // The first page's gauge carries "/1000" and, beneath it, the level in the Portal's words; present
        // only when a score exists.
        string text = RenderText(pdf).Replace(" ", "");
        Assert.Contains("/1000", text);
        Assert.Contains("RiesgoAlto", text);
        Assert.DoesNotContain("Nivel:", text);
    }

    [Fact]
    public async Task Risk_ScoreNull_RendersNoGauge_AndStillProducesPdf()
    {
        // No score → no gauge is drawn at all (never an empty/zero gauge). "/1000" lives only on the
        // gauge, so its absence proves the gauge was skipped; the section still renders.
        byte[] pdf = await RenderAsync(ResultWith(BalanceJson(225000000), score: null, level: null), "risk");

        AssertIsPdf(pdf);
        Assert.DoesNotContain("/1000", RenderText(pdf).Replace(" ", ""));
    }

    [Fact]
    public async Task Balance_AssetFiguresComplete_LiabilityFigurePartiallyNull_StillProducesPdf()
    {
        // non_current_liabilities is absent → the liabilities/equity bar cannot be drawn (a figure is
        // never inferred by subtraction), but the asset bar's figures are complete, so a partial
        // composition graphic renders. Either way the export must not abort.
        const string balance = """
            {
              "current_assets": 250000000, "non_current_assets": 250000000,
              "current_liabilities": 150000000, "non_current_liabilities": null,
              "equity": 200000000, "total_equity": 200000000
            }
            """;
        byte[] pdf = await RenderAsync(ResultWith(balance, 30m, "High"), "balance");
        AssertIsPdf(pdf);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public async Task Balance_NegativeEquity_OmitsLiabilitiesEquityBar_ButDrawsAssetBar()
    {
        // Negative equity has no honest share of a stacked bar, so the liabilities-and-equity bar
        // (and its legend) is not drawn at all; the asset bar, whose figures are fine, still draws.
        // The balance table above still shows every figure with its sign — unchanged by this PR.
        const string balance = """
            {
              "current_assets": 250000000, "non_current_assets": 250000000,
              "current_liabilities": 150000000, "non_current_liabilities": 400000000,
              "equity": -50000000
            }
            """;
        byte[] pdf = await RenderAsync(ResultWith(balance, 30m, "High"), "balance");
        AssertIsPdf(pdf);

        string text = RenderText(pdf).Replace(" ", "");
        // "Pasivo corriente" appears once — the balance table row. The omitted bar's legend would have
        // added a second occurrence, so exactly one proves the liabilities-and-equity bar is absent.
        Assert.Equal(1, Count(text, "Pasivocorriente"));
        // "Activo corriente" appears in the table AND in the asset bar's legend → the asset bar drew.
        Assert.True(Count(text, "Activocorriente") >= 2,
            "expected the asset bar's legend to render (>= 2 occurrences of 'Activo corriente')");
    }

    [Fact]
    public async Task Balance_NegativeEquity_CaptionNamesOnlyTheAssetComposition()
    {
        // Negative equity drops the financing bar (StackedBarRenders is false for a negative segment), so
        // only the asset bar renders. The caption must then name ONLY the asset composition — never promise
        // a financing/equity exhibit the reader won't find.
        const string balance = """
            {
              "current_assets": 250000000, "non_current_assets": 250000000,
              "current_liabilities": 150000000, "non_current_liabilities": 400000000,
              "equity": -50000000
            }
            """;
        byte[] pdf = await RenderAsync(ResultWith(balance, 30m, "High"), "balance");
        AssertIsPdf(pdf);

        string text = RenderText(pdf).Replace(" ", "");
        // The asset bar still renders (its legend appears in addition to the table row)...
        Assert.True(Count(text, "Activocorriente") >= 2, "asset bar legend should render");
        // ...a composition caption is present ("Composici" is accent-free and caption-only)...
        Assert.Contains("Composici", text);
        // ...and it names only the asset side: "financiación" appears solely in the other two caption
        // variants, so its absence proves the caption mentions no financing / equity / pasivo.
        Assert.DoesNotContain("financiaci", text);
    }

    [Fact]
    public async Task Balance_AllCompositionFiguresNull_OmitsGraphic_AndStillProducesPdf()
    {
        // Neither bar has complete figures → no composition graphic at all; the table is left alone.
        const string balance = """
            {
              "current_assets": null, "non_current_assets": null,
              "current_liabilities": null, "non_current_liabilities": null, "total_equity": null,
              "net_revenue": 400000000
            }
            """;
        byte[] pdf = await RenderAsync(ResultWith(balance, 30m, "High"), "balance");
        AssertIsPdf(pdf);
    }

    [Fact]
    public async Task CashFlow_ReconciliationFails_WaterfallStillProducesPdf()
    {
        // cash_end = 999000000 breaks the beginning+change vs end continuity check. The waterfall must
        // still draw (the closing column not landing on the running total is the visible finding) and
        // the export must not abort.
        byte[] pdf = await RenderAsync(ResultWith(BalanceJson(999000000), 30m, "High"), "cash_flow", "footer");
        AssertIsPdf(pdf);
        Assert.Contains("no concilia", RenderText(pdf));
    }

    [Fact]
    public async Task Reorder_RendersExactlyTheRequestedSections_RegardlessOfRequestOrder()
    {
        // The dispatch order changed to reading order, but WHICH sections render still depends only on
        // the requested key set, never the order. Request a subset in an order different from the
        // dispatch and assert exactly those sections appear (and the unrequested ones do not).
        byte[] pdf = await RenderAsync(ResultWith(BalanceJson(225000000), 30m, "High"),
            "cash_flow", "risk", "ratios");   // deliberately not in dispatch order
        AssertIsPdf(pdf);

        string text = RenderText(pdf).Replace(" ", "");
        // requested → present (accent-free markers)
        Assert.Contains("Análisisderiesgo", text);    // risk
        Assert.Contains("IndicadorValorEstado", text); // ratios: the value columns alone
        Assert.Contains("Flujodeefectivo", text);     // cash_flow
        // not requested → absent
        Assert.DoesNotContain("Estadopatrimonial", text);               // balance
        Assert.DoesNotContain("IndicadorValorPuntaje", text);           // ratio_breakdown's column
    }

    [Fact]
    public async Task Waterfall_ColumnAmounts_MatchTheBridgeTableStrings()
    {
        // Each waterfall column prints its amount with the SAME source field and helper the bridge table
        // uses (Pesos for the anchors, PesosSigned for the flows), so every amount appears at least twice
        // — once in the bridge row, once as the chart label. Distinct magnitudes rule out collisions.
        byte[] pdf = await RenderAsync(ResultWith(WaterfallBalanceJson, 30m, "High"), "cash_flow");
        AssertIsPdf(pdf);

        string t = RenderText(pdf).Replace(" ", "");
        Assert.True(Count(t, "$111.111.111")  >= 2, "Inicio amount missing from bridge or chart");
        Assert.True(Count(t, "+$222.222.222") >= 2, "Operativo amount missing from bridge or chart");
        // Negative figures carry the typographic minus (U+2212).
        Assert.True(Count(t, "\u2212$33.333.333")  >= 2, "Inversión amount missing from bridge or chart");
        Assert.True(Count(t, "\u2212$44.444.444")  >= 2, "Financiación amount missing from bridge or chart");
        Assert.True(Count(t, "$255.555.556")  >= 2, "Cierre amount missing from bridge or chart");
    }

    [Fact]
    public async Task EveryPage_CarriesAPageNumber_WithResolvedTotal()
    {
        // The running footer writes "Página X de Y" on every page; the total Y comes from a reserved
        // placeholder filled once at close. Render all sections (multi-page) and assert every page shows
        // the page-number footer, and that page 1 shows the resolved total "de {pages}".
        byte[] pdf = await RenderAsync(ResultWith(BalanceJson(225000000), 30m, "High"));  // empty = all
        AssertIsPdf(pdf);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        int pages = doc.GetNumberOfPages();
        Assert.True(pages >= 1);

        for (int i = 1; i <= pages; i++)
        {
            string pageText = PdfTextExtractor.GetTextFromPage(doc.GetPage(i)).Replace(" ", "");
            // "gina" is the accent-free tail of "Página" — present only in the running page-number footer.
            Assert.True(pageText.Contains("gina"), $"page {i} is missing its running page-number footer");
        }

        // The reserved placeholder resolved to the real total on page 1: "Página 1 de {pages}".
        string page1 = PdfTextExtractor.GetTextFromPage(doc.GetPage(1)).Replace(" ", "");
        Assert.Contains($"gina1de{pages}", page1);
    }
}
