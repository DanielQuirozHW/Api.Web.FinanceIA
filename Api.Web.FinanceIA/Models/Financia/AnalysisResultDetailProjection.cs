// Project : Api.Web.FinanceIA
// Created : 2026-08-26
// Modified: 2026-08-26

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The columns of <see cref="AnalysisResult"/> that GET /api/analyses/{id} actually consumes — and
/// nothing else. A REPOSITORY projection, not a wire contract: it is the target of the
/// <c>.Select(...)</c> in <c>AnalysisResultRepository.BuildDetail</c>, so EF emits a SELECT naming
/// exactly these columns. The response DTO clients see is still <see cref="AnalysisResultResponse"/>.
///
/// <para>
/// This finishes on the analyses DETAIL path what #139 did on the analyses LIST path, and mirrors
/// what #141 did for the entity detail. <c>GetByIdAsync</c> is a full-row load, so SQL Server read
/// every <c>nvarchar(max)</c> column off disk on every detail request. On a representative 81KB row
/// the four frozen "Contexto" snapshots alone — LufeDataJson 26KB, FiscalDataJson 4.6KB,
/// CasfogDataJson 3.5KB, NosisDataJson NULL — were ~34KB, about half the blob I/O, for a section the
/// user has to open before it renders.
/// </para>
///
/// <para>
/// So <c>LufeDataJson</c>, <c>FiscalDataJson</c>, <c>CasfogDataJson</c> and <c>NosisDataJson</c> are
/// deliberately ABSENT. They are point-in-time copies frozen at analysis time, READ-ONLY, and the
/// read-time reconciliation on the detail path never touches them — it works on BalanceJson and
/// rewrites RiskAlerts / ExecutiveSummary / Anomalies. They are served on demand by
/// GET /api/analyses/{id}/context, which reads the stored columns and never calls a gateway.
/// </para>
///
/// <para>
/// <c>BcraDataJson</c> is absent too, but for a THIRD reason and by a different mechanism. #143 kept
/// it on the grounds that "the Score section renders it on load", which is true and far too coarse:
/// the Score reads exactly two scalars off it — <c>deuda.estado</c> and <c>deuda.situacionMaxima</c>.
/// Everything else in that blob — <c>entidadesInformantes</c>, <c>entidadesDetalle</c>,
/// <c>historialSituaciones</c>, the whole <c>cheques</c> block — renders only inside the Contexto
/// section's BCRA tab, so it belongs with the other four. On an entity with heavy BCRA history it is
/// ~40KB of a ~73KB payload, dominated by <c>cheques.detalle</c> (2930 entries on a real row).
/// So the blob moved to GET /api/analyses/{id}/context, and the two scalars are extracted SERVER-SIDE
/// by <c>JSON_VALUE</c> into <see cref="BcraDeudaEstadoRaw"/> and
/// <see cref="BcraDeudaSituacionMaximaRaw"/> — the column is named in the SELECT only as an argument,
/// never projected, so its 40KB never crosses the wire and is never deserialised.
/// </para>
///
/// <para>
/// Three further columns are absent for the plain reason that nothing on this path reads them at
/// all: <c>MacroDataJson</c> (read only by <c>BuildChatSystemPrompt</c>, which loads its own full
/// row), <c>ValidationNotes</c> (no reader in the project), and the <c>AiInputHash</c>/
/// <c>AiOutputHash</c> audit pair. None appears on <see cref="AnalysisResultResponse"/>, so omitting
/// them changes no payload.
/// </para>
///
/// <para>
/// Adding any snapshot property here — or restoring <c>BcraDataJson</c> — puts the column straight
/// back into the SELECT and undoes the change; <c>DetailQuery_SqlNeverNamesAContextSnapshot</c>,
/// <c>DetailQuery_ExtractsTheTwoScoreFieldsAndNeverProjectsTheBlob</c> and the shape-lock test
/// <c>AnalysisResultDetailProjection_ExposesExactlyTheColumnsTheDetailNeeds</c> exist to stop that.
/// </para>
/// </summary>
public sealed class AnalysisResultDetailProjection
{
    /// <summary>Primary key. The route key of the detail endpoint.</summary>
    public Guid ResultId { get; set; }

    /// <summary>FK to Analyses — the key the entity/user lookups and the cupo provenance join on.</summary>
    public Guid AnalysisId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this result was generated.</summary>
    public DateTime GeneratedAt { get; set; }

    // ── Blobs the detail renders on load ──────────────────────────────────────────

    /// <summary>
    /// Also the INPUT to both read-time computations on this path: the cash-flow narrative and the
    /// alert/executive-summary/anomaly percentage reconciliation. Not merely echoed.
    /// </summary>
    public string? BalanceJson { get; set; }

    public string? RatiosJson { get; set; }

    public string? ScoreBreakdownJson { get; set; }

    public string? ExecutiveSummary { get; set; }

    public string? ScoringNarrative { get; set; }

    /// <summary>LEGACY SURFACE. Rewritten in place by the read-time percentage reconciliation.</summary>
    public string? RiskAlerts { get; set; }

    /// <summary>DETERMINISTIC CHANNEL. The only column <c>ApplyDetailChannels</c> reads.</summary>
    public string? CatalogueAlerts { get; set; }

    public string? Anomalies { get; set; }

    // ── Extracted out of BcraDataJson, which is NOT selected ──────────────────────
    // Both are the output of a JSON_VALUE(BcraDataJson, ...) call in BuildDetail. That is the whole
    // point: SQL Server resolves the path and returns a handful of characters, so the 40KB blob
    // never crosses the wire and the app never deserialises it. The full object is served by
    // GET /api/analyses/{id}/context. (SQL Server still has to read the LOB to parse it — this
    // saves the transfer and the client-side parse, not the disk read.)
    //
    // string, not int?, because that is what JSON_VALUE returns — nvarchar(4000). A CAST in SQL
    // would buy nothing over parsing in the mapper, and would hide a malformed value behind a
    // conversion error on a read path that must not fail. MapToDetailResponse parses them.

    /// <summary>
    /// Raw <c>$.deuda.estado</c>, i.e. the TEXT of a <see cref="BcraDeudaConsultaEstado"/> ordinal —
    /// <c>"0"</c> DebtFound, <c>"1"</c> NoDebtReported, <c>"2"</c> LookupFailed. It is a number in
    /// the stored blob because no <c>JsonStringEnumConverter</c> is registered in this project.
    ///
    /// <para>
    /// Null means the analysis has no BCRA blob at all, OR has one whose <c>deuda</c> is null or
    /// absent. Those two are NOT distinguishable here, deliberately: the Score reads
    /// <c>deuda?.estado</c> and renders both identically. Since <c>Estado</c> is non-nullable in C#
    /// and every write site sets it, a non-null <c>deuda</c> always carries this key — so null here
    /// really does mean "no deuda".
    /// </para>
    /// </summary>
    public string? BcraDeudaEstadoRaw { get; set; }

    /// <summary>
    /// Raw <c>$.deuda.situacionMaxima</c> — the worst BCRA risk classification across reporting
    /// banks, <c>"1"</c> (normal) to <c>"6"</c> (irrecoverable). Independently nullable from
    /// <see cref="BcraDeudaEstadoRaw"/>: the stored field is <c>int?</c> and is genuinely null
    /// whenever the lookup found no debt or failed, so a populated estado with a null situación is
    /// the normal shape, not a defect.
    /// </summary>
    public string? BcraDeudaSituacionMaximaRaw { get; set; }

    /// <summary>
    /// Raw <c>$.deuda.fetchedAt</c> — when the Central de Deudores answer behind this result was
    /// actually fetched (round-trip UTC text; BcraService's cache can put it up to a day before the
    /// analysis). Dates the BCRA entry of the derived score sources
    /// (<see cref="Api.Web.FinanceIA.Servicios.AnalysisRuns.RescoreSources.ParseFetchedAt"/>). Null with
    /// no deuda block; an old failed block may carry the year-1 default, which the parser discards.
    /// </summary>
    public string? BcraDeudaFetchedAtRaw { get; set; }

    // ── Scalars ───────────────────────────────────────────────────────────────────

    public decimal? AiSuggestedScore { get; set; }

    public string? AiSuggestedLevel { get; set; }

    public int? AiInputTokens { get; set; }

    public int? AiOutputTokens { get; set; }

    public string ValidationStatus { get; set; } = string.Empty;

    public string EnrichmentStatus { get; set; } = string.Empty;

    public string? EnrichmentNotes { get; set; }

    /// <summary>AnalysisResults.RescoredAt (DDL 073): the latest re-score, null when never re-scored.</summary>
    public DateTime? RescoredAt { get; set; }

    /// <summary>AnalysisResults.RescoredBy (DDL 073).</summary>
    public Guid? RescoredBy { get; set; }

    /// <summary>AnalysisResults.RescoreCount (DDL 073).</summary>
    public int RescoreCount { get; set; }

    /// <summary>
    /// SourcesJson of the LATEST [AnalysisRescores] row for this analysis — the exact source dates the
    /// current score was computed from — or null when the result was never re-scored. A correlated
    /// subquery on IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt; dates only, never a payload.
    /// </summary>
    public string? LatestRescoreSourcesJson { get; set; }
}
