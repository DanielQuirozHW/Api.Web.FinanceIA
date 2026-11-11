// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// The body lines under one data heading (the heading itself excluded), across page breaks and repeated
/// "(continuación)" headings. <see cref="InThousands"/> is true when the section, or its heading group
/// (e.g. INFORMACIÓN BANCARIA), says "Importes expresados en miles de $"; <see cref="NumberStyle"/> is
/// the number format its own tokens show (formats vary by section in one report).
/// </summary>
internal sealed record NosisReportSection(
    NosisReportHeadingKind Kind, IReadOnlyList<NosisReportLine> Lines, bool InThousands)
{
    /// <summary>How this section writes its numbers (it only decides ambiguous tokens).</summary>
    public NosisNumberStyle NumberStyle { get; } = NosisReportNumbers.InferStyle(Lines);
}
