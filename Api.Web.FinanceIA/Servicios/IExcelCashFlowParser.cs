// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Turns an uploaded .xlsx into compact Markdown tables so the analyst's spreadsheet can be given
/// to a language model, which cannot read .xlsx itself. This is the structuring half of the job —
/// the model does the interpreting — so the parser reproduces the sheet and never editorialises:
/// figures are passed through as the file stores them, and anything dropped is reported, never
/// silently discarded.
/// </summary>
public interface IExcelCashFlowParser
{
    /// <summary>
    /// Validates and renders <paramref name="xlsxStream"/>. Never throws for bad input: an
    /// unreadable, macro-enabled, empty or bomb-shaped file comes back as the matching
    /// <see cref="ExcelParseStatus"/>. The stream is read from its current position and is left
    /// open for the caller to dispose.
    /// </summary>
    /// <param name="xlsxStream">Seekable stream over the uploaded bytes.</param>
    /// <returns>The rendering, or a rejection describing why the file was not usable.</returns>
    ExcelParseResult Parse(Stream xlsxStream);
}
