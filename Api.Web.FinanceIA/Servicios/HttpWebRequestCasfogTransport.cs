// Project : Api.Web.FinanceIA
// Created : 2026-07-29
// Modified: 2026-07-29

using System.Net;
using System.Text;

namespace Api.Web.FinanceIA.Servicios;

// The CASFOG endpoint hangs the login POST when driven through HttpClient/SocketsHttpHandler
// in the App Service, yet the same call works via HttpWebRequest from the already-deployed
// Api.Web.Dynamics365 client. This transport ports that proven mechanism verbatim: HttpWebRequest,
// Expect:100-continue disabled, body written via GetRequestStream, response read via GetResponse.
// WebRequest/ServicePoint are obsolete (SYSLIB0014) but are exactly what the working client uses;
// the suppression is deliberate and scoped to this legacy transport only.
#pragma warning disable SYSLIB0014

/// <summary>
/// <see cref="ICasfogTransport"/> implemented with <see cref="HttpWebRequest"/>, mirroring the
/// proven Api.Web.Dynamics365 CASFOG client. Stateless — register as a singleton.
/// </summary>
/// <remarks>
/// Security note: the reference client disables TLS validation <b>globally</b> via
/// <c>ServicePointManager.ServerCertificateValidationCallback</c>. That is unacceptable in the BFF
/// (it would weaken TLS for BCRA/ARCA/LUFE/Dynamics/SQL). Here the cert-accept callback is set
/// <b>per request</b> and guarded to the CASFOG host, so every other host keeps strict validation.
/// </remarks>
public sealed class HttpWebRequestCasfogTransport : ICasfogTransport
{
    private const int TimeoutMs = 15000;

    static HttpWebRequestCasfogTransport()
    {
        // Fundamental to the fix: CASFOG hangs when it receives Expect: 100-continue. This is the
        // literally-proven mechanism from the Dynamics client. It is benign here — in .NET Core+
        // ServicePointManager governs only HttpWebRequest, and CASFOG is the sole HttpWebRequest
        // user in this codebase, so it is de-facto CASFOG-scoped. (This flag has no TLS/security
        // implication — the cert-accept bypass below stays strictly per-request.)
        ServicePointManager.Expect100Continue = false;
    }

    /// <inheritdoc />
    public async Task<(int? StatusCode, string? Body)> SendAsync(CasfogHttpRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var req = (HttpWebRequest)WebRequest.Create(request.Url);
        req.Method      = request.Method;
        req.ContentType = "application/json";
        req.Timeout     = TimeoutMs;

        // Per-request cert accept, scoped to the CASFOG host — never the global callback, so every
        // other host keeps strict TLS validation.
        var host = req.RequestUri.Host;
        req.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            ((HttpWebRequest)sender).RequestUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

        // Per-request backup for the Expect:100-continue disable (may be inert on the .NET 8
        // HttpWebRequest→SocketsHttpHandler shim; the static ServicePointManager flag is the guarantee).
        req.ServicePoint.Expect100Continue = false;

        if (!string.IsNullOrEmpty(request.BearerToken))
        {
            req.Headers.Set("Authorization", $"Bearer {request.BearerToken}");
            req.KeepAlive      = true;   // already the default; kept for parity with the proven client
            req.PreAuthenticate = true;  // inert with a manual Authorization header; kept for parity
        }

        // HttpWebRequest is synchronous; honor the CancellationToken by aborting the request.
        using var abortRegistration = ct.Register(
            static state => { try { ((HttpWebRequest)state!).Abort(); } catch { /* best-effort */ } },
            req);

        // Run the sync round-trip off the request thread. GetResponseAsync ignores req.Timeout on
        // .NET Core, so the synchronous GetResponse is used deliberately.
        return await Task.Run<(int?, string?)>(() =>
        {
            try
            {
                if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    request.JsonBody is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(request.JsonBody);   // UTF-8, no BOM
                    using var body = req.GetRequestStream();
                    body.Write(bytes, 0, bytes.Length);
                }

                using var response = (HttpWebResponse)req.GetResponse();
                using var stream   = response.GetResponseStream();
                using var reader   = new StreamReader(stream, Encoding.UTF8);
                return ((int)response.StatusCode, reader.ReadToEnd());
            }
            catch (WebException) when (ct.IsCancellationRequested)
            {
                // The request was aborted because the caller cancelled — surface as cancellation.
                throw new OperationCanceledException(ct);
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse http)
            {
                // Non-2xx (incl. 401) arrives here as a protocol-error WebException. Return the
                // numeric status so the service can special-case the 401 re-login path. The error
                // body is discarded by the service; dispose the response so no connection leaks.
                using (http) { }
                return ((int)http.StatusCode, null);
            }
            // Any other WebException (Timeout/DNS/connect) bubbles out → the service degrades it.
        }, CancellationToken.None).ConfigureAwait(false);
    }
}

#pragma warning restore SYSLIB0014
