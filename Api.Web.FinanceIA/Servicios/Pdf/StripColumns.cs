// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The widths of page 1's strip (Monto solicitado · Analista · Estado · Documento): each column gets the width its own
/// label and value need, and the slack is shared equally, so no value wraps while the four fit on one line; when they
/// do not, the fixed fallback widths apply and the longest value wraps, as before. Pure.
/// </summary>
internal static class StripColumns
{
    /// <summary>The strip's fixed shares, in percent, used when the measured contents do not fit.</summary>
    internal static readonly float[] FallbackPercents = [22f, 26f, 22f, 30f];

    /// <summary>
    /// Column widths in points summing to <paramref name="total"/>: <paramref name="minWidths"/> plus an equal share of
    /// the slack when they fit, else <paramref name="fallbackPercents"/> of the total.
    /// </summary>
    internal static float[] Fit(IReadOnlyList<float> minWidths, float total, IReadOnlyList<float> fallbackPercents)
    {
        ArgumentNullException.ThrowIfNull(minWidths);
        ArgumentNullException.ThrowIfNull(fallbackPercents);
        if (minWidths.Count == 0 || minWidths.Count != fallbackPercents.Count)
            throw new ArgumentException("One minimum and one fallback share per column.", nameof(minWidths));

        float needed = minWidths.Sum();
        if (needed <= total)
        {
            float share = (total - needed) / minWidths.Count;
            return minWidths.Select(width => width + share).ToArray();
        }

        float percents = fallbackPercents.Sum();
        return fallbackPercents.Select(percent => total * percent / percents).ToArray();
    }
}
