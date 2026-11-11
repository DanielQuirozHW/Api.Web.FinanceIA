// Project : Api.Web.FinanceIA
// Created : 2026-05-09

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Pdf;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Generates PDF reports from an analysis result.
/// </summary>
public interface IPdfExportService
{
    /// <summary>
    /// Generates a PDF report from the given analysis data.
    /// </summary>
    /// <param name="result">The analysis result containing all data.</param>
    /// <param name="analysis">The analysis record (for fiscal year and other metadata).</param>
    /// <param name="tenantId">Tenant slug shown in the PDF header.</param>
    /// <param name="sections">
    /// Ordered list of section keys to include.
    /// Valid keys: balance | ratios | ratio_breakdown | executive_summary | risk | bcra | lufe | casfog |
    /// anomalies | cash_flow | footer. Unrecognised keys render nothing and are logged.
    /// When empty, all balance sections are included.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <param name="report">
    /// What the first page prints beyond the analysis and its result — razón social, CUIT, the people the
    /// analysis names, its credit lines, the previous analysis, the tenant's name. Null (or any null member)
    /// omits the item that needs it; the export never fails for it.
    /// </param>
    /// <param name="theme">
    /// The palette the report prints in: <see cref="PdfTheme.Light"/> (default — the paper document, unchanged)
    /// or <see cref="PdfTheme.Dark"/> (navy paper, for reading on screen).
    /// </param>
    /// <returns>PDF file bytes.</returns>
    Task<byte[]> GenerateAsync(
        AnalysisResult          result,
        Analysis                analysis,
        string                  tenantId,
        IReadOnlyList<string>   sections,
        CancellationToken       ct,
        CompanyPdfReport?       report = null,
        PdfTheme                theme  = PdfTheme.Light);

    /// <summary>
    /// Generates the persona humana report (DDL 072): the frozen evaluation, the decision and the credit line
    /// it originated. A different document from <see cref="GenerateAsync"/>, with its own section keys.
    /// </summary>
    /// <param name="report">Everything the report prints, gathered by the caller.</param>
    /// <param name="sections">
    /// Ordered list of persona section keys: persona_resumen | persona_elegibilidad | persona_filtros |
    /// persona_capacidad | persona_solvencia | persona_fiadores | persona_coherencia | persona_cobertura |
    /// persona_fuentes | persona_decision. When empty, all of them. The report renders them in that fixed order.
    /// </param>
    /// <param name="ct">Token to observe for cancellation.</param>
    /// <param name="theme">The palette the report prints in; see <see cref="GenerateAsync"/>.</param>
    /// <returns>PDF file bytes.</returns>
    Task<byte[]> GeneratePersonAsync(
        PersonPdfReport         report,
        IReadOnlyList<string>   sections,
        CancellationToken       ct,
        PdfTheme                theme = PdfTheme.Light);
}
