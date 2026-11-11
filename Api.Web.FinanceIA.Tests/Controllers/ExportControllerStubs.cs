// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Api.Web.FinanceIA.Servicios.Pdf;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>A tenant-settings service for the export: a fixed display name, or a read that throws.</summary>
internal sealed class ExportTenantSettingsStub(string? displayName = null, bool throws = false) : ITenantSettingsService
{
    public Task<string> GetStringAsync(string tenantId, string key, string defaultValue, CancellationToken ct) =>
        Task.FromResult(defaultValue);

    public Task<string> GetCreditLineModeAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult(string.Empty);

    public Task<string?> GetTenantDisplayNameAsync(string tenantId, CancellationToken ct) =>
        throws ? throw new InvalidOperationException("settings unavailable") : Task.FromResult(displayName);
}

/// <summary>A previous-analysis comparison for the export: a fixed answer, or a read that throws.</summary>
internal sealed class ExportComparisonStub(AnalysisComparisonResponse? response = null, bool throws = false) : IAnalysisComparisonService
{
    public Task<AnalysisComparisonResponse?> CompareWithPreviousAsync(Guid resultId, string tenantId, CancellationToken ct) =>
        throws ? throw new InvalidOperationException("comparison unavailable") : Task.FromResult(response);
}

/// <summary>The export's branding provider: a fixed branding (default none), or a resolution that throws.</summary>
internal sealed class ExportBrandingStub(TenantPdfBranding? branding = null, bool throws = false) : ITenantPdfBrandingProvider
{
    public int Calls { get; private set; }

    public PdfTheme? Theme { get; private set; }

    public Task<TenantPdfBranding> ResolveAsync(string tenantId, PdfTheme theme, CancellationToken ct)
    {
        Calls++;
        Theme = theme;
        return throws
            ? throw new InvalidOperationException("branding unavailable")
            : Task.FromResult(branding ?? TenantPdfBranding.None);
    }
}

/// <summary>The export's alert-catalogue reader: fixed rows (default none), or a read that throws. Only GetAllAsync is used.</summary>
internal sealed class ExportAlertCatalogueStub(IReadOnlyList<AlertRuleDefinition>? rules = null, bool throws = false)
    : Api.Web.FinanceIA.Data.Repositories.IAlertRuleCatalogueRepository
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<AlertRuleDefinition>> GetAllAsync(string tenantId, CancellationToken ct)
    {
        Calls++;
        return throws
            ? throw new InvalidOperationException("catalogue unavailable")
            : Task.FromResult(rules ?? []);
    }

    public Task<IReadOnlyList<AlertRuleDefinition>> GetEnabledAsync(string tenantId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<AlertRuleDefinition?> GetByIdAsync(Guid ruleId, string tenantId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<AlertRuleDefinition?> UpdateAsync(
        Guid ruleId, string tenantId, bool enabled, string severity, decimal? thresholdValue, Guid updatedBy, CancellationToken ct) =>
        throw new NotSupportedException();
}
