// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// How a line is printed: its dominant point size (in 0.5 pt steps), whether it is bold, and its fill
/// colour ("#rrggbb", or null when unknown). Two lines with equal styles look alike on the page — which
/// is how a heading the parser has never seen is told from body text.
/// </summary>
internal sealed record NosisReportLineStyle(double Size, bool Bold, string? Color)
{
    /// <summary>The style of a line built without letters (tests): unknown, matches nothing but itself.</summary>
    public static readonly NosisReportLineStyle Unknown = new(0d, false, null);

    /// <summary>"[9.0 B #1f4e79]" — the harness prints this; it carries no content.</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{Size:0.0} {(Bold ? "B" : "n")} {Color ?? "-"}]");
}
