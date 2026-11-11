// Project : Api.Web.FinanceIA
// Created : 2026-11-01

using System.Collections.Concurrent;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Logs a problem with one configuration value ONCE per process, however many times the value is
/// read. Built for settings that scoped services resolve on every request (the extraction model, the
/// legajo models): an invalid App Setting must be loud in Application Insights, not one Error per
/// analysis. App Service restarts the process when an App Setting changes, so a corrected value is
/// re-evaluated — and a still-wrong one logged again — on the next start.
///
/// <para>
/// Only the setting NAME and a value cut to <see cref="MaxValueLength"/> characters are logged. Use it
/// for model ids, efforts and numbers — never for a key, a secret or a connection string.
/// </para>
/// </summary>
public static class ConfigurationProblemLog
{
    /// <summary>The longest stretch of a configured value that is ever logged.</summary>
    public const int MaxValueLength = 100;

    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Logs <paramref name="problem"/> at <paramref name="level"/> the first time this
    /// (<paramref name="settingName"/>, <paramref name="value"/>, <paramref name="problem"/>) triple is
    /// seen in this process; later calls are no-ops. Returns true when it logged.
    /// </summary>
    public static bool LogOnce(
        ILogger logger,
        LogLevel level,
        string settingName,
        string? value,
        string problem)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingName);
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        var shown = Truncate(value);

        if (!Seen.TryAdd($"{settingName}\u001f{shown}\u001f{problem}", 0))
            return false;

        logger.Log(level,
            "Configuration setting {SettingName} has an unusable value {SettingValue}: {Problem}",
            settingName, shown, problem);

        return true;
    }

    private static string Truncate(string? value)
    {
        if (value is null)
            return "(null)";

        var trimmed = value.Trim();
        return trimmed.Length > MaxValueLength ? trimmed[..MaxValueLength] + "…" : trimmed;
    }
}
