// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Whether the critical-ratio floor actually MOVED a persisted score, read from the two numbers the breakdown
/// stores (<c>scoreBeforeFloor</c>, <c>appliedFloor</c>). The one read-side copy of the rule: the analyst chat
/// and the score-reliability verdict both call it, so they cannot disagree about the same row.
/// <para>
/// <c>ScoreBeforeFloor &lt; AppliedFloor</c> reproduces ScoringService's own guard bit-for-bit (it compares the
/// same 2dp-rounded composite against the same unrounded floor). <c>appliedFloor</c> alone is the floor
/// COMPUTED from the critical ratios whether or not it bound, so it cannot answer by itself. When
/// <c>scoreBeforeFloor</c> is ABSENT (an older blob) the pair cannot tell, and the older, more permissive
/// <c>AppliedFloor &gt; 0</c> is kept rather than silently dropping a floor that may well have bound.
/// </para>
/// </summary>
internal static class CriticalFloor
{
    /// <summary>True when the floor raised the composite (on the internal 0-100 risk scale).</summary>
    public static bool WasApplied(decimal? scoreBeforeFloor, decimal? appliedFloor)
        => scoreBeforeFloor is null
            ? appliedFloor > 0
            : scoreBeforeFloor < appliedFloor;
}
