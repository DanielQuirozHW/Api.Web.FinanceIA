// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>How a parse ended.</summary>
public enum NosisReportParseOutcome
{
    /// <summary>A NOSIS Manager "Informe Individual"; the report is in the result.</summary>
    Parsed,

    /// <summary>Not a readable NOSIS Manager "Informe Individual"; <c>Reason</c> says why.</summary>
    NotRecognized,

    /// <summary>More pages than <see cref="NosisManagerReportParser.MaxPages"/>; not read.</summary>
    TooManyPages,
}
