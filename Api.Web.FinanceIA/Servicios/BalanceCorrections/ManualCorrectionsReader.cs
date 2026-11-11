// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// What <c>BalanceJson.manual_corrections</c> says, summarised: how many distinct figures ("cifras") were
/// corrected, when and by whom the latest correction was made, its reason, and which paths are corrected.
/// </summary>
/// <param name="Count">Distinct corrected paths.</param>
/// <param name="LastAt">UTC instant of the latest entry; null when no entry carries a readable one.</param>
/// <param name="LastBy">Users.UserId of the latest entry; null when unreadable.</param>
/// <param name="LastReason">The latest entry's reason — free text: never logged, never in a prompt.</param>
/// <param name="Paths">Every corrected path.</param>
/// <param name="Entries">Every readable entry, in trail order, with its origin.</param>
public sealed record ManualCorrectionsSummary(
    int Count, DateTime? LastAt, Guid? LastBy, string? LastReason, IReadOnlySet<string> Paths,
    IReadOnlyList<ManualCorrectionEntry> Entries)
{
    /// <summary>
    /// The origin of the trail entry written by the same correction as an [AnalysisEdits] row — same path,
    /// same author, same instant (both are the re-score's RescoredAt) — or "manual" when none matches (an
    /// entry written before <c>origin</c> existed carries none and reads as manual too).
    /// </summary>
    public string OriginOf(string path, Guid by, DateTime atUtc)
    {
        foreach (var entry in Entries)
        {
            if (entry.At is { } at && at == DateTime.SpecifyKind(atUtc, DateTimeKind.Utc)
                && entry.By == by
                && string.Equals(entry.Path, path, StringComparison.Ordinal))
            {
                return entry.Origin;
            }
        }

        return BalanceCorrectionOrigins.Manual;
    }
}

/// <summary>One <c>manual_corrections</c> entry: path, instant, author and origin (manual when absent).</summary>
public sealed record ManualCorrectionEntry(string Path, DateTime? At, Guid? By, string Origin);

/// <summary>
/// Reads <see cref="ManualCorrectionsSummary"/> out of a persisted BalanceJson, at read time — the ONE
/// reader the reliability facts, the analysis detail and the PDF share, so they cannot count differently.
/// Never throws: a missing, empty or malformed trail yields null.
/// </summary>
public static class ManualCorrectionsReader
{
    /// <summary>The summary, or null when the balance carries no correction.</summary>
    public static ManualCorrectionsSummary? Read(string? balanceJson)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(balanceJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(BalanceCorrectionApplier.ManualCorrectionsKey, out var trail)
                || trail.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var paths   = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<ManualCorrectionEntry>();
            DateTime? lastAt     = null;
            Guid?     lastBy     = null;
            string?   lastReason = null;
            var       hasLast    = false;

            foreach (var entry in trail.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                    || path.GetString() is not { Length: > 0 } pathText)
                {
                    continue;
                }

                paths.Add(pathText);

                var at = entry.TryGetProperty("at", out var atElement) && atElement.ValueKind == JsonValueKind.String
                         && DateTime.TryParse(atElement.GetString(), CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : (DateTime?)null;

                Guid? entryBy = entry.TryGetProperty("by", out var byElement) && byElement.ValueKind == JsonValueKind.String
                                && Guid.TryParse(byElement.GetString(), out var byGuid) ? byGuid : null;

                // A known origin as written; anything else (absent, null, unknown) reads as manual.
                var origin = entry.TryGetProperty("origin", out var originElement) && originElement.ValueKind == JsonValueKind.String
                             && BalanceCorrectionOrigins.All.Contains(originElement.GetString(), StringComparer.Ordinal)
                    ? originElement.GetString()!
                    : BalanceCorrectionOrigins.Manual;

                entries.Add(new ManualCorrectionEntry(pathText, at, entryBy, origin));

                // Entries are appended in order; a later entry with an equal or later instant is the latest.
                if (!hasLast || at is null || lastAt is null || at >= lastAt)
                {
                    hasLast    = true;
                    lastAt     = at ?? lastAt;
                    lastBy     = entryBy;
                    lastReason = entry.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                        ? reason.GetString()
                        : null;
                }
            }

            return paths.Count == 0
                ? null
                : new ManualCorrectionsSummary(paths.Count, lastAt, lastBy, lastReason, paths, entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
