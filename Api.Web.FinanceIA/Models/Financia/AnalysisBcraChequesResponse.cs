// Project : Api.Web.FinanceIA
// Created : 2026-08-27
// Modified: 2026-08-27

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The per-cheque rows of one analysis's frozen BCRA snapshot, as returned by
/// GET /api/analyses/{id}/bcra/cheques — the cheque-by-cheque table of the "Contexto" BCRA tab.
///
/// <para>
/// This is the array <see cref="AnalysisContextResponse.Bcra"/> deliberately stopped carrying.
/// <c>cheques.detalle</c> is one object per rejected cheque, ~295 bytes serialised, and on a
/// cheque-heavy CUIT it is very nearly the whole BCRA payload — #149 measured 859.295 characters
/// for the blob on a 2930-cheque profile. The Contexto tab renders the aggregates and the causal
/// chart when it opens; the row-by-row table is a separate thing the analyst scrolls to. So it
/// moved behind its own URL, exactly as GET /api/entities/{id}/bcra, /casfog and /nosis serve the
/// entity snapshots that GET /api/entities/{id} stopped shipping.
/// </para>
///
/// <para>
/// A wrapper object rather than a bare array, so a count, a paging envelope or a "the snapshot
/// predates this field" flag can be added later without changing the shape callers already parse.
/// That anticipated evolution is now the point of the type: the default response no longer carries
/// the array at all.
/// </para>
///
/// <para>
/// ── The default response is a SUMMARY ─────────────────────────────────────────────────────
/// #150 moved the array off <c>/context</c>, which fixed that endpoint but only relocated the cost:
/// this one still shipped all 2936 rows, and the Portal's use for nearly all of them was to fold
/// them into a ~24-point monthly series in the browser. That fold now happens server-side —
/// <see cref="Evolucion"/> is the result — so the default response carries the series, the date
/// bounds, the <see cref="RecientesCount"/> most recent rows and the true
/// <see cref="Total"/>, and NOT the array. <c>?full=true</c> opts back into it for the "ver todos"
/// table, which is the one view that genuinely needs every row.
/// </para>
///
/// <para>
/// The element type is <see cref="BcraChequeDetalle"/> — the SAME type that was nested inside the
/// blob on <c>/context</c> — so each cheque's wire shape is byte-identical to what the Portal
/// already parses, on both <see cref="Recientes"/> and <see cref="Cheques"/>. Only which rows
/// arrive, and under which key, is new.
/// </para>
///
/// <para>
/// Read-only: it reads one stored column and consults no external source. These are point-in-time
/// copies taken when the analysis ran, never refreshed, so there is no timestamp to carry and no
/// refresh counterpart — the live entity's cheques are GET /api/entities/{entityId}/bcra.
/// </para>
/// </summary>
public sealed class AnalysisBcraChequesResponse
{
    /// <summary>
    /// How many rows <see cref="Recientes"/> carries at most. The table's first screen, not a page
    /// size: there is no offset parameter, because the caller that wants more wants ALL of them and
    /// asks with <c>full=true</c>.
    /// </summary>
    public const int RecientesCount = 50;

    /// <summary>
    /// The monthly evolution folded from the full stored array — the bars, the line, the totals and
    /// the axis maxima. ALWAYS present; all-zero with an empty <c>meses</c> when there are no usable
    /// rows. This is what replaced shipping the array by default.
    /// </summary>
    public ChequesEvolucion Evolucion { get; set; } = new();

    /// <summary>
    /// The rejection-date range across every stored row, so a date filter can bound itself without
    /// holding them. ALWAYS present; both fields null when no row is dated.
    /// </summary>
    public ChequeDateBounds DateBounds { get; set; } = new();

    /// <summary>
    /// The <see cref="RecientesCount"/> most recent rejected cheques — the head of the stored
    /// <c>fechaRechazo</c>-descending order, so this is genuinely the recent ones (rows with no
    /// rejection date sort to the tail and appear only once the list is short enough to reach them).
    ///
    /// <para>
    /// ALWAYS an array, never null, in both modes. Empty covers the same four cases as before: the
    /// analysis froze no BCRA snapshot, the snapshot no longer parses, BCRA reported no cheques for
    /// the CUIT, or the stored document predates the <c>detalle</c> field. None is an error — an
    /// analysis that does not exist in this tenant is the 404, and that is the only 404 this
    /// endpoint returns.
    /// </para>
    /// </summary>
    public List<BcraChequeDetalle> Recientes { get; set; } = [];

    /// <summary>
    /// How many rejected cheques the snapshot holds in total — the length the array WOULD have.
    /// Reported in both modes, so a caller holding only <see cref="Recientes"/> still knows whether
    /// it is looking at all of them.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Every rejected cheque, ordered by <c>fechaRechazo</c> descending as stored — but ONLY when
    /// the request passed <c>full=true</c>.
    ///
    /// <para>
    /// NULL means "not requested", and is the default. That is deliberately distinct from an empty
    /// array, which would assert that the analysis HAS no cheques — a claim this endpoint would be
    /// making falsely on the 2936-row snapshots the split exists for, and one no caller could detect.
    /// A client that needs the rows asks for them; a client that reads this key without asking gets
    /// a null it can recognise as a contract mismatch rather than silently rendering an empty table.
    /// </para>
    ///
    /// <para>
    /// Not paginated even then. The count is bounded by what BCRA reports over a ~24-month window,
    /// the Portal sorts and filters that table client-side, and it is now fetched only when the
    /// analyst opens it — which is where the saving is.
    /// </para>
    /// </summary>
    public List<BcraChequeDetalle>? Cheques { get; set; }
}
