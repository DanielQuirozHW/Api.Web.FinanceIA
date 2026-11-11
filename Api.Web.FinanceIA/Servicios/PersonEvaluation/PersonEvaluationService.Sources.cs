// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>What the evaluation read, frozen for reproducibility (AnalysisParticipants.DocumentVersionsJson).</summary>
public static partial class PersonEvaluationService
{
    /// <summary>
    /// Pins every live legajo document to the extraction run that is current for it and the corrections
    /// applied to that run, in the order they were applied — the same effective values the reconciliation
    /// read — plus the snapshot stamps and the thresholds.
    /// </summary>
    /// <param name="documents">The legajo's live documents.</param>
    /// <param name="currentRuns">The current extraction run of each document, by DocumentId.</param>
    /// <param name="corrections">The corrections of those runs.</param>
    /// <param name="legajo">The legajo (its snapshot stamps only).</param>
    /// <param name="lufeDataUpdatedAt">The entity's LUFE snapshot stamp (the MiPyME certificate).</param>
    /// <param name="inventory">The reconciled inventory read.</param>
    /// <param name="incomeRows">How many reconciled income rows were read.</param>
    /// <param name="macroFetchedAt">When the CPI series was fetched, if any.</param>
    /// <param name="macroSource">Where it came from.</param>
    /// <param name="parameters">The thresholds.</param>
    /// <param name="agro">The solicitante's agro annual mode (engine 0.7); null for a fiador.</param>
    /// <param name="expectedDocuments">
    /// The expected documents the solicitante's coverage measured against (engine 0.10); null for a fiador.
    /// </param>
    public static PersonEvaluationSources Sources(
        IReadOnlyList<Document> documents,
        IReadOnlyDictionary<Guid, LegajoExtraction> currentRuns,
        IReadOnlyList<LegajoCorrection> corrections,
        PersonLegajo legajo,
        DateTime? lufeDataUpdatedAt,
        LegajoInventoryResponse inventory,
        int incomeRows,
        DateTime? macroFetchedAt,
        string? macroSource,
        PersonEvaluationParameters parameters,
        PersonAgroMode? agro = null,
        PersonExpectedDocumentsForRegime? expectedDocuments = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(currentRuns);
        ArgumentNullException.ThrowIfNull(corrections);
        ArgumentNullException.ThrowIfNull(legajo);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(parameters);

        var correctionsByRun = corrections
            .GroupBy(c => c.ExtractionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.CorrectedAt).ThenBy(c => c.CorrectionId).Select(c => c.CorrectionId).ToList());

        var versions = documents
            .Where(d => !d.IsDeleted)
            .OrderBy(d => d.UploadedAt)
            .ThenBy(d => d.DocumentId)
            .Select(d =>
            {
                // Only an extraction run counts: a classification run is not what the figures came from.
                currentRuns.TryGetValue(d.DocumentId, out var run);
                if (run is not null && run.Kind != LegajoExtractionKinds.Extraction)
                    run = null;

                return new PersonDocumentVersion
                {
                    DocumentId    = d.DocumentId,
                    DocumentType  = d.DocumentType,
                    LegajoStatus  = d.LegajoStatus,
                    PeriodEnd     = d.PeriodEnd is { } end ? DateOnly.FromDateTime(end) : null,
                    ExtractionId  = run?.ExtractionId,
                    SchemaVersion = run?.SchemaVersion,
                    PromptVersion = run?.PromptVersion,
                    Model         = run?.Model,
                    OutputHash    = run?.OutputHash,
                    CorrectionIds = run is not null && correctionsByRun.TryGetValue(run.ExtractionId, out var ids) ? ids : []
                };
            })
            .ToList();

        return new PersonEvaluationSources
        {
            Documents          = versions,
            BcraDataUpdatedAt  = legajo.BcraDataUpdatedAt,
            ArcaDataUpdatedAt  = legajo.ArcaDataUpdatedAt,
            LufeDataUpdatedAt  = lufeDataUpdatedAt,
            InventoryUpdatedAt = inventory.LastUpdatedAt,
            InventoryRows      = inventory.Groups.Sum(g => g.Items.Count),
            IncomeRows         = incomeRows,
            MacroFetchedAt     = macroFetchedAt,
            MacroSource        = macroSource,
            Parameters         = parameters,
            Agro               = agro,
            ExpectedDocuments  = expectedDocuments
        };
    }

    /// <summary>
    /// True when the legajo has at least one document the evaluation can read: live, Ready and of a type
    /// with an extractor. The consent alone (an AutorizacionDatos in Ready) does not count.
    /// </summary>
    public static bool HasEvaluableDocument(IReadOnlyList<Document> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        return documents.Any(d => !d.IsDeleted
                               && d.LegajoStatus == LegajoStatuses.Ready
                               && LegajoExtractionPrompts.For(d.DocumentType) is not null);
    }
}
