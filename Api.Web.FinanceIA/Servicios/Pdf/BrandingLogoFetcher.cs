// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Buffers;
using Api.Web.FinanceIA.Models.Financia;
using iText.IO.Image;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Default <see cref="IBrandingLogoFetcher"/>. Singleton: it holds no tenant state beyond the shared
/// <see cref="IMemoryCache"/>, and the bytes it caches are never written after validation.
/// </summary>
/// <remarks>
/// The checks, in order: https, port 443, no user info (no request is made otherwise); the connect policy on the
/// named client refuses non-public addresses; a 3xx is a refusal (redirects are never followed); the body is read
/// as a stream and abandoned past 1 MB; the bytes must be PNG or JPEG by magic number (the Content-Type is
/// ignored); at most 4000 px a side; and iText must decode them, so a broken file is refused here and never fails a
/// render. One 5-second budget covers the whole fetch, headers and body.
/// </remarks>
public sealed class BrandingLogoFetcher : IBrandingLogoFetcher
{
    /// <summary>The named client registered by <see cref="PdfBrandingServiceRegistration"/>.</summary>
    public const string HttpClientName = "BrandingLogoClient";

    internal const int MaxBytes = 1024 * 1024;
    internal const int MaxSide  = 4000;

    internal static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(6);
    internal static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory           _httpClientFactory;
    private readonly IMemoryCache                 _cache;
    private readonly ILogger<BrandingLogoFetcher> _logger;

    /// <summary>Initializes a new instance of <see cref="BrandingLogoFetcher"/>.</summary>
    public BrandingLogoFetcher(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<BrandingLogoFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _logger            = logger;
    }

    /// <summary>
    /// The budget for one whole fetch (headers and body). A seam for tests only; production keeps 5 seconds.
    /// </summary>
    internal TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<PdfBrandLogo?> GetAsync(string tenantId, Uri url, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(url);

        string cacheKey = string.Concat("pdf-brand-logo:", tenantId, ":", url.AbsoluteUri);

        if (_cache.TryGetValue(cacheKey, out Outcome? cached) && cached is not null)
        {
            if (cached.Logo is null)
            {
                _logger.LogDebug(
                    "PDF branding logo for tenant {TenantId} host {LogoHost} still refused ({Reason}); cached",
                    tenantId, url.IdnHost, cached.FailureReason);
            }

            return cached.Logo;
        }

        Outcome outcome = await FetchAsync(url, ct).ConfigureAwait(false);

        if (outcome.Logo is null)
        {
            _logger.LogWarning(
                "Ignoring TenantSettings {SettingKey} for tenant {TenantId}: logo refused ({Reason}) from host {LogoHost}; the PDF keeps the Financia header",
                TenantSettingKeys.BrandingLogoUrl, tenantId, outcome.FailureReason, url.IdnHost);
        }

        _cache.Set(cacheKey, outcome, outcome.Logo is null ? FailureCacheDuration : SuccessCacheDuration);
        return outcome.Logo;
    }

    private async Task<Outcome> FetchAsync(Uri url, CancellationToken ct)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            return Outcome.Refused(BrandingLogoFailure.Scheme);

        if (url.Port != 443)
            return Outcome.Refused(BrandingLogoFailure.Port);

        if (!string.IsNullOrEmpty(url.UserInfo))
            return Outcome.Refused(BrandingLogoFailure.UserInfo);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(FetchTimeout);

        byte[] bytes;
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                return Outcome.Refused(BrandingLogoFailure.Redirect);

            if (!response.IsSuccessStatusCode)
                return Outcome.Refused(BrandingLogoFailure.HttpStatus);

            if (response.Content.Headers.ContentLength is > MaxBytes)
                return Outcome.Refused(BrandingLogoFailure.Oversize);

            Stream body = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                byte[]? read = await ReadCappedAsync(body, budget.Token).ConfigureAwait(false);
                if (read is null)
                    return Outcome.Refused(BrandingLogoFailure.Oversize);

                bytes = read;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the export itself was cancelled: not a logo outcome, so nothing is cached
        }
        catch (OperationCanceledException)
        {
            return Outcome.Refused(BrandingLogoFailure.Timeout);   // our budget (or the client's 5 s timeout)
        }
        catch (Exception ex) when (IsBlockedAddress(ex))
        {
            return Outcome.Refused(BrandingLogoFailure.BlockedAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Outcome.Refused(BrandingLogoFailure.Network);
        }

        LogoImageProbe.Probe probe = LogoImageProbe.TryProbe(bytes, MaxSide);
        if (probe.Format is not { } format)
            return Outcome.Refused(probe.FailureReason ?? BrandingLogoFailure.Format);

        try
        {
            // Decode once here so a file whose header passes but whose data does not is refused (and cached as
            // refused) instead of failing a render. The ImageData is thrown away: each export builds its own.
            _ = ImageDataFactory.Create(bytes);
        }
        catch (Exception)
        {
            return Outcome.Refused(BrandingLogoFailure.Undecodable);
        }

        return new Outcome(new PdfBrandLogo(bytes, probe.Width, probe.Height, format), null);
    }

    // Reads at most MaxBytes; null as soon as the body proves longer, without reading the rest.
    private static async Task<byte[]?> ReadCappedAsync(Stream body, CancellationToken ct)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                int n = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
                if (n == 0)
                    return buffer.ToArray();

                if (buffer.Length + n > MaxBytes)
                    return null;

                buffer.Write(chunk, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static bool IsBlockedAddress(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is BrandingLogoBlockedAddressException)
                return true;
        }

        return false;
    }

    // The cached value: a logo, or the reason it was refused.
    private sealed record Outcome(PdfBrandLogo? Logo, string? FailureReason)
    {
        internal static Outcome Refused(string reason) => new(null, reason);
    }
}
