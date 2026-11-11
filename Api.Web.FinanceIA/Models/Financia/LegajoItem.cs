// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[LegajoItems] (DDL 071): one asset or debt of a legajo's
/// reconciled inventory (M10 Phase 3b), with the market value of the manifestación de bienes and the
/// fiscal value of Bienes Personales side by side.
///
/// <para>
/// REBUILT by LegajoReconciliationService from the legajo's Ready documents, never written by hand: a
/// rebuild keeps the row whose <see cref="ItemKey"/> it computes again, rewrites its document-derived
/// columns except those listed in <see cref="EditedFields"/>, never touches <see cref="AnalystNote"/>,
/// and keeps <see cref="LegajoReconciliationStatuses.AnalystConfirmed"/> only while the same conflict
/// (<see cref="ConfirmedFingerprint"/>) still stands. Amounts are in pesos.
/// </para>
///
/// <para>PII: a natural person's patrimony. Never logged; returned to the tenant's users only.</para>
/// </summary>
public sealed class LegajoItem
{
    /// <summary>Primary key. Supplied by the application; kept across rebuilds.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Tenant that owns the row. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The legajo.</summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>The rebuild identity, unique per legajo (UX_LegajoItems_Key).</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>One of <see cref="LegajoItemSides.Known"/>.</summary>
    public string Side { get; set; } = LegajoItemSides.Activo;

    /// <summary>One of <see cref="LegajoItemKinds.Known"/>.</summary>
    public string Kind { get; set; } = LegajoItemKinds.Otro;

    /// <summary>
    /// One of <see cref="LegajoInvestmentClasses.Known"/>, only on an <see cref="LegajoItemKinds.Inversion"/> row; null
    /// when not classified (DDL 080, CK_LegajoItems_InvestmentClass).
    /// </summary>
    public string? InvestmentClass { get; set; }

    /// <summary>One of <see cref="LegajoMatchKeyKinds.Known"/>.</summary>
    public string MatchKeyKind { get; set; } = LegajoMatchKeyKinds.Ninguno;

    /// <summary>The normalized identifier, or null.</summary>
    public string? MatchKey { get; set; }

    /// <summary>Short description, as printed.</summary>
    public string? Description { get; set; }

    /// <summary>0..100, or null when no document prints it.</summary>
    public decimal? OwnershipPct { get; set; }

    /// <summary>Pesos, from the manifestación de bienes.</summary>
    public decimal? MarketValue { get; set; }

    /// <summary>The document <see cref="MarketValue"/> was read from.</summary>
    public Guid? MarketValueDocumentId { get; set; }

    /// <summary>Pesos, from Bienes Personales.</summary>
    public decimal? FiscalValue { get; set; }

    /// <summary>The document <see cref="FiscalValue"/> was read from.</summary>
    public Guid? FiscalValueDocumentId { get; set; }

    /// <summary>ISO code of the asset's currency when printed, else null.</summary>
    public string? Currency { get; set; }

    /// <summary>Assigned to the person's business activity.</summary>
    public bool IsActivityAsset { get; set; }

    /// <summary>The casa-habitación.</summary>
    public bool IsHousing { get; set; }

    /// <summary>XX-XXXXXXXX-X of the company held, when the match key is a CUIT.</summary>
    public string? LinkedCuit { get; set; }

    /// <summary>One of <see cref="LegajoReconciliationStatuses.Known"/>.</summary>
    public string ReconciliationStatus { get; set; } = LegajoReconciliationStatuses.Matched;

    /// <summary>CSV of <see cref="LegajoItemConflictReasons"/>, or null.</summary>
    public string? ConflictReasons { get; set; }

    /// <summary>CSV of <see cref="LegajoItemEditableFields"/> the analyst set, or null.</summary>
    public string? EditedFields { get; set; }

    /// <summary>The analyst's note. PII; never logged.</summary>
    public string? AnalystNote { get; set; }

    /// <summary>SHA-256 hex of the conflict the analyst confirmed, or null.</summary>
    public string? ConfirmedFingerprint { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC. The optimistic-concurrency token of the row.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>UserId of the analyst who last edited it; null when a rebuild last wrote it.</summary>
    public Guid? UpdatedBy { get; set; }
}
