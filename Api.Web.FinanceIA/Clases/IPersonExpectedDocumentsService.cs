// Project : Api.Web.FinanceIA
// Created : 2026-10-31

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// A tenant's expected-documents matrix for the persona humana legajo (TenantSettings key
/// <see cref="TenantSettingKeys.PersonExpectedDocuments"/>), with the code defaults
/// (<see cref="LegajoExpectedDocuments"/>) as the per-regime fallback. Registered Scoped. Pass <c>tenantId</c>
/// as a parameter — never inject the tenant accessor.
/// </summary>
/// <remarks>
/// The matrix drives the legajo checklist and the coverage index only; it never blocks an upload, a
/// classification, an analysis or a suggestion. The READS never throw (except on cancellation): a missing row
/// reads the defaults, and a row that does not parse or validate — or a failed read — reads the defaults for
/// every regime with a Warning that names the tenant and the error kind, never the value. So a settings fault
/// can never fail an evaluation, a re-evaluation or a legajo read.
/// </remarks>
public interface IPersonExpectedDocumentsService
{
    /// <summary>The matrix in force for every regime bucket, each with its source. Never throws.</summary>
    Task<PersonExpectedDocumentsResolved> GetResolvedAsync(string tenantId, CancellationToken ct);

    /// <summary>The bucket <paramref name="fiscalRegime"/> reads, resolved. Never throws.</summary>
    Task<PersonExpectedDocumentsForRegime> ForAsync(string tenantId, string? fiscalRegime, CancellationToken ct);

    /// <summary>
    /// GET /api/bff/settings/person-expected-documents: the matrix in force, the defaults, the configurable
    /// types, and each line's effective recency cut under the tenant's pf_ age keys. A failed pf_ read propagates.
    /// </summary>
    Task<PersonExpectedDocumentsSettingsDto> GetViewAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Validates and stores <paramref name="regimes"/> (a regime left out keeps its code default), audits
    /// <c>Settings.UpdatePersonExpectedDocuments</c> with the matrix before and after, and returns the view.
    /// THROWS <see cref="Exceptions.ValidationException"/> when invalid; a failed write propagates.
    /// </summary>
    Task<PersonExpectedDocumentsSettingsDto> UpdateAsync(
        string tenantId,
        IReadOnlyDictionary<string, List<PersonExpectedDocumentLineDto?>?>? regimes,
        Guid userId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// Deletes the tenant's matrix — every regime back to the code default — audits it like an update, and
    /// returns the view. A failed write propagates.
    /// </summary>
    Task<PersonExpectedDocumentsSettingsDto> ResetAsync(
        string tenantId,
        Guid userId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);
}
