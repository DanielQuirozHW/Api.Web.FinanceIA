// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Central de Deudores traffic shaping, bound from the "Bcra" configuration section.
/// <para>
/// WHY THIS EXISTS: on 2026-10-19 the BCRA Central de Deudores answered our App Service with TCP
/// connection resets for a few minutes and then recovered on its own, while the estadísticas API
/// stayed healthy throughout. The working hypothesis is a per-source-IP rate limit that, once
/// tripped, resets connections for a while — and our own retries and parallel legs were exactly the
/// burst that would trip and then extend it. These knobs govern the Central de Deudores only; the
/// estadísticas traffic (macro, cambiario) is untouched by every one of them.
/// </para>
/// </summary>
public sealed record BcraOptions
{
    /// <summary>
    /// How long a Central de Deudores <c>LookupFailed</c> (deuda or cheques) is cached per CUIT.
    /// Default 60 s.
    /// <para>
    /// A failure used to be never cached, so every "Reintentar consulta" or "Actualizar" click
    /// during a penalty fired a fresh burst and lengthened it. The cached value is the honest
    /// <c>LookupFailed</c> block, so callers still see the outage for what it is. Zero or negative
    /// disables the negative cache (the pre-2026-10 behaviour). The success TTLs are not affected.
    /// </para>
    /// </summary>
    public int FailureCacheSeconds { get; init; } = 60;

    /// <summary>
    /// How many Central de Deudores HTTP requests may be in flight at once, process-wide. Default 2;
    /// values below 1 are treated as 1. Callers beyond the limit queue, they are not refused.
    /// </summary>
    public int CentralDeudoresMaxConcurrency { get; init; } = 2;

    /// <summary>
    /// The wall-clock budget for one Central de Deudores lookup — every leg it sequences (Deudas,
    /// Historicas, Cheques), including the time spent queued for a concurrency permit. Default 40 s,
    /// the same ceiling a single BCRA request had before the legs were sequenced. When it runs out
    /// the remaining legs report <c>LookupFailed</c> rather than waiting on: the analysis and
    /// entity-create paths call with <c>CancellationToken.None</c>, so without it a queued lookup
    /// would have no upper bound at all.
    /// </summary>
    public int CentralDeudoresLookupBudgetSeconds { get; init; } = 40;
}
