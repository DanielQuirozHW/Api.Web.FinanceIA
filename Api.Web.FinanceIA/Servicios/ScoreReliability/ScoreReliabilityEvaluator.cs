// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Codes = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityCodes;
using Messages = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityMessages;
using Policy = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityPolicy;
using Reason = (string Code, string Message, Api.Web.FinanceIA.Models.Financia.ScoreReliabilityReasonArgs? Args);

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The score-reliability rules (score credibility 1b-2b). Pure and static: no I/O, no clock, no logging —
/// every age is measured against the score's own date (<c>RescoredAt ?? GeneratedAt</c>), so the same row
/// always gets the same verdict. Reached only through <see cref="ScoreReliabilityAdapter"/>.
/// <list type="bullet">
/// <item>persona humana → null (another engine);</item>
/// <item>no score → <c>none</c> with <c>score_unavailable</c> alone;</item>
/// <item>otherwise <c>provisional</c> if any provisional reason, else <c>with_caveats</c> if any caveat,
/// else <c>complete</c>.</item>
/// </list>
/// Reasons are ordered none → provisional → caveat, and by <see cref="ScoreReliabilityCodes.Catalogue"/>
/// within a severity. An unknown date or period never fires a rule.
/// </summary>
internal static class ScoreReliabilityEvaluator
{
    private const string Degraded      = nameof(BcraEnrichmentOutcome.Degraded);
    private const string NotApplicable = nameof(BcraEnrichmentOutcome.NotApplicable);

    // The two ratios equity <= 0 makes meaningless (ScoringService.NegativeEquityFrom).
    private static readonly HashSet<string> EquityRatios = new(StringComparer.Ordinal) { "leverage", "roe" };

    // The order the Portal lists the ratios in; unknown keys follow, alphabetically.
    private static readonly List<string> CanonicalRatioOrder = [.. ClaudeService.RatioDisplayLabel.Keys];

    public static ScoreReliabilityResponse? Evaluate(ScoreReliabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var row = input.Row;
        if (string.Equals(row.AnalysisKind, AnalysisKinds.PersonaHumana, StringComparison.Ordinal))
            return null;

        if (row.AiSuggestedScore is null)
            return Build([(Codes.ScoreUnavailable, Messages.ScoreUnavailable, null)]);

        var reasons   = new List<Reason>();
        var facts     = input.Facts;
        var scoreDate = Policy.KnownUtc(row.RescoredAt) ?? Policy.KnownUtc(row.GeneratedAt);

        AddBcra(reasons, input, facts, scoreDate);
        AddIntegrity(reasons, input, facts);
        AddManualCorrections(reasons, facts);
        AddRatios(reasons, input, facts);

        if (facts?.SectorFallback == true)
            // The sector text rides the MESSAGE only; Args carries nothing of it (prompt-safe by construction).
            reasons.Add((Codes.SectorFallback, Messages.SectorFallback(facts.SectorRaw), null));

        AddBalanceAge(reasons, row, facts, scoreDate);
        AddLufe(reasons, facts, scoreDate);
        AddCasfog(reasons, facts, scoreDate);

        if (facts?.MacroImputed == true)
            reasons.Add((Codes.MacroImputed, Messages.MacroImputed, null));

        if (input.ThresholdFallbacks.Count > 0)
        {
            var keys = Ordered(input.ThresholdFallbacks);
            reasons.Add((Codes.ConfigThresholdFallback, Messages.ConfigThresholdFallback(keys), new ScoreReliabilityReasonArgs { RatioKeys = keys }));
        }

        switch (input.FactsState)
        {
            case ScoreReliabilityFactsState.Absent:
                reasons.Add((Codes.FactsUnavailable, Messages.FactsUnavailableBeforeRecording, null));
                break;
            case ScoreReliabilityFactsState.NotRecorded:
                reasons.Add((Codes.FactsUnavailable, Messages.FactsUnavailableNotRecorded, null));
                break;
            case ScoreReliabilityFactsState.Unreadable:
                reasons.Add((Codes.FactsUnavailable, Messages.FactsUnavailableUnreadable, null));
                break;
        }

        return Build(reasons);
    }

    // ── BCRA ────────────────────────────────────────────────────────────────

    private static void AddBcra(
        List<Reason> reasons, ScoreReliabilityInput input, ScoreReliabilityFacts? facts, DateTime? scoreDate)
    {
        var status        = input.Row.EnrichmentStatus;
        var degraded      = string.Equals(status, Degraded, StringComparison.Ordinal);
        var notApplicable = string.Equals(status, NotApplicable, StringComparison.Ordinal);

        // The Central de Deudores answered — the only way a Degraded row did not lose the BCRA component:
        // BcraEnrichmentClassifier marks it Degraded then only for a failed cheques lookup that counts.
        var deudaEstado   = facts?.Bcra?.DeudaEstado;
        var deudaAnswered = deudaEstado is nameof(BcraDeudaConsultaEstado.DebtFound)
                                        or nameof(BcraDeudaConsultaEstado.NoDebtReported);

        // Backstop: the engine itself excluded BCRA (whatever the stored status says), unless the exclusion
        // is the permanent "no CUIT" one, which bcra_not_applicable reports.
        var bcraUnavailable = (degraded && !deudaAnswered) || (input.BcraExcluded && !notApplicable);
        if (bcraUnavailable)
        {
            var message = input.BcraExclusionReason switch
            {
                BcraExclusionReasons.NoDeudaBlock => Messages.BcraUnavailableNoDeudaBlock,
                BcraExclusionReasons.NoBcraData   => Messages.BcraUnavailableNoData,
                _                                 => Messages.BcraUnavailableLookupFailed,
            };
            reasons.Add((Codes.BcraUnavailable, message, null));
        }

        var chequesFailed = facts?.Bcra is { ChequesEvaluated: true } bcra
                            && bcra.ChequesEstado == nameof(BcraChequesConsultaEstado.LookupFailed);
        if ((degraded && deudaAnswered) || chequesFailed)
            reasons.Add((Codes.ChequesUnavailable, Messages.ChequesUnavailable, null));

        if (facts?.BcraSituacionUnreadable == true)
            reasons.Add((Codes.BcraSituacionUnreadable, Messages.BcraSituacionUnreadable, null));

        // The period only means something when the Central de Deudores answered.
        if (deudaAnswered
            && scoreDate is { } date
            && Policy.ParsePeriodo(facts!.Bcra!.PeriodoConsultado) is { } periodo)
        {
            var lag = Policy.MonthIndex(date.Year, date.Month) - Policy.MonthIndex(periodo.Year, periodo.Month);
            if (lag > Policy.BcraPeriodLagMonths)
                reasons.Add((Codes.BcraPeriodLag, Messages.BcraPeriodLag(periodo, lag),
                             new ScoreReliabilityReasonArgs { Periodo = periodo, PeriodoMonths = lag }));
        }

        if (notApplicable)
            reasons.Add((Codes.BcraNotApplicable, Messages.BcraNotApplicable, null));
    }

    // ── Extraction integrity ────────────────────────────────────────────────

    private static void AddIntegrity(List<Reason> reasons, ScoreReliabilityInput input, ScoreReliabilityFacts? facts)
    {
        if (facts?.Integrity is not { } integrity)
            return;

        var row = input.Row;

        // An integrity verdict that never ran (integrity_unavailable) is "not passed" only because nothing
        // was proven: with no failed check, integrity_unverified alone says so truthfully.
        var integrityFailed = !integrity.Passed && (integrity.Verified || integrity.FailedChecks > 0);

        // Policy 5: when the ONLY failures are presentation checks (net_profit_reconciliation — RECPAM after the
        // tax, no pre-tax subtotal), the figures are read right and the balance adds up: a caveat, with no
        // integrity_failed and therefore no critical_floor_unverified and no figures-review requirement. A net
        // profit read with the wrong sign is a misread, not a presentation, and so is an unknown flip; a block
        // without names (recorded before facts version 4) is unknown too. Both keep the verdict they always had.
        // Policy 6: a pre-tax result and tax read from the comparative column (a column mix) is a misread too; an
        // unknown mix (no comparative figure, a block before facts version 6) keeps the verdict it always had.
        if (integrityFailed
            && BalanceIntegrityService.FailsOnlyOnPresentation(
                integrity.FailedCheckNames, integrity.NetProfitSignFlip, integrity.NetProfitColumnMix))
        {
            reasons.Add((Codes.IntegrityPresentation, Messages.IntegrityPresentation, null));
        }
        else if (integrityFailed)
        {
            // Same code and Args either way: only the message names the column mix.
            var message = integrity.NetProfitColumnMix == true
                ? Messages.IntegrityFailedColumnMix(integrity.FailedChecks)
                : Messages.IntegrityFailed(integrity.FailedChecks);
            reasons.Add((Codes.IntegrityFailed, message,
                         new ScoreReliabilityReasonArgs { Count = integrity.FailedChecks }));

            // Alongside integrity_failed, never instead of it: the same predicate, so "the figures do not
            // add up" means exactly what raises integrity_failed.
            AddCriticalFloorUnverified(reasons, input, integrity);
        }

        if (!integrity.Verified)
            reasons.Add((Codes.IntegrityUnverified, Messages.IntegrityUnverified(integrity.NotVerifiedReasons), null));

        // A caveat, never provisional: the level change is pending a decision with the SGR.
        if (string.Equals(integrity.DocumentKind, TrialBalanceNormalizer.DocumentKind, StringComparison.Ordinal))
            reasons.Add((Codes.TrialBalance, Messages.TrialBalance, null));

        if (string.Equals(integrity.FiscalYearStatus, FiscalYearCheck.Mismatch, StringComparison.Ordinal))
        {
            var requested = row.FiscalYear ?? facts.FiscalYear;
            reasons.Add((Codes.FiscalYearMismatch, Messages.FiscalYearMismatch(requested),
                         new ScoreReliabilityReasonArgs { FiscalYear = requested }));
        }

        if (integrity.AmountsUnitAssumed == true)
            reasons.Add((Codes.AmountsUnitAssumed, Messages.AmountsUnitAssumed, null));
    }

    // ── Critical floor on unverified figures (score credibility 1b-5) ─────────

    // The floor STAYS applied (the conservative choice); this says it decides the score from figures that do
    // not add up. Raised only when the floor actually moved the score and a critical ratio is computed from a
    // line a failed check involved. A block recorded before affectedRatios existed (null) cannot say which
    // ratios were affected, so every critical ratio is taken as involved: the conservative fallback.
    private static void AddCriticalFloorUnverified(
        List<Reason> reasons, ScoreReliabilityInput input, ScoreReliabilityIntegrityFacts integrity)
    {
        if (!input.CriticalFloorApplied)
            return;

        var involved = integrity.AffectedRatios is { } affected
            ? input.CriticalRatios.Where(k => affected.Contains(k, StringComparer.Ordinal)).ToList()
            : [.. input.CriticalRatios];
        if (involved.Count == 0)
            return;

        var keys  = Ordered(involved);
        int? score = input.ScoreBeforeFloor is { } before ? ScoreScale.ToNosis(before) : null;
        reasons.Add((Codes.CriticalFloorUnverified, Messages.CriticalFloorUnverified(keys, score),
                     new ScoreReliabilityReasonArgs { RatioKeys = keys, Score = score }));
    }

    // ── Manual corrections (Phase 10-F3a) ───────────────────────────────────

    // A caveat, not a provisional reason: the corrected figures passed (or failed) the same integrity
    // checks as a reading, and those checks speak for themselves above. This one says that a person, not
    // the reading, set some figures.
    private static void AddManualCorrections(List<Reason> reasons, ScoreReliabilityFacts? facts)
    {
        if (facts?.ManualCorrections is not ({ } count and > 0))
            return;

        var lastAt = Policy.KnownUtc(facts.ManualCorrectionsLastAt);
        reasons.Add((Codes.ManualCorrection, Messages.ManualCorrection(count, lastAt),
                     new ScoreReliabilityReasonArgs { Count = count, DateUtc = lastAt }));
    }

    // ── Ratios ──────────────────────────────────────────────────────────────

    private static void AddRatios(List<Reason> reasons, ScoreReliabilityInput input, ScoreReliabilityFacts? facts)
    {
        if (input.RatiosLowCoverageFallback)
        {
            // measured:false names them since #300; before it, nonComputableRatios is the same set.
            var unmeasured = input.Ratios.Where(kv => kv.Value.Measured == false).Select(kv => kv.Key).ToList();
            if (unmeasured.Count == 0)
                unmeasured = [.. input.NonComputableRatios];

            var keys = Ordered(unmeasured);
            reasons.Add((Codes.RatiosLowCoverage, Messages.RatiosLowCoverage(keys), new ScoreReliabilityReasonArgs { RatioKeys = keys }));
        }
        else
        {
            // The renormalise path: an excluded ratio was not read. A null reason is an exclusion whose
            // cause is unknown (the model's own ratios) — still a ratio the score did not measure. Interest
            // coverage is left out: its absence is the RT 6 format, not the company (Policy.RatioUnreadByPresentation).
            var unread = input.Ratios
                .Where(kv => kv.Value.Excluded
                             && (kv.Value.ExclusionReason is null
                                 || string.Equals(kv.Value.ExclusionReason, RatioExclusionReasons.Unread, StringComparison.Ordinal))
                             && !string.Equals(kv.Key, Policy.RatioUnreadByPresentation, StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .ToList();
            if (unread.Count > 0)
            {
                var keys = Ordered(unread);
                reasons.Add((Codes.RatiosNotMeasured, Messages.RatiosNotMeasured(keys), new ScoreReliabilityReasonArgs { RatioKeys = keys }));
            }
        }

        var negativeEquity = facts?.NegativeEquity == true;
        if (negativeEquity)
            reasons.Add((Codes.NegativeEquity, Messages.NegativeEquity, null));

        var notMeaningful = input.Ratios
            .Where(kv => kv.Value.Excluded
                         && string.Equals(kv.Value.ExclusionReason, RatioExclusionReasons.NotMeaningful, StringComparison.Ordinal)
                         && !(negativeEquity && EquityRatios.Contains(kv.Key)))
            .Select(kv => kv.Key)
            .ToList();
        if (notMeaningful.Count > 0)
        {
            var keys = Ordered(notMeaningful);
            reasons.Add((Codes.RatiosNotMeaningful, Messages.RatiosNotMeaningful(keys), new ScoreReliabilityReasonArgs { RatioKeys = keys }));
        }

        if (string.Equals(facts?.RatiosSource, ScoreReliabilityFactsBuilder.RatiosModel, StringComparison.Ordinal))
            reasons.Add((Codes.RatiosFromModel, Messages.RatiosFromModel, null));
    }

    // ── Ages ────────────────────────────────────────────────────────────────

    private static void AddBalanceAge(
        List<Reason> reasons, ScoreReliabilityRow row, ScoreReliabilityFacts? facts, DateTime? scoreDate)
    {
        // 31-Dec of the fiscal year: the latest the balance can close, so the age is a minimum.
        if (scoreDate is not { } date || (row.FiscalYear ?? facts?.FiscalYear) is not { } fiscalYear || fiscalYear < 1900)
            return;

        var months = Policy.MonthIndex(date.Year, date.Month) - Policy.MonthIndex(fiscalYear, 12);
        if (months >= Policy.BalanceStaleMonths)
            reasons.Add((Codes.BalanceStale, Messages.BalanceStale(months, fiscalYear),
                         new ScoreReliabilityReasonArgs { Months = months, FiscalYear = fiscalYear }));
    }

    private static void AddLufe(List<Reason> reasons, ScoreReliabilityFacts? facts, DateTime? scoreDate)
    {
        if (facts?.Lufe is not { } lufe)
            return;

        if (string.Equals(lufe.VentasEstado, ScoreReliabilityLufeVentasEstados.CarriedForward, StringComparison.Ordinal))
        {
            var asOf = Policy.KnownUtc(lufe.VentasAsOf);
            reasons.Add((Codes.LufeCarriedForward, Messages.LufeCarriedForward(asOf), new ScoreReliabilityReasonArgs { DateUtc = asOf }));
        }

        if (string.Equals(lufe.VentasEstado, ScoreReliabilityLufeVentasEstados.LookupFailed, StringComparison.Ordinal))
            reasons.Add((Codes.LufeUnavailable, Messages.LufeUnavailable, null));

        if (scoreDate is not { } date)
            return;

        DateTime? staleSnapshot = null;
        var snapshotMonths = 0;
        if (Policy.KnownUtc(lufe.SnapshotAt) is { } snapshotAt)
        {
            snapshotMonths = Policy.MonthsBefore(snapshotAt, date);
            if (snapshotMonths > Policy.LufeSnapshotStaleMonths)
                staleSnapshot = snapshotAt;
        }

        (int Year, int Month)? staleSales = null;
        var salesMonths = 0;
        if (Policy.ParsePeriodo(lufe.VentasUltimoPeriodo) is { } periodo)
        {
            salesMonths = Policy.MonthIndex(date.Year, date.Month) - Policy.MonthIndex(periodo.Year, periodo.Month);
            if (salesMonths > Policy.LufeSalesStaleMonths)
                staleSales = periodo;
        }

        if (staleSnapshot is not null || staleSales is not null)
            reasons.Add((Codes.LufeStale, Messages.LufeStale(staleSnapshot, snapshotMonths, staleSales, salesMonths),
                         new ScoreReliabilityReasonArgs
                         {
                             DateUtc       = staleSnapshot,
                             Months        = staleSnapshot is null ? null : snapshotMonths,
                             Periodo       = staleSales,
                             PeriodoMonths = staleSales is null ? null : salesMonths,
                         }));
    }

    private static void AddCasfog(List<Reason> reasons, ScoreReliabilityFacts? facts, DateTime? scoreDate)
    {
        if (facts?.Casfog is not { } casfog)
            return;

        if (string.Equals(casfog.Estado, nameof(CasfogConsultaEstado.LookupFailed), StringComparison.Ordinal))
        {
            reasons.Add((Codes.CasfogUnavailable, Messages.CasfogUnavailable, null));
            return;
        }

        if (scoreDate is { } date && Policy.KnownUtc(casfog.FetchedAt) is { } fetchedAt)
        {
            var months = Policy.MonthsBefore(fetchedAt, date);
            if (months > Policy.CasfogStaleMonths)
                reasons.Add((Codes.CasfogStale, Messages.CasfogStale(fetchedAt, months),
                             new ScoreReliabilityReasonArgs { DateUtc = fetchedAt, Months = months }));
        }
    }

    // ── Assembly ────────────────────────────────────────────────────────────

    private static ScoreReliabilityResponse Build(List<Reason> reasons)
    {
        var ordered = reasons
            .OrderBy(r => Codes.OrderOf(r.Code))
            .Select(r => new ScoreReliabilityReasonResponse
            {
                Code     = r.Code,
                Severity = Codes.SeverityOf(r.Code),
                Message  = r.Message,
                Args     = r.Args,
            })
            .ToList();

        var level = ordered.Any(r => r.Severity == ScoreReliabilitySeverities.None)        ? ScoreReliabilityLevels.None
                  : ordered.Any(r => r.Severity == ScoreReliabilitySeverities.Provisional) ? ScoreReliabilityLevels.Provisional
                  : ordered.Count > 0                                                      ? ScoreReliabilityLevels.WithCaveats
                  :                                                                          ScoreReliabilityLevels.Complete;

        return new ScoreReliabilityResponse
        {
            Level         = level,
            Reasons       = ordered,
            PolicyVersion = Policy.PolicyVersion,
        };
    }

    /// <summary>Ratio keys in the Portal's order, unknown keys last (alphabetically).</summary>
    private static List<string> Ordered(IEnumerable<string> keys)
        => keys.Distinct(StringComparer.Ordinal)
               .OrderBy(k => CanonicalRatioOrder.IndexOf(k) is var i and >= 0 ? i : int.MaxValue)
               .ThenBy(k => k, StringComparer.Ordinal)
               .ToList();
}
