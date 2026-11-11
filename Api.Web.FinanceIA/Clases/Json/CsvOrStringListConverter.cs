// Project : Api.Web.FinanceIA
// Created : 2026-09-02

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Clases.Json;

/// <summary>
/// Reads a list of strings from EITHER a JSON array (<c>["a","b"]</c>) OR a single comma-separated
/// string (<c>"a,b"</c>), so one request property accepts both spellings.
///
/// <para>
/// It exists because the value it carries has two natural shapes on two sides of the same feature.
/// The Portal holds a chat-model allow-list as an array of checkbox states and will send an array;
/// the database stores it as one comma-separated column, which is also what an operator has in hand
/// when they copy a value out of SQL or a DDL script to replay it through the API. Refusing either
/// form would make a correct-looking request fail for a reason that is pure encoding, so the
/// endpoint takes both and normalises.
/// </para>
///
/// <para>
/// It NORMALISES ONLY, and validates nothing: entries are trimmed and blanks dropped, but whether
/// the resulting ids mean anything is the caller's business — <c>BillingController</c> checks them
/// against <c>ChatModelCatalog</c> so it can answer 400 naming the offending id. A converter that
/// silently discarded an unknown value would turn a typo into a quiet no-op.
/// </para>
///
/// <para>
/// A JSON <c>null</c> reads as null, which is how the request model expresses "field omitted, leave
/// it alone" — distinct from an empty list, which means "I am setting this to nothing" and which the
/// controller rejects. Writing always emits an array, so a response never round-trips as CSV.
/// </para>
///
/// <para>
/// Attached per-property with <c>[JsonConverter]</c>, never registered globally: coercing a string
/// into a list everywhere on the API surface would hide real type errors.
/// </para>
/// </summary>
public sealed class CsvOrStringListConverter : JsonConverter<IReadOnlyList<string>?>
{
    /// <inheritdoc />
    public override IReadOnlyList<string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
            {
                var raw = reader.GetString();

                // An explicitly empty or whitespace string is an empty LIST, not null. The caller
                // asked to set the field to nothing, and that is a decision the controller must be
                // able to see and refuse — collapsing it to null here would silently reinterpret it
                // as "field omitted" and apply a default the caller never asked for.
                if (string.IsNullOrWhiteSpace(raw))
                    return [];

                return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }

            case JsonTokenType.StartArray:
            {
                var values = new List<string>();

                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.EndArray:
                            return values;

                        case JsonTokenType.String:
                        {
                            var item = reader.GetString();

                            // Blank entries are dropped rather than preserved: ["", "x"] and ["x"]
                            // express the same intent, and keeping the blank would produce a
                            // validation error naming an empty model id, which reads as a bug.
                            if (!string.IsNullOrWhiteSpace(item))
                                values.Add(item.Trim());

                            break;
                        }

                        case JsonTokenType.Null:
                            // Same treatment as a blank string — an absent entry, not an error.
                            break;

                        default:
                            throw new JsonException(
                                $"Expected a string inside the array but found {reader.TokenType}.");
                    }
                }

                throw new JsonException("Unterminated JSON array.");
            }

            default:
                throw new JsonException(
                    $"Expected a string or an array of strings but found {reader.TokenType}.");
        }
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<string>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();

        foreach (var item in value)
            writer.WriteStringValue(item);

        writer.WriteEndArray();
    }
}
