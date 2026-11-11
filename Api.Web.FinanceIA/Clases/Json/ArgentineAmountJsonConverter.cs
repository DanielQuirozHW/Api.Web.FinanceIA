// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Clases.Json;

/// <summary>
/// Reads a monetary figure the extraction model emitted into a <see cref="double"/>, whether it
/// arrived as a JSON number or — against the prompt's instructions — as a string written the way
/// an Argentine financial statement prints it.
///
/// The gap this closes: the extraction response used to be deserialized with default options, so
/// a single quoted figure such as <c>"(1.234.567)"</c> threw, the whole analysis failed with a 502,
/// and a malformed cash-flow figure silently degraded the statement to <c>present=false</c>.
///
/// Strings are parsed by <see cref="TryParse"/>, which understands parentheses and a leading or
/// trailing minus as a NEGATIVE sign, es-AR grouping (<c>1.234.567,89</c>), a currency prefix, and
/// invariant decimals (<c>1234567.89</c>). A string it cannot read never throws: the value becomes
/// null and the raw text is reported through the optional callback, so the caller can surface it
/// to the analyst as an anomaly instead of losing it silently.
///
/// Not registered globally — MVC serialization must not start coercing strings into numbers on the
/// API surface. <c>ClaudeService</c> attaches it to a per-call <see cref="JsonSerializerOptions"/>
/// for the extraction response only.
/// </summary>
public sealed class ArgentineAmountJsonConverter : JsonConverter<double?>
{
    private readonly Action<string>? _onUnparseable;

    /// <summary>Creates a converter that drops unparseable figures without reporting them.</summary>
    public ArgentineAmountJsonConverter()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a converter that reports the raw text of every figure it could not read through
    /// <paramref name="onUnparseable"/>.
    /// </summary>
    public ArgentineAmountJsonConverter(Action<string>? onUnparseable)
    {
        _onUnparseable = onUnparseable;
    }

    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                if (reader.TryGetDouble(out var number) && double.IsFinite(number))
                    return number;

                _onUnparseable?.Invoke(Encoding.UTF8.GetString(reader.ValueSpan));
                return null;

            case JsonTokenType.String:
                var text = reader.GetString() ?? string.Empty;
                if (TryParse(text, out var parsed))
                    return parsed;

                _onUnparseable?.Invoke(text);
                return null;

            default:
                // Skip() is load-bearing: returning from a StartObject/StartArray without consuming
                // the subtree would leave the reader inside it and corrupt the rest of the document.
                var shape = reader.TokenType switch
                {
                    JsonTokenType.True        => "true",
                    JsonTokenType.False       => "false",
                    JsonTokenType.StartObject => "{…}",
                    JsonTokenType.StartArray  => "[…]",
                    _                         => reader.TokenType.ToString()
                };
                reader.Skip();
                _onUnparseable?.Invoke(shape);
                return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } number)
            writer.WriteNumberValue(number);
        else
            writer.WriteNullValue();
    }

    /// <summary>
    /// Parses a figure written the way a financial statement prints it. Returns false — never
    /// throws — when the text is not a single unambiguous amount.
    /// <list type="bullet">
    /// <item>Negative: <c>(1.234)</c>, <c>-1.234</c>, <c>−1.234</c> (U+2212) or <c>1.234-</c>;
    /// exactly one marker.</item>
    /// <item>Ignored: whitespace (incl. NBSP), <c>$</c>, <c>AR$</c>, <c>ARS</c>.</item>
    /// <item>Both <c>.</c> and <c>,</c> present: the LAST one is the decimal separator.</item>
    /// <item>Only <c>.</c>: several → thousands; one followed by exactly three digits after a
    /// 1–3 digit integer part (not starting with 0) → thousands (es-AR <c>1.234</c>); otherwise
    /// decimal (<c>1234567.89</c>).</item>
    /// <item>Only <c>,</c>: several → thousands; one → decimal (es-AR <c>1,5</c>).</item>
    /// <item>Thousands groups must be well formed (first 1–3 digits, the rest exactly 3).</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? raw, out double value)
    {
        value = 0;
        if (raw is null)
            return false;

        var s = StripNoise(raw);
        if (s.Length == 0)
            return false;

        var negative = false;
        var markers  = 0;

        if (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            negative = true;
            markers++;
            s = s[1..^1];
        }

        if (s.Length > 0 && (s[0] == '-' || s[0] == '−'))
        {
            negative = true;
            markers++;
            s = s[1..];
        }
        else if (s.Length > 0 && s[0] == '+')
        {
            markers++;
            s = s[1..];
        }

        if (s.Length > 0 && (s[^1] == '-' || s[^1] == '−'))
        {
            negative = true;
            markers++;
            s = s[..^1];
        }

        if (markers > 1 || s.Length == 0)
            return false;

        foreach (var ch in s)
        {
            if (!char.IsAsciiDigit(ch) && ch != '.' && ch != ',')
                return false;
        }

        if (!TrySplit(s, out var integerPart, out var fraction))
            return false;

        var normalized = fraction.Length > 0 ? $"{integerPart}.{fraction}" : integerPart;
        if (!double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            return false;
        }

        value = negative ? -parsed : parsed;
        return true;
    }

    private static string StripNoise(string raw)
    {
        var s = raw.Trim();
        s = s.Replace("AR$", string.Empty, StringComparison.OrdinalIgnoreCase)
             .Replace("ARS", string.Empty, StringComparison.OrdinalIgnoreCase)
             .Replace("$", string.Empty, StringComparison.Ordinal);

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    // Splits a digits-and-separators body into its integer digits and fraction digits, resolving
    // which separator is the decimal one. False when the layout is not a single well-formed amount.
    private static bool TrySplit(string s, out string integerPart, out string fraction)
    {
        integerPart = string.Empty;
        fraction    = string.Empty;

        var dots   = s.Count(ch => ch == '.');
        var commas = s.Count(ch => ch == ',');

        char? decimalSeparator = null;
        char? groupSeparator   = null;

        if (dots > 0 && commas > 0)
        {
            decimalSeparator = s.LastIndexOf('.') > s.LastIndexOf(',') ? '.' : ',';
            groupSeparator   = decimalSeparator == '.' ? ',' : '.';
            if ((decimalSeparator == '.' ? dots : commas) > 1)
                return false;
        }
        else if (dots > 1)
        {
            groupSeparator = '.';
        }
        else if (dots == 1)
        {
            var dot = s.IndexOf('.');
            var looksGrouped = s.Length - dot - 1 == 3 && dot is >= 1 and <= 3 && s[0] != '0';
            if (looksGrouped)
                groupSeparator = '.';
            else
                decimalSeparator = '.';
        }
        else if (commas > 1)
        {
            groupSeparator = ',';
        }
        else if (commas == 1)
        {
            decimalSeparator = ',';
        }

        var integerText = s;
        if (decimalSeparator is { } dec)
        {
            var at = s.LastIndexOf(dec);
            integerText = s[..at];
            fraction    = s[(at + 1)..];
            if (fraction.Length == 0 || fraction.Any(ch => !char.IsAsciiDigit(ch)))
                return false;
        }

        if (groupSeparator is { } group)
        {
            var groups = integerText.Split(group);
            if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3))
                return false;

            integerText = string.Concat(groups);
        }

        if (integerText.Length == 0 || integerText.Any(ch => !char.IsAsciiDigit(ch)))
            return false;

        integerPart = integerText;
        return true;
    }
}
