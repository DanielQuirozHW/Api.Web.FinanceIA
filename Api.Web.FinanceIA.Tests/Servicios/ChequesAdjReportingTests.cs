// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

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
/// Pins the two places that explain the rejected-cheques penalty to a reader: the chat system prompt
/// and the PDF export. Both are fed from the persisted <c>ScoreBreakdownJson</c>, never from a live
/// <see cref="ScoringResult"/>.
/// <para>
/// WHY THIS MATTERS BEYOND COSMETICS: both surfaces explain why scoreBcra differs from the raw
/// situación, and the chat block closes by instructing the model to explain these figures and to call
/// anything not listed unavailable. A penalty folded into scoreBcra with no line here would leave the
/// model and the report explaining a BCRA score whose arithmetic they cannot see — the same class of
/// defect as the trend adjustment claiming points it never applied.
/// </para>
/// <para>
/// The silence cases are equally load-bearing. <c>chequesAdj</c> is the REALISED movement, so a zero
/// means the score does not carry the penalty and nothing may be announced; and an analysis scored
/// before this signal existed carries no such key at all, which must render exactly as it did then.
/// </para>
/// </summary>
public sealed class ChequesAdjReportingTests
{
    private const string ChequesHeading = "Cheques rechazados";

    // The tail only the PDF's cheques row prints — the row, not the word, is what the silence cases pin.
    private const string ChequesRowMarker = "(incluido en el score BCRA)";

    // A breakdown blob in the shape the controller persists, with the cheques keys optionally present.
    // Written with invariant literals so a comma-decimal test host cannot produce a blob that fails to
    // parse and turn these into false passes.
    private static string Blob(string? chequesKeys) => $$"""
        {
          "scoreRatios": 21.00, "scoreBcra": 51.00, "scoreMacro": 30.00,
          "scoreBeforeFloor": 32.85, "appliedFloor": 0.00, "criticalRatios": [],
          "bcraTrendDelta": 0, "bcraTrendAdj": 0.00,
          "sectorProfile": "General"{{chequesKeys}}
        }
        """;

    private static string WithCheques(decimal chequesAdj, decimal? proporcion)
    {
        string prop = proporcion is { } p
            ? $", \"chequesProporcionRecuperada\": {p.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : ", \"chequesProporcionRecuperada\": null";
        return $", \"chequesAdj\": {chequesAdj.ToString(System.Globalization.CultureInfo.InvariantCulture)}{prop}";
    }

    // ── 1. The chat system prompt ───────────────────────────────────────────────────────────────

    [Fact]
    public void ChatBreakdownBlock_WhenThePenaltyWasApplied_ExplainsRecoveryAndPoints()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(WithCheques(6.00m, 0.6m)), finalScore: 32.85m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains(ChequesHeading, block);
        // InvariantCulture in the prompt builder, so these are stable on any test host.
        Assert.Contains("se recuperó el 60.0 % del importe rechazado", block);
        Assert.Contains("+6.0 puntos", block);
    }

    // Zero means the clamp absorbed the penalty, or there was nothing to penalise: the score does not
    // carry it, so nothing may be claimed. This is the trend block's D3 lesson applied to the sibling.
    [Fact]
    public void ChatBreakdownBlock_WhenThePenaltyMovedNothing_SaysNothing()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(WithCheques(0.00m, 0.0m)), finalScore: 30.75m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain(ChequesHeading, block);
    }

    // An analysis scored before algorithm version 5 has no cheques keys at all. It is never recomputed,
    // so the blob must keep rendering exactly as it did — no line, and no crash on the missing keys.
    [Fact]
    public void ChatBreakdownBlock_WhenTheBlobPredatesTheSignal_RendersUnchanged()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(Blob(null), finalScore: 32.85m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain(ChequesHeading, block);
        Assert.Contains("DESGLOSE DEL SCORING", block);   // the rest of the block is intact
    }

    // A penalty was applied but the proportion is absent — a shape only a hand-edited blob produces.
    // The points are real and must still be reported; the unknown input is reported as unknown rather
    // than invented, the same treatment the floor callout gives a missing before-score.
    [Fact]
    public void ChatBreakdownBlock_WhenTheRecoveredShareIsAbsent_StillReportsThePoints()
    {
        string block = AnalysisController.FormatScoreBreakdownBlock(
            Blob(WithCheques(6.00m, null)), finalScore: 32.85m)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains(ChequesHeading, block);
        Assert.Contains("el importe recuperado no está disponible", block);
        Assert.Contains("+6.0 puntos", block);
    }

    // ── 2. The PDF export ───────────────────────────────────────────────────────────────────────

    // The cheques row lives in the BCRA section beside the trend block, so the fixture needs a BCRA
    // snapshot as well as a breakdown.
    private const string BcraDataJson = """
        {"deuda":{"identificacion":30712345678,"situacionMaxima":3,"montoTotal":1500.0,
          "periodoConsultado":"202601","estado":0,"fetchedAt":"2026-02-01T00:00:00Z"},
         "cheques":{"totalRechazados":1,"totalImpagos":0,"montoTotalRechazado":100000.0,
          "montoTotalImpago":40000.0,"estado":0,"fetchedAt":"2026-02-01T00:00:00Z"}}
        """;

    private static async Task<string> RenderBcraSectionText(string scoreBreakdownJson)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("44444444-4444-4444-4444-444444444444"),
            AiSuggestedScore   = 32.85m,
            BcraDataJson       = BcraDataJson,
            ScoreBreakdownJson = scoreBreakdownJson,
        };

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            new[] { "bcra" }, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    [Fact]
    public async Task PdfBcraSection_WhenThePenaltyWasApplied_RendersTheChequesRow()
    {
        string text = await RenderBcraSectionText(Blob(WithCheques(6.00m, 0.6m)));

        Assert.Contains(ChequesHeading, text);
        // Restated on the 1000 scale exactly as the Portal's anatomy restates it (6.0 internal → −60),
        // and flagged as already inside the BCRA component rather than a term of composite → final.
        Assert.Contains("\u221260 pts " + ChequesRowMarker, text);
        Assert.DoesNotContain("penalidad +6,0", text);
        // es-AR throughout this report: comma decimal separator.
        Assert.Contains("60,0", text);
    }

    [Fact]
    public async Task PdfBcraSection_WhenThePenaltyMovedNothing_RendersNoChequesRow()
    {
        string text = await RenderBcraSectionText(Blob(WithCheques(0.00m, 0.0m)));

        Assert.DoesNotContain(ChequesRowMarker, text);
    }

    [Fact]
    public async Task PdfBcraSection_WhenTheBlobPredatesTheSignal_RendersNoChequesRow()
    {
        string text = await RenderBcraSectionText(Blob(null));

        Assert.DoesNotContain(ChequesRowMarker, text);
        Assert.Contains("Datos BCRA", text);   // the section itself still rendered
    }
}
