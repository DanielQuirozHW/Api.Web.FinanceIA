// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Net;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// The logo "server" of the fetcher tests: an in-memory handler that answers with a scripted response and counts
/// the requests it saw. No socket is ever opened.
/// </summary>
internal sealed class FakeLogoServer : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _calls;

    internal FakeLogoServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    internal int Calls => Volatile.Read(ref _calls);

    internal static FakeLogoServer Serving(byte[] body, string contentType = "image/png", bool sendLength = true) =>
        new((_, _) => Task.FromResult(Ok(body, contentType, sendLength)));

    internal static FakeLogoServer Status(HttpStatusCode status, string? location = null) =>
        new((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            if (location is not null)
                response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        });

    internal static FakeLogoServer Hanging() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    internal static HttpResponseMessage Ok(byte[] body, string contentType, bool sendLength)
    {
        HttpContent content = sendLength
            ? new ByteArrayContent(body)
            : new StreamContent(new UnknownLengthStream(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (!sendLength)
            content.Headers.ContentLength = null;

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return _respond(request, cancellationToken);
    }

    /// <summary>A readable stream that does not report its length (a chunked body).</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> whose every client goes through one handler.</summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public string? LastName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastName = name;
        return new HttpClient(handler, disposeHandler: false);
    }
}
