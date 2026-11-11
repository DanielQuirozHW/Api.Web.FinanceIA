// Project : Api.Web.FinanceIA
// Created : 2026-11-01

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// What one balance-extraction request is built from: the model spec, the effort actually sent (or
/// none) and the per-attempt timeout. Resolved from <see cref="ExtractionModelOptions"/> by
/// <see cref="Resolve"/>, which NEVER throws: an analysis must not fail because an App Setting was
/// mistyped, so every unusable value degrades to the default and is logged once.
/// </summary>
/// <param name="Spec">The model and its request differences.</param>
/// <param name="Effort">The <c>output_config.effort</c> to send, or null to send none.</param>
/// <param name="Timeout">How long one attempt may take.</param>
public sealed record ExtractionModelSelection(
    ExtractionModelSpec Spec,
    string?             Effort,
    TimeSpan            Timeout)
{
    /// <summary>Setting name of the model id.</summary>
    public const string ModelSetting = "Anthropic:ExtractionModel";

    /// <summary>Setting name of the effort.</summary>
    public const string EffortSetting = "Anthropic:ExtractionEffort";

    /// <summary>Setting name of the per-attempt timeout override.</summary>
    public const string TimeoutSetting = "Anthropic:ExtractionTimeoutSeconds";

    /// <summary>The selection with nothing configured: today's request, exactly.</summary>
    public static ExtractionModelSelection Default { get; } = new(
        ExtractionModelCatalog.Default,
        null,
        TimeSpan.FromSeconds(ExtractionModelCatalog.Default.TimeoutSeconds));

    /// <summary>
    /// Resolves <paramref name="options"/> against <see cref="ExtractionModelCatalog"/>:
    /// <list type="bullet">
    /// <item>model unset or blank → the default, silently; unknown → the default + an Error;</item>
    /// <item>effort invalid → none sent + an Error; valid but the model has no effort support →
    /// none sent + a Warning;</item>
    /// <item>timeout outside 30..900 → clamped + a Warning.</item>
    /// </list>
    /// Each problem is logged once per process (<see cref="ConfigurationProblemLog"/>).
    /// </summary>
    public static ExtractionModelSelection Resolve(ExtractionModelOptions? options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (options is null)
            return Default;

        // ── Model ───────────────────────────────────────────────────────────────
        var spec = ExtractionModelCatalog.Default;
        if (!string.IsNullOrWhiteSpace(options.ExtractionModel))
        {
            var found = ExtractionModelCatalog.Find(options.ExtractionModel);
            if (found is null)
            {
                ConfigurationProblemLog.LogOnce(logger, LogLevel.Error, ModelSetting, options.ExtractionModel,
                    $"not a supported extraction model; using {ExtractionModelCatalog.Default.Id}. " +
                    $"Supported: {string.Join(", ", ExtractionModelCatalog.All.Select(m => m.Id))}.");
            }
            else
            {
                spec = found;
            }
        }

        // ── Effort ──────────────────────────────────────────────────────────────
        string? effort = null;
        if (!string.IsNullOrWhiteSpace(options.ExtractionEffort))
        {
            var normalized = ExtractionModelCatalog.NormalizeEffort(options.ExtractionEffort);
            if (normalized is null)
            {
                ConfigurationProblemLog.LogOnce(logger, LogLevel.Error, EffortSetting, options.ExtractionEffort,
                    $"not a valid effort; none is sent (the API default applies). " +
                    $"Valid: {string.Join(", ", ExtractionModelCatalog.ValidEfforts)}.");
            }
            else if (!spec.SupportsEffort)
            {
                ConfigurationProblemLog.LogOnce(logger, LogLevel.Warning, EffortSetting, options.ExtractionEffort,
                    $"ignored: {spec.Id} is configured without effort support, so no output_config is sent.");
            }
            else
            {
                effort = normalized;
            }
        }

        // ── Timeout ─────────────────────────────────────────────────────────────
        var seconds = spec.TimeoutSeconds;
        if (options.ExtractionTimeoutSeconds is { } configured)
        {
            seconds = Math.Clamp(configured, ExtractionModelCatalog.MinTimeoutSeconds, ExtractionModelCatalog.MaxTimeoutSeconds);
            if (seconds != configured)
            {
                ConfigurationProblemLog.LogOnce(logger, LogLevel.Warning, TimeoutSetting,
                    configured.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"outside {ExtractionModelCatalog.MinTimeoutSeconds}..{ExtractionModelCatalog.MaxTimeoutSeconds}; using {seconds} s.");
            }
        }

        return new ExtractionModelSelection(spec, effort, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// The selection for the ONE re-send of a pass that stopped at <c>max_tokens</c>, or null when there is
    /// none. Only for a spec that sends a temperature (Sonnet 4.6): the 5.x specs already sit at
    /// <see cref="ExtractionModelCatalog.MaxEscalatedTokens"/>, and their thinking shares the cap. The cap
    /// is doubled up to <see cref="ExtractionModelCatalog.MaxEscalatedTokens"/>, and the per-attempt
    /// timeout is scaled by the same factor up to <see cref="ExtractionModelCatalog.MaxTimeoutSeconds"/>,
    /// because a longer answer takes proportionally longer to write. Nothing else changes, so the
    /// escalated request differs from the original only in <c>max_tokens</c>. Null when doubling gains
    /// nothing (a cap already at the ceiling).
    /// </summary>
    public ExtractionModelSelection? EscalateForMaxTokens()
    {
        if (Spec.Temperature is null || Spec.MaxTokens <= 0)
            return null;

        var escalated = Math.Min(Spec.MaxTokens * 2, ExtractionModelCatalog.MaxEscalatedTokens);
        if (escalated <= Spec.MaxTokens)
            return null;

        var factor  = (double)escalated / Spec.MaxTokens;
        var seconds = Math.Min(Timeout.TotalSeconds * factor, ExtractionModelCatalog.MaxTimeoutSeconds);

        return this with
        {
            Spec    = Spec with { MaxTokens = escalated },
            Timeout = TimeSpan.FromSeconds(seconds)
        };
    }
}
