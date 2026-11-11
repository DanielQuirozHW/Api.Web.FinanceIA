// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using System.Linq.Expressions;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The two read-time rules of a credit-line operation, in one place: whether it occupies cupo, and its
/// effective status.
/// <para>
/// Extracted from <c>CreditLinesController</c>, which owned them as private statics while the cupo
/// screen was their only reader. The portfolio dashboard sums and buckets the same operations, and a
/// second private copy is how a dashboard figure starts disagreeing with the cupo it summarises — the
/// reasoning that produced <see cref="Currencies"/> and <see cref="CreditLineTypes"/>.
/// </para>
/// </summary>
public static class CreditLineRules
{
    /// <summary>Stored: granted and committed, not yet confirmed in force.</summary>
    public const string Aprobada = "Aprobada";

    /// <summary>Stored: confirmed in force by an analyst.</summary>
    public const string Vigente = "Vigente";

    /// <summary>Stored: fell through or was closed. The only status that frees cupo.</summary>
    public const string Cancelada = "Cancelada";

    /// <summary>Derived, never stored: a stored-'Vigente' operation whose ValidUntil has passed.</summary>
    public const string Vencida = "Vencida";

    /// <summary>
    /// <see cref="OccupiesCupo"/> as an expression SQL Server can translate — a C# method cannot be.
    /// The same predicate <c>CreditLineRepository.BuildMainActive</c> sums under; parity with
    /// <see cref="OccupiesCupo"/> is pinned by CreditLineRulesTests.
    /// </summary>
    internal static readonly Expression<Func<CreditLineOperation, bool>> OccupiesCupoPredicate =
        o => o.Status != Cancelada;

    /// <summary>
    /// True when an operation reserves cupo. 'Aprobada' (granted, not yet confirmed in force),
    /// 'Vigente' and effective-'Vencida' all occupy it; only an explicit 'Cancelada' frees it.
    /// Stated as an exclusion rather than an inclusion so it agrees with
    /// <c>HasNonCancelledByAnalysisAsync</c>: an operation that blocks an analysis rejection is
    /// exactly one that consumes cupo. The two predicates drifting apart would let an operation
    /// block a rejection while reserving nothing, or the reverse.
    /// </summary>
    public static bool OccupiesCupo(CreditLineOperation op)
        => !string.Equals(op.Status, Cancelada, StringComparison.Ordinal);

    /// <summary>
    /// Effective operation status. 'Vencida' is derived at read time (a stored-'Vigente'
    /// operation whose ValidUntil has passed) and is never persisted.
    /// </summary>
    public static string EffectiveStatus(CreditLineOperation op)
        => EffectiveStatus(op, DateTime.UtcNow);

    /// <summary>
    /// <see cref="EffectiveStatus(CreditLineOperation)"/> as of <paramref name="nowUtc"/>, so one instant
    /// can drive a whole response. 'Vencida' is decided on the UTC DATE of that instant.
    /// </summary>
    public static string EffectiveStatus(CreditLineOperation op, DateTime nowUtc)
    {
        if (string.Equals(op.Status, Cancelada, StringComparison.Ordinal))
            return Cancelada;
        // Checked BEFORE ValidUntil, deliberately: 'Vencida' is a statement about a line that was
        // once in force, so an approval nobody ever confirmed cannot expire into it. An 'Aprobada'
        // operation past its ValidUntil still reads as 'Aprobada' — stale, but honestly so.
        if (string.Equals(op.Status, Aprobada, StringComparison.Ordinal))
            return Aprobada;
        if (op.ValidUntil.HasValue && op.ValidUntil.Value.Date < nowUtc.Date)
            return Vencida;
        return Vigente;
    }
}
