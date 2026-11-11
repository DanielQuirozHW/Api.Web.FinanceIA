// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Clases.Json;

/// <summary>
/// A field in a PATCH payload that distinguishes ABSENT from PRESENT-AND-NULL.
///
/// A nullable property alone cannot express this: <c>decimal? ThresholdValue</c> is null both when
/// the client omitted the key (leave it alone) and when the client sent <c>null</c> (clear it).
/// Those are different instructions, and for a rule threshold the difference is material — a
/// threshold can legitimately be cleared.
///
/// <see cref="IsSet"/> is false by default, so a field the payload never mentioned arrives as
/// <c>default(Patch&lt;T&gt;)</c> and is left unchanged. It is only true when the key was present,
/// whatever its value.
/// </summary>
/// <typeparam name="T">The patched field's type. Use a nullable type when null is meaningful.</typeparam>
[JsonConverter(typeof(PatchJsonConverterFactory))]
public readonly record struct Patch<T>
{
    /// <summary>True when the key was present in the payload, including when its value was null.</summary>
    public bool IsSet { get; }

    /// <summary>The value sent, or <c>default</c> when <see cref="IsSet"/> is false.</summary>
    public T? Value { get; }

    /// <summary>Creates a set field carrying <paramref name="value"/>.</summary>
    public Patch(T? value)
    {
        IsSet = true;
        Value = value;
    }

    /// <summary>
    /// Returns <see cref="Value"/> when the field was set, otherwise <paramref name="current"/>.
    /// This is the whole point of the type: it collapses "patch or keep" to one expression.
    /// </summary>
    public T? Or(T? current) => IsSet ? Value : current;
}

/// <summary>
/// Supplies a <see cref="JsonConverter"/> for any <see cref="Patch{T}"/>.
///
/// System.Text.Json invokes a property's converter ONLY for keys actually present in the payload,
/// which is what makes absent-vs-null detectable at all: an omitted key never reaches
/// <c>Read</c>, so the property keeps its <c>default</c> (<c>IsSet == false</c>).
/// </summary>
public sealed class PatchJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(Patch<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(PatchJsonConverter<>).MakeGenericType(valueType))!;
    }

    private sealed class PatchJsonConverter<T> : JsonConverter<Patch<T>>
    {
        // Without this, System.Text.Json intercepts a null token before the converter sees it and
        // throws, because Patch<T> is a non-nullable struct. Handling null here is precisely the
        // case this type exists for, so the null must be allowed through.
        public override bool HandleNull => true;

        public override Patch<T> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return new Patch<T>(default);

            return new Patch<T>(JsonSerializer.Deserialize<T>(ref reader, options));
        }

        public override void Write(Utf8JsonWriter writer, Patch<T> value, JsonSerializerOptions options)
        {
            // Patch<T> is a request-only type; it is never part of a response. Writing the value
            // (rather than throwing) keeps round-tripping in tests and logs harmless.
            if (!value.IsSet || value.Value is null)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
