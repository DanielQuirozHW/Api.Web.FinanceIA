// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Text.Json;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The one serialisation of the frozen evaluation columns of [dbo].[AnalysisParticipants]: camelCase, the
/// same shape the API returns. Reading is defensive — a blob that will not parse reads as absent, never as
/// a failed request, the same contract as the vendor snapshots.
/// </summary>
public static class PersonEvaluationJson
{
    /// <summary>Web defaults (camelCase, case-insensitive read).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serialises a frozen column value.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Reads a frozen column value; null when absent or unreadable.</summary>
    public static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The frozen filter results of one participant row; empty when the column is absent or unreadable —
    /// the same fallback <see cref="ToResponse"/> applies, so a suggestion derived from it is the one the
    /// detail shows.
    /// </summary>
    public static IReadOnlyList<PersonFilterResult> FiltersOf(string? filtersJson)
        => TryDeserialize<List<PersonFilterResult>>(filtersJson) ?? [];

    /// <summary>
    /// What approving a persona humana analysis requires: the tenant's CURRENT pf_ rules applied to the
    /// suggestion frozen on the solicitante's filters. The one resolution behind GET /api/analyses/{id}
    /// (personEvaluation.decisionRules), PATCH .../decision and the approvals dashboard, so none of them
    /// can disagree about who may confirm.
    /// </summary>
    /// <param name="config">The tenant's pf_ rows, as IPersonAnalysisRepository.GetEvaluationConfigAsync reads them.</param>
    /// <param name="solicitanteFiltersJson">The solicitante's FiltersJson; null when the analysis has none.</param>
    public static PersonDecisionRules DecisionRules(
        IReadOnlyDictionary<string, decimal> config, string? solicitanteFiltersJson)
    {
        ArgumentNullException.ThrowIfNull(config);

        return PersonDecisionRules.From(
            PersonEvaluationParameters.From(config),
            PersonEvaluationService.Suggest(FiltersOf(solicitanteFiltersJson)));
    }

    /// <summary>
    /// The decision rules of an analysis with its participants: <see cref="DecisionRules(IReadOnlyDictionary{string, decimal}, string?)"/>
    /// over the solicitante's frozen suggestion, plus the aggregate coverage of the fiador rows, recomputed from
    /// them (see <see cref="CounterGuaranteeCoverageOf"/>). PURE: the one computation behind the POST response,
    /// GET /api/analyses/{id}, PATCH .../decision and the persona PDF.
    /// </summary>
    public static PersonDecisionRules DecisionRules(
        IReadOnlyDictionary<string, decimal> config,
        AnalysisParticipant? solicitante,
        IReadOnlyList<AnalysisParticipant> fiadores)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(fiadores);

        var rules = DecisionRules(config, solicitante?.FiltersJson);
        return rules.WithCounterGuaranteeCoverage(
            CounterGuaranteeCoverageOf(solicitante, fiadores, rules.MinCounterGuaranteeCoveragePct));
    }

    /// <summary>
    /// The solicitante of a persona humana analysis, its fiadores and the decision rules that apply: the
    /// tenant's CURRENT pf_ rules over the suggestion frozen on the solicitante's filters, with the aggregate
    /// counter-guarantee coverage of the frozen fiador rows. The ONE read behind GET /api/analyses/{id},
    /// PATCH .../decision and the persona PDF, so what the Portal shows, what the decision enforces and what
    /// the report prints cannot disagree.
    /// </summary>
    public static async Task<(PersonDecisionRules Rules, AnalysisParticipant? Solicitante, IReadOnlyList<PersonFiadorEvaluationResponse> Fiadores)>
        ResolveSolicitanteAsync(IPersonAnalysisRepository repository, Guid analysisId, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var participants = await repository
            .GetParticipantsAsync(analysisId, tenantId, ct)
            .ConfigureAwait(false);
        var solicitante = participants.FirstOrDefault(p => p.Role == ParticipantRoles.Solicitante);
        var fiadores    = FiadorRowsOf(participants);

        var config = await repository
            .GetEvaluationConfigAsync(tenantId, ct)
            .ConfigureAwait(false);

        var identities = fiadores.Count == 0
            ? new Dictionary<Guid, FiadorIdentity>()
            : await repository
                .GetFiadorIdentitiesAsync(fiadores.Select(f => f.FiadorId!.Value).ToList(), tenantId, ct)
                .ConfigureAwait(false);

        return (DecisionRules(config, solicitante, fiadores), solicitante, ToFiadorResponses(fiadores, identities));
    }

    /// <summary>
    /// The Fiador rows of an analysis in their one canonical order (CreatedAt, then ParticipantId), so the
    /// POST, which holds them in memory, and every later read list them — and break de-duplication ties — the
    /// same way.
    /// </summary>
    public static IReadOnlyList<AnalysisParticipant> FiadorRowsOf(IEnumerable<AnalysisParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);

        return participants
            .Where(p => p.Role == ParticipantRoles.Fiador && p.FiadorId is not null)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.ParticipantId)
            .ToList();
    }

    /// <summary>
    /// The <c>personEvaluation</c> of a detail response: the solicitante's frozen row with the decision rules
    /// that apply to it; null when the analysis has no solicitante.
    /// </summary>
    public static PersonEvaluationResponse? ToDetailResponse(PersonDecisionRules rules, AnalysisParticipant? solicitante)
        => ToDetailResponse(rules, solicitante, []);

    /// <summary>
    /// The <c>personEvaluation</c> of a detail response: the solicitante's frozen row with the decision rules
    /// that apply to it, its fiadores and their aggregate coverage (the rules' own, never a second
    /// computation); null when the analysis has no solicitante. Fiadores and coverage stay null without fiadores.
    /// </summary>
    public static PersonEvaluationResponse? ToDetailResponse(
        PersonDecisionRules rules, AnalysisParticipant? solicitante, IReadOnlyList<PersonFiadorEvaluationResponse> fiadores)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(fiadores);

        return solicitante is null
            ? null
            : ToResponse(solicitante) with
            {
                DecisionRules            = rules,
                Fiadores                 = fiadores.Count == 0 ? null : fiadores,
                CounterGuaranteeCoverage = rules.CounterGuaranteeCoverage
            };
    }

    /// <summary>The API view of the fiador rows, in order, with each one's name and CUIT from the Fiadores row.</summary>
    public static IReadOnlyList<PersonFiadorEvaluationResponse> ToFiadorResponses(
        IReadOnlyList<AnalysisParticipant> fiadores, IReadOnlyDictionary<Guid, FiadorIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(fiadores);
        ArgumentNullException.ThrowIfNull(identities);

        return fiadores
            .Select(f => ToFiadorResponse(f, f.FiadorId is { } id && identities.TryGetValue(id, out var identity) ? identity : null))
            .ToList();
    }

    /// <summary>The API view of one stored Fiador row; <paramref name="identity"/> is null when the Fiadores row was not found.</summary>
    public static PersonFiadorEvaluationResponse ToFiadorResponse(AnalysisParticipant participant, FiadorIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var filters = FiltersOf(participant.FiltersJson);

        return new PersonFiadorEvaluationResponse
        {
            ParticipantId            = participant.ParticipantId,
            FiadorId                 = participant.FiadorId ?? Guid.Empty,
            Name                     = identity?.Name,
            Cuit                     = identity?.Cuit,
            PersonLegajoId           = participant.PersonLegajoId,
            GuaranteeType            = participant.GuaranteeType,
            AmountAtCharge           = participant.AmountAtCharge,
            Currency                 = participant.Currency,
            InstallmentAmount        = participant.InstallmentAmount,
            SpouseConsent            = participant.SpouseConsent,
            HousingInopposable       = participant.HousingInopposable,
            GuaranteesGivenAmount    = participant.GuaranteesGivenAmount,
            CuotasVigentesDeclaradas = participant.ExistingInstallmentsAmount,
            EvaluationVersion        = participant.EvaluationVersion,
            Suggestion               = PersonEvaluationService.Suggest(filters),
            Filters                  = filters,
            Indicators               = TryDeserialize<List<PersonIndicatorResult>>(participant.IndicatorsJson) ?? [],
            Coverage                 = TryDeserialize<FiadorCoverageResult>(participant.CoverageJson),
            Sources                  = TryDeserialize<PersonEvaluationSources>(participant.DocumentVersionsJson),
            CreatedAt                = participant.CreatedAt
        };
    }

    /// <summary>
    /// The aggregate counter-guarantee coverage, RECOMPUTED from the frozen Fiador rows; null without fiadores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1. A fiador whose frozen pre-filters suggest a rejection is left out of the headline figures
    /// (ExecutableTotal, Ratio, MeetsMin, Band) and listed with the reason; ExecutableTotalIncludingFlagged
    /// shows what the coverage would be counting him.
    /// </para>
    /// <para>
    /// 2. Between fiadores, an asset with the same match key (partida, dominio, CUIT, CBU) counts every
    /// holder's share when all of them print an ownership percentage and the shares add up to at most 100 %
    /// (each is then a real share, since the values already are the person's share). Otherwise it is counted
    /// once, for the holder with the highest executable value; the others' copies are duplicates.
    /// </para>
    /// <para>
    /// 3. Each fiador contributes min(max(0, executable assets after de-duplication − liabilities − guarantees
    /// given), amount at his charge): no one can be executed beyond what he guarantees, and a negative net
    /// never reduces another fiador's coverage.
    /// </para>
    /// <para>
    /// The asset values are the frozen ones; <paramref name="minPct"/> is the tenant's current minimum, as the
    /// decision applies it. The band follows it — Verde when the minimum is met, Rojo otherwise, null without a
    /// ratio — not the per-fiador pf_cobertura_* cuts, which only the fiador's own (uncapped) indicator uses.
    /// </para>
    /// </remarks>
    public static CounterGuaranteeCoverage? CounterGuaranteeCoverageOf(
        AnalysisParticipant? solicitante, IReadOnlyList<AnalysisParticipant> fiadores, int minPct)
    {
        ArgumentNullException.ThrowIfNull(fiadores);

        if (fiadores.Count == 0)
            return null;

        var rows = fiadores
            .Select(f => new AggregateRow(
                f,
                TryDeserialize<FiadorCoverageResult>(f.CoverageJson),
                PersonEvaluationService.Suggest(FiltersOf(f.FiltersJson)) == PersonSuggestions.RechazoSugerido))
            .ToList();

        var headline = Contributions(rows.Where(r => !r.Flagged).ToList());
        var all      = Contributions(rows);

        var requested = solicitante?.AmountAtCharge ?? 0m;
        var currency  = solicitante?.Currency ?? fiadores[0].Currency;
        var comparable = string.Equals(currency, "ARS", StringComparison.Ordinal)
                         && requested > 0m
                         && rows.All(r => r.Coverage is null || r.Coverage.Comparable);

        var total = headline.Values.Sum(c => c.Contribution);
        decimal? ratio = comparable ? Math.Round(total / requested, 4) : null;

        // The band says whether the minimum is met. Not the per-fiador pf_cobertura_* cuts: every contribution
        // is capped at its amount at charge (at most the requested amount), so on those cuts one fiador who
        // fully covers the aval could never be better than Amarillo.
        var meetsMin = ratio is { } value ? value * 100m >= minPct : minPct <= 0;
        string? band = ratio is null
            ? null
            : meetsMin ? PersonIndicatorBands.Verde : PersonIndicatorBands.Rojo;

        return new CounterGuaranteeCoverage
        {
            ExecutableTotal                 = total,
            ExecutableTotalIncludingFlagged = all.Values.Sum(c => c.Contribution),
            FlaggedFiadoresCount            = rows.Count(r => r.Flagged),
            RequestedAmount                 = requested,
            Currency                        = currency,
            Ratio                           = ratio,
            MinPct                          = minPct,
            MeetsMin                        = meetsMin,
            Band                            = band,
            FiadoresCount                   = rows.Count,
            Contributions                   = rows.Select(r => new CounterGuaranteeContribution
            {
                FiadorId                     = r.Row.FiadorId!.Value,
                ParticipantId                = r.Row.ParticipantId,
                Counted                      = !r.Flagged,
                Reason                       = r.Flagged ? FiadorAssetReasons.RechazoSugerido : null,
                Contribution                 = r.Flagged ? 0m : headline[r.Row.ParticipantId].Contribution,
                ContributionIncludingFlagged = all[r.Row.ParticipantId].Contribution,
                DuplicateItemIds             = (r.Flagged ? all : headline)[r.Row.ParticipantId].Duplicates
            }).ToList()
        };
    }

    private sealed record AggregateRow(AnalysisParticipant Row, FiadorCoverageResult? Coverage, bool Flagged);

    /// <summary>The de-duplicated, capped contribution of each row of <paramref name="rows"/>, by ParticipantId.</summary>
    private static Dictionary<Guid, (decimal Contribution, IReadOnlyList<Guid> Duplicates)> Contributions(
        IReadOnlyList<AggregateRow> rows)
    {
        var counted = rows.ToDictionary(
            r => r.Row.ParticipantId,
            r => (r.Coverage?.Assets ?? []).Where(a => a.Treatment == FiadorAssetTreatments.Computado).ToList());

        var duplicates = rows.ToDictionary(r => r.Row.ParticipantId, _ => new List<Guid>());

        // Holders of each trustworthy key, per fiador, in the rows' order.
        var holdersByKey = rows
            .SelectMany(r => counted[r.Row.ParticipantId]
                .Select(a => (Key: PersonEvaluationService.DeduplicationKey(a.MatchKeyKind, a.MatchKey), r.Row.ParticipantId, Asset: a)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal);

        foreach (var group in holdersByKey)
        {
            var holders = group
                .GroupBy(x => x.ParticipantId)
                .Select(h => (ParticipantId: h.Key,
                              Executable: h.Sum(x => x.Asset.ExecutableValue),
                              Shares: h.Select(x => x.Asset.OwnershipPct).ToList(),
                              Items: h.Select(x => x.Asset.ItemId).ToList()))
                .ToList();

            if (holders.Count < 2)
                continue;

            var sharesFit = holders.All(h => h.Shares.All(s => s is not null))
                            && holders.Sum(h => h.Shares.Sum(s => s!.Value)) <= 100.01m;
            if (sharesFit)
                continue;

            var keeper = holders.OrderByDescending(h => h.Executable).First().ParticipantId;
            foreach (var holder in holders.Where(h => h.ParticipantId != keeper))
                duplicates[holder.ParticipantId].AddRange(holder.Items);
        }

        return rows.ToDictionary(
            r => r.Row.ParticipantId,
            r =>
            {
                var id     = r.Row.ParticipantId;
                var assets = counted[id].Where(a => !duplicates[id].Contains(a.ItemId)).Sum(a => a.ExecutableValue);
                var net    = assets - (r.Coverage?.Liabilities ?? 0m) - (r.Coverage?.GuaranteesGivenAmount ?? 0m);
                var contribution = Math.Min(Math.Max(0m, net), r.Row.AmountAtCharge);
                return (contribution, (IReadOnlyList<Guid>)duplicates[id]);
            });
    }

    /// <summary>The API view of one stored participant row.</summary>
    public static PersonEvaluationResponse ToResponse(AnalysisParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        var filters = FiltersOf(participant.FiltersJson);
        var sources = TryDeserialize<PersonEvaluationSources>(participant.DocumentVersionsJson);

        return new PersonEvaluationResponse
        {
            ParticipantId            = participant.ParticipantId,
            PersonLegajoId           = participant.PersonLegajoId,
            Role                     = participant.Role,
            AmountAtCharge           = participant.AmountAtCharge,
            Currency                 = participant.Currency,
            InstallmentAmount        = participant.InstallmentAmount,
            CuotasPorAnio            = participant.CuotasPorAnio ?? sources?.Agro?.CuotasPorAnio,
            CuotasVigentesDeclaradas = participant.ExistingInstallmentsAmount,
            AgroMode                 = sources?.Agro,
            EvaluationVersion        = participant.EvaluationVersion,
            Suggestion               = PersonEvaluationService.Suggest(filters),
            Filters                  = filters,
            Indicators               = TryDeserialize<List<PersonIndicatorResult>>(participant.IndicatorsJson) ?? [],
            Coverage                 = TryDeserialize<PersonCoverageResult>(participant.CoverageJson),
            Sources                  = sources,
            CreatedAt                = participant.CreatedAt
        };
    }
}
