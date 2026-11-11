// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// One image of a rendered page set, as it went to the model: its 1-based position in the request, the
/// 1-based page it shows, whether it is the full page or one of its halves, its size in pixels and its
/// JPEG data in base64 — the very string the request carried, so nothing is encoded twice. Held in memory
/// for the duration of one call only: never logged, never persisted.
/// </summary>
/// <param name="ImageNumber">1-based position among the request's images.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="Half"><see cref="FullHalf"/>, <see cref="LeftHalf"/> or <see cref="RightHalf"/>.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Base64Jpeg">The JPEG, base64-encoded.</param>
public sealed record RenderedImage(int ImageNumber, int Page, string Half, int Width, int Height, string Base64Jpeg)
{
    /// <summary>The image is the whole page.</summary>
    public const string FullHalf = "full";

    /// <summary>The image is the page's left half.</summary>
    public const string LeftHalf = "left";

    /// <summary>The image is the page's right half.</summary>
    public const string RightHalf = "right";
}
