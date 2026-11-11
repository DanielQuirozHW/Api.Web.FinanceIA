// Project : Api.Web.FinanceIA
// Created : 2026-07-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[CreditLineOperations].
/// A child operation under a credit line (cupo), tied to an approved analysis.
/// Database First — schema changes via DDL scripts only.
/// </summary>
public sealed class CreditLineOperation
{
    /// <summary>Primary key (database-assigned via NEWSEQUENTIALID()).</summary>
    public Guid OperationId { get; set; }

    /// <summary>Tenant that owns this operation.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Foreign key to [dbo].[CreditLines].CreditLineId — the parent cupo.</summary>
    public Guid CreditLineId { get; set; }

    /// <summary>Foreign key to [dbo].[Analyses].AnalysisId — the approved analysis this operation stems from.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>
    /// SGR One Click catalog code (0..17) from the credit-line type catalog.
    /// The 0..17 range is validated in the API; there is no DB CHECK constraint.
    /// </summary>
    public int Type { get; set; }

    /// <summary>Amount of this operation.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code (e.g. 'ARS', 'USD').</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Validity expiry (vigencia); null when open-ended.</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Free-text notes for this operation.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Persisted lifecycle status: 'Aprobada', 'Vigente' or 'Cancelada' ONLY. An operation is
    /// born 'Aprobada' (granted and committed, not yet confirmed in force) and reaches 'Vigente'
    /// only through an analyst's explicit confirmation. 'Vencida' is derived at read time from a
    /// stored-'Vigente' operation whose ValidUntil has passed — it is never stored, and an
    /// 'Aprobada' operation never derives to it.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>[dbo].[Users].UserId that created this operation.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>UTC timestamp when the operation was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// True when the operation was granted "a sola firma" (script 057). Pure metadata recorded at
    /// creation: it does not affect whether the operation occupies cupo, its status lifecycle, or
    /// any validation. False for every operation created before script 057 — the column backfills
    /// to 0, and the condition was never recorded anywhere structured to recover it from.
    /// </summary>
    public bool ASolaFirma { get; set; }

    /// <summary>
    /// True when the operation requires a "contragarantía real" (script 057). Same nature as
    /// <see cref="ASolaFirma"/>: metadata only, with no effect on cupo consumption or the lifecycle.
    /// The two flags are independent — an operation may carry both, either or neither.
    /// </summary>
    public bool RequiereContragarantiaReal { get; set; }

    /// <summary>
    /// UTC instant an analyst confirmed this operation is in force ('Aprobada' → 'Vigente').
    /// Null while still 'Aprobada', and null for operations that predate script 040 — we do not
    /// know when those became vigente, and null is the honest answer rather than a made-up date.
    /// </summary>
    public DateTime? VigenteConfirmadaEl { get; set; }

    /// <summary>
    /// [dbo].[Users].UserId of the analyst who confirmed vigencia. Null under the same two
    /// conditions as <see cref="VigenteConfirmadaEl"/>.
    /// </summary>
    public Guid? VigenteConfirmadaPor { get; set; }

    /// <summary>
    /// UTC instant this operation was cancelled — either it fell through ("se cayó", from
    /// 'Aprobada') or it was closed (from 'Vigente'). Null while the operation is still live, and
    /// null for operations cancelled before script 041 — we do not know when those were cancelled,
    /// and null is the honest answer rather than a made-up date. Which of the two transitions it
    /// was lives in the AuditLogs entry, not here: the row records that it happened, the trail
    /// records what it was before.
    /// </summary>
    public DateTime? CanceladaEl { get; set; }

    /// <summary>
    /// [dbo].[Users].UserId of the analyst who cancelled the operation. Null under the same two
    /// conditions as <see cref="CanceladaEl"/>.
    /// </summary>
    public Guid? CanceladaPor { get; set; }
}
