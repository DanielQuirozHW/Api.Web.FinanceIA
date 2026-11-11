// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[LegajoIncomes] (DDL 071): one income or sales figure of a legajo
/// by source, concept and date range (M10 Phase 3b). REPLACED by every rebuild of
/// LegajoReconciliationService; carries no analyst state. Amounts are in pesos.
/// </summary>
public sealed class LegajoIncome
{
    /// <summary>Primary key. Supplied by the application.</summary>
    public Guid IncomeId { get; set; }

    /// <summary>Tenant that owns the row. Firebase tenant slug.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The legajo.</summary>
    public Guid PersonLegajoId { get; set; }

    /// <summary>YYYY-MM for a month, YYYY for a calendar year, null for any other range.</summary>
    public string? Period { get; set; }

    /// <summary>First day the amount covers.</summary>
    public DateOnly PeriodFrom { get; set; }

    /// <summary>Last day the amount covers.</summary>
    public DateOnly PeriodTo { get; set; }

    /// <summary>One of <see cref="LegajoIncomeSources.Known"/>.</summary>
    public string Source { get; set; } = LegajoIncomeSources.Iva;

    /// <summary>One of <see cref="LegajoIncomeConcepts.Known"/>.</summary>
    public string Concept { get; set; } = LegajoIncomeConcepts.Ventas;

    /// <summary>Pesos.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO code; ARS for every tax document.</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>The document the figure was read from.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The extraction run it was read from.</summary>
    public Guid ExtractionId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }
}
