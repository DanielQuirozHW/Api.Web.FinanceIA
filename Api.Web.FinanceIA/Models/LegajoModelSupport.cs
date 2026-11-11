// Project : Api.Web.FinanceIA
// Created : 2026-11-01

using Api.Web.FinanceIA.Clases;

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Which Claude models the legajo pipeline (classification and extraction) may be configured to call.
///
/// <para>
/// Every legajo request is built by <c>LegajoMessagesClient.BuildRequestJson</c> with a FORCED
/// <c>tool_choice {type:"tool"}</c> and <c>temperature 0</c>, both fixed. A model must accept both or
/// every legajo call fails with HTTP 400:
/// <list type="bullet">
/// <item>claude-sonnet-5-5 rejects a forced tool_choice (and any non-default temperature);</item>
/// <item>claude-sonnet-5 accepts a forced tool_choice on the Claude API but rejects temperature 0;</item>
/// <item>Opus 4.7 and later reject sampling parameters too.</item>
/// </list>
/// So this is an ALLOW-list of the ids known to accept both — the two the project already uses and
/// prices — not a deny-list: an id nobody has checked is refused. Adding one needs the reason and a
/// test, and a live run of the legajo harness.
/// </para>
/// </summary>
public static class LegajoModelSupport
{
    /// <summary>Setting name of the legajo extraction model.</summary>
    public const string ExtractionModelSetting = "Anthropic:LegajoExtractionModel";

    /// <summary>Setting name of the legajo classification model.</summary>
    public const string ClassificationModelSetting = "Anthropic:LegajoClassificationModel";

    /// <summary>Model ids known to accept a forced tool_choice together with temperature 0.</summary>
    public static readonly IReadOnlyList<string> ForcedToolUseModels = ["claude-haiku-4-5", "claude-sonnet-4-6"];

    /// <summary>True when <paramref name="model"/> is on <see cref="ForcedToolUseModels"/> (case-insensitive, trimmed).</summary>
    public static bool SupportsForcedToolUse(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && ForcedToolUseModels.Any(m => string.Equals(m, model.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The model a legajo service sends: <paramref name="defaultModel"/> when the setting is blank
    /// (silently, as before); the configured id, trimmed, when it is supported; otherwise
    /// <paramref name="defaultModel"/> plus an Error logged once per process.
    /// </summary>
    public static string ResolveOrDefault(string? configured, string defaultModel, string settingName, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(configured))
            return defaultModel;

        var trimmed = configured.Trim();
        if (SupportsForcedToolUse(trimmed))
            return trimmed;

        ConfigurationProblemLog.LogOnce(logger, LogLevel.Error, settingName, configured,
            $"the legajo pipeline sends a forced tool_choice with temperature 0, which this model is not known to accept; " +
            $"using {defaultModel}. Supported: {string.Join(", ", ForcedToolUseModels)}.");

        return defaultModel;
    }
}
