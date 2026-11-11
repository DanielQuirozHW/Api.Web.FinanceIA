// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// The one atomic write of a persona humana re-evaluation (POST /api/analyses/{id}/rescore on a persona
/// analysis). Kept apart from <see cref="IAnalysisRescoreRepository"/> so the balance write and its test
/// doubles do not change; implemented by the same <see cref="AnalysisRescoreRepository"/>, which shares the
/// decision gate and the transaction with it.
/// </summary>
public interface IPersonAnalysisRescoreRepository
{
    /// <summary>
    /// Applies one re-evaluation: re-checks the decision gate, updates the result's derived columns with
    /// <c>UPDATE … WHERE ResultId = @id AND RescoreCount = @expected</c>, overwrites every participant row's
    /// evaluation columns in place and inserts the history row — all in ONE transaction, so either all of it
    /// lands or none does.
    /// </summary>
    Task<AnalysisRescoreWriteOutcome> TryApplyAsync(PersonAnalysisRescoreWrite write, CancellationToken ct);
}
