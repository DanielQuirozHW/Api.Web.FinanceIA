// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>srt</c>: OTROS ANTECEDENTES (SRT) — the ART insurer, whether the contract is in force, and whether
/// it was extinguished for lack of payment.
/// </summary>
internal static class SrtSectionParser
{
    public static NosisReportSrt Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        var aseguradora = NosisReportText.ValueOf(lines, "Aseguradora de riesgos del trabajo", "Aseguradora", "ART");

        bool? vigente = NosisReportText.YesNo(NosisReportText.ValueOf(lines, "Vigente", "Contrato vigente"));
        if (vigente is null)
        {
            var estado = NosisReportText.Normalise(NosisReportText.ValueOf(lines, "Estado", "Estado del contrato", "Situación"));
            if (estado.Contains("NO VIGENTE", StringComparison.Ordinal) || estado.Contains("EXTINGUID", StringComparison.Ordinal)
                || estado.Contains("RESCINDID", StringComparison.Ordinal) || estado.Contains("BAJA", StringComparison.Ordinal))
                vigente = false;
            else if (estado.Contains("VIGENTE", StringComparison.Ordinal) || estado.Contains("ACTIVO", StringComparison.Ordinal))
                vigente = true;
        }

        bool? extinguido = NosisReportText.YesNo(
            NosisReportText.ValueOf(lines, "Extinguido por falta de pago", "Extinción por falta de pago"));
        if (extinguido is null && (NosisReportText.Contains(lines, "EXTINGUIDO POR FALTA DE PAGO")
                                   || NosisReportText.Contains(lines, "EXTINCION POR FALTA DE PAGO")))
            extinguido = true;

        if (aseguradora is null && vigente is null && extinguido is null && !NosisReportText.SaysNone(lines))
            throw new NosisReportFormatException("No SRT field.");

        return new NosisReportSrt { Aseguradora = aseguradora, Vigente = vigente, ExtinguidoPorFaltaPago = extinguido };
    }
}
