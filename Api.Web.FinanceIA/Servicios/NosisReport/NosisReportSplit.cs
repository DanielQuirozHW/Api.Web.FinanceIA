// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// The report cut into sections. <see cref="Preamble"/> is the page-1 block before the first heading:
/// only the header row reader (score, situación) looks into it, for two aligned numbers, and nothing
/// of it is ever stored. <see cref="StyleCalibrated"/> says whether headings were told by their style
/// (see <see cref="NosisReportSectionSplitter"/>) or by their text alone.
/// </summary>
internal sealed record NosisReportSplit(
    IReadOnlyDictionary<NosisReportHeadingKind, NosisReportSection> Sections,
    IReadOnlyList<NosisReportLine> Preamble,
    IReadOnlyList<NosisReportHeading> Headings,
    bool StyleCalibrated);
