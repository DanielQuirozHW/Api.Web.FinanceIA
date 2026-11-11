// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The four frozen source snapshots of one analysis, as returned by
/// GET /api/analyses/{id}/context — the "Contexto" section of the analysis detail.
///
/// <para>
/// Property names and types match <see cref="AnalysisResultResponse.Lufe"/>,
/// <see cref="AnalysisResultResponse.Fiscal"/>, <see cref="AnalysisResultResponse.Casfog"/> and
/// <see cref="AnalysisResultResponse.Nosis"/> exactly, so the JSON keys are <c>lufe</c>,
/// <c>fiscal</c>, <c>casfog</c> and <c>nosis</c> and the Portal reuses the parsing it already has
/// for the detail payload.
/// </para>
///
/// <para>
/// Every field is a POINT-IN-TIME copy taken when the analysis ran, not the live entity value — the
/// analysis view is meant to keep showing the data used at calification time, independent of later
/// entity refreshes. This endpoint READS what is stored: no gateway call, no write, no side effect,
/// nothing that could re-freeze a snapshot. The live values live behind
/// GET /api/entities/{id}/bcra, /casfog and /nosis, which are a different question about a
/// different resource.
/// </para>
///
/// <para>
/// A field is null when that source was unavailable at analysis time, or when the stored snapshot
/// could not be parsed. Deliberately NOT distinguishable from each other, exactly as on the detail
/// response today. <see cref="Nosis"/> is null on every existing row — no integration writes that
/// column yet — and nulls are serialised (<c>DefaultIgnoreCondition</c> is not configured), so it is
/// emitted as <c>"nosis": null</c> just as the detail payload already emits it.
/// </para>
///
/// <para>
/// There are no <c>*DataUpdatedAt</c> fields here, unlike the entity per-source responses: these
/// snapshots are frozen and never refreshed, so their capture time is the analysis's own
/// <c>generatedAt</c>, which the detail response already carries.
/// </para>
/// </summary>
public sealed class AnalysisContextResponse
{
    /// <summary>
    /// LUFE (government registry) data as it was when the analysis ran. The heaviest of the four —
    /// 26KB on a representative row — and the main reason this section is fetched on demand.
    /// </summary>
    public LufeData? Lufe { get; set; }

    /// <summary>Fiscal/ARCA data as it was when the analysis ran.</summary>
    public ArcaFiscalData? Fiscal { get; set; }

    /// <summary>CASFOG (SGR central de deudores / garantías) data as it was when the analysis ran.</summary>
    public CasfogData? Casfog { get; set; }

    /// <summary>
    /// NOSIS (WS.01 variables) data as it was when the analysis ran. Null on every row today.
    /// <see cref="NosisData"/> carries PII — including natural-person data (documento, fecha de
    /// nacimiento, sexo, edad, domicilio) since the analyst's primary variable set was mapped. It
    /// is returned to the authenticated, tenant-scoped caller exactly as the detail response
    /// already did, and it is never logged.
    /// </summary>
    public NosisData? Nosis { get; set; }

    /// <summary>
    /// BCRA data as it was when the analysis ran — Central de Deudores, macro, cambiario and
    /// rejected cheques. Still the heaviest field here, though no longer by the margin it was:
    /// <c>cheques.detalle</c> has come off it.
    ///
    /// <para>
    /// <c>cheques.detalle</c> IS DELIBERATELY OMITTED — it is always <c>null</c> on this endpoint —
    /// and is served by GET /api/analyses/{id}/bcra/cheques as
    /// <see cref="AnalysisBcraChequesResponse"/>. The array is one object per rejected cheque, ~295
    /// bytes serialised, and dominated this payload: #149 measured the whole blob at 859.295
    /// characters on a 2930-cheque profile. The tab renders the aggregates and the causal chart when
    /// it opens and the row-by-row table only when the analyst scrolls to it, so the rows now cost a
    /// second request instead of being paid for on every open. Each row's wire shape is unchanged —
    /// same <c>BcraChequeDetalle</c> type — so a client only swaps where it fetches them from.
    /// </para>
    ///
    /// <para>
    /// EVERYTHING ELSE IS UNCHANGED. Both cheque totals, both montos, the first and last rejection
    /// dates, the per-causal breakdown in <c>causales</c>, and the judicial and revision flags are
    /// all still here — none of them is derived from the array at read time, they are computed once
    /// in <c>BcraService.MapCheques</c> when the snapshot is written. <c>deuda</c>, <c>macro</c> and
    /// <c>cambiario</c> are untouched.
    /// </para>
    ///
    /// <para>
    /// Why <c>null</c> and not <c>[]</c>: <see cref="BcraChequesData.Detalle"/> documents <c>[]</c>
    /// as "no cheques reported", which would contradict a non-zero <c>totalRechazados</c> three keys
    /// above it. It documents <c>null</c> as "legacy stored data", but that reading cannot mislead
    /// here because the value is CONSTANT on this endpoint — no row ever comes back with the array
    /// populated — and <c>totalRechazados</c> stays authoritative regardless. Callers must read the
    /// count from the aggregate, never from the array's length.
    /// </para>
    ///
    /// <para>
    /// ADDITIVE otherwise, and the odd one out. The other four moved here whole in #143; this one
    /// arrived later and only mostly. The detail still renders <c>deuda.estado</c> and
    /// <c>deuda.situacionMaxima</c> in its Score section, so it keeps those two — extracted by
    /// SQL Server, in <see cref="AnalysisResultResponse.BcraSummary"/> — while everything else, all
    /// of which lives in the Contexto BCRA tab, comes from here. A client rendering that tab needs
    /// this field; one rendering only the Score does not.
    /// </para>
    ///
    /// <para>
    /// Type and key match <see cref="AnalysisResultResponse.BcraData"/>, and the parse is the SAME
    /// helper the detail used before the split, so a blob that rendered there renders identically
    /// here apart from the omitted array. Note <c>bcraData</c> on the POST and decision responses
    /// still carries <c>detalle</c>: those already load the whole row for other reasons.
    /// </para>
    ///
    /// <para>
    /// Null when BCRA was unavailable at analysis time, when the entity had no CUIT, or when the
    /// stored snapshot could not be parsed — deliberately not distinguishable, as with the other
    /// four. Note this column is left NULL outright when every BCRA call failed, rather than storing
    /// a document with a <c>LookupFailed</c> estado.
    /// </para>
    /// </summary>
    public BcraData? Bcra { get; set; }
}
