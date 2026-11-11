// Project : Api.Web.FinanceIA
// Created : 2026-05-08
// Modified: 2026-10-19

using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Consumes public BCRA APIs and caches the results.
/// Uses two named HttpClients (see <see cref="BcraHttpClientRegistration"/>): "BcraClient" for the
/// estadísticas APIs and "BcraCentralDeudoresClient" for the Central de Deudores.
/// All public methods are fault-isolated: exceptions are caught, logged, and
/// <c>null</c> is returned so the caller's main flow is never interrupted.
/// <para>
/// Central de Deudores traffic is deliberately GENTLE (2026-10-19: the endpoint reset our
/// connections for minutes after bursts, consistent with a per-IP rate limit). One lookup sends its
/// legs one after another — Deudas, then Historicas only when Deudas gave a real answer (DebtFound or
/// NoDebtReported, never after a failed Deudas), then Cheques — never at the same time; every request
/// holds a permit from the process-wide
/// <see cref="IBcraCentralDeudoresGate"/>; the whole sequence, queueing included, shares one
/// <see cref="BcraOptions.CentralDeudoresLookupBudgetSeconds"/> budget; and a LookupFailed is cached
/// for <see cref="BcraOptions.FailureCacheSeconds"/> so repeated clicks during a penalty send nothing.
/// The estadísticas legs (macro, cambiario) keep their parallel requests and their own pipeline.
/// </para>
/// </summary>
public sealed class BcraService : IBcraService
{
    private const string DeudaPathTemplate        = "/centraldedeudores/v1.0/Deudas/{0}";
    // The 24-month history. Deudas/{cuit} answers with the LATEST período only, so every history
    // series built from it had exactly one point; this endpoint is where the older months live.
    private const string DeudaHistoricaPathTemplate = "/centraldedeudores/v1.0/Deudas/Historicas/{0}";
    private const string ChequesPathTemplate      = "/centraldedeudores/v1.0/Deudas/ChequesRechazados/{0}";
    private const string MonetariasPathTemplate   = "/estadisticas/v4.0/Monetarias/{0}?Desde={1}&Hasta={2}";
    // Full absolute URL: BcraClient BaseAddress does not cover the estadisticascambiarias host path.
    private const string CotizacionesUrlTemplate  = "https://api.bcra.gob.ar/estadisticascambiarias/v1.0/Cotizaciones/{0}?fechaDesde={1}&fechaHasta={2}";

    // BCRA "Monetarias" (v4.0) variable ids, verified against the live catalog:
    //   27  = "Inflación mensual"
    //   135 = "Tasa de interés TAMAR de bancos públicos y privados" (nominal annual)
    // The former reference/policy-rate series ("Tasa de Política Monetaria", id 160) was
    // discontinued by the BCRA on 2025-07-10, so TAMAR now carries the reference-rate row.
    private const int VarInflacionMensual = 27;
    private const int VarTasaTamar        = 135;

    private const string CacheKeyDeudaPrefix   = "bcra:deuda:";
    private const string CacheKeyChequesPrefix = "bcra:cheques:";
    private const string CacheKeyMacro         = "bcra:macro";
    private const string CacheKeyCambiario     = "bcra:cambiario";

    private static readonly TimeSpan DeudaTtl     = TimeSpan.FromHours(24);
    private static readonly TimeSpan ChequesTtl   = TimeSpan.FromHours(24);
    private static readonly TimeSpan MacroTtl     = TimeSpan.FromHours(1);
    private static readonly TimeSpan CambiarioTtl = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory       _httpClientFactory;
    private readonly IMemoryCache             _cache;
    private readonly IBcraCentralDeudoresGate _centralDeudoresGate;
    private readonly TimeSpan                 _failureTtl;
    private readonly TimeSpan                 _lookupBudget;
    private readonly ILogger<BcraService>     _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BcraService"/>.
    /// </summary>
    public BcraService(
        IHttpClientFactory       httpClientFactory,
        IMemoryCache             cache,
        IBcraCentralDeudoresGate centralDeudoresGate,
        IOptions<BcraOptions>    options,
        ILogger<BcraService>     logger)
    {
        _httpClientFactory   = httpClientFactory;
        _cache               = cache;
        _centralDeudoresGate = centralDeudoresGate;
        _failureTtl          = TimeSpan.FromSeconds(Math.Max(0, options.Value.FailureCacheSeconds));
        _lookupBudget        = TimeSpan.FromSeconds(Math.Max(1, options.Value.CentralDeudoresLookupBudgetSeconds));
        _logger              = logger;
    }

    // ── IBcraService ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuit);
        cuit = cuit.Replace("-", "").Trim();

        using var budget = StartCentralDeudoresBudget();
        return await GetDeudaCoreAsync(cuit, budget.Token, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuit);
        cuit = cuit.Replace("-", "").Trim();

        using var budget = StartCentralDeudoresBudget();
        return await GetChequesCoreAsync(cuit, budget.Token, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BcraMacroData?> GetMacroAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKeyMacro, out BcraMacroData? cached))
            return cached;

        var now   = DateTime.UtcNow;
        var today = now.ToString("yyyy-MM-dd");
        var desde = now.AddDays(-60).ToString("yyyy-MM-dd");

        // §7.3b: the CPI SERIES comes from its own request, deliberately NOT from a widened version
        // of the scalar's. FetchWithStatusAsync returns a null body on ANY non-success status, so had
        // one request served both, a range the BCRA refused would take InflacionMensual down with it
        // — and ScoringService reads a missing inflation figure as the NEUTRAL 50 rather than the 30
        // base, which would quietly shift the composite of every analysis of every tenant, LUFE or
        // not. As a separate leg the new request can only ever cost the new signal, which then
        // measures the sales drop nominally and reports that it did.
        //
        // Month-ALIGNED, not `now.AddMonths(-26)`: anchoring on today's day-of-month would move the
        // set of months covered as the month advances, so the same entity could deflate on the 28th
        // and not on the 1st with no code change and no new data.
        //
        // 26 months, not the 13 a year-over-year span strictly needs: the span ends at the most
        // recent month CARRYING SALES, and LUFE filings lag — its own window is a rolling ~18 months
        // and the snapshot scoring reads may itself be months old. 26 covers a 12-month span plus a
        // year of that lag; the cost is ~26 numbers in a blob that is already nvarchar(max).
        var desdeSerie = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(-26)
            .ToString("yyyy-MM-dd");

        var pathInflacion      = string.Format(MonetariasPathTemplate, VarInflacionMensual, desde,      today);
        var pathInflacionSerie = string.Format(MonetariasPathTemplate, VarInflacionMensual, desdeSerie, today);
        var pathTamar          = string.Format(MonetariasPathTemplate, VarTasaTamar,        desde,      today);

        var taskInflacion      = FetchAsync(pathInflacion, ct);
        var taskInflacionSerie = FetchAsync(pathInflacionSerie, ct);
        var taskTamar          = FetchAsync(pathTamar, ct);
        await Task.WhenAll(taskInflacion, taskInflacionSerie, taskTamar).ConfigureAwait(false);

        var inflacionMensual = ParseMonetariasLastValor(taskInflacion.Result, VarInflacionMensual);
        var tasaTamar        = ParseMonetariasLastValor(taskTamar.Result, VarTasaTamar);
        var inflacionSerie   = ParseMonetariasSerieMensual(taskInflacionSerie.Result, VarInflacionMensual);

        // Unchanged, and deliberately blind to the series: macro survives on either scalar, and the
        // series failing on its own must not null a payload the engine can still score with.
        if (inflacionMensual is null && tasaTamar is null)
        {
            _logger.LogWarning("BCRA macro: both variables {VarInflacion} and {VarTamar} failed",
                VarInflacionMensual, VarTasaTamar);
            return null;
        }

        var data = new BcraMacroData
        {
            InflacionMensual      = inflacionMensual,
            TasaReferencia        = tasaTamar,
            InflacionMensualSerie = inflacionSerie,
            FetchedAt             = DateTime.UtcNow
        };

        _cache.Set(CacheKeyMacro, data, MacroTtl);

        // The series' first/last period keys are logged because the mapping from the wire `fecha` to
        // a "YYYYMM" reference month is an ASSUMPTION (see ParseMonetariasSerieMensual). If the BCRA
        // ever dated variable 27 by publication rather than by reference month, every key would be
        // off by one and nothing would fail — these two values are what makes that visible.
        var periodos = inflacionSerie?.Keys.Order(StringComparer.Ordinal).ToArray();

        _logger.LogInformation(
            "BCRA macro fetched: inflacionMensual={Inflacion} tasaReferencia={Tasa} " +
            "serieMeses={SerieMeses} seriePrimerPeriodo={Primero} serieUltimoPeriodo={Ultimo}",
            data.InflacionMensual, data.TasaReferencia,
            periodos?.Length ?? 0,
            periodos is { Length: > 0 } ? periodos[0]  : null,
            periodos is { Length: > 0 } ? periodos[^1] : null);

        return data;
    }

    /// <inheritdoc />
    public async Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKeyCambiario, out BcraCambiarioData? cached))
            return cached;

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var desde = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");

        var path = string.Format(CotizacionesUrlTemplate, "USD", desde, today);
        var raw  = await FetchAsync(path, ct).ConfigureAwait(false);

        var tipoCambioOficial = ParseCotizacionLastValue(raw);

        if (tipoCambioOficial is null)
        {
            _logger.LogWarning("BCRA cambiario: USD cotizacion failed");
            return null;
        }

        var data = new BcraCambiarioData
        {
            TipoCambioOficial = tipoCambioOficial,
            FetchedAt         = DateTime.UtcNow
        };

        _cache.Set(CacheKeyCambiario, data, CambiarioTtl);

        _logger.LogInformation(
            "BCRA cambiario fetched: tipoCambioOficial={Valor}",
            data.TipoCambioOficial);

        return data;
    }

    /// <inheritdoc />
    public async Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuit);
        cuit = cuit.Replace("-", "").Trim();

        var (deuda, macro, cambiario, cheques) = await FetchAllAsync(cuit, ct).ConfigureAwait(false);

        // The all-failed guard is intentionally left as-is: it considers only deuda/macro/cambiario.
        // A cheques-only failure therefore never nulls the whole block, and cheques succeeding never
        // resurrects a BcraData that would otherwise be null.
        if (deuda.Estado == BcraDeudaConsultaEstado.LookupFailed && macro is null && cambiario is null)
        {
            _logger.LogWarning(
                "All BCRA calls failed for CUIT {Cuit}; BcraDataJson will be null",
                cuit);
            return null;
        }

        return new BcraData
        {
            Deuda     = deuda,
            Macro     = macro,
            Cambiario = cambiario,
            Cheques   = cheques
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// The deuda lookup for an already-normalised CUIT. <paramref name="budget"/> is the lookup's
    /// shared wall-clock budget (see <see cref="StartCentralDeudoresBudget"/>); <paramref name="ct"/>
    /// is the caller's own token, the only one whose cancellation propagates as an exception.
    /// </summary>
    private async Task<BcraDeudaData> GetDeudaCoreAsync(string cuit, CancellationToken budget, CancellationToken ct)
    {
        var cacheKey = CacheKeyDeudaPrefix + cuit;

        if (_cache.TryGetValue(cacheKey, out BcraDeudaData? cached) && cached is not null)
        {
            if (cached.Estado == BcraDeudaConsultaEstado.LookupFailed)
                LogServingCachedFailure("deuda", cuit);
            return cached;
        }

        var escapedCuit = Uri.EscapeDataString(cuit);

        // Deudas FIRST and alone. Historicas is requested only once Deudas has given a real answer —
        // debt found or no debt reported — never after a failed Deudas, whose outcome no history can
        // change.
        var (deudaStatus, raw) = await FetchCentralDeudoresAsync(
            string.Format(DeudaPathTemplate, escapedCuit), budget, ct).ConfigureAwait(false);

        // The endpoint answers 404 "No se encontró datos..." for a CUIT with no reported debt — the
        // same convention as ChequesRechazados — a good outcome, not a failure. Estado is decided by
        // Deudas alone; Historicas is still consulted, because no debt in the latest período says
        // nothing about the 24 before it.
        if (deudaStatus == 404)
        {
            _logger.LogInformation(
                "BCRA deuda: no debt reported (404) for CUIT {Cuit}",
                cuit);
            long? identificacion = long.TryParse(cuit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            return await CompleteNoDebtAsync(
                cacheKey, escapedCuit, cuit, identificacion, denominacion: null, deudaPeriodos: null, budget, ct)
                .ConfigureAwait(false);
        }

        // Any other non-success (5xx, 429, other 4xx) or a transport failure (status null) is a
        // failed lookup — cached only for the short failure window, so it is retried after it.
        if (raw is null)
            return RememberFailure(cacheKey, BuildFailedDeuda());

        BcraDeudaApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<BcraDeudaApiResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "BCRA deuda response parse error for CUIT {Cuit}",
                cuit);
            return RememberFailure(cacheKey, BuildFailedDeuda());
        }

        if (apiResponse?.Results is null)
        {
            _logger.LogWarning(
                "BCRA deuda returned empty results for CUIT {Cuit}",
                cuit);
            return RememberFailure(cacheKey, BuildFailedDeuda());
        }

        var periodos    = apiResponse.Results.Periodos;
        var lastPeriodo = periodos?.LastOrDefault();
        var entidades   = lastPeriodo?.Entidades ?? [];

        if (periodos is null or { Count: 0 } || entidades.Count == 0)
        {
            _logger.LogInformation(
                "BCRA deuda: no debt reported for CUIT {Cuit}",
                cuit);
            return await CompleteNoDebtAsync(
                cacheKey, escapedCuit, cuit,
                apiResponse.Results.Identificacion,
                apiResponse.Results.Denominacion,
                periodos,
                budget, ct).ConfigureAwait(false);
        }

        // Estado and every current-período field were decided above from Deudas alone; the history
        // leg only lengthens the three series, and when it fails they fall back to the Deudas períodos.
        var (historicasStatus, historicasRaw) = await FetchCentralDeudoresAsync(
            string.Format(DeudaHistoricaPathTemplate, escapedCuit), budget, ct).ConfigureAwait(false);
        var (historicas, historicasFailedTransiently) =
            TryParseHistoricas(historicasStatus, historicasRaw, cuit);

        if (historicas is not null)
            WarnIfLatestPeriodoDisagrees(lastPeriodo!, historicas, cuit);

        var data = MapDeuda(apiResponse.Results, MergeHistorialPeriodos(periodos, historicas));
        data.Estado = BcraDeudaConsultaEstado.DebtFound;

        // A history lost to a TRANSIENT failure is not cached, so the next call retries it rather than
        // serving a one-point trajectory for 24 h. A deterministic answer (404, malformed body, no
        // períodos) would come back the same on a retry, so that result is cached as usual.
        if (!historicasFailedTransiently)
            _cache.Set(cacheKey, data, DeudaTtl);

        _logger.LogInformation(
            "BCRA deuda fetched for CUIT {Cuit}: situacionMaxima={Situacion} montoTotal={Monto}",
            cuit, data.SituacionMaxima, data.MontoTotal);

        return data;
    }

    /// <summary>
    /// Finishes a NoDebtReported answer — shared by both exits, the Deudas 404 and the 200 with no
    /// períodos / no entidades. Deudas has already decided Estado and every current-período field; this
    /// asks Historicas for the months BEFORE it, because "no debt today" is not "no debt ever": a CUIT
    /// that was situación 3 four months ago would otherwise read as a clean record with no trajectory.
    /// <para>
    /// Only the three history series can be filled. When Historicas 404s, is empty or fails the plain
    /// NoDebtReported block is returned — never LookupFailed, since the current-debt answer was real.
    /// Caching mirrors the DebtFound path: a history lost to a TRANSIENT failure is not cached, so the
    /// next refresh fills it; a deterministic answer is cached for <see cref="DeudaTtl"/>.
    /// </para>
    /// </summary>
    /// <param name="deudaPeriodos">
    /// The Deudas períodos (none for the 404). Merged in so Deudas wins its own month: the series can
    /// never show debt in the período Deudas reports as clean.
    /// </param>
    private async Task<BcraDeudaData> CompleteNoDebtAsync(
        string                  cacheKey,
        string                  escapedCuit,
        string                  cuit,
        long?                   identificacion,
        string?                 denominacion,
        List<BcraDeudaPeriodo>? deudaPeriodos,
        CancellationToken       budget,
        CancellationToken       ct)
    {
        var noDebt = BuildNoDebtDeuda(identificacion, denominacion);

        var (historicasStatus, historicasRaw) = await FetchCentralDeudoresAsync(
            string.Format(DeudaHistoricaPathTemplate, escapedCuit), budget, ct).ConfigureAwait(false);
        var (historicas, historicasFailedTransiently) =
            TryParseHistoricas(historicasStatus, historicasRaw, cuit);

        if (historicas is not null)
        {
            var (situaciones, montos, montosPorSituacion) =
                BuildHistorialSeries(MergeHistorialPeriodos(deudaPeriodos, historicas));

            noDebt.HistorialSituaciones        = situaciones        ?? new();
            noDebt.HistorialMontos             = montos             ?? new();
            noDebt.HistorialMontosPorSituacion = montosPorSituacion ?? new();
        }

        if (!historicasFailedTransiently)
            _cache.Set(cacheKey, noDebt, DeudaTtl);

        _logger.LogInformation(
            "BCRA deuda: no current debt for CUIT {Cuit}; history carries {HistorialPeriodos} período(s) with debt",
            cuit, noDebt.HistorialMontos!.Count);

        return noDebt;
    }

    /// <summary>
    /// The cheques lookup for an already-normalised CUIT. Same token contract as
    /// <see cref="GetDeudaCoreAsync"/>.
    /// </summary>
    private async Task<BcraChequesData> GetChequesCoreAsync(string cuit, CancellationToken budget, CancellationToken ct)
    {
        var cacheKey = CacheKeyChequesPrefix + cuit;

        if (_cache.TryGetValue(cacheKey, out BcraChequesData? cached) && cached is not null)
        {
            if (cached.Estado == BcraChequesConsultaEstado.LookupFailed)
                LogServingCachedFailure("cheques", cuit);
            return cached;
        }

        var path = string.Format(ChequesPathTemplate, Uri.EscapeDataString(cuit));
        var (statusCode, raw) = await FetchCentralDeudoresAsync(path, budget, ct).ConfigureAwait(false);

        // The endpoint answers 404 "No se encontró datos..." for a CUIT with no rejected
        // cheques — a good outcome, not a failure. Distinct from a transient lookup error.
        if (statusCode == 404)
        {
            _logger.LogInformation("BCRA cheques: no rejected cheques reported for CUIT {Cuit}", cuit);
            var none = BuildEmptyCheques(BcraChequesConsultaEstado.NoChequesReported);
            _cache.Set(cacheKey, none, ChequesTtl);
            return none;
        }

        // Any other non-success (5xx, other 4xx) or a transport failure (statusCode null) is a
        // failed lookup — cached only for the short failure window, so it is retried after it.
        if (statusCode != 200 || raw is null)
            return RememberFailure(cacheKey, BuildEmptyCheques(BcraChequesConsultaEstado.LookupFailed));

        BcraChequesApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<BcraChequesApiResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "BCRA cheques response parse error for CUIT {Cuit}",
                cuit);
            return RememberFailure(cacheKey, BuildEmptyCheques(BcraChequesConsultaEstado.LookupFailed));
        }

        if (apiResponse?.Results is null)
        {
            _logger.LogWarning(
                "BCRA cheques returned empty results for CUIT {Cuit}",
                cuit);
            return RememberFailure(cacheKey, BuildEmptyCheques(BcraChequesConsultaEstado.LookupFailed));
        }

        var data = MapCheques(apiResponse.Results);
        data.Estado = data.TotalRechazados > 0
            ? BcraChequesConsultaEstado.ChequesFound
            : BcraChequesConsultaEstado.NoChequesReported;
        _cache.Set(cacheKey, data, ChequesTtl);

        _logger.LogInformation(
            "BCRA cheques fetched for CUIT {Cuit}: totalRechazados={Total} totalImpagos={Impagos}",
            cuit, data.TotalRechazados, data.TotalImpagos);

        return data;
    }

    private async Task<(BcraDeudaData Deuda, BcraMacroData? Macro, BcraCambiarioData? Cambiario, BcraChequesData Cheques)>
        FetchAllAsync(string cuit, CancellationToken ct)
    {
        // The estadísticas legs still run in parallel with the Central de Deudores; only the
        // Central de Deudores legs are sequenced among themselves.
        var centralDeudoresTask = FetchCentralDeudoresSequenceAsync(cuit, ct);
        var macroTask           = GetMacroAsync(ct);
        var cambiarioTask       = GetCambiarioAsync(ct);

        await Task.WhenAll(centralDeudoresTask, macroTask, cambiarioTask).ConfigureAwait(false);

        var (deuda, cheques) = centralDeudoresTask.Result;
        return (deuda, macroTask.Result, cambiarioTask.Result, cheques);
    }

    /// <summary>
    /// Deudas (and, inside it, Historicas whenever Deudas answered) and THEN cheques — one Central de
    /// Deudores request at a time, under one shared budget. They used to fire together, three
    /// simultaneous requests per lookup against an endpoint that resets connections when hit in bursts.
    /// </summary>
    private async Task<(BcraDeudaData Deuda, BcraChequesData Cheques)> FetchCentralDeudoresSequenceAsync(
        string cuit, CancellationToken ct)
    {
        using var budget = StartCentralDeudoresBudget();

        var deuda   = await GetDeudaCoreAsync(cuit, budget.Token, ct).ConfigureAwait(false);
        var cheques = await GetChequesCoreAsync(cuit, budget.Token, ct).ConfigureAwait(false);

        return (deuda, cheques);
    }

    /// <summary>
    /// Starts one Central de Deudores lookup's wall-clock budget. Deliberately NOT linked to the
    /// caller's token: the two are combined per request in <see cref="FetchCentralDeudoresAsync"/>,
    /// which is what lets it tell "the budget ran out" (a failed lookup) from "the caller cancelled"
    /// (an exception) apart.
    /// </summary>
    private CancellationTokenSource StartCentralDeudoresBudget() => new(_lookupBudget);

    /// <summary>
    /// Caches a Central de Deudores LookupFailed for the short failure window and returns it, so
    /// repeated clicks during a BCRA penalty serve the honest failure instead of extending it.
    /// A zero window (<see cref="BcraOptions.FailureCacheSeconds"/> &lt;= 0) caches nothing.
    /// </summary>
    private T RememberFailure<T>(string cacheKey, T failed)
    {
        if (_failureTtl > TimeSpan.Zero)
            _cache.Set(cacheKey, failed, _failureTtl);

        return failed;
    }

    private void LogServingCachedFailure(string leg, string cuit)
        => _logger.LogInformation(
            "BCRA {Leg}: serving the cached LookupFailed for CUIT {Cuit} (failure window {FailureCacheSeconds}s); no request sent",
            leg, cuit, (int)_failureTtl.TotalSeconds);

    /// <summary>
    /// Deserializes a <see cref="BcraMonetariasApiResponse"/> (v4.0 Monetarias) and
    /// returns the <c>valor</c> of the last <c>detalle</c> entry (most recent data point).
    /// Returns <c>null</c> if <paramref name="raw"/> is null, empty, or cannot be parsed.
    /// </summary>
    private double? ParseMonetariasLastValor(string? raw, int varId)
    {
        if (raw is null) return null;

        BcraMonetariasApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<BcraMonetariasApiResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "BCRA monetarias variable {VarId} response parse error", varId);
            return null;
        }

        var last = apiResponse?.Results?.FirstOrDefault()?.Detalle?.LastOrDefault();
        if (last is null)
        {
            _logger.LogWarning("BCRA monetarias variable {VarId} returned empty detalle", varId);
            return null;
        }

        return last.Valor;
    }

    /// <summary>
    /// Deserializes a <see cref="BcraMonetariasApiResponse"/> (v4.0 Monetarias) into a MONTHLY series
    /// keyed "YYYYMM" → <c>valor</c>, for §7.3b's CPI deflator. Returns <c>null</c> — never an empty
    /// dictionary — when <paramref name="raw"/> is null, unparseable, or carries no usable point, so
    /// "no series" is one shape rather than two.
    /// <para>
    /// The period key is taken by SUBSTRING off the "YYYY-MM-DD" <c>fecha</c>, never through
    /// <c>DateTime.Parse</c>: a month key has no time zone and no calendar, and parsing would drag
    /// both into it. Anything that is not that shape is skipped rather than guessed at.
    /// </para>
    /// <para>
    /// ASSUMPTION, and the one thing here that cannot be verified from this repository: the wire
    /// <c>fecha</c> dates a point in the month the inflation REFERS TO (the BCRA publishes variable
    /// 27 at month end). If it were the publication date instead, every key would be off by one and
    /// nothing would fail loudly — the span would still be twelve months, just shifted. That is why
    /// <see cref="GetMacroAsync"/> logs the first and last period keys it derived.
    /// </para>
    /// <para>
    /// Duplicate months take the LAST entry (the <c>detalle</c> array is documented ascending, so
    /// that is the newer reading). Assignment, not <c>Add</c>, on purpose: <c>Add</c> would throw
    /// <c>ArgumentException</c> on a revised month, and nothing between here and
    /// <c>GetBcraDataForAnalysisAsync</c> catches it — the analysis would lose its whole BCRA block,
    /// and its 0.35 weight, over a duplicate row.
    /// </para>
    /// </summary>
    private Dictionary<string, double>? ParseMonetariasSerieMensual(string? raw, int varId)
    {
        if (raw is null) return null;

        BcraMonetariasApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<BcraMonetariasApiResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "BCRA monetarias variable {VarId} series response parse error", varId);
            return null;
        }

        var detalle = apiResponse?.Results?.FirstOrDefault()?.Detalle;
        if (detalle is null || detalle.Count == 0)
        {
            _logger.LogWarning(
                "BCRA monetarias variable {VarId} returned no detalle for the series window", varId);
            return null;
        }

        var serie = new Dictionary<string, double>(detalle.Count, StringComparer.Ordinal);

        foreach (var punto in detalle)
        {
            if (punto?.Fecha is not { } fecha) continue;
            if (ToPeriodo(fecha) is not { } periodo) continue;

            serie[periodo] = punto.Valor;
        }

        return serie.Count == 0 ? null : serie;
    }

    /// <summary>
    /// Converts a BCRA "YYYY-MM-DD" date string to the "YYYYMM" period key the scoring engine
    /// compares on, or <c>null</c> when it is not that shape. Validates rather than trusts: the value
    /// arrives off a vendor payload, and a malformed one that silently produced a key would pollute
    /// the series with a month that can never match.
    /// </summary>
    private static string? ToPeriodo(string fecha)
    {
        if (fecha.Length < 10 || fecha[4] != '-' || fecha[7] != '-') return null;

        var anio = fecha.AsSpan(0, 4);
        var mes  = fecha.AsSpan(5, 2);

        // NumberStyles.None rejects signs, whitespace and group separators, so "+123" and " 12"
        // cannot pass for a year or a month — the same guard ShiftPeriodoMonths uses on the other
        // side of this contract.
        if (!int.TryParse(anio, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(mes,  NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            year  is < 1 or > 9999 ||
            month is < 1 or > 12)
        {
            return null;
        }

        return string.Concat(anio, mes);
    }

    /// <summary>
    /// Deserializes a <see cref="BcraCotizacionesApiResponse"/> (v1.0 Cotizaciones) and
    /// returns <c>tipoCotizacion</c> from <c>results[0].detalle[0]</c>.
    /// Results are sorted descending by date so index 0 is the most recent.
    /// Returns <c>null</c> if <paramref name="raw"/> is null, empty, or cannot be parsed.
    /// </summary>
    private double? ParseCotizacionLastValue(string? raw)
    {
        if (raw is null) return null;

        BcraCotizacionesApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<BcraCotizacionesApiResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "BCRA cotizaciones response parse error");
            return null;
        }

        var detalle = apiResponse?.Results?.FirstOrDefault()?.Detalle?.FirstOrDefault();
        if (detalle is null)
        {
            _logger.LogWarning("BCRA cotizaciones returned empty detalle");
            return null;
        }

        return detalle.TipoCotizacion;
    }

    private async Task<string?> FetchAsync(string path, CancellationToken ct)
    {
        var (_, body) = await FetchWithStatusAsync(path, ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// Like <see cref="FetchAsync"/> but also surfaces the HTTP status code, so callers that
    /// need to distinguish outcomes (e.g. a 404 "no data" that is a valid answer rather than a
    /// failure) can do so. On a transport failure (network error / timeout) the status is
    /// <c>null</c>. On any non-success status the body is <c>null</c>.
    /// </summary>
    private Task<(int? StatusCode, string? Body)> FetchWithStatusAsync(string path, CancellationToken ct)
        => SendAsync(BcraHttpClientRegistration.EstadisticasClientName, path, ct, ct);

    /// <summary>
    /// <see cref="FetchWithStatusAsync"/> for the Central de Deudores: its own named client (the
    /// gentler pipeline), a permit from the process-wide <see cref="IBcraCentralDeudoresGate"/> held
    /// for the request and all its retries, and the lookup's <paramref name="budget"/>, which also
    /// bounds the time spent queued for that permit. The budget running out is a failed lookup
    /// (status <c>null</c>); only the caller's own <paramref name="ct"/> propagates as cancellation.
    /// </summary>
    private async Task<(int? StatusCode, string? Body)> FetchCentralDeudoresAsync(
        string path, CancellationToken budget, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget);

        IDisposable permit;
        try
        {
            permit = await _centralDeudoresGate.EnterAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "BCRA central de deudores lookup budget exhausted while queued for path {Path}",
                path);
            return (null, null);
        }

        using (permit)
        {
            return await SendAsync(
                    BcraHttpClientRegistration.CentralDeudoresClientName, path, linked.Token, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The shared GET. <paramref name="requestToken"/> is what the request observes;
    /// <paramref name="ct"/> is the caller's token. They are the same token for estadísticas, and
    /// the caller's token linked to the lookup budget for the Central de Deudores.
    /// </summary>
    private async Task<(int? StatusCode, string? Body)> SendAsync(
        string clientName, string path, CancellationToken requestToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(clientName);
        try
        {
            var response = await client
                .GetAsync(path, requestToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "BCRA API returned {StatusCode} for path {Path}",
                    (int)response.StatusCode, path);
                return ((int)response.StatusCode, null);
            }

            var body = await response.Content
                .ReadAsStringAsync(requestToken)
                .ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
            when (clientName == BcraHttpClientRegistration.CentralDeudoresClientName && IsConnectionReset(ex))
        {
            // Logged apart from the generic network error because it is the signature of the
            // 2026-10-19 incident: a reset, after its one retry, is the BCRA most likely rate-limiting
            // this source IP. The path carries the CUIT, already logged on every line here, and no
            // query string — the Central de Deudores paths have none.
            _logger.LogWarning(ex,
                "BCRA central de deudores connection reset — possible rate limit for path {Path}",
                path);
            return (null, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "BCRA API network error for path {Path}",
                path);
            return (null, null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && requestToken.IsCancellationRequested)
        {
            // Only reachable on the Central de Deudores, where requestToken carries the lookup budget
            // (for estadísticas it IS ct, so this filter cannot match). The budget ran out mid-request:
            // a failed lookup, not the caller's cancellation.
            _logger.LogWarning(ex,
                "BCRA central de deudores lookup budget exhausted for path {Path}",
                path);
            return (null, null);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "BCRA API timeout for path {Path}",
                path);
            return (null, null);
        }
        catch (TimeoutRejectedException ex)
        {
            // The real timeout signal on these clients. Both BCRA clients set Timeout.InfiniteTimeSpan
            // (BcraHttpClientRegistration) so HttpClient never raises the TaskCanceledException caught
            // above; the total cap in the resilience pipeline (40 s on BcraClient, 20 s on
            // BcraCentralDeudoresClient) throws this instead, and it
            // derives from ExecutionRejectedException — neither HttpRequestException nor
            // TaskCanceledException. Uncaught, it escaped every layer up to
            // GetBcraDataForAnalysisAsync and broke this type's "never throw" contract.
            _logger.LogWarning(ex,
                "BCRA API total timeout (resilience pipeline) for path {Path}",
                path);
            return (null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller asked us to stop; that is not a BCRA failure and must not be reported as
            // one. Without this, the backstop below would swallow it and fabricate a LookupFailed —
            // which would also render the `when (!ct.IsCancellationRequested)` filter above dead,
            // since TaskCanceledException derives from OperationCanceledException. Reachable today:
            // BcraController and EntitiesController's refresh-bcra endpoint pass a real token.
            throw;
        }
        catch (Exception ex)
        {
            // Backstop so the documented contract actually holds: a caller's analysis must never
            // fail because BCRA misbehaved in a way not enumerated above. Error, not Warning —
            // reaching here means an exception type we did not anticipate.
            _logger.LogError(ex,
                "BCRA API unexpected error for path {Path}",
                path);
            return (null, null);
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is (or wraps) a TCP connection reset — what curl reported as
    /// "(56) Recv failure: Connection was reset" during the 2026-10-19 incident. Walks the inner
    /// exceptions because SocketsHttpHandler surfaces it as HttpRequestException → IOException →
    /// SocketException, and the nesting depth is not part of any contract.
    /// </summary>
    internal static bool IsConnectionReset(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the Deudas/Historicas leg, on both the DebtFound and the NoDebtReported paths. Never throws:
    /// any failure yields <c>null</c> períodos and the series are built without it — from the Deudas
    /// período on DebtFound, empty on NoDebtReported. <c>FailedTransiently</c> tells the caller whether
    /// a retry could succeed, which is what decides whether the result may be cached.
    /// <para>
    /// Historicas lists, in every período, each entidad the CUIT EVER had debt with — one it owed
    /// nothing that month arrives as <c>situacion 0, monto 0</c>, where Deudas omits it altogether.
    /// Those entidades are dropped here, which makes a Historicas período equivalent to the Deudas one
    /// for the same month (verified on a real response: identical montos entidad by entidad). A
    /// período left with no entidades is then skipped by the series' existing empty-período rule, so a
    /// month with no debt adds no point, and a situación-0 entidad can never drag a período's
    /// situaciónMaxima down to 0 — which would read as a one-step worsening to the trend adjustment.
    /// </para>
    /// </summary>
    private (List<BcraDeudaPeriodo>? Periodos, bool FailedTransiently) TryParseHistoricas(
        int? statusCode, string? raw, string cuit)
    {
        if (raw is null)
        {
            // Null status = network error or timeout (TimeoutRejectedException included); 5xx, 408
            // and 429 are the statuses a retry can fix. A 404 or any other 4xx is a real answer.
            var isTransient = statusCode is null or >= 500 or 408 or 429;
            LogHistoricasUnavailable(cuit, statusCode);
            return (null, isTransient);
        }

        BcraDeudaApiResponse? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<BcraDeudaApiResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "BCRA deuda históricas response parse error for CUIT {Cuit}; history series built without it",
                cuit);
            return (null, false);
        }

        var periodos = apiResponse?.Results?.Periodos;
        if (periodos is null or { Count: 0 })
        {
            LogHistoricasUnavailable(cuit, statusCode);
            return (null, false);
        }

        var normalizados = periodos
            .Where(p => p is not null)
            .Select(p => new BcraDeudaPeriodo
            {
                Periodo   = p.Periodo,
                Entidades = p.Entidades?.Where(e => e is not null && e.Situacion != 0).ToList()
            })
            .ToList();

        return (normalizados, false);
    }

    private void LogHistoricasUnavailable(string cuit, int? statusCode)
        => _logger.LogWarning(
            "BCRA deuda históricas unavailable for CUIT {Cuit} (status {StatusCode}); history series built without it",
            cuit, statusCode);

    /// <summary>
    /// Tripwire for the two endpoints drifting apart. On a real response the Deudas período and the
    /// same month in Historicas carry identical montos (the same thousands-of-pesos unit), so a
    /// different total means the unit or the shape changed. Logging only: Deudas wins that período
    /// in <see cref="MergeHistorialPeriodos"/> regardless, so the output cannot change.
    /// </summary>
    private void WarnIfLatestPeriodoDisagrees(
        BcraDeudaPeriodo deudaPeriodo, List<BcraDeudaPeriodo> historicas, string cuit)
    {
        var mismo = historicas.FirstOrDefault(p => string.Equals(p.Periodo, deudaPeriodo.Periodo, StringComparison.Ordinal));
        if (mismo?.Entidades is null)
            return;

        var totalDeudas     = deudaPeriodo.Entidades?.Sum(e => e.Monto) ?? 0m;
        var totalHistoricas = mismo.Entidades.Sum(e => e.Monto);
        if (totalDeudas != totalHistoricas)
        {
            _logger.LogWarning(
                "BCRA deuda: período {Periodo} totals differ between Deudas ({TotalDeudas}) and Historicas ({TotalHistoricas}) for CUIT {Cuit}",
                deudaPeriodo.Periodo, totalDeudas, totalHistoricas, cuit);
        }
    }

    /// <summary>
    /// Merges the Deudas períodos into the Historicas ones, keyed by <c>periodo</c>. Deudas is added
    /// LAST so it wins a shared month — the current período then reads the same in the series as in
    /// the montoTotal / situacionMaxima scalars — and a Deudas período newer than Historicas' first is
    /// simply added. Null elements and blank periodos never become keys; the series' own
    /// skip-malformed rule has nothing to lose there, since it drops those anyway. With no Historicas
    /// the Deudas períodos come back as they are, which is exactly the pre-Historicas input.
    /// </summary>
    internal static List<BcraDeudaPeriodo> MergeHistorialPeriodos(
        List<BcraDeudaPeriodo>? deudaPeriodos, List<BcraDeudaPeriodo>? historicasPeriodos)
    {
        if (historicasPeriodos is null)
            return deudaPeriodos ?? [];

        var porPeriodo = new Dictionary<string, BcraDeudaPeriodo>(StringComparer.Ordinal);

        foreach (var p in historicasPeriodos.Concat(deudaPeriodos ?? []))
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Periodo))
                continue;
            porPeriodo[p.Periodo] = p;
        }

        return porPeriodo.Values.ToList();
    }

    /// <param name="node">The Deudas result — the ONLY source of the current-período fields.</param>
    /// <param name="historialPeriodos">
    /// The períodos the three history series are built from: Deudas merged with Historicas, or the
    /// Deudas períodos alone when the history leg failed. Order does not matter; every series re-sorts.
    /// </param>
    private static BcraDeudaData MapDeuda(BcraDeudaResultsNode node, List<BcraDeudaPeriodo> historialPeriodos)
    {
        // The current período is the last Deudas entry. Deudas answers with that single período, so
        // its order is moot; the history comes from Historicas (newest first), which never reaches here.
        var lastPeriodo = node.Periodos?.LastOrDefault();
        var entidades   = lastPeriodo?.Entidades ?? [];

        var situacionMaxima    = entidades.Count > 0 ? entidades.Max(e => e.Situacion) : (int?)null;
        var montoTotal         = entidades.Count > 0 ? entidades.Sum(e => e.Monto)     : (decimal?)null;
        var entidadesInformantes = entidades
            .Where(e => !string.IsNullOrWhiteSpace(e.NombreEntidad))
            .Select(e => e.NombreEntidad!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tieneProcesoJudicial   = entidades.Any(e => e.ProcesoJud);
        var tieneSituacionJuridica = entidades.Any(e => e.SituacionJuridica);
        var enRevision             = entidades.Any(e => e.EnRevision);
        var maxDiasAtraso          = entidades.Count > 0 ? entidades.Max(e => e.DiasAtrasoPago) : 0;

        var entidadesDetalle = entidades
            .OrderByDescending(e => e.Monto)
            .Select(e => new BcraEntidadDeudaDetalle
            {
                Entidad           = e.NombreEntidad,
                Situacion         = e.Situacion,
                Monto             = e.Monto,
                DiasAtrasoPago    = e.DiasAtrasoPago,
                Refinanciaciones  = e.Refinanciaciones,
                SituacionJuridica = e.SituacionJuridica,
                EnRevision        = e.EnRevision,
                ProcesoJud        = e.ProcesoJud,
            })
            .ToList();

        var (historial, montos, montosPorSituacion) = BuildHistorialSeries(historialPeriodos);

        return new BcraDeudaData
        {
            Identificacion        = node.Identificacion,
            Denominacion          = node.Denominacion,
            SituacionMaxima       = situacionMaxima,
            MontoTotal            = montoTotal,
            EntidadesInformantes  = entidadesInformantes,
            PeriodoConsultado     = lastPeriodo?.Periodo,
            HistorialSituaciones  = historial,
            HistorialMontos       = montos,
            HistorialMontosPorSituacion = montosPorSituacion,
            EntidadesDetalle      = entidadesDetalle,
            TieneProcesoJudicial  = tieneProcesoJudicial,
            TieneSituacionJuridica = tieneSituacionJuridica,
            EnRevision            = enRevision,
            MaxDiasAtraso         = maxDiasAtraso,
            FetchedAt             = DateTime.UtcNow
        };
    }

    /// <summary>
    /// The three history series, off one set of períodos. Shared by <see cref="MapDeuda"/> (DebtFound)
    /// and <see cref="CompleteNoDebtAsync"/> (NoDebtReported with a history), so the two paths cannot
    /// filter, order or cap differently. Each series is <c>null</c> when
    /// <paramref name="historialPeriodos"/> is null or empty — MapDeuda's long-standing shape.
    /// </summary>
    private static (List<BcraSituacionPeriodo>? Situaciones,
                    List<BcraMontoPeriodo>? Montos,
                    List<BcraMontoSituacionPeriodo>? MontosPorSituacion)
        BuildHistorialSeries(List<BcraDeudaPeriodo>? historialPeriodos)
    {
        // Build per-period situación history for trend calculation (Feature 5c), and — off the very
        // same períodos — the per-period TOTAL debt the portal's debt-evolution chart renders. The
        // montos were already in this loop and were being dropped: only `situacion` was read from a
        // non-last período, so every older month's amount was fetched and then discarded.
        //
        // A third series splits those same montos by situación, so the chart can stack the month's
        // debt by severity instead of drawing one flat bar.
        //
        // The depths are deliberately different: situaciones stays at 12 because its only consumer
        // is ScoringService's trend window (bcra_trend_window_months, default 6), while the two
        // chart series ask for 24 months. Whatever BCRA actually returns is the ceiling; Take()
        // only caps.
        List<BcraSituacionPeriodo>?      historial          = null;
        List<BcraMontoPeriodo>?          montos             = null;
        List<BcraMontoSituacionPeriodo>? montosPorSituacion = null;
        if (historialPeriodos is { Count: > 0 })
        {
            historial = historialPeriodos
                .Where(p => p.Entidades is { Count: > 0 })
                .Select(p => new BcraSituacionPeriodo
                {
                    Periodo          = p.Periodo,
                    SituacionMaxima  = p.Entidades!.Max(e => e.Situacion)
                })
                .OrderByDescending(p => p.Periodo, StringComparer.Ordinal)
                .Take(12)
                .ToList();

            // A malformed período is SKIPPED, never fatal: a null element, a blank periodo or an
            // absent/empty entidades list costs that one bar and leaves the rest of the series — and
            // the whole deuda block — intact. That is why the guard is declarative rather than a
            // try/catch: this helper is static with no logger, so a catch here could only swallow
            // silently, and the Sums below are the same decimal Sum MapDeuda already does for
            // montoTotal, so they add no new kind of throw path to the never-throw contract.
            //
            // Both amount series walk the SAME períodos — identical filter, identical order,
            // identical cap — so a bar in the stacked chart always lines up with the bar the
            // totals chart draws for that month. Filtering and capping once, ahead of the two
            // projections, is what guarantees that rather than leaving it to two copies of the
            // predicate drifting apart later.
            var periodosChart = historialPeriodos
                .Where(p => p is not null
                         && !string.IsNullOrWhiteSpace(p.Periodo)
                         && p.Entidades is { Count: > 0 })
                .OrderByDescending(p => p.Periodo, StringComparer.Ordinal)
                .Take(24)
                .ToList();

            montos = periodosChart
                .Select(p => new BcraMontoPeriodo
                {
                    Periodo    = p.Periodo,
                    MontoTotal = p.Entidades!.Sum(e => e.Monto)
                })
                .ToList();

            // The same montos, split by situación for the stacked chart — the per-entidad
            // `situacion` was already being read here for the 12-month series and its `monto`
            // summed for the 24-month one; only the CROSS of the two was being dropped.
            //
            // The bands are `<= 1` / `2 or 3` / `>= 4`, not `== 1` / `2 or 3` / `4..6`: written
            // that way they partition every integer, so a situación outside BCRA's documented
            // 1-6 cannot fall between two bands and silently shrink the stack below the
            // montoTotal the series above reports for the very same período.
            montosPorSituacion = periodosChart
                .Select(p => new BcraMontoSituacionPeriodo
                {
                    Periodo          = p.Periodo,
                    MontoSituacion1  = p.Entidades!.Where(e => e.Situacion <= 1).Sum(e => e.Monto),
                    MontoSituacion23 = p.Entidades!.Where(e => e.Situacion is 2 or 3).Sum(e => e.Monto),
                    MontoSituacion46 = p.Entidades!.Where(e => e.Situacion >= 4).Sum(e => e.Monto)
                })
                .ToList();
        }

        return (historial, montos, montosPorSituacion);
    }

    private static BcraChequesData MapCheques(BcraChequesResultsNode node)
    {
        var causalesResumen = new List<BcraChequeCausalResumen>();
        var detalle         = new List<BcraChequeDetalle>();

        // Flatten causales → entidades → detalle into a single list, carrying causal + entidad
        // down onto each row, and build the per-causal aggregate in the same pass.
        foreach (var causalNode in node.Causales ?? [])
        {
            var cantidad        = 0;
            var cantidadImpagos = 0;
            var montoCausal     = 0m;

            foreach (var entidadNode in causalNode.Entidades ?? [])
            {
                foreach (var d in entidadNode.Detalle ?? [])
                {
                    var fechaPago = ParseChequeDate(d.FechaPago);
                    var pagado    = fechaPago is not null;   // impago iff FechaPago is null

                    detalle.Add(new BcraChequeDetalle
                    {
                        Causal         = causalNode.Causal,
                        Entidad        = entidadNode.Entidad,
                        NroCheque      = d.NroCheque,
                        FechaRechazo   = ParseChequeDate(d.FechaRechazo),
                        Monto          = d.Monto,
                        FechaPago      = fechaPago,
                        FechaPagoMulta = ParseChequeDate(d.FechaPagoMulta),
                        EstadoMulta    = d.EstadoMulta,
                        CtaPersonal    = d.CtaPersonal,
                        DenomJuridica  = d.DenomJuridica,
                        EnRevision     = d.EnRevision,
                        ProcesoJud     = d.ProcesoJud,
                        Pagado         = pagado
                    });

                    cantidad++;
                    montoCausal += d.Monto;
                    if (!pagado) cantidadImpagos++;
                }
            }

            causalesResumen.Add(new BcraChequeCausalResumen
            {
                Causal          = causalNode.Causal,
                Cantidad        = cantidad,
                Monto           = montoCausal,
                CantidadImpagos = cantidadImpagos
            });
        }

        // Most recent first.
        detalle = detalle.OrderByDescending(c => c.FechaRechazo).ToList();

        var impagos = detalle.Where(c => !c.Pagado).ToList();

        return new BcraChequesData
        {
            Identificacion       = node.Identificacion,
            Denominacion         = node.Denominacion,
            TotalRechazados      = detalle.Count,
            TotalImpagos         = impagos.Count,
            MontoTotalRechazado  = detalle.Sum(c => c.Monto),
            MontoTotalImpago     = impagos.Sum(c => c.Monto),
            FechaPrimerRechazo   = detalle.Count > 0 ? detalle.Min(c => c.FechaRechazo) : null,
            FechaUltimoRechazo   = detalle.Count > 0 ? detalle.Max(c => c.FechaRechazo) : null,
            TieneProcesoJudicial = detalle.Any(c => c.ProcesoJud),
            EnRevision           = detalle.Any(c => c.EnRevision),
            Causales             = causalesResumen,
            Detalle              = detalle,
            FetchedAt            = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Parses a BCRA <c>YYYY-MM-DD</c> date defensively: a malformed or null value returns
    /// <c>null</c> instead of throwing, so one bad date never loses the whole response.
    /// </summary>
    private static DateTime? ParseChequeDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;

    /// <summary>
    /// Builds an empty cheques block with the given outcome and non-null (empty) collections,
    /// used for the NoChequesReported and LookupFailed paths.
    /// <para>
    /// <c>FetchedAt</c> is stamped here rather than by the caller: it was previously set only on the
    /// NoChequesReported path, so every LookupFailed result serialised as
    /// <c>"fetchedAt":"0001-01-01T00:00:00"</c> — a default masquerading as a timestamp. The field
    /// records when we asked, which is meaningful whether or not we got an answer.
    /// </para>
    /// </summary>
    private static BcraChequesData BuildEmptyCheques(BcraChequesConsultaEstado estado)
        => new()
        {
            Estado    = estado,
            Causales  = new(),
            Detalle   = new(),
            FetchedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Builds a failed-lookup deuda block with non-null (empty) collections. Shared by the three
    /// LookupFailed exits of <see cref="GetDeudaAsync"/>, which were byte-identical initialisers.
    /// <para>
    /// <c>FetchedAt</c> is stamped for the same reason as in <see cref="BuildEmptyCheques"/>: it was
    /// previously left at <c>default</c> on every failure path, so persisted JSON claimed a fetch
    /// date of year 1. Cached only for the short failure window — see <see cref="RememberFailure{T}"/>.
    /// </para>
    /// </summary>
    private static BcraDeudaData BuildFailedDeuda()
        => new()
        {
            Estado               = BcraDeudaConsultaEstado.LookupFailed,
            EntidadesInformantes = new(),
            HistorialSituaciones = new(),
            HistorialMontos      = new(),
            HistorialMontosPorSituacion = new(),
            EntidadesDetalle     = new(),
            FetchedAt            = DateTime.UtcNow
        };

    /// <summary>
    /// Builds a no-debt deuda block with non-null (empty) collections. Shared by the two
    /// NoDebtReported exits of <see cref="GetDeudaAsync"/> — the 404 "No se encontró datos" answer
    /// and a 200 with no períodos or no entidades — so the two cannot drift apart. Every
    /// current-período field stays empty here for good; only the three history series may then be
    /// filled from Historicas, by <see cref="CompleteNoDebtAsync"/>, which also decides the caching:
    /// unlike <see cref="BuildFailedDeuda"/>, this is a real answer.
    /// </summary>
    private static BcraDeudaData BuildNoDebtDeuda(long? identificacion, string? denominacion)
        => new()
        {
            Identificacion       = identificacion,
            Denominacion         = denominacion,
            Estado               = BcraDeudaConsultaEstado.NoDebtReported,
            EntidadesInformantes = new(),
            HistorialSituaciones = new(),
            HistorialMontos      = new(),
            HistorialMontosPorSituacion = new(),
            EntidadesDetalle     = new(),
            FetchedAt            = DateTime.UtcNow
        };
}
