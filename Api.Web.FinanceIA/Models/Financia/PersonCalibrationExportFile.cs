// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The calibration CSV and what it covers.</summary>
public sealed record PersonCalibrationExportFile
{
    /// <summary>The file bytes: UTF-8 with BOM.</summary>
    public required byte[] Content { get; init; }

    /// <summary><c>calibracion_persona_{from}_{to}.csv</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>The analyses the file lists, in its row order — for the audit row.</summary>
    public required IReadOnlyList<Guid> AnalysisIds { get; init; }
}
