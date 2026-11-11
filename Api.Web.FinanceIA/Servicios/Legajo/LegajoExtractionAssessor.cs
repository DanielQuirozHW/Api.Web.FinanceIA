// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Turns an extraction tree into a <see cref="LegajoExtractionAssessment"/>: the integrity checks, the
/// two review queues (missing required fields; fields flagged for low confidence or a failed check),
/// the holder-CUIT comparison with the legajo, and the mean confidence. Pure — the same function serves
/// the pipeline (the run's own reading) and the review endpoints (the effective values).
/// </summary>
public static class LegajoExtractionAssessor
{
    /// <summary>Assesses <paramref name="tree"/>.</summary>
    /// <param name="schema">The document type's schema.</param>
    /// <param name="tree">A normalized ResultJson tree, or the effective one.</param>
    /// <param name="integrity">The checks.</param>
    /// <param name="periodEnd">Documents.PeriodEnd, for the fiscal-year check.</param>
    /// <param name="legajoCuit">The legajo's person CUIT — compared in C#, never sent anywhere.</param>
    /// <param name="confidenceThreshold">Legajo:FieldConfidenceThreshold.</param>
    /// <param name="correctedPaths">
    /// Paths an analyst corrected. A corrected field, or a row with a corrected field, is no longer
    /// low-confidence: the analyst looked at it.
    /// </param>
    public static LegajoExtractionAssessment Assess(
        LegajoExtractionSchema schema,
        JsonObject tree,
        ILegajoIntegrityService integrity,
        DateOnly? periodEnd,
        string legajoCuit,
        decimal confidenceThreshold,
        IReadOnlyCollection<string>? correctedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(integrity);

        correctedPaths ??= [];

        // The service guards its own arithmetic; this is the second line, so an overflow can never
        // break the pipeline, a GET or the documents list.
        LegajoIntegrityReport report;
        try
        {
            report = integrity.Evaluate(schema.DocumentType, tree, periodEnd);
        }
        catch (ArithmeticException)
        {
            report = new LegajoIntegrityReport
            {
                Passed     = false,
                AmountsUnit = LegajoJsonValues.FieldString(tree, V.AmountsUnitPath) ?? V.Pesos,
                Checks     = [LegajoIntegrityService.ArithmeticFailure()],
                Skipped    = []
            };
        }

        // ── Missing ────────────────────────────────────────────────────────────
        var missing = schema.RequiredFieldPaths
            .Where(path => LegajoJsonPointer.Get(tree, path + "/value") is null)
            .ToList();

        if (schema.RequiredAnyArrayPaths.Count > 0
            && schema.RequiredAnyArrayPaths.All(path => LegajoJsonValues.ArrayAt(tree, path).Count == 0))
        {
            missing.Add(schema.RequiredAnyArrayPaths[0]);
        }

        foreach (var requirement in schema.Requirements)
        {
            if (!requirement.IsMet(tree) && !missing.Contains(requirement.Path))
                missing.Add(requirement.Path);
        }

        // ── Flagged ────────────────────────────────────────────────────────────
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Flag(string path, string reason)
        {
            if (!reasons.TryGetValue(path, out var list))
                reasons[path] = list = [];
            if (!list.Contains(reason))
                list.Add(reason);
        }

        var confidences = new List<decimal>();
        foreach (var unit in ProvenanceUnits(schema.Root, tree, string.Empty))
        {
            if (!unit.HasValue)
                continue;

            confidences.Add(unit.Confidence);

            // The holder CUIT is compared, not reviewed: it is never shown nor correctable, and the
            // classification already vetted it — a doubt about it is not the analyst's queue.
            if (unit.Path == V.HolderCuitPath)
                continue;

            var corrected = correctedPaths.Any(p => p == unit.Path || p.StartsWith(unit.Path + "/", StringComparison.Ordinal));
            if (unit.Confidence < confidenceThreshold && !corrected)
                Flag(unit.Path, LegajoExtractionAssessment.ReasonLowConfidence);
        }

        // A total-vs-parts check flags only its printed total (PathsToFlag); its FieldPaths still name
        // every operand so the Portal can highlight them when the check is opened.
        foreach (var check in report.Checks.Where(c => !c.Passed))
            foreach (var path in check.PathsToFlag)
                Flag(path, LegajoExtractionAssessment.ReasonCheckFailed);

        var flagged = reasons
            .Select(kv => new LegajoFlaggedField(kv.Key, kv.Value))
            .OrderBy(f => f.FieldPath, StringComparer.Ordinal)
            .ToList();

        // ── Holder CUIT (defence in depth after the classification) ────────────
        string? holderCuit = null;
        bool?   matches    = null;
        var read = LegajoCuit.Digits(LegajoJsonValues.FieldString(tree, V.HolderCuitPath));
        if (read is not null && LegajoCuit.HasValidCheckDigit(read))
        {
            holderCuit = LegajoCuit.Format(read);
            matches    = string.Equals(read, LegajoCuit.Digits(legajoCuit), StringComparison.Ordinal);
        }

        decimal? mean;
        try
        {
            mean = confidences.Count == 0 ? null : Math.Round(confidences.Average(), 4);
        }
        catch (ArithmeticException)
        {
            mean = null;
        }

        return new LegajoExtractionAssessment(report, missing, flagged, holderCuit, matches, mean);
    }

    /// <summary>A unit the model gave a confidence for: a provenance field, or an array row.</summary>
    private readonly record struct ProvenanceUnit(string Path, bool HasValue, decimal Confidence);

    private static IEnumerable<ProvenanceUnit> ProvenanceUnits(LegajoSchemaObject node, JsonNode? data, string path)
    {
        if (data is not JsonObject obj)
            yield break;

        foreach (var property in node.Properties)
        {
            var childPath = LegajoJsonPointer.Child(path, property.Name);
            var child = obj[property.Name];

            switch (property.Node)
            {
                case LegajoSchemaField when child is JsonObject field:
                    yield return new ProvenanceUnit(childPath, field["value"] is not null,
                        LegajoJsonValues.Decimal(field["confidence"]) ?? 0m);
                    break;

                case LegajoSchemaObject nested:
                    foreach (var unit in ProvenanceUnits(nested, child, childPath))
                        yield return unit;
                    break;

                case LegajoSchemaArray array when child is JsonArray rows:
                    for (var i = 0; i < rows.Count; i++)
                    {
                        if (rows[i] is not JsonObject row)
                            continue;

                        var rowPath  = LegajoJsonPointer.Child(childPath, i);
                        var hasValue = array.Items.Properties.Any(p => row[p.Name] is not null);
                        yield return new ProvenanceUnit(rowPath, hasValue, LegajoJsonValues.Decimal(row["confidence"]) ?? 0m);
                    }
                    break;
            }
        }
    }
}
