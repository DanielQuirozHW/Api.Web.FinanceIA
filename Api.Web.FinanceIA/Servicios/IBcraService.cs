// Project : Api.Web.FinanceIA
// Created : 2026-05-08
// Modified: 2026-10-19

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Provides access to public BCRA (Banco Central de la República Argentina) APIs.
/// All methods are fault-isolated and never throw.
/// <see cref="GetDeudaAsync"/> always returns a <see cref="BcraDeudaData"/> instance whose
/// <see cref="BcraDeudaData.Estado"/> discriminates between a successful lookup, no debt reported,
/// and a transient API failure. The Central de Deudores answers HTTP 404 ("No se encontró datos para
/// la identificación ingresada.") for a CUIT with nothing on file — on Deudas and ChequesRechazados
/// alike — and both methods map that 404 to their no-data outcome, never to <c>LookupFailed</c>.
/// Macro and cambiario methods return <c>null</c> on error.
/// Results are cached with IMemoryCache:
///   - Central de Deudores: 24 h per CUIT (DebtFound / NoDebtReported only; not when the
///     Deudas/Historicas leg failed transiently, so the next call fills the history)
///   - Cheques rechazados : 24 h per CUIT (ChequesFound / NoChequesReported only)
///   - Monetary variables  : 1 h
///   - Exchange-rate data  : 1 h
/// </summary>
public interface IBcraService
{
    /// <summary>
    /// Queries the BCRA Central de Deudores for the given CUIT.
    /// Always returns a non-null result; inspect <see cref="BcraDeudaData.Estado"/> to
    /// distinguish <c>DebtFound</c>, <c>NoDebtReported</c>, and <c>LookupFailed</c>.
    /// A CUIT with no reported debt is a good outcome (<c>NoDebtReported</c>), distinct from a failed
    /// lookup: BCRA signals it either with HTTP 404 or with a 200 carrying no períodos / no entidades.
    /// A transport error or timeout, a 5xx, a 429, any other 4xx, or a 200 whose body cannot be parsed
    /// is <c>LookupFailed</c>.
    /// <para>
    /// Estado and every current-período field come from Deudas alone. The three history series
    /// (<see cref="BcraDeudaData.HistorialSituaciones"/>, <see cref="BcraDeudaData.HistorialMontos"/>,
    /// <see cref="BcraDeudaData.HistorialMontosPorSituacion"/>) come from Deudas/Historicas, which is
    /// requested on <c>DebtFound</c> AND on <c>NoDebtReported</c>: a CUIT with no debt in the latest
    /// período may still have had debt in the 24 before it, and then its <c>NoDebtReported</c> block
    /// carries those months while SituacionMaxima, MontoTotal and PeriodoConsultado stay null and
    /// EntidadesInformantes / EntidadesDetalle stay empty. A failed history leg never changes Estado.
    /// </para>
    /// Cache key: <c>bcra:deuda:{cuit}</c>, TTL 24 h — except when the history leg failed
    /// transiently, which is not cached so the next call retries it; a <c>LookupFailed</c> is cached
    /// only for <c>Bcra:FailureCacheSeconds</c> (default 60 s), so a retry during a BCRA penalty sends
    /// nothing.
    /// </summary>
    Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct);

    /// <summary>
    /// Queries the BCRA Central de Deudores for rejected cheques reported against the given CUIT.
    /// Always returns a non-null result; inspect <see cref="BcraChequesData.Estado"/> to
    /// distinguish <c>ChequesFound</c>, <c>NoChequesReported</c>, and <c>LookupFailed</c>.
    /// A CUIT with no rejected cheques is a good outcome (<c>NoChequesReported</c>), distinct
    /// from a failed lookup. Cache key: <c>bcra:cheques:{cuit}</c>, TTL 24 h; a <c>LookupFailed</c>
    /// is cached only for <c>Bcra:FailureCacheSeconds</c> (default 60 s).
    /// </summary>
    Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct);

    /// <summary>
    /// Retrieves the most recent macroeconomic (monetary) variables.
    /// Cache key: <c>bcra:macro</c>, TTL 1 h.
    /// </summary>
    Task<BcraMacroData?> GetMacroAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves the most recent exchange-rate variables.
    /// Cache key: <c>bcra:cambiario</c>, TTL 1 h.
    /// </summary>
    Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct);

    /// <summary>
    /// Fetches the four data sources — deuda, macro, cambiario and cheques rechazados — in parallel and
    /// returns a combined <see cref="BcraData"/>. Partial results are included; the method returns
    /// <c>null</c> only when deuda, macro AND cambiario all fail. Cheques never decide that: a cheques
    /// failure leaves <see cref="BcraData.Cheques"/> as a <c>LookupFailed</c> block (never null, never an
    /// exception) with the rest of the payload intact, and a cheques success never resurrects a payload
    /// the other three would have nulled. Since scoring algorithm version 5 the cheques block is a score
    /// input; its <c>Estado</c> is what tells the engine "no cheques" from "no answer".
    /// </summary>
    Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct);
}
