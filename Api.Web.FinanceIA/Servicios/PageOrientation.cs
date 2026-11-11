// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// One page's orientation as <see cref="PdfPageOrientation"/> reads it from the glyphs: the clockwise
/// turn to upright, and whether the glyphs were enough to decide it.
/// </summary>
internal readonly record struct PageOrientation(int Rotation, bool Decided)
{
    /// <summary>Not decided by the glyphs: rendered as it is unless something else judges it.</summary>
    public static PageOrientation Undecided => new(0, false);
}
