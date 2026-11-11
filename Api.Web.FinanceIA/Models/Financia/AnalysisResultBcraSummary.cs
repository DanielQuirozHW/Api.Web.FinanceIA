// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The only part of the BCRA blob the analysis DETAIL still ships — what the Score section at the
/// top of the page reads, and nothing else. Returned as <c>bcraSummary</c> by
/// GET /api/analyses/{id}.
///
/// <para>
/// This exists because <c>bcraData</c> on that endpoint was ~40KB on an entity with BCRA history —
/// 2930 rejected cheques and ~40 reporting banks on a real row — of which the Score read two
/// scalars. The rest renders only inside the Contexto section's BCRA tab and is now served, whole,
/// by GET /api/analyses/{id}/context alongside the four frozen snapshots. The two scalars here are
/// extracted SERVER-SIDE with <c>JSON_VALUE</c>, so producing this object costs no blob read on the
/// app side at all — see <see cref="AnalysisResultDetailProjection.BcraDeudaEstadoRaw"/>.
/// </para>
///
/// <para>
/// NESTED under <see cref="Deuda"/> rather than flattened, and using the same key names, so it is
/// shape-compatible with <c>bcraData.deuda</c>. That is what lets a client bridge the two
/// deployments with <c>bcraSummary?.deuda ?? bcraData?.deuda</c> instead of branching. Keep it that
/// way: this is a compatibility shim for one field group, not a place to design a better BCRA DTO.
/// </para>
///
/// <para>
/// The whole object is null when the analysis has no BCRA data — no blob at all, or a blob whose
/// <c>deuda</c> is null. Those two were distinguishable before this change (<c>bcraData: null</c>
/// versus <c>bcraData: {deuda: null, …}</c>) and are not now. Nothing rendered them differently:
/// the Score reads <c>deuda?.estado</c>, which was undefined either way.
/// </para>
/// </summary>
public sealed class AnalysisResultBcraSummary
{
    /// <summary>
    /// The Central de Deudores fields the Score reads. Non-null whenever this object exists — the
    /// nesting is for wire compatibility with <c>bcraData.deuda</c>, not to express a second
    /// absence.
    /// </summary>
    public BcraDeudaSummary? Deuda { get; set; }
}

/// <summary>
/// The two Central de Deudores fields the Score section renders. A strict subset of
/// <see cref="BcraDeudaData"/>, with identical key names and identical wire types, so the same
/// client code reads either.
/// </summary>
public sealed class BcraDeudaSummary
{
    /// <summary>
    /// Outcome of the debt lookup. Serialises as a NUMBER — 0 DebtFound, 1 NoDebtReported,
    /// 2 LookupFailed — because no <c>JsonStringEnumConverter</c> is registered in this project,
    /// which is exactly what <c>bcraData.deuda.estado</c> already emitted. Typed as the enum rather
    /// than <c>int?</c> so the ordinals stay defined in one place; the wire output is the same.
    ///
    /// <para>
    /// Nullable here although <see cref="BcraDeudaData.Estado"/> is not: this is projected from a
    /// <c>JSON_VALUE</c> that returns SQL NULL when there is no <c>deuda</c> node, and defaulting
    /// that to <c>DebtFound</c> would report debt for an analysis that never had a lookup.
    /// </para>
    /// </summary>
    public BcraDeudaConsultaEstado? Estado { get; set; }

    /// <summary>
    /// Highest risk classification across all reporting banks — 1 (normal) to 6 (irrecoverable).
    /// Null whenever the lookup found no debt or failed, so a populated <see cref="Estado"/> beside
    /// a null here is the normal shape rather than a partial read.
    /// </summary>
    public int? SituacionMaxima { get; set; }
}
