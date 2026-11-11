// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Score credibility 1b-3, PDF half: the "Confiabilidad del score" box under the header of EVERY balance
/// report (whatever sections were chosen), the "Sin score" gauge, the three reasons a ratio-breakdown row has
/// no score, and the guarantee that a failing evaluation never fails the export. The persona report is
/// untouched.
/// </summary>
public sealed class PdfExportScoreReliabilityTests
{
    private const string Title = "Confiabilidad del score";

    private static Analysis Balance(int fiscalYear = 2025) => new() { FiscalYear = fiscalYear, AnalysisKind = AnalysisKinds.Balance };

    private static AnalysisResult Result(
        decimal? score = 42.5m, string? breakdown = null, string enrichment = "Complete", string? ratiosJson = null) => new()
    {
        ResultId           = new Guid("33333333-3333-3333-3333-333333333333"),
        AnalysisId         = new Guid("44444444-4444-4444-4444-444444444444"),
        AiSuggestedScore   = score,
        AiSuggestedLevel   = score is null ? null : "Medium",
        EnrichmentStatus   = enrichment,
        GeneratedAt        = ScoreDate,
        ScoreBreakdownJson = breakdown ?? Json(CleanScoring(), CleanFacts()),
        RatiosJson         = ratiosJson,
    };

    private static async Task<string> RenderAsync(
        AnalysisResult result, Analysis? analysis = null, PdfExportService? service = null, params string[] sections)
    {
        byte[] pdf = await (service ?? PdfExportServiceRenderTests.NewService())
            .GenerateAsync(result, analysis ?? Balance(), "tenant-x", sections, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc    = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    // Text extraction may break a long line anywhere; compare with whitespace removed.
    private static string Squash(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static void AssertHas(string text, string expected) =>
        Assert.Contains(Squash(expected), Squash(text), StringComparison.Ordinal);

    // ── The box, on every balance report ──────────────────────────────────────

    [Theory]
    [InlineData("balance")]
    [InlineData("footer")]
    [InlineData("")]   // no section chosen: the default full report
    public async Task The_box_renders_after_the_first_page_whatever_the_sections(string sectionsCsv)
    {
        string[] sections = sectionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var breakdown = Json(CleanScoring(), CleanFacts() with { MacroImputed = true });

        string text = await RenderAsync(Result(breakdown: breakdown), sections: sections);

        // The Portal's words (lib/score-reliability.ts): with_caveats → "Con notas".
        AssertHas(text, Title + ": Con notas");
        AssertHas(text, ScoreReliabilityMessages.MacroImputed);
        // The first page carries the verdict's label; the full box follows it — inside the risk section
        // when that section is rendered, right after the first page otherwise.
        int box = Squash(text).IndexOf(Squash(Title), StringComparison.Ordinal);
        Assert.True(box > Squash(text).IndexOf("Puntosdeatención", StringComparison.Ordinal));
        int risk = Squash(text).IndexOf("Análisisderiesgo", StringComparison.Ordinal);
        Assert.True(risk < 0 || box > risk);
    }

    [Fact]
    public async Task A_provisional_box_lists_every_reason_message_including_the_sector_text()
    {
        // The report is the analyst's own document: the full #301 messages belong here (not in a prompt).
        var breakdown = Json(CleanScoring(), CleanFacts() with
        {
            Integrity      = CleanFacts().Integrity! with { Passed = false, FailedChecks = 2 },
            SectorFallback = true,
            SectorRaw      = "Fabricación de autopartes",
        });

        string text = await RenderAsync(Result(breakdown: breakdown), sections: "balance");

        AssertHas(text, Title + ": Score provisorio");
        AssertHas(text, "• " + ScoreReliabilityMessages.IntegrityFailed(2));
        AssertHas(text, "• " + ScoreReliabilityMessages.SectorFallback("Fabricación de autopartes"));
    }

    [Fact]
    public async Task A_net_profit_only_failure_is_a_caveat_in_the_box_not_figures_that_do_not_add_up()
    {
        // Policy 5: the same verdict the prompt block restates (IntegrityPresentationTests), in its #301 message.
        var breakdown = Json(FlooredScoring("roe"), CleanFacts() with
        {
            Integrity = CleanFacts().Integrity! with
            {
                Passed           = false,
                FailedChecks     = 1,
                FailedCheckNames = [BalanceIntegrityService.NetProfitReconciliationCheck],
                NetProfitSignFlip = false,
                AffectedRatios   = ["roe", "roa", "margen_neto"],
            },
        });

        string text = await RenderAsync(Result(score: 60m, breakdown: breakdown), sections: "balance");

        AssertHas(text, Title + ": Con notas");
        AssertHas(text, "• " + ScoreReliabilityMessages.IntegrityPresentation);
        Assert.DoesNotContain(Squash(ScoreReliabilityMessages.IntegrityFailed(1)), Squash(text), StringComparison.Ordinal);
        Assert.DoesNotContain(Squash("piso crítico"), Squash(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_complete_verdict_is_one_short_line_not_an_empty_box()
    {
        string text = await RenderAsync(Result(), sections: "balance");

        AssertHas(text, Title + ": Datos completos");
        AssertHas(text, "Datos completos y verificados.");
    }

    [Fact]
    public async Task Interest_coverage_unread_under_RT6_leaves_the_box_complete()
    {
        string text = await RenderAsync(
            Result(breakdown: Json(InterestCoverageUnreadScoring(), CleanFacts())), sections: "balance");

        AssertHas(text, Title + ": Datos completos");
        Assert.DoesNotContain(Squash("No se pudo medir Cobertura de intereses"), Squash(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_without_a_score_says_sin_score_in_the_box_and_in_the_gauge()
    {
        string text = await RenderAsync(Result(score: null), sections: "risk");

        AssertHas(text, Title + ": Sin score");
        // Once in the box, once in the risk section where the gauge would be.
        // On the first page's score card (no gauge, no KPI tiles), in the box, and in the risk section where
        // the anatomy would be.
        Assert.Equal(3, CountOf(Squash(text), Squash(ScoreReliabilityMessages.ScoreUnavailable)));
        string squashed = Squash(text);
        int risk = squashed.IndexOf("Análisisderiesgo", StringComparison.Ordinal);
        Assert.True(risk >= 0 && squashed.IndexOf("Sinscore", risk, StringComparison.Ordinal) > risk,
            "the risk section must say 'Sin score' instead of drawing a gauge");
        Assert.DoesNotContain("/1000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Liquidezcorriente", squashed[..risk], StringComparison.Ordinal);
    }

    // ── A failing evaluation never fails the export ───────────────────────────

    [Fact]
    public async Task A_failing_evaluator_is_logged_and_the_export_still_succeeds()
    {
        var logger  = new ListLogger();
        var service = new PdfExportService(
            new CashFlowNarrativeService(), new CashFlowReconciliationService(), new AlertRatioReconciliationService(), logger)
        {
            ReliabilityEvaluator = (_, _) => throw new InvalidOperationException("boom"),
        };

        string text = await RenderAsync(Result(), service: service, sections: ["risk", "balance"]);

        AssertHas(text, "No se pudo determinar la confiabilidad del score.");
        AssertHas(text, "Análisis de riesgo");                     // the rest of the report rendered
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
        Assert.Contains("44444444-4444-4444-4444-444444444444", warning.Message, StringComparison.Ordinal);
    }

    // ── Ratio breakdown: why a row has no score ───────────────────────────────

    [Fact]
    public async Task The_breakdown_says_why_a_ratio_has_no_score()
    {
        var scoring = CleanScoring();
        scoring.Breakdown["liquidez_corriente"]  = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.Unread);
        scoring.Breakdown["leverage"]            = new RatioBreakdownEntry(50m, "Atencion", true, 0m, RatioExclusionReasons.NotMeaningful);
        scoring.Breakdown["margen_neto"]         = new RatioBreakdownEntry(50m, "Atencion", false, 0.07m, null, Measured: false);
        scoring.Breakdown["cobertura_intereses"] = new RatioBreakdownEntry(50m, "Atencion", true, 0m);   // cause unknown

        // No RatiosJson: margen_neto would otherwise read as computable and print its neutral 500.
        string text = await RenderAsync(Result(breakdown: Json(scoring, CleanFacts())), sections: "ratio_breakdown");

        // Extraction does not keep a status label beside its own row (it is vertically centred), so the
        // score cell is checked per row and each label once.
        string table = Squash(text[text.IndexOf("Ratios financieros", StringComparison.Ordinal)..]);
        foreach (string row in new[] { "Liquidezcorriente—", "Apalancamiento—", "Margenneto—", "Coberturadeintereses—" })
            Assert.Contains(row, table, StringComparison.Ordinal);
        foreach (string label in new[] { "Nosepudoleer", "Noaplica", "Puntajeneutro", "Nodeterminable" })
            Assert.Equal(1, CountOf(table, label));
        Assert.DoesNotContain("500", table, StringComparison.Ordinal);   // no placeholder printed as a score
        Assert.Contains("ROA750", table, StringComparison.Ordinal);
    }

    // ── Persona humana: untouched ─────────────────────────────────────────────

    [Fact]
    public async Task The_persona_report_carries_no_reliability_box()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        byte[] pdf = await PdfExportServiceRenderTests.NewService()
            .GeneratePersonAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation), [], CancellationToken.None);

        Assert.DoesNotContain(Title, PersonPdfFixtures.Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_persona_analysis_routed_to_the_balance_report_gets_no_box_either()
    {
        var persona = new Analysis { FiscalYear = 2025, AnalysisKind = AnalysisKinds.PersonaHumana };

        string text = await RenderAsync(Result(), persona, sections: "balance");

        Assert.DoesNotContain(Squash(Title), Squash(text), StringComparison.Ordinal);
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private sealed class ListLogger : ILogger<PdfExportService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
