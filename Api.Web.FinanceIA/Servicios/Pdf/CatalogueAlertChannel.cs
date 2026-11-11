// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using System.Text.Json;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>What an alert channel's stored JSON turned out to hold.</summary>
internal enum AlertChannelState
{
    /// <summary>Null, blank or the literal <c>null</c>: this analysis carries nothing on the channel.</summary>
    Absent,

    /// <summary>Read; the list may still be empty.</summary>
    Ready,

    /// <summary>Held something that could not be interpreted — never to be printed as "no findings".</summary>
    Unreadable,
}

/// <summary>
/// Reads <c>AnalysisResults.CatalogueAlerts</c> — the deterministic channel — for the PDF, rule for rule as the
/// Portal reads it (<c>apps/portal/src/lib/analysis-alerts.ts</c>, <c>parseCatalogueAlerts</c>), so the report and the
/// screen agree on which rows exist:
/// <list type="bullet">
///   <item>null, blank or the literal <c>null</c> → <see cref="AlertChannelState.Absent"/>;</item>
///   <item>invalid JSON or anything but an array → <see cref="AlertChannelState.Unreadable"/>;</item>
///   <item>an element is kept only when it is an object with a non-blank <c>description</c>; a code-less one is kept
///   with an empty code; strings are trimmed and a blank one is null;</item>
///   <item>a metric is read only from a JSON number (a numeric string is NOT coerced) and only when finite;</item>
///   <item>a non-empty array that yields no usable element is <see cref="AlertChannelState.Unreadable"/>.</item>
/// </list>
/// Pure: the caller logs (counts only, never the payload — it holds the company's figures).
/// </summary>
internal static class CatalogueAlertChannel
{
    /// <summary>One catalogue finding as the PDF prints it.</summary>
    internal sealed record Item
    {
        /// <summary>The wire severity as sent (<c>low|medium|high|critical</c>); null when it was not a string.</summary>
        public string? Level { get; init; }

        public string Code { get; init; } = string.Empty;

        /// <summary>The es-AR title from the tenant's catalogue row; null when blank.</summary>
        public string? Title { get; init; }

        public required string Description { get; init; }

        public string? MetricKey { get; init; }

        public double? MetricValue { get; init; }

        public double? ThresholdValue { get; init; }

        /// <summary>The raw <c>metric_unit</c> token, deliberately not narrowed (see <see cref="CatalogueMetricFormat"/>).</summary>
        public string? MetricUnit { get; init; }
    }

    /// <summary>The channel read: its state, the usable rows in stored order, and how many elements arrived.</summary>
    internal sealed record Result(AlertChannelState State, IReadOnlyList<Item> Alerts, int Received)
    {
        /// <summary>Elements that arrived but could not be used.</summary>
        public int Dropped => Received - Alerts.Count;

        internal static readonly Result Absent = new(AlertChannelState.Absent, [], 0);
    }

    /// <summary>Reads the stored JSON. Never throws.</summary>
    internal static Result Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Absent;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return new Result(AlertChannelState.Unreadable, [], 0);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            // The literal "null" says "nothing here", not a malformed payload.
            if (root.ValueKind == JsonValueKind.Null)
                return Result.Absent;

            if (root.ValueKind != JsonValueKind.Array)
                return new Result(AlertChannelState.Unreadable, [], 0);

            var alerts = new List<Item>();
            int received = 0;
            foreach (JsonElement element in root.EnumerateArray())
            {
                received++;
                if (Read(element) is { } item)
                    alerts.Add(item);
            }

            AlertChannelState state = received > 0 && alerts.Count == 0
                ? AlertChannelState.Unreadable
                : AlertChannelState.Ready;

            return new Result(state, alerts, received);
        }
    }

    private static Item? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        string? description = Str(element, "description");
        if (description is null)
            return null;

        return new Item
        {
            Level          = Str(element, "level"),
            Code           = Str(element, "code") ?? string.Empty,
            Title          = Str(element, "title"),
            Description    = description,
            MetricKey      = Str(element, "metric_key"),
            MetricValue    = Num(element, "metric_value"),
            ThresholdValue = Num(element, "threshold_value"),
            MetricUnit     = Str(element, "metric_unit"),
        };
    }

    private static string? Str(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;

        string trimmed = value.GetString()!.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static double? Num(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    }
}
