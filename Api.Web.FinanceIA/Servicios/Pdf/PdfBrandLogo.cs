// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// A tenant logo that passed every check of <see cref="BrandingLogoFetcher"/>: at most 1 MB, PNG or JPEG by its
/// magic bytes, at most 4000×4000 px, and decodable by iText. The bytes are never written after construction, so
/// one instance is safely shared by the cache and every concurrent export; each export builds its own iText image
/// from them.
/// </summary>
public sealed record PdfBrandLogo(byte[] Bytes, int PixelWidth, int PixelHeight, PdfLogoFormat Format);
