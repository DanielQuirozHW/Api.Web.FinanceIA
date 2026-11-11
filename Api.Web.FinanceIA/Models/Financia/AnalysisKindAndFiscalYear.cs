// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// <c>Analyses.AnalysisKind</c>, <c>Analyses.FiscalYear</c> and <c>Analyses.EntityId</c> of one analysis,
/// read together by <c>IPersonAnalysisRepository.GetKindAndFiscalYearAsync</c>.
/// </summary>
/// <param name="Kind">One of <see cref="AnalysisKinds.Known"/>.</param>
/// <param name="FiscalYear">The fiscal year the analysis was requested for.</param>
/// <param name="EntityId">
/// The evaluated entity. The chat reads it to know whose identifiers to strip from the prompt, without a
/// second query.
/// </param>
public sealed record AnalysisKindAndFiscalYear(string Kind, int FiscalYear, Guid EntityId);
