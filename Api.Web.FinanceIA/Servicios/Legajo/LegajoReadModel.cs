// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Builds the legajo read models from stored rows. Pure. Its one rule about personal data: the holder
/// CUIT goes in, only a match flag comes out.
/// </summary>
public static class LegajoReadModel
{
    /// <summary>The CODE-DEFAULT checklist for the legajo's regime, each line with the documents that satisfy it.</summary>
    public static IReadOnlyList<LegajoExpectedDocumentResponse> BuildExpectedDocuments(
        string? fiscalRegime, IReadOnlyList<Document> documents)
        => BuildExpectedDocuments(LegajoExpectedDocuments.For(fiscalRegime), documents);

    /// <summary>
    /// The checklist made of <paramref name="lines"/> — the tenant's expected documents for the legajo's regime,
    /// as resolved by IPersonExpectedDocumentsService — each line with the documents that satisfy it.
    /// </summary>
    public static IReadOnlyList<LegajoExpectedDocumentResponse> BuildExpectedDocuments(
        IReadOnlyList<LegajoExpectedDocument> lines, IReadOnlyList<Document> documents)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(documents);

        // A Blocked document is another person's paperwork: it satisfies nothing.
        var usable = documents
            .Where(d => !d.IsDeleted && d.LegajoStatus != LegajoStatuses.Blocked)
            .ToList();

        return lines
            .Select(line => new LegajoExpectedDocumentResponse
            {
                DocumentType           = line.DocumentType,
                Required               = line.Required,
                Alternatives           = line.Alternatives,
                SatisfiedByDocumentIds = usable
                    .Where(d => line.IsSatisfiedBy(d.DocumentType))
                    .Select(d => d.DocumentId)
                    .ToList()
            })
            .ToList();
    }

    /// <summary>One document of the legajo documents list.</summary>
    public static LegajoDocumentResponse BuildDocument(
        Document document, string legajoCuit, LegajoExtraction? currentClassification,
        LegajoExtractionSummaryResponse? currentExtraction = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new LegajoDocumentResponse
        {
            DocumentId        = document.DocumentId,
            DocumentType      = document.DocumentType,
            OriginalFileName  = document.OriginalFileName,
            UploadedAt        = document.UploadedAt,
            FileSizeBytes     = document.FileSizeBytes,
            PageCount         = document.PageCount,
            PdfType           = document.PdfType,
            LegajoStatus      = document.LegajoStatus,
            PeriodEnd         = document.PeriodEnd.HasValue ? DateOnly.FromDateTime(document.PeriodEnd.Value) : null,
            EngagementType    = document.EngagementType,
            HolderCuitMatches = HolderCuitMatches(document.HolderCuit, legajoCuit),
            PageQuality       = PageQualityCounts(document.PageQualityJson),
            Classification    = Classification(currentClassification),
            CurrentExtraction = currentExtraction,
            LastErrorCode     = LegajoFailureCodes.ForResponse(document.LegajoStatus, document.LastExtractionError)
        };
    }

    /// <summary>Whether the stored holder CUIT is the legajo's person, by digits; null when none is stored.</summary>
    public static bool? HolderCuitMatches(string? holderCuit, string legajoCuit)
    {
        var held = LegajoCuit.Digits(holderCuit);
        if (held is null)
            return null;

        return string.Equals(held, LegajoCuit.Digits(legajoCuit), StringComparison.Ordinal);
    }

    /// <summary>Counts the verdicts in Documents.PageQualityJson. Unreadable or absent JSON counts nothing.</summary>
    public static LegajoPageQualityResponse PageQualityCounts(string? pageQualityJson)
    {
        int good = 0, suspect = 0, noText = 0;

        if (!string.IsNullOrWhiteSpace(pageQualityJson))
        {
            try
            {
                using var json = JsonDocument.Parse(pageQualityJson);
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in json.RootElement.EnumerateArray())
                    {
                        var verdict = page.ValueKind == JsonValueKind.Object
                                      && page.TryGetProperty("verdict", out var v)
                                      && v.ValueKind == JsonValueKind.String
                            ? v.GetString()
                            : null;

                        switch (verdict)
                        {
                            case PageTextVerdicts.Good:    good++;    break;
                            case PageTextVerdicts.Suspect: suspect++; break;
                            case PageTextVerdicts.NoText:  noText++;  break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A hand-edited or truncated column must not break the list: count nothing.
            }
        }

        return new LegajoPageQualityResponse { Good = good, Suspect = suspect, NoText = noText };
    }

    /// <summary>
    /// The public view of a classification run: detected type, confidence, composite flag and review
    /// reasons. Reads only those keys of ResultJson — the holder CUIT and its quote stay in the row.
    /// </summary>
    public static LegajoClassificationResponse? Classification(LegajoExtraction? run)
    {
        if (run is null || run.Status != LegajoExtractionStatuses.Succeeded)
            return null;

        var isComposite = false;
        var reasons = new List<string>();
        var detected = run.DocumentType;

        try
        {
            using var json = JsonDocument.Parse(run.ResultJson);
            var root = json.RootElement;

            if (root.TryGetProperty("detectedType", out var dt) && dt.ValueKind == JsonValueKind.String)
                detected = dt.GetString() ?? detected;

            isComposite = root.TryGetProperty("isComposite", out var ic) && ic.ValueKind == JsonValueKind.True;

            if (root.TryGetProperty("reviewReasons", out var rr) && rr.ValueKind == JsonValueKind.Array)
            {
                reasons.AddRange(rr.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String)
                    .Select(r => r.GetString()!));
            }
        }
        catch (JsonException)
        {
            // Keep the columns we have; the JSON is only the detail.
        }

        return new LegajoClassificationResponse
        {
            DetectedType  = detected,
            Confidence    = run.Confidence,
            IsComposite   = isComposite,
            ReviewReasons = reasons
        };
    }
}
