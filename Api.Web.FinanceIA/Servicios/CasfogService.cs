// Project : Api.Web.FinanceIA
// Created : 2026-07-29
// Modified: 2026-07-29

using System.Globalization;
using System.Net;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Consumes the CASFOG (SGR central de deudores / garantías) API and caches the results.
/// The HTTP transport is an HttpWebRequest round-trip (see <see cref="ICasfogTransport"/>),
/// ported from the proven Api.Web.Dynamics365 client; the base URL comes from configuration.
/// Authentication is a login (email + password → bearer token) read from configuration;
/// the token is cached (<see cref="TokenTtl"/>) and refreshed once on a 401. All public
/// methods are fault-isolated: every failure path returns a non-null <see cref="CasfogData"/>
/// with <see cref="CasfogConsultaEstado.LookupFailed"/> and empty (non-null) collections —
/// no exception ever escapes. The password and token are never logged.
/// </summary>
public sealed class CasfogService : ICasfogService
{
    // Relative to Casfog:BaseUrl (which ends in .../api/v1.1/); no leading slash.
    private const string LoginPath                    = "login";
    private const string CentralDeudoresPathTemplate  = "central_deudores/{0}";

    private const string DashSentinel = "-";

    private const string CacheKeyToken      = "casfog:token";
    private const string CacheKeyDataPrefix = "casfog:";

    internal const string ConfigEmailKey    = "Casfog:Email";
    internal const string ConfigPasswordKey = "Casfog:Password";
    private const string  ConfigBaseUrlKey  = "Casfog:BaseUrl";
    private const string  DefaultBaseUrl    = "https://sgr.casfog.com.ar/api/v1.1/";

    // Conservative token TTL: the vendor does not document the token lifetime, so the
    // 401-driven refresh below is the real safety net; the TTL just avoids re-logging in
    // on every call.
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan DataTtl  = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling              = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly ICasfogTransport       _transport;
    private readonly IMemoryCache           _cache;
    private readonly ILogger<CasfogService> _logger;
    private readonly IConfiguration         _configuration;
    private readonly Uri                    _baseUri;

    /// <summary>
    /// Initializes a new instance of <see cref="CasfogService"/>.
    /// </summary>
    public CasfogService(
        ICasfogTransport       transport,
        IMemoryCache           cache,
        ILogger<CasfogService> logger,
        IConfiguration         configuration)
    {
        _transport     = transport;
        _cache         = cache;
        _logger        = logger;
        _configuration = configuration;

        // With HttpWebRequest there is no HttpClient.BaseAddress; resolve the base URL here and
        // compose absolute request URLs. The trailing slash is required so relative paths
        // (no leading slash) resolve under .../api/v1.1/ rather than the host root.
        var baseUrl = configuration[ConfigBaseUrlKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultBaseUrl;
        if (!baseUrl.EndsWith('/'))
            baseUrl += '/';
        _baseUri = new Uri(baseUrl, UriKind.Absolute);
    }

    // ── ICasfogService ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<CasfogData> GetCentralDeudoresAsync(long cuit, CancellationToken ct)
    {
        var cacheKey = CacheKeyDataPrefix + cuit;

        if (_cache.TryGetValue(cacheKey, out CasfogData? cached) && cached is not null)
            return cached;

        try
        {
            var token = await EnsureTokenAsync(ct).ConfigureAwait(false);
            if (token is null)
                return BuildEmpty(CasfogConsultaEstado.LookupFailed);

            var path = string.Format(CentralDeudoresPathTemplate, cuit);
            var (status, body) = await FetchCentralDeudoresAsync(path, token, ct).ConfigureAwait(false);

            // 401 → the cached token is stale/invalid: drop it, re-login once, and retry once.
            if (status == 401)
            {
                _logger.LogInformation("CASFOG central_deudores returned 401; refreshing token and retrying once.");
                _cache.Remove(CacheKeyToken);

                token = await EnsureTokenAsync(ct).ConfigureAwait(false);
                if (token is null)
                    return BuildEmpty(CasfogConsultaEstado.LookupFailed);

                (status, body) = await FetchCentralDeudoresAsync(path, token, ct).ConfigureAwait(false);
            }

            // Transport failure, non-success status (incl. an exhausted 401), or empty body.
            if (status is not 200 || body is null)
                return BuildEmpty(CasfogConsultaEstado.LookupFailed);

            CasfogCentralDeudoresApiResponse? apiResponse;
            try
            {
                apiResponse = JsonSerializer.Deserialize<CasfogCentralDeudoresApiResponse>(body, JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "CASFOG central_deudores response parse error for CUIT {Cuit}", cuit);
                return BuildEmpty(CasfogConsultaEstado.LookupFailed);
            }

            // An explicit vendor error / success:false / missing data block is a valid
            // "nothing on file" answer (NoDataReported), cacheable — distinct from a failure.
            if (apiResponse?.Data is null || IsFailureFlagged(apiResponse))
            {
                var noData = BuildEmpty(CasfogConsultaEstado.NoDataReported);
                _cache.Set(cacheKey, noData, DataTtl);
                return noData;
            }

            var data = MapCentralDeudores(apiResponse.Data);
            data.FetchedAt = DateTime.UtcNow;

            // No error but no content either → still "nothing on file".
            data.Estado = HasContent(data)
                ? CasfogConsultaEstado.DataFound
                : CasfogConsultaEstado.NoDataReported;

            _cache.Set(cacheKey, data, DataTtl);

            _logger.LogInformation(
                "CASFOG central_deudores fetched for CUIT {Cuit}: estado={Estado} sgrRows={SgrRows} certificados={Certs}",
                cuit, data.Estado, data.Garantias.PorSgr.Count, data.Certificados.Count);

            return data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation (aborted request) — propagate, do not swallow.
            throw;
        }
        catch (Exception ex)
        {
            // Fault-isolation safety net: the documented contract is that this method never throws
            // for any reason other than caller cancellation. Any unexpected failure (mapping/overflow,
            // client creation, malformed data, etc.) degrades to a LookupFailed result.
            _logger.LogError(ex, "CASFOG central_deudores unexpected failure for CUIT {Cuit}", cuit);
            return BuildEmpty(CasfogConsultaEstado.LookupFailed);
        }
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>Returns a cached bearer token, logging in if the cache is empty. Null on failure.</summary>
    private async Task<string?> EnsureTokenAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKeyToken, out string? token) && !string.IsNullOrEmpty(token))
            return token;

        return await LoginAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs in with the configured credentials and caches the returned token.
    /// Returns null (never throws) on any failure. The password and token are never logged.
    /// </summary>
    private async Task<string?> LoginAsync(CancellationToken ct)
    {
        var email    = _configuration[ConfigEmailKey];
        var password = _configuration[ConfigPasswordKey];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "CASFOG credentials are not configured (Casfog:Email / Casfog:Password); cannot log in.");
            return null;
        }

        try
        {
            var loginUrl = new Uri(_baseUri, LoginPath).ToString();
            // Body carries the password — never log this request.
            var json = JsonSerializer.Serialize(new { email, password });

            var (status, loginBody) = await _transport
                .SendAsync(new CasfogHttpRequest("POST", loginUrl, null, json), ct)
                .ConfigureAwait(false);

            if (status is < 200 or > 299 || loginBody is null)
            {
                _logger.LogWarning("CASFOG login returned {StatusCode}.", status);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<CasfogLoginApiResponse>(loginBody, JsonOpts);
            var token  = parsed?.Success?.Token;

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("CASFOG login succeeded but no token was returned.");
                return null;
            }

            _cache.Set(CacheKeyToken, token, TokenTtl);
            _logger.LogInformation("CASFOG login succeeded; token cached for {Minutes} min.", TokenTtl.TotalMinutes);
            return token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation (aborted request) — propagate, do not swallow.
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "CASFOG login response parse error.");
            return null;
        }
        catch (WebException ex)
        {
            _logger.LogWarning(ex, "CASFOG login network error.");
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Client-side timeout (request Timeout) — the caller's token is NOT cancelled.
            _logger.LogWarning(ex, "CASFOG login timeout.");
            return null;
        }
        catch (Exception ex)
        {
            // Fault-isolation safety net: never throw on an unexpected failure (a malformed URL,
            // an unexpected transport error, or a non-JsonException serializer error).
            _logger.LogError(ex, "CASFOG login unexpected error.");
            return null;
        }
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues the authenticated central_deudores GET. On a transport failure the status is
    /// <c>null</c>; on any non-success status the body is <c>null</c> (the status is preserved
    /// so the caller can special-case a 401). Never throws.
    /// </summary>
    private async Task<(int? StatusCode, string? Body)> FetchCentralDeudoresAsync(
        string path, string token, CancellationToken ct)
    {
        try
        {
            var url = new Uri(_baseUri, path).ToString();

            var (status, body) = await _transport
                .SendAsync(new CasfogHttpRequest("GET", url, token, null), ct)
                .ConfigureAwait(false);

            if (status is < 200 or > 299)
            {
                _logger.LogWarning("CASFOG API returned {StatusCode} for path {Path}", status, path);
                return (status, null);   // status preserved so the caller can special-case a 401
            }

            return (status, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation (aborted request) — propagate, do not swallow.
            throw;
        }
        catch (WebException ex)
        {
            _logger.LogWarning(ex, "CASFOG API network error for path {Path}", path);
            return (null, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Client-side timeout (request Timeout) — the caller's token is NOT cancelled.
            _logger.LogWarning(ex, "CASFOG API timeout for path {Path}", path);
            return (null, null);
        }
        catch (Exception ex)
        {
            // Fault-isolation safety net: never throw on an unexpected failure (a malformed URL,
            // an unexpected transport error, or a bad Authorization header value).
            _logger.LogError(ex, "CASFOG API unexpected error for path {Path}", path);
            return (null, null);
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static CasfogData MapCentralDeudores(CasfogDataNode d)
    {
        var data = new CasfogData();

        var g = d.Garantias;
        if (g is not null)
        {
            var m = g.Metricas;
            if (m is not null)
            {
                data.Garantias.CantidadGarantiasOtorgadas  = ParseInt(m.CantidadTotalGarantiasOtorgadas);
                data.Garantias.MontoGarantiasOtorgadas     = ParseDecimal(m.MontoTotalGarantiasOtorgadas);
                data.Garantias.CantidadGarantiasVigentes   = ParseInt(m.CantidadTotalGarantiasVigentes);
                data.Garantias.SaldoBrutoGarantiasVigentes = ParseDecimal(m.SaldoBrutoGarantiasVigentes);
            }

            foreach (var row in g.Data ?? Enumerable.Empty<CasfogGarantiaSgrNode>())
            {
                data.Garantias.PorSgr.Add(new CasfogGarantiaSgr
                {
                    Sgr                = ParseString(row.Sgr),
                    SaldoBrutoVigente  = ParseDecimal(row.SaldoBrutoVigente),
                    SaldoDeudaAbonadas = ParseDecimal(row.SaldoDeudaAbonadas),
                    CantidadEnMora     = ParseInt(row.CantidadEnMora),
                    SituacionDeuda     = ParseSituacion(row.SituacionDeuda),
                    DiasAtraso         = ParseInt(row.DiasAtraso)
                });
            }
        }

        foreach (var c in d.CertificadosPymes ?? Enumerable.Empty<CasfogCertificadoNode>())
        {
            data.Certificados.Add(new CasfogCertificadoPyme
            {
                Numero           = ParseString(c.Numero),
                FechaRegistro    = ParseDate(c.FechaRegistro),
                FechaVencimiento = ParseDate(c.FechaVencimiento),
                Sector           = ParseString(c.Sector),
                Categoria        = ParseString(c.Categoria)
            });
        }

        var gen = d.General;
        if (gen is not null)
        {
            data.Socios.SocioProtector = MapSocios(gen.SocioProtector);
            data.Socios.SocioParticipe = MapSocios(gen.SocioParticipe);
            data.Socios.CuitTerceros   = MapSocios(gen.CuitDeTerceros);
        }

        return data;
    }

    private static List<CasfogSocio> MapSocios(List<CasfogSocioNode>? nodes)
    {
        var list = new List<CasfogSocio>();
        foreach (var n in nodes ?? Enumerable.Empty<CasfogSocioNode>())
        {
            list.Add(new CasfogSocio
            {
                BusinessName = ParseString(n.BusinessName),
                Cuit         = ParseString(n.Cuit)
            });
        }
        return list;
    }

    // ── Tri-state helpers ─────────────────────────────────────────────────────

    /// <summary>True when the response carries a vendor error message or a falsey success flag.</summary>
    private static bool IsFailureFlagged(CasfogCentralDeudoresApiResponse resp)
    {
        if (!string.IsNullOrWhiteSpace(resp.Data?.Error?.Message))
            return true;

        var s = resp.Success;
        if (s is null)
            return false;

        return s.Value.ValueKind switch
        {
            JsonValueKind.False  => true,
            JsonValueKind.String => IsFalsey(s.Value.GetString()),
            JsonValueKind.Number => s.Value.TryGetDecimal(out var n) && n == 0m,
            _                    => false
        };
    }

    private static bool IsFalsey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var t = value.Trim();
        return t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0";
    }

    private static bool HasContent(CasfogData d)
        => d.Garantias.PorSgr.Count > 0
        || d.Certificados.Count > 0
        || d.Socios.SocioProtector.Count > 0
        || d.Socios.SocioParticipe.Count > 0
        || d.Socios.CuitTerceros.Count > 0
        || d.Garantias.CantidadGarantiasOtorgadas is not null
        || d.Garantias.MontoGarantiasOtorgadas is not null
        || d.Garantias.CantidadGarantiasVigentes is not null
        || d.Garantias.SaldoBrutoGarantiasVigentes is not null;

    private static CasfogData BuildEmpty(CasfogConsultaEstado estado)
        => new()
        {
            Estado       = estado,
            Garantias    = new CasfogGarantias(),
            Certificados = new(),
            Socios       = new CasfogSocios(),
            FetchedAt    = DateTime.UtcNow
        };

    // ── Defensive string parsing ──────────────────────────────────────────────
    // Every leaf arrives as a string, with "-" as the vendor's empty sentinel. Numbers
    // are parsed with the INVARIANT culture; the vendor's decimal/thousands format is
    // UNCONFIRMED (es-AR "1.234.567,89" vs invariant "1234567.89"). A field that does not
    // parse becomes null and never brings the whole response down.

    private static string? ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed == DashSentinel ? null : trimmed;
    }

    private static decimal? ParseDecimal(string? value)
    {
        var s = ParseString(value);
        if (s is null)
            return null;
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static int? ParseInt(string? value)
    {
        var s = ParseString(value);
        if (s is null)
            return null;
        // Tolerate a decimal part (e.g. "3.0") by parsing as decimal then truncating.
        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return null;
        // Out-of-range values map to null (documented per-field tolerance) rather than
        // overflowing the (int) cast — a single bad field must never fail the whole lookup.
        if (d > int.MaxValue || d < int.MinValue)
            return null;
        return (int)decimal.Truncate(d);
    }

    /// <summary>Parses the debt classification and caps it at 10 (vendor convention).</summary>
    private static int? ParseSituacion(string? value)
    {
        var n = ParseInt(value);
        if (n is null)
            return null;
        return n > 10 ? 10 : n;
    }

    private static DateTime? ParseDate(string? value)
    {
        var s = ParseString(value);
        if (s is null)
            return null;
        // DateTimeStyles.None: no implicit local/UTC shifting — the parsed value is kept as-is.
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;
    }
}
