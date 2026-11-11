// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The confirmations one approval act of a persona humana analysis carried (DDL 072): stored as JSON in
/// Analyses.PreApprovalChecksJson (the pre-approval) or Analyses.ConfirmationChecksJson (the final
/// approval), each written once by its own act — a confirmation never rewrites the pre-approval's record.
/// </summary>
public sealed record AnalysisDecisionChecks
{
    /// <summary>The approver confirmed the counter-guarantees.</summary>
    public required bool CounterGuaranteesConfirmed { get; init; }

    /// <summary>The approver confirmed the Fondo de Riesgo exposure limit.</summary>
    public required bool FdrLimitConfirmed { get; init; }

    /// <summary>The reason given with the approval.</summary>
    public string? Reason { get; init; }

    /// <summary>Who approved.</summary>
    public required Guid By { get; init; }

    /// <summary>When (UTC).</summary>
    public required DateTime At { get; init; }
}
