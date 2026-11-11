// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// The <c>[{ source, asOf, live }]</c> list behind a score — built when a re-score runs (and frozen into
/// [AnalysisRescores].[SourcesJson]) and read back for the analysis detail.
/// </summary>
public static class RescoreSources
{
    /// <summary>camelCase, the shape the Portal reads and SourcesJson stores.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// The sources of a score computed at <paramref name="now"/>: the BCRA, live when its Central de
    /// Deudores gave a real answer (debt found or none reported) and dated when that answer was actually
    /// FETCHED — <c>Deuda.FetchedAt</c>, which BcraService's 24 h cache can put up to a day before
    /// <paramref name="now"/> (<paramref name="now"/> only for a block without a stamp) — and the four
    /// entity snapshots with their refresh dates. A failed or missing deuda is not live and has no date.
    /// A snapshot the entity does not hold gets a null date.
    /// </summary>
    internal static IReadOnlyList<RescoreSource> FromEnrichment(EnrichmentSnapshot enrichment, DateTime now)
    {
        var entity   = enrichment.Entity;
        var deuda    = enrichment.BcraData?.Deuda;
        var bcraLive = deuda is { Estado: BcraDeudaConsultaEstado.DebtFound or BcraDeudaConsultaEstado.NoDebtReported };
        DateTime? bcraAsOf = bcraLive
            ? (deuda!.FetchedAt > DateTime.MinValue ? DateTime.SpecifyKind(deuda.FetchedAt, DateTimeKind.Utc) : now)
            : null;

        return
        [
            new RescoreSource(RescoreSource.Bcra,   bcraAsOf, bcraLive),
            new RescoreSource(RescoreSource.Lufe,   entity?.LufeDataJson   is not null ? entity.LufeDataUpdatedAt   : null, false),
            new RescoreSource(RescoreSource.Casfog, entity?.CasfogDataJson is not null ? entity.CasfogDataUpdatedAt : null, false),
            new RescoreSource(RescoreSource.Arca,   entity?.FiscalDataJson is not null ? entity.FiscalDataUpdatedAt : null, false),
            new RescoreSource(RescoreSource.Nosis,  entity?.NosisDataJson  is not null ? entity.NosisDataUpdatedAt  : null, false),
        ];
    }

    /// <summary>
    /// The sources of the CURRENT score of a result, for the detail. A re-scored result answers from
    /// <paramref name="latestRescoreSourcesJson"/>, which is exact. One never re-scored has no record
    /// of its snapshot dates, so they are derived: the BCRA is live when its deuda gave a real answer
    /// (<paramref name="bcraAnswered"/>), dated by the deuda block's own <paramref name="bcraFetchedAt"/>
    /// when it carries one and by <paramref name="generatedAt"/> otherwise; a snapshot's date is the entity's
    /// refresh date only when that is not later than <paramref name="generatedAt"/> — a later one means
    /// the entity was refreshed after the copy was taken, and the copy's own date is unknown (null).
    /// Never throws: an unreadable SourcesJson falls back to the derivation.
    /// </summary>
    public static IReadOnlyList<RescoreSource> ForCurrentResult(
        string? latestRescoreSourcesJson, DateTime generatedAt, bool bcraAnswered, Entity? entity,
        DateTime? bcraFetchedAt)
    {
        if (!string.IsNullOrWhiteSpace(latestRescoreSourcesJson))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<List<RescoreSource>>(latestRescoreSourcesJson, JsonOpts);
                if (stored is { Count: > 0 })
                    return stored;
            }
            catch (JsonException)
            {
                // Fall through to the derivation: a source list is context, never worth a failed read.
            }
        }

        return
        [
            new RescoreSource(RescoreSource.Bcra, bcraAnswered ? bcraFetchedAt ?? generatedAt : null, bcraAnswered),
            Snapshot(RescoreSource.Lufe,   entity?.LufeDataJson,   entity?.LufeDataUpdatedAt,   generatedAt),
            Snapshot(RescoreSource.Casfog, entity?.CasfogDataJson, entity?.CasfogDataUpdatedAt, generatedAt),
            Snapshot(RescoreSource.Arca,   entity?.FiscalDataJson, entity?.FiscalDataUpdatedAt, generatedAt),
            Snapshot(RescoreSource.Nosis,  entity?.NosisDataJson,  entity?.NosisDataUpdatedAt,  generatedAt),
        ];
    }

    /// <summary>
    /// The raw <c>$.deuda.fetchedAt</c> JSON_VALUE as a UTC instant, or null when absent, unparseable or
    /// the year-1 default an old failed block was stored with. Never throws.
    /// </summary>
    public static DateTime? ParseFetchedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!DateTime.TryParse(
                raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
            return null;

        return parsed.Year <= 1 ? null : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static RescoreSource Snapshot(string source, string? json, DateTime? updatedAt, DateTime generatedAt)
        => new(source, json is not null && updatedAt is { } at && at <= generatedAt ? at : null, false);
}
