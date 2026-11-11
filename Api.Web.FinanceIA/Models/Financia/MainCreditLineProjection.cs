// Project : Api.Web.FinanceIA
// Created : 2026-09-08

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The MAIN credit line (cupo) of one entity, as read by the entities-list projection.
///
/// <para>
/// "Main" is the ACTIVE cupo with the most recent <see cref="CreatedAt"/>, whatever its currency —
/// deliberately NOT the largest <see cref="Amount"/>: amounts in different currencies are not
/// comparable, which is why <see cref="Currency"/> travels beside the figure all the way to the
/// wire. <c>UX_CreditLines_ActivePerEntityCurrency</c> allows at most one Active cupo per
/// (TenantId, EntityId, Currency), so the candidate set per entity is a handful of rows.
/// </para>
///
/// <para>
/// An internal read model, not a wire type: it exists so
/// <see cref="Api.Web.FinanceIA.Data.Repositories.ICreditLineRepository.GetMainActiveByEntityIdsAsync"/>
/// can return exactly the columns the list row needs — the same role
/// <see cref="EntityListProjection"/> plays for <c>Entities</c>. <see cref="CreditLineResponse"/>
/// remains the wire shape for the credit-line endpoints.
/// </para>
/// </summary>
public sealed class MainCreditLineProjection
{
    /// <summary>The entity (PyME) this cupo belongs to — the key the list row joins on.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The cupo's primary key. Carried for the deterministic tie-break, and for logging.</summary>
    public Guid CreditLineId { get; set; }

    /// <summary>Total approved amount of the cupo (the "tope").</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code of this cupo (e.g. 'ARS', 'USD').</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>UTC creation timestamp — the ordering key that decides which cupo is the main one.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Sum of <c>Amount</c> over the cupo's operations whose stored status is not 'Cancelada',
    /// summed in SQL. Same rule as <c>CreditLineRules.OccupiesCupo</c>: an 'Aprobada'
    /// operation reserves cupo from creation and an effective-'Vencida' one still consumes it,
    /// so only an explicit cancellation frees saldo. Zero when the cupo has no such operations.
    /// </summary>
    public decimal Utilizado { get; set; }
}
