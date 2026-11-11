// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One persona humana indicator, as frozen in AnalysisParticipants.IndicatorsJson (M10 Phase 4a).</summary>
public sealed record PersonIndicatorResult
{
    /// <summary>Stable id, e.g. <c>cuota_ingreso</c>.</summary>
    public required string Id { get; init; }

    /// <summary>One of <see cref="PersonIndicatorStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>The figure; null when <see cref="Status"/> is SinDatos.</summary>
    public decimal? Value { get; init; }

    /// <summary>ARS (pesos), ratio (a fraction) or months.</summary>
    public required string Unit { get; init; }

    /// <summary>PROVISIONAL band where the design gives cuts (<see cref="PersonIndicatorBands"/>); otherwise null.</summary>
    public string? Band { get; init; }

    /// <summary>An alert the indicator raises (<see cref="PersonSeverities"/>), e.g. creditor &gt; fisco; otherwise null.</summary>
    public string? Severity { get; init; }

    /// <summary>How it was computed, or what is missing, in es-AR.</summary>
    public required string Evidence { get; init; }

    /// <summary>One of <see cref="PersonEvidenceKinds.Known"/>: whether the sources behind it are verified or declared.</summary>
    public string EvidenceKind { get; init; } = PersonEvidenceKinds.Declarado;

    /// <summary>The inputs and thresholds, invariant-formatted.</summary>
    public IReadOnlyDictionary<string, string?> Inputs { get; init; } = new Dictionary<string, string?>();

    /// <summary>The legajo documents the figures came from.</summary>
    public IReadOnlyList<Guid> SourceDocumentIds { get; init; } = [];

    /// <summary>True for every indicator in this phase: the cuts and formulas await the SGR's calibration.</summary>
    public bool Provisional { get; init; } = true;
}
