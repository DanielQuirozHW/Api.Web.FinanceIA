// Project : Api.Web.FinanceIA
// Created : 2026-11-05

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.Privacy;

/// <summary>
/// Replaces the evaluated company's identity — its names, its CUIT, the names and CUITs of its partners
/// and authorities — with neutral phrases before a text reaches the Claude API. Pure: no DI, no logging,
/// no I/O. The project rule it enforces is that the model analyses figures, never identities; the
/// analysis chat is where model-written free text (the executive summary read off the PDF, earlier
/// answers, the analyst's own questions) would otherwise carry the razón social back to the model.
/// </summary>
/// <remarks>
/// <para><b>Folding.</b> Matching runs on a folded copy of the text — canonical decomposition, combining
/// marks dropped, lower-cased invariantly (á→a, ñ→n, Ü→u) — with an index map back to the original, and
/// replacements are spliced into the ORIGINAL by span. So matching is case- and accent-insensitive while
/// every character outside a match stays byte-identical.</para>
///
/// <para><b>Company names.</b> A name is tokenised (any non-letter/digit splits; <c>&amp;</c> is the word
/// <c>y</c>), its trailing legal form (S.R.L., S.A., S.A.S., S.A.U., S.A.I.C., Coop., Ltda., "y Cía.",
/// "Sociedad Anónima"…) is stripped, and what remains is the CORE. The core matches only whole, only as
/// whole words, with 1-3 separator characters between its tokens, optionally followed by ANY legal form
/// written with or without dots and spaces. A fragment of the core never matches. A core shorter than
/// <see cref="MinBareCoreLength"/> letters/digits matches only together with a legal form.</para>
///
/// <para><b>Person names.</b> A natural person's name (LUFE writes "APELLIDO NOMBRES") matches as a
/// contiguous run of at least two DISTINCT of its own tokens, in any order, commas and connectors (de,
/// del, la…) allowed between them — "Juan Pérez", "Pérez, Juan Carlos" — never one token alone.</para>
///
/// <para><b>CUITs.</b> A known CUIT matches with or without one separator (dash, en dash, space, dot)
/// at each of its two group boundaries; any OTHER CUIT-shaped token matches only in its dashed form, so
/// an eleven-digit amount in the prompt's JSON is never taken for a CUIT. A digit run that continues
/// into a decimal part is never cut.</para>
///
/// <para><b>Never throws.</b> <see cref="TryCreate"/> and <see cref="TryRedact"/> catch everything —
/// including <see cref="RegexMatchTimeoutException"/> — and answer false, so the caller can fail CLOSED.
/// Neither the exception nor the text is surfaced: a timeout carries both the input and the pattern,
/// and the pattern IS the company's name.</para>
/// </remarks>
public sealed class EntityIdentifierRedactor
{
    /// <summary>What replaces the evaluated company's name.</summary>
    public const string CompanyPlaceholder = "la empresa";

    /// <summary>What replaces the name of a partner, authority or related legal person.</summary>
    public const string PersonPlaceholder = "[persona vinculada]";

    /// <summary>What replaces a CUIT.</summary>
    public const string CuitPlaceholder = "[CUIT]";

    /// <summary>A core with fewer letters/digits than this matches only together with a legal form.</summary>
    public const int MinBareCoreLength = 3;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    // Between two tokens of a name: 1-3 of space, dot, comma, apostrophe, hyphen or en dash.
    private const string NameSeparator = @"[\s.,'’\-–]{1,3}";

    // Between the core and its legal form.
    private const string SuffixSeparator = @"[\s.,]{1,3}";

    // Between two tokens of a person's name. Dots are not allowed: "Pérez. Juan" is two sentences.
    private const string PersonSeparator = @"[\s,]{1,3}";

    private const string WordStart = @"(?<![\p{L}\p{N}])";
    private const string WordEnd   = @"(?![\p{L}\p{N}])";

    // The digits of a CUIT never touch other digits, and never continue into a decimal part.
    private const string CuitStart = @"(?<!\d)(?<!\d[.,])";
    private const string CuitEnd   = @"(?!\d)(?![.,]\d)";

    private const string CuitGroupSeparator = @"[\-–\s.]?";

    private const string GenericCuit = @"\d{2}[\-–]\d{8}[\-–]\d";

    /// <summary>
    /// Legal forms written as letters (S.R.L. = "srl"). Matched letter by letter with optional dots and
    /// spaces between the letters. Longest first. "se" (sociedad del Estado) is deliberately absent:
    /// "Ejemplo Agro se encuentra…" would read the pronoun as a legal form and swallow it.
    /// </summary>
    private static readonly string[] LetterLegalForms =
    [
        "sacifi", "saicyf", "sapem", "saic", "saci", "sas", "sau", "sca", "srl", "sa", "sc", "sh"
    ];

    /// <summary>Legal forms written as words. Longest first.</summary>
    private static readonly string[] WordLegalForms =
    [
        "sociedad de responsabilidad limitada",
        "sociedad por acciones simplificada",
        "sociedad anonima unipersonal",
        "sociedad en comandita por acciones",
        "sociedad en comandita simple",
        "sociedad colectiva",
        "sociedad anonima",
        "sociedad de hecho",
        "cooperativa limitada",
        "cooperativa",
        "y compania",
        "limitada",
        "y cia",
        "coop",
        "ltda"
    ];

    /// <summary>Words that sit inside a person's name without identifying anyone.</summary>
    private static readonly HashSet<string> NameConnectors =
        new(StringComparer.Ordinal) { "de", "del", "la", "las", "los", "y", "e", "van", "von", "di", "da" };

    private static readonly HashSet<string> LetterLegalFormSet = new(LetterLegalForms, StringComparer.Ordinal);
    private static readonly HashSet<string> WordLegalFormSet   = new(WordLegalForms, StringComparer.Ordinal);

    /// <summary>Any legal form, letters with optional dots and spaces, words with 1-3 separators.</summary>
    private static readonly string LegalFormPattern = BuildLegalFormPattern();

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", Options, MatchTimeout);

    private readonly Regex  _cuitRegex;
    private readonly Regex? _companyRegex;
    private readonly Regex? _personRegex;
    private readonly IReadOnlyList<HashSet<string>> _persons;

    private EntityIdentifierRedactor(
        Regex cuitRegex, Regex? companyRegex, Regex? personRegex, IReadOnlyList<HashSet<string>> persons)
    {
        _cuitRegex    = cuitRegex;
        _companyRegex = companyRegex;
        _personRegex  = personRegex;
        _persons      = persons;
    }

    /// <summary>
    /// Compiles the patterns for one set of identifiers. Blank, duplicate and unusable values are dropped;
    /// an empty set yields a redactor that changes nothing. Answers false — never throws — when the
    /// patterns could not be built.
    /// </summary>
    public static bool TryCreate(EntityIdentifiers identifiers, out EntityIdentifierRedactor? redactor)
    {
        redactor = null;

        try
        {
            ArgumentNullException.ThrowIfNull(identifiers);

            var cuitRegex    = BuildCuitRegex(identifiers.Cuits);
            var companyRegex = BuildCompanyRegex(identifiers.CompanyNames, identifiers.RelatedCompanyNames);
            var persons      = BuildPersons(identifiers.PersonNames);
            var personRegex  = BuildPersonRegex(persons);

            redactor = new EntityIdentifierRedactor(cuitRegex, companyRegex, personRegex, persons);
            return true;
        }
        catch (Exception)
        {
            // Deliberately silent and deliberately broad: the caller fails closed on false, and anything
            // this could say would be about the identifiers themselves.
            redactor = null;
            return false;
        }
    }

    /// <summary>
    /// Redacts <paramref name="text"/>. With nothing to replace, <paramref name="redacted"/> is the SAME
    /// instance as <paramref name="text"/> (null and empty come back as the empty string). Answers false —
    /// never throws — on any failure, a match timeout included; <paramref name="redacted"/> is then empty
    /// and must not be used.
    /// </summary>
    public bool TryRedact(string? text, out string redacted, out RedactionCounts counts)
    {
        counts = RedactionCounts.None;

        if (string.IsNullOrEmpty(text))
        {
            redacted = text ?? string.Empty;
            return true;
        }

        try
        {
            var current = text;
            var cuits   = 0;
            var names   = 0;
            var persons = 0;

            // CUITs first: they are digits and cannot interact with a name. Then companies, whose cores
            // are longer than any single person token. Persons last.
            current = ReplaceCuits(current, _cuitRegex, ref cuits);

            if (_companyRegex is not null)
                current = ReplaceCompanies(current, _companyRegex, ref names, ref persons);

            if (_personRegex is not null)
                current = ReplacePersons(current, _personRegex, _persons, ref persons);

            counts   = new RedactionCounts(names, persons, cuits);
            redacted = current;
            return true;
        }
        catch (Exception)
        {
            // RegexMatchTimeoutException carries Input (the text) and Pattern (the name): neither leaves.
            counts   = RedactionCounts.None;
            redacted = string.Empty;
            return false;
        }
    }

    // ── Passes ────────────────────────────────────────────────────────────────────

    private static string ReplaceCuits(string text, Regex regex, ref int count)
    {
        var folded = Fold(text);
        var spans  = new List<Replacement>();

        foreach (Match match in regex.Matches(folded.Text))
            spans.Add(new Replacement(match.Index, match.Index + match.Length, CuitPlaceholder));

        count += spans.Count;
        return Apply(text, folded, spans);
    }

    private static string ReplaceCompanies(string text, Regex regex, ref int names, ref int persons)
    {
        var folded = Fold(text);
        var spans  = new List<Replacement>();

        foreach (Match match in regex.Matches(folded.Text))
        {
            var isEntity = match.Groups["e"].Success;
            var start    = match.Index;
            var end      = match.Index + match.Length;

            var originalStart = folded.Map[start];
            var originalEnd   = folded.OriginalEnd(end, text.Length);

            // "… Ejemplo Agro S.R.L. es …": the last dot belongs to the abbreviation, and a lower-case word
            // after it says it did not also end the sentence. Before an upper-case word, a line break or the
            // end of the text it did both, so a period survives the replacement.
            var keepPeriod = text[originalEnd - 1] == '.' && EndsSentence(text, originalEnd);

            string replacement;
            if (isEntity)
            {
                replacement = StartsSentence(text, originalStart) ? "La empresa" : CompanyPlaceholder;
                names++;
            }
            else
            {
                replacement = PersonPlaceholder;
                persons++;
            }

            spans.Add(new Replacement(start, end, keepPeriod ? replacement + "." : replacement));
        }

        return Apply(text, folded, spans);
    }

    private static string ReplacePersons(
        string text, Regex regex, IReadOnlyList<HashSet<string>> persons, ref int count)
    {
        var folded = Fold(text);
        var spans  = new List<Replacement>();

        foreach (Match match in regex.Matches(folded.Text))
        {
            // The run may hold several people side by side ("Juan Pérez, Ana Gómez"), or a lone token of
            // one. Walk it left to right: at each token take the longest contiguous sub-run of at least two
            // DISTINCT tokens of ONE person, replace it, and continue after it.
            var tokens = new List<(int Start, int End, string Value)>();
            foreach (Match token in TokenRegex.Matches(match.Value))
            {
                if (NameConnectors.Contains(token.Value)) continue;
                tokens.Add((match.Index + token.Index, match.Index + token.Index + token.Length, token.Value));
            }

            var from = 0;
            while (from < tokens.Count)
            {
                var to = LongestPersonRunFrom(tokens, from, persons);
                if (to < 0)
                {
                    from++;
                    continue;
                }

                spans.Add(new Replacement(tokens[from].Start, tokens[to].End, PersonPlaceholder));
                count++;
                from = to + 1;
            }
        }

        return Apply(text, folded, spans);
    }

    /// <summary>
    /// The last index of the longest run starting at <paramref name="from"/> made of at least two distinct
    /// tokens of one person, or -1 when no person has two of their tokens there.
    /// </summary>
    private static int LongestPersonRunFrom(
        IReadOnlyList<(int Start, int End, string Value)> tokens, int from, IReadOnlyList<HashSet<string>> persons)
    {
        var best = -1;

        foreach (var person in persons)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var to   = from - 1;

            while (to + 1 < tokens.Count && person.Contains(tokens[to + 1].Value) && seen.Add(tokens[to + 1].Value))
                to++;

            if (seen.Count >= 2 && to > best)
                best = to;
        }

        return best;
    }

    // ── Pattern construction ──────────────────────────────────────────────────────

    private static Regex BuildCuitRegex(IReadOnlyList<string> cuits)
    {
        var alternatives = cuits
            .Select(Digits)
            .Where(d => d is not null)
            .Distinct(StringComparer.Ordinal)
            .Select(d => $"{d![..2]}{CuitGroupSeparator}{d[2..10]}{CuitGroupSeparator}{d[10]}")
            .Append(GenericCuit)
            .ToList();

        // The generic dashed form is always on: a fiador's or a supplier's CUIT is PII as much as the
        // company's own, and the dashed shape cannot be an amount.
        return new Regex($"{CuitStart}(?:{string.Join("|", alternatives)}){CuitEnd}", Options, MatchTimeout);
    }

    private static Regex? BuildCompanyRegex(IReadOnlyList<string> entityNames, IReadOnlyList<string> relatedNames)
    {
        var alternatives = new List<(int Length, string Pattern)>();
        var seen         = new HashSet<string>(StringComparer.Ordinal);

        void AddAll(IEnumerable<string> names, string group)
        {
            foreach (var name in names)
            {
                if (BuildCompanyAlternative(name) is not { } alternative) continue;
                if (!seen.Add(alternative.Pattern)) continue;

                alternatives.Add((alternative.Length, $"(?<{group}>{alternative.Pattern})"));
            }
        }

        // The evaluated company first: a related legal person spelled exactly like it is the company.
        AddAll(entityNames, "e");
        AddAll(relatedNames, "r");

        if (alternatives.Count == 0) return null;

        // Longest core first, so a razón social that contains another listed name is consumed whole.
        var ordered = alternatives.OrderByDescending(a => a.Length).Select(a => a.Pattern);

        return new Regex($"{WordStart}(?:{string.Join("|", ordered)}){WordEnd}", Options, MatchTimeout);
    }

    private static (int Length, string Pattern)? BuildCompanyAlternative(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var tokens = Tokenize(name);
        var core   = StripLegalForms(tokens);
        if (core.Count == 0) return null;

        var coreLength  = core.Sum(t => t.Length);
        var corePattern = string.Join(NameSeparator, core.Select(TokenPattern));
        var suffix      = $"{SuffixSeparator}{LegalFormPattern}";

        // Too short to stand alone ("AB S.A."): only together with its legal form.
        var pattern = coreLength < MinBareCoreLength
            ? $"{corePattern}{suffix}"
            : $"{corePattern}(?:{suffix})?";

        return (coreLength, pattern);
    }

    private static List<HashSet<string>> BuildPersons(IReadOnlyList<string> personNames)
    {
        var persons = new List<HashSet<string>>();

        foreach (var name in personNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var tokens = Tokenize(name)
                .Where(t => t.Length >= 2 && !NameConnectors.Contains(t))
                .ToHashSet(StringComparer.Ordinal);

            // One token alone would replace a common surname everywhere; that is not this person.
            if (tokens.Count < 2) continue;

            if (persons.Any(p => p.SetEquals(tokens))) continue;
            persons.Add(tokens);
        }

        return persons;
    }

    private static Regex? BuildPersonRegex(IReadOnlyList<HashSet<string>> persons)
    {
        if (persons.Count == 0) return null;

        var tokens = string.Join("|", persons
            .SelectMany(p => p)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(t => t.Length)
            .Select(Regex.Escape));

        var connectors = string.Join("|", NameConnectors.Select(Regex.Escape));

        // A run of name tokens, connectors allowed BETWEEN two of them, never at either end.
        var pattern =
            $"{WordStart}(?:{tokens})" +
            $"(?:{PersonSeparator}(?:(?:{connectors}){PersonSeparator})*(?:{tokens}))*{WordEnd}";

        return new Regex(pattern, Options, MatchTimeout);
    }

    private static string TokenPattern(string token)
        => token == "y" ? "(?:y|&)" : Regex.Escape(token);

    private static string BuildLegalFormPattern()
    {
        var letters = LetterLegalForms.Select(form =>
            string.Join(@"[.\s]*", form.Select(c => Regex.Escape(c.ToString()))) + @"\.?");

        var words = WordLegalForms.Select(form =>
            string.Join(SuffixSeparator, form.Split(' ').Select(TokenPattern)) + @"\.?");

        return $"(?:{string.Join("|", words.Concat(letters))})";
    }

    /// <summary>The folded tokens of a name, <c>&amp;</c> read as <c>y</c>.</summary>
    private static List<string> Tokenize(string name)
    {
        var folded = Fold(name.Replace("&", " y ", StringComparison.Ordinal)).Text;
        return TokenRegex.Matches(folded).Select(m => m.Value).ToList();
    }

    /// <summary>Drops a trailing legal form, longest first, at most twice ("Coop. … Ltda.", "… y Cía. S.R.L.").</summary>
    private static List<string> StripLegalForms(List<string> tokens)
    {
        var core = new List<string>(tokens);

        for (var round = 0; round < 2; round++)
        {
            var stripped = false;

            for (var take = Math.Min(6, core.Count - 1); take >= 1; take--)
            {
                var tail = core.GetRange(core.Count - take, take);

                if (LetterLegalFormSet.Contains(string.Concat(tail)) || WordLegalFormSet.Contains(string.Join(' ', tail)))
                {
                    core.RemoveRange(core.Count - take, take);
                    stripped = true;
                    break;
                }
            }

            if (!stripped) break;
        }

        return core;
    }

    private static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 11 ? digits : null;
    }

    // ── Sentence position ─────────────────────────────────────────────────────────

    private static bool StartsSentence(string text, int start)
    {
        var i = start - 1;
        var skippedSpace = false;

        while (i >= 0 && (text[i] is ' ' or '\t' or '*' or '_' or '#' or '>' or '"' or '«' or '“' or '(' or '-' or '•' or '¿' or '¡'))
        {
            skippedSpace |= text[i] is ' ' or '\t';
            i--;
        }

        if (i < 0) return true;
        if (text[i] is '\n' or '\r') return true;

        return skippedSpace && text[i] is '.' or '!' or '?';
    }

    private static bool EndsSentence(string text, int end)
    {
        if (end >= text.Length) return true;
        if (text[end] is not (' ' or '\t')) return false;

        // Same line only: before a line break the break already separates, and a heading ("# Análisis de
        // Ejemplo S.A.") has no period to keep.
        var i = end;
        while (i < text.Length && text[i] is ' ' or '\t') i++;

        return i >= text.Length || char.IsUpper(text[i]);
    }

    // ── Folding ───────────────────────────────────────────────────────────────────

    private readonly record struct Replacement(int FoldedStart, int FoldedEnd, string Text);

    private sealed class FoldedText(string text, int[] map)
    {
        public string Text { get; } = text;

        /// <summary>For each folded character, the index of the original character it came from.</summary>
        public int[] Map { get; } = map;

        /// <summary>The original index where a folded span ending at <paramref name="foldedEnd"/> ends.</summary>
        public int OriginalEnd(int foldedEnd, int originalLength)
            => foldedEnd < Map.Length ? Map[foldedEnd] : originalLength;
    }

    private static FoldedText Fold(string text)
    {
        var sb  = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c < 0x80)
            {
                sb.Append(char.ToLowerInvariant(c));
                map.Add(i);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                sb.Append(c);
                map.Add(i);
                continue;
            }

            // A combining mark in the text itself (a decomposed "á") belongs to the letter before it.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var appended   = false;

            foreach (var d in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark) continue;

                sb.Append(char.ToLowerInvariant(d));
                map.Add(i);
                appended = true;
            }

            if (!appended)
            {
                sb.Append(c);
                map.Add(i);
            }
        }

        return new FoldedText(sb.ToString(), map.ToArray());
    }

    private static string Apply(string original, FoldedText folded, List<Replacement> spans)
    {
        if (spans.Count == 0) return original;

        var sb   = new StringBuilder(original.Length);
        var last = 0;

        foreach (var span in spans)
        {
            var start = folded.Map[span.FoldedStart];
            var end   = folded.OriginalEnd(span.FoldedEnd, original.Length);

            sb.Append(original, last, start - last);
            sb.Append(span.Text);
            last = end;
        }

        sb.Append(original, last, original.Length - last);
        return sb.ToString();
    }
}
