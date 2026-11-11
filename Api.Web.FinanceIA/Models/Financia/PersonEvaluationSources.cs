// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Everything a persona humana evaluation read, as frozen in AnalysisParticipants.DocumentVersionsJson
/// (M10 Phase 4a): the documents and their runs, the snapshot stamps and the thresholds. With it a past
/// evaluation can be reproduced, and a later change to the legajo is visible as a difference.
/// </summary>
public sealed record PersonEvaluationSources
{
    /// <summary>The legajo documents, by document id.</summary>
    public required IReadOnlyList<PersonDocumentVersion> Documents { get; init; }

    /// <summary>The legajo's BCRA snapshot stamp.</summary>
    public DateTime? BcraDataUpdatedAt { get; init; }

    /// <summary>The legajo's ARCA snapshot stamp.</summary>
    public DateTime? ArcaDataUpdatedAt { get; init; }

    /// <summary>The entity's LUFE snapshot stamp (the MiPyME certificate).</summary>
    public DateTime? LufeDataUpdatedAt { get; init; }

    /// <summary>The last change to the reconciled inventory.</summary>
    public DateTime? InventoryUpdatedAt { get; init; }

    /// <summary>Count of reconciled income rows read.</summary>
    public int IncomeRows { get; init; }

    /// <summary>Count of reconciled inventory rows read.</summary>
    public int InventoryRows { get; init; }

    /// <summary>When the CPI series was fetched from the BCRA; null when none was available.</summary>
    public DateTime? MacroFetchedAt { get; init; }

    /// <summary>Where the CPI series came from: <c>legajo</c> (its BCRA snapshot), <c>bcra</c> (a fresh read) or null.</summary>
    public string? MacroSource { get; init; }

    /// <summary>The thresholds the evaluation ran on.</summary>
    public required PersonEvaluationParameters Parameters { get; init; }

    /// <summary>
    /// The solicitante's agro annual mode and cuotas a year (engine 0.7). Null on a fiador's sources and on
    /// every evaluation frozen before 0.7, which therefore read exactly as they were frozen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersonAgroMode? Agro { get; init; }

    /// <summary>
    /// The expected documents the coverage index measured against (engine 0.10): the tenant's matrix for the
    /// legajo's regime, or the code default, with its source and when the tenant saved it. Kept OUT of
    /// <see cref="Parameters"/> so the calibration board's frozen differences read only the thresholds. Null on
    /// a fiador's sources and on every evaluation frozen before 0.10, which therefore read exactly as they were
    /// frozen.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PersonExpectedDocumentsForRegime? ExpectedDocuments { get; init; }
}
