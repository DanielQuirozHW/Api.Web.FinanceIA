// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Net;
using System.Text;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="AnthropicErrorDiagnostics"/>: what an Anthropic error response is reduced to before it reaches
/// ClaudeApiException.DiagnosticMessage, ApiCallLogs.ErrorMessage and the logs — the status, the error type,
/// a clamped message and the request id, never more.
/// </summary>
public sealed class AnthropicErrorDiagnosticsTests
{
    private const string ValidBody =
        """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: 64000 > 32000, which is the maximum allowed"},"request_id":"req_body123"}""";

    [Fact]
    public void A_valid_body_gives_type_message_and_the_header_request_id()
    {
        var info = AnthropicErrorDiagnostics.Parse(400, ValidBody, "req_011CTest");

        Assert.Equal(400, info.Status);
        Assert.Equal("invalid_request_error", info.ErrorType);
        Assert.Equal("max_tokens: 64000 > 32000, which is the maximum allowed", info.Message);
        Assert.Equal("req_011CTest", info.RequestId);
        Assert.Equal(
            "HTTP 400 invalid_request_error: max_tokens: 64000 > 32000, which is the maximum allowed [req_011CTest]",
            info.ToString());
    }

    [Fact]
    public void Without_the_header_the_request_id_comes_from_the_body()
    {
        var info = AnthropicErrorDiagnostics.Parse(400, ValidBody, null);

        Assert.Equal("req_body123", info.RequestId);
    }

    [Fact]
    public void Without_any_request_id_there_is_no_bracket()
    {
        const string body = """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""";

        Assert.Equal("HTTP 529 overloaded_error: Overloaded", AnthropicErrorDiagnostics.Parse(529, body, null).ToString());
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"message":"no type here"}}""", null, "no type here", "HTTP 400 no type here")]
    [InlineData("""{"type":"error","error":{"type":"not_found_error"}}""", "not_found_error", null, "HTTP 400 not_found_error")]
    [InlineData("""{"type":"error","error":{}}""", null, null, "HTTP 400")]
    [InlineData("""{"type":"error"}""", null, null, "HTTP 400")]
    [InlineData("""{"type":"message","error":{"type":"x_error","message":"m"}}""", null, null, "HTTP 400")]
    [InlineData("""{"error":{"type":"x_error","message":"m"}}""", null, null, "HTTP 400")]
    [InlineData("""{"type":"error","error":{"type":42,"message":["m"]}}""", null, null, "HTTP 400")]
    [InlineData("""["type","error"]""", null, null, "HTTP 400")]
    public void Missing_or_misshaped_fields_are_simply_left_out(string body, string? type, string? message, string expected)
    {
        var info = AnthropicErrorDiagnostics.Parse(400, body, null);

        Assert.Equal(type, info.ErrorType);
        Assert.Equal(message, info.Message);
        Assert.Equal(expected, info.ToString());
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request")] // truncated
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_body_that_is_not_an_error_envelope_leaves_only_status_and_request_id(string? body)
    {
        var info = AnthropicErrorDiagnostics.Parse(502, body, "req_abc");

        Assert.Null(info.ErrorType);
        Assert.Null(info.Message);
        Assert.Equal("HTTP 502 [req_abc]", info.ToString());
        if (!string.IsNullOrWhiteSpace(body))
            Assert.DoesNotContain(body, info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_message_is_clamped_to_300_characters_and_marked()
    {
        var huge = new string('a', 10_000);
        var body = $$$"""{"type":"error","error":{"type":"invalid_request_error","message":"{{{huge}}}"}}""";

        var info = AnthropicErrorDiagnostics.Parse(400, body, "req_1");

        Assert.Equal(AnthropicErrorDiagnostics.MaxMessageLength + 1, info.Message!.Length);
        Assert.EndsWith("…", info.Message, StringComparison.Ordinal);
        Assert.StartsWith(new string('a', 300), info.Message, StringComparison.Ordinal);
        Assert.True(info.ToString().Length < 520);
    }

    [Fact]
    public void A_message_of_exactly_the_cap_is_not_marked()
    {
        var exact = new string('b', AnthropicErrorDiagnostics.MaxMessageLength);
        var body  = $$$"""{"type":"error","error":{"type":"api_error","message":"{{{exact}}}"}}""";

        Assert.Equal(exact, AnthropicErrorDiagnostics.Parse(500, body, null).Message);
    }

    [Fact]
    public void Control_characters_and_whitespace_runs_collapse_to_one_space()
    {
        const string body = """{"type":"error","error":{"type":"invalid_request_error","message":"  line one\r\nFAKE LOG LINE\t\tend\u0000 "}}""";

        var info = AnthropicErrorDiagnostics.Parse(400, body, null);

        Assert.Equal("line one FAKE LOG LINE end", info.Message);
        Assert.DoesNotContain('\n', info.ToString());
        Assert.DoesNotContain('\r', info.ToString());
    }

    [Theory]
    [InlineData("req with spaces")]
    [InlineData("req_<script>")]
    [InlineData("req;drop")]
    [InlineData("")]
    public void A_malformed_request_id_is_rejected(string requestId)
    {
        const string body = """{"type":"error","error":{"type":"api_error","message":"m"}}""";

        Assert.Null(AnthropicErrorDiagnostics.Parse(500, body, requestId).RequestId);
    }

    [Fact]
    public void An_overlong_request_id_or_error_type_is_rejected()
    {
        var longType = new string('x', AnthropicErrorDiagnostics.MaxErrorTypeLength + 1);
        var body     = $$$"""{"type":"error","error":{"type":"{{{longType}}}","message":"m"}}""";

        var info = AnthropicErrorDiagnostics.Parse(400, body, new string('r', AnthropicErrorDiagnostics.MaxRequestIdLength + 1));

        Assert.Null(info.ErrorType);
        Assert.Null(info.RequestId);
    }

    [Theory]
    [InlineData("Invalid_Request")]
    [InlineData("type-with-dash")]
    [InlineData("type with space")]
    public void An_error_type_outside_snake_case_is_rejected(string type)
    {
        var body = $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"m"}}""";

        Assert.Null(AnthropicErrorDiagnostics.Parse(400, body, null).ErrorType);
    }

    [Fact]
    public void Nothing_but_the_parsed_parts_ever_appears()
    {
        // A body with extra members (and a "document" one, as if echoed): only type, message and id survive.
        const string body =
            """{"type":"error","error":{"type":"invalid_request_error","message":"bad","param":"messages.0.content"},"echo":"CUIT 30-71234567-8 balance figures","request_id":"req_x"}""";

        var text = AnthropicErrorDiagnostics.Parse(400, body, null).ToString();

        Assert.Equal("HTTP 400 invalid_request_error: bad [req_x]", text);
        Assert.DoesNotContain("CUIT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("param", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_reads_the_body_and_the_request_id_header()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(ValidBody, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("request-id", "req_header");

        var info = await AnthropicErrorDiagnostics.ReadAsync(response, CancellationToken.None);

        Assert.Equal(400, info.Status);
        Assert.Equal("invalid_request_error", info.ErrorType);
        Assert.Equal("req_header", info.RequestId);
    }

    [Fact]
    public async Task ReadAsync_reads_no_more_than_the_bound()
    {
        // A huge body is not an error envelope: read up to the bound, it is not JSON, nothing is kept.
        var huge = "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"" + new string('z', 200_000) + "\"}}";
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(huge, Encoding.UTF8, "application/json")
        };

        var info = await AnthropicErrorDiagnostics.ReadAsync(response, CancellationToken.None);

        Assert.Equal("HTTP 500", info.ToString());
    }

    [Theory]
    [InlineData("claude_api_error", "HTTP 400 x", "claude_api_error | HTTP 400 x")]
    [InlineData("claude_api_error", null, "claude_api_error")]
    [InlineData("claude_api_error", " ", "claude_api_error")]
    [InlineData(null, "HTTP 400 x", "HTTP 400 x")]
    [InlineData(null, null, null)]
    public void WithDetail_keeps_the_code_as_the_prefix(string? code, string? detail, string? expected) =>
        Assert.Equal(expected, AnthropicErrorDiagnostics.WithDetail(code, detail));
}
