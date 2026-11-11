// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using Api.Web.FinanceIA.Servicios.Pdf;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Everything the persona humana PDF prints, gathered by ExportController from the same readers the
/// detail uses: the frozen evaluation with its decision rules, the analysis and its approval records, the
/// identity from <see cref="Entity"/> and the credit line the approval originated. The renderer reads
/// nothing else.
/// </summary>
public sealed record PersonPdfReport
{
    /// <summary>The AnalysisResult the export was asked for (the id in the header and the footer).</summary>
    public required Guid ResultId { get; init; }

    /// <summary>The analysis: status, requested amount and the pre-approval / decision fields.</summary>
    public required Analysis Analysis { get; init; }

    /// <summary>
    /// The solicitante's frozen evaluation with its decision rules, exactly as GET /api/analyses/{id} returns
    /// it as <c>personEvaluation</c>; null when the analysis has no solicitante row.
    /// </summary>
    public PersonEvaluationResponse? Evaluation { get; init; }

    /// <summary>The person's name, from <see cref="Entity.BusinessName"/>.</summary>
    public required string EntityName { get; init; }

    /// <summary>The person's CUIT, from <see cref="Entity.CUIT"/>.</summary>
    public required string EntityCuit { get; init; }

    /// <summary>The confirmations the pre-approval carried (Analyses.PreApprovalChecksJson).</summary>
    public AnalysisDecisionChecks? PreApprovalChecks { get; init; }

    /// <summary>The confirmations the final approval carried (Analyses.ConfirmationChecksJson).</summary>
    public AnalysisDecisionChecks? ConfirmationChecks { get; init; }

    /// <summary>Display names of the users the approval records name, by UserId; a missing id prints "—".</summary>
    public IReadOnlyDictionary<Guid, string> UserNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The credit lines this analysis originated.</summary>
    public IReadOnlyList<CreditLine> CreditLines { get; init; } = [];

    /// <summary>When the analysis was last re-evaluated (AnalysisResults.RescoredAt); null when never.</summary>
    public DateTime? RescoredAt { get; init; }

    /// <summary>How many times it was re-evaluated (AnalysisResults.RescoreCount).</summary>
    public int RescoreCount { get; init; }

    /// <summary>The tenant's display name (branding.company_name) for the document's Author; null → "Financia".</summary>
    public string? TenantDisplayName { get; init; }

    /// <summary>
    /// The client identity page 1's header prints (ITenantPdfBrandingProvider): the logo and name in the brand slot
    /// and an optional accent, only when the tenant has a usable logo. <see cref="TenantPdfBranding.None"/> → the
    /// Financia header, exactly as before.
    /// </summary>
    public TenantPdfBranding Branding { get; init; } = TenantPdfBranding.None;
}
