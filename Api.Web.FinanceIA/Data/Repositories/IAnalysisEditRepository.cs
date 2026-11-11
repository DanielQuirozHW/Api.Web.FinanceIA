// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Data access for [dbo].[AnalysisEdits] (DDL 081). APPEND-ONLY by construction: an insert and a read, and
/// NO update or delete method — <c>AnalysisEditRepositoryTests</c> reflects over this interface to hold
/// that line.
/// </summary>
public interface IAnalysisEditRepository
{
    /// <summary>
    /// Inserts <paramref name="edits"/> on the scoped FinanciaDbContext. It never opens a transaction of
    /// its own, so a call made inside the caller's transaction (the balance-correction write in
    /// <see cref="AnalysisRescoreRepository"/>) commits or rolls back with it.
    /// </summary>
    Task AddRangeAsync(IReadOnlyList<AnalysisEdit> edits, CancellationToken ct);

    /// <summary>The corrections of one analysis in this tenant, newest first. Empty when there are none.</summary>
    Task<IReadOnlyList<AnalysisEditHistoryItem>> GetByAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct);
}
