// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Which of two extraction passes over the same document is kept (M10 Phase 2b). Pure.
/// </summary>
public static class LegajoExtractionSelection
{
    /// <summary>
    /// True when <paramref name="candidate"/> is STRICTLY better than <paramref name="incumbent"/>:
    /// fewer failed checks, then fewer missing required fields, then a higher mean confidence. A tie
    /// keeps the incumbent (the first pass). A pass whose output was unusable has no assessment and
    /// always loses.
    /// </summary>
    public static bool IsBetter(LegajoExtractionAssessment? incumbent, LegajoExtractionAssessment? candidate)
    {
        if (candidate is null)
            return false;
        if (incumbent is null)
            return true;

        if (candidate.FailedChecks != incumbent.FailedChecks)
            return candidate.FailedChecks < incumbent.FailedChecks;

        if (candidate.Missing.Count != incumbent.Missing.Count)
            return candidate.Missing.Count < incumbent.Missing.Count;

        return (candidate.MeanConfidence ?? 0m) > (incumbent.MeanConfidence ?? 0m);
    }
}
