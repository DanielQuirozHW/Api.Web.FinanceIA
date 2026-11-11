// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Net;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Score credibility 1b-3, narrative half: <see cref="ClaudeService.BuildNarrativePrompt"/> states the data
/// quality behind the score (the shared CALIDAD block and its first-paragraph rule), the adjustments the score
/// carries, and the low-coverage ratios it did not measure — and a clean, unadjusted score keeps its prompt
/// byte for byte (the registered fingerprint in <see cref="NarrativePromptFingerprintTests"/> is untouched).
/// </summary>
public sealed class ScoringNarrativeReliabilityTests
{
    private const string QualityRuleStart = "  • La sección CALIDAD DE LOS DATOS indica que el score";

    // ── Inputs mirroring the fingerprint fixtures (that file stays untouched) ──────────────────

    private static ScoringResult Scoring(
        Dictionary<string, RatioBreakdownEntry>? breakdown = null,
        bool bcraExcluded = false, List<string>? criticalRatios = null,
        decimal appliedFloor = 0m, decimal scoreBeforeFloor = 40m, decimal score = 40m) => new()
    {
        Score                 = score,
        ScoreBeforeFloor      = scoreBeforeFloor,
        AppliedFloor          = appliedFloor,
        CriticalFloorTrigger  = 100m,
        Level                 = "Critical",
        ScoreRatios           = 24m,
        ScoreBcra             = 50m,
        ScoreMacro            = 50m,
        CriticalRatios        = criticalRatios ?? [],
        Breakdown             = breakdown ?? new Dictionary<string, RatioBreakdownEntry>
        {
            ["liquidez_corriente"] = new(80m, "Saludable"),
            ["leverage"]           = new(55m, "Atencion"),
            ["margen_neto"]        = new(72m, "Saludable"),
        },
        BcraExcluded          = bcraExcluded,
        EffectiveWeightRatios = bcraExcluded ? 0.60m : 0.50m,
        EffectiveWeightBcra   = bcraExcluded ? 0m    : 0.35m,
        EffectiveWeightMacro  = bcraExcluded ? 0.40m : 0.15m,
    };

    private static readonly RatiosData Ratios = new() { CurrentRatio = 1.5, Leverage = 1.8, NetMargin = 11.2 };

    public static TheoryData<string> FixtureNames() =>
        ["healthy", "bcra_excluded", "excluded_ratio", "floor", "bcra_and_macro", "all_null"];

    private static (ScoringResult Scoring, RatiosData Ratios, BcraData? Bcra, BcraMacroData? Macro) Fixture(string name) => name switch
    {
        "healthy"        => (Scoring(), Ratios, null, null),
        "bcra_excluded"  => (Scoring(bcraExcluded: true), Ratios, null, null),
        "excluded_ratio" => (Scoring(new Dictionary<string, RatioBreakdownEntry>
                             {
                                 ["cobertura_intereses"] = new(50m, "Atencion", Excluded: true, EffectiveWeight: 0m),
                                 ["leverage"]            = new(72m, "Atencion"),
                             }), new RatiosData { Leverage = 1.95 }, null, null),
        "floor"          => (Scoring(new Dictionary<string, RatioBreakdownEntry> { ["leverage"] = new(85m, "Critico") },
                                     criticalRatios: ["leverage"], appliedFloor: 65m, scoreBeforeFloor: 38m, score: 65m),
                             new RatiosData { Leverage = 2.67 }, null, null),
        "bcra_and_macro" => (Scoring(), Ratios,
                             new BcraData { Deuda = new BcraDeudaData { SituacionMaxima = 2, MontoTotal = 1500m } },
                             new BcraMacroData { InflacionMensual = 4.2, TasaReferencia = 38.5 }),
        _                => (Scoring(), new RatiosData(), null, null),
    };

    private static ScoreReliabilityResponse CompleteVerdict()
    {
        var verdict = Evaluate(Row(), Json(CleanScoring(), CleanFacts()))!;
        Assert.Equal(ScoreReliabilityLevels.Complete, verdict.Level);
        return verdict;
    }

    private static ScoreReliabilityResponse ProvisionalVerdict()
        => Evaluate(Row(), Json(CleanScoring(), CleanFacts() with
           {
               Integrity  = CleanFacts().Integrity! with { Passed = false, FailedChecks = 2 },
               MacroImputed = true,
           }))!;

    private static string Prompt(ScoringResult scoring, ScoreReliabilityResponse? reliability, RatiosData? ratios = null)
        => ClaudeService.BuildNarrativePrompt(scoring, ratios ?? Ratios, null, null, reliability);

    // ── Happy path: byte-identical ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void A_complete_verdict_leaves_the_prompt_byte_identical(string fixture)
    {
        var (scoring, ratios, bcra, macro) = Fixture(fixture);

        string before = ClaudeService.BuildNarrativePrompt(scoring, ratios, bcra, macro);
        string after  = ClaudeService.BuildNarrativePrompt(scoring, ratios, bcra, macro, CompleteVerdict());

        Assert.Equal(before, after);
        Assert.DoesNotContain("CALIDAD DE LOS DATOS", after, StringComparison.Ordinal);
        Assert.DoesNotContain("AJUSTES AL SCORE", after, StringComparison.Ordinal);
    }

    // ── The CALIDAD block and its rule ─────────────────────────────────────────

    [Fact]
    public void A_provisional_score_gets_the_block_and_the_first_paragraph_rule()
    {
        var verdict = ProvisionalVerdict();
        Assert.Equal(ScoreReliabilityLevels.Provisional, verdict.Level);

        string prompt = Prompt(Scoring(), verdict);
        string block  = ScoreReliabilityPromptBlock.Build(verdict)!;

        Assert.Contains("\n" + block + "\n", prompt, StringComparison.Ordinal);
        Assert.Contains("Confiabilidad del score: provisoria", prompt, StringComparison.Ordinal);
        Assert.Contains("no superó 2 controles de consistencia", prompt, StringComparison.Ordinal);
        Assert.Contains(
            QualityRuleStart + " es provisorio. En el PRIMER párrafo indica que el score es provisorio, menciona las " +
            "principales observaciones de esa sección y no presentes el score como definitivo.",
            prompt, StringComparison.Ordinal);

        // The rule sits among the REGLAS; the block sits after the score and before the ratio breakdown.
        Assert.True(prompt.IndexOf(QualityRuleStart, StringComparison.Ordinal) < prompt.IndexOf("IMPORTANTE:", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("Score final:", StringComparison.Ordinal) < prompt.IndexOf(block, StringComparison.Ordinal));
        Assert.True(prompt.IndexOf(block, StringComparison.Ordinal) < prompt.IndexOf("Desglose de ratios financieros:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_score_with_caveats_says_it_has_observations()
    {
        var verdict = Evaluate(Row(), Json(CleanScoring(), CleanFacts() with { MacroImputed = true }))!;
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict.Level);

        string prompt = Prompt(Scoring(), verdict);

        Assert.Contains(QualityRuleStart + " tiene observaciones sobre la calidad de los datos.", prompt, StringComparison.Ordinal);
        Assert.Contains("Confiabilidad del score: con observaciones", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_identifying_sector_never_reaches_the_narrative_prompt()
    {
        var verdict = Evaluate(Row(), Json(CleanScoring(), CleanFacts() with
        {
            SectorFallback = true,
            SectorRaw      = ScoreReliabilityPromptBlockTests.IdentifyingSector,
        }))!;
        Assert.Contains("ACME", Assert.Single(verdict.Reasons).Message, StringComparison.Ordinal);

        string prompt = Prompt(Scoring(), verdict);

        Assert.Contains("perfil General", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("30-71224873-0", prompt, StringComparison.Ordinal);
    }

    // ── AJUSTES AL SCORE ──────────────────────────────────────────────────────

    [Fact]
    public void The_adjustments_block_lists_only_the_non_zero_adjustments_on_the_1000_scale()
    {
        var scoring = Scoring();
        scoring.BcraTrendAdj  = -5.00m;   // improvement → +50 on the /1000 scale
        scoring.ChequesAdj    = 0m;       // not carried → not listed
        scoring.VentasAdj     = 10.00m;   // penalty → −100
        scoring.CasfogMoraAdj = 1.71m;    // penalty → −17

        string prompt = Prompt(scoring, reliability: null);

        Assert.Contains(
            "AJUSTES AL SCORE (puntos aplicados, escala /1000):\n" +
            "  • Tendencia de la situación BCRA: +50 puntos, ya incluidos en el score del componente BCRA.\n" +
            "  • Caída de ventas declaradas (LUFE): −100 puntos sobre el score final.\n" +
            "  • Garantías SGR en mora (CASFOG): −17 puntos sobre el score final.\n" +
            "  El score final resulta de ponderar los scores por componente (que ya incluyen los ajustes del " +
            "componente BCRA), aplicar el piso por ratio crítico si corresponde y luego los ajustes sobre el " +
            "score final.",
            prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Cheques rechazados", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cheques_adjustment_is_listed_inside_the_bcra_component()
    {
        var scoring = Scoring();
        scoring.ChequesAdj = 6.00m;

        Assert.Contains("  • Cheques rechazados: −60 puntos, ya incluidos en el score del componente BCRA.",
                        Prompt(scoring, reliability: null), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unadjusted_score_has_no_adjustments_block()
        => Assert.DoesNotContain("AJUSTES AL SCORE", Prompt(Scoring(), reliability: null), StringComparison.Ordinal);

    // ── measured:false ────────────────────────────────────────────────────────

    [Fact]
    public void A_low_coverage_ratio_reads_no_medido_with_its_own_rule()
    {
        var scoring = Scoring(new Dictionary<string, RatioBreakdownEntry>
        {
            ["roa"]         = new(0m, "Saludable"),
            ["margen_neto"] = new(50m, "Atencion", Excluded: false, EffectiveWeight: 0.07m, Measured: false),
            ["leverage"]    = new(50m, "Atencion", Excluded: false, EffectiveWeight: 0.10m, Measured: false),
        });

        string prompt = Prompt(scoring, reliability: null);

        Assert.Contains("  Margen neto: no medido (no pudo calcularse; se puntuó con un valor neutro)", prompt, StringComparison.Ordinal);
        Assert.Contains("  Apalancamiento: no medido (no pudo calcularse; se puntuó con un valor neutro)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Margen neto: Atencion", prompt, StringComparison.Ordinal);
        Assert.Contains("  ROA: Saludable", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "  • Los siguientes indicadores no pudieron calcularse y se puntuaron con un valor neutro por cobertura " +
            "insuficiente de ratios: Margen neto, Apalancamiento. Descríbelos como NO MEDIDOS",
            prompt, StringComparison.Ordinal);
        // The excluded-ratio rule ("peso 0") is false for these and must not be emitted for them.
        Assert.DoesNotContain("quedaron excluidos del score", prompt, StringComparison.Ordinal);
    }

    // ── The request actually carries it ───────────────────────────────────────

    [Fact]
    public async Task GenerateScoringNarrativeAsync_sends_the_prompt_built_with_the_verdict()
    {
        var handler = new CapturingHandler();
        var sut = new ClaudeService(
            new Factory(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "test-key" })
                .Build(),
            NullLogger<ClaudeService>.Instance,
            new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions()),
            NullScanOrientationDetector.Instance);

        var verdict = ProvisionalVerdict();
        var result  = await sut.GenerateScoringNarrativeAsync(Scoring(), Ratios, null, null, verdict, CancellationToken.None);

        Assert.Equal("Narrativa.", result.Text);
        using var request = JsonDocument.Parse(Assert.Single(handler.Captured));
        string sent = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Equal(ClaudeService.BuildNarrativePrompt(Scoring(), Ratios, null, null, verdict), sent);
        Assert.Contains("CALIDAD DE LOS DATOS", sent, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            string body = JsonSerializer.Serialize(new
            {
                content     = new[] { new { type = "text", text = "Narrativa." } },
                stop_reason = "end_turn",
                usage       = new { input_tokens = 10, output_tokens = 20 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
