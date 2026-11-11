// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>One live document of the legajo, as the coverage index reads it.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="DocumentType">Its type.</param>
/// <param name="LegajoStatus">Its legajo status (Ready, NeedsReview, …).</param>
/// <param name="PeriodEnd">The period end it measures, when known.</param>
public sealed record PersonEvaluationDocument(
    Guid DocumentId,
    string DocumentType,
    string? LegajoStatus,
    DateOnly? PeriodEnd);
