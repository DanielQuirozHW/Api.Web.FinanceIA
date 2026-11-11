// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// One approved document of a legajo, as the reconciliation reads it: a live document in Ready with its
/// current extraction run on the EFFECTIVE values (corrections applied).
/// </summary>
/// <param name="DocumentId">The document.</param>
/// <param name="UploadedAt">When it was uploaded; the newest wins a tie.</param>
/// <param name="SupersedesDocumentId">The document this one supersedes, or null.</param>
/// <param name="Extraction">The current run on its effective values.</param>
public sealed record LegajoReconciliationInput(
    Guid DocumentId,
    DateTime UploadedAt,
    Guid? SupersedesDocumentId,
    LegajoEffectiveExtraction Extraction);
