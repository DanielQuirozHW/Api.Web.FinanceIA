// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>Helpers for the per-tenant expected-documents matrix (engine 0.10).</summary>
internal static class ExpectedDocumentsTestKit
{
    /// <summary>The REAL service over <paramref name="db"/>, as DI builds it for one request.</summary>
    public static PersonExpectedDocumentsService RealService(
        FinanciaDbContext db,
        IAuditService? audit = null,
        ILogger<PersonExpectedDocumentsService>? logger = null)
        => new(
            new TenantSettingsRepository(db, NullLogger<TenantSettingsRepository>.Instance),
            new PersonAnalysisRepository(db, NullLogger<PersonAnalysisRepository>.Instance),
            audit ?? new Api.Web.FinanceIA.Tests.Clases.RecordingAuditService(),
            logger ?? NullLogger<PersonExpectedDocumentsService>.Instance);

    /// <summary>Stores <paramref name="json"/> as the tenant's <c>person.expected_documents</c> row.</summary>
    public static void Seed(FinanciaDbContext db, string tenantId, string json, DateTime? updatedAt = null)
    {
        db.TenantSettings.Add(new TenantSettings
        {
            TenantId     = tenantId,
            SettingKey   = TenantSettingKeys.PersonExpectedDocuments,
            SettingValue = json,
            UpdatedAt    = updatedAt ?? new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    /// <summary>A line DTO.</summary>
    public static PersonExpectedDocumentLineDto? Line(
        string type, bool required = true, decimal? maxAge = null, params string[] alternatives) => new()
    {
        DocumentType = type,
        Requirement  = required ? "required" : "optional",
        Alternatives = alternatives.Select(a => (string?)a).ToList(),
        MaxAgeMonths = maxAge
    };

    /// <summary>
    /// A Monotributo matrix that differs from the default: no DDJJ IIBB ("no se pide"), the certificación de
    /// ingresos optional with a 3-month cut, the manifestación with a 12-month cut, and Bienes Personales
    /// satisfiable by a Ganancias DDJJ instead of a REIBP.
    /// </summary>
    public static List<PersonExpectedDocumentLineDto?> CustomMonotributo() =>
    [
        Line(DocumentTypes.AutorizacionDatos),
        Line(DocumentTypes.Monotributo),
        Line(DocumentTypes.CertificacionIngresos, required: false, maxAge: 3),
        Line(DocumentTypes.ManifestacionBienes, maxAge: 12),
        Line(DocumentTypes.BienesPersonales, true, null, DocumentTypes.Ganancias),
        Line(DocumentTypes.FlujoProyectado, required: false)
    ];

    /// <summary>The stored JSON of a matrix with <see cref="CustomMonotributo"/> only.</summary>
    public static string CustomMonotributoJson()
        => PersonExpectedDocumentsMatrix.Serialize(
            new Dictionary<string, List<PersonExpectedDocumentLineDto?>?>
            {
                [ExpectedDocumentRegimes.Monotributo] = CustomMonotributo()
            });
}

/// <summary>An <see cref="IPersonExpectedDocumentsService"/> that answers a fixed matrix and counts reads.</summary>
internal sealed class StubPersonExpectedDocumentsService : IPersonExpectedDocumentsService
{
    public PersonExpectedDocumentsResolved Resolved { get; set; } = PersonExpectedDocumentsResolved.Defaults;

    public int ReadCount { get; private set; }

    public Task<PersonExpectedDocumentsResolved> GetResolvedAsync(string tenantId, CancellationToken ct)
    {
        ReadCount++;
        return Task.FromResult(Resolved);
    }

    public async Task<PersonExpectedDocumentsForRegime> ForAsync(string tenantId, string? fiscalRegime, CancellationToken ct)
        => (await GetResolvedAsync(tenantId, ct)).For(fiscalRegime);

    public Task<PersonExpectedDocumentsSettingsDto> GetViewAsync(string tenantId, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<PersonExpectedDocumentsSettingsDto> UpdateAsync(
        string tenantId, IReadOnlyDictionary<string, List<PersonExpectedDocumentLineDto?>?>? regimes, Guid userId,
        string? ipAddress, string? requestId, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<PersonExpectedDocumentsSettingsDto> ResetAsync(
        string tenantId, Guid userId, string? ipAddress, string? requestId, CancellationToken ct)
        => throw new NotSupportedException();
}
