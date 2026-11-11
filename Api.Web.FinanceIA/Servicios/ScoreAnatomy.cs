// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Globalization;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The score anatomy on the 1000 scale — a rule-for-rule port of the Portal's
/// <c>apps/portal/src/lib/score-anatomy.ts</c> (<c>buildScoreAnatomy</c>, PortalesHWA/Portal-FinanceIA#327),
/// so the exported PDF prints exactly the figures "Anatomía del score" shows on screen.
/// </summary>
/// <remarks>
/// <para>
/// The engine (<see cref="ScoringService"/>) works on the INTERNAL 0–100 scale (0 = healthy): weighted
/// sum → critical-ratio floor → §7.3 LUFE ventasAdj → §7.4 CASFOG casfogMoraAdj. The reader sees the
/// 1000 scale via <see cref="ScoreScale.ToNosis"/>, which rounds to multiples of 10, so the weighted
/// contributions rarely sum to the final. This restates the engine's steps on the 1000 scale with the
/// ENGINE's own figures (PortalesHWA/Portal-FinanceIA#429): the composite is ToNosis(scoreBeforeFloor) when
/// the row carries it, every adjustment is its own engine delta (a full LUFE penalty of 10 internal points
/// is −100), and whatever ToNosis' rounding leaves is a separate "redondeo" figure — one per block — so the
/// printed lines always add up exactly to the score the gauge shows.
/// </para>
/// <para>
/// One deliberate divergence from the Portal: a legacy row without scoreBeforeFloor does NOT fold the
/// residual into its last adjustment either. The report never attributes rounding to an adjustment.
/// </para>
/// <para>
/// §7.1 chequesAdj is NOT one of those steps: the engine adds it to scoreBcra BEFORE the weighted sum
/// (it is already inside the BCRA contribution), so it is returned as an informational BCRA note and
/// never enters the composite → final arithmetic.
/// </para>
/// <para>
/// Rounding parity. Every <c>Math.round</c> in the Portal rounds half toward +∞; <see cref="JsRound"/>
/// reproduces that on <see cref="decimal"/> (C#'s default is banker's rounding, and AwayFromZero differs
/// for negative midpoints). The one deliberate divergence is the component → 1000 conversion: this uses
/// the canonical <see cref="ScoreScale.ToNosis"/>, which does not copy the Portal's legacy rule of
/// reading any score in (0, 1] as a 0–1 fraction (Portal toNosis(0.8) = 200, here 990). Scores are
/// clamped to 0–100 by the engine, so the negative-midpoint difference between the two is unreachable.
/// </para>
/// </remarks>
public static class ScoreAnatomy
{
    public const decimal WeightRatios = 0.50m;
    public const decimal WeightBcra   = 0.35m;
    public const decimal WeightMacro  = 0.15m;

    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    public enum ComponentKey
    {
        Ratios,
        Bcra,
        Macro
    }

    public enum AdjustmentKey
    {
        Floor,
        Ventas,
        Casfog
    }

    /// <summary>The persisted score breakdown fields the anatomy reads. All on the internal 0–100 scale.</summary>
    public sealed record Input
    {
        public decimal? ScoreRatios { get; init; }
        public decimal? ScoreBcra { get; init; }
        public decimal? ScoreMacro { get; init; }
        public bool? BcraExcluded { get; init; }
        public decimal? EffectiveWeightRatios { get; init; }
        public decimal? EffectiveWeightBcra { get; init; }
        public decimal? EffectiveWeightMacro { get; init; }
        public decimal? AppliedFloor { get; init; }
        public decimal? ScoreBeforeFloor { get; init; }
        public int CriticalRatioCount { get; init; }
        public decimal? ChequesAdj { get; init; }
        public decimal? ChequesProporcionRecuperada { get; init; }
        public decimal? VentasAdj { get; init; }
        public decimal? VentasVariacion { get; init; }
        public bool? VentasVariacionEsReal { get; init; }
        public string? VentasBase { get; init; }
        public decimal? CasfogMoraAdj { get; init; }
        public decimal? CasfogProporcionMora { get; init; }
    }

    /// <param name="Nosis">Component score on the 1000 scale.</param>
    /// <param name="Value">Nosis × weight, one decimal — exactly what the anatomy prints.</param>
    public sealed record Contribution(ComponentKey Key, int Nosis, decimal Weight, decimal Value);

    /// <param name="DeltaNosis">Signed points on the 1000 scale: negative = penalty, positive = improvement.</param>
    public sealed record Adjustment(AdjustmentKey Key, string Label, string ShortLabel, decimal DeltaNosis);

    /// <param name="DeltaNosis">Signed points on the 1000 scale, already inside the BCRA component. Informational only.</param>
    public sealed record Note(string Label, string ShortLabel, decimal DeltaNosis);

    public sealed record Model
    {
        public required IReadOnlyList<Contribution> Contributions { get; init; }

        /// <summary>ToNosis(scoreBeforeFloor) when the row carries it; Σ contributions as displayed otherwise.</summary>
        public required decimal CompositeDisplay { get; init; }

        /// <summary>CompositeDisplay − Σ contributions: the rounding between the components and the composite.</summary>
        public required decimal ComponentsRounding { get; init; }

        /// <summary><see cref="ComponentsRounding"/> to print on its own line, or null when it is 0.</summary>
        public required decimal? ComponentsRoundingLine { get; init; }

        /// <summary>§7.1 cheques — already inside scoreBcra; never part of composite → final.</summary>
        public required IReadOnlyList<Note> BcraNotes { get; init; }

        /// <summary>AppliedFloor &gt; 0 with at least one critical ratio — the Portal's floor rule.</summary>
        public required bool ShowFloor { get; init; }

        /// <summary>
        /// Internal points the floor actually raised the composite by (0 when it was reported but the
        /// composite had already cleared it). Not in the Portal model: the PDF uses it to keep its
        /// "no 'se redujo' for a floor that never bound" rule.
        /// </summary>
        public required decimal FloorRaised { get; init; }

        /// <summary>Engine order: floor (when shown), ventas, casfog — each with the engine's own delta.</summary>
        public required IReadOnlyList<Adjustment> Adjustments { get; init; }

        /// <summary>CompositeDisplay + floor delta — the figure the floor callout says the score went to.</summary>
        public required decimal AfterFloorDisplay { get; init; }

        /// <summary>ToNosis(AiSuggestedScore) — the same value the gauge shows.</summary>
        public required int FinalDisplay { get; init; }

        /// <summary>FinalDisplay − (CompositeDisplay + Σ adjustment deltas). Never folded into an adjustment.</summary>
        public required decimal Rounding { get; init; }

        /// <summary><see cref="Rounding"/> to print on its own line, or null when it is 0.</summary>
        public required decimal? RoundingLine { get; init; }
    }

    /// <summary>
    /// The nominal engine weights, or the persisted effective ones when BCRA was excluded from the
    /// score. Same rule as the Portal's <c>effectiveWeights</c>.
    /// </summary>
    public static (decimal Ratios, decimal Bcra, decimal Macro) EffectiveWeights(Input input)
    {
        ArgumentNullException.ThrowIfNull(input);

        bool isExcluded = input.BcraExcluded == true;
        return (
            isExcluded ? input.EffectiveWeightRatios ?? WeightRatios : WeightRatios,
            isExcluded ? input.EffectiveWeightBcra   ?? 0m           : WeightBcra,
            isExcluded ? input.EffectiveWeightMacro  ?? WeightMacro  : WeightMacro);
    }

    /// <summary>
    /// Builds the anatomy. Every clause is guarded, so a signal the analysis never carried contributes
    /// no line, and an absent score reads as 0 on the 1000 scale — the Portal's conservative fallback.
    /// </summary>
    public static Model Build(Input input, decimal? aiSuggestedScore)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (wRatios, wBcra, wMacro) = EffectiveWeights(input);

        List<Contribution> contributions =
        [
            BuildContribution(ComponentKey.Ratios, input.ScoreRatios, wRatios),
            BuildContribution(ComponentKey.Bcra,   input.ScoreBcra,   wBcra),
            BuildContribution(ComponentKey.Macro,  input.ScoreMacro,  wMacro),
        ];
        decimal contributionsSum = Round1(contributions.Sum(part => part.Value));

        // The engine's weighted sum, when the row carries it. Older rows only have the components.
        bool isEngineComposite = input.ScoreBeforeFloor is not null;
        decimal compositeDisplay = isEngineComposite
            ? ScoreScale.ToNosis(input.ScoreBeforeFloor!.Value)
            : contributionsSum;
        decimal componentsRounding = Round1(compositeDisplay - contributionsSum);

        List<Note> bcraNotes = [];
        if (input.ChequesAdj is { } chequesAdj && chequesAdj != 0m)
        {
            string recuperado = input.ChequesProporcionRecuperada is { } proporcion
                ? $" — importe recuperado {FormatProportionPct(proporcion)}"
                : string.Empty;
            bcraNotes.Add(new Note($"Cheques rechazados{recuperado}", "Cheques rechazados", ToNosisDelta(chequesAdj)));
        }

        bool showFloor = input.AppliedFloor is > 0m && input.CriticalRatioCount > 0;

        // The engine applies max(scoreBeforeFloor, floor) and reports AppliedFloor whenever a ratio is
        // critical — even when the composite was already past it — so a floor that did not bind moves nothing.
        decimal floorRaised = showFloor
            ? Math.Max(0m, (input.AppliedFloor ?? 0m) - (input.ScoreBeforeFloor ?? input.AppliedFloor ?? 0m))
            : 0m;

        List<Adjustment> adjustments = [];
        if (showFloor)
        {
            // A floor that binds REPLACES the composite: the score after it is the floor itself, so its delta
            // is measured from the composite as displayed — never from the unrounded internal value, which
            // would leave rounding inside "se redujo a". A row without scoreBeforeFloor cannot prove it bound.
            bool binds = input.ScoreBeforeFloor is { } before && (input.AppliedFloor ?? 0m) > before;
            decimal floorFigure = Math.Clamp(1000m - (10m * (input.AppliedFloor ?? 0m)), 0m, 1000m);

            adjustments.Add(new Adjustment(
                AdjustmentKey.Floor, "Piso por ratio crítico aplicado", "Piso por ratio crítico",
                binds ? Round1(floorFigure - compositeDisplay) : 0m));
        }

        if (input.VentasAdj is { } ventasAdj && ventasAdj != 0m)
        {
            adjustments.Add(new Adjustment(
                AdjustmentKey.Ventas, VentasLabel(input), "Ventas declaradas (LUFE)", ToNosisDelta(ventasAdj)));
        }

        if (input.CasfogMoraAdj is { } casfogMoraAdj && casfogMoraAdj != 0m)
        {
            string mora = input.CasfogProporcionMora is { } proporcionMora
                ? $" — {FormatProportionPct(proporcionMora)} del saldo en mora"
                : string.Empty;
            adjustments.Add(new Adjustment(
                AdjustmentKey.Casfog, $"Garantías SGR (CASFOG){mora}", "Garantías SGR (CASFOG)", ToNosisDelta(casfogMoraAdj)));
        }

        int finalDisplay = aiSuggestedScore is { } score ? ScoreScale.ToNosis(score) : 0;

        // The residual is stated on its own — never inside an adjustment: a full LUFE penalty reads −100.
        decimal rounding = Round1(finalDisplay - (compositeDisplay + adjustments.Sum(adjustment => adjustment.DeltaNosis)));

        Adjustment? floor = adjustments.Find(adjustment => adjustment.Key == AdjustmentKey.Floor);
        decimal afterFloorDisplay = Round1(compositeDisplay + (floor?.DeltaNosis ?? 0m));

        return new Model
        {
            Contributions          = contributions,
            CompositeDisplay       = compositeDisplay,
            ComponentsRounding     = componentsRounding,
            ComponentsRoundingLine = componentsRounding != 0m ? componentsRounding : null,
            BcraNotes              = bcraNotes,
            ShowFloor              = showFloor,
            FloorRaised            = floorRaised,
            Adjustments            = adjustments,
            AfterFloorDisplay      = afterFloorDisplay,
            FinalDisplay           = finalDisplay,
            Rounding               = rounding,
            RoundingLine           = rounding != 0m ? rounding : null,
        };
    }

    /// <summary>A 0–1 proportion as an es-AR percentage at one decimal (0.635 → "63,5 %").</summary>
    public static string FormatProportionPct(decimal fraction) =>
        (fraction * 100m).ToString("F1", EsAr) + " %";

    /// <summary>A 1000-scale figure: integers as-is, anything else at one es-AR decimal (722.5 → "722,5").</summary>
    public static string FormatFigure(decimal value) =>
        value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", EsAr);

    /// <summary>
    /// A signed 1000-scale delta with its explicit sign (−15 / +50 / 0). Negative uses the typographic
    /// minus U+2212; the Portal prints an ASCII hyphen, the figure is the same.
    /// </summary>
    public static string FormatDelta(decimal delta)
    {
        string figure = FormatFigure(Math.Abs(delta));
        return delta > 0m ? $"+{figure}" : delta < 0m ? $"\u2212{figure}" : figure;
    }

    /// <summary>JavaScript's <c>Math.round</c> on a decimal: half rounds toward +∞ (−2.5 → −2, 2.5 → 3).</summary>
    public static decimal JsRound(decimal value) => Math.Floor(value + 0.5m);

    /// <summary>Internal points (0–100, + = penalty) → 1000-scale points (+ = better): 1.6 → −16.</summary>
    public static decimal ToNosisDelta(decimal internalDelta) => NormaliseZero(-JsRound(10m * internalDelta));

    private static Contribution BuildContribution(ComponentKey key, decimal? score, decimal weight)
    {
        int nosis = score is { } value ? ScoreScale.ToNosis(value) : 0;
        return new Contribution(key, nosis, weight, JsRound(nosis * weight * 10m) / 10m);
    }

    private static decimal Round1(decimal value) => NormaliseZero(JsRound(value * 10m) / 10m);

    // decimal carries a sign bit on zero too; a "−0" must never reach a printed line.
    private static decimal NormaliseZero(decimal value) => value == 0m ? 0m : value;

    private static string VentasLabel(Input input)
    {
        string variacion = input.VentasVariacion is { } valor
            ? $" — {(valor < 0m ? "caída" : "suba")} {FormatProportionPct(Math.Abs(valor))}"
            : string.Empty;

        // An unrecognised base is dropped rather than echoed: a raw wire token in the middle of a
        // Spanish sentence reads as a bug. interanual_3m is checked before the generic 'inter' prefix.
        string normalised = (input.VentasBase ?? string.Empty).Trim().ToLowerInvariant();
        string? baseTexto = normalised switch
        {
            "interanual_3m"                                                => "interanual, 3 meses",
            _ when normalised.StartsWith("inter", StringComparison.Ordinal) => "interanual",
            _ when normalised.StartsWith("trim",  StringComparison.Ordinal) => "trimestral",
            _                                                              => null
        };

        string? deflactado = input.VentasVariacionEsReal switch
        {
            true  => "real, deflactado por IPC",
            false => "nominal",
            null  => null
        };

        string[] qualifiers = new[] { baseTexto, deflactado }.OfType<string>().ToArray();
        string qualifierText = qualifiers.Length > 0 ? $" ({string.Join(", ", qualifiers)})" : string.Empty;
        return $"Ventas declaradas (LUFE){variacion}{qualifierText}";
    }
}
