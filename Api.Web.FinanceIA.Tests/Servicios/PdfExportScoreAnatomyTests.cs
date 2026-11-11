// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The exported PDF prints the score anatomy on the 1000 scale with exactly the figures the Portal's
/// "Anatomía del score" shows (PortalesHWA/Portal-FinanceIA#327), read back with PdfPig so the
/// typographic minus is proven to survive the embedded font and text extraction. Since #429 the composite is
/// the ENGINE's (toNosis(scoreBeforeFloor)), every adjustment prints its own engine delta, and each rounding
/// is a line of its own — never folded into an adjustment.
/// </summary>
public sealed class PdfExportScoreAnatomyTests
{
    // The analysis the Portal fix was reported on: components 22 / 0 / 30 at 0.50 / 0.35 / 0.15 →
    // 390 + 350 + 105 = 845, engine composite 15.5 → 840 (components rounding −5), LUFE ventasAdj 1.6 → −16,
    // final 17.1 → 830 (rounding +6 on its own line).
    private const string RutaSurBreakdown = """
        {
          "scoreRatios": 22.00, "scoreBcra": 0.00, "scoreMacro": 30.00,
          "scoreBeforeFloor": 15.50, "appliedFloor": 0.00, "criticalRatios": [],
          "bcraTrendDelta": 0, "bcraTrendAdj": 0.00, "sectorProfile": "General",
          "ventasAdj": 1.60, "ventasVariacion": -0.1400, "ventasBase": "interanual_3m",
          "ventasVariacionEsReal": true,
          "bcraExcluded": false,
          "effectiveWeightRatios": 0.5000, "effectiveWeightBcra": 0.3500, "effectiveWeightMacro": 0.1500
        }
        """;

    private const string BcraDataJson = """
        {"deuda":{"identificacion":30712345678,"situacionMaxima":3,"montoTotal":1500.0,
          "periodoConsultado":"202601","estado":0,"fetchedAt":"2026-02-01T00:00:00Z"}}
        """;

    private static async Task<string> RenderText(
        string scoreBreakdownJson, decimal aiSuggestedScore, string[] sections, string? bcraDataJson = null)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("66666666-6666-6666-6666-666666666666"),
            AiSuggestedScore   = aiSuggestedScore,
            AiSuggestedLevel   = "Low",
            BcraDataJson       = bcraDataJson,
            ScoreBreakdownJson = scoreBreakdownJson,
        };

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x", sections, CancellationToken.None);

        using PigDocument doc = PigDocument.Open(pdf);
        return string.Join(
            "\n",
            doc.GetPages().Select(page => string.Join(" ", page.GetWords().Select(word => word.Text))));
    }

    [Fact]
    public async Task RutaSur_PrintsTheSameAnatomyAsThePortal()
    {
        string text = await RenderText(RutaSurBreakdown, 17.1m, ["risk"]);

        Assert.Contains("Ratios 390 + BCRA 350 + Macro 105 + redondeo −5 → Score compuesto 840", text);
        Assert.Contains("−16 pts", text);                                         // the engine's own delta
        Assert.Contains("Redondeo a la escala del score — +6 pts", text);       // never folded into LUFE
        Assert.Contains("Score final 830", text);

        // The internal-scale figure and the old disclaimer are gone: the lines now add up.
        Assert.DoesNotContain("+1,6", text);
        Assert.DoesNotContain("penalidad", text);
        Assert.DoesNotContain("ponderar", text);
        Assert.DoesNotContain("−10 pts", text);
    }

    [Fact]
    public async Task NoAdjustment_WithAResidual_PrintsTheComponentsRounding()
    {
        // 22/0/30 without LUFE: 845 of components against the engine's toNosis(15.5) = 840 — the −5 is the
        // components' rounding, stated beside them; composite and final then agree.
        string breakdown = RutaSurBreakdown.Replace("\"ventasAdj\": 1.60", "\"ventasAdj\": 0.00");

        string text = await RenderText(breakdown, 15.5m, ["risk"]);

        Assert.Contains("+ redondeo −5 → Score compuesto 840", text);
        Assert.DoesNotContain("Redondeo a la escala del score", text);
        Assert.Contains("Score final 840", text);
    }

    [Fact]
    public async Task BoundFloor_CalloutUsesTheAnatomyFigures()
    {
        // 60/40/30 → 200 + 210 + 105 = 515 displayed (internal 48.5); a critical ratio floors it to 65 → 350.
        const string breakdown = """
            {
              "scoreRatios": 60.00, "scoreBcra": 40.00, "scoreMacro": 30.00,
              "scoreBeforeFloor": 48.50, "appliedFloor": 65.00, "criticalRatios": ["endeudamiento"],
              "bcraTrendDelta": 0, "bcraTrendAdj": 0.00, "sectorProfile": "General"
            }
            """;

        string text = await RenderText(breakdown, 65m, ["risk"]);

        Assert.Contains("Score compuesto 510", text);
        Assert.Contains("se redujo a 350", text);
        Assert.Contains("−160 pts", text);
        Assert.Contains("Score final 350", text);
    }

    [Fact]
    public async Task UnboundFloor_ClaimsNoReduction_AndNothingMoves()
    {
        // A critical ratio reports a floor of 40 the 48.5 composite had already cleared: the engine's
        // composite (510) IS the final; the components' −5 is stated beside them, as rounding.
        const string breakdown = """
            {
              "scoreRatios": 60.00, "scoreBcra": 40.00, "scoreMacro": 30.00,
              "scoreBeforeFloor": 48.50, "appliedFloor": 40.00, "criticalRatios": ["endeudamiento"],
              "bcraTrendDelta": 0, "bcraTrendAdj": 0.00, "sectorProfile": "General"
            }
            """;

        string text = await RenderText(breakdown, 48.5m, ["risk"]);

        Assert.DoesNotContain("se redujo", text);
        Assert.Contains("+ redondeo −5 → Score compuesto 510", text);
        Assert.DoesNotContain("Redondeo a la escala del score", text);
        Assert.Contains("Score final 510", text);
    }

    // The BCRA trend line is on the 1000 display scale with the display sign, like the component bar beside it:
    // internal bcraTrendAdj a → display −10·a (JavaScript rounding, as ScoreAnatomy.ToNosisDelta).
    [Theory]
    [InlineData(1,  "8.00",  "Situación BCRA empeorando (1 nivel) — −80 pts en el componente BCRA")]
    [InlineData(2,  "3",     "Situación BCRA empeorando (2 niveles) — −30 pts en el componente BCRA")]
    [InlineData(-1, "-3.00", "Situación BCRA mejorando (1 nivel) — +30 pts en el componente BCRA")]
    [InlineData(-2, "-1.25", "Situación BCRA mejorando (2 niveles) — +12 pts en el componente BCRA")]   // 12.5 rounds to 12, as JS does for −12.5
    [InlineData(1,  "0.00",  "Situación BCRA empeorando (1 nivel)")]                                      // clamped: moved nothing
    public async Task BcraTrend_IsStatedOnTheDisplayScale(int delta, string adj, string expected)
    {
        string breakdown = RutaSurBreakdown.Replace(
            "\"bcraTrendDelta\": 0, \"bcraTrendAdj\": 0.00", $"\"bcraTrendDelta\": {delta}, \"bcraTrendAdj\": {adj}");

        string text = await RenderText(breakdown, 17.1m, ["bcra"], BcraDataJson);

        Assert.Contains(expected, text);
        Assert.DoesNotContain("al score BCRA", text);
        Assert.DoesNotContain("nivel(es)", text);
        if (adj is "0.00")
            Assert.DoesNotContain("pts en el componente BCRA", text);
    }

    [Fact]
    public async Task BcraTrend_WithoutAnAdjustment_StatesTheMovementAlone()
    {
        string breakdown = RutaSurBreakdown.Replace(
            "\"bcraTrendDelta\": 0, \"bcraTrendAdj\": 0.00", "\"bcraTrendDelta\": -1");

        string text = await RenderText(breakdown, 17.1m, ["bcra"], BcraDataJson);

        Assert.Contains("Situación BCRA mejorando (1 nivel)", text);
        Assert.DoesNotContain("pts en el componente BCRA", text);
    }

    /// <summary>
    /// Score credibility 1b-2a: a breakdown carrying the new <c>reliabilityFacts</c> block renders the
    /// same risk SECTION as one without it — the section reads the keys it knows and ignores the rest.
    /// The "Confiabilidad del score" box (now at the top of the risk section, and the first page's reliability
    /// line) DOES read the facts — that is its job — so the comparison starts after the box.
    /// </summary>
    [Fact]
    public async Task ABreakdownWithReliabilityFacts_RendersExactlyAsWithout()
    {
        const string facts = """
            "reliabilityFacts":{"version":1,"bcraSituacionUnreadable":false,"macroImputed":true,"sectorFallback":false,"configSource":"defaults","configHash":"0f1e2d3c4b5a6978","ratiosSource":"computed","fiscalYear":2024,"bcra":{"deudaEstado":"DebtFound","periodoConsultado":"202609","chequesEvaluated":true},"lufe":{"ventasEstado":"carried_forward","ventasUltimoPeriodo":"202608"}}
            """;
        string withFacts = RutaSurBreakdown.TrimEnd()[..^1] + "," + facts + "}";

        string without = await RenderText(RutaSurBreakdown, 17.1m, ["risk"]);
        string with    = await RenderText(withFacts, 17.1m, ["risk"]);

        Assert.Equal(RiskSection(without), RiskSection(with));
        Assert.NotEqual(without, with);   // the reliability box above it is what changed

        static string RiskSection(string text)
        {
            int start = text.IndexOf("Perfil de scoring", StringComparison.Ordinal);
            Assert.True(start >= 0, "the risk section body must render");
            return text[start..];
        }
    }
}
