// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// One heading the splitter acted on. <see cref="Known"/> is false for a heading recognised by its
/// STYLE only (an unknown section, skipped). For the local harness's diagnostics; never stored.
/// </summary>
internal sealed record NosisReportHeading(NosisReportHeadingKind Kind, bool Known, NosisReportLine Line);
