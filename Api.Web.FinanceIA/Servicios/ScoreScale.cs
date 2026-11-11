// Project : Api.Web.FinanceIA
// Created : 2026-06-16

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Converts internal motor scores (0–100, 0=healthy) to the NOSIS presentation
/// scale (0–1000, 1000=healthy). Single canonical implementation — do NOT duplicate.
/// </summary>
public static class ScoreScale
{
    /// <summary>
    /// Canonical formula: round motor to integer first, invert, scale ×10, clamp 0–1000.
    /// Examples: 13.8→860, 14→860, 0→1000, 100→0.
    /// </summary>
    public static int ToNosis(decimal scoreMotor)
    {
        var rounded = Math.Round(scoreMotor, MidpointRounding.AwayFromZero);
        var nosis = (100m - rounded) * 10m;
        return (int)Math.Clamp(nosis, 0m, 1000m);
    }
}
