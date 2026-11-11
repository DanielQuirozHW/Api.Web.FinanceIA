// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using System.Globalization;
using System.Text;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the two places that explain the CASFOG guarantee-arrears penalty to a reader (§7.4): the chat
/// system prompt and the PDF export. Both are fed from the persisted <c>ScoreBreakdownJson</c>, never
/// from a live <see cref="ScoringResult"/>.
/// <para>
/// WHY THIS MATTERS BEYOND COSMETICS: like the sales-drop penalty, this one acts on the FINAL score
/// rather than inside a component, so the component figures both surfaces already print will NOT add
/// up to the final score without it. Omitting the line leaves the model and the report explaining an
/// arithmetic they cannot close.
/// </para>
/// <para>
/// The silence cases are equally load-bearing. <c>casfogMoraAdj</c> is the REALISED movement, so a
/// zero means the score does not carry the penalty and nothing may be announced; and an analysis
/// scored before this signal existed carries no such key at all, which must render exactly as it did
/// then.
/// </para>
/// <para>
/// Note the deliberate culture split, which mirrors the §7.1 and §7.3 lines: the chat prompt is built
/// with InvariantCulture so it does not change shape with the server's culture, while the PDF is
/// es-AR throughout. The assertions below check for dot decimals in one and comma decimals in the
/// other.
/// </para>
/// </summary>
public sealed class CasfogMoraAdjReportingTests
{
    private const string CasfogHeading = "Garantías SGR";

    // The same row, matched without accented characters. The chat prompt is a plain string and can be
    // asserted on the heading itself; PDF text comes back through iText's extractor, and every PDF
    // assertion in this project deliberately pins an ASCII substring rather than betting on how a
    // glyph round-trips.
    private const string CasfogPdfMarker = "SGR (CASFOG)";

    // A breakdown blob in the shape the controller persists, with the CASFOG keys optionally present.
    // Written with invariant literals so a comma-decimal test host cannot produce a blob that fails to
    // parse and turn these into false passes.
    private static string Blob(string? casfogKeys) => $$"""
        {
          "scoreRatios": 21.00, "scoreBcra": 0.00, "scoreMacro": 30.00,
          "scoreBeforeFloor": 15.00, "appliedFloor": 0.00, "criticalRatios": [],
          "bcraTrendDelta": 0, "bcraTrendAdj": 0.00,
          "sectorProfile": "General"{{casfogKeys}}
        }
        """;

    /// <summary>
    /// The §7.4 key shape. <paramref name="proporcion"/> may be null, which is the engine saying the
    /// share could not be formed at all — distinct from a real 0.
    /// </summary>
    private static string WithCasfog(decimal casfogMoraAdj, decimal? proporcion)
    {
        string p = proporcion is { } x
            ? $", \"casfogProporcionMora\": {x.ToString(CultureInfo.InvariantCulture)}"
            : ", \"casfogProporcionMora\": null";
        return $", \"casfogMoraAdj\": {casfogMoraAdj.ToString(CultureInfo.InvariantCulture)}{p}";
    }

    // ── 1. The chat system prompt ───────────────────────────────────────────────────────────────

    [Fact]
    public void ChatBreakdownBlock_WhenThePenaltyWasApplied_ExplainsTheShareAndThePoints()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithCasfog(9.60m, 0.3300m)), finalScore: 24.60m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains(CasfogHeading, block);
        Assert.Contains("CASFOG", block);
        Assert.Contains("33.0 %", block);            // invariant: dot decimals
        Assert.Contains("+9.6 puntos", block);

        // Says which score moved. This penalty does not touch a component, so a reader told only
        // "penalidad aplicada" would reasonably assume it came out of the BCRA term.
        Assert.Contains("score final", block);

        // Says HOW the share was measured. A reader who assumed it was a count of guarantees would
        // misread the number, and the choice of basis is the substantive design decision here.
        Assert.Contains("por saldo", block);
    }

    // A share that could not be formed while points were still applied cannot arise from the engine,
    // but the renderer must not print "null %" if a hand-edited or future blob ever carries it.
    [Fact]
    public void ChatBreakdownBlock_WhenTheShareIsMissing_StillNamesThePenalty()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithCasfog(4.80m, null)), finalScore: 19.80m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains(CasfogHeading, block);
        Assert.Contains("+4.8 puntos", block);
        Assert.DoesNotContain("%", block.Split('\n').First(l => l.Contains(CasfogHeading)));
    }

    // The realised movement is zero: the score does not carry the penalty, so nothing may be
    // announced. A share of 0 is a real reading — the portfolio is clean — and reporting it as a
    // penalty would be a false statement about the score.
    [Fact]
    public void ChatBreakdownBlock_WhenNothingIsInArrears_SaysNothing()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithCasfog(0m, 0m)), finalScore: 15.00m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain(CasfogHeading, block);
        Assert.DoesNotContain("CASFOG", block);
    }

    // A pre-v8 blob carries no CASFOG key at all and must render exactly as it did before §7.4 —
    // the whole reason old analyses need no backfill.
    [Fact]
    public void ChatBreakdownBlock_ForAnAnalysisScoredBeforeThisSignal_RendersUnchanged()
    {
        string antes = AnalysisController.FormatScoreBreakdownBlock(Blob(null), finalScore: 15.00m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain(CasfogHeading, antes);
        Assert.DoesNotContain("CASFOG", antes);

        // Still a usable block — the absence is of one line, not of the whole thing.
        Assert.Contains("General", antes);
    }

    // ── 2. The PDF export ───────────────────────────────────────────────────────────────────────
    //
    // The CASFOG row lives in the general "Análisis de Riesgo" section beside the critical-floor
    // callout and the ventas row — NOT in "Datos BCRA" where the cheques row is — because it is not a
    // BCRA figure and it moves the final score rather than a component.

    private static async Task<string> RenderRiskSectionText(string scoreBreakdownJson)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("66666666-6666-6666-6666-666666666666"),
            AiSuggestedScore   = 24.60m,
            AiSuggestedLevel   = "Low",
            ScoreBreakdownJson = scoreBreakdownJson,
        };

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            new[] { "risk" }, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    [Fact]
    public async Task PdfRiskSection_WhenThePenaltyWasApplied_RendersTheCasfogRow()
    {
        string text = await RenderRiskSectionText(Blob(WithCasfog(9.60m, 0.3300m)));

        Assert.Contains(CasfogPdfMarker, text);

        // es-AR throughout this report: comma decimal separator, as the callout beside it uses.
        Assert.Contains("33,0", text);

        // On the 1000 scale: 395 + 350 + 105 = 850, 9.6 internal is the engine's −96, and toNosis(24.6) = 750
        // leaves −4 of rounding — stated on its own line, never folded into the CASFOG row (Portal #429).
        Assert.Contains("\u221296 pts", text);
        Assert.Contains("Redondeo a la escala del score \u2014 \u22124 pts", text);
        Assert.Contains("Score compuesto 850", text);
        Assert.Contains("Score final 750", text);
        Assert.DoesNotContain("+9,6", text);

        // Names the basis, so the figure cannot be read as a count of guarantees.
        Assert.Contains("saldo vigente", text);
    }

    [Fact]
    public async Task PdfRiskSection_WhenNothingIsInArrears_RendersNoCasfogRow()
    {
        string text = await RenderRiskSectionText(Blob(WithCasfog(0m, 0m)));

        Assert.DoesNotContain(CasfogPdfMarker, text);
        Assert.DoesNotContain("CASFOG", text);
    }

    [Fact]
    public async Task PdfRiskSection_ForAnAnalysisScoredBeforeThisSignal_RendersUnchanged()
    {
        string conClave = await RenderRiskSectionText(Blob(WithCasfog(0m, null)));
        string sinClave = await RenderRiskSectionText(Blob(null));

        Assert.DoesNotContain(CasfogPdfMarker, sinClave);
        Assert.DoesNotContain(CasfogPdfMarker, conClave);
    }

    // ── 3. The two final-score penalties coexist ────────────────────────────────────────────────

    // Both adjustments move the same score, so a row carrying both must announce both — on both
    // surfaces. A reader shown only one cannot reconcile the components with the final score.
    [Fact]
    public async Task BothFinalScoreAdjustments_AreReportedTogether()
    {
        string keys = ", \"ventasAdj\": 10.00, \"ventasVariacion\": -0.3500, "
                    + "\"ventasBase\": \"interanual\", \"ventasVariacionEsReal\": true"
                    + WithCasfog(12.00m, 0.4000m);

        string block = AnalysisController.FormatScoreBreakdownBlock(Blob(keys), finalScore: 37.00m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains("Ventas declaradas", block);
        Assert.Contains(CasfogHeading, block);
        Assert.Contains("+10.0 puntos", block);
        Assert.Contains("+12.0 puntos", block);

        string text = await RenderRiskSectionText(Blob(keys));

        Assert.Contains("Ventas declaradas", text);
        Assert.Contains(CasfogPdfMarker, text);
    }
}
