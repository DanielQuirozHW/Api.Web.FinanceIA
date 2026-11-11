// Project : Api.Web.FinanceIA
// Created : 2026-09-07

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One operation (operación) attached to a credit decision, as the analyst proposed it at
/// pre-approval or as the RiskManager/Admin settled it at confirmation.
///
/// <para>
/// The SAME type is used on the way in (<see cref="AnalysisDecisionRequest.Operations"/>) and on the
/// way out (<see cref="AnalysisResultResponse.ProposedOperations"/> /
/// <see cref="AnalysisResultResponse.ConfirmedOperations"/>), so a confirmed proposal round-trips
/// with no translation layer: the shape matches <see cref="CreateOperationRequest"/> field for
/// field and the Portal can hand an element straight to
/// <c>POST /api/credit-lines/{creditLineId}/operations</c>.
/// </para>
///
/// <para>
/// The one deliberate omission is <c>AnalysisId</c>. On <see cref="CreateOperationRequest"/> it ties
/// a loose operation to the analysis that justifies it; here the operation is stored INSIDE that
/// analysis, so carrying the id again would create a second, contradictable copy of the same fact.
/// </para>
///
/// <para>
/// Nothing here is a persisted operation. These are the terms of a decision, frozen on
/// [dbo].[Analyses]; the real rows in [dbo].[CreditLineOperations] are still created by
/// <c>CreditLinesController</c> once the analysis is Approved.
/// </para>
/// </summary>
public sealed class AnalysisDecisionOperation
{
    /// <summary>SGR One Click credit-line type code (0..17), validated against <c>CreditLineTypes</c>.</summary>
    public int Type { get; set; }

    /// <summary>Amount of this operation. Must be greater than zero.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// ISO 4217 currency code. Optional on the way in: omitted or blank, it inherits the currency of
    /// the decision itself, which is the only currency the resulting cupo can carry. When supplied it
    /// must match that currency. Always populated on the way out.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>Validity expiry (vigencia); null when open-ended.</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Optional free-text notes for this operation.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// The operation is proposed "a sola firma". Defaults to false when omitted. Metadata carried
    /// through to the eventual operation row, with one exception: on a persona humana approval whose
    /// tenant requires a counter-guarantee coverage (pf_contragarantia_cobertura_min_pct above 0) any
    /// operation carrying it is refused with 400 person_sola_firma_not_allowed.
    /// </summary>
    public bool ASolaFirma { get; set; }

    /// <summary>
    /// The operation requires a "contragarantía real". Defaults to false when omitted, and is
    /// independent of <see cref="ASolaFirma"/> — both, either or neither may be set.
    /// </summary>
    public bool RequiereContragarantiaReal { get; set; }
}
