// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// A section's layout was not what its parser understands. Caught per section by
/// <see cref="NosisManagerReportParser"/>, which marks that section "unreadable" and carries on. The
/// message names the SHAPE that failed, never a value from the report.
/// </summary>
internal sealed class NosisReportFormatException : Exception
{
    public NosisReportFormatException(string message) : base(message)
    {
    }
}
