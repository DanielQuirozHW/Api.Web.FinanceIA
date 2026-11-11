// Project : Api.Web.FinanceIA
// Created : 2026-08-27

using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Clases.Json;

/// <summary>
/// Reads a JSON value into a <see cref="string"/> whether the producer quoted it or not, for feeds
/// whose typing cannot be relied on.
///
/// The gap this closes: <c>JsonNumberHandling.AllowReadingFromString</c> — set on the NOSIS, CASFOG,
/// LUFE and BCRA option objects — is ONE-DIRECTIONAL. It lets a numeric CLR property read a JSON
/// STRING token; it does nothing for the reverse, so a bare JSON number landing in a
/// <c>string?</c> property still throws. NOSIS hit exactly that in production:
///
///     System.Text.Json.JsonException: The JSON value could not be converted to System.String.
///     Path: $.Contenido.Resultado.Estado
///     Cannot get the value of a token type 'Number' as a string.
///
/// One unquoted <c>200</c> aborted the whole document, so a healthy HTTP 200 with a full variable
/// set degraded to LookupFailed and the caller saw a 502.
///
/// Values always materialise as a string, so downstream string comparisons keep working unchanged
/// (<c>NosisService.IsResultadoOk</c> compares against the literal <c>"200"</c>).
///
/// Nothing here throws: an unexpected token yields null rather than failing the document, because
/// losing one field is always better than losing the whole response.
///
/// Not registered globally in <c>Program.cs</c> — MVC serialization must NOT start coercing numbers
/// into every string on the API surface. Attach it to the specific vendor's
/// <see cref="JsonSerializerOptions"/> instead.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                // Identical to the built-in string converter, so quoted values are untouched.
                return reader.GetString();

            case JsonTokenType.Number:
                // The RAW token text, deliberately not GetInt64()/GetDecimal(): it reproduces the
                // literal exactly as the vendor wrote it, so 200 reads as "200" — never "200.0",
                // never culture-formatted, and with no overflow branch to get wrong.
                return reader.HasValueSequence
                    ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                    : Encoding.UTF8.GetString(reader.ValueSpan);

            case JsonTokenType.Null:
                // Unreachable in practice: for a nullable reference type System.Text.Json handles
                // the null token itself and never calls Read (HandleNull is left false on purpose).
                // Kept so the mapping is complete on its face.
                return null;

            default:
                // Skip() is load-bearing. Returning from a StartObject/StartArray without consuming
                // the subtree leaves the reader INSIDE it, and the deserializer then reads that
                // subtree's members as siblings of this property — corrupting the rest of the
                // document instead of degrading this one field. Skip() advances to the matching end
                // token, and is a no-op for scalars.
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// These are read-only wire shapes, so this path is not exercised today. It writes a plain
    /// string rather than throwing, because a converter that throws on write is a landmine for the
    /// next person who serializes one of these models for a log or a snapshot.
    /// </remarks>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
