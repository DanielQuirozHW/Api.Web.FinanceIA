// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>What a BCRA Central de Deudores reading says, before any score is put on it.</summary>
internal enum BcraDeudaReadingKind
{
    /// <summary>Nothing usable: no payload, no Deuda block, or the lookup failed. See <see cref="BcraDeudaReading.ExclusionReason"/>.</summary>
    Excluded,

    /// <summary>BCRA answered and the company has no debt on file.</summary>
    NoDebt,

    /// <summary>BCRA answered with debt; <see cref="BcraDeudaReading.SituacionMaxima"/> is the stored maximum.</summary>
    Debt
}

/// <summary>
/// One BCRA Central de Deudores reading.
/// </summary>
/// <param name="Kind">What the reading says.</param>
/// <param name="ExclusionReason">One of <see cref="BcraExclusionReasons"/>; non-null exactly when <paramref name="Kind"/> is Excluded.</param>
/// <param name="SituacionMaxima">The stored <c>Deuda.SituacionMaxima</c>, as is, when <paramref name="Kind"/> is Debt; otherwise null.</param>
internal readonly record struct BcraDeudaReading(BcraDeudaReadingKind Kind, string? ExclusionReason, int? SituacionMaxima);

/// <summary>
/// The scoring engine's reading of a BCRA payload, in one place: the guard
/// <see cref="ScoringService"/> applies before it scores BCRA, and what the dashboard compares when
/// it asks whether a company's situación got worse.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>ScoringService.Compute</c>, which held it inline as five guards over
/// <c>bcraData?.Deuda?.Estado</c>. The order is the engine's and is load-bearing: no payload, no Deuda
/// block, a failed lookup, no debt reported, debt found. The situación → score table stays in the engine;
/// only the reading moved.
/// </para>
/// <para>
/// The situación is the stored <c>Deuda.SituacionMaxima</c> — the maximum over the LATEST period's
/// entities, as <c>BcraService.MapDeuda</c> computes it. History is never consulted here, exactly as the
/// engine scores it.
/// </para>
/// </remarks>
internal static class BcraSituacionRule
{
    private const int BestSituacion  = 1;
    private const int WorstSituacion = 6;

    /// <summary>Reads a deserialized payload, in the engine's guard order.</summary>
    public static BcraDeudaReading Read(BcraData? data)
    {
        if (data is null)
            return new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.NoBcraData, null);

        if (data.Deuda is null)
            return new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.NoDeudaBlock, null);

        return Read(data.Deuda.Estado, data.Deuda.SituacionMaxima);
    }

    /// <summary>
    /// Reads the two scalars <c>JSON_VALUE</c> returns for <c>$.deuda.estado</c> and
    /// <c>$.deuda.situacionMaxima</c>, so a caller never has to select the blob. Parsed the way
    /// <c>AnalysisController.BuildBcraSummary</c> parses them. A missing or unparseable estado means
    /// the Deuda block is not there to read — the engine's NoDeudaBlock — because the serializer always
    /// writes estado when the block exists.
    /// </summary>
    public static BcraDeudaReading Read(string? estadoRaw, string? situacionMaximaRaw)
    {
        if (!int.TryParse(estadoRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var estado)
            || !Enum.IsDefined(typeof(BcraDeudaConsultaEstado), estado))
        {
            return new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.NoDeudaBlock, null);
        }

        int? situacion = int.TryParse(situacionMaximaRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? s
            : null;

        return Read((BcraDeudaConsultaEstado)estado, situacion);
    }

    /// <summary>
    /// The worst situación the reading supports, on BCRA's 1..6 scale, or null when it supports none.
    /// No debt reported counts as 1 — the engine scores it "equivalent to situación 1". A Debt reading
    /// whose stored maximum is missing or outside 1..6 is null: the engine scores that as neutral, not as
    /// a situación.
    /// </summary>
    public static int? Worst(BcraDeudaReading reading) => reading.Kind switch
    {
        BcraDeudaReadingKind.NoDebt => BestSituacion,
        BcraDeudaReadingKind.Debt   => reading.SituacionMaxima is int s and >= BestSituacion and <= WorstSituacion ? s : null,
        _                           => null
    };

    private static BcraDeudaReading Read(BcraDeudaConsultaEstado estado, int? situacionMaxima) => estado switch
    {
        BcraDeudaConsultaEstado.LookupFailed   => new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.LookupFailed, null),
        BcraDeudaConsultaEstado.NoDebtReported => new BcraDeudaReading(BcraDeudaReadingKind.NoDebt, null, null),
        _                                      => new BcraDeudaReading(BcraDeudaReadingKind.Debt, null, situacionMaxima)
    };
}
