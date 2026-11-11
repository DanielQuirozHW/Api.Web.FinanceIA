// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios.NosisReport;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>Per compared field: equal, different, or absent from the parser's output.</summary>
internal enum GroundTruthVerdict
{
    Ok,
    Diff,
    Missing,
}

/// <summary>One compared field: its path ("summary.score") and the verdict — never either value.</summary>
internal sealed record GroundTruthField(string Path, GroundTruthVerdict Verdict);

/// <summary>
/// Compares a parsed report with a hand-written ground-truth entry for the LOCAL harness. The entry
/// holds only the fields the analyst checked; a field absent from it is not compared. The report is
/// projected to the entry's vocabulary first:
/// <list type="bullet">
/// <item><c>evolucion24m</c> → <c>{ count, first, last, lastScore }</c>;</item>
/// <item><c>chequesRechazadosPorMes</c> → <c>{ count, latest: { periodo, cantidad, monto } }</c>;</item>
/// <item>everything else as the contract names it.</item>
/// </list>
/// Numbers compare within <see cref="Tolerance"/>; strings ordinally; booleans exactly. The result
/// carries verdicts only, so printing it can never leak a value.
/// </summary>
internal static class NosisReportGroundTruth
{
    public const double Tolerance = 0.01;

    /// <summary>Entry properties that identify the file rather than describe it.</summary>
    private static readonly HashSet<string> NotCompared = new(StringComparer.Ordinal) { "alias", "sha256" };

    /// <summary>
    /// Compares a parse with an entry. The entry's <c>outcome</c> ("ok" = parsed, "not_recognized",
    /// "too_many_pages") is compared with the parse's; when the file did not parse, every other field
    /// the entry holds reads MISSING.
    /// </summary>
    public static IReadOnlyList<GroundTruthField> Compare(NosisReportParseResult result, JsonElement expectedEntry)
    {
        var actual = result.Report is null ? new JsonObject() : Project(result.Report);
        actual["outcome"] = OutcomeKey(result.Outcome);
        return CompareProjected(actual, expectedEntry);
    }

    /// <summary>Compares a parsed report (its outcome taken as "ok").</summary>
    public static IReadOnlyList<GroundTruthField> Compare(NosisManagerReport report, JsonElement expectedEntry)
    {
        var actual = Project(report);
        actual["outcome"] = OutcomeKey(NosisReportParseOutcome.Parsed);
        return CompareProjected(actual, expectedEntry);
    }

    internal static string OutcomeKey(NosisReportParseOutcome outcome) => outcome switch
    {
        NosisReportParseOutcome.Parsed        => "ok",
        NosisReportParseOutcome.NotRecognized => "not_recognized",
        NosisReportParseOutcome.TooManyPages  => "too_many_pages",
        _                                     => outcome.ToString(),
    };

    private static IReadOnlyList<GroundTruthField> CompareProjected(JsonObject actual, JsonElement expectedEntry)
    {
        var fields = new List<GroundTruthField>();

        foreach (var property in expectedEntry.EnumerateObject())
        {
            if (NotCompared.Contains(property.Name))
                continue;

            Walk(property.Value, actual[property.Name], property.Name, fields);
        }

        return fields;
    }

    /// <summary>The report in the ground truth's vocabulary.</summary>
    internal static JsonObject Project(NosisManagerReport report)
    {
        var node = JsonSerializer.SerializeToNode(report, NosisManagerReport.JsonOptions)!.AsObject();

        if (report.Evolucion24m is { } evolucion)
        {
            node["evolucion24m"] = new JsonObject
            {
                ["count"]     = evolucion.Count,
                ["first"]     = evolucion.Count > 0 ? evolucion[0].Periodo : null,
                ["last"]      = evolucion.Count > 0 ? evolucion[^1].Periodo : null,
                ["lastScore"] = evolucion.Count > 0 ? evolucion[^1].Score : null,
            };
        }

        if (report.ChequesRechazadosPorMes is { } cheques)
        {
            node["chequesRechazadosPorMes"] = new JsonObject
            {
                ["count"]  = cheques.Count,
                ["latest"] = cheques.Count > 0 ? JsonSerializer.SerializeToNode(cheques[^1], NosisManagerReport.JsonOptions) : null,
            };
        }

        return node;
    }

    private static void Walk(JsonElement expected, JsonNode? actual, string path, List<GroundTruthField> fields)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                    Walk(property.Value, actual is JsonObject o ? o[property.Name] : null, $"{path}.{property.Name}", fields);
                return;

            case JsonValueKind.Array:
                var items = expected.EnumerateArray().ToList();
                if (actual is not JsonArray array)
                {
                    fields.Add(new GroundTruthField(path, GroundTruthVerdict.Missing));
                    return;
                }

                if (array.Count != items.Count)
                {
                    fields.Add(new GroundTruthField($"{path}.length", GroundTruthVerdict.Diff));
                    return;
                }

                for (var i = 0; i < items.Count; i++)
                    Walk(items[i], array[i], $"{path}[{i}]", fields);
                return;

            default:
                fields.Add(new GroundTruthField(path, Leaf(expected, actual)));
                return;
        }
    }

    private static GroundTruthVerdict Leaf(JsonElement expected, JsonNode? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            return actual is null ? GroundTruthVerdict.Ok : GroundTruthVerdict.Diff;

        if (actual is not JsonValue value)
            return GroundTruthVerdict.Missing;

        return expected.ValueKind switch
        {
            JsonValueKind.Number => TryNumber(value, out var number) && Math.Abs(number - expected.GetDouble()) <= Tolerance
                ? GroundTruthVerdict.Ok : GroundTruthVerdict.Diff,
            JsonValueKind.String => value.TryGetValue<string>(out var text) && string.Equals(text, expected.GetString(), StringComparison.Ordinal)
                ? GroundTruthVerdict.Ok : GroundTruthVerdict.Diff,
            JsonValueKind.True or JsonValueKind.False => value.TryGetValue<bool>(out var flag) && flag == expected.GetBoolean()
                ? GroundTruthVerdict.Ok : GroundTruthVerdict.Diff,
            _ => GroundTruthVerdict.Diff,
        };
    }

    private static bool TryNumber(JsonValue value, out double number)
    {
        number = 0;
        var element = JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        number = double.Parse(element.GetRawText(), CultureInfo.InvariantCulture);
        return true;
    }
}
