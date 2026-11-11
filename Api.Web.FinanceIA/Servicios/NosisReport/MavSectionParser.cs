// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// <c>mav</c>: MERCADO ARGENTINO DE VALORES — "&lt;label&gt; &lt;amount&gt;" lines, by instrument and by currency.
/// A line goes to <c>porMoneda</c> when its label is a currency or it follows a "Moneda" sub-header,
/// otherwise to <c>porInstrumento</c>. Total lines are skipped. This block mixes number formats
/// ("1.527.384.960" with a total "1,939,384,960"): each figure is read by its own shape, and an
/// ambiguous one stays null.
/// </summary>
internal static class MavSectionParser
{
    private static readonly string[] Currencies =
        ["PESOS", "PESO", "ARS", "$", "DOLARES", "DOLAR", "USD", "U$S", "US$", "EUROS", "EURO", "EUR"];

    public static NosisReportMav Parse(NosisReportSection section)
    {
        var instrumentos = new List<NosisReportMavInstrumento>();
        var monedas = new List<NosisReportMavMoneda>();
        var byCurrency = false;

        string? pending = null;

        foreach (var line in section.Lines)
        {
            // The monthly cross-table ("Instrumento | Cant. | Monto | Cant. | U$S MEP ($) | …") is not part
            // of the two lists.
            if (line.Normalised.Contains("INSTRUMENTO", StringComparison.Ordinal) && line.Normalised.Contains("CANT", StringComparison.Ordinal))
                break;

            if (NosisReportTableLines.IsNote(line) || line.Normalised.StartsWith("TOTAL", StringComparison.Ordinal))
            {
                pending = null;
                continue;
            }

            var words = line.Words;
            if (words.Count == 0 || NosisReportPeriods.Parse(words[0].Text) is not null)
                continue;   // a period row of a table this block does not read

            var last = words[^1].Text;
            var isAmount = NosisReportNumbers.TryAmount(last, section.NumberStyle, out var monto);

            string label;
            if (isAmount && words.Count == 1)
            {
                // A label that wrapped: its amount sits alone on the line below it.
                if (pending is null)
                    continue;
                label = pending;
            }
            else if (!isAmount)
            {
                // A sub-header ("Por moneda", "Instrumento  Monto"), or a label whose amount follows.
                if (line.Normalised.Contains("MONEDA", StringComparison.Ordinal))
                {
                    byCurrency = true;
                    pending = null;
                }
                else if (line.Normalised.Contains("INSTRUMENTO", StringComparison.Ordinal))
                {
                    byCurrency = false;
                    pending = null;
                }
                else
                {
                    pending = line.Text;
                }

                continue;
            }
            else
            {
                label = string.Join(' ', words.Take(words.Count - 1).Select(w => w.Text)).Trim();
            }

            pending = null;
            if (label.Length == 0)
                continue;

            var amount = NosisReportNumbers.ToPesos(monto, section.InThousands);
            if (byCurrency || IsCurrency(label))
                monedas.Add(new NosisReportMavMoneda { Moneda = label, Monto = amount });
            else
                instrumentos.Add(new NosisReportMavInstrumento { Instrumento = label, Monto = amount });
        }

        if (instrumentos.Count == 0 && monedas.Count == 0 && !NosisReportText.SaysNone(section.Lines))
            throw new NosisReportFormatException("No MAV amount line.");

        return new NosisReportMav { PorInstrumento = instrumentos, PorMoneda = monedas };
    }

    private static bool IsCurrency(string label)
    {
        var key = NosisReportText.Normalise(label);
        return Currencies.Any(c => key == c || key.StartsWith(c + " ", StringComparison.Ordinal))
               || key.StartsWith("DOLAR", StringComparison.Ordinal) || key.StartsWith("PESO", StringComparison.Ordinal);
    }
}
