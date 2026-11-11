// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Deterministic arithmetic checks over a legajo extraction (M10 Phase 2b). Stateless; never modifies
/// the data it reads.
/// </summary>
public interface ILegajoIntegrityService
{
    /// <summary>
    /// Evaluates <paramref name="result"/> — a normalized (or effective, corrections applied) ResultJson
    /// tree of <paramref name="documentType"/>. A type without checks yields an empty, passed report.
    /// </summary>
    /// <param name="documentType">One of LegajoExtractionPrompts.SupportedTypes.</param>
    /// <param name="result">The tree to check.</param>
    /// <param name="periodEnd">Documents.PeriodEnd (from the classification or the analyst's confirmation), or null.</param>
    LegajoIntegrityReport Evaluate(string documentType, JsonObject result, DateOnly? periodEnd);
}
