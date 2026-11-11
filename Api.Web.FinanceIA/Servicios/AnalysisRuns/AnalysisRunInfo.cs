// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// What <see cref="AnalysisRunRegistry"/> remembers about a run on THIS instance, for GET
/// /api/analyses/runs: who asked for it and when the document was claimed. In memory only —
/// [dbo].[Documents] has no "started at" or "requested by" column, so a run held by another instance
/// or orphaned by a restart has neither.
/// </summary>
/// <param name="TenantId">Tenant that owns the document; a lookup for another tenant gets nothing.</param>
/// <param name="RequestedBy">Users.UserId of the analyst who triggered the run.</param>
/// <param name="StartedAtUtc">When the document was moved to Processing (queued or running).</param>
public sealed record AnalysisRunInfo(string TenantId, Guid RequestedBy, DateTime StartedAtUtc);
