// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The persona humana calibration board: how the pilot's analysts decided against the derived suggestion,
/// which filters lack data, which documents are missing or stale, the re-evaluations and the pf_ parameters.
/// READ-ONLY — nothing here writes; the export's audit row is the controller's.
/// </summary>
public interface IPersonCalibrationService
{
    /// <summary>
    /// The board over the PersonaHumana analyses created in <paramref name="period"/>, optionally narrowed to
    /// the CURRENT members of <paramref name="teamId"/>. Each section is computed independently: one that
    /// fails is null and named in <c>degradedSections</c>; the call itself does not fail for it.
    /// </summary>
    Task<PersonCalibrationResponse> GetBoardAsync(
        string tenantId, PersonCalibrationPeriod period, Guid? teamId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// The same population as a CSV for Excel in es-AR, one row per analysis. Unlike the board it is all or
    /// nothing: a partial spreadsheet would read as a complete one.
    /// </summary>
    Task<PersonCalibrationExportFile> ExportAsync(
        string tenantId, PersonCalibrationPeriod period, Guid? teamId, CancellationToken ct);
}
