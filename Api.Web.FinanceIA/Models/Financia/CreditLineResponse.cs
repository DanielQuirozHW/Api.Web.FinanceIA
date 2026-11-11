// Project : Api.Web.FinanceIA
// Created : 2026-07-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Read model for a credit line (cupo) with its derived utilisation figures and its
/// operations. <see cref="Utilizado"/> and <see cref="OperationsCount"/> count every
/// operation whose stored status is not 'Cancelada' — 'Aprobada' ones reserve the cupo
/// from creation, and effective-'Vencida' ones still consume it until explicitly cancelled.
/// Confirming an 'Aprobada' operation therefore does not move these figures.
/// </summary>
public sealed class CreditLineResponse
{
    /// <summary>Credit line primary key.</summary>
    public Guid CreditLineId { get; set; }

    /// <summary>Entity (PyME) this cupo belongs to.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Total approved amount of the cupo.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Lifecycle status: 'Active', 'Superseded' or 'Suspended'.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the cupo was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// [dbo].[Users].UserId of the analyst who created this cupo. Surfaced because it decides who
    /// may mutate it — the creator or an Admin — so the client can compare it against its own user
    /// id and offer the raise/suspend/reactivate affordances only where they will be honoured. No
    /// server-side "isOwned" is computed, mirroring how the chat conversation list works.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// Display name of the creating analyst, resolved in one batched lookup. Falls back to
    /// "Usuario" when the id no longer resolves to a user.
    /// </summary>
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the most recent ampliación (raise), or null if never raised.</summary>
    public DateTime? LastRaisedAt { get; set; }

    /// <summary>Id of the approved analysis that justifies the cupo's current amount, or null if never raised.</summary>
    public Guid? LastRaiseAnalysisId { get; set; }

    /// <summary>Id of the approved analysis that justified this cupo's creation, or null for cupos created before this was recorded.</summary>
    public Guid? OriginAnalysisId { get; set; }

    /// <summary>
    /// The <c>AnalysisResult.ResultId</c> for <see cref="OriginAnalysisId"/> — i.e. the id the
    /// client navigates to (the analysis detail route is keyed by ResultId, not AnalysisId). A
    /// NAVIGATION CONVENIENCE only; <see cref="OriginAnalysisId"/> remains the real domain foreign
    /// key. Null when <see cref="OriginAnalysisId"/> is null (legacy cupos) or no result row exists
    /// for it. Populated by the entity credit-line read; null on action-confirmation responses.
    /// </summary>
    public Guid? OriginAnalysisResultId { get; set; }

    /// <summary>Sum of Amount over the cupo's non-'Cancelada' operations.</summary>
    public decimal Utilizado { get; set; }

    /// <summary>Remaining cupo: <see cref="Amount"/> minus <see cref="Utilizado"/>.</summary>
    public decimal Disponible { get; set; }

    /// <summary>Number of non-'Cancelada' operations under the cupo.</summary>
    public int OperationsCount { get; set; }

    /// <summary>All operations under this cupo, each with its effective status.</summary>
    public IReadOnlyList<OperationResponse> Operations { get; set; } = [];
}
