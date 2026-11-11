// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// What the verification of a suggestion decided, with the counts the logs carry (never a figure).
/// </summary>
/// <param name="Outcome">One of <see cref="BalanceSuggestionOutcomes"/>.</param>
/// <param name="Message">es-AR, ready to show.</param>
/// <param name="Suggestions">The verified proposals (fixes / reduces only).</param>
/// <param name="FailedChecksBefore">Failed targeted checks (<see cref="BalanceSuggestionPlanner.TargetedChecks"/>) over the stored figures.</param>
/// <param name="FailedChecksAfter">Failed targeted checks with the proposals applied.</param>
/// <param name="Proposed">Changed values the model proposed (after every drop).</param>
/// <param name="DroppedOutside">Answers for a path outside the whitelist or not asked, or a stored index that does not exist.</param>
/// <param name="DroppedInvalid">Values that are not finite or out of range.</param>
/// <param name="Unchanged">Values equal to the stored one.</param>
/// <param name="UnmatchedPrinted">Printed rubro lines the model matched to no stored line.</param>
/// <param name="Derived">Derived proposals kept (0 or 1: result_before_tax).</param>
public sealed record BalanceSuggestionVerdict(
    string Outcome,
    string Message,
    IReadOnlyList<BalanceCorrectionSuggestionItem> Suggestions,
    IReadOnlyList<string> FailedChecksBefore,
    IReadOnlyList<string> FailedChecksAfter,
    int Proposed,
    int DroppedOutside,
    int DroppedInvalid,
    int Unchanged,
    int UnmatchedPrinted,
    int Derived = 0);

/// <summary>
/// Verify, never trust: turns the model's answer into whitelisted corrections, applies them to a COPY of
/// the stored balance through the correction's own Validator/Applier (<see cref="BalanceCorrectionApplier.DryRun"/>:
/// no DB, no trail) and re-runs the SAME integrity checks. Pure and static.
/// </summary>
/// <remarks>
/// <para>
/// Proposals, in order: (a) every printed value that differs from the stored one (basis <c>printed</c>);
/// (b) when net_profit_reconciliation failed and still fails with those, and the model CONFIRMED net_profit
/// and income_tax as stored, a <c>result_before_tax</c> derived from them (basis <c>derived</c>, replacing
/// any printed one): net_profit − income_tax (the signed reading) or net_profit + |income_tax| (the
/// magnitude reading), the reading that fits the stored tax's sign first — a negative tax gives the same
/// figure both ways, a positive one is read as a charge — kept only when it makes net_profit_reconciliation
/// pass without newly failing any check.
/// </para>
/// <para>
/// Outcomes, in order: every previously failed targeted check passes and none newly fails →
/// <c>fixes</c>; fewer targeted checks fail and none newly fails → <c>reduces</c>; nothing changes and the
/// model confirmed every involved figure as stored → <c>document_error</c> (the document itself does not
/// add up); anything else → <c>no_suggestion</c>. "Newly fails" counts every check, not only the
/// targeted ones: a proposal that breaks a sign check is not a fix.
/// </para>
/// </remarks>
public static class BalanceSuggestionVerifier
{
    /// <summary>Longest printed label returned.</summary>
    public const int MaxLabelLength = 80;

    /// <summary>Message of <c>fixes</c>.</summary>
    public const string FixesMessage =
        "Con las cifras sugeridas el balance pasa todos los controles de integridad que fallaban. " +
        "Verificá cada cifra en el documento antes de aplicarla.";

    /// <summary>Message of <c>fixes</c> when a proposal is derived.</summary>
    public const string DerivedFixesMessage =
        "Con el resultado antes de impuestos calculado a partir del resultado del ejercicio y del impuesto a las " +
        "ganancias, el balance pasa todos los controles de integridad que fallaban. Verificá las cifras en el " +
        "documento antes de aplicarla.";

    /// <summary>The closing sentence of every derivation.</summary>
    public const string DerivationReason =
        "El documento no expone un subtotal antes de impuestos que cierre (por ejemplo, el RECPAM figura " +
        "después del impuesto o el subtotal omite partidas).";

    private const string NetProfitPath       = "net_profit";
    private const string IncomeTaxPath       = "income_tax";
    private const string ResultBeforeTaxPath = "result_before_tax";

    /// <summary>
    /// The opening sentence of every message when the stored net_profit_reconciliation recorded a column mix:
    /// the result before tax and the tax as stored are the comparative column's.
    /// </summary>
    public const string ColumnMixSentence =
        "El resultado antes de impuestos y el impuesto a las ganancias se leyeron de la columna del ejercicio anterior.";

    /// <summary>After <see cref="ColumnMixSentence"/> when the current-year figures were found printed and verified.</summary>
    public const string ColumnMixPrintedSentence =
        "Las cifras sugeridas son las del ejercicio actual, tal como están impresas.";

    /// <summary>After <see cref="ColumnMixSentence"/> when they were not: nothing is calculated from the mixed columns.</summary>
    public const string ColumnMixUnresolvedSentence =
        "No se calcula un resultado antes de impuestos con esas cifras: corregí ambas con los importes del " +
        "ejercicio actual tal como figuran en el documento.";

    /// <summary>Message of <c>no_suggestion</c> under a column mix.</summary>
    public const string ColumnMixNoSuggestionMessage = ColumnMixSentence + " " + ColumnMixUnresolvedSentence;

    /// <summary>Message of <c>no_suggestion</c>.</summary>
    public const string NoSuggestionMessage =
        "No se encontró una corrección que haga cerrar el balance. Revisá las cifras en el documento.";

    /// <summary>Verifies <paramref name="answer"/> against <paramref name="plan"/>.</summary>
    /// <param name="text">
    /// The document's text layer, when the caller has it: every kept proposal is then also looked for where it
    /// is PRINTED (<see cref="BalanceCorrectionSuggestionItem.TextMatch"/>). Null leaves every text match null.
    /// </param>
    public static BalanceSuggestionVerdict Verify(
        BalanceSuggestionPlan plan,
        BalanceSuggestionAnswer answer,
        IReadOnlyList<RenderedImage> images,
        IBalanceIntegrityService integrity,
        BalanceSuggestionPageText? text = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(integrity);

        var tally     = new Tally();
        var proposals = new List<Proposal>();

        // ── Top-level figures ─────────────────────────────────────────────────────
        var askedFields    = plan.Question.Fields.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var answeredFields = new HashSet<string>(StringComparer.Ordinal);
        var answers        = new Dictionary<string, BalanceSuggestionFieldAnswer>(StringComparer.Ordinal);
        var confirmed      = new HashSet<string>(StringComparer.Ordinal);   // printed = stored, not denied
        foreach (var field in answer.Fields ?? [])
        {
            if (field is null)
                continue;

            var path = BalanceCorrectionPath.TryParse(field.Path);
            if (path is null || path.IsItem || !askedFields.Contains(path.Raw))
            {
                tally.Outside++;
                continue;
            }

            if (!answeredFields.Add(path.Raw))
                continue;   // a second answer for the same figure: the first one counts

            answers[path.Raw] = field;
            if (Consider(path, field, plan, proposals, tally))
                confirmed.Add(path.Raw);
        }

        if (answeredFields.Count < askedFields.Count)
            tally.AllConfirmed = false;

        // ── Rubro lists ───────────────────────────────────────────────────────────
        var groups         = plan.Question.ItemGroups.ToDictionary(g => g.Array, StringComparer.Ordinal);
        var answeredGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var groupAnswer in answer.Items ?? [])
        {
            if (groupAnswer?.Array is not { } array || !groups.TryGetValue(array, out var group))
            {
                tally.Outside++;
                continue;
            }

            if (!answeredGroups.Add(array))
                continue;

            var lines   = groupAnswer.Lines ?? [];
            var claimed = new bool[group.StoredPrinted.Count];
            foreach (var line in lines)
            {
                if (line is null)
                    continue;

                if (line.StoredIndex is not { } index)
                {
                    // A printed line no stored line stands for: the extraction missed it, and no correction
                    // can add a line — never a confirmation that the document is wrong.
                    tally.UnmatchedPrinted++;
                    tally.AllConfirmed = false;
                    continue;
                }

                if (index < 0 || index >= claimed.Length || claimed[index])
                {
                    tally.Outside++;
                    tally.AllConfirmed = false;
                    continue;
                }

                claimed[index] = true;
                _ = Consider(ItemPath(array, index), line, plan, proposals, tally);
            }

            if (lines.Count == 0)
            {
                tally.AllConfirmed = false;
                continue;
            }

            // A stored line no printed line corresponds to is not printed under that rubro: zero (the "–"
            // read as a figure, or a line taken from elsewhere). Proposed without a location.
            for (var i = 0; i < claimed.Length; i++)
            {
                if (claimed[i])
                    continue;

                tally.AllConfirmed = false;
                var path   = ItemPath(array, i);
                var stored = plan.StoredByPath.GetValueOrDefault(path.Raw);
                if (stored is 0d)
                {
                    tally.Unchanged++;
                    continue;
                }

                proposals.Add(new Proposal(path, 0d, stored, null));
            }
        }

        if (answeredGroups.Count < groups.Count)
            tally.AllConfirmed = false;

        // ── Dry run: the stored figures, then the corrected copy, through the same checks ───────
        var before = BalanceCorrectionApplier.DryRun(plan.BalanceJson, [], integrity).Report;
        var after  = proposals.Count == 0 ? before : DryRun(plan, proposals, integrity);

        // ── (c) A recorded column mix: the stored result before tax and tax are the prior year's ───────
        //    A confirmation of them confirms nothing (never document_error), nothing is derived from them, and a
        //    printed current-year figure for either is kept only when it is located on a page and, with every
        //    other proposal, makes net_profit_reconciliation pass.
        var columnMix = plan.NetProfitColumnMix;
        var keptMixed = false;
        if (columnMix)
        {
            tally.AllConfirmed = false;

            var mixed = proposals.Where(p => p.Path.Raw is ResultBeforeTaxPath or IncomeTaxPath).ToList();
            if (mixed.Count > 0)
            {
                var dropped = mixed.Where(p => !IsLocated(p.Source, images)).ToList();
                var kept    = proposals.Where(p => !dropped.Contains(p)).ToList();
                if (mixed.Count > dropped.Count
                    && (DryRun(plan, kept, integrity) is not { } trial
                        || !Passes(trial, BalanceIntegrityService.NetProfitReconciliationCheck)))
                {
                    dropped = mixed;
                    kept    = proposals.Where(p => !mixed.Contains(p)).ToList();
                }

                tally.Invalid += dropped.Count;
                keptMixed      = mixed.Count > dropped.Count;
                proposals      = kept;
                after          = proposals.Count == 0 ? before : DryRun(plan, proposals, integrity);
            }
        }

        // ── (b) A result_before_tax derived from the confirmed net result and tax ───────────────
        var derived = 0;
        if (!columnMix
            && before is not null && after is not null
            && Fails(before, BalanceIntegrityService.NetProfitReconciliationCheck)
            && Fails(after, BalanceIntegrityService.NetProfitReconciliationCheck)
            && Derive(plan, answers, confirmed, proposals, before, integrity) is { } derivation)
        {
            proposals = derivation.Proposals;
            after     = derivation.Report;
            derived   = 1;
        }

        var failedBefore = FailedTargeted(before);
        var failedAfter  = FailedTargeted(after);

        // Under a column mix every message opens by saying so, and says whether the current-year figures were found.
        string Say(string message) =>
            !columnMix ? message
            : keptMixed ? ColumnMixSentence + " " + ColumnMixPrintedSentence + " " + message
            : message == NoSuggestionMessage ? ColumnMixNoSuggestionMessage
            : ColumnMixNoSuggestionMessage + " " + message;

        BalanceSuggestionVerdict Verdict(string outcome, string message, IReadOnlyList<BalanceCorrectionSuggestionItem> suggestions) =>
            new(outcome, Say(message), suggestions, failedBefore, failedAfter,
                proposals.Count, tally.Outside, tally.Invalid, tally.Unchanged, tally.UnmatchedPrinted,
                suggestions.Count(i => i.Basis == BalanceSuggestionBases.Derived));

        if (before is null || after is null || failedBefore.Count == 0)
            return Verdict(BalanceSuggestionOutcomes.NoSuggestion, NoSuggestionMessage, []);

        if (proposals.Count == 0)
        {
            return tally.AllConfirmed
                ? Verdict(BalanceSuggestionOutcomes.DocumentError, DocumentErrorMessage(failedBefore), [])
                : Verdict(BalanceSuggestionOutcomes.NoSuggestion, NoSuggestionMessage, []);
        }

        var failedBeforeAll = FailedNames(before);
        var newlyFailed     = FailedNames(after).Where(n => !failedBeforeAll.Contains(n)).ToList();
        if (newlyFailed.Count > 0)
            return Verdict(BalanceSuggestionOutcomes.NoSuggestion, NoSuggestionMessage, []);

        var resolved = failedBefore.Count(name => Passes(after, name));

        if (resolved == failedBefore.Count)
        {
            return Verdict(BalanceSuggestionOutcomes.Fixes, derived > 0 ? DerivedFixesMessage : FixesMessage,
                Items(proposals, images, plan.Scale, text));
        }

        if (failedAfter.Count < failedBefore.Count)
        {
            return Verdict(BalanceSuggestionOutcomes.Reduces, ReducesMessage(resolved, failedBefore.Count),
                Items(proposals, images, plan.Scale, text));
        }

        return Verdict(BalanceSuggestionOutcomes.NoSuggestion, NoSuggestionMessage, []);
    }

    /// <summary>
    /// The es-AR sentence of a derived result_before_tax: the two figures it was calculated from (pesos, as
    /// stored) and why no printed line was used. Returned to the Portal only, never logged.
    /// </summary>
    /// <remarks>
    /// The operator word carries the tax's sign and the tax is always quoted as a magnitude, so a sign is never
    /// doubled ("menos … (−2.330.167,58)" read as a minus before a negative figure). The signed reading computes
    /// net − tax: a charge stored negative is ADDED back ("más … de 2.330.167,58"), a benefit stored positive is
    /// taken off ("menos … de X"). The magnitude reading always adds it. The net result keeps its own sign — the
    /// only signed figure in the sentence.
    /// </remarks>
    public static string DerivationText(double netProfit, double incomeTax, bool magnitude)
    {
        var tax = Amount(Math.Abs(incomeTax));
        var step = magnitude
            ? "más el impuesto a las ganancias, informado como importe positivo, de " + tax
            : incomeTax < 0d
                ? "más el impuesto a las ganancias de " + tax
                : "menos el impuesto a las ganancias de " + tax;

        return "Calculado: resultado del ejercicio de " + Amount(netProfit) + " " + step + ". " + DerivationReason;
    }

    /// <summary>An amount in es-AR: '.' for thousands, ',' and two decimals, a leading '−' when negative.</summary>
    public static string Amount(double value)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        var body = Math.Abs(rounded).ToString("N2", CultureInfo.InvariantCulture)
            .Replace(',', '\u0001').Replace('.', ',').Replace('\u0001', '.');
        return rounded < 0 ? "\u2212" + body : body;
    }

    /// <summary>"El balance presentado no cierra: …", naming every failed check without a figure.</summary>
    public static string DocumentErrorMessage(IReadOnlyList<string> failedChecks)
    {
        var phrases = failedChecks
            .Select(c => BalanceSuggestionLabels.CheckPhrases.TryGetValue(c, out var phrase) ? phrase : c)
            .ToList();

        return "El balance presentado no cierra: " + string.Join("; ", phrases) +
               ". Las cifras extraídas coinciden con las impresas en el documento, así que no hay una corrección que sugerir.";
    }

    private static string ReducesMessage(int resolved, int failed) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Con las cifras sugeridas el balance pasa {resolved} de los {failed} controles de integridad que fallaban; ")
        + "los demás siguen sin cerrar. Verificá cada cifra en el documento antes de aplicarla.";

    // One answer → a proposal, a confirmation, or a drop. True when it CONFIRMS the stored figure: the
    // printed value equals it and the model did not say otherwise.
    private static bool Consider(
        BalanceCorrectionPath path, BalanceSuggestionLocatedAnswer located, BalanceSuggestionPlan plan,
        List<Proposal> proposals, Tally tally)
    {
        if (located.PrintedValue is not { } printed)
        {
            tally.AllConfirmed = false;   // not found: nothing to propose, nothing confirmed
            return false;
        }

        var value = Math.Round(printed * plan.Scale, 2, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(value) || Math.Abs(value) > BalanceCorrectionValidator.MaxAbsoluteValue)
        {
            tally.Invalid++;
            tally.AllConfirmed = false;
            return false;
        }

        if (value == 0d)
            value = 0d;   // -0 and 0 are the same figure

        var stored = plan.StoredByPath.GetValueOrDefault(path.Raw);
        if (stored is { } s && Math.Round(s, 2, MidpointRounding.AwayFromZero) == value)
        {
            tally.Unchanged++;
            if (located.MatchesStored == false)
            {
                tally.AllConfirmed = false;
                return false;
            }

            return true;
        }

        tally.AllConfirmed = false;
        proposals.Add(new Proposal(path, value, stored, located));
        return false;
    }

    // (b): the first reading of the tax whose result_before_tax makes net_profit_reconciliation pass with the
    // printed proposals (minus any printed result_before_tax) and newly fails nothing; null when the net
    // result or the tax is not confirmed, the figure is not correctable, or no reading qualifies.
    private static (List<Proposal> Proposals, BalanceIntegrityReport Report)? Derive(
        BalanceSuggestionPlan plan,
        IReadOnlyDictionary<string, BalanceSuggestionFieldAnswer> answers,
        IReadOnlySet<string> confirmed,
        IReadOnlyList<Proposal> printedProposals,
        BalanceIntegrityReport before,
        IBalanceIntegrityService integrity)
    {
        if (!confirmed.Contains(NetProfitPath) || !confirmed.Contains(IncomeTaxPath)
            || plan.StoredByPath.GetValueOrDefault(NetProfitPath) is not { } netProfit
            || plan.StoredByPath.GetValueOrDefault(IncomeTaxPath) is not { } incomeTax
            || !plan.StoredByPath.TryGetValue(ResultBeforeTaxPath, out var storedRbt)
            || BalanceCorrectionPath.TryParse(ResultBeforeTaxPath) is not { } rbtPath)
        {
            return null;
        }

        var kept            = printedProposals.Where(p => p.Path.Raw != ResultBeforeTaxPath).ToList();
        var failedBeforeAll = FailedNames(before);

        // The reading that fits the stored sign first: a charge printed negative is the signed reading, one
        // stored as a positive magnitude the magnitude reading (for a negative tax both are the same figure).
        bool[] readings = incomeTax > 0d ? [true, false] : [false, true];
        foreach (var magnitude in readings)
        {
            var value = Math.Round(
                magnitude ? netProfit + Math.Abs(incomeTax) : netProfit - incomeTax, 2, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(value) || Math.Abs(value) > BalanceCorrectionValidator.MaxAbsoluteValue)
                continue;

            if (value == 0d)
                value = 0d;

            if (storedRbt is { } s && Math.Round(s, 2, MidpointRounding.AwayFromZero) == value)
                continue;   // the stored figure itself: it already failed

            var trial = new List<Proposal>(kept)
            {
                new(rbtPath, value, storedRbt, answers.GetValueOrDefault(ResultBeforeTaxPath),
                    BalanceSuggestionBases.Derived,
                    DerivationText(netProfit, incomeTax, magnitude),
                    answers[NetProfitPath], answers[IncomeTaxPath],
                    netProfit, incomeTax),
            };

            if (DryRun(plan, trial, integrity) is not { } report
                || !Passes(report, BalanceIntegrityService.NetProfitReconciliationCheck)
                || FailedNames(report).Any(n => !failedBeforeAll.Contains(n)))
            {
                continue;
            }

            return (trial, report);
        }

        return null;
    }

    private static IReadOnlyList<BalanceCorrectionSuggestionItem> Items(
        IReadOnlyList<Proposal> proposals, IReadOnlyList<RenderedImage> images, double scale,
        BalanceSuggestionPageText? text)
    {
        var items = new List<BalanceCorrectionSuggestionItem>(proposals.Count);
        foreach (var proposal in proposals)
        {
            var isDerived = proposal.Basis == BalanceSuggestionBases.Derived;

            // A printed figure is shown where it is printed; a derived one where the net result is printed.
            var primary   = Locate(isDerived ? proposal.Primary : proposal.Source, images);
            var secondary = isDerived ? Locate(proposal.Secondary, images) : default;

            // Where the verified figure is PRINTED, in printed units: the figure itself, or for a derived one the
            // stored net result and tax it was calculated from — each on the page the model read it from.
            var textMatch = isDerived
                ? Find(text, primary.Page, proposal.PrimaryValue, proposal.Primary?.Label, scale, images)
                : Find(text, primary.Page, proposal.Value, proposal.Source?.Label, scale, images);
            var textMatchSecondary = isDerived
                ? Find(text, secondary.Page, proposal.SecondaryValue, proposal.Secondary?.Label, scale, images)
                : null;

            items.Add(new BalanceCorrectionSuggestionItem
            {
                Path              = proposal.Path.Raw,
                CurrentValue      = proposal.Stored,
                SuggestedValue    = proposal.Value,
                PrintedLabel      = Label(proposal.Source?.Label),
                Page              = primary.Page,
                Evidence          = primary.Evidence,
                Highlight         = primary.Highlight,
                Basis             = proposal.Basis,
                Derivation        = proposal.Derivation,
                EvidenceSecondary = secondary.Evidence,
                SecondaryPage     = secondary.Page,
                TextMatch          = textMatch,
                TextMatchSecondary = textMatchSecondary,
            });
        }

        return items;
    }

    // Where an answer is printed: its page, crop and highlight, or nothing when the location is unusable.
    private static Location Locate(BalanceSuggestionLocatedAnswer? located, IReadOnlyList<RenderedImage> images)
    {
        var image = located?.Image is { } number ? images.FirstOrDefault(i => i.ImageNumber == number) : null;

        // The model's half must be the one our own image map says that image is; otherwise it was
        // describing some other image and its band means nothing here.
        if (image is null
            || (located!.Half is not null && !string.Equals(located.Half, image.Half, StringComparison.OrdinalIgnoreCase)))
        {
            return default;
        }

        if (located.Band is not { Count: 2 } band)
            return new Location(image.Page, null, null);

        return new Location(
            image.Page,
            BalanceSuggestionEvidence.Crop(image, band[0], band[1]),
            BalanceSuggestionEvidence.Highlight(image, images, band[0], band[1]));
    }

    // The text match of a figure in pesos on a page, or null without a text layer, a page or a usable scale.
    private static BalanceSuggestionTextMatch? Find(
        BalanceSuggestionPageText? text, int? page, double? pesos, string? label, double scale,
        IReadOnlyList<RenderedImage> images)
    {
        if (text is null || page is not { } p || pesos is not { } value || !double.IsFinite(scale) || scale <= 0d)
            return null;

        return text.Find(p, value / scale, label, BalanceSuggestionEvidence.FullPage(images, p));
    }

    private static string? Label(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var trimmed = label.Trim();
        return trimmed.Length <= MaxLabelLength ? trimmed : trimmed[..MaxLabelLength];
    }

    // A printed figure with a usable location: an image the reply carries and a two-edged band on it.
    private static bool IsLocated(BalanceSuggestionLocatedAnswer? source, IReadOnlyList<RenderedImage> images) =>
        source?.Image is { } number
        && source.Band is { Count: 2 }
        && images.Any(i => i.ImageNumber == number);

    private static BalanceCorrectionPath ItemPath(string array, int index)
        => BalanceCorrectionPath.TryParse(string.Create(CultureInfo.InvariantCulture, $"{array}[{index}].amount"))!;

    private static HashSet<string> FailedNames(BalanceIntegrityReport report)
        => report.Checks.Where(c => !c.Passed).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    // Distinct failed targeted checks, in report order.
    private static IReadOnlyList<string> FailedTargeted(BalanceIntegrityReport? report) =>
        report is null
            ? []
            : report.Checks
                .Where(c => !c.Passed && BalanceSuggestionPlanner.TargetedChecks.Contains(c.Name))
                .Select(c => c.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static bool Passes(BalanceIntegrityReport report, string name) =>
        report.Checks.Any(c => c.Name == name) && report.Checks.Where(c => c.Name == name).All(c => c.Passed);

    private static bool Fails(BalanceIntegrityReport report, string name) =>
        report.Checks.Any(c => c.Name == name && !c.Passed);

    private static BalanceIntegrityReport? DryRun(
        BalanceSuggestionPlan plan, IReadOnlyList<Proposal> proposals, IBalanceIntegrityService integrity) =>
        BalanceCorrectionApplier.DryRun(
            plan.BalanceJson, proposals.Select(p => new BalanceCorrectionEntry(p.Path, p.Value)).ToList(), integrity).Report;

    /// <param name="Source">The answer for the figure itself (a derived one's printed line, when the model found one).</param>
    /// <param name="Primary">A derived figure's first input (net_profit): its evidence and highlight.</param>
    /// <param name="Secondary">A derived figure's second input (income_tax): its secondary evidence.</param>
    /// <param name="PrimaryValue">A derived figure's first input as stored (pesos): what its text match looks for.</param>
    /// <param name="SecondaryValue">A derived figure's second input as stored (pesos).</param>
    private sealed record Proposal(
        BalanceCorrectionPath Path,
        double Value,
        double? Stored,
        BalanceSuggestionLocatedAnswer? Source,
        string Basis = BalanceSuggestionBases.Printed,
        string? Derivation = null,
        BalanceSuggestionLocatedAnswer? Primary = null,
        BalanceSuggestionLocatedAnswer? Secondary = null,
        double? PrimaryValue = null,
        double? SecondaryValue = null);

    private readonly record struct Location(
        int? Page, BalanceSuggestionEvidenceResponse? Evidence, BalanceSuggestionHighlight? Highlight);

    private sealed class Tally
    {
        public int  Outside          { get; set; }
        public int  Invalid          { get; set; }
        public int  Unchanged        { get; set; }
        public int  UnmatchedPrinted { get; set; }
        public bool AllConfirmed     { get; set; } = true;
    }
}
