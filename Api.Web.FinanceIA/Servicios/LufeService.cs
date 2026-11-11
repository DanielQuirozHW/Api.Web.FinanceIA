// Project : Api.Web.FinanceIA
// Created : 2026-06-14
// Modified: 2026-09-24

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Consumes the Lufe (Legajo Único de Facturación Electrónica) government API.
/// Uses named HttpClient "LufeClient" (base address configured in Program.cs).
/// The API key is per-tenant and injected per-request via the <c>apikey</c> header.
/// All public methods are fault-isolated: exceptions are caught, logged, and
/// <c>null</c> is returned so the caller's main flow is never interrupted.
/// </summary>
public sealed class LufeService : ILufeService
{
    private const int MaxPeriodSearchDepth = 6;

    /// <summary>Cap on how many recent periods to fetch historical indicators for (charting series).</summary>
    private const int MaxIndicatorHistoryDepth = 12;

    /// <summary>Cap, in chars, on how much of a non-2xx error body is read for logging.</summary>
    private const int MaxErrorBodyChars = 2048;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory   _httpClientFactory;
    private readonly IApiCallLogService   _apiCallLogService;
    private readonly ILogger<LufeService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="LufeService"/>.
    /// </summary>
    public LufeService(
        IHttpClientFactory   httpClientFactory,
        IApiCallLogService   apiCallLogService,
        ILogger<LufeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiCallLogService = apiCallLogService;
        _logger            = logger;
    }

    // ── ILufeService ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<LufeLookupResult> GetLufeDataAsync(
        string tenantId,
        long cuit,
        string apiKey,
        CancellationToken ct)
    {
        var entidadPath     = $"entidades/{cuit}";
        var autoridadesPath = $"entidades/{cuit}/autoridades";
        var indicadoresPath = $"entidades/{cuit}/indicadores";
        var documentosPath  = $"entidades/{cuit}/documentos";

        var periodosPath    = $"entidades/{cuit}/periodos";
        var postBalancePath = $"entidades/{cuit}/indicadorespostbalance";

        var entidadTask     = GetEntidadAsync(cuit, apiKey, ct);
        var autoridadesTask = GetAutoridadesAsync(cuit, apiKey, ct);
        var indicadoresTask = GetIndicadoresAsync(cuit, apiKey, ct);
        var documentosTask  = GetDocumentosAsync(cuit, apiKey, ct);
        var periodosTask    = GetPeriodosAsync(cuit, apiKey, ct);
        var postBalanceTask = GetVentasComprasMensualAsync(cuit, apiKey, ct);

        await Task.WhenAll(entidadTask, autoridadesTask, indicadoresTask, documentosTask, periodosTask,
                           postBalanceTask)
            .ConfigureAwait(false);

        var (entidad, entidadEstado, entidadStatus, entidadDurationMs) = entidadTask.Result;
        var autoridades = autoridadesTask.Result;
        var indicadores = indicadoresTask.Result;
        var documentos  = documentosTask.Result;
        var periodos    = periodosTask.Result;
        var postBalance = postBalanceTask.Result;

        // Log each call fire-and-forget, every row with what Lufe actually answered: the real
        // HTTP status (null when no response came back) and why the section is empty. The entidad
        // row describes the estado it drives; the other sections describe their own fetch.
        _ = _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "LUFE",
            endpoint:         entidadPath,
            responseStatus:   entidadStatus,
            inputTokens:      null,
            outputTokens:     null,
            estimatedCostUsd: null,
            isSuccess:        entidad is not null,
            errorMessage:     DescribeEntidadOutcome(entidadEstado, entidadStatus),
            durationMs:       entidadDurationMs,
            ct:               CancellationToken.None);

        LogSection(tenantId, autoridadesPath, autoridades);
        LogSection(tenantId, indicadoresPath, indicadores);
        LogSection(tenantId, documentosPath,  documentos);
        LogSection(tenantId, periodosPath,    periodos);
        LogSection(tenantId, postBalancePath, postBalance);

        // postBalance is deliberately NOT part of this guard: a post-balance-only success must not
        // resurrect an otherwise-dead snapshot, which would turn refresh-lufe's 502 into a 200 shell.
        // When the three core sections are all empty the estado splits on WHY the entidad call came
        // back empty: Lufe answering "no legajo" is a legitimate not-found, anything else is an
        // outage. A partial snapshot (entidad failed, another core section answered) is still
        // LegajoFound, so the caller's partial-snapshot guard keeps deciding what to persist.
        if (entidad is null && autoridades.Value is null && indicadores.Value is null)
        {
            if (entidadEstado == LufeConsultaEstado.NoLegajoReported)
            {
                _logger.LogInformation(
                    "Lufe has no legajo for CUIT {Cuit} tenant {TenantId}; nothing to store",
                    cuit, tenantId);
                return new LufeLookupResult(LufeConsultaEstado.NoLegajoReported, null);
            }

            _logger.LogWarning(
                "All Lufe calls failed for CUIT {Cuit}; LufeDataJson will be null",
                cuit);
            return new LufeLookupResult(LufeConsultaEstado.LookupFailed, null);
        }

        // Descending search for most recent balance period
        LufeDocumento[]? balanceDocs = null;

        if (periodos.Value is { Length: > 0 })
        {
            var sorted = periodos.Value.OrderByDescending(p => p).ToArray();
            var depth  = Math.Min(sorted.Length, MaxPeriodSearchDepth);

            for (int i = 0; i < depth; i++)
            {
                var periodo = sorted[i];
                var docs    = await GetDocumentosByPeriodoAsync(cuit, periodo, apiKey, ct).ConfigureAwait(false);

                LogSection(tenantId, $"entidades/{cuit}/periodos/{periodo}/documentos", docs);

                if (docs.Value is null) continue;

                if (docs.Value.Any(d => d.Nombre?.Contains("balance", StringComparison.OrdinalIgnoreCase) == true))
                {
                    balanceDocs = docs.Value;
                    break;
                }
            }
        }

        var mergedDocs = MergeDocumentos(documentos.Value, balanceDocs);

        // Historical indicators series — fetch the most recent N periods' indicators in parallel
        // (capped by MaxIndicatorHistoryDepth to keep refresh latency bounded). Each period is
        // fault-isolated: a null/parse-broken period is dropped, never aborting the rest.
        LufeIndicador[]? indicadoresHistoricos = null;

        if (periodos.Value is { Length: > 0 })
        {
            var histSlice = periodos.Value
                .OrderByDescending(p => p)
                .Take(MaxIndicatorHistoryDepth)
                .ToArray();

            var histTasks = histSlice.Select(async periodo =>
            {
                var ind = await GetIndicadoresByPeriodoAsync(cuit, periodo, apiKey, ct).ConfigureAwait(false);

                LogSection(tenantId, $"entidades/{cuit}/periodos/{periodo}/indicadores", ind);

                return ind.Value;
            });

            var histResults = await Task.WhenAll(histTasks).ConfigureAwait(false);

            // Chronological ascending (oldest → newest) is the natural order for charting evolution.
            var ordered = histResults
                .Where(i => i is not null)
                .Cast<LufeIndicador>()
                .OrderBy(i => i.Periodo)
                .ToArray();

            indicadoresHistoricos = ordered.Length > 0 ? ordered : null;
        }

        var data = new LufeData
        {
            Entidad               = entidad,
            Autoridades           = autoridades.Value,
            Indicadores           = indicadores.Value,
            Documentos            = mergedDocs,
            IndicadoresHistoricos = indicadoresHistoricos,
            VentasComprasMensual  = postBalance.Value,
            // A FAILED post-balance call is marked, so the caller can keep a stored series and a
            // reader can tell an outage from "Lufe answered with no monthly data" (left unmarked).
            VentasComprasMensualEstado = postBalance.Failed ? LufeVentasComprasEstados.LookupFailed : null
        };

        return new LufeLookupResult(LufeConsultaEstado.LegajoFound, data);
    }

    /// <inheritdoc />
    public async Task<string?> GetMipymeCategoriaAsync(
        string tenantId, long cuit, string apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (entidad, estado, statusCode, _) = await GetEntidadAsync(cuit, apiKey, ct).ConfigureAwait(false);
            sw.Stop();

            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          "LUFE",
                endpoint:         $"entidades/{cuit}",
                responseStatus:   statusCode,
                inputTokens:      null,
                outputTokens:     null,
                estimatedCostUsd: null,
                isSuccess:        entidad is not null,
                errorMessage:     DescribeEntidadOutcome(estado, statusCode),
                durationMs:       (int)sw.ElapsedMilliseconds,
                ct:               CancellationToken.None);

            return entidad?.CertificadoPyme?.Categoria;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GetMipymeCategoriaAsync failed for CUIT {Cuit} tenant {TenantId}", cuit, tenantId);
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the base entidades/{cuit} record and says why it is null when it is. Any 4xx other
    /// than 408/429 is Lufe answering that it holds no legajo for the CUIT, or that its PyME has
    /// not authorized sharing it with this entity (see <see cref="FetchWithStatusAsync"/> for why
    /// the status class, not the body, decides). A 200 with no real entity (empty body, JSON null,
    /// or Cuit 0) is read the same way. A non-empty 200 body that is not valid JSON cannot be read
    /// as "no legajo", so it stays LookupFailed like any transport failure. The HTTP status Lufe
    /// gave (null when no response came back) and the call's duration ride along for ApiCallLogs.
    /// </summary>
    private async Task<(LufeEntidad? Entidad, LufeConsultaEstado Estado, int? StatusCode, int DurationMs)>
        GetEntidadAsync(long cuit, string apiKey, CancellationToken ct)
    {
        var path  = $"entidades/{cuit}";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);

        switch (fetch.Outcome)
        {
            case LufeFetchOutcome.NoLegajo:
                _logger.LogInformation(
                    "Lufe entidades answered HTTP {StatusCode} for CUIT {Cuit}: no legajo or not shared with this entity",
                    fetch.StatusCode, cuit);
                return (null, LufeConsultaEstado.NoLegajoReported, fetch.StatusCode, fetch.DurationMs);

            case LufeFetchOutcome.Failed:
                return (null, LufeConsultaEstado.LookupFailed, fetch.StatusCode, fetch.DurationMs);
        }

        if (string.IsNullOrWhiteSpace(fetch.Body))
        {
            _logger.LogWarning("Lufe entidades returned an empty body for CUIT {Cuit}", cuit);
            return (null, LufeConsultaEstado.NoLegajoReported, fetch.StatusCode, fetch.DurationMs);
        }

        try
        {
            var entidad = JsonSerializer.Deserialize<LufeEntidad>(fetch.Body, JsonOpts);
            if (entidad is null || entidad.Cuit == 0)
            {
                _logger.LogWarning("Lufe entidades returned no valid entity for CUIT {Cuit}", cuit);
                return (null, LufeConsultaEstado.NoLegajoReported, fetch.StatusCode, fetch.DurationMs);
            }
            return (entidad, LufeConsultaEstado.LegajoFound, fetch.StatusCode, fetch.DurationMs);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lufe entidades response parse error for CUIT {Cuit}", cuit);
            return (null, LufeConsultaEstado.LookupFailed, fetch.StatusCode, fetch.DurationMs);
        }
    }

    /// <summary>
    /// ApiCallLogs error text for the entidad call: null when the entity was found, otherwise what
    /// Lufe actually answered, so the table no longer reports every miss as a generic failure.
    /// </summary>
    private static string? DescribeEntidadOutcome(LufeConsultaEstado estado, int? statusCode)
        => estado switch
        {
            LufeConsultaEstado.LegajoFound      => null,
            LufeConsultaEstado.NoLegajoReported => LufeCallOutcome.DescribeNoLegajo(statusCode),
            _ => statusCode is null
                ? "Lookup failed (network error or timeout)"
                : $"Lookup failed (HTTP {statusCode})"
        };

    /// <summary>
    /// Writes one fire-and-forget ApiCallLogs row for a sub-section call, carrying the HTTP status
    /// Lufe actually sent (null when no response came back) and why the section came back empty.
    /// </summary>
    private void LogSection<T>(string tenantId, string endpoint, LufeSection<T> section)
        where T : class
    {
        _ = _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "LUFE",
            endpoint:         endpoint,
            responseStatus:   section.StatusCode,
            inputTokens:      null,
            outputTokens:     null,
            estimatedCostUsd: null,
            isSuccess:        section.Value is not null,
            errorMessage:     section.ErrorMessage,
            durationMs:       section.DurationMs,
            ct:               CancellationToken.None);
    }

    private async Task<LufeSection<LufeAutoridad[]>> GetAutoridadesAsync(long cuit, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/autoridades";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<LufeAutoridad[]>(fetch);

        try
        {
            return Parsed(JsonSerializer.Deserialize<LufeAutoridad[]>(fetch.Body!, JsonOpts), fetch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lufe autoridades response parse error for CUIT {Cuit}", cuit);
            return ParseError<LufeAutoridad[]>(fetch);
        }
    }

    private async Task<LufeSection<LufeIndicador>> GetIndicadoresAsync(long cuit, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/indicadores";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<LufeIndicador>(fetch);

        try
        {
            return Parsed(JsonSerializer.Deserialize<LufeIndicador>(fetch.Body!, JsonOpts), fetch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lufe indicadores response parse error for CUIT {Cuit}", cuit);
            return ParseError<LufeIndicador>(fetch);
        }
    }

    private async Task<LufeSection<LufeDocumento[]>> GetDocumentosAsync(long cuit, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/documentos";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<LufeDocumento[]>(fetch);

        try
        {
            var resp = JsonSerializer.Deserialize<LufeDocumentosResponse>(fetch.Body!, JsonOpts);
            var all  = resp?.Documentos;
            if (all is null or { Length: 0 }) return Parsed<LufeDocumento[]>(null, fetch);

            // Deduplicate by nombre — keep entry with highest URL string (encodes periodo implicitly)
            var docs = all
                .GroupBy(d => d.Nombre)
                .Select(g => g.OrderByDescending(d => d.Url ?? "").First())
                .ToArray();

            // Propagate tanda period to each document so it survives JSON serialisation
            if (resp?.Periodo is { } tandaPeriodo)
                foreach (var d in docs)
                    d.Periodo = tandaPeriodo;

            return Parsed(docs, fetch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lufe documentos response parse error for CUIT {Cuit}", cuit);
            return ParseError<LufeDocumento[]>(fetch);
        }
    }

    private async Task<LufeSection<List<LufeVentaCompraMes>>> GetVentasComprasMensualAsync(
        long cuit, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/indicadorespostbalance";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<List<LufeVentaCompraMes>>(fetch);

        // Catches Exception, NOT just JsonException like its siblings — deliberate. They only
        // deserialize; this one also merges, and anything escaping here would fault the
        // Task.WhenAll in GetLufeDataAsync and take the WHOLE snapshot down (refresh-lufe has no
        // try/catch and would 500; entity create swallows it and stores no Lufe data at all).
        // Same reasoning as GetMipymeCategoriaAsync above. A broken post-balance response must
        // cost only its own section.
        try
        {
            var resp   = JsonSerializer.Deserialize<LufePostBalanceResponse>(fetch.Body!, JsonOpts);
            var merged = MergeVentasComprasMensual(resp);

            if (merged is null)
                _logger.LogWarning(
                    "Lufe indicadorespostbalance returned no usable monthly data for CUIT {Cuit}", cuit);

            return Parsed(merged, fetch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Lufe indicadorespostbalance response parse error for CUIT {Cuit}", cuit);
            return ParseError<List<LufeVentaCompraMes>>(fetch);
        }
    }

    /// <summary>
    /// Merges Lufe's four post-balance series ("YYYYMM" → monto) into a single per-month list,
    /// ascending by período.
    ///
    /// Takes the UNION of the periods present in ANY series — a period missing from one map, or
    /// present but null, yields null on that side. A period whose every value is null carries no
    /// information and is dropped. Returns null (never an empty list) when nothing survives,
    /// matching how IndicadoresHistoricos is built above.
    ///
    /// Ordering is ordinal, not cultural: "YYYYMM" is fixed-width, so ordinal string order IS
    /// chronological order — no DateTime, no culture, no parsing. Comparer&lt;string&gt;.Default
    /// would be CurrentCulture-sensitive, under which ignorable characters can make a malformed
    /// key sort differently per OS/locale.
    ///
    /// internal (not private) so the test project can exercise it directly via InternalsVisibleTo.
    /// </summary>
    internal static List<LufeVentaCompraMes>? MergeVentasComprasMensual(LufePostBalanceResponse? resp)
    {
        if (resp is null) return null;

        // SortedSet with an ordinal comparer gives dedup + ascending order in one step. A plain
        // Concat/ToDictionary would throw ArgumentException here: every period is present in all
        // four maps, so the union is 100% duplicates.
        var periodos = new SortedSet<string>(StringComparer.Ordinal);
        CollectPeriodos(periodos, resp.Ventas);
        CollectPeriodos(periodos, resp.Compras);
        CollectPeriodos(periodos, resp.Melo);
        CollectPeriodos(periodos, resp.Expo);

        if (periodos.Count == 0) return null;

        var merged = new List<LufeVentaCompraMes>(periodos.Count);

        foreach (var periodo in periodos)
        {
            var ventas  = ValueFor(resp.Ventas,  periodo);
            var compras = ValueFor(resp.Compras, periodo);
            var melo    = ValueFor(resp.Melo,    periodo);
            var expo    = ValueFor(resp.Expo,    periodo);

            // Drop only fully empty months. Generalises "omit when both sides are null" to the
            // four series: a month is never dropped while it still carries a figure.
            if (ventas is null && compras is null && melo is null && expo is null)
                continue;

            // Left-hand side is our vocabulary, right-hand side is the vendor's: melo/expo are
            // Lufe's abbreviations and stop at the transport DTO.
            merged.Add(new LufeVentaCompraMes
            {
                Periodo            = periodo,
                Ventas             = ventas,
                Compras            = compras,
                VentasMercadoLocal = melo,
                VentasExportacion  = expo
            });
        }

        return merged.Count > 0 ? merged : null;
    }

    private static void CollectPeriodos(SortedSet<string> into, Dictionary<string, decimal?>? map)
    {
        if (map is null) return;

        foreach (var key in map.Keys)
            if (IsYearMonth(key))
                into.Add(key);
    }

    private static decimal? ValueFor(Dictionary<string, decimal?>? map, string periodo) =>
        map is not null && map.TryGetValue(periodo, out var value) ? value : null;

    /// <summary>
    /// True when <paramref name="periodo"/> is a well-formed Lufe month key: exactly six ASCII
    /// digits, month 01-12, year 1990 or later. Validated lexically — no DateTime, no culture,
    /// no clock, so the result is deterministic and directly unit-testable.
    ///
    /// Deliberately NOT <see cref="Models.Lufe.LufePeriodo.ParseYear"/>: that helper mirrors the
    /// portal's parseLufePeriodo and accepts "2024", "202400" and date-like shapes by design,
    /// which makes it useless as a YYYYMM guard. This keeps a stray key ("total", a phantom
    /// "202613") from becoming a point on the portal's chart.
    /// </summary>
    internal static bool IsYearMonth(string? periodo)
    {
        if (periodo is not { Length: 6 }) return false;

        foreach (var c in periodo)
            if (c is < '0' or > '9')
                return false;

        var year  = ((periodo[0] - '0') * 1000) + ((periodo[1] - '0') * 100)
                    + ((periodo[2] - '0') * 10) + (periodo[3] - '0');
        var month = ((periodo[4] - '0') * 10) + (periodo[5] - '0');

        return year >= 1990 && month is >= 1 and <= 12;
    }

    private async Task<LufeSection<string[]>> GetPeriodosAsync(long cuit, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/periodos";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<string[]>(fetch);

        try
        {
            return Parsed(JsonSerializer.Deserialize<string[]>(fetch.Body!, JsonOpts), fetch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lufe periodos response parse error for CUIT {Cuit}", cuit);
            return ParseError<string[]>(fetch);
        }
    }

    private async Task<LufeSection<LufeDocumento[]>> GetDocumentosByPeriodoAsync(
        long cuit, string periodo, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/periodos/{periodo}/documentos";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<LufeDocumento[]>(fetch);

        try
        {
            var resp = JsonSerializer.Deserialize<LufeDocumentosResponse>(fetch.Body!, JsonOpts);
            var all  = resp?.Documentos;
            if (all is null or { Length: 0 }) return Parsed<LufeDocumento[]>(null, fetch);

            foreach (var d in all)
                d.Periodo = periodo;

            return Parsed(all, fetch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Lufe periodos/{Periodo}/documentos response parse error for CUIT {Cuit}",
                periodo, cuit);
            return ParseError<LufeDocumento[]>(fetch);
        }
    }

    private async Task<LufeSection<LufeIndicador>> GetIndicadoresByPeriodoAsync(
        long cuit, string periodo, string apiKey, CancellationToken ct)
    {
        var path = $"entidades/{cuit}/periodos/{periodo}/indicadores";
        var fetch = await FetchWithStatusAsync(path, apiKey, ct).ConfigureAwait(false);
        if (!HasBody(fetch)) return NoBody<LufeIndicador>(fetch);

        try
        {
            return Parsed(JsonSerializer.Deserialize<LufeIndicador>(fetch.Body!, JsonOpts), fetch);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Lufe periodos/{Periodo}/indicadores response parse error for CUIT {Cuit}",
                periodo, cuit);
            return ParseError<LufeIndicador>(fetch);
        }
    }

    private static LufeDocumento[]? MergeDocumentos(
        LufeDocumento[]? current, LufeDocumento[]? balance)
    {
        if (current is null && balance is null) return null;

        var seen   = new HashSet<(string?, string?)>();
        var merged = new List<LufeDocumento>();

        if (current != null)
            foreach (var d in current)
                if (seen.Add((d.Nombre, d.Url)))
                    merged.Add(d);

        if (balance != null)
            foreach (var d in balance)
                if (seen.Add((d.Nombre, d.Url)))
                    merged.Add(d);

        return merged.Count > 0 ? merged.ToArray() : null;
    }

    /// <inheritdoc />
    public async Task<LufeDocumentDownload?> DownloadDocumentAsync(
        string absoluteUrl, string apiKey, CancellationToken ct)
    {
        var client  = _httpClientFactory.CreateClient("LufeClient");
        var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        request.Headers.Add("apikey", apiKey);

        try
        {
            var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new LufeDocumentDownload(Found: false, Content: null, ContentType: null);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Lufe document download returned {StatusCode} for URL {Url}",
                    (int)response.StatusCode, absoluteUrl);
                return null;
            }

            var content     = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString()
                              ?? "application/octet-stream";

            return new LufeDocumentDownload(Found: true, Content: content, ContentType: contentType);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lufe document download network error for URL {Url}", absoluteUrl);
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lufe document download timeout for URL {Url}", absoluteUrl);
            return null;
        }
    }

    /// <summary>
    /// Sends one GET to Lufe and classifies the answer by HTTP status CLASS, never by body text:
    /// 2xx is Ok; any 4xx except 408 (Request Timeout) and 429 (Too Many Requests) is NoLegajo —
    /// Lufe answering that it holds no legajo for the CUIT, or that its PyME has not authorized
    /// sharing it with this entity; 408, 429, 5xx, a network error and a timeout are Failed, since
    /// they are transient and say nothing about the legajo.
    ///
    /// The body used to decide a 401: only one carrying the functional spec's "No se encontró
    /// ninguna Entidad para el CUIT enviado" read as no legajo. Verified in production (CUIT
    /// 30556565798), Lufe's 401 for a PyME with no legajo does NOT reliably carry that text, so a
    /// legitimate not-found surfaced as "servicio no disponible". The cost of the status-class rule
    /// is that a bad tenant API key also reads as no legajo; it stays visible because every non-2xx
    /// is logged once with its status, path and bounded body, and ApiCallLogs records the real
    /// status — a broken key shows up as 401s across every PyME of the tenant.
    ///
    /// The HTTP status (null when no response came back) and the call's duration are carried
    /// out. Caller cancellation still propagates.
    /// </summary>
    private async Task<LufeFetch> FetchWithStatusAsync(string path, string apiKey, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("LufeClient");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("apikey", apiKey);

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadBoundedBodyAsync(response, path, ct).ConfigureAwait(false);
                sw.Stop();

                if (LufeCallOutcome.IsNoLegajoStatus(statusCode))
                {
                    _logger.LogInformation(
                        "Lufe API answered HTTP {StatusCode} for path {Path}; read as no legajo " +
                        "(repeated 401s across every PyME would instead point at a bad tenant API key). Body: {Body}",
                        statusCode, path, errorBody);
                    return new LufeFetch(LufeFetchOutcome.NoLegajo, null, statusCode, (int)sw.ElapsedMilliseconds);
                }

                _logger.LogWarning(
                    "Lufe API returned HTTP {StatusCode} for path {Path}; lookup failed. Body: {Body}",
                    statusCode, path, errorBody);
                return new LufeFetch(LufeFetchOutcome.Failed, null, statusCode, (int)sw.ElapsedMilliseconds);
            }

            var body = await response.Content
                .ReadAsStringAsync(ct)
                .ConfigureAwait(false);
            sw.Stop();

            return new LufeFetch(LufeFetchOutcome.Ok, body, statusCode, (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lufe API network error for path {Path}", path);
            return new LufeFetch(LufeFetchOutcome.Failed, null, null, (int)sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lufe API timeout for path {Path}", path);
            return new LufeFetch(LufeFetchOutcome.Failed, null, null, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxErrorBodyChars"/> of an error response body. Never throws: a
    /// read failure yields null, only caller cancellation propagates.
    /// </summary>
    private async Task<string?> ReadBoundedBodyAsync(
        HttpResponseMessage response, string path, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(ct)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new char[MaxErrorBodyChars];
            var total  = 0;
            while (total < buffer.Length)
            {
                var read = await reader
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                    .ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }

            return new string(buffer, 0, total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lufe API error body could not be read for path {Path}", path);
            return null;
        }
    }

    /// <summary>True when the fetch produced a 2xx body worth parsing.</summary>
    private static bool HasBody(LufeFetch fetch)
        => fetch.Outcome == LufeFetchOutcome.Ok && !string.IsNullOrWhiteSpace(fetch.Body);

    /// <summary>
    /// A section with no body to parse, described by what Lufe answered: a 2xx with an empty body,
    /// a 4xx read as no data, or a failed fetch (with its status, or none when no response came back).
    /// </summary>
    private static LufeSection<T> NoBody<T>(LufeFetch fetch) where T : class
        => new(null, fetch.StatusCode, fetch.Outcome switch
        {
            LufeFetchOutcome.Ok       => LufeCallOutcome.DescribeEmptyAnswer(fetch.StatusCode),
            LufeFetchOutcome.NoLegajo => $"No data (HTTP {fetch.StatusCode})",
            _ => fetch.StatusCode is null
                ? "Fetch failed (network error or timeout)"
                : $"Fetch failed (HTTP {fetch.StatusCode})"
        }, fetch.DurationMs, Failed: fetch.Outcome == LufeFetchOutcome.Failed);

    /// <summary>A parsed 2xx section; a null value means the body carried no usable data.</summary>
    private static LufeSection<T> Parsed<T>(T? value, LufeFetch fetch) where T : class
        => new(value, fetch.StatusCode, value is null ? LufeCallOutcome.DescribeEmptyAnswer(fetch.StatusCode) : null, fetch.DurationMs);

    /// <summary>A 2xx section whose body did not deserialize.</summary>
    private static LufeSection<T> ParseError<T>(LufeFetch fetch) where T : class
        => new(null, fetch.StatusCode, "Parse error", fetch.DurationMs, Failed: true);

    private enum LufeFetchOutcome
    {
        Ok,
        NoLegajo,
        Failed
    }

    private readonly record struct LufeFetch(LufeFetchOutcome Outcome, string? Body, int? StatusCode, int DurationMs);

    /// <summary>
    /// One sub-section's result plus what its call actually answered, for ApiCallLogs: the HTTP
    /// status (null when no response came back), the error text (null on success) and the duration.
    /// <see cref="Failed"/> is true when the call FAILED — transport error, timeout, a non-2xx other
    /// than the no-data 4xx, or a body that did not parse — and false when Lufe answered, with data or
    /// without: a null <see cref="Value"/> alone does not tell the two apart.
    /// </summary>
    private readonly record struct LufeSection<T>(
        T? Value, int? StatusCode, string? ErrorMessage, int DurationMs, bool Failed = false)
        where T : class;
}
