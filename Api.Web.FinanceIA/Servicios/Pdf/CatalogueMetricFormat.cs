// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using System.Globalization;
using System.Text;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The es-AR rendering of a catalogue alert's measured value and threshold — a port of the Portal's
/// <c>formatMetricValue</c> / <c>resolveUnit</c> (<c>apps/portal/src/lib/analysis-alerts.ts</c>), so the PDF prints the
/// same figure the screen shows:
/// <list type="bullet">
///   <item>the unit is the one the alert states (<c>metric_unit</c>): <c>proportion</c> → a percentage,
///   <c>absolute</c> or any token this build does not know → a plain number;</item>
///   <item>no unit at all (a row stored before the field existed) → the legacy registry, which knows only
///   <c>inventory_over_current_assets</c>; any other key, or no key → a plain number. Never guessed from the value;</item>
///   <item>a proportion is multiplied by 100 and printed with exactly one decimal plus <c>" %"</c> (0,5543 → "55,4 %");</item>
///   <item>a plain number is printed with up to four decimals and es-AR grouping from four digits (1500 → "1.500").</item>
/// </list>
/// Rounding follows <c>Intl.NumberFormat</c>: the double's shortest round-trip decimal, half away from zero; a negative
/// value keeps its sign even when it rounds to zero ("-0,0 %"). Culture-independent: the separators are written here,
/// not read from an ICU or NLS <c>es-AR</c> that may differ between machines.
/// </summary>
internal static class CatalogueMetricFormat
{
    private enum Unit
    {
        Proportion,
        Plain,
    }

    // The wire vocabulary of metric_unit (RuleThresholdUnitWire), matched exactly — no case folding, as in the Portal.
    private static readonly IReadOnlyDictionary<string, Unit> UnitByWireToken =
        new Dictionary<string, Unit>(StringComparer.Ordinal)
        {
            [RuleThresholdUnitWire.Proportion] = Unit.Proportion,
            [RuleThresholdUnitWire.Absolute]   = Unit.Plain,
        };

    // LEGACY: read only when the row carries no metric_unit (lib/analysis-alerts.ts UNIT_BY_METRIC_KEY).
    private static readonly IReadOnlyDictionary<string, Unit> UnitByMetricKey =
        new Dictionary<string, Unit>(StringComparer.Ordinal)
        {
            ["inventory_over_current_assets"] = Unit.Proportion,
        };

    /// <summary>The figure as printed, or null for a null value (the caller then omits that half).</summary>
    internal static string? Format(string? metricKey, string? metricUnit, double? value)
    {
        if (value is not { } number)
            return null;

        return ResolveUnit(metricUnit, metricKey) == Unit.Proportion
            ? Fixed(number * 100d, minDecimals: 1, maxDecimals: 1) + " %"
            : Fixed(number, minDecimals: 0, maxDecimals: 4);
    }

    /// <summary>
    /// "Medido: 41,8 % · Umbral: 40,0 %" — each half only when its value exists; null when neither does.
    /// </summary>
    internal static string? MetricLine(CatalogueAlertChannel.Item alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        string? measured  = Format(alert.MetricKey, alert.MetricUnit, alert.MetricValue);
        string? threshold = Format(alert.MetricKey, alert.MetricUnit, alert.ThresholdValue);

        var parts = new List<string>(2);
        if (measured is not null)
            parts.Add($"Medido: {measured}");
        if (threshold is not null)
            parts.Add($"Umbral: {threshold}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// The figure a catalogue bullet on page 1 carries after its title: "84,4 % (umbral 50,0 %)", or "84,4 %" with no
    /// threshold. Null when the alert states no measured value — a threshold alone is not a figure of the company.
    /// </summary>
    internal static string? Inline(CatalogueAlertChannel.Item alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        string? measured = Format(alert.MetricKey, alert.MetricUnit, alert.MetricValue);
        if (measured is null)
            return null;

        string? threshold = Format(alert.MetricKey, alert.MetricUnit, alert.ThresholdValue);
        return threshold is null ? measured : $"{measured} (umbral {threshold})";
    }

    private static Unit ResolveUnit(string? metricUnit, string? metricKey)
    {
        if (metricUnit is not null)
            return UnitByWireToken.TryGetValue(metricUnit, out Unit stated) ? stated : Unit.Plain;
        if (metricKey is not null)
            return UnitByMetricKey.TryGetValue(metricKey, out Unit legacy) ? legacy : Unit.Plain;
        return Unit.Plain;
    }

    private static string Fixed(double value, int minDecimals, int maxDecimals)
    {
        bool negative = double.IsNegative(value);
        double magnitude = Math.Abs(value);

        // Intl rounds the shortest decimal that round-trips the double (1.0005, not 1.000499…), half away from zero.
        string digits;
        if (decimal.TryParse(magnitude.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact))
        {
            decimal rounded = decimal.Round(exact, maxDecimals, MidpointRounding.AwayFromZero);
            digits = rounded.ToString("F" + maxDecimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        else
        {
            // Beyond decimal's range: no catalogue figure is ever this large, but never throw for it.
            digits = magnitude.ToString("F" + maxDecimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        int point = digits.IndexOf('.', StringComparison.Ordinal);
        string integerPart  = point < 0 ? digits : digits[..point];
        string fractionPart = point < 0 ? string.Empty : digits[(point + 1)..];

        // Trailing zeros beyond the minimum go, as Intl drops them.
        int keep = fractionPart.Length;
        while (keep > minDecimals && fractionPart[keep - 1] == '0')
            keep--;
        fractionPart = fractionPart[..keep];

        var sb = new StringBuilder();
        if (negative)
            sb.Append('-');

        for (int i = 0; i < integerPart.Length; i++)
        {
            if (i > 0 && (integerPart.Length - i) % 3 == 0)
                sb.Append('.');
            sb.Append(integerPart[i]);
        }

        if (fractionPart.Length > 0)
            sb.Append(',').Append(fractionPart);

        return sb.ToString();
    }
}
