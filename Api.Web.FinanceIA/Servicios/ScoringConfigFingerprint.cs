// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Identifies the EFFECTIVE scoring configuration a score was computed with (score credibility 1b-2a,
/// S4): where its values came from, and a short hash of the values themselves. Reporting only — it
/// reads the merged config after the engine has finished with it and writes nothing back.
/// <para>
/// Both are taken over the keys of <see cref="ScoringDefaults.Values"/>, the engine's full key set: a
/// stored row for any other key is not configuration the engine can read, so it neither counts as a
/// tenant source nor enters the hash.
/// </para>
/// </summary>
internal static class ScoringConfigFingerprint
{
    /// <summary>Hex characters kept from the SHA-256: 64 bits, ample to tell two configs apart.</summary>
    private const int HashLength = 16;

    /// <summary>
    /// The first 16 lowercase hex characters of a SHA-256 over the effective value of every scoring
    /// key — <paramref name="effectiveConfig"/>'s value where it holds one, the code default otherwise —
    /// written as canonical <c>key=value</c> lines, ordinal-sorted, joined with <c>\n</c>, UTF-8.
    /// <para>
    /// Deterministic: independent of dictionary order, of the current culture, and of a decimal's
    /// stored scale, so a row read back as <c>0.5000</c> hashes exactly like the default <c>0.50</c>.
    /// Pass the map AFTER the threshold-ordering fallback, since that is the map the engine scored on.
    /// </para>
    /// </summary>
    public static string ComputeHash(IReadOnlyDictionary<string, decimal> effectiveConfig)
    {
        ArgumentNullException.ThrowIfNull(effectiveConfig);

        var lines = ScoringDefaults.Values.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => key + "=" + Canonical(
                effectiveConfig.TryGetValue(key, out var value) ? value : ScoringDefaults.Values[key]));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(bytes)[..HashLength].ToLowerInvariant();
    }

    /// <summary>
    /// <see cref="ScoringConfigSources.Defaults"/> when no scoring key took a row value,
    /// <see cref="ScoringConfigSources.Tenant"/> when every one did, <see cref="ScoringConfigSources.Mixed"/>
    /// otherwise. <paramref name="rowKeys"/> are the keys a tenant or sector row supplied, MINUS any the
    /// engine then replaced with defaults (the threshold-ordering fallback): those were scored on code
    /// defaults, whatever is stored.
    /// </summary>
    public static string ClassifySource(IReadOnlySet<string> rowKeys)
    {
        ArgumentNullException.ThrowIfNull(rowKeys);

        int fromRows = ScoringDefaults.Values.Keys.Count(rowKeys.Contains);

        return fromRows == 0                              ? ScoringConfigSources.Defaults
             : fromRows == ScoringDefaults.Values.Count  ? ScoringConfigSources.Tenant
             :                                             ScoringConfigSources.Mixed;
    }

    /// <summary>
    /// A decimal in invariant culture with trailing zeros removed (<c>G29</c>), so equal values always
    /// print alike whatever scale they were stored or written with.
    /// </summary>
    internal static string Canonical(decimal value)
        => value.ToString("G29", CultureInfo.InvariantCulture);
}
