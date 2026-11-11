// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[AnalysisParticipants] (DDL 072): the role of one person in an
/// analysis, with the evaluation computed for them FROZEN on the row (M10 Phase 4a). Written when the
/// persona analysis is created; a later correction, upload or rebuild of the legajo does not change it
/// by itself. The ONE exception is an explicit "Reanalizar" (POST /api/analyses/{id}/rescore) while the
/// analysis still awaits its decision: it overwrites the evaluation columns in place — never the identity
/// or the terms — and keeps their previous values in [AnalysisRescores].
/// </summary>
/// <remarks>
/// The four JSON columns are plain strings serialised at the controller boundary, like
/// AnalysisResults.BcraDataJson. They carry figures and document ids, never the person's name or CUIT.
/// </remarks>
public sealed class AnalysisParticipant
{
    /// <summary>Primary key. Supplied by the application.</summary>
    public Guid ParticipantId { get; set; }

    /// <summary>Tenant that owns the row. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The analysis.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>The person's legajo.</summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>One of <see cref="ParticipantRoles.Known"/>.</summary>
    public string Role { get; set; } = ParticipantRoles.Solicitante;

    /// <summary>The amount this person answers for; for the solicitante, the requested amount.</summary>
    public decimal AmountAtCharge { get; set; }

    /// <summary>ISO 4217 code of <see cref="AmountAtCharge"/> and <see cref="InstallmentAmount"/>.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The cuota of the requested operation, when the analyst stated it.</summary>
    public decimal? InstallmentAmount { get; set; }

    /// <summary>JSON: the documents, extraction runs and corrections the evaluation read, and the snapshot stamps.</summary>
    public string DocumentVersionsJson { get; set; } = "{}";

    /// <summary>JSON array of the pre-filters.</summary>
    public string FiltersJson { get; set; } = "[]";

    /// <summary>JSON array of the indicators.</summary>
    public string IndicatorsJson { get; set; } = "[]";

    /// <summary>JSON object: the documentary coverage index.</summary>
    public string CoverageJson { get; set; } = "{}";

    /// <summary>RESERVED for the score of a later phase. Always NULL in Phase 4a.</summary>
    public string? ScoreJson { get; set; }

    /// <summary>The evaluation engine version that produced the row.</summary>
    public string EvaluationVersion { get; set; } = string.Empty;

    /// <summary>
    /// UTC instant of the evaluation stored on the row (its AsOf): the creation, or the latest re-evaluation.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    // ── Fiador columns (DDL 077). Written on Fiador rows by POST /api/person-analyses (Phase 5a-2). ──

    /// <summary>
    /// The [dbo].[Fiadores] row a <see cref="ParticipantRoles.Fiador"/> participant stands for. The
    /// database requires it on a Fiador row and forbids it on a Solicitante row
    /// (CK_AnalysisParticipants_FiadorId_Role). FK_AnalysisParticipants_Fiadores is not modelled.
    /// </summary>
    public Guid? FiadorId { get; set; }

    /// <summary>The kind of guarantee the fiador gives: one of <see cref="GuaranteeTypes.Known"/>, validated in code (no CHECK).</summary>
    public string? GuaranteeType { get; set; }

    /// <summary>The spouse's assent (asentimiento conyugal) was obtained. Implies marital status: personal data.</summary>
    public bool? SpouseConsent { get; set; }

    /// <summary>
    /// HousingInopposable = true: the housing protection (CCyC art. 244/456) was declared inopposable to this
    /// creditor by deed, so the home IS executable for this guarantee.
    /// </summary>
    public bool? HousingInopposable { get; set; }

    /// <summary>The guarantees the fiador already gives elsewhere, in <see cref="Currency"/>. Never negative.</summary>
    public decimal? GuaranteesGivenAmount { get; set; }

    // ── Agro annual mode (DDL 078, engine 0.7) — the solicitante's terms; NULL on a fiador row ──────────

    /// <summary>
    /// How many cuotas a year <see cref="InstallmentAmount"/> is (1, 2, 4 or 12), as RESOLVED when the analysis
    /// was created. NULL on rows written before DDL 078, which mean 12 (a monthly cuota) when re-evaluated.
    /// </summary>
    public byte? CuotasPorAnio { get; set; }

    /// <summary>The analyst's agro annual mode override (true | false); NULL detects it from the ARCA activity.</summary>
    public bool? AgroModeOverride { get; set; }

    // ── Declared installments (DDL 079, engine 0.8) — solicitante and fiador rows ──────────────────────

    /// <summary>
    /// The monthly cuotas vigentes the analyst declared for this person, in pesos. NULL: not declared — the
    /// evaluation estimates them from the Central de Deudores, as on every row written before DDL 079. Zero is a
    /// declaration (no installments). Never negative.
    /// </summary>
    public decimal? ExistingInstallmentsAmount { get; set; }
}
