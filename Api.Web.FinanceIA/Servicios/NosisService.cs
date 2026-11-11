// Project : Api.Web.FinanceIA
// Created : 2026-08-24

using System.Diagnostics;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Json;
using Api.Web.FinanceIA.Models.Nosis;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Consumes the NOSIS WS.01 variables API and caches the results.
/// Uses named HttpClient "NosisClient" (registered in Program.cs); the absolute endpoint comes from
/// <c>Nosis:BaseUrl</c> so it stays configurable per environment. Authentication is a single
/// <c>X-API-KEY</c> header — no login POST, no token cache, no 401 retry (contrast CASFOG).
/// All public methods are fault-isolated: every failure path returns a non-null
/// <see cref="NosisData"/> with <see cref="NosisConsultaEstado.LookupFailed"/> — no exception ever
/// escapes except genuine caller cancellation. The API key is never logged.
/// </summary>
public sealed class NosisService : INosisService
{
    private const string HttpClientName = "NosisClient";
    private const string ApiKeyHeader   = "X-API-KEY";

    /// <summary>Endpoint label recorded in [dbo].[ApiCallLogs].Endpoint — never the full query (it carries the CUIT).</summary>
    private const string EndpointLabel = "rest/variables";

    private const string CacheKeyDataPrefix = "nosis:";

    private const string  ConfigBaseUrlKey = "Nosis:BaseUrl";
    internal const string ConfigApiKeyKey  = "Nosis:ApiKey";
    internal const string ConfigVrKey      = "Nosis:VR";

    /// <summary>
    /// VR group for a FIADOR lookup. Optional: when absent or blank the fiador lookup falls back to
    /// <see cref="ConfigVrKey"/>, so shipping this key empty changes nothing until the group number
    /// is confirmed with the vendor.
    /// </summary>
    private const string ConfigVrFiadorKey = "Nosis:VrFiador";

    internal const string ConfigEnabledKey = "Nosis:Enabled";
    private const string  DefaultBaseUrl   = "https://ws01.nosis.com/rest/variables";

    /// <summary>Vendor outcome code meaning OK, per the WS.01 spec.</summary>
    private const string ResultadoEstadoOk = "200";

    private static readonly TimeSpan DataTtl = TimeSpan.FromHours(24);

    /// <remarks>
    /// The two tolerance settings cover OPPOSITE directions and neither substitutes for the other.
    /// <c>AllowReadingFromString</c> lets a numeric property read a JSON string; the
    /// <see cref="FlexibleStringConverter"/> lets a string property read a JSON number. NOSIS needs
    /// both: it sends <c>Resultado.Estado</c> as a bare <c>200</c> — unquoted — which aborted the
    /// entire document until the converter was added, turning a healthy response into a 502.
    /// Registered on the options rather than per property so it also covers the variable nodes
    /// (Nombre / Valor / Tipo / FechaAct), whose typing is just as loose and which the projection
    /// cannot defend — that runs only AFTER deserialization has already succeeded.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling              = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        Converters                  = { new FlexibleStringConverter() }
    };

    private readonly IHttpClientFactory      _httpClientFactory;
    private readonly IMemoryCache            _cache;
    private readonly IApiCallLogService      _apiCallLogService;
    private readonly ILogger<NosisService>   _logger;
    private readonly IConfiguration          _configuration;

    /// <summary>
    /// Initializes a new instance of <see cref="NosisService"/>.
    /// </summary>
    public NosisService(
        IHttpClientFactory    httpClientFactory,
        IMemoryCache          cache,
        IApiCallLogService    apiCallLogService,
        ILogger<NosisService> logger,
        IConfiguration        configuration)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _apiCallLogService = apiCallLogService;
        _logger            = logger;
        _configuration     = configuration;
    }

    // ── INosisService ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<NosisData> GetVariablesAsync(
        string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
    {
        // The gate runs BEFORE the cache read, unlike the pre-fiador shape, because the cache key
        // now depends on the resolved VR and there is no key to look under until it is known.
        // Resolution itself is pure configuration reads — no network, no allocation worth caring
        // about — so nothing is lost by doing it first.
        try
        {
            // ── Gate ──────────────────────────────────────────────────────────
            // The integration ships dark: without Nosis:Enabled, an API key and a VR group there is
            // nothing to call, so degrade to LookupFailed WITHOUT touching the network. LookupFailed
            // (rather than NoDataReported) is deliberate — "we did not ask" must never be persisted
            // as "NOSIS has nothing on this CUIT".
            if (!IsEnabled())
            {
                _logger.LogInformation(
                    "NOSIS is disabled ({ConfigKey} is not true); skipping the lookup for this request.",
                    ConfigEnabledKey);
                return BuildEmpty(NosisConsultaEstado.LookupFailed);
            }

            var apiKey = _configuration[ConfigApiKeyKey];
            var vr     = ResolveVr(grupo);
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(vr))
            {
                _logger.LogWarning(
                    "NOSIS is enabled but not configured for group {Grupo} ({ApiKeyKey} / {VrKey}); cannot query.",
                    grupo, ConfigApiKeyKey, ConfigVrKey);
                return BuildEmpty(NosisConsultaEstado.LookupFailed);
            }

            // Keyed on the RESOLVED vr, not on the group: once the two groups differ a principal and
            // a fiador lookup of the same CUIT can never collide, and while Nosis:VrFiador is unset
            // both resolve to the same query and share one cached answer rather than billing twice.
            var cacheKey = CacheKeyDataPrefix + vr + ":" + cuit;

            if (_cache.TryGetValue(cacheKey, out NosisData? cached) && cached is not null)
                return cached;

            // ── Call ──────────────────────────────────────────────────────────
            var (status, body) = await FetchVariablesAsync(tenantId, cuit, apiKey, vr, ct).ConfigureAwait(false);

            if (status is not 200 || body is null)
                return BuildEmpty(NosisConsultaEstado.LookupFailed);

            NosisApiResponse? apiResponse;
            try
            {
                apiResponse = JsonSerializer.Deserialize<NosisApiResponse>(body, JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "NOSIS variables response parse error for CUIT {Cuit}", cuit);
                return BuildEmpty(NosisConsultaEstado.LookupFailed);
            }

            // A vendor Resultado.Estado other than 200 is an error from NOSIS, not an empty answer:
            // treat it as transient so it is neither cached nor persisted over good stored data.
            var resultado = apiResponse?.Contenido?.Resultado;
            if (resultado is null || !IsResultadoOk(resultado.Estado))
            {
                _logger.LogWarning(
                    "NOSIS returned Resultado.Estado {Estado} for CUIT {Cuit}; treating as a failed lookup.",
                    resultado?.Estado ?? "(absent)", cuit);
                return BuildEmpty(NosisConsultaEstado.LookupFailed);
            }

            var variables = apiResponse?.Contenido?.Datos?.Variables;
            var data = NosisVariableProjection.BuildFromVariables(
                variables, NosisConsultaEstado.DataFound, DateTime.UtcNow);

            // 200 with no usable variable is a valid "nothing on file" answer — cacheable, and
            // distinct from a failure.
            if (!NosisVariableProjection.HasContent(data))
                data.Estado = NosisConsultaEstado.NoDataReported;

            _cache.Set(cacheKey, data, DataTtl);
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
            // for any reason other than caller cancellation. Any unexpected failure (mapping,
            // client creation, malformed data) degrades to a LookupFailed result.
            _logger.LogError(ex, "NOSIS variables unexpected failure for CUIT {Cuit}", cuit);
            return BuildEmpty(NosisConsultaEstado.LookupFailed);
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private bool IsEnabled()
        // GetValue<bool> would silently read a missing key as false, which is the behaviour we want
        // here: absent means off. An explicit non-boolean value also reads as off rather than throwing.
        => _configuration.GetValue<bool>(ConfigEnabledKey);

    /// <summary>
    /// Resolves a <see cref="NosisVariableGroup"/> to the configured VR number to ask for.
    /// <c>Fiador</c> prefers <see cref="ConfigVrFiadorKey"/> and falls back to
    /// <see cref="ConfigVrKey"/> when it is absent or blank, so the fiador path keeps working —
    /// against the principal group, which is the honest degradation — until the vendor confirms the
    /// fiador group number. The fallback is logged at Debug rather than Warning: it is the expected
    /// state for now, and a per-call Warning would drown the log for something already visible in
    /// configuration. Returns null/blank only when neither key is set, which the caller's gate
    /// turns into LookupFailed.
    /// </summary>
    private string? ResolveVr(NosisVariableGroup grupo)
    {
        if (grupo is not NosisVariableGroup.Fiador)
            return _configuration[ConfigVrKey];

        var vrFiador = _configuration[ConfigVrFiadorKey];
        if (!string.IsNullOrWhiteSpace(vrFiador))
            return vrFiador;

        _logger.LogDebug(
            "{VrFiadorKey} is not configured; the fiador lookup falls back to {VrKey}.",
            ConfigVrFiadorKey, ConfigVrKey);

        return _configuration[ConfigVrKey];
    }

    private static bool IsResultadoOk(string? estado)
        => string.Equals(estado?.Trim(), ResultadoEstadoOk, StringComparison.Ordinal);

    /// <summary>
    /// Executes the single NOSIS GET. Returns <c>(status, body)</c> on a completed round-trip and
    /// <c>(null, null)</c> on a transport failure — never throws except on caller cancellation.
    /// </summary>
    private async Task<(int? StatusCode, string? Body)> FetchVariablesAsync(
        string tenantId, long cuit, string apiKey, string vr, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int?   status = null;
        string? error = null;

        try
        {
            var baseUrl = _configuration[ConfigBaseUrlKey];
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = DefaultBaseUrl;

            // Format=JSON is MANDATORY — the service defaults to XML. FEX=TODAS refreshes the
            // external sources. The query carries the CUIT, so it is never written to a log.
            var url = $"{baseUrl}?Documento={cuit}&VR={Uri.EscapeDataString(vr)}&Format=JSON&FEX=TODAS";

            var client  = _httpClientFactory.CreateClient(HttpClientName);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(ApiKeyHeader, apiKey);   // never logged

            var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NOSIS API returned {StatusCode} for CUIT {Cuit}", status, cuit);
                error = $"HTTP {status}";
                return (status, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (status, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation — propagate; no api-call log row for an aborted request.
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NOSIS API network error for CUIT {Cuit}", cuit);
            error = ex.Message;
            return (null, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Client-side timeout — the caller's token is NOT cancelled.
            _logger.LogWarning(ex, "NOSIS API timeout for CUIT {Cuit}", cuit);
            error = "Timeout";
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NOSIS API unexpected error for CUIT {Cuit}", cuit);
            error = ex.Message;
            return (null, null);
        }
        finally
        {
            sw.Stop();
            LogApiCall(tenantId, status, error, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Fire-and-forget api-call log, mirroring <see cref="LufeService"/>. Requires BOTH halves of
    /// docs/database/043_apicallogs_fix_tenantid_and_add_services.sql to have run: 'NOSIS' must be in
    /// CK_ApiCallLogs_Service, and ApiCallLogs.TenantId must be NVARCHAR — it is UNIQUEIDENTIFIER in
    /// the base schema, which rejects the Firebase tenant slugs this application actually writes.
    /// Until then every row is rejected and swallowed by ApiCallLogService's catch-all; the lookup
    /// itself is unaffected either way.
    /// </summary>
    private void LogApiCall(string tenantId, int? status, string? error, int durationMs)
    {
        var isSuccess = status == 200;

        _ = _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "NOSIS",
            endpoint:         EndpointLabel,
            responseStatus:   status ?? 502,
            inputTokens:      null,
            outputTokens:     null,
            estimatedCostUsd: null,
            isSuccess:        isSuccess,
            errorMessage:     isSuccess ? null : (error ?? "No data or fetch failed"),
            durationMs:       durationMs,
            ct:               CancellationToken.None);
    }

    private static NosisData BuildEmpty(NosisConsultaEstado estado)
        => new()
        {
            Estado    = estado,
            FetchedAt = DateTime.UtcNow
        };
}
