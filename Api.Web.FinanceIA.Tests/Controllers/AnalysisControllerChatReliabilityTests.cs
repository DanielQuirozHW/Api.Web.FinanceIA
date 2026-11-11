// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Score credibility 1b-3, chat half: the system prompt carries the row's reliability verdict as the shared
/// fixed-phrase block right after DATOS DEL ANÁLISIS, with the rule that makes the model state the limitation,
/// the ratios a failed integrity check touched, and per-ratio lines that never present an unmeasured ratio as
/// a reading. A row without a verdict, or with a complete one, gets the prompt it always got.
/// </summary>
public sealed class AnalysisControllerChatReliabilityTests
{
    private const string Rule =
        "Si el analista pregunta por una cifra, un ratio o un componente afectado por algún punto de CALIDAD DE " +
        "LOS DATOS, mencioná esa limitación. No presentes como ciertas cifras de un balance cuya lectura no " +
        "superó los controles de consistencia.";

    private static readonly IReadOnlyList<string> NoReading = [];

    /// <summary>A balance whose integrity report failed one check touching two ratios.</summary>
    private const string FailedIntegrityBalance = """
        {"integrity":{"passed":false,
          "checks":[{"name":"assets_components","passed":true},{"name":"liabilities_components","passed":true},
                    {"name":"accounting_equation","passed":true},{"name":"current_assets_items","passed":false}],
          "affectedRatios":["prueba_acida","liquidez_corriente"]}}
        """;

    private static ScoreReliabilityResponse IntegrityFailedVerdict()
        => Evaluate(Row(), Json(CleanScoring(), CleanFacts() with
           {
               Integrity = CleanFacts().Integrity! with { Passed = false, FailedChecks = 1 },
           }))!;

    private static AnalysisResult Result(string? balanceJson = null) => new()
    {
        AiSuggestedScore   = 42.5m,
        AiSuggestedLevel   = "Medium",
        ScoringNarrative   = "Narrativa almacenada.",
        BalanceJson        = balanceJson,
        ScoreBreakdownJson = Json(CleanScoring(), CleanFacts()),
    };

    private static string Prompt(AnalysisResult result, ScoreReliabilityResponse? verdict, int? fiscalYear = 2025)
        => AnalysisController.BuildChatSystemPrompt(result, NoReading, hasAttachment: false, verdict, fiscalYear);

    // ── The block in the system prompt ────────────────────────────────────────

    [Fact]
    public void The_block_and_the_rule_sit_right_after_datos_del_analisis()
    {
        var verdict = IntegrityFailedVerdict();
        string prompt = Prompt(Result(), verdict);

        string block = ScoreReliabilityPromptBlock.Build(verdict)!;
        int datos    = prompt.IndexOf("- JUSTIFICACIÓN DEL SCORE: Narrativa almacenada.\n\n", StringComparison.Ordinal);
        int quality  = prompt.IndexOf(block, StringComparison.Ordinal);
        int desglose = prompt.IndexOf("DESGLOSE DEL SCORING:", StringComparison.Ordinal);

        Assert.True(datos >= 0 && quality > datos && desglose > quality, "DATOS → CALIDAD → DESGLOSE");
        Assert.Equal(datos + "- JUSTIFICACIÓN DEL SCORE: Narrativa almacenada.\n\n".Length, quality);
        Assert.Contains(Rule + "\n\nDESGLOSE DEL SCORING:", prompt, StringComparison.Ordinal);
        Assert.Contains("Confiabilidad del score: provisoria", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void No_verdict_or_a_complete_one_leaves_the_prompt_as_it_was()
    {
        var complete = Evaluate(Row(), Json(CleanScoring(), CleanFacts()))!;
        Assert.Equal(ScoreReliabilityLevels.Complete, complete.Level);

        string without = Prompt(Result(), verdict: null, fiscalYear: null);

        Assert.Equal(without, Prompt(Result(), complete));
        // The block HEADER, not the name: the CIFRAS DEL BALANCE rules refer to the block on every prompt.
        Assert.DoesNotContain(ScoreReliabilityPromptBlock.Header, without, StringComparison.Ordinal);
        Assert.DoesNotContain("mencioná esa limitación", without, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_integrity_lists_the_affected_ratios_by_label()
    {
        string prompt = Prompt(Result(FailedIntegrityBalance), IntegrityFailedVerdict());

        Assert.Contains(
            "  • Ratios calculados con cifras que no superaron los controles de consistencia: Prueba ácida, Liquidez corriente.\n" + Rule,
            prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("prueba_acida", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_not_the_balance_decides_whether_affected_ratios_are_listed()
    {
        // The same failed-integrity balance, but a verdict that does not report integrity_failed.
        var verdict = Evaluate(Row(), Json(CleanScoring(), CleanFacts() with { MacroImputed = true }))!;

        string prompt = Prompt(Result(FailedIntegrityBalance), verdict);

        Assert.Contains("CALIDAD DE LOS DATOS", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Ratios calculados con cifras", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_identifying_sector_never_reaches_the_chat_prompt()
    {
        var verdict = Evaluate(Row(), Json(CleanScoring(), CleanFacts() with
        {
            SectorFallback = true,
            SectorRaw      = ScoreReliabilityPromptBlockTests.IdentifyingSector,
        }))!;
        var result = Result();
        result.ScoreBreakdownJson = Json(CleanScoring(), CleanFacts() with
        {
            SectorFallback = true,
            SectorRaw      = ScoreReliabilityPromptBlockTests.IdentifyingSector,
        });

        string prompt = Prompt(result, verdict);

        Assert.Contains("perfil General", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("30-71224873-0", prompt, StringComparison.Ordinal);
    }

    // ── Per-ratio lines of DESGLOSE DEL SCORING ───────────────────────────────

    [Fact]
    public void Excluded_and_neutral_ratios_read_no_medido()
    {
        const string blob = """
            {"scoreRatios":40.00,"scoreBcra":30.00,"scoreMacro":30.00,
             "breakdown":{
               "liquidez_corriente":{"score":50,"status":"Atencion","excluded":true,"effectiveWeight":0,"exclusionReason":"unread"},
               "leverage":{"score":50,"status":"Atencion","excluded":true,"effectiveWeight":0,"exclusionReason":"not_meaningful"},
               "roe":{"score":50,"status":"Atencion","excluded":true,"effectiveWeight":0},
               "margen_neto":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.07,"measured":false},
               "roa":{"score":0,"status":"Saludable","excluded":false,"effectiveWeight":0.08}},
             "scoreBeforeFloor":38.00,"appliedFloor":0,"criticalRatios":[]}
            """;

        string block = AnalysisController.FormatScoreBreakdownBlock(blob, finalScore: 38m)!;

        Assert.Contains("  Liquidez corriente: no medido — no se pudo leer del balance\n", block, StringComparison.Ordinal);
        Assert.Contains("  Apalancamiento: no medido — no aplica con estas cifras\n", block, StringComparison.Ordinal);
        Assert.Contains("  ROE: no medido\n", block, StringComparison.Ordinal);
        Assert.Contains("  Margen neto: no medido — se puntuó con un valor neutro por cobertura insuficiente\n", block, StringComparison.Ordinal);
        Assert.Contains("  ROA: 1000/1000 — Saludable\n", block, StringComparison.Ordinal);
        Assert.DoesNotContain("500/1000 — Atencion", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_breakdown_without_the_new_keys_renders_as_before()
    {
        const string blob = """
            {"scoreRatios":21.00,"scoreBcra":51.00,"scoreMacro":30.00,
             "breakdown":{"leverage":{"score":18.00,"status":"Critico"},"endeudamiento":{"score":44.00,"status":"Atencion"}},
             "scoreBeforeFloor":20.00,"appliedFloor":0,"criticalRatios":[]}
            """;

        string block = AnalysisController.FormatScoreBreakdownBlock(blob, finalScore: 20m)!;

        Assert.Contains(
            "Scores parciales por ratio:\n  Apalancamiento: 820/1000 — Critico\n  Endeudamiento: 560/1000 — Atencion\n",
            block, StringComparison.Ordinal);
        Assert.DoesNotContain("no medido", block, StringComparison.Ordinal);
    }
}
