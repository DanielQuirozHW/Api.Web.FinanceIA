// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Keeps the first-page fact sheet on ONE page. When the measured blocks would overflow, it degrades in a fixed
/// order — drop "Vigencia de los datos", then "Comparación", then trim "Puntos de atención" to three bullets —
/// and never touches the identity, the strip, the score card or its composition. Pure: the caller measures.
/// </summary>
internal static class FactSheetFit
{
    internal const int TrimmedAttentionCap = 3;

    /// <summary>Measured heights (points) of the blocks, as laid out at full content width.</summary>
    internal sealed record Heights
    {
        /// <summary>Identity, strip, score card, composition, decision — never dropped.</summary>
        public required float Fixed { get; init; }

        /// <summary>"Puntos de atención" with every selected bullet.</summary>
        public required float AttentionFull { get; init; }

        /// <summary>"Puntos de atención" with at most <see cref="TrimmedAttentionCap"/> bullets.</summary>
        public required float AttentionTrimmed { get; init; }

        /// <summary>"Vigencia de los datos" (0 when there is nothing to print).</summary>
        public required float Vigencia { get; init; }

        /// <summary>"Comparación" (0 when there is nothing to print).</summary>
        public required float Comparacion { get; init; }

        /// <summary>Both blocks side by side, when both exist; null otherwise (they then stack).</summary>
        public float? Both { get; init; }
    }

    /// <summary>What to render.</summary>
    internal sealed record Plan(bool IncludeVigencia, bool IncludeComparacion, bool TrimAttention, bool Fits);

    internal static Plan Choose(Heights heights, float available)
    {
        ArgumentNullException.ThrowIfNull(heights);

        float Optional(bool vigencia, bool comparacion) =>
            vigencia && comparacion && heights.Both is { } both
                ? both
                : (vigencia ? heights.Vigencia : 0f) + (comparacion ? heights.Comparacion : 0f);

        bool Fits(bool vigencia, bool comparacion, bool trim) =>
            heights.Fixed
            + (trim ? heights.AttentionTrimmed : heights.AttentionFull)
            + Optional(vigencia, comparacion)
            <= available;

        if (Fits(true, true, false))   return new Plan(true,  true,  false, true);
        if (Fits(false, true, false))  return new Plan(false, true,  false, true);
        if (Fits(false, false, false)) return new Plan(false, false, false, true);

        return new Plan(false, false, true, Fits(false, false, true));
    }
}
