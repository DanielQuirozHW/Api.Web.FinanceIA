// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Reads and writes the NOSIS Manager report columns of <c>[Entities]</c> (DDL 087):
/// <c>NosisReportJson</c>, <c>NosisReportDate</c> and <c>NosisReportImportedAt</c>. Every call is
/// scoped by entity AND tenant. Kept apart from <see cref="IEntityRepository"/> so the report never
/// rides on an entity read, and so a write touches these three columns and nothing else.
/// </summary>
public interface INosisReportRepository
{
    /// <summary>
    /// The entity's CUIT and person type, for the import's checks. Null when the entity does not exist
    /// in this tenant.
    /// </summary>
    Task<NosisReportSubject?> GetSubjectAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The stored report of one entity. Null when the entity does not exist in this tenant; an
    /// instance with a null <see cref="NosisReportSnapshot.Json"/> when it has no report.
    /// </summary>
    Task<NosisReportSnapshot?> GetAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The <c>fileSha256</c> and <c>parserVersion</c> of the stored report, read inside the database
    /// with <c>JSON_VALUE</c> so the blob is not materialised. Both null when there is no report (or no
    /// such entity). The same file read by an older parser is re-parsed, so both are compared.
    /// </summary>
    Task<(string? Sha256, string? ParserVersion)> GetStoredFingerprintAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Stores a report, replacing any previous one. Only the three report columns are written.
    /// Returns false when the entity does not exist in this tenant.
    /// </summary>
    Task<bool> SaveAsync(
        Guid entityId, string tenantId, string json, DateTime reportDate, DateTime importedAt, CancellationToken ct);

    /// <summary>
    /// Clears the three report columns. Returns true when a report was there to clear; false when the
    /// entity had none or does not exist in this tenant.
    /// </summary>
    Task<bool> ClearAsync(Guid entityId, string tenantId, CancellationToken ct);
}
