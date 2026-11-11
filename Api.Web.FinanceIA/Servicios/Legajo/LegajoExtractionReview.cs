// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The read side of the extraction review (M10 Phase 2b): evaluates a run on its effective values and
/// builds the list summary and the detailed view. Pure. The holder CUIT goes in; only whether it matches
/// comes out.
/// </summary>
public static class LegajoExtractionReview
{
    /// <summary>
    /// The run on its effective values, with the checks recomputed in C# (no model call). Null when the
    /// run has no schema or did not succeed — a failed run is never current.
    /// </summary>
    public static LegajoEffectiveExtraction? Evaluate(
        LegajoExtraction run,
        IEnumerable<LegajoCorrection> corrections,
        ILegajoIntegrityService integrity,
        DateOnly? periodEnd,
        string legajoCuit,
        decimal confidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(corrections);

        var schema = LegajoExtractionPrompts.For(run.DocumentType);
        if (schema is null || run.Status != LegajoExtractionStatuses.Succeeded)
            return null;

        var own = corrections.Where(c => c.ExtractionId == run.ExtractionId).OrderBy(c => c.CorrectedAt).ToList();

        JsonObject effective;
        IReadOnlyList<string> correctedPaths;
        try
        {
            (effective, correctedPaths) = LegajoEffectiveValues.Apply(schema, run.ResultJson, own);
        }
        catch (JsonException)
        {
            // A hand-edited or truncated ResultJson must not break the list.
            return null;
        }

        LegajoExtractionAssessment assessment;
        try
        {
            assessment = LegajoExtractionAssessor.Assess(
                schema, effective, integrity, periodEnd, legajoCuit, confidenceThreshold, correctedPaths);
        }
        catch (ArithmeticException)
        {
            // Unreachable today (the assessor reports arithmetic as a failed check); kept so no read path
            // can ever turn a stored figure into a 500.
            return null;
        }

        return new LegajoEffectiveExtraction(schema, run, own, effective, correctedPaths, assessment);
    }

    /// <summary>LegajoDocumentResponse.CurrentExtraction.</summary>
    public static LegajoExtractionSummaryResponse Summarize(LegajoEffectiveExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        var a = extraction.Assessment;
        return new LegajoExtractionSummaryResponse
        {
            ExtractionId     = extraction.Run.ExtractionId,
            SchemaVersion    = extraction.Run.SchemaVersion,
            ReadMode         = extraction.Run.ReadMode,
            Confidence       = extraction.Run.Confidence,
            ChecksPassed     = a.Checks.Passed,
            FailedChecks     = a.FailedChecks,
            MissingCount     = a.Missing.Count,
            FlaggedCount     = a.Flagged.Count,
            CorrectionsCount = extraction.Corrections.Count,
            ReviewedAt       = extraction.Run.ReviewedAt,
            ReviewedWithOverride = extraction.Run.ReviewedAt is null ? null : extraction.Run.ReviewOverride,
            ReviewReason     = extraction.Run.ReviewedAt is null ? null : extraction.Run.ReviewReason
        };
    }

    /// <summary>GET /api/documents/{id}/extraction.</summary>
    /// <param name="extraction">The evaluated run.</param>
    /// <param name="document">Its document.</param>
    /// <param name="userNames">Display names of this tenant's users, by UserId (others stay unnamed).</param>
    public static LegajoExtractionViewResponse BuildView(
        LegajoEffectiveExtraction extraction, Document document, IReadOnlyDictionary<Guid, string> userNames)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(userNames);

        var run = extraction.Run;
        var fields = new List<LegajoExtractionFieldResponse>();
        CollectFields(extraction, userNames, extraction.Schema.Root, extraction.Effective, string.Empty, null, fields);

        return new LegajoExtractionViewResponse
        {
            ExtractionId      = run.ExtractionId,
            DocumentId        = document.DocumentId,
            DocumentType      = run.DocumentType,
            LegajoStatus      = document.LegajoStatus,
            SchemaVersion     = run.SchemaVersion,
            PromptVersion     = run.PromptVersion,
            Model             = run.Model,
            ReadMode          = run.ReadMode,
            CreatedAt         = run.CreatedAt,
            ReviewedAt        = run.ReviewedAt,
            ReviewedByName    = run.ReviewedBy is { } by ? userNames.GetValueOrDefault(by) : null,
            ReviewedWithOverride = run.ReviewedAt is null ? null : run.ReviewOverride,
            ReviewReason      = run.ReviewedAt is null ? null : run.ReviewReason,
            AmountsUnit       = extraction.Assessment.Checks.AmountsUnit,
            Confidence        = run.Confidence,
            HolderCuitMatches = extraction.Assessment.HolderCuitMatches,
            Fields            = fields,
            Checks            = extraction.Assessment.Checks,
            Missing           = extraction.Assessment.Missing,
            Flagged           = extraction.Assessment.Flagged
                                    .Select(f => new LegajoFlaggedFieldResponse { FieldPath = f.FieldPath, Reasons = f.Reasons })
                                    .ToList(),
            CorrectionsCount  = extraction.Corrections.Count,
            BienesPersonalesSummary = run.DocumentType == DocumentTypes.BienesPersonales ? TrySummary(extraction) : null
        };
    }

    // The summary guards its own arithmetic; this is the second line, so a stored figure can never turn
    // the GET into a 500.
    private static LegajoBienesPersonalesSummaryResponse? TrySummary(LegajoEffectiveExtraction extraction)
    {
        try
        {
            return LegajoBienesPersonalesSummary.Build(extraction.Effective, extraction.Assessment.Checks.AmountsUnit);
        }
        catch (ArithmeticException)
        {
            return null;
        }
    }

    /// <summary>The row provenance a row's values inherit.</summary>
    private sealed record RowProvenance(int? Page, string? Quote, decimal? Confidence);

    private static void CollectFields(
        LegajoEffectiveExtraction extraction, IReadOnlyDictionary<Guid, string> userNames,
        LegajoSchemaObject node, JsonNode? data, string path, RowProvenance? row, List<LegajoExtractionFieldResponse> fields)
    {
        var obj = data as JsonObject;

        foreach (var property in node.Properties)
        {
            var childPath = LegajoJsonPointer.Child(path, property.Name);
            var child = obj?[property.Name];

            switch (property.Node)
            {
                // Compared, never shown: the view carries holderCuitMatches instead.
                case LegajoSchemaField when childPath == V.HolderCuitPath:
                    break;

                case LegajoSchemaField:
                    fields.Add(Field(extraction, userNames, property.Node, childPath, child?["value"],
                        new RowProvenance(LegajoJsonValues.Integer(child?["page"]), LegajoJsonValues.String(child?["quote"]),
                                          LegajoJsonValues.Decimal(child?["confidence"]))));
                    break;

                case LegajoSchemaValue:
                    fields.Add(Field(extraction, userNames, property.Node, childPath, child, row));
                    break;

                // A row's match-key: its members when present, the whole (null) object otherwise.
                case LegajoSchemaObject nested when row is not null:
                    if (child is JsonObject)
                        CollectFields(extraction, userNames, nested, child, childPath, row, fields);
                    else
                        fields.Add(Field(extraction, userNames, nested, childPath, null, row));
                    break;

                case LegajoSchemaObject nested:
                    CollectFields(extraction, userNames, nested, child, childPath, null, fields);
                    break;

                case LegajoSchemaArray array when child is JsonArray rows:
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var rowNode = rows[i] as JsonObject;
                        var provenance = new RowProvenance(LegajoJsonValues.Integer(rowNode?["page"]),
                            LegajoJsonValues.String(rowNode?["quote"]), LegajoJsonValues.Decimal(rowNode?["confidence"]));
                        CollectFields(extraction, userNames, array.Items, rowNode, LegajoJsonPointer.Child(childPath, i),
                                      provenance, fields);
                    }
                    break;
            }
        }
    }

    private static LegajoExtractionFieldResponse Field(
        LegajoEffectiveExtraction extraction, IReadOnlyDictionary<Guid, string> userNames,
        LegajoSchemaNode node, string path, JsonNode? value, RowProvenance? provenance)
    {
        // A correction applies to this field when it names it, or names a row or object that contains it.
        var history = extraction.Corrections
            .Where(c => c.FieldPath == path || path.StartsWith(c.FieldPath + "/", StringComparison.Ordinal))
            .Select(c => new LegajoCorrectionHistoryResponse
            {
                Value           = SubValue(c, path),
                Reason          = c.Reason,
                CorrectedByName = userNames.GetValueOrDefault(c.CorrectedBy),
                CorrectedAt     = c.CorrectedAt
            })
            .ToList();

        return new LegajoExtractionFieldResponse
        {
            FieldPath   = path,
            Value       = value?.DeepClone(),
            Page        = provenance?.Page,
            Quote       = provenance?.Quote,
            Confidence  = provenance?.Confidence,
            ReadMode    = extraction.Run.ReadMode,
            Corrected   = history.Count > 0,
            History     = history,
            Correctable = LegajoCorrectionValidator.IsCorrectableInView(node, extraction.Effective, path)
        };
    }

    // The part of a correction's value that lands on this field (all of it, unless it set a whole row).
    private static JsonNode? SubValue(LegajoCorrection correction, string path)
    {
        if (string.IsNullOrWhiteSpace(correction.CorrectedValueJson))
            return null;

        JsonNode? value;
        try
        {
            value = JsonNode.Parse(correction.CorrectedValueJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return correction.FieldPath == path
            ? value
            : LegajoJsonPointer.Get(value, path[correction.FieldPath.Length..])?.DeepClone();
    }
}
