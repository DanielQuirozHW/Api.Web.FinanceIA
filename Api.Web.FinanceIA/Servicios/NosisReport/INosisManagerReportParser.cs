// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// Reads a NOSIS Manager "Informe Individual" PDF in memory, deterministically (PdfPig; no model call),
/// into the company-level <c>NosisManagerReport</c>. Pure: no I/O, no logging, nothing kept.
/// </summary>
public interface INosisManagerReportParser
{
    /// <summary>Parses <paramref name="pdf"/>. Cancellation is honoured between pages.</summary>
    NosisReportParseResult Parse(byte[] pdf, CancellationToken ct);
}
