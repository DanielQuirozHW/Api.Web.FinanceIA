// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What the company report's first page prints beyond the analysis and its result, gathered by
/// ExportController from the readers it already has. Every member is optional: a value that could not be
/// read is null, and the page omits the item that needed it — the export never fails for it.
/// </summary>
public sealed record CompanyPdfReport
{
    /// <summary>The company's razón social (Entities.BusinessName).</summary>
    public string? EntityName { get; init; }

    /// <summary>The company's CUIT (Entities.CUIT), eleven digits.</summary>
    public string? EntityCuit { get; init; }

    /// <summary>Display names of this tenant's users the analysis names (requester, reviewer, pre-approver), by UserId.</summary>
    public IReadOnlyDictionary<Guid, string> UserNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>
    /// This tenant's AlertRuleCatalogue display names (DisplayNameEs) by rule code, case-insensitive: the title an
    /// alert with that code prints, ahead of the curated map, the model's title and the humanised code. Empty when
    /// the tenant has no catalogue rows or they could not be read.
    /// </summary>
    public IReadOnlyDictionary<string, string> AlertDisplayNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The credit lines this analysis originated.</summary>
    public IReadOnlyList<CreditLine> CreditLines { get; init; } = [];

    /// <summary>The previous decided analysis of the same company, from IAnalysisComparisonService; null when none.</summary>
    public AnalysisComparisonSide? Previous { get; init; }

    /// <summary>This analysis as the comparison reads it (its 0–1000 score); null when the comparison is unavailable.</summary>
    public AnalysisComparisonSide? Current { get; init; }

    /// <summary>The tenant's display name (branding.company_name) for the document's Author; null → "Financia".</summary>
    public string? TenantDisplayName { get; init; }

    /// <summary>
    /// The client identity page 1's header prints (ITenantPdfBrandingProvider): the logo and name in the brand slot
    /// and an optional accent, only when the tenant has a usable logo. <see cref="TenantPdfBranding.None"/> → the
    /// Financia header, exactly as before.
    /// </summary>
    public TenantPdfBranding Branding { get; init; } = TenantPdfBranding.None;
}
