// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>The image formats a tenant logo may be in, decided by the file's magic bytes alone.</summary>
public enum PdfLogoFormat
{
    /// <summary>PNG (<c>89 50 4E 47 0D 0A 1A 0A</c>).</summary>
    Png,

    /// <summary>JPEG (<c>FF D8 FF</c>).</summary>
    Jpeg
}
