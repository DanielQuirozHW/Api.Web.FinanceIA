// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-08-25

using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Response DTO for a single entity.
/// Returned by GET /api/entities/{id}, GET /api/entities/cuit/{cuit},
/// POST /api/entities and PUT /api/entities/{id}.
/// </summary>
public sealed class EntityResponse : IEntityProjectionTarget
{
    public Guid EntityId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string CUIT { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public string? TradeName { get; set; }
    public string? Sector { get; set; }
    public string? MipymeCategory { get; set; }
    public string? SgrOneClickEntityId { get; set; }
    /// <summary>
    /// LEGACY: <c>Entities.RiskScore</c>, a column this API never writes and that has no level beside
    /// it. Do not render it as the entity's score — render <see cref="LatestScore"/> with
    /// <see cref="LatestRiskLevel"/>. Kept only so the wire contract does not break.
    /// </summary>
    public decimal? RiskScore { get; set; }

    /// <summary>LEGACY, the timestamp of <see cref="RiskScore"/>. Use <see cref="LatestScoredAt"/>.</summary>
    public DateTime? RiskScoreUpdatedAt { get; set; }

    public bool IsMonitored { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ArcaFiscalData? Fiscal { get; set; }
    public DateTime? FiscalDataUpdatedAt { get; set; }
    public LufeData? Lufe { get; set; }
    public DateTime? LufeDataUpdatedAt { get; set; }
    public BcraData? Bcra { get; set; }
    public DateTime? BcraDataUpdatedAt { get; set; }
    public CasfogData? Casfog { get; set; }
    public DateTime? CasfogDataUpdatedAt { get; set; }
    public NosisData? Nosis { get; set; }
    public DateTime? NosisDataUpdatedAt { get; set; }

    /// <summary>UserId of the author who created this entity. Null when unknown (existing rows).</summary>
    public Guid? CreatedBy { get; set; }

    // ── Persona humana (DDL 069) ──────────────────────────────────────────────

    /// <summary>"Juridica" (a company) or "Humana" (a natural person).</summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;

    /// <summary>Monotributo / ResponsableInscripto / Exento / Desconocido, or null when not determined.</summary>
    public string? FiscalRegime { get; set; }

    /// <summary>Monotributo category A–K, from the persona humana's legajo. Null otherwise.</summary>
    public string? MonotributoCategory { get; set; }

    /// <summary>The persona humana's legajo. Null for every Juridica.</summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>Display name of the author. Null when the author is unknown or unresolved.</summary>
    public string? CreatedByName { get; set; }

    /// <summary>True when the entity has at least one Active credit line. Set on the list endpoint.</summary>
    public bool HasCreditLine { get; set; }

    /// <summary>Status of the most recent analysis for this entity, or null if it has none.</summary>
    public string? LatestAnalysisStatus { get; set; }

    // ── Latest score (the entity header's source) ─────────────────────────────
    //
    // Filled, like LatestAnalysisStatus, by the shared enrichment on GET /api/entities/{id} (and on
    // every list row). The mutation and CUIT responses are not enriched and carry null here: read the
    // header from GET /api/entities/{id}.

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

    /// <summary>
    /// Number of LUFE balance documents imported automatically by this call. Set ONLY by
    /// POST /api/entities/{id}/refresh-lufe (0 when nothing new was imported); null — and
    /// therefore omitted from the JSON — everywhere else. The Portal reads it at the top level of
    /// the refresh body to show "Se guardó el balance de LUFE para análisis".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AutoImportedDocuments { get; set; }
}
