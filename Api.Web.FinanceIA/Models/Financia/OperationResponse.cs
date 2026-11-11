// Project : Api.Web.FinanceIA
// Created : 2026-07-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Read model for a single operation under a cupo. <see cref="Status"/> is the
/// effective status: 'Vencida' is derived at read time (a stored-'Vigente' operation
/// whose ValidUntil is in the past) and is never persisted. An 'Aprobada' operation never
/// derives to 'Vencida' — it was never in force to begin with.
/// </summary>
public sealed class OperationResponse
{
    /// <summary>Operation primary key.</summary>
    public Guid OperationId { get; set; }

    /// <summary>Parent cupo id.</summary>
    public Guid CreditLineId { get; set; }

    /// <summary>Approved analysis this operation stems from.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>SGR One Click credit-line type code (0..17).</summary>
    public int Type { get; set; }

    /// <summary>Amount of this operation.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Validity expiry (vigencia); null when open-ended.</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Optional free-text notes.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// True when the operation was granted "a sola firma". Metadata only — it does not affect
    /// utilizado/disponible, the status lifecycle, or any validation. False for every operation
    /// created before DDL 057.
    /// </summary>
    public bool ASolaFirma { get; set; }

    /// <summary>
    /// True when the operation requires a "contragarantía real". Same nature as
    /// <see cref="ASolaFirma"/>, and independent of it.
    /// </summary>
    public bool RequiereContragarantiaReal { get; set; }

    /// <summary>
    /// Effective status: 'Aprobada', 'Vigente', 'Cancelada' or 'Vencida' (derived).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the operation was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// [dbo].[Users].UserId of the analyst who created this operation. Surfaced — unlike the
    /// confirming and cancelling users below — because it decides who may mutate the operation:
    /// the creator or an Admin. The client compares this against its own user id to know whether
    /// to offer the confirm/cancel affordances at all, rather than discovering the 403. No
    /// server-side "isOwned" is computed, mirroring how the chat conversation list works.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// Display name of the creating analyst, resolved in one batched lookup. Falls back to
    /// "Usuario" when the id no longer resolves to a user.
    /// </summary>
    public string CreatedByName { get; set; } = string.Empty;

    /// <summary>
    /// UTC instant an analyst confirmed the operation is in force. Null while still 'Aprobada',
    /// and null for operations confirmed before this was recorded (see DDL 040) — no date is
    /// reconstructable for those, so null is the honest answer rather than a plausible guess.
    /// The confirming user is deliberately not surfaced here; it lives in the audit trail.
    /// </summary>
    public DateTime? VigenteConfirmadaEl { get; set; }

    /// <summary>
    /// UTC instant the operation was cancelled — whether it fell through ("se cayó") or was closed.
    /// Null while the operation is still live, and null for operations cancelled before this was
    /// recorded (see DDL 041) — no date is reconstructable for those, so null is the honest answer
    /// rather than a plausible guess. As with <see cref="VigenteConfirmadaEl"/>, the cancelling user
    /// is deliberately not surfaced here; it lives in the audit trail, which is also the only place
    /// that says which of the two transitions this was.
    /// </summary>
    public DateTime? CanceladaEl { get; set; }
}
