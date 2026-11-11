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
/// Content-level tests for the cash-flow composition: every one of the three activity sections is
/// rendered (Operativo included, with its "no identifica ninguno" note), the RECPAM-on-cash figure
/// is rendered as its own row in its attributed section and subtracted from that section's residual
/// (never silently absorbed into "Resto sin identificar"), and the RECPAM-absent note is carried.
/// Assertions read the generated PDF's extracted text.
/// </summary>
public sealed class PdfExportCashFlowContentTests
{
    private static PdfExportService NewService() =>
        new(new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

    private static async Task<string> RenderCashFlowText(string cashFlowNode)
    {
        var result = new AnalysisResult
        {
            ResultId    = new Guid("22222222-2222-2222-2222-222222222222"),
            BalanceJson = $$"""{ "assets": 1, "cash_flow": {{cashFlowNode}} }""",
        };

        byte[] pdf = await NewService().GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            new[] { "cash_flow" }, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    [Fact]
    public async Task Composition_RecpamInsideFinancing_RendersOwnRowAndSubtractsFromResidual()
    {
        // financing subtotal -90M already includes the RECPAM (-25M) and dividends (-55M).
        // Identified = 55M + 25M = 80M ⇒ residual -10M. Without the fix the RECPAM is unidentified and
        // the residual would be -35M ("Resto sin identificar" absorbing a figure we identify exactly).
        //
        // Reconciliation is made to FAIL on purpose (sections 298M ≠ net_change 500M) so the verbatim
        // reading is the single caution sentence (no figures) — the narrative service computes its own
        // per-section residual WITHOUT subtracting RECPAM and would otherwise print "-$ 35.000.000",
        // which is expected verbatim Portal behaviour but must not pollute this composition assertion.
        string node = $$"""
        {
          "present": true, "method": "indirect",
          "cash_beginning": 700000000, "cash_end": 1200000000, "net_change": 500000000,
          "operating": 400000000, "investing": -12000000, "financing": -90000000,
          "monetary_result_on_cash": -25000000, "monetary_result_on_cash_section": "financing",
          "components": { "dividends_paid": -55000000 }
        }
        """;

        string text = await RenderCashFlowText(node);

        string packed = text.Replace(" ", "");
        Assert.Contains("RECPAM sobre el efectivo", text);   // RECPAM rendered as its own row
        Assert.Contains("Ajuste por inflaci", text);         // its inflation-adjustment marker
        Assert.Contains("25.000.000", packed);               // the RECPAM figure is shown
        Assert.Contains("Resto sin identificar", text);      // residual row still present
        Assert.Contains("10.000.000", packed);               // residual is -10M (RECPAM subtracted)
        Assert.DoesNotContain("35.000.000", packed);         // NOT -35M (RECPAM not absorbed)
    }

    [Fact]
    public async Task Composition_OperatingSection_IsRenderedWithItsNote()
    {
        // Operating has a subtotal but no catalogue components → the "no identifica ninguno" note.
        string node = $$"""
        {
          "present": true,
          "cash_beginning": 100000000, "cash_end": 130000000, "net_change": 30000000,
          "operating": 100000000, "investing": -40000000, "financing": -30000000,
          "monetary_result_on_cash": null,
          "components": { "capex": -40000000, "dividends_paid": -30000000 }
        }
        """;

        string text = await RenderCashFlowText(node);

        Assert.Contains("Operativo", text);
        Assert.Contains("esta lectura no identifica ninguno de sus conceptos", text);
    }

    [Fact]
    public async Task Composition_RecpamAbsent_CarriesTheAbsentNote()
    {
        string node = $$"""
        {
          "present": true,
          "cash_beginning": 100000000, "cash_end": 130000000, "net_change": 30000000,
          "operating": 100000000, "investing": -40000000, "financing": -30000000,
          "monetary_result_on_cash": null,
          "components": { "capex": -40000000, "dividends_paid": -30000000 }
        }
        """;

        string text = await RenderCashFlowText(node);

        Assert.Contains("El balance no informa el RECPAM sobre el efectivo", text);
    }

    // ── QA Q48: placements the statement printed under Financing (shared case 1) ─────

    [Fact]
    public async Task Composition_PlacementsPrintedInFinancing_AreListedThere_AndLeaveNoUnidentifiedRest()
    {
        string text = await RenderCashFlowText("""
            {
              "present": true,
              "operating": 1300, "investing": -1000, "financing": -300,
              "components": { "capex": -1000, "short_term_debt_raised": 200, "financial_placements": -500 }
            }
            """);

        Assert.DoesNotContain("Resto sin identificar", text);

        int financingLabel = text.IndexOf("Financiación:", StringComparison.Ordinal);
        int investingLabel = text.IndexOf("Inversión:", StringComparison.Ordinal);
        int placements     = text.IndexOf("Colocaciones financieras", StringComparison.Ordinal);
        Assert.True(investingLabel >= 0 && financingLabel > investingLabel, text);
        Assert.True(placements > financingLabel, "Colocaciones financieras must be listed under Financiación.");
    }

    // ── QA Q54: the reading prints the report's minus, not the screen's hyphen ─────

    [Fact]
    public async Task Reading_NegativeAmounts_PrintTheTypographicMinus()
    {
        string text = await RenderCashFlowText("""
            {
              "present": true,
              "operating": 1300, "investing": -1000, "financing": -300,
              "components": { "capex": -1000, "short_term_debt_raised": 200, "financial_placements": -500 }
            }
            """);

        Assert.Contains("y \u2212$ 500 en concepto de", text, StringComparison.Ordinal);   // the reading, not the table
        Assert.DoesNotContain("-$", text, StringComparison.Ordinal);
    }
}
