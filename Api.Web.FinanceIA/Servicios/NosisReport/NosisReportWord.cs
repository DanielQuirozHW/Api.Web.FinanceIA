// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>One word of a report line, with its horizontal extent in PDF points.</summary>
internal sealed record NosisReportWord(string Text, double Left, double Right)
{
    /// <summary>Horizontal centre — what a word is matched to a table column by.</summary>
    public double Center => (Left + Right) / 2d;
}
