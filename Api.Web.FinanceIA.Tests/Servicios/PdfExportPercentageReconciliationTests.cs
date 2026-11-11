// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-14

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
/// End-to-end coverage for the read-time percentage reconciliation applied inside
/// <see cref="PdfExportService"/> (mirrors <c>AnalysisController.GetById</c>'s own read-time
/// repair — see PR #125). Before this test, there was zero coverage of reconciliation actually
/// running through the export path: this locks in that <see cref="PdfExportService"/>'s
/// <c>JsonOpts</c> correctly binds a real, persisted-shaped <c>BalanceJson</c> into
/// <see cref="BalanceAccounts"/>, and that both <see cref="IAlertRatioReconciliationService.Reconcile"/>
/// (risk alerts) and <see cref="IAlertRatioReconciliationService.ReconcileNarrative"/> (executive
/// summary) run and rewrite the rendered PDF text, not just the in-memory service under test.
/// </summary>
public sealed class PdfExportPercentageReconciliationTests
{
    private static PdfExportService NewService() =>
        new(new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

    private static async Task<string> RenderText(AnalysisResult result, IReadOnlyList<string> sections)
    {
        byte[] pdf = await NewService().GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            sections, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    [Fact]
    public async Task RiskAndExecutiveSummarySections_RenderReconciledPercentages_NotTheStoredWrongOnes()
    {
        // Realistic persisted-shaped BalanceJson — the same key names BalanceJsonForDb actually
        // writes (assets/liabilities/equity), proving PdfExportService.JsonOpts binds
        // BalanceAccounts from it correctly (a concern raised for this PR — confirmed NOT a bug in
        // planning; this test locks that in against regression).
        const string balanceJson =
            """{"assets": 12239430766.71, "liabilities": 4462924519.89, "equity": 7776506246.82}""";

        // Leaked-bug shape (the original defect PR #125 fixes): numerator before the clause, the
        // clause itself, and the denominator's own confirming figure right after it. 55,4% is
        // wrong — 6.236.534.182,74 / 12.239.430.766,71 = 51,0%.
        const string riskAlerts =
            """
            [{"level":"High","code":"BC-01","description":"Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del activo total de $12.239.430.766,71."}]
            """;

        // A wrong clause in free-form narrative text — exercises ReconcileNarrative.
        // 3.671.829.230,01 / 12.239.430.766,71 = 30,0%, not the stated 70%.
        const string executiveSummary =
            "Los bienes de uso ascienden a $3.671.829.230,01, representando el 70% del activo total.";

        var result = new AnalysisResult
        {
            ResultId         = new Guid("66666666-6666-6666-6666-666666666666"),
            BalanceJson      = balanceJson,
            RiskAlerts       = riskAlerts,
            ExecutiveSummary = executiveSummary,
        };

        string text   = await RenderText(result, new[] { "risk", "executive_summary" });
        string packed = text.Replace(" ", "");

        // Corrected percentages render...
        Assert.Contains("51,0", packed);
        Assert.Contains("30,0", packed);
        // ...the stored wrong ones never do.
        Assert.DoesNotContain("55,4", packed);
        Assert.DoesNotContain("70%", packed);
    }

    /// <summary>
    /// Anomalies get the same guard as risk alerts and executive_summary (verifiable-percentages
    /// v2.15, Part 2) — the one model-authored surface PR #125 left unreconciled. Same leaked-bug
    /// fixture as the risk-alert case above, this time inside the plain-string anomalies array
    /// (<see cref="AnalysisResult.Anomalies"/> is a raw JSON array of strings).
    /// </summary>
    [Fact]
    public async Task AnomaliesSection_RendersReconciledPercentage_NotTheStoredWrongOne()
    {
        const string balanceJson = """{"assets": 12239430766.71}""";

        const string anomalies =
            """
            ["Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del activo total de $12.239.430.766,71."]
            """;

        var result = new AnalysisResult
        {
            ResultId    = new Guid("77777777-7777-7777-7777-777777777777"),
            BalanceJson = balanceJson,
            Anomalies   = anomalies,
        };

        string text   = await RenderText(result, new[] { "anomalies" });
        string packed = text.Replace(" ", "");

        Assert.Contains("51,0", packed);
        Assert.DoesNotContain("55,4", packed);
    }

    /// <summary>
    /// M11: assets, liabilities and equity now persist as null when the model did not extract them
    /// (they used to be forced to 0). The balance table must render them as "No se pudo leer" (the ratios'
    /// words for an unread figure; it said "N/D" before QA Q54), not "$ 0", and
    /// the composition chart must not break.
    /// </summary>
    [Fact]
    public async Task BalanceSection_NullTotals_RenderAsNotDisclosed()
    {
        const string balanceJson =
            """{"assets": null, "current_assets": 600, "non_current_assets": 400, "liabilities": null, "current_liabilities": 500, "non_current_liabilities": 200, "equity": null}""";

        var result = new AnalysisResult
        {
            ResultId    = new Guid("88888888-8888-8888-8888-888888888888"),
            BalanceJson = balanceJson,
        };

        string text = await RenderText(result, new[] { "balance" });

        Assert.Contains("No se pudo leer", text);
        Assert.DoesNotContain("$ 0", text);
    }
}
