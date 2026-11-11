// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>referenciasComerciales</c>: REFERENCIAS COMERCIALES — how many, and which sources (companies)
/// reported. The contact persons, phones and e-mails are located as columns only so their words are
/// DROPPED; they never reach the model.
/// </summary>
internal static class ReferenciasComercialesSectionParser
{
    private const string Fuente = "fuente";
    private const string Drop   = "drop";

    private static readonly Dictionary<string, string> HeaderPhrases = new(StringComparer.Ordinal)
    {
        ["Fuente"] = Fuente, ["Empresa"] = Fuente, ["Informante"] = Fuente, ["Proveedor"] = Fuente,
        ["Contacto"] = Drop, ["Persona de contacto"] = Drop, ["Teléfono"] = Drop, ["Telefono"] = Drop,
        ["E-mail"] = Drop, ["Email"] = Drop, ["Mail"] = Drop,
        ["Fecha"] = Drop, ["Monto"] = Drop, ["Importe"] = Drop, ["Calificación"] = Drop, ["Concepto"] = Drop,
        ["Rubro"] = Drop, ["Plazo"] = Drop,
    };

    public static NosisReportReferenciasComerciales Parse(NosisReportSection section)
    {
        var lines = section.Lines;
        var declared = NosisReportNumbers.FirstInteger(
            NosisReportText.ValueOf(lines, "Cantidad de referencias", "Cantidad", "Total de referencias", "Total"), section.NumberStyle);

        var fuentes = new List<string>();
        var named = NosisReportNamedColumns.Find(lines, HeaderPhrases, 2);
        if (named is not null && named.Columns.Any(c => c.Key == Fuente))
        {
            for (var i = named.HeaderIndex + 1; i < lines.Count; i++)
            {
                var line = lines[i];
                if (NosisReportTableLines.IsNote(line) || named.IsRepeatedHeader(line, HeaderPhrases)
                    || NosisReportText.ValueAfter(line, "CANTIDAD") is not null || NosisReportText.ValueAfter(line, "TOTAL") is not null)
                    continue;

                if (named.Read(line).GetValueOrDefault(Fuente) is { Length: > 0 } fuente
                    && !fuentes.Contains(fuente, StringComparer.Ordinal))
                    fuentes.Add(fuente);
            }
        }
        else if (declared is null && !NosisReportText.SaysNone(lines))
        {
            throw new NosisReportFormatException("No references table header and no count.");
        }

        return new NosisReportReferenciasComerciales { Cantidad = declared ?? fuentes.Count, Fuentes = fuentes };
    }
}
