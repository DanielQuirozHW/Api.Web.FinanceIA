// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// Everything the evaluation of one fiador reads (M10 Phase 5a-2), already loaded: his own legajo, shaped as a
/// <see cref="PersonEvaluationInput"/> so the solicitante's filters and income rules run on it unchanged, plus
/// what the guarantee adds — the solicitante it guarantees and the role data the analyst declared.
/// </summary>
public sealed record FiadorEvaluationInput
{
    /// <summary>
    /// The fiador's legajo: his BCRA and ARCA snapshots, reconciled inventory and incomes. Its
    /// <see cref="PersonEvaluationInput.RequestedAmount"/> is the amount at his charge, its
    /// <see cref="PersonEvaluationInput.InstallmentAmount"/> the FULL cuota of the operation and its
    /// <see cref="PersonEvaluationInput.Currency"/> the operation's.
    /// </summary>
    public required PersonEvaluationInput Person { get; init; }

    /// <summary>The solicitante's CUIT: an item linked to it is correlated collateral.</summary>
    public string? SolicitanteCuit { get; init; }

    /// <summary>The solicitante's reconciled inventory: the spouse's de-duplication base and the housing the spouse can unlock.</summary>
    public required LegajoInventoryResponse SolicitanteInventory { get; init; }

    /// <summary>One of <see cref="GuaranteeTypes.Known"/>.</summary>
    public required string GuaranteeType { get; init; }

    /// <summary>The spouse consented to execute the housing.</summary>
    public bool SpouseConsent { get; init; }

    /// <summary>The housing protection was declared inopposable to this creditor: the home is executable.</summary>
    public bool HousingInopposable { get; init; }

    /// <summary>Guarantees already given elsewhere, declared by the analyst (0 when not stated).</summary>
    public decimal GuaranteesGivenAmount { get; init; }
}
