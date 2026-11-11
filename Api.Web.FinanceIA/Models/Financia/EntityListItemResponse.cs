// Project : Api.Web.FinanceIA
// Created : 2026-08-25
// Modified: 2026-08-25

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One row of GET /api/entities — ONLY the fields the Portal's entities list renders.
///
/// Deliberately NOT <see cref="EntityResponse"/>. The list used to reuse the detail DTO, so every
/// row deserialised and shipped all five nvarchar(max) source snapshots — FiscalDataJson,
/// LufeDataJson, BcraDataJson, CasfogDataJson, NosisDataJson — none of which the table shows. That
/// measured 375 KB for a single pageSize=100 page. The list path now never reads those columns at
/// all; fetch GET /api/entities/{id} for them.
///
/// Every property keeps the exact name and type it had on <see cref="EntityResponse"/>, so the
/// emitted camelCase key and JSON type are unchanged for the fields that survive — including
/// <see cref="RiskScoreUpdatedAt"/>, which stays <c>riskScoreUpdatedAt</c> on the wire.
/// </summary>
public sealed class EntityListItemResponse : IEntityProjectionTarget
{
    public Guid EntityId { get; set; }
    public string CUIT { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public string? TradeName { get; set; }
    public string? Sector { get; set; }
    public string? MipymeCategory { get; set; }
    public bool IsMonitored { get; set; }

    /// <summary>
    /// LEGACY: <c>Entities.RiskScore</c>, a column this API never writes and that has no level beside
    /// it. Do not render it as the entity's score — render <see cref="LatestScore"/> with
    /// <see cref="LatestRiskLevel"/>. Kept only so the wire contract does not break.
    /// </summary>
    public decimal? RiskScore { get; set; }

    /// <summary>LEGACY, the timestamp of <see cref="RiskScore"/>. Use <see cref="LatestScoredAt"/>.</summary>
    public DateTime? RiskScoreUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>UserId of the author who created this entity. Null when unknown (existing rows).</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>"Juridica" or "Humana" (DDL 069). Same name and type as on <see cref="EntityResponse"/>.</summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;

    /// <summary>Monotributo / ResponsableInscripto / Exento / Desconocido, or null when not determined.</summary>
    public string? FiscalRegime { get; set; }

    // ── Projected fields (cross-row lookups, set by EnrichProjectionAsync) ─────

    /// <summary>Display name of the author. Null when the author is unknown or unresolved.</summary>
    public string? CreatedByName { get; set; }

    /// <summary>True when the entity has at least one non-terminal credit line. Set on the list endpoint.</summary>
    public bool HasCreditLine { get; set; }

    /// <summary>Status of the most recent analysis for this entity, or null if it has none.</summary>
    public string? LatestAnalysisStatus { get; set; }

    /// <summary>
    /// The score (0–100, motor scale) of the entity's most recent SCORED analysis result, or null
    /// when it has none. Always shipped with <see cref="LatestRiskLevel"/> from the same row.
    /// </summary>
    public decimal? LatestScore { get; set; }

    /// <summary>
    /// The risk level the BFF STORED for <see cref="LatestScore"/> (Low / Medium / High / Critical).
    /// Render this — never recompute a level from the score. Null exactly when there is no score.
    /// </summary>
    public string? LatestRiskLevel { get; set; }

    /// <summary>The analysis <see cref="LatestScore"/> belongs to, for linking; null with it.</summary>
    public Guid? LatestScoreAnalysisId { get; set; }

    /// <summary>When <see cref="LatestScore"/> was produced: its last re-score, else its generation (UTC).</summary>
    public DateTime? LatestScoredAt { get; set; }

    // ── Main credit line (cupo) — LIST ONLY ───────────────────────────────────
    //
    // The three fields below let the Portal's PyMEs table render "cupo + disponible + a
    // utilisation bar" from this payload alone, instead of one credit-line call per row.
    //
    // The MAIN cupo is the ACTIVE one with the most recent CreatedAt, whatever its currency —
    // not the largest amount, since amounts across currencies are not comparable. That is why
    // MainCreditLineCurrency ships alongside the figures and must be rendered with them.
    //
    // ALL THREE ARE NULL TOGETHER when the entity has no 'Active' cupo. Note this is a STRICTER
    // predicate than HasCreditLine, which counts 'Active' OR 'Suspended' (see
    // CreditLineRepository.NonTerminalStatuses). An entity whose only cupo is Suspended therefore
    // reports hasCreditLine: true with all three of these null — correct, not a bug: a suspended
    // cupo lends nothing further, so there is no live tope to draw a bar for.
    //
    // Deliberately NOT on IEntityProjectionTarget: that interface is shared with the DETAIL DTO
    // (EntityResponse), and these belong to the list row only. GET /api/entities/{id}'s payload
    // is unchanged; the credit-line detail lives at the credit-line endpoints.

    /// <summary>
    /// The main cupo's total approved amount (its "tope"), or null when the entity has no Active cupo.
    /// Read together with <see cref="MainCreditLineCurrency"/> — never compare it across currencies.
    /// </summary>
    public decimal? MainCreditLineAmount { get; set; }

    /// <summary>
    /// The main cupo's remaining saldo: <see cref="MainCreditLineAmount"/> minus the sum of its
    /// non-'Cancelada' operations. Null when the entity has no Active cupo.
    /// <para>
    /// NOT clamped at zero — an over-committed ("underwater") cupo reports a negative figure, exactly
    /// as <c>CreditLineResponse.Disponible</c> does. The client must render a negative saldo rather
    /// than assume the range is 0..amount.
    /// </para>
    /// </summary>
    public decimal? MainCreditLineDisponible { get; set; }

    /// <summary>
    /// ISO 4217 currency code of the main cupo (e.g. 'ARS', 'USD'), or null when the entity has no
    /// Active cupo. Load-bearing: the two figures above are meaningless without it.
    /// </summary>
    public string? MainCreditLineCurrency { get; set; }

    /// <summary>
    /// Whether <see cref="LatestScore"/> is PROVISIONAL, from the same analysis result row and by the same
    /// score-reliability verdict the analysis detail uses to say "Score provisorio"
    /// (<c>reliability.level == "provisional"</c>): true when provisional; false when the verdict is
    /// <c>complete</c> or <c>with_caveats</c> (a row scored before the facts were recorded included); null
    /// when there is no score, or no verdict (persona humana, level <c>none</c>). LIST ONLY, like the cupo
    /// fields above.
    /// </summary>
    public bool? LatestScoreProvisional { get; set; }
}
