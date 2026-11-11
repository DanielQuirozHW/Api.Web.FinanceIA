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
/// Internal-consistency content tests for <see cref="PdfExportService"/>, asserting on extracted PDF
/// text. Covers: change C (a reconciliation figure renders once, with two decimals, never whole-peso),
/// change D (both ratio tables list the same indicators in the same canonical order, including the
/// non-computable one), and change H (the cash-flow bridge carries the statement's own reported net
/// change, and omits the row when there is none).
/// </summary>
public sealed class PdfExportInternalConsistencyTests
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

    private static AnalysisResult CashFlowResult(string cashFlowNode) => new()
    {
        ResultId    = new Guid("33333333-3333-3333-3333-333333333333"),
        BalanceJson = $$"""{ "assets": 1, "cash_flow": {{cashFlowNode}} }""",
    };

    // ── C — one figure, one rendering ─────────────────────────────────────────

    [Fact]
    public async Task CashFlowVerdict_RendersReconciliationFigure_WithTwoDecimals_NeverWholePeso()
    {
        // Sections do NOT reconcile: Expected = 100000000.50 - 45000000.25 - 30000000 = 25000000.25,
        // Reported (net_change) = 30000000.75. Continuity is kept passing (200M + 30000000.75 = end),
        // so only the sections verdict line renders the two disputed figures.
        string node = """
        {
          "present": true, "method": "indirect",
          "cash_beginning": 200000000, "cash_end": 230000000.75, "net_change": 30000000.75,
          "operating": 100000000.50, "investing": -45000000.25, "financing": -30000000
        }
        """;

        string text   = await RenderText(CashFlowResult(node), new[] { "cash_flow" });
        string packed = text.Replace(" ", "");

        Assert.Contains("no concilia", text);            // we are on the failure verdict path
        Assert.Contains("25.000.000,25", packed);        // Expected, two decimals + es-AR grouping
        Assert.Contains("30.000.000,75", packed);        // Reported, two decimals + es-AR grouping
        Assert.DoesNotContain("$25.000.000", packed);    // never the whole-peso Pesos() treatment
    }

    // ── D — both ratio tables list the same indicators, in the same order ──────

    // interest_coverage is null (non-computable). Keys are deliberately scrambled to prove both tables
    // render in the canonical RatioToBreakdownKey order regardless of the stored JSON key order.
    private const string ScrambledRatiosJson =
        """{"net_margin":10.0,"interest_coverage":null,"current_ratio":1.67,"roe":20.0,"acid_test":0.78,"roa":8.0,"debt_ratio":0.6,"leverage":1.5}""";

    private const string FullBreakdownJson =
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
          "scoreRatios": 21, "scoreBcra": 45, "scoreMacro": 40, "bcraExcluded": false
        }
        """;

    [Fact]
    public async Task RatioTables_ListSameIndicators_InCanonicalOrder_IncludingNonComputable()
    {
        var result = new AnalysisResult
        {
            ResultId           = new Guid("44444444-4444-4444-4444-444444444444"),
            RatiosJson         = ScrambledRatiosJson,
            ScoreBreakdownJson = FullBreakdownJson,
        };

        string ratios    = await RenderText(result, new[] { "ratios" });
        string breakdown = await RenderText(result, new[] { "ratio_breakdown" });

        // Canonical RatioToBreakdownKey order (== RatioMetaMap order). "Prueba" anchors "Prueba ácida"
        // without depending on how the accented glyph extracts.
        string[] canonical =
        {
            "Liquidez corriente", "Prueba", "Endeudamiento", "Apalancamiento",
            "ROE", "ROA", "Margen neto", "Cobertura de intereses",
        };

        foreach (string table in new[] { ratios, breakdown })
        {
            // Same indicators: every canonical label is present in each table.
            foreach (string label in canonical)
                Assert.Contains(label, table);

            // Same order: label positions are strictly increasing in canonical order.
            int prev = -1;
            foreach (string label in canonical)
            {
                int idx = table.IndexOf(label, StringComparison.Ordinal);
                Assert.True(idx > prev, $"'{label}' is out of canonical order. Table text:\n{table}");
                prev = idx;
            }

            // The non-computable indicator (interest_coverage = null) is shown, not dropped.
            Assert.Contains("No determinable", table);
        }
    }

    // ── H — the bridge carries the statement's own reported net change ─────────

    [Fact]
    public async Task CashFlowBridge_RendersReportedNetChangeRow_WhenPresent()
    {
        // Reconciles cleanly (sections sum 25M = net_change; 200M + 25M = 225M end), so "informada"
        // can only originate from the bridge's own "Variación informada" row, never the verdict.
        string node = """
        {
          "present": true, "method": "indirect",
          "cash_beginning": 200000000, "cash_end": 225000000, "net_change": 25000000,
          "operating": 100000000, "investing": -45000000, "financing": -30000000
        }
        """;

        string text   = await RenderText(CashFlowResult(node), new[] { "cash_flow" });
        string packed = text.Replace(" ", "");

        Assert.Contains("informada", text);        // the bridge's "Variación informada" row label
        Assert.Contains("$25.000.000", packed);    // its value — the reported net change (+$ 25.000.000)
    }

    [Fact]
    public async Task CashFlowBridge_OmitsReportedNetChangeRow_WhenNull()
    {
        // net_change null ⇒ reconciliation is "no evaluada" (its verdict says "variación neta:", not
        // "informada"), and the bridge omits the row entirely — so "informada" appears nowhere.
        string node = """
        {
          "present": true, "method": "indirect",
          "cash_beginning": 200000000, "cash_end": 225000000, "net_change": null,
          "operating": 100000000, "investing": -45000000, "financing": -30000000
        }
        """;

        string text = await RenderText(CashFlowResult(node), new[] { "cash_flow" });

        Assert.DoesNotContain("informada", text);
    }

    // ── Section reading ORDER (not just the set) ──────────────────────────────

    [Fact]
    public async Task Sections_RenderInReadingOrder_ConclusionFirst()
    {
        // The dispatch was reordered to how a credit analyst reads. This locks that ORDER, not just which
        // sections render: in an all-sections export the eight headings must appear at strictly increasing
        // positions. Reverting the reorder makes this fail. Technique mirrors the ratio-order test above.
        var result = new AnalysisResult
        {
            ResultId           = new Guid("55555555-5555-5555-5555-555555555555"),
            RatiosJson         = ScrambledRatiosJson,
            ScoreBreakdownJson = FullBreakdownJson,   // renders the Risk "Ratios financieros" component row
            AiSuggestedScore   = 30m,
            AiSuggestedLevel   = "High",
        };

        string text = await RenderText(result, Array.Empty<string>());   // empty ⇒ all sections

        // Collision-verified markers, Ordinal (case-SENSITIVE): the headings are in sentence case, as in the
        // Portal; the ratios table's "Indicador" header stands for its heading, which shares its words with the
        // risk section's "Ratios financieros" component row; "Datos BCRA" avoids the "Contexto BCRA" component.
        string[] readingOrder =
        {
            "Resumen ejecutivo", "Análisis de riesgo", "Estado patrimonial", "Indicador",
            "Flujo de efectivo", "Datos BCRA", "Anomalías detectadas",
        };

        int prev = -1;
        foreach (string marker in readingOrder)
        {
            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(idx > prev,
                $"'{marker}' is out of reading order (idx {idx}, prev {prev}).\nText:\n{text}");
            prev = idx;
        }
    }
}
