// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Api.Web.FinanceIA.Tests.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// Everything that READS <c>BalanceJson.manual_corrections</c>: the one reader, the reliability facts, the
/// chat system prompt (count and date only — never the reason or the analyst) and the PDF balance section.
/// </summary>
public sealed class BalanceCorrectionReadSideTests
{
    private static readonly Guid Analyst = Guid.Parse("7a1c2b3d-0000-4000-8000-0000000000aa");
    private static readonly DateTime At  = new(2026, 6, 14, 15, 0, 0, DateTimeKind.Utc);

    // ── ManualCorrectionsReader ─────────────────────────────────────────────────

    [Fact]
    public void The_reader_counts_distinct_paths_and_takes_the_latest_entry()
    {
        var once  = Corrected(BalanceCorrectionFixtures.FinexCommand(value: 5d), At);
        var twice = BalanceCorrectionApplier.Apply(once, new BalanceCorrectionCommand("Segunda corrección del rubro.",
            [
                new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse(BalanceCorrectionFixtures.MisreadPath)!, 0d),
                new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse("prepaid_expenses")!, 10d),
            ]), Analyst, At.AddDays(1), new BalanceIntegrityService()).BalanceJson;

        var summary = ManualCorrectionsReader.Read(twice)!;

        Assert.Equal(2, summary.Count);   // two figures, three entries
        Assert.Equal(At.AddDays(1), summary.LastAt);
        Assert.Equal(Analyst, summary.LastBy);
        Assert.Equal("Segunda corrección del rubro.", summary.LastReason);
        Assert.Equal(new HashSet<string> { BalanceCorrectionFixtures.MisreadPath, "prepaid_expenses" }, summary.Paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"assets":1}""")]
    [InlineData("""{"manual_corrections":[]}""")]
    [InlineData("""{"manual_corrections":"x"}""")]
    [InlineData("""{"manual_corrections":[{"path":""},{"nopath":1}]}""")]
    public void The_reader_yields_null_when_nothing_was_corrected(string? balanceJson)
        => Assert.Null(ManualCorrectionsReader.Read(balanceJson));

    // ── Reliability facts ───────────────────────────────────────────────────────

    [Fact]
    public void The_facts_carry_the_count_and_the_last_date_of_the_corrections()
    {
        var facts = ScoreReliabilityFactsBuilder.Build(CleanScoring(), Inputs(Corrected(BalanceCorrectionFixtures.FinexCommand(), At)));

        Assert.Equal(1, facts.ManualCorrections);
        Assert.Equal(At, facts.ManualCorrectionsLastAt);
        Assert.Equal(ScoreReliabilityFacts.CurrentVersion, facts.Version);
        Assert.Equal(6, facts.Version);  // facts version 6 added netProfitColumnMix
        Assert.Contains("manual_correction", ScoreReliabilityFactsBuilder.CaveatCodes(CleanScoring(), facts));
    }

    [Fact]
    public void An_uncorrected_balance_omits_both_facts()
    {
        var facts = ScoreReliabilityFactsBuilder.Build(CleanScoring(), Inputs(BalanceCorrectionFixtures.FinexBalanceJson()));

        Assert.Null(facts.ManualCorrections);
        Assert.Null(facts.ManualCorrectionsLastAt);
        var json = System.Text.Json.JsonSerializer.Serialize(facts);
        Assert.DoesNotContain("manualCorrections", json);
        Assert.DoesNotContain("manual_correction", ScoreReliabilityFactsBuilder.CaveatCodes(CleanScoring(), facts));
    }

    [Fact]
    public void Manual_correction_is_a_caveat_with_the_es_AR_message()
    {
        var verdict = Evaluate(Row(), Json(CleanScoring(), CleanFacts() with { ManualCorrections = 3, ManualCorrectionsLastAt = At }))!;

        var reason = Assert.Single(verdict.Reasons);
        Assert.Equal(ScoreReliabilityCodes.ManualCorrection, reason.Code);
        Assert.Equal(ScoreReliabilitySeverities.Caveat, reason.Severity);
        Assert.Equal(
            "Se corrigieron manualmente 3 cifras del balance después de la lectura automática (última corrección: 14/06/2026).",
            reason.Message);
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);
        Assert.Equal("6", verdict.PolicyVersion);  // policy 6: a column mix is not a presentation

        var one = Evaluate(Row(), Json(CleanScoring(), CleanFacts() with { ManualCorrections = 1, ManualCorrectionsLastAt = At }))!;
        Assert.Equal(
            "Se corrigieron manualmente 1 cifra del balance después de la lectura automática (última corrección: 14/06/2026).",
            Assert.Single(one.Reasons).Message);
    }

    // ── Chat ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_chat_prompt_states_the_correction_with_no_reason_and_no_analyst()
    {
        var balanceJson = Corrected(BalanceCorrectionFixtures.FinexCommand(), At);
        var breakdown   = Json(CleanScoring(), CleanFacts() with { ManualCorrections = 1, ManualCorrectionsLastAt = At });
        var verdict     = Evaluate(Row(), breakdown)!;
        var result      = new AnalysisResult
        {
            AiSuggestedScore   = 42.5m,
            AiSuggestedLevel   = "Medium",
            ScoringNarrative   = "Narrativa almacenada.",
            BalanceJson        = balanceJson,
            ScoreBreakdownJson = breakdown,
        };

        var prompt = AnalysisController.BuildChatSystemPrompt(result, [], hasAttachment: false, verdict, 2024);

        Assert.Contains("Un analista corrigió manualmente 1 cifra del balance después de la lectura automática (última corrección: 14/06/2026).", prompt);
        Assert.DoesNotContain(BalanceCorrectionFixtures.Reason, prompt);
        Assert.DoesNotContain(Analyst.ToString(), prompt);
        Assert.DoesNotContain("manual_corrections", prompt);
    }

    // ── PDF ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_pdf_marks_a_corrected_figure_and_states_the_count_and_the_reason()
    {
        var balanceJson = Corrected(BalanceCorrectionFixtures.Command("current_liabilities", 100_000_000d), At);

        var text = await RenderBalanceAsync(balanceJson);

        Assert.Contains(Squash("Pasivo corriente (corregido)"), text);
        Assert.DoesNotContain(Squash("Activo corriente (corregido)"), text);
        Assert.Contains(Squash("1 cifra corregida manualmente; motivo: " + BalanceCorrectionFixtures.Reason), text);
    }

    [Fact]
    public async Task An_uncorrected_balance_renders_as_before()
    {
        var text = await RenderBalanceAsync(BalanceCorrectionFixtures.FinexBalanceJson());

        Assert.DoesNotContain(Squash("(corregido)"), text);
        Assert.DoesNotContain(Squash("corregida manualmente"), text);
        Assert.DoesNotContain(Squash("corregidas manualmente"), text);
    }

    [Fact]
    public async Task A_corrected_figure_outside_the_table_is_stated_in_the_line_and_marks_no_row()
    {
        // result_before_tax has no row in the PDF balance table: the count line carries it.
        var balanceJson = Corrected(BalanceCorrectionFixtures.Command("result_before_tax", 91_000_000d), At);

        var text = await RenderBalanceAsync(balanceJson);

        Assert.DoesNotContain(Squash("(corregido)"), text);
        Assert.Contains(Squash("1 cifra corregida manualmente; motivo: " + BalanceCorrectionFixtures.Reason), text);
    }

    [Fact]
    public void The_reader_lists_the_net_profit_reconciliation_inputs_like_any_other_path()
    {
        // The one reader drives the detail's balanceCorrections.count, the reliability count and the PDF
        // marks; the Portal marks a figure from the same manual_corrections paths in BalanceJson.
        var balanceJson = BalanceCorrectionApplier.Apply(
            BalanceCorrectionFixtures.MisreadResultBeforeTaxJson(),
            new BalanceCorrectionCommand(BalanceCorrectionFixtures.Reason,
            [
                new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse("result_before_tax")!, BalanceCorrectionFixtures.ResultBeforeTax),
                new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse("income_tax")!, -30_000_001d),
            ]),
            Analyst, At, new BalanceIntegrityService()).BalanceJson;

        var summary = ManualCorrectionsReader.Read(balanceJson)!;

        Assert.Equal(2, summary.Count);
        Assert.Equal(new HashSet<string> { "result_before_tax", "income_tax" }, summary.Paths);
    }

    [Fact]
    public void The_pdf_line_counts_rubro_lines_too()
    {
        var summary = new ManualCorrectionsSummary(3, At, Analyst, "  motivo  ", new HashSet<string> { "a", "b", "c" }, []);

        Assert.Equal("3 cifras corregidas manualmente; motivo: motivo", PdfExportService.ManualCorrectionsLine(summary));
        Assert.Equal("1 cifra corregida manualmente.", PdfExportService.ManualCorrectionsLine(summary with { Count = 1, LastReason = null }));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string Corrected(BalanceCorrectionCommand command, DateTime at)
        => BalanceCorrectionApplier.Apply(BalanceCorrectionFixtures.FinexBalanceJson(), command, Analyst, at, new BalanceIntegrityService()).BalanceJson!;

    private static ScoreReliabilityInputs Inputs(string balanceJson)
        => new(RatiosComputed: true, BalanceJson: balanceJson, FiscalYear: 2024, BcraData: null, Lufe: null,
               LufeSnapshotAt: null, Casfog: null);

    private static async Task<string> RenderBalanceAsync(string balanceJson)
    {
        var result = new AnalysisResult
        {
            ResultId           = Guid.NewGuid(),
            AnalysisId         = Guid.NewGuid(),
            AiSuggestedScore   = 42.5m,
            AiSuggestedLevel   = "Medium",
            EnrichmentStatus   = "Complete",
            GeneratedAt        = ScoreDate,
            BalanceJson        = balanceJson,
            ScoreBreakdownJson = Json(CleanScoring(), CleanFacts()),
        };

        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            result, new Analysis { FiscalYear = 2024, AnalysisKind = AnalysisKinds.Balance }, "tenant-x", ["balance"],
            CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc    = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (var i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return Squash(sb.ToString());
    }

    private static string Squash(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
