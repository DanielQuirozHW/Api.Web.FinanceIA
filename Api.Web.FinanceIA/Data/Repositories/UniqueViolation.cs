// Project : Api.Web.FinanceIA
// Created : 2026-09-27

using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Recognises which per-tenant CUIT uniqueness index (DDL 069) a failed SaveChanges ran into.
/// </summary>
/// <remarks>
/// Matched on the index NAME in the provider's message rather than on SQL error numbers 2601/2627,
/// the approach <see cref="AnalysisApprovalVoteRepository"/> already takes for its own constraint:
/// a name cannot mistake some other unique index for this one, and both error numbers carry the
/// name ("… with unique index 'X'" / "Violation of UNIQUE KEY constraint 'X'"). It also keeps the
/// check testable without constructing a SqlException, whose constructors are internal.
/// </remarks>
internal static class UniqueViolation
{
    /// <summary>[dbo].[Entities] (TenantId, CUIT) — one entity per CUIT per tenant.</summary>
    internal const string EntityTenantCuit = "UQ_Entities_TenantId_CUIT";

    /// <summary>[dbo].[Entities] (PersonLegajoId) filtered — one entity per legajo.</summary>
    internal const string EntityPersonLegajo = "UX_Entities_PersonLegajoId";

    /// <summary>[dbo].[PersonLegajos] (TenantId, CUIT) — one legajo per person per tenant.</summary>
    internal const string PersonLegajoTenantCuit = "UX_PersonLegajos_TenantId_CUIT";

    /// <summary>
    /// [dbo].[Documents] (PersonLegajoId, FileHashSHA256) filtered on live legajo rows (DDL 070) — the
    /// same file at most once per legajo.
    /// </summary>
    internal const string DocumentLegajoHash = "UX_Documents_Legajo_Hash";

    /// <summary>True when <paramref name="ex"/> is a violation of the index named <paramref name="indexName"/>.</summary>
    internal static bool Is(DbUpdateException ex, string indexName)
        => ex.InnerException?.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The scope of the CUIT rule <paramref name="ex"/> violated, or null when it is any other
    /// failure (which the caller must let propagate). Both Entities indexes map to
    /// <see cref="DuplicateCuitScope.Entity"/>: when a relinked create collides, SQL Server reports
    /// whichever of the two it checks first, and both mean "this person already has an entity here".
    /// </summary>
    internal static DuplicateCuitScope? Classify(DbUpdateException ex)
    {
        if (Is(ex, PersonLegajoTenantCuit)) return DuplicateCuitScope.PersonLegajo;
        if (Is(ex, EntityTenantCuit) || Is(ex, EntityPersonLegajo)) return DuplicateCuitScope.Entity;

        return null;
    }
}
