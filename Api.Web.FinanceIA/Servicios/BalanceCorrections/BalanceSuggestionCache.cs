// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// A verified suggestion held in the in-process <c>IMemoryCache</c>, with the instant it was computed.
/// </summary>
/// <param name="Response">The 200 body exactly as it was returned (its <c>CachedAt</c> null).</param>
/// <param name="CachedAt">When it was computed and cached, UTC.</param>
public sealed record CachedBalanceSuggestion(BalanceCorrectionSuggestionResponse Response, DateTimeOffset CachedAt);

/// <summary>
/// The cache of correction suggestions, so re-opening "Sugerir corrección" on the same figures does not pay
/// for a second model call. The key is (tenant, result, SHA-256 of the stored BalanceJson): a correction
/// rewrites BalanceJson, so the key changes by itself and a stale proposal is never served. Only 200s are
/// cached, for <see cref="Ttl"/>, and only up to <see cref="MaxEntryBytes"/>.
/// </summary>
/// <remarks>
/// In-process (per App Service instance). Concurrent first opens on ONE instance share one model call
/// (<see cref="BalanceSuggestionInFlight"/>); two instances can still each call. The entry holds the crops and
/// pages in memory only — never logged, never persisted.
/// </remarks>
public static class BalanceSuggestionCache
{
    /// <summary>How long a suggestion is served from the cache (absolute).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    /// <summary>Largest response cached, by <see cref="EstimatedBytes"/>.</summary>
    public const long MaxEntryBytes = 8L * 1024 * 1024;

    private const string Prefix = "balance-suggestion:";

    /// <summary>The cache key of a suggestion over <paramref name="balanceJson"/>.</summary>
    public static string Key(string tenantId, Guid resultId, string balanceJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(balanceJson);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(balanceJson)));
        return $"{Prefix}{tenantId}:{resultId:N}:{hash}";
    }

    /// <summary>
    /// The response's approximate size: the base64 of every crop and page (which dominate it) plus the
    /// strings beside them. An upper-ish bound, not an exact count.
    /// </summary>
    public static long EstimatedBytes(BalanceCorrectionSuggestionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        long bytes = response.Message.Length + response.Model.Length + response.Outcome.Length;
        bytes += response.FailedChecksBefore.Sum(c => (long)c.Length) + response.FailedChecksAfter.Sum(c => (long)c.Length);

        foreach (var item in response.Suggestions)
        {
            bytes += item.Path.Length + (item.PrintedLabel?.Length ?? 0) + (item.Derivation?.Length ?? 0) + 128;
            bytes += (item.Evidence?.Data.Length ?? 0) + (item.EvidenceSecondary?.Data.Length ?? 0);
        }

        foreach (var page in response.Pages)
            bytes += page.Image.Data.Length + 64;

        return bytes;
    }

    /// <summary>True when the response is small enough to cache.</summary>
    public static bool IsCacheable(BalanceCorrectionSuggestionResponse response) =>
        EstimatedBytes(response) <= MaxEntryBytes;
}
