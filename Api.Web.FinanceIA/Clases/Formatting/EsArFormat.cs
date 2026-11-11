// Project : Api.Web.FinanceIA
// Created : 2026-11-01
// Modified: 2026-11-01

using System.Globalization;

namespace Api.Web.FinanceIA.Clases.Formatting;

/// <summary>
/// es-AR rendering shared by the two Claude prompts that print scoring weights: the scoring narrative
/// (<c>ClaudeService.BuildNarrativePrompt</c>) and the chat score breakdown
/// (<c>AnalysisController.FormatScoreBreakdownBlock</c>). One helper, so both prompts render a weight
/// identically on every host.
/// </summary>
/// <remarks>
/// Deliberately small: other files keep their own <c>es-AR</c> culture field. Only the sites that
/// must agree byte for byte across prompts and operating systems read from here.
/// </remarks>
internal static class EsArFormat
{
    /// <summary>The es-AR culture (comma decimal, point thousands).</summary>
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// A 0..1 weight as a whole percentage with no space before the sign ("35%"). Not "P0": its es-AR
    /// pattern comes from the host's culture data and renders "35 %" on Windows but "35%" on Linux,
    /// and under the invariant culture "35 %", so a prompt built with it would differ by host.
    /// </summary>
    /// <param name="weight">The weight as a ratio, e.g. 0.35m.</param>
    /// <returns>The weight as a whole es-AR percentage, e.g. "35%".</returns>
    public static string Percent(decimal weight) => (weight * 100m).ToString("F0", Culture) + "%";
}
