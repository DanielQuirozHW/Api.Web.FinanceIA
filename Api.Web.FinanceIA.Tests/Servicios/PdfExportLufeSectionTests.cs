// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the "Ventas declaradas (LUFE)" PDF section — the monthly sales series behind the §7.3
/// sales-drop penalty that the risk section states as a one-line verdict.
/// <para>
/// The honesty rules this section carries, each one a place where a rendering bug would invent a fact:
/// a null LUFE month means NO DECLARATION WAS FILED, never zero sales, so it must read
/// "Sin declaración" and never "$ 0"; the series is ragged, so a month missing from it entirely is the
/// same fact and must be counted the same way; the real variation is deflated with the CPI of the
/// analysis's own frozen BCRA snapshot through <see cref="DeflactorIpc"/>, the scoring engine's own
/// arithmetic; and the variation the SCORE used is read off the breakdown and printed with the risk
/// row's own clause — the PDF never recomputes it, so the two cannot tell two stories.
/// </para>
/// <para>
/// Absent data renders NOTHING, not even the heading — unlike BCRA and CASFOG, which print a
/// placeholder. The section ships in the "tecnico" template for every tenant, and most analyses carry
/// no LUFE series, so the absent cases are as load-bearing as the rendered ones.
/// </para>
/// <para>
/// Fixtures are real <see cref="LufeData"/> / <see cref="BcraData"/> objects serialised with a bare
/// <c>JsonSerializer.Serialize</c> — how the entity column and the BCRA snapshot are persisted.
/// Assertions pin ASCII substrings (iText's extractor is the wrong place to bet on how an accented
/// glyph round-trips), and every number and phrase goes through <see cref="Squash"/>.
/// </para>
/// </summary>
public sealed class PdfExportLufeSectionTests
{
    private const string SectionMarker = "Ventas declaradas (LUFE)";
    private const string MonthlyMarker = "Ventas y compras mensuales";
    private const string AnnualMarker  = "Indicadores anuales";
    private const string SinDeclarar   = "Sin declaraci";   // "Sin declaración"

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>"YYYYMM" for <paramref name="count"/> consecutive months starting at year/month.</summary>
    private static IEnumerable<string> Months(int year, int month, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int idx = (year * 12) + (month - 1) + i;
            yield return string.Create(CultureInfo.InvariantCulture, $"{idx / 12:D4}{(idx % 12) + 1:D2}");
        }
    }

    /// <summary>
    /// 24 months, 07/2024 → 06/2026: $ 1.000.000 a month in the first year, $ 1.100.000 in the second,
    /// purchases $ 400.000 throughout. Nominal interanual +10,0 % on every one of the twelve pairs.
    /// </summary>
    private static List<LufeVentaCompraMes> FullSeries() =>
        Months(2024, 7, 24)
            .Select((p, i) => new LufeVentaCompraMes
            {
                Periodo = p,
                Ventas  = i < 12 ? 1_000_000m : 1_100_000m,
                Compras = 400_000m
            })
            .ToList();

    /// <summary>A flat 2 % a month from 01/2024 to 06/2026 — covers every pair's twelve-month span.</summary>
    private static string BcraWithFlatCpi() =>
        JsonSerializer.Serialize(new BcraData
        {
            Macro = new BcraMacroData
            {
                InflacionMensualSerie = Months(2024, 1, 30).ToDictionary(p => p, _ => 2.0d)
            }
        });

    private static string Json(LufeData data) => JsonSerializer.Serialize(data);

    // A breakdown blob in the shape the controller persists, invariant literals throughout.
    private static string Breakdown(decimal ventasAdj, decimal? variacion, string basis, bool esReal) =>
        "{\"scoreRatios\": 21.00, \"scoreBcra\": 0.00, \"scoreMacro\": 30.00, \"sectorProfile\": \"General\"" +
        $", \"ventasAdj\": {ventasAdj.ToString(CultureInfo.InvariantCulture)}" +
        $", \"ventasVariacion\": {(variacion is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null")}" +
        $", \"ventasBase\": \"{basis}\", \"ventasVariacionEsReal\": {(esReal ? "true" : "false")}}}";

    // ── Render helper ─────────────────────────────────────────────────────────

    private static async Task<string> RenderText(
        string?               lufeDataJson,
        string?               bcraDataJson       = null,
        string?               scoreBreakdownJson = null,
        IReadOnlyList<string>? sections          = null)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        var result = new AnalysisResult
        {
            ResultId           = new Guid("99999999-9999-9999-9999-999999999999"),
            AiSuggestedScore   = 23.00m,
            AiSuggestedLevel   = "Low",
            LufeDataJson       = lufeDataJson,
            BcraDataJson       = bcraDataJson,
            ScoreBreakdownJson = scoreBreakdownJson
        };

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            sections ?? new[] { "lufe" }, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    // Prose wraps wherever the column ends, so a multi-word phrase is matched with EVERY whitespace
    // removed — otherwise a line break turns Contains into a false failure and, worse, DoesNotContain
    // into a vacuous pass.
    private static string Squash(string text) => Regex.Replace(text, @"\s+", "");

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // A "$ 0" amount — never a legitimate rendering of a LUFE month, whose null is "no declaration".
    private static bool HasZeroPesos(string packed) => Regex.IsMatch(packed, @"\$0(?![\d.])");

    // ── A full series ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FullSeries_StatesTheTwelveMonthTotalAsContext_AndNoSecondComparison()
    {
        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), BcraWithFlatCpi());
        string packed = Squash(text);

        Assert.Contains(SectionMarker, text);

        // Window 07/2025 – 06/2026, anchored on the latest month carrying sales, all twelve declared — stated
        // as context, explicitly NOT what the score used.
        Assert.Contains("Contexto(nousadoporelscore)", packed);
        Assert.Contains("07/2025", packed);
        Assert.Contains("$13.200.000", packed);
        Assert.Contains("sobre12mesescondeclaraci", packed);
        Assert.Contains("0de12sindeclaraci", packed);

        // ONE window is the score's: the section's own "mismos N meses" comparison is gone.
        Assert.DoesNotContain("mismos12meses", packed);
        Assert.DoesNotContain("$10.408.110", packed);
    }

    [Fact]
    public async Task FullSeries_MonthlyTableListsTheWindowMostRecentFirst()
    {
        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), BcraWithFlatCpi());

        int table = text.IndexOf(MonthlyMarker, StringComparison.Ordinal);
        Assert.True(table >= 0);
        string tableText = text[table..];
        string packed    = Squash(tableText);

        int newest = tableText.IndexOf("06/2026", StringComparison.Ordinal);
        int oldest = tableText.IndexOf("07/2025", StringComparison.Ordinal);
        Assert.True(newest >= 0 && oldest > newest, "The monthly table must run from the most recent month back.");

        // Exactly the twelve-month window: the prior year is compared, not tabled.
        Assert.DoesNotContain("06/2025", tableText);

        // es-AR money, and Ventas/Compras = 1.100.000 / 400.000 = 2,75x on every row.
        Assert.Equal(12, Count(packed, "$1.100.000"));
        Assert.Equal(12, Count(packed, "2,75x"));
        Assert.DoesNotContain(SinDeclarar, tableText);
    }

    // ── Undeclared months ─────────────────────────────────────────────────────

    [Fact]
    public async Task NullAndMissingMonths_ReadSinDeclaracion_AndAreCounted()
    {
        List<LufeVentaCompraMes> serie = FullSeries();

        // 03/2026 present with sales null (purchases declared) — LUFE's "no declaration", not zero.
        serie.Single(m => m.Periodo == "202603").Ventas = null;
        // 11/2025 omitted entirely — the ragged-series shape of the same fact.
        serie.RemoveAll(m => m.Periodo == "202511");

        string text = await RenderText(Json(new LufeData { VentasComprasMensual = serie }), BcraWithFlatCpi());
        string packed = Squash(text);

        Assert.Contains("2de12sindeclaraci", packed);
        Assert.Contains("sobre10mesescondeclaraci", packed);
        Assert.Contains("$11.000.000", packed);   // 10 × 1.100.000 — the gaps add nothing

        // Two undeclared sales cells plus the missing month's purchases cell.
        Assert.Equal(3, Count(text, SinDeclarar));
        Assert.False(HasZeroPesos(packed), "An undeclared month must never render as $ 0.");
    }

    // ── CPI does not cover the span ───────────────────────────────────────────

    [Fact]
    public async Task WithoutCpi_ANominalScore_StatesItsWindow_AndNoRealCompanion()
    {
        // 04–06/2026 (3.300.000) against 04–06/2025 (3.000.000): nominal +10,0 %, scored without inflation data.
        string breakdown = Breakdown(0m, 0.10m, "interanual_3m", esReal: false);

        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), bcraDataJson: null, breakdown);
        string packed = Squash(text);

        Assert.Contains("suba10,0%(interanual3meses,nominal—sindatosdeinflación)", packed);
        Assert.Contains("Ventana:04–06/2026vs.04–06/2025(interanual,3meses)", packed);
        Assert.DoesNotContain("Variaciónrealdelamismaventana", packed);
    }

    [Fact]
    public async Task ABreakdownTheSnapshotNoLongerReproduces_PrintsOnlyTheScoresFigure()
    {
        // Twelve months only: nothing a year earlier, so the engine's arithmetic on this snapshot cannot land
        // on interanual_3m. The score's own figure prints; no window and no companion are guessed.
        List<LufeVentaCompraMes> serie = FullSeries().Skip(12).ToList();
        string breakdown = Breakdown(8.00m, -0.20m, "interanual_3m", esReal: true);

        string text = await RenderText(Json(new LufeData { VentasComprasMensual = serie }), BcraWithFlatCpi(), breakdown);
        string packed = Squash(text);

        Assert.Contains("Variaciónusadaporelscore:caída20,0%", packed);
        Assert.DoesNotContain("Ventana:", packed);
        Assert.DoesNotContain("delamismaventana", packed);
        Assert.Contains("$13.200.000", packed);
    }

    // ── Degradation: no data renders nothing ──────────────────────────────────

    public static TheoryData<string?> UnusableSnapshots() => new()
    {
        null,
        "",
        "   ",
        "{ not json",
        JsonSerializer.Serialize(new LufeData()),                                       // no series
        JsonSerializer.Serialize(new LufeData { VentasComprasMensual = [] }),           // empty series
        JsonSerializer.Serialize(new LufeData                                           // no month with sales
        {
            VentasComprasMensual = [new LufeVentaCompraMes { Periodo = "202606", Compras = 5m }]
        })
    };

    [Theory]
    [MemberData(nameof(UnusableSnapshots))]
    public async Task UnusableSnapshot_RendersNoSection_AndDoesNotBreakTheExport(string? lufeDataJson)
    {
        string text = await RenderText(lufeDataJson, BcraWithFlatCpi(), sections: new[] { "risk", "lufe" });

        Assert.DoesNotContain(SectionMarker, text);
        Assert.DoesNotContain(MonthlyMarker, text);
        Assert.Contains("Riesgo", text);   // the neighbouring section still rendered
    }

    // ── The score's own variation, read from the breakdown ────────────────────

    [Fact]
    public async Task ScoringLine_MatchesTheBreakdownAndTheRiskRow_IncludingInteranual3m()
    {
        string breakdown = Breakdown(8.00m, -0.20m, "interanual_3m", esReal: true);

        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), BcraWithFlatCpi(), breakdown,
            new[] { "risk", "lufe" });
        string packed = Squash(text);

        // The same clause in both sections — one builder, so they cannot drift.
        Assert.Equal(2, Count(packed, "20,0%(interanual3meses,real"));
        // The same 1000-scale delta in both sections too — and in the first page's "Puntos de atención":
        // 8.0 internal → −80, the engine's own figure (850 − 80 = 770 = toNosis(23)).
        Assert.Equal(3, Count(packed, "\u221280pts"));
        Assert.DoesNotContain("+8,0", packed);
        Assert.Contains("usadaporelscore", Squash(text));

        // The window the score compared, and the nominal figure of that SAME window beside the real one —
        // ScoringService's own arithmetic: 3.300.000 against 3.000.000 → +10,0 %. The score's −20,0 % is
        // printed as recorded, never re-derived.
        Assert.Contains("Ventana:04–06/2026vs.04–06/2025(interanual,3meses)", packed);
        Assert.Contains("Variaciónnominaldelamismaventana:+10,0%", packed);
    }

    [Fact]
    public async Task ScoringLine_OnGrowth_SaysSubaAndSinPenalidad()
    {
        string breakdown = Breakdown(0m, 0.05m, "interanual", esReal: false);

        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), BcraWithFlatCpi(), breakdown);

        Assert.Contains("suba5,0%(interanual,nominal", Squash(text));
        Assert.Contains("sinpenalidad", Squash(text));
    }

    [Fact]
    public async Task ScoringLine_WhenTheBreakdownPredatesTheSignal_SaysItWasNotEvaluated()
    {
        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), BcraWithFlatCpi(),
            "{\"scoreRatios\": 21.00, \"scoreBcra\": 0.00, \"scoreMacro\": 30.00}");

        Assert.Contains("noevalu", Squash(text));
        Assert.DoesNotContain("usadaporelscore", Squash(text));
    }

    [Fact]
    public async Task RiskRow_IsUnchangedByTheClauseExtraction()
    {
        // The risk row alone, as VentasAdjReportingTests renders it — the exact §7.3b wording.
        string breakdown = Breakdown(8.00m, -0.20m, "interanual_3m", esReal: true);

        string text = await RenderText(lufeDataJson: null, scoreBreakdownJson: breakdown, sections: new[] { "risk" });

        Assert.Contains("20,0%(interanual3meses,real", Squash(text));
        Assert.Contains("\u221280pts", Squash(text));
        Assert.DoesNotContain("usadaporelscore", Squash(text));
    }

    // ── Annual indicators ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnnualIndicators_RenderMostRecentFirst_AndAZeroPurchaseIsNotAFigure()
    {
        var lufe = new LufeData
        {
            VentasComprasMensual  = FullSeries(),
            IndicadoresHistoricos =
            [
                new LufeIndicador { Periodo = 202300m, VentasPb = 50_000_000m, ComprasPb = 0m,          VtasMensualesProm = 4_166_667m },
                new LufeIndicador { Periodo = 202400m, VentasPb = 60_000_000m, ComprasPb = 20_000_000m, VtasMensualesProm = 5_000_000m },
                new LufeIndicador { Periodo = 202200m, Rentabilidad = 0.1m }   // no sales figures → no row
            ]
        };

        string text = await RenderText(Json(lufe), BcraWithFlatCpi());

        int annual = text.IndexOf(AnnualMarker, StringComparison.Ordinal);
        Assert.True(annual >= 0);
        string annualText = text[annual..];
        string packed     = Squash(annualText);

        int y2024 = annualText.IndexOf("2024", StringComparison.Ordinal);
        int y2023 = annualText.IndexOf("2023", StringComparison.Ordinal);
        Assert.True(y2024 >= 0 && y2023 > y2024, "Annual rows must run from the most recent balance back.");
        Assert.DoesNotContain("2022", annualText);

        Assert.Contains("$60.000.000", packed);
        Assert.Contains("$20.000.000", packed);
        Assert.Contains("$4.166.667", packed);
        Assert.False(HasZeroPesos(packed), "LUFE's 0 purchases on an annual row is 'no figure', never $ 0.");
    }

    [Fact]
    public async Task WithoutAnnualIndicators_TheAnnualTableIsOmitted()
    {
        string text = await RenderText(
            Json(new LufeData { VentasComprasMensual = FullSeries() }), BcraWithFlatCpi());

        Assert.Contains(MonthlyMarker, text);
        Assert.DoesNotContain(AnnualMarker, text);
    }

    // ── Identity never reaches the section ────────────────────────────────────

    [Fact]
    public async Task EntityIdentity_IsNeverPrinted()
    {
        var lufe = new LufeData
        {
            Entidad              = new LufeEntidad { Cuit = 30712345678, Nombre = "ACME SRL", DomicilioFiscal = "Calle Falsa 123" },
            Autoridades          = [new LufeAutoridad { Cuit = 20111111112, Denominacion = "PEREZ JUAN" }],
            VentasComprasMensual = FullSeries()
        };

        string text = await RenderText(Json(lufe), BcraWithFlatCpi());

        Assert.Contains(SectionMarker, text);
        Assert.DoesNotContain("ACME", text);
        Assert.DoesNotContain("30712345678", Squash(text));
        Assert.DoesNotContain("PEREZ", text);
        Assert.DoesNotContain("Falsa", text);
    }
}
