// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Globalization;
using System.Text;
using Api.Web.FinanceIA.Clases.Time;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// A CSV writer for Excel in es-AR: UTF-8 with a byte-order mark (without it Excel reads the bytes as
/// ANSI and mangles every accent), <c>;</c> as the separator (es-AR Excel splits on it because the comma
/// is the decimal mark), CRLF line ends and RFC 4180 quoting.
/// </summary>
/// <remarks>
/// <para>
/// CSV INJECTION. A cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is
/// read by Excel as a formula. Every cell — names, reasons, ids alike — is prefixed with an apostrophe in that
/// case BEFORE quoting, so free text an analyst typed can never execute when the risk lead opens the file.
/// No numeric cell this board writes is ever negative, so the guard never mangles a figure.
/// </para>
/// </remarks>
public static class PersonCalibrationCsv
{
    /// <summary>The field separator.</summary>
    public const char Separator = ';';

    private const string LineEnd = "\r\n";

    // es-AR's decimal comma, spelled out rather than taken from CultureInfo("es-AR"): the culture data
    // comes from ICU on the Linux CI and from NLS on the Windows App Service, and a CSV must not depend on
    // which host wrote it.
    private static readonly NumberFormatInfo EsAr = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator   = ".",
        NegativeSign           = "-"
    };

    /// <summary>The whole file: BOM, the header row and one line per row.</summary>
    public static byte[] Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        AppendLine(builder, headers);
        foreach (var row in rows)
            AppendLine(builder, row);

        var preamble = Encoding.UTF8.GetPreamble();
        var body     = Encoding.UTF8.GetBytes(builder.ToString());

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>One cell: the injection guard, then RFC 4180 quoting when it holds a separator, a quote or a line break.</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        var mustQuote = value.IndexOfAny([Separator, '"', '\r', '\n']) >= 0;
        return mustQuote
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>A decimal with the es-AR decimal comma and no thousands separator; empty when null.</summary>
    public static string Decimal(decimal? value)
        => value is { } v ? v.ToString("0.##", EsAr) : string.Empty;

    /// <summary>An integer; empty when null.</summary>
    public static string Integer(int? value)
        => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A UTC instant as its Argentine date, dd/MM/yyyy; empty when null.</summary>
    public static string Date(DateTime? utc)
        => utc is { } v ? ArgentineTime.ToLocal(v).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Sí / No; empty when null.</summary>
    public static string Flag(bool? value) => value switch
    {
        true  => "Sí",
        false => "No",
        null  => string.Empty
    };

    private static void AppendLine(StringBuilder builder, IReadOnlyList<string?> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                builder.Append(Separator);
            builder.Append(Cell(cells[i]));
        }

        builder.Append(LineEnd);
    }
}
