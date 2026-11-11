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
/// Pins the two places that explain the LUFE sales-drop penalty to a reader (§7.3a): the chat system
/// prompt and the PDF export. Both are fed from the persisted <c>ScoreBreakdownJson</c>, never from a
/// live <see cref="ScoringResult"/>.
/// <para>
/// WHY THIS MATTERS BEYOND COSMETICS: this penalty is the only adjustment that acts on the FINAL
/// score rather than inside a component, so the component figures both surfaces already print will
/// NOT add up to the final score without it. Omitting the line leaves the model and the report
/// explaining an arithmetic they cannot close — a worse failure than for the cheques penalty, which
/// at least stays inside the BCRA component it distorts.
/// </para>
/// <para>
/// The silence cases are equally load-bearing. <c>ventasAdj</c> is the REALISED movement, so a zero
/// means the score does not carry the penalty and nothing may be announced; and an analysis scored
/// before this signal existed carries no such key at all, which must render exactly as it did then.
/// </para>
/// <para>
/// Note the deliberate culture split, which mirrors the §7.1 lines: the chat prompt is built with
/// InvariantCulture so it does not change shape with the server's culture, while the PDF is es-AR
/// throughout. The assertions below check for dot decimals in one and comma decimals in the other.
/// </para>
/// </summary>
public sealed class VentasAdjReportingTests
{
    private const string VentasHeading = "Ventas declaradas";

    // A breakdown blob in the shape the controller persists, with the ventas keys optionally present.
    // Written with invariant literals so a comma-decimal test host cannot produce a blob that fails to
    // parse and turn these into false passes.
    private static string Blob(string? ventasKeys) => $$"""
        {
          "scoreRatios": 21.00, "scoreBcra": 0.00, "scoreMacro": 30.00,
          "scoreBeforeFloor": 15.00, "appliedFloor": 0.00, "criticalRatios": [],
          "bcraTrendDelta": 0, "bcraTrendAdj": 0.00,
          "sectorProfile": "General"{{ventasKeys}}
        }
        """;

    /// <summary>
    /// The §7.3a key shape: the three ventas keys and NO <c>ventasVariacionEsReal</c>, which is what
    /// every analysis scored before §7.3b carries. Its absence is meaningful — see
    /// <see cref="WithVentasV7"/>.
    /// </summary>
    private static string WithVentas(decimal ventasAdj, decimal? variacion, string? basis)
        => VentasKeys(ventasAdj, variacion, basis, esReal: null, incluirFlag: false);

    /// <summary>
    /// The §7.3b key shape: the same three keys PLUS <c>ventasVariacionEsReal</c>, which may itself
    /// be null (the penalty was not evaluated). Three states have to be distinguishable —
    /// key absent = scored before §7.3b and therefore nominal by design; false = §7.3b looked and had
    /// no CPI covering the basis; true = deflated — and only the JSON can express the first.
    /// </summary>
    private static string WithVentasV7(decimal ventasAdj, decimal? variacion, string? basis, bool? esReal)
        => VentasKeys(ventasAdj, variacion, basis, esReal, incluirFlag: true);

    private static string VentasKeys(
        decimal ventasAdj, decimal? variacion, string? basis, bool? esReal, bool incluirFlag)
    {
        string v = variacion is { } x
            ? $", \"ventasVariacion\": {x.ToString(CultureInfo.InvariantCulture)}"
            : ", \"ventasVariacion\": null";
        string b = basis is not null ? $", \"ventasBase\": \"{basis}\"" : ", \"ventasBase\": null";
        string r = incluirFlag
            ? $", \"ventasVariacionEsReal\": {(esReal is { } f ? (f ? "true" : "false") : "null")}"
            : "";
        return $", \"ventasAdj\": {ventasAdj.ToString(CultureInfo.InvariantCulture)}{v}{b}{r}";
    }

    // ── 1. The chat system prompt ───────────────────────────────────────────────────────────────

    [Fact]
    public void ChatBreakdownBlock_WhenThePenaltyWasApplied_ExplainsTheDropAndThePoints()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentas(8.00m, -0.20m, "interanual")), finalScore: 23.00m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains(VentasHeading, block);
        Assert.Contains("20.0 %", block);            // invariant: dot decimals
        Assert.Contains("interanual", block);
        Assert.Contains("+8.0 puntos", block);

        // Says which score moved. This penalty does not touch a component, so a reader told only
        // "penalidad aplicada" would reasonably assume it came out of the BCRA term.
        Assert.Contains("score final", block);

        // This blob has no ventasVariacionEsReal key, so it is a §7.3a row and must keep §7.3a's
        // exact sentence. "nominal" alone would not prove that — it is a substring of "nominales"
        // and of "nominales (sin datos de inflación)", so all three states would satisfy it.
        Assert.Contains("medida en términos nominales;", block);
    }

    [Fact]
    public void ChatBreakdownBlock_WhenTheBasisWasQuarterly_SaysSo()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentas(8.00m, -0.20m, "trimestral")), finalScore: 23.00m)!;

        Assert.Contains("trimestral", block);
        Assert.DoesNotContain("interanual", block);
    }

    // ventasAdj is the REALISED movement, so 0 means the score does not carry the penalty — whether
    // sales grew, the tenant switched the signal off, or the score was already at the ceiling.
    [Fact]
    public void ChatBreakdownBlock_WhenThePenaltyMovedNothing_SaysNothing()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentas(0m, 0.15m, "interanual")), finalScore: 15.00m)!;

        Assert.DoesNotContain(VentasHeading, block);
    }

    // An analysis scored before algorithm version 6 has no ventas keys at all. It is never recomputed,
    // so the blob must keep rendering exactly as it did — no line, and no crash on the missing keys.
    [Fact]
    public void ChatBreakdownBlock_WhenTheBlobPredatesTheSignal_RendersUnchanged()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(Blob(null), finalScore: 15.00m)
                       ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain(VentasHeading, block);
        Assert.Contains("DESGLOSE DEL SCORING", block);   // the rest of the block is intact
    }

    // A blob carrying the penalty but not the variation still has to report the points: the score
    // moved, and the reason it moved is the part that is missing, not the movement.
    [Fact]
    public void ChatBreakdownBlock_WhenTheVariationIsAbsent_StillReportsThePoints()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentas(8.00m, null, null)), finalScore: 23.00m)!;

        Assert.Contains(VentasHeading, block);
        Assert.Contains("+8.0 puntos", block);
    }

    // ── 2. The PDF export ───────────────────────────────────────────────────────────────────────
    //
    // The ventas row lives in the general "Análisis de Riesgo" section beside the critical-floor
    // callout — NOT in "Datos BCRA" where the cheques row is — because it is not a BCRA figure and it
    // moves the final score rather than a component.

    private static async Task<string> RenderRiskSectionText(string scoreBreakdownJson)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("55555555-5555-5555-5555-555555555555"),
            AiSuggestedScore   = 23.00m,
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
    public async Task PdfRiskSection_WhenThePenaltyWasApplied_RendersTheVentasRow()
    {
        string text = await RenderRiskSectionText(Blob(WithVentas(8.00m, -0.20m, "interanual")));

        Assert.Contains(VentasHeading, text);

        // No ventasVariacionEsReal key: a §7.3a row, which keeps §7.3a's exact parenthetical. Pinned
        // as the whole parenthetical rather than the bare word, which every state contains.
        Assert.Contains("(interanual, nominal)", text);

        // es-AR throughout this report: comma decimal separator, as the callout beside it uses.
        Assert.Contains("20,0", text);

        // On the 1000 scale, as the Portal's anatomy shows it: 395 + 350 + 105 = 850, 8.0 internal is
        // −80, and 850 − 80 = 770 = toNosis(23), so nothing is left to round.
        Assert.Contains("\u221280 pts", text);
        Assert.Contains("Score final 770", text);
        Assert.DoesNotContain("+8,0", text);
    }

    [Fact]
    public async Task PdfRiskSection_WhenThePenaltyMovedNothing_RendersNoVentasRow()
    {
        string text = await RenderRiskSectionText(Blob(WithVentas(0m, -0.02m, "interanual")));

        Assert.DoesNotContain(VentasHeading, text);
        Assert.Contains("Análisis de riesgo", text);   // the section itself still rendered
    }

    [Fact]
    public async Task PdfRiskSection_WhenTheBlobPredatesTheSignal_RendersNoVentasRow()
    {
        string text = await RenderRiskSectionText(Blob(null));

        Assert.DoesNotContain(VentasHeading, text);
        Assert.Contains("Análisis de riesgo", text);   // the section itself still rendered
    }

    // ── 3. Real vs nominal (§7.3b) ──────────────────────────────────────────────────────────────
    //
    // The variation changed MEANING at algorithm version 7, so the surfaces have to say which one a
    // given row carries. There are THREE states, and the third is the reason the flag is nullable:
    //
    //   key absent  → scored before §7.3b. Nominal BY DESIGN, not for want of data, and it renders
    //                 with §7.3a's exact wording so re-exporting an old analysis is byte-identical.
    //   true        → deflated by the BCRA monthly-inflation series.
    //   false       → §7.3b evaluated it and had no CPI covering the basis, so this is the nominal
    //                 figure. Saying that about an old row would be a claim about it that is untrue.
    //
    // Each state is asserted on the full clause, never on the bare word "nominal" — that is a
    // substring of all three renderings and would make every one of these pass vacuously.

    [Theory]
    [InlineData(true,  "medida en términos reales (deflactado por IPC)")]
    [InlineData(false, "medida en términos nominales (sin datos de inflación)")]
    public void ChatBreakdownBlock_SaysWhetherTheVariationWasDeflated(bool esReal, string expected)
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentasV7(8.00m, -0.20m, "interanual", esReal)),
                           finalScore: 23.00m)!;

        Assert.Contains(expected, block);

        // The figures either side of the clause are unaffected by it.
        Assert.Contains("20.0 %", block);
        Assert.Contains("+8.0 puntos", block);
    }

    // The variation-absent arm carries the clause too: "we could not tell you the size of the drop"
    // and "we could not deflate it" are different admissions, and the reader needs both.
    [Theory]
    [InlineData(true,  "medida en términos reales (deflactado por IPC)")]
    [InlineData(false, "medida en términos nominales (sin datos de inflación)")]
    public void ChatBreakdownBlock_WithNoVariation_StillSaysWhetherItWasDeflated(bool esReal, string expected)
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentasV7(8.00m, null, null, esReal)), finalScore: 23.00m)!;

        Assert.Contains("caída registrada", block);
        Assert.Contains(expected, block);
    }

    // A §7.3b row whose penalty was evaluated always carries true or false, so a null flag beside a
    // non-zero ventasAdj cannot occur — but a hand-edited or partially-written blob could produce
    // one, and it must degrade to the old wording rather than crash or invent a claim.
    [Fact]
    public void ChatBreakdownBlock_WhenTheFlagIsPresentButNull_UsesTheUnqualifiedWording()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentasV7(8.00m, -0.20m, "interanual", esReal: null)),
                           finalScore: 23.00m)!;

        Assert.Contains("medida en términos nominales;", block);
        Assert.DoesNotContain("sin datos de inflación", block);
    }

    [Theory]
    [InlineData(true,  "(interanual, real — deflactado por IPC)")]
    [InlineData(false, "(interanual, nominal — sin datos de inflación)")]
    public async Task PdfRiskSection_SaysWhetherTheVariationWasDeflated(bool esReal, string expected)
    {
        string text = await RenderRiskSectionText(
            Blob(WithVentasV7(8.00m, -0.20m, "interanual", esReal)));

        Assert.Contains(VentasHeading, text);
        Assert.Contains(expected, text);
        Assert.Contains("20,0", text);   // still es-AR
    }

    [Theory]
    [InlineData(true,  "real — deflactado por IPC")]
    [InlineData(false, "nominal — sin datos de inflación")]
    public async Task PdfRiskSection_WithNoVariation_StillSaysWhetherItWasDeflated(bool esReal, string expected)
    {
        string text = await RenderRiskSectionText(Blob(WithVentasV7(8.00m, null, null, esReal)));

        Assert.Contains("caída registrada", text);
        Assert.Contains(expected, text);
    }

    // The property this whole section protects: an analysis scored under §7.3a re-exported today
    // comes out with the sentence it was written under, on both surfaces.
    [Fact]
    public async Task BothSurfaces_ForAPreV7Blob_RenderExactlyTheSeventhreeaWording()
    {
        string blob = Blob(WithVentas(8.00m, -0.20m, "trimestral"));

        string block = AnalysisController.FormatScoreBreakdownBlock(blob, finalScore: 23.00m)!;
        Assert.Contains("medida en términos nominales;", block);
        Assert.DoesNotContain("deflactado por IPC", block);
        Assert.DoesNotContain("sin datos de inflación", block);

        string text = await RenderRiskSectionText(blob);
        Assert.Contains("(trimestral, nominal)", text);
        Assert.DoesNotContain("deflactado por IPC", text);
        Assert.DoesNotContain("sin datos de inflación", text);
    }

    // ── 4. The three-month basis (scoring algorithm 9) ──────────────────────────────────────────
    //
    // A value neither surface knew would fall into its default arm and read "base no informada" /
    // "base n/d" — a basis the engine DID report, rendered as missing.

    [Fact]
    public void ChatBreakdownBlock_WhenTheBasisWasThreeMonthInteranual_SaysSo()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
                           Blob(WithVentasV7(1.71m, -0.1429m, "interanual_3m", esReal: false)), finalScore: 16.71m)!;

        Assert.Contains("interanual, últimos 3 meses contra los mismos 3 meses del año anterior", block);
        Assert.DoesNotContain("base no informada", block);
    }

    [Fact]
    public async Task PdfRiskSection_WhenTheBasisWasThreeMonthInteranual_SaysSo()
    {
        string text = await RenderRiskSectionText(
            Blob(WithVentasV7(1.71m, -0.1429m, "interanual_3m", esReal: true)));

        Assert.Contains("(interanual 3 meses, real — deflactado por IPC)", text);
        Assert.DoesNotContain("base n/d", text);
    }
}
