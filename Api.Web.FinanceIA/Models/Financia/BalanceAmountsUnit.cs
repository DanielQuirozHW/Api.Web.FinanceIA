// Project : Api.Web.FinanceIA
// Created : 2026-09-24

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Resolves the printed unit a balance document declares ("expresado en miles de pesos") — as
/// reported by the extraction model in <c>amounts_unit</c> — into the factor that brings its
/// figures to pesos. The model reports amounts AS PRINTED and never multiplies; this is the one
/// place the scale is applied, deterministically.
/// </summary>
public static class BalanceAmountsUnit
{
    public const string Pesos    = "pesos";
    public const string Miles    = "miles";
    public const string Millones = "millones";

    /// <summary>
    /// Maps the reported unit to (canonical unit, factor, assumed). Accepts the three canonical
    /// values and obvious variants ("miles de pesos", "thousands", "millions"). A missing or
    /// unrecognised value resolves to pesos with <c>Assumed = true</c>, so an older analysis or
    /// an unexpected token never scales anything.
    /// </summary>
    public static (string Unit, double Factor, bool Assumed) Resolve(string? reported)
    {
        var text = reported?.Trim().ToLowerInvariant() ?? string.Empty;

        if (text.Contains("millon") || text.Contains("million"))
            return (Millones, 1_000_000d, false);

        if (text.Contains("miles") || text.Contains("thousand") || text == "mil")
            return (Miles, 1_000d, false);

        if (text.Contains("peso") || text is "unidades" or "units" or "ars")
            return (Pesos, 1d, false);

        return (Pesos, 1d, true);
    }
}
