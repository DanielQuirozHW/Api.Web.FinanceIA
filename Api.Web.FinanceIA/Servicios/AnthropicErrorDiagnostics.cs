using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// What an Anthropic Messages API error response says about itself, read from its error envelope
/// (<c>{"type":"error","error":{"type":"…","message":"…"},"request_id":"…"}</c>) and its
/// <c>request-id</c> header. Every field but <see cref="Status"/> is optional: a body that is not
/// JSON, or not that shape, simply leaves them null.
/// </summary>
/// <param name="Status">The HTTP status of the response.</param>
/// <param name="ErrorType">
/// <c>error.type</c> (e.g. <c>invalid_request_error</c>), only when it is a plain snake_case word of at
/// most <see cref="AnthropicErrorDiagnostics.MaxErrorTypeLength"/> characters.
/// </param>
/// <param name="Message">
/// <c>error.message</c>, control characters replaced, whitespace collapsed and clamped to
/// <see cref="AnthropicErrorDiagnostics.MaxMessageLength"/> characters.
/// </param>
/// <param name="RequestId">
/// The <c>request-id</c> header, or the body's <c>request_id</c> when the header is missing; only when it
/// is made of letters, digits, <c>_</c> and <c>-</c> (at most
/// <see cref="AnthropicErrorDiagnostics.MaxRequestIdLength"/>).
/// </param>
public sealed record AnthropicErrorInfo(int Status, string? ErrorType, string? Message, string? RequestId)
{
    /// <summary>
    /// <c>HTTP 400 invalid_request_error: &lt;message&gt; [req_…]</c>, or as much of it as is known, down to
    /// <c>HTTP 400</c>. Bounded (about 520 characters) and made only of the parsed parts: never the raw
    /// response body, never anything of the request.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder(64);
        text.Append(CultureInfo.InvariantCulture, $"HTTP {Status}");

        if (ErrorType is not null)
            text.Append(' ').Append(ErrorType);

        if (Message is not null)
            text.Append(ErrorType is null ? " " : ": ").Append(Message);

        if (RequestId is not null)
            text.Append(" [").Append(RequestId).Append(']');

        return text.ToString();
    }
}

/// <summary>
/// The one reader of an Anthropic error response, shared by every Claude call path (balance extraction
/// and its escalation, the balance-correction suggestion, the scan-orientation detector, the scoring
/// narrative, the analyst chat and the legajo passes) so that the reason of a 4xx reaches
/// <c>ApiCallLogs.ErrorMessage</c> and the logs the same way everywhere.
///
/// It keeps ONLY the error type, a clamped message and the request id. The request body (which carries
/// the document) is never involved, and the raw response body is never returned.
/// </summary>
public static class AnthropicErrorDiagnostics
{
    /// <summary>The longest <c>error.message</c> kept, in characters (an ellipsis marks a cut).</summary>
    public const int MaxMessageLength = 300;

    /// <summary>The longest <c>error.type</c> accepted.</summary>
    public const int MaxErrorTypeLength = 64;

    /// <summary>The longest request id accepted.</summary>
    public const int MaxRequestIdLength = 128;

    /// <summary>
    /// How much of an error body is read, in characters. An Anthropic error envelope is a few hundred
    /// bytes; anything longer is not one, and is not worth holding in memory.
    /// </summary>
    public const int MaxBodyChars = 64 * 1024;

    /// <summary>The response header Anthropic names each request with.</summary>
    public const string RequestIdHeader = "request-id";

    /// <summary>
    /// Reads at most <see cref="MaxBodyChars"/> of a non-success response's body and its
    /// <c>request-id</c> header, and parses them. A body that cannot be read is treated as absent: the
    /// status and the header are still reported. Only the caller's cancellation propagates.
    /// </summary>
    public static async Task<AnthropicErrorInfo> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        var    status = (int)response.StatusCode;
        string? requestId = response.Headers.TryGetValues(RequestIdHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        string? body = null;
        try
        {
            body = await ReadBoundedAsync(response.Content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                                       or InvalidOperationException or DecoderFallbackException)
        {
            body = null;
        }

        return Parse(status, body, requestId);
    }

    /// <summary>
    /// Parses an error response already read. Never throws: a body that is not JSON, or JSON of another
    /// shape, leaves <see cref="AnthropicErrorInfo.ErrorType"/> and <see cref="AnthropicErrorInfo.Message"/>
    /// null.
    /// </summary>
    public static AnthropicErrorInfo Parse(int status, string? body, string? requestIdHeader)
    {
        string? errorType     = null;
        string? message       = null;
        string? bodyRequestId = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            var text = body.Length > MaxBodyChars ? body[..MaxBodyChars] : body;
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var rootType)
                    && rootType.ValueKind == JsonValueKind.String
                    && rootType.ValueEquals("error"))
                {
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                            errorType = SanitizeErrorType(type.GetString());

                        if (error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                            message = SanitizeMessage(msg.GetString());
                    }

                    if (root.TryGetProperty("request_id", out var rid) && rid.ValueKind == JsonValueKind.String)
                        bodyRequestId = SanitizeRequestId(rid.GetString());
                }
            }
            catch (JsonException)
            {
                // Not JSON (an HTML gateway page, a truncated body): only the status and the header remain.
            }
        }

        var requestId = SanitizeRequestId(requestIdHeader) ?? bodyRequestId;
        return new AnthropicErrorInfo(status, errorType, message, requestId);
    }

    /// <summary>
    /// <paramref name="code"/> alone, or <c>"{code} | {detail}"</c> when there is a detail: the
    /// <c>ApiCallLogs.ErrorMessage</c> of the paths that store a short failure code. The code stays the
    /// prefix, so anything reading the code keeps reading it.
    /// </summary>
    public static string? WithDetail(string? code, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return code;

        return string.IsNullOrWhiteSpace(code) ? detail : $"{code} | {detail}";
    }

    internal static string? SanitizeErrorType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxErrorTypeLength)
            return null;

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or '_'))
                return null;
        }

        return value;
    }

    internal static string? SanitizeRequestId(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        value = value.Trim();
        if (value.Length is 0 or > MaxRequestIdLength)
            return null;

        foreach (var c in value)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return null;
        }

        return value;
    }

    internal static string? SanitizeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Control characters (incl. CR/LF) become spaces and whitespace runs collapse, so a message can
        // neither break a log line nor smuggle a fake one in. Built only up to the cap (+1 to know a cut
        // happened), so a huge message costs no more than a short one.
        var text = new StringBuilder(Math.Min(value.Length, MaxMessageLength + 1));
        var pendingSpace = false;
        foreach (var raw in value)
        {
            var isSpace = char.IsWhiteSpace(raw) || char.IsControl(raw);
            if (isSpace)
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            text.Append(raw);
            if (text.Length > MaxMessageLength)
                break;
        }

        if (text.Length == 0)
            return null;

        if (text.Length <= MaxMessageLength)
            return text.ToString();

        // Cut on the cap without splitting a surrogate pair, then mark the cut.
        var cut = MaxMessageLength;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;

        return text.ToString(0, cut).TrimEnd() + "…";
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var buffer = new char[MaxBodyChars];
        var read   = 0;
        while (read < buffer.Length)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return new string(buffer, 0, read);
    }
}
