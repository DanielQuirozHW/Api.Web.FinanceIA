// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The one line under "Cómo se compone el score" that says what moved the score after the composite: every
/// composite-level adjustment with the ENGINE's applied value (a binding critical-ratio floor, the LUFE sales
/// penalty, the CASFOG arrears penalty), then the rounding to the score scale on its own — never folded into
/// an adjustment. Cheques and the BCRA trend are not here: the engine applies them inside the BCRA component,
/// before the composite.
/// </summary>
internal static class ScoreAdjustmentLine
{
    /// <summary>One movement on the line.</summary>
    internal sealed record Part(string Label, decimal DeltaNosis, bool IsRounding);

    /// <summary>The parts and the sentence that prints them.</summary>
    internal sealed record Line(decimal Composite, IReadOnlyList<Part> Parts, int Final, string Text)
    {
        /// <summary>True when nothing but rounding separates the composite from the final score.</summary>
        internal bool HasAdjustments => Parts.Any(part => !part.IsRounding);
    }

    /// <summary>Builds the line from the anatomy (the engine's figures restated on the 1000 scale).</summary>
    internal static Line Build(ScoreAnatomy.Model anatomy)
    {
        ArgumentNullException.ThrowIfNull(anatomy);

        var parts = new List<Part>();
        foreach (ScoreAnatomy.Adjustment adjustment in anatomy.Adjustments)
        {
            // A floor the engine reports but that did not bind moved nothing: it was not applied.
            if (adjustment.DeltaNosis == 0m) continue;

            string label = adjustment.Key switch
            {
                ScoreAnatomy.AdjustmentKey.Floor  => "piso por ratio crítico",
                ScoreAnatomy.AdjustmentKey.Ventas => "ventas declaradas (LUFE)",
                _                                 => "garantías SGR en mora (CASFOG)"
            };
            parts.Add(new Part(label, adjustment.DeltaNosis, IsRounding: false));
        }

        if (anatomy.RoundingLine is { } rounding)
            parts.Add(new Part("redondeo", rounding, IsRounding: true));

        string composite = ScoreAnatomy.FormatFigure(anatomy.CompositeDisplay);
        string final     = anatomy.FinalDisplay.ToString(System.Globalization.CultureInfo.InvariantCulture);

        IEnumerable<string> movements = parts.Select(part => $"{part.Label} {ScoreAnatomy.FormatDelta(part.DeltaNosis)}");
        string middle = parts.Any(part => !part.IsRounding)
            ? string.Join(" · ", movements)
            : string.Join(" · ", new[] { "sin ajustes ni pisos" }.Concat(movements));

        string text = $"Score compuesto {composite} → {middle} → score final {final}.";
        return new Line(anatomy.CompositeDisplay, parts, anatomy.FinalDisplay, text);
    }
}
