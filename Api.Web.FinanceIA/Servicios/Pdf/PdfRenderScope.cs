// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The palette of the export being rendered on the current flow. Every renderer of
/// <see cref="PdfExportService"/> is static and reads its colours through one alias set, so the theme is
/// ambient rather than a parameter threaded through each of them: the two Build methods enter a scope, render
/// the whole document (every <c>doc.Add</c>, the END_PAGE furniture, the page-count placeholder and
/// <c>doc.Close</c>) synchronously inside it, and leave it. An <see cref="AsyncLocal{T}"/> keeps concurrent
/// exports of different themes apart; outside any scope the palette is <see cref="PdfPalette.Light"/>.
/// </summary>
internal static class PdfRenderScope
{
    private static readonly AsyncLocal<PdfPalette?> Active = new();

    /// <summary>The palette of the render in progress, or <see cref="PdfPalette.Light"/> outside one.</summary>
    internal static PdfPalette Current => Active.Value ?? PdfPalette.Light;

    /// <summary>
    /// Makes <paramref name="palette"/> current until the returned scope is disposed. A synchronous method's
    /// AsyncLocal write is visible to its caller after it returns, so the previous value is always restored.
    /// </summary>
    internal static Scope Enter(PdfPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        PdfPalette? previous = Active.Value;
        Active.Value = palette;
        return new Scope(previous);
    }

    /// <summary>Restores the palette that was current before <see cref="Enter"/>.</summary>
    internal readonly struct Scope(PdfPalette? previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
