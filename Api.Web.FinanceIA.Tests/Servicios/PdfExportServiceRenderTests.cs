// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-13

using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// End-to-end rendering smoke tests for <see cref="PdfExportService"/>. These do not assert visual
/// output; they guard against iText runtime failures (mismatched table cell counts, null paragraph
/// text) across every section — especially the new executive-summary and cash-flow sections and the
/// widened score-component table — and confirm a cash-flow failure never aborts the export.
/// </summary>
public sealed class PdfExportServiceRenderTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF");

    internal static PdfExportService NewService() =>
        new(new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

    internal static Analysis SampleAnalysis() => new() { FiscalYear = 2025 };

    private const string RatiosJson =
        """{"current_ratio":1.67,"acid_test":0.78,"debt_ratio":0.6,"leverage":1.5,"roe":20.0,"roa":8.0,"net_margin":10.0,"interest_coverage":null}""";

    private const string ScoreBreakdownJson =
        """
        {
          "breakdown": {
            "liquidez_corriente": {"score": 10, "status": "Saludable"},
            "prueba_acida":       {"score": 20, "status": "Saludable"},
            "endeudamiento":      {"score": 30, "status": "Atencion"},
            "leverage":           {"score": 15, "status": "Saludable"},
            "cobertura_intereses":{"score": 50, "status": "Atencion"},
            "roe":                {"score": 5,  "status": "Saludable"},
            "roa":                {"score": 10, "status": "Saludable"},
            "margen_neto":        {"score": 8,  "status": "Saludable"}
          },
          "scoreRatios": 21, "scoreBcra": 45, "scoreMacro": 40,
          "bcraExcluded": false,
          "effectiveWeightRatios": 0.50, "effectiveWeightBcra": 0.35, "effectiveWeightMacro": 0.15,
          "sectorProfile": "General"
        }
        """;

    private const string RiskAlertsJson =
        """[{"level":"high","code":"LIQ-01","description":"Liquidez ajustada"},{"level":"medium","code":"END-02","description":"Endeudamiento elevado"},{"level":"low","code":"INF-03","description":"Contexto inflacionario"}]""";

    private const string AnomaliesJson =
        """["El estado de flujo de efectivo no concilia: suma de secciones 423875995.69 vs variación informada 804632266.63. Las cifras se muestran tal como fueron extraídas."]""";

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
            "components": { "capex": -40000000, "dividends_paid": -30000000 }
          }
        }
        """;

    internal static AnalysisResult SampleResult(double cashEnd) => new()
    {
        ResultId          = new Guid("11111111-1111-1111-1111-111111111111"),
        BalanceJson       = BalanceJson(cashEnd),
        RatiosJson        = RatiosJson,
        ScoreBreakdownJson = ScoreBreakdownJson,
        AiSuggestedScore  = 30m,
        AiSuggestedLevel  = "High",
        ExecutiveSummary  = "La empresa presenta liquidez adecuada y rentabilidad positiva, con un nivel de endeudamiento a monitorear.",
        ScoringNarrative  = "El score refleja ratios sólidos compensados por el contexto macroeconómico.",
        Anomalies         = AnomaliesJson,
        RiskAlerts        = RiskAlertsJson,
    };

    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1000, $"PDF suspiciously small: {bytes.Length} bytes");
        Assert.Equal(PdfMagic, bytes.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public async Task GenerateAsync_AllSections_ReconcilingCashFlow_ProducesPdf()
    {
        // cash_end = 225000000 → both reconciliation checks pass; empty sections → every section.
        byte[] bytes = await NewService().GenerateAsync(
            SampleResult(225000000), SampleAnalysis(), "tenant-x",
            Array.Empty<string>(), CancellationToken.None);

        AssertIsPdf(bytes);
    }

    [Fact]
    public async Task GenerateAsync_CashFlowReconciliationFails_StillProducesPdf()
    {
        // cash_end = 999000000 breaks the beginning+change vs end check → fail verdict path.
        byte[] bytes = await NewService().GenerateAsync(
            SampleResult(999000000), SampleAnalysis(), "tenant-x",
            new[] { "cash_flow", "footer" }, CancellationToken.None);

        AssertIsPdf(bytes);
    }

    [Fact]
    public async Task GenerateAsync_MalformedCashFlowNode_DoesNotAbortExport()
    {
        AnalysisResult result = SampleResult(225000000);
        result.BalanceJson = """{"assets":1,"cash_flow":"not-an-object"}""";

        byte[] bytes = await NewService().GenerateAsync(
            result, SampleAnalysis(), "tenant-x",
            new[] { "cash_flow" }, CancellationToken.None);

        AssertIsPdf(bytes);
    }

    [Fact]
    public async Task GenerateAsync_UnknownKey_DoesNotThrow()
    {
        // A key with no renderer flows through without aborting (the controller would have 400'd it).
        byte[] bytes = await NewService().GenerateAsync(
            SampleResult(225000000), SampleAnalysis(), "tenant-x",
            new[] { "balance", "totally_unknown_key", "footer" }, CancellationToken.None);

        AssertIsPdf(bytes);
    }
}
