// Project : Api.Web.FinanceIA
// Created : 2026-08-09

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless, deterministic implementation of <see cref="IAlertRatioReconciliationService"/>.
/// No DI dependencies — can be instantiated directly in tests.
///
/// Context: an alert's <c>description</c> is authored verbatim by the model and stored
/// unchanged, so a percentage may be computed against one denominator (e.g. equity) yet
/// labeled as another (e.g. "del total del pasivo"). This service recomputes the percentage
/// against the denominator the text itself names — using aggregate figures already extracted
/// into <see cref="BalanceAccounts"/> — and, only when the two provably disagree, rewrites the
/// number so it matches its label. Everything else in the sentence is left untouched.
/// </summary>
public sealed class AlertRatioReconciliationService : IAlertRatioReconciliationService
{
    // Short connector that may sit between the "% del <denominador>" clause and a currency amount
    // — either right before the denominator phrase (inside PercentClause) or right after it (the
    // trailing cross-check). Factored out so the two can't drift apart.
    private const string DenominatorConnector = @"(?:de\s+(?:la|los|las|el)\s+|del\s+|de\s+)?";

    // Percentage token immediately followed by a recognized balance-sheet denominator phrase,
    // e.g. "22,6% del total del pasivo". Only the <pct> group is ever rewritten. Ordered
    // most-specific first so "pasivo no corriente" wins over "pasivo".
    private static readonly Regex PercentClause = new(
        @"(?<pct>\d{1,3}(?:[.,]\d+)?)\s*%\s*" + DenominatorConnector +
        @"(?<den>total\s+del\s+pasivo|pasivo\s+total|pasivo\s+no\s+corriente|pasivo\s+corriente|" +
        @"pasivo\s+circulante|pasivo|patrimonio\s+neto|patrimonio|total\s+del\s+activo|" +
        @"activo\s+total|activo\s+no\s+corriente|activo\s+corriente|activo\s+circulante|activo)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The (possibly empty) connector immediately after a clause, anchored at its start — used to
    // test whether a trailing currency amount is CONTIGUOUS with the clause ("del activo total de
    // $12.239.430.766,71"), not merely "appears later in the sentence somewhere".
    private static readonly Regex TrailingConnector = new(
        @"^\s*" + DenominatorConnector, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Currency amount in es-AR notation: a "$"-prefixed number or a number with grouped
    // thousands (so bare integers such as a fiscal year, and the percentage itself, do NOT
    // match). e.g. "$1.670.594.267,95".
    private static readonly Regex CurrencyAmount = new(
        @"\$\s?\d{1,3}(?:\.\d{3})*(?:,\d+)?|\d{1,3}(?:\.\d{3})+(?:,\d+)?",
        RegexOptions.CultureInvariant);

    // A sentence terminator ('.', '!', '?') immediately followed by whitespace — used by
    // ReconcileNarrative to bound a clause's numerator search to its OWN sentence. Never matches
    // inside an es-AR thousands separator ("12.239.430.766,71"): those dots are always followed
    // immediately by another digit, never whitespace, so no special-casing is needed beyond
    // requiring trailing whitespace.
    private static readonly Regex SentenceTerminator = new(
        @"[.!?]\s+", RegexOptions.CultureInvariant);

    // Correct alerts are never rewritten: intervene only on a clear contradiction, using the
    // same dual absolute+relative guard as the ratio divergence check. This is a text-consistency
    // tolerance, NOT a risk threshold.
    private const double AbsTolerancePercentagePoints = 0.5;
    private const double RelTolerance                 = 0.05;

    // ── IAlertRatioReconciliationService ──────────────────────────────────────

    /// <inheritdoc />
    public AlertDescriptionReconciliation Reconcile(string description, BalanceAccounts accounts)
    {
        var unchanged = new AlertDescriptionReconciliation(description, null, false);

        if (string.IsNullOrWhiteSpace(description) || accounts is null)
            return unchanged;

        // Exactly one "% de <denominador>" clause, else it is ambiguous which figure it refers to.
        var clauses = PercentClause.Matches(description);
        if (clauses.Count != 1)
            return unchanged;

        var clause = clauses[0];

        // The numerator is the amount ending closest before the clause. A second amount that names
        // the denominator's own figure right AFTER the clause (the leaked shape: "...del activo
        // total de $X") does not make this ambiguous — it is validated separately below. But two or
        // more amounts BEFORE the clause genuinely are ambiguous (which one is being related to the
        // percentage?), so that gate is preserved exactly as before.
        var amounts = CurrencyAmount.Matches(description);
        var before  = AmountsBefore(amounts, clause);
        if (before.Count != 1)
            return unchanged;   // 0 ⇒ nothing precedes the clause; 2+ ⇒ still ambiguous

        var numerator = before[0];

        var res = ResolveClause(description, clause, numerator, accounts);
        if (!res.Resolvable || !res.NeedsCorrection)
            return unchanged;

        // Contradiction proven: swap ONLY the percentage digits so the value matches its label.
        var pctGroup     = clause.Groups["pct"];
        var correctedTxt = FormatArPercent(res.ExpectedPercent);
        var newDescription =
            description[..pctGroup.Index] + correctedTxt + description[(pctGroup.Index + pctGroup.Length)..];

        var note =
            $"Se corrigió el porcentaje de una alerta: el texto declaraba {clause.Groups["pct"].Value}% del " +
            $"{res.Denominator}, pero {numerator.Value.Trim()} equivale al {correctedTxt}% de ese valor; " +
            "se ajustó el número al denominador indicado.";

        return new AlertDescriptionReconciliation(newDescription, note, true)
        {
            StatedPercent   = res.StatedPercent,
            ExpectedPercent = res.ExpectedPercent,
            Denominator     = res.Denominator,
            Clause          = res.Clause,
        };
    }

    /// <inheritdoc />
    public NarrativeReconciliation ReconcileNarrative(string text, BalanceAccounts accounts)
    {
        var none = new NarrativeReconciliation(text, Array.Empty<NarrativeClauseCorrection>(), Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(text) || accounts is null)
            return none;

        var clauses = PercentClause.Matches(text);
        if (clauses.Count == 0)
            return none;

        var amounts     = CurrencyAmount.Matches(text);
        var edits       = new List<(int Index, int Length, string Replacement)>();
        var corrections = new List<NarrativeClauseCorrection>();
        var unresolved  = new List<string>();

        foreach (Match clause in clauses)
        {
            // Pure proximity, no "exactly one" gate: free-form prose naturally has several amounts
            // scattered across several ratio-mentions, and each clause's own figure is expected to
            // be the nearest one preceding it. (Reconcile's stricter "exactly one before" gate
            // exists specifically to preserve single-alert-sentence semantics that don't apply to
            // multi-clause narrative text — see the class doc.)
            //
            // Bounded to the clause's OWN sentence: a clause with no amount in its own sentence
            // must never reach back into an earlier, unrelated sentence for a numerator (e.g. a
            // sentence about "bienes de cambio" picking up the previous sentence's "activo no
            // corriente" figure just because it happens to be the closest amount in the whole
            // text). No amount in the sentence ⇒ unresolvable ⇒ never guess — but it IS reported,
            // so "never checked" stays distinguishable from "checked and correct".
            var sentenceStart = SentenceStart(text, clause.Index);
            var before = AmountsBefore(amounts, clause, sentenceStart);
            if (before.Count == 0)
            {
                unresolved.Add(clause.Value.Trim());
                continue;   // nothing precedes this clause within its own sentence — never guess
            }

            var numerator = before[^1];
            var res = ResolveClause(text, clause, numerator, accounts);
            if (!res.Resolvable)
            {
                // Denominator phrase unrecognized, or a trailing denominator amount disagreed with
                // the extracted figures beyond tolerance — cannot be corrected NOR confirmed
                // correct, so it is reported the same way as a missing numerator above.
                unresolved.Add(clause.Value.Trim());
                continue;
            }
            if (!res.NeedsCorrection)
                continue;   // successfully checked and already correct — never "unresolved"

            var pctGroup = clause.Groups["pct"];
            edits.Add((pctGroup.Index, pctGroup.Length, FormatArPercent(res.ExpectedPercent)));
            corrections.Add(new NarrativeClauseCorrection(
                res.StatedPercent, res.ExpectedPercent, res.Denominator!, res.Clause!));
        }

        if (edits.Count == 0)
            return new NarrativeReconciliation(text, Array.Empty<NarrativeClauseCorrection>(), unresolved);

        // Apply rightmost-first, all against the ORIGINAL string's indices (collected above before
        // any rewrite), so an earlier edit changing the text's length can never invalidate a later
        // edit's already-computed span.
        var rewritten = text;
        foreach (var edit in edits.OrderByDescending(e => e.Index))
            rewritten = rewritten[..edit.Index] + edit.Replacement + rewritten[(edit.Index + edit.Length)..];

        return new NarrativeReconciliation(rewritten, corrections, unresolved);
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    /// <summary>Outcome of validating one clause against one candidate numerator amount.
    /// <see cref="Resolvable"/> is <c>false</c> when the numerator, percentage, or denominator
    /// could not be parsed/resolved, or a trailing denominator amount disagrees with
    /// <paramref name="accounts"/> beyond tolerance — in every such case the caller must leave the
    /// text untouched, never guess.</summary>
    private readonly record struct ClauseResolution(
        bool    Resolvable,
        bool    NeedsCorrection,
        double  StatedPercent,
        double  ExpectedPercent,
        string? Denominator,
        string? Clause);

    /// <summary>Currency-amount matches whose entire span lies within [<paramref name="lowerBound"/>,
    /// clause.Index), in the order they appear in the text. <paramref name="lowerBound"/> defaults
    /// to 0 (the whole text up to the clause) — <see cref="Reconcile"/> relies on that default and
    /// is unaffected by it; <see cref="ReconcileNarrative"/> passes the clause's own sentence start
    /// so it never reaches into an earlier sentence for a numerator.</summary>
    private static List<Match> AmountsBefore(MatchCollection amounts, Match clause, int lowerBound = 0)
    {
        var before = new List<Match>();
        foreach (Match m in amounts)
        {
            if (m.Index >= lowerBound && m.Index + m.Length <= clause.Index)
                before.Add(m);
        }
        return before;
    }

    /// <summary>
    /// Index where the sentence containing <paramref name="clauseStart"/> begins: the position
    /// right after the closest <see cref="SentenceTerminator"/> match ending at or before
    /// <paramref name="clauseStart"/>, or 0 when the clause is in the text's first sentence.
    /// </summary>
    private static int SentenceStart(string text, int clauseStart)
    {
        var start = 0;
        foreach (Match m in SentenceTerminator.Matches(text))
        {
            if (m.Index + m.Length > clauseStart)
                break;
            start = m.Index + m.Length;
        }
        return start;
    }

    /// <summary>
    /// True when a currency amount is CONTIGUOUS with the end of the clause — separated only by
    /// whitespace and an optional short connector ("de"/"del"/"de la"/...), e.g. "...del activo
    /// total de $12.239.430.766,71." A later, unrelated amount elsewhere in the text does not count.
    /// </summary>
    private static bool TryGetTrailingAmount(string text, int clauseEnd, out double amount)
    {
        amount = 0;
        var remainder      = text[clauseEnd..];
        var connector      = TrailingConnector.Match(remainder);   // always succeeds — all-optional
        var afterConnector = remainder[connector.Length..];
        var amountMatch    = CurrencyAmount.Match(afterConnector);
        return amountMatch.Success && amountMatch.Index == 0
            && TryParseArNumber(amountMatch.Value, out amount);
    }

    /// <summary>
    /// Validates one clause against one chosen numerator amount: parses the stated percentage and
    /// the numerator, resolves the denominator's figure, cross-checks a trailing denominator amount
    /// if present, then compares the stated percentage against what the numerator/denominator pair
    /// actually yields. Shared by <see cref="Reconcile"/> and <see cref="ReconcileNarrative"/> so
    /// their math never drifts apart.
    /// </summary>
    private static ClauseResolution ResolveClause(string text, Match clause, Match numerator, BalanceAccounts accounts)
    {
        if (!TryParseArNumber(clause.Groups["pct"].Value, out var statedPct))
            return default;

        if (!TryParseArNumber(numerator.Value, out var amount))
            return default;

        var denText     = CollapseSpaces(clause.Groups["den"].Value).ToLowerInvariant();
        var denominator = ResolveDenominator(denText, accounts);
        if (denominator is null or <= 0)
            return default;

        // Cross-check: when the sentence itself states the denominator's own figure right after the
        // clause ("...del activo total de $X"), and $X disagrees with what BalanceAccounts holds
        // beyond a 5% relative tolerance, the sentence is already internally inconsistent about
        // which figure is right — do not guess which one to trust, leave it untouched. Reuses
        // RelTolerance: it is dimensionless, unlike AbsTolerancePercentagePoints (percentage
        // points), which has no meaning applied to two currency amounts.
        if (TryGetTrailingAmount(text, clause.Index + clause.Length, out var trailingAmount))
        {
            var denDiff = Math.Abs(trailingAmount - denominator.Value) / Math.Abs(denominator.Value);
            if (denDiff > RelTolerance)
                return default;
        }

        var expectedPct = amount / denominator.Value * 100.0;
        var absDiff = Math.Abs(statedPct - expectedPct);
        var relDiff = expectedPct != 0 ? absDiff / Math.Abs(expectedPct) : absDiff;
        var needsCorrection = !(absDiff <= AbsTolerancePercentagePoints || relDiff <= RelTolerance);

        return new ClauseResolution(true, needsCorrection, statedPct, expectedPct, denText, clause.Value);
    }

    /// <summary>
    /// Maps a normalized (lowercased, single-spaced) denominator phrase to the matching
    /// aggregate already extracted into <see cref="BalanceAccounts"/>. Returns <c>null</c>
    /// when the phrase is not one we hold a figure for.
    /// </summary>
    private static double? ResolveDenominator(string den, BalanceAccounts a) => den switch
    {
        "total del pasivo" or "pasivo total" or "pasivo"    => a.TotalLiabilities,
        "pasivo no corriente"                                => a.NonCurrentLiabilities,
        "pasivo corriente" or "pasivo circulante"            => a.CurrentLiabilities,
        "patrimonio neto" or "patrimonio"                    => a.Equity,
        "total del activo" or "activo total" or "activo"     => a.TotalAssets,
        "activo no corriente"                                => a.NonCurrentAssets,
        "activo corriente" or "activo circulante"            => a.CurrentAssets,
        _                                                    => null,
    };

    /// <summary>
    /// Parses an es-AR number (or currency) without any culture dependency: strips the currency
    /// symbol and spaces, drops "." thousands separators, and turns the "," decimal into ".".
    /// </summary>
    private static bool TryParseArNumber(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Replace("$", string.Empty)
                   .Replace(" ", string.Empty)
                   .Replace(" ", string.Empty)   // non-breaking space
                   .Replace(".", string.Empty)        // thousands separator
                   .Replace(",", ".");                 // decimal separator

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Formats a percentage es-AR with one decimal (e.g. 37.432 → "37,4").</summary>
    private static string FormatArPercent(double pct)
    {
        var rounded = Math.Round(pct, 1, MidpointRounding.AwayFromZero);
        return rounded.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
    }

    private static string CollapseSpaces(string s) =>
        Regex.Replace(s, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
}
