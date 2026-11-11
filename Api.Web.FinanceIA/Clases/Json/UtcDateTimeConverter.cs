using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Clases.Json;

/// <summary>
/// Ensures every <see cref="DateTime"/> is serialized as UTC with the Z suffix.
/// SQL Server datetime2 returns Kind=Unspecified; this converter normalizes it.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dt = reader.GetDateTime();
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        writer.WriteStringValue(utc.ToString(Format, CultureInfo.InvariantCulture));
    }
}

public sealed class NullableUtcDateTimeConverter : JsonConverter<DateTime?>
{
    private static readonly UtcDateTimeConverter Inner = new();

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        return Inner.Read(ref reader, typeof(DateTime), options);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        Inner.Write(writer, value.Value, options);
    }
}
