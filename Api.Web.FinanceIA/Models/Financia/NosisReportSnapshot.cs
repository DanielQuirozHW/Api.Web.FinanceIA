// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The stored NOSIS Manager report of one entity (DDL 087). A REPOSITORY projection, not a wire
/// contract: the target of <c>NosisReportRepository.GetAsync</c>'s <c>.Select(...)</c>, which names
/// the key, the report blob and its two dates — and no other <c>nvarchar(max)</c> column.
///
/// <para>
/// A null return from the repository means NO SUCH ENTITY in this tenant (a 404). A non-null instance
/// whose <see cref="Json"/> is null means the entity exists and has no report.
/// </para>
/// </summary>
public sealed class NosisReportSnapshot
{
    /// <summary>Primary key of the entity.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The stored report JSON, or null when none was imported.</summary>
    public string? Json { get; set; }

    /// <summary>The report's own footer date (Argentina wall-clock time, not UTC).</summary>
    public DateTime? ReportDate { get; set; }

    /// <summary>When it was imported, UTC.</summary>
    public DateTime? ImportedAt { get; set; }
}
