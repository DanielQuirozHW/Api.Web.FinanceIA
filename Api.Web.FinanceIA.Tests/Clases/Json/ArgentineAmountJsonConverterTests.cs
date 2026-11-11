// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Text.Json;
using Api.Web.FinanceIA.Clases.Json;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases.Json;

/// <summary>
/// Pins how a monetary figure the extraction model quoted as a STRING is read. Before M11 a single
/// quoted figure failed the whole analysis (502); now it is parsed the way an Argentine statement
/// prints it, and anything unreadable becomes null and is reported, never thrown.
/// </summary>
public sealed class ArgentineAmountJsonConverterTests
{
    private sealed class Holder
    {
        public double? Amount { get; set; }
    }

    private static (double? Value, List<string> Unparseable) Read(string json)
    {
        var unparseable = new List<string>();
        var options = new JsonSerializerOptions { Converters = { new ArgentineAmountJsonConverter(unparseable.Add) } };
        var holder = JsonSerializer.Deserialize<Holder>($$"""{"Amount": {{json}}}""", options)!;
        return (holder.Amount, unparseable);
    }

    // ── TryParse ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("(1.234.567)",       -1234567d)]
    [InlineData("1.234.567-",        -1234567d)]
    [InlineData("-1.234.567",        -1234567d)]
    [InlineData("−5",           -5d)]
    [InlineData("-1.234.567,89",     -1234567.89d)]
    [InlineData("1.234.567,89",      1234567.89d)]
    [InlineData("$ 1.234",           1234d)]
    [InlineData("($ 43.247.190.286)", -43247190286d)]
    [InlineData("AR$ 1.234.567", 1234567d)]
    [InlineData("1234567.89",        1234567.89d)]
    [InlineData("1234567",           1234567d)]
    [InlineData("1,234,567",         1234567d)]
    [InlineData("1,234,567.89",      1234567.89d)]
    [InlineData("1,5",               1.5d)]
    [InlineData("0.500",             0.5d)]
    [InlineData("0",                 0d)]
    [InlineData("  (0)  ",           0d)]
    public void TryParse_ReadsStatementFormats(string raw, double expected)
    {
        Assert.True(ArgentineAmountJsonConverter.TryParse(raw, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("N/A")]
    [InlineData("abc")]
    [InlineData("(1.234-)")]
    [InlineData("--5")]
    [InlineData("1.23.4")]
    [InlineData("12.34,5.6")]
    [InlineData("1,234.567.8")]
    [InlineData("1e9")]
    public void TryParse_RejectsAmbiguousOrNonNumericText(string raw)
    {
        Assert.False(ArgentineAmountJsonConverter.TryParse(raw, out _));
    }

    // ── Converter ─────────────────────────────────────────────────────────────

    [Fact]
    public void Read_LossInParenthesesAsString_IsNegative()
    {
        var (value, unparseable) = Read("\"(1.234.567)\"");

        Assert.Equal(-1234567d, value);
        Assert.Empty(unparseable);
    }

    [Fact]
    public void Read_JsonNumber_PassesThrough()
    {
        var (value, unparseable) = Read("-43247190286.5");

        Assert.Equal(-43247190286.5d, value);
        Assert.Empty(unparseable);
    }

    [Fact]
    public void Read_Null_IsNullAndNotReported()
    {
        var (value, unparseable) = Read("null");

        Assert.Null(value);
        Assert.Empty(unparseable);
    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("\"-\"",   "-")]
    [InlineData("\"\"",    "")]
    [InlineData("true",    "true")]
    [InlineData("{\"x\": 1}", "{…}")]
    public void Read_Unparseable_IsNullAndReported(string json, string reported)
    {
        var (value, unparseable) = Read(json);

        Assert.Null(value);
        Assert.Equal(reported, Assert.Single(unparseable));
    }

    [Fact]
    public void Read_ObjectToken_IsSkippedWithoutCorruptingSiblings()
    {
        var options = new JsonSerializerOptions { Converters = { new ArgentineAmountJsonConverter() } };

        var holder = JsonSerializer.Deserialize<Dictionary<string, double?>>(
            """{"a": {"nested": [1, 2]}, "b": "1.000"}""", options)!;

        Assert.Null(holder["a"]);
        Assert.Equal(1000d, holder["b"]);
    }

    [Fact]
    public void Write_RoundTripsAsPlainNumber()
    {
        var options = new JsonSerializerOptions { Converters = { new ArgentineAmountJsonConverter() } };

        Assert.Equal("""{"Amount":-5.5}""", JsonSerializer.Serialize(new Holder { Amount = -5.5 }, options));
        Assert.Equal("""{"Amount":null}""", JsonSerializer.Serialize(new Holder(), options));
    }
}
