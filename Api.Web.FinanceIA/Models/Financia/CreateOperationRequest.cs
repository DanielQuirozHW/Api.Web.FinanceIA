// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using System.ComponentModel.DataAnnotations;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for POST /api/credit-lines/{creditLineId}/operations.
/// Creates a child operation under a cupo, tied to an approved analysis.
/// </summary>
public sealed class CreateOperationRequest
{
    /// <summary>Foreign key to the approved analysis this operation stems from.</summary>
    [Required]
    public Guid AnalysisId { get; set; }

    /// <summary>SGR One Click credit-line type code (0..17).</summary>
    public int Type { get; set; }

    /// <summary>Amount of this operation. Must be greater than zero.</summary>
    [Required]
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code; must match the parent cupo's currency.</summary>
    [Required]
    public string Currency { get; set; } = string.Empty;

    /// <summary>Validity expiry (vigencia); null when open-ended.</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>Optional free-text notes for this operation.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Optional: the operation was granted "a sola firma". Defaults to false when the field is
    /// omitted. Metadata only — it does not affect the cupo sum rule, the status the operation is
    /// born with, or any validation.
    /// </summary>
    public bool ASolaFirma { get; set; }

    /// <summary>
    /// Optional: the operation requires a "contragarantía real". Defaults to false when omitted,
    /// and is independent of <see cref="ASolaFirma"/> — both, either or neither may be set.
    /// </summary>
    public bool RequiereContragarantiaReal { get; set; }
}
