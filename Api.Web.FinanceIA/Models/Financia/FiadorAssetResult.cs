// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One asset of a fiador's breakdown (M10 Phase 5a-2): the reconciled LegajoItem as the evaluation read it,
/// what value it used, which haircut it applied and why. Frozen inside AnalysisParticipants.CoverageJson.
/// </summary>
public sealed record FiadorAssetResult
{
    /// <summary>The LegajoItems row.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>One of <see cref="FiadorAssetOrigins"/>: the fiador's legajo or, for a spouse, the solicitante's.</summary>
    public required string Origin { get; init; }

    /// <summary>One of <see cref="LegajoItemKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// For an Inversion: its class (LegajoInvestmentClasses), which picked the haircut (engine 0.9); null when not
    /// classified, for every other kind, and on every asset frozen before 0.9.
    /// </summary>
    public string? InvestmentClass { get; init; }

    /// <summary>The item's description, as reconciled.</summary>
    public string? Description { get; init; }

    /// <summary>One of <see cref="LegajoMatchKeyKinds"/>.</summary>
    public required string MatchKeyKind { get; init; }

    /// <summary>The normalized match key (partida, dominio, CUIT, CBU), the de-duplication key of the aggregate.</summary>
    public string? MatchKey { get; init; }

    /// <summary>The ownership percentage printed by the documents. Informative: the values already are the person's share.</summary>
    public decimal? OwnershipPct { get; init; }

    /// <summary>The manifestación value, in pesos.</summary>
    public decimal? MarketValue { get; init; }

    /// <summary>The Bienes Personales value, in pesos.</summary>
    public decimal? FiscalValue { get; init; }

    /// <summary>The value the haircut applied to: the manifestación's, else the fiscal one; null when neither exists.</summary>
    public decimal? ValueUsed { get; init; }

    /// <summary><c>manifestacion</c> or <c>bienes_personales</c>; null without a value.</summary>
    public string? ValueSource { get; init; }

    /// <summary>Only the fiscal value exists (the manifestación does not list the asset).</summary>
    public required bool SoloValorFiscal { get; init; }

    /// <summary>The document the value used came from.</summary>
    public Guid? SourceDocumentId { get; init; }

    /// <summary>The ISO code the documents printed for the asset, when any.</summary>
    public string? Currency { get; init; }

    /// <summary>The casa-habitación.</summary>
    public required bool IsHousing { get; init; }

    /// <summary>The haircut of the asset's kind (a fraction); null when it was not counted.</summary>
    public decimal? Haircut { get; init; }

    /// <summary>The extra currency-mismatch haircut (pf_haircut_descalce_moneda) when applied; null otherwise.</summary>
    public decimal? CurrencyHaircut { get; init; }

    /// <summary>ValueUsed × (1 − haircut − currency haircut), floored at 0; 0 when not counted.</summary>
    public required decimal ExecutableValue { get; init; }

    /// <summary>One of <see cref="FiadorAssetTreatments"/>.</summary>
    public required string Treatment { get; init; }

    /// <summary>One of <see cref="FiadorAssetReasons"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>The reason in es-AR.</summary>
    public required string ReasonText { get; init; }

    /// <summary>
    /// <see cref="PersonEvidenceKinds.Declarado"/> for a manifestación value, <see cref="PersonEvidenceKinds.Verificado"/>
    /// for a value read from the Bienes Personales return.
    /// </summary>
    public required string EvidenceKind { get; init; }

    /// <summary>Codes of <see cref="FiadorAssetAlerts"/>.</summary>
    public IReadOnlyList<string> Alerts { get; init; } = [];
}
