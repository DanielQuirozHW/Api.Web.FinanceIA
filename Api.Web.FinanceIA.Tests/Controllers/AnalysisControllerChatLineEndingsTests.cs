// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using System.Globalization;
using Api.Web.FinanceIA.Clases.Formatting;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Pins the CHAT prompt as host-independent, so it is the same on the Windows App Service as on the
/// ubuntu CI that tests it.
/// <list type="bullet">
/// <item>Line endings: everything sent to Claude uses "\n" only. <c>AppendLine</c> writes
/// <c>Environment.NewLine</c>, which is CRLF on Windows.</item>
/// <item>Culture: scoring weights render as <c>50%</c> under any current culture. "P0" depends on
/// the host's culture data: "50 %" on Windows es-AR and under the invariant culture.</item>
/// </list>
/// Every line-ending test feeds an input that already carries a CR, so it fails on Linux too if the
/// normalisation is removed; asserting "no \r" on Linux-built text alone would prove nothing.
/// </summary>
public sealed class AnalysisControllerChatLineEndingsTests
{
    // ── Line endings ──────────────────────────────────────────────────────────

    [Fact]
    public void The_chat_system_prompt_has_no_carriage_return_even_when_its_inputs_carry_one()
    {
        var result = new AnalysisResult
        {
            ExecutiveSummary   = "Primera línea\r\nSegunda línea",
            ScoringNarrative   = "Justificación\rcon CR suelto",
            ScoreBreakdownJson = """{ "scoreRatios": 50.00, "sectorProfile": "Comercio\r\nminorista" }"""
        };

        var prompt = AnalysisController.BuildChatSystemPrompt(
            result, ["Oración uno.\r\nOración dos."], hasAttachment: true, reliability: null, fiscalYear: null);

        Assert.DoesNotContain('\r', prompt);
        Assert.Contains("Primera línea\nSegunda línea", prompt, StringComparison.Ordinal);
        Assert.Contains("Justificación\ncon CR suelto", prompt, StringComparison.Ordinal);
        Assert.Contains("Oración uno.\nOración dos.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_score_breakdown_block_has_no_carriage_return_even_when_the_blob_carries_one()
    {
        // JSON "\r\n" escape: the deserialised SectorProfile holds a real CR LF pair.
        const string blob = """{ "scoreRatios": 50.00, "sectorProfile": "Comercio\r\nminorista" }""";

        var block = AnalysisController.FormatScoreBreakdownBlock(blob)
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.DoesNotContain('\r', block);
        Assert.Contains("Perfil sectorial: Comercio\nminorista.", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bcra_summary_joins_its_elision_note_with_a_bare_line_feed()
    {
        // A passthrough field carries a CR LF: JSON re-serialisation escapes it, so the prompt still
        // holds no raw CR, and the note follows the JSON after a single "\n".
        const string blob = """
            { "cheques": { "totalRechazados": 1, "observacion": "linea\r\nsiguiente",
                           "detalle": [ { "monto": 1000 } ] } }
            """;

        var summary = AnalysisController.BuildBcraChatSummary(blob);

        Assert.DoesNotContain('\r', summary);
        var parts = summary.Split('\n');
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("{", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("(Del bloque de cheques rechazados", parts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_capped_attachment_gets_its_truncation_note_after_two_bare_line_feeds()
    {
        var markdown = string.Concat(Enumerable.Repeat("| a | b |\r\n", 30_000));

        var (capped, truncated) = AnalysisController.CapAttachmentMarkdown(markdown);

        Assert.True(truncated);
        Assert.NotNull(capped);
        Assert.Contains("\n\n> **Nota:** el adjunto se truncó", capped, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n\r\n> **Nota:**", capped, StringComparison.Ordinal);
    }

    // ── Culture ───────────────────────────────────────────────────────────────

    public static TheoryData<string> Cultures => new() { "en-US", "es-AR", "" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Weights_render_without_a_space_before_the_percent_sign_under_any_culture(string culture)
    {
        const string blob = """
            { "scoreRatios": 40.00, "scoreBcra": 50.00, "scoreMacro": 30.00,
              "effectiveWeightRatios": 0.5, "effectiveWeightBcra": 0.35, "effectiveWeightMacro": 0.15 }
            """;

        var block = UnderCulture(culture, () => AnalysisController.FormatScoreBreakdownBlock(blob))
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains("(peso 50%)", block, StringComparison.Ordinal);
        Assert.Contains("(peso 35%)", block, StringComparison.Ordinal);
        Assert.Contains("(peso 15%).", block, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Effective_weights_render_without_a_space_when_bcra_is_excluded(string culture)
    {
        const string blob = """
            { "scoreRatios": 40.00, "scoreMacro": 30.00, "bcraExcluded": true,
              "effectiveWeightRatios": 0.5, "effectiveWeightMacro": 0.5 }
            """;

        var block = UnderCulture(culture, () => AnalysisController.FormatScoreBreakdownBlock(blob))
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains("(peso efectivo 50%), ", block, StringComparison.Ordinal);
        Assert.Contains("(peso efectivo 50%). ", block, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void The_bcra_trend_adjustment_is_invariant_like_its_neighbours(string culture)
    {
        const string blob = """{ "bcraTrendDelta": 1, "bcraTrendAdj": 2.5 }""";

        var block = UnderCulture(culture, () => AnalysisController.FormatScoreBreakdownBlock(blob))
            ?? throw new Xunit.Sdk.XunitException("the breakdown block must render");

        Assert.Contains("ajuste aplicado: 2.5 puntos.", block, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void EsArFormat_Percent_is_the_same_under_any_culture(string culture)
    {
        Assert.Equal("35%", UnderCulture(culture, () => EsArFormat.Percent(0.35m)));
        Assert.Equal("100%", UnderCulture(culture, () => EsArFormat.Percent(1m)));
        Assert.Equal("0%", UnderCulture(culture, () => EsArFormat.Percent(0m)));
    }

    /// <summary>
    /// Runs <paramref name="act"/> with CurrentCulture and CurrentUICulture set to
    /// <paramref name="culture"/> ("" = invariant), restoring both afterwards. Both are per async
    /// flow, so parallel test classes do not see the change.
    /// </summary>
    private static T UnderCulture<T>(string culture, Func<T> act)
    {
        var previous   = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture   = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture   = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
