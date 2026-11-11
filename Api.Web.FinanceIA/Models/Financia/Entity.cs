// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-05-04

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[Entities].
/// Represents a PyME (small or medium-sized enterprise) registered within a tenant.
/// </summary>
public sealed class Entity
{
    /// <summary>Primary key.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Tenant that owns this entity record. Firebase tenant slug (e.g. SgrOneClickSandboxFB2).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Argentine tax identification number (CUIT; the CUIL of a persona humana). Unique per tenant —
    /// UQ_Entities_TenantId_CUIT on (TenantId, CUIT), since DDL 069 — so the same CUIT may exist once
    /// in each tenant.
    /// </summary>
    public string CUIT { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="PersonTypes.Juridica"/> (a company, the default and every row before DDL 069) or
    /// <see cref="PersonTypes.Humana"/> (a natural person). Constrained by CK_Entities_PersonType.
    /// Initialised to the default so EF always sends a non-null value on insert.
    /// </summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;

    /// <summary>
    /// One of <see cref="FiscalRegimes.Known"/>, derived from ARCA. Null means "not determined yet"
    /// (legacy rows, and a Juridica created without fiscal data).
    /// </summary>
    public string? FiscalRegime { get; set; }

    /// <summary>
    /// The persona humana's legajo in [dbo].[PersonLegajos]. Null for every Juridica
    /// (CK_Entities_PersonLegajo_Humana). Deliberately a bare foreign key with NO navigation property:
    /// the relationship is modelled (EntityConfiguration) only so EF inserts a new legajo before the
    /// entity that points at it; a navigation would let Add/Update pull a detached legajo into the
    /// graph. Read the category through the repository projections instead.
    /// </summary>
    public Guid? PersonLegajoId { get; set; }

    /// <summary>Legal business name.</summary>
    public string BusinessName { get; set; } = string.Empty;

    /// <summary>Commercial trade name, if different from the legal business name.</summary>
    public string? TradeName { get; set; }

    /// <summary>Industry sector.</summary>
    public string? Sector { get; set; }

    /// <summary>MiPyME category assigned by SEPYME (e.g., Micro, Pequeña, Mediana).</summary>
    public string? MipymeCategory { get; set; }

    /// <summary>Entity identifier in the SGR OneClick system, if linked.</summary>
    public string? SgrOneClickEntityId { get; set; }

    /// <summary>Latest risk score computed by the AI analysis. Null until first analysis.</summary>
    public decimal? RiskScore { get; set; }

    /// <summary>UTC timestamp of the most recent risk score update. Null until first analysis.</summary>
    public DateTime? RiskScoreUpdatedAt { get; set; }

    /// <summary>When true, the entity is actively monitored for alerts.</summary>
    public bool IsMonitored { get; set; }

    /// <summary>UTC timestamp when the record was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UserId of the author who created this entity. Null for entities that
    /// predate author tracking (the UI renders "—"; no fabricated fallback).
    /// </summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>UTC timestamp of the most recent update to this record.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Full fiscal data snapshot from ARCA (JSON). Informational only.</summary>
    public string? FiscalDataJson { get; set; }

    /// <summary>UTC timestamp of the most recent ARCA refresh. Null until <see cref="FiscalDataJson"/> is refreshed.</summary>
    public DateTime? FiscalDataUpdatedAt { get; set; }

    /// <summary>Full Lufe (government registry) data snapshot (JSON). Informational only.</summary>
    public string? LufeDataJson { get; set; }

    /// <summary>UTC timestamp of the most recent Lufe refresh. Null until <see cref="LufeDataJson"/> is refreshed.</summary>
    public DateTime? LufeDataUpdatedAt { get; set; }

    /// <summary>Full BCRA (Central de Deudores) data snapshot (JSON). Informational only.</summary>
    public string? BcraDataJson { get; set; }

    /// <summary>UTC timestamp of the most recent BCRA refresh. Null until <see cref="BcraDataJson"/> is refreshed.</summary>
    public DateTime? BcraDataUpdatedAt { get; set; }

    /// <summary>Full CASFOG (SGR central de deudores / garantías) data snapshot (JSON). Informational only.</summary>
    public string? CasfogDataJson { get; set; }

    /// <summary>UTC timestamp of the most recent CASFOG refresh. Null until <see cref="CasfogDataJson"/> is refreshed.</summary>
    public DateTime? CasfogDataUpdatedAt { get; set; }

    /// <summary>Full NOSIS (WS.01 variables) data snapshot (JSON). Informational only.</summary>
    public string? NosisDataJson { get; set; }

    /// <summary>UTC timestamp of the most recent NOSIS refresh. Null until <see cref="NosisDataJson"/> is refreshed.</summary>
    public DateTime? NosisDataUpdatedAt { get; set; }

    /// <summary>
    /// The parsed NOSIS Manager "Informe Individual" an analyst uploaded (DDL 087), as the JSON of
    /// <c>Models.Nosis.NosisManagerReport</c>. Company-level data only; the PDF is never stored. Display
    /// only: no score, alert or prompt reads it. Null when no report was imported.
    /// </summary>
    public string? NosisReportJson { get; set; }

    /// <summary>
    /// The report's own date and time as its footer prints it — Argentina wall-clock time, NOT UTC
    /// (Kind Unspecified). Null exactly when <see cref="NosisReportJson"/> is.
    /// </summary>
    public DateTime? NosisReportDate { get; set; }

    /// <summary>UTC timestamp of the import. Null exactly when <see cref="NosisReportJson"/> is.</summary>
    public DateTime? NosisReportImportedAt { get; set; }
}
