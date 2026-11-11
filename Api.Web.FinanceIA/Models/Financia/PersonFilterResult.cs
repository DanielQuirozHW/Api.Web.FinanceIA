// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One persona humana pre-filter, as frozen in AnalysisParticipants.FiltersJson (M10 Phase 4a).</summary>
public sealed record PersonFilterResult
{
    /// <summary>Stable id, e.g. <c>bcra_situacion</c>.</summary>
    public required string Id { get; init; }

    /// <summary>One of <see cref="PersonFilterOutcomes.Known"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>For an <see cref="PersonFilterOutcomes.Alerta"/>, one of <see cref="PersonSeverities"/>; otherwise null.</summary>
    public string? Severity { get; init; }

    /// <summary>What was found, in es-AR, for the analyst. Never the person's name or CUIT.</summary>
    public required string Evidence { get; init; }

    /// <summary>Where it came from, e.g. "BCRA · Central de Deudores".</summary>
    public required string Source { get; init; }

    /// <summary>One of <see cref="PersonEvidenceKinds.Known"/>: whether the sources behind it are verified or declared.</summary>
    public string EvidenceKind { get; init; } = PersonEvidenceKinds.Declarado;

    /// <summary>The figures read and the thresholds applied, invariant-formatted.</summary>
    public IReadOnlyDictionary<string, string?> Inputs { get; init; } = new Dictionary<string, string?>();
}
