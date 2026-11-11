// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-27

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Locks the person-type rules of <see cref="CuitHelper"/> and the two DDL 069 vocabularies they
/// lean on, <see cref="PersonTypes"/> and <see cref="FiscalRegimes"/>.
///
/// <para>
/// Why this exists: ONE resolver decides whether an entity is a company or a natural person, for
/// both POST /api/entities/lookup-arca and POST /api/entities, and its answer lands in a column
/// guarded by CK_Entities_PersonType and CK_Entities_PersonLegajo_Humana. A wrong answer is silent.
/// The create still succeeds, but the entity is the wrong kind: a persona humana gets no legajo, or
/// a company gets one, and nothing downstream complains. The precedence rule is covered
/// exhaustively, over every prefix class × every kind of ARCA answer × every request value. An
/// unambiguous CUIT prefix beats ARCA, ARCA beats the request, and Juridica is the floor.
/// </para>
///
/// <para>
/// The Warning a disagreement logs is asserted on two counts. Its NUMBER: one line per dissenting
/// source, and silence when ARCA said nothing usable or the prefix decides nothing (50). Its
/// CONTENT: for a persona humana the CUIT is a CUIL, which is personal data (its body is the DNI),
/// so neither the rendered message nor any structured property may carry it.
/// </para>
/// </summary>
public sealed class CuitHelperTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────
    // Literals rather than PersonTypes.*: the stored value must be exactly what
    // CK_Entities_PersonType admits, so a typo in a constant has to fail here, not be echoed.
    private const string Humana   = "Humana";
    private const string Juridica = "Juridica";

    // Synthetic only. The check digit is not validated by the helper, and is not meant to be valid.
    private const string Body       = "11111111";
    private const string CheckDigit = "2";

    private static readonly (string Prefix, string? Type)[] PrefixClasses =
    [
        ("20", Humana), ("23", Humana), ("24", Humana), ("27", Humana),
        ("30", Juridica), ("33", Juridica), ("34", Juridica),
        // Foreign subjects and anything else: the prefix decides nothing.
        ("50", null), ("51", null), ("55", null), ("99", null),
    ];

    // The resolution matrix: one representative of each prefix outcome the plan names.
    private static readonly (string Prefix, string? Type)[] MatrixPrefixes =
    [
        ("20", Humana), ("23", Humana), ("24", Humana), ("27", Humana),
        ("30", Juridica), ("33", Juridica), ("34", Juridica),
        ("50", null),
    ];

    // ARCA's raw datosGenerales/tipoPersona: absent, FISICA in two cases, JURIDICA, and noise.
    private static readonly (string? Raw, string? Type)[] ArcaAnswers =
    [
        (null, null), ("FISICA", Humana), ("fisica", Humana), ("JURIDICA", Juridica), ("junk", null),
    ];

    // CreateEntityRequest.PersonType after the controller's validation: absent, or a known value in any case.
    private static readonly (string? Raw, string? Type)[] RequestedValues =
    [
        (null, null), ("Humana", Humana), ("Juridica", Juridica), ("humana", Humana),
    ];

    private static string Hyphenated(string prefix) => $"{prefix}-{Body}-{CheckDigit}";

    private static string Bare(string prefix) => $"{prefix}{Body}{CheckDigit}";

    // ── PersonTypeFromPrefix ────────────────────────────────────────────────────

    public static TheoryData<string, string?> PrefixCases()
    {
        var data = new TheoryData<string, string?>();

        foreach (var (prefix, type) in PrefixClasses)
        {
            data.Add(Hyphenated(prefix), type);
            data.Add(Bare(prefix), type);
            data.Add($"  {Hyphenated(prefix)}  ", type);
            data.Add($"\t{Bare(prefix)}\n", type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PrefixCases))]
    public void PersonTypeFromPrefix_WithOrWithoutHyphensOrPadded_ClassifiesByThePrefix(
        string cuit, string? expected)
    {
        Assert.Equal(expected, CuitHelper.PersonTypeFromPrefix(cuit));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("20")]                    // the prefix alone
    [InlineData("20-1111111-2")]          // 10 digits: short
    [InlineData("2011111112")]            // 10 digits, bare
    [InlineData("20-111111111-2")]        // 12 digits: long
    [InlineData("20-ABCDEFGH-2")]         // letters in the body
    [InlineData("2O-11111111-2")]         // a letter O where the prefix's zero belongs
    [InlineData("20 11111111 2")]         // inner spaces are not separators
    [InlineData("20.11111111.2")]         // nor are dots
    [InlineData("２０-11111111-2")] // full-width digits: char.IsDigit says yes, ASCII says no
    public void PersonTypeFromPrefix_WhenInputIsNotACuit_ReturnsNull(string? cuit)
    {
        Assert.Null(CuitHelper.PersonTypeFromPrefix(cuit));
    }

    // ── PersonTypeFromArca ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("FISICA",      Humana)]
    [InlineData("fisica",      Humana)]
    [InlineData(" Fisica ",    Humana)]
    [InlineData("JURIDICA",    Juridica)]
    [InlineData("juridica",    Juridica)]
    [InlineData("\tJuridica\n", Juridica)]
    public void PersonTypeFromArca_ArcaVocabularyInAnyCase_MapsToThePersonType(string raw, string expected)
    {
        Assert.Equal(expected, CuitHelper.PersonTypeFromArca(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("junk")]
    [InlineData("F")]
    [InlineData("FISICA JURIDICA")]
    // Our own word is not ARCA's: ARCA says FISICA, never HUMANA.
    [InlineData("HUMANA")]
    [InlineData("Humana")]
    public void PersonTypeFromArca_AnythingElse_ReturnsNull(string? raw)
    {
        Assert.Null(CuitHelper.PersonTypeFromArca(raw));
    }

    // ── ResolvePersonType ───────────────────────────────────────────────────────

    /// <summary>
    /// Every prefix class × every ARCA answer × every request value (8 × 5 × 4 = 160 cases). The
    /// expected outcome is read off three small lookup tables with the rule from the plan: the
    /// prefix if unambiguous, else ARCA, else the request, else Juridica. Warnings only happen when
    /// the prefix decides, and each source that says something different costs exactly one.
    /// </summary>
    public static TheoryData<string, string?, string?, string, int> ResolutionMatrix()
    {
        var data = new TheoryData<string, string?, string?, string, int>();

        foreach (var (prefix, prefixType) in MatrixPrefixes)
        {
            foreach (var (arca, arcaType) in ArcaAnswers)
            {
                foreach (var (requested, requestedType) in RequestedValues)
                {
                    var expected = prefixType ?? arcaType ?? requestedType ?? Juridica;

                    var warnings = prefixType is null
                        ? 0
                        : (Dissents(arcaType, prefixType) ? 1 : 0) + (Dissents(requestedType, prefixType) ? 1 : 0);

                    data.Add(Hyphenated(prefix), arca, requested, expected, warnings);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ResolutionMatrix))]
    public void ResolvePersonType_FullMatrix_FollowsThePrecedenceRule_AndWarnsOncePerDissentingSource(
        string cuit, string? arcaTipoPersona, string? requested, string expected, int expectedWarnings)
    {
        var logger = new RecordingLogger<CuitHelperTests>();

        var resolved = CuitHelper.ResolvePersonType(cuit, arcaTipoPersona, requested, logger);

        Assert.Equal(expected, resolved);
        Assert.True(PersonTypes.IsKnown(resolved)); // always something CK_Entities_PersonType admits
        Assert.Equal(expectedWarnings, logger.Warnings.Count);
        Assert.Empty(logger.Others);
        AssertNoCuitLogged(logger, cuit);
    }

    /// <summary>
    /// Worked examples, written out by hand, so the matrix above is not the only witness to the
    /// rule it encodes.
    /// </summary>
    [Theory]
    // The prefix is unambiguous: it wins, and each dissenting source costs one Warning.
    [InlineData("20-11111111-2", "JURIDICA", "Juridica", Humana,   2)]
    [InlineData("20-11111111-2", "FISICA",   "Humana",   Humana,   0)]
    [InlineData("27-22222222-3", "junk",     "Juridica", Humana,   1)]
    [InlineData("30-12345678-9", "fisica",   null,       Juridica, 1)]
    [InlineData("30-12345678-9", null,       "humana",   Juridica, 1)]
    [InlineData("30-12345678-9", "JURIDICA", "Juridica", Juridica, 0)]
    // The prefix decides nothing (50): ARCA, then the request, then the Juridica floor. No Warnings.
    [InlineData("50-12345678-1", "fisica",   "Juridica", Humana,   0)]
    [InlineData("50-12345678-1", "JURIDICA", "Humana",   Juridica, 0)]
    [InlineData("50-12345678-1", "junk",     "humana",   Humana,   0)]
    [InlineData("50-12345678-1", null,       "Humana",   Humana,   0)]
    [InlineData("50-12345678-1", "junk",     null,       Juridica, 0)]
    [InlineData("50-12345678-1", null,       null,       Juridica, 0)]
    public void ResolvePersonType_WorkedExamples_ResolveAndWarnAsDocumented(
        string cuit, string? arcaTipoPersona, string? requested, string expected, int expectedWarnings)
    {
        var logger = new RecordingLogger<CuitHelperTests>();

        var resolved = CuitHelper.ResolvePersonType(cuit, arcaTipoPersona, requested, logger);

        Assert.Equal(expected, resolved);
        Assert.Equal(expectedWarnings, logger.Warnings.Count);
        Assert.Empty(logger.Others);
        AssertNoCuitLogged(logger, cuit);
    }

    [Theory]
    [InlineData("20111111112")]
    [InlineData("  20-11111111-2  ")]
    [InlineData("\t20111111112\n")]
    public void ResolvePersonType_CuitWithoutHyphensOrPadded_StillLetsThePrefixWin(string cuit)
    {
        var logger = new RecordingLogger<CuitHelperTests>();

        var resolved = CuitHelper.ResolvePersonType(cuit, "JURIDICA", null, logger);

        Assert.Equal(Humana, resolved);
        Assert.Single(logger.Warnings);
        AssertNoCuitLogged(logger, cuit);
    }

    [Fact]
    public void ResolvePersonType_RequestedValueInAnyCase_IsReturnedInItsCanonicalSpelling()
    {
        // With the prefix undecided and no ARCA answer, the request decides, and what comes back is
        // what gets stored, so it must be the canonical spelling, not the client's.
        var logger = new RecordingLogger<CuitHelperTests>();

        Assert.Equal(Humana, CuitHelper.ResolvePersonType("50-12345678-1", null, "HUMANA", logger));
        Assert.Equal(Juridica, CuitHelper.ResolvePersonType("50-12345678-1", null, " juridica ", logger));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void ResolvePersonType_UnknownRequestedValue_IsIgnored_AndNeverWarns()
    {
        // The controller rejects an unknown PersonType with a 400 before it gets here. If one ever
        // got through anyway, it is simply not a source: no Warning, and no override of the floor.
        var logger = new RecordingLogger<CuitHelperTests>();

        Assert.Equal(Juridica, CuitHelper.ResolvePersonType("50-12345678-1", null, "Fisica", logger));
        Assert.Equal(Humana, CuitHelper.ResolvePersonType("20-11111111-2", null, "Empresa", logger));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void ResolvePersonType_Warning_NamesTheSourceAndBothTypes_ButNeverTheCuit()
    {
        const string cuit = "20-11111111-2";
        var logger = new RecordingLogger<CuitHelperTests>();

        CuitHelper.ResolvePersonType(cuit, "JURIDICA", "Juridica", logger);

        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains(logger.Warnings, w => w.Contains("ARCA", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, w => w.Contains("request", StringComparison.Ordinal));

        foreach (var warning in logger.Warnings)
        {
            Assert.Contains(Juridica, warning, StringComparison.Ordinal); // the dissenting value
            Assert.Contains(Humana, warning, StringComparison.Ordinal);   // the prefix's, which is kept
        }

        AssertNoCuitLogged(logger, cuit);
    }

    [Fact]
    public void ResolvePersonType_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => CuitHelper.ResolvePersonType("20-11111111-2", null, null, null!));
    }

    // ── PersonTypes ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Humana",     Humana)]
    [InlineData("humana",     Humana)]
    [InlineData("HUMANA",     Humana)]
    [InlineData(" Humana ",   Humana)]
    [InlineData("Juridica",   Juridica)]
    [InlineData("juridica",   Juridica)]
    [InlineData("JURIDICA",   Juridica)]
    [InlineData("\tJuridica", Juridica)]
    public void Canonicalize_KnownValueInAnyCase_ReturnsTheCanonicalSpelling(string raw, string expected)
    {
        Assert.Equal(expected, PersonTypes.Canonicalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("junk")]
    [InlineData("Fisica")]    // ARCA's word, not a person type
    [InlineData("Humanas")]
    [InlineData("Juri dica")]
    public void Canonicalize_BlankOrUnknown_ReturnsNull(string? raw)
    {
        Assert.Null(PersonTypes.Canonicalize(raw));
    }

    [Theory]
    [InlineData(Humana)]
    [InlineData(Juridica)]
    public void IsKnown_ExactCanonicalValue_ReturnsTrue(string value)
    {
        Assert.True(PersonTypes.IsKnown(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    // Ordinal on purpose. A case-insensitive database collation would let "humana" through the
    // CHECK, so the API itself must be the one that only ever stores the canonical spelling.
    [InlineData("humana")]
    [InlineData("JURIDICA")]
    [InlineData(" Humana")]
    [InlineData("Fisica")]
    public void IsKnown_AnythingButTheExactCanonicalValue_ReturnsFalse(string? value)
    {
        Assert.False(PersonTypes.IsKnown(value));
    }

    // ── FiscalRegimes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Monotributo")]
    [InlineData("ResponsableInscripto")]
    [InlineData("Exento")]
    [InlineData("Desconocido")]
    public void FiscalRegimes_IsKnown_ExactValue_ReturnsTrue(string regime)
    {
        Assert.True(FiscalRegimes.IsKnown(regime));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("monotributo")]
    [InlineData("Monotributo ")]
    [InlineData("RI")]
    [InlineData("Responsable Inscripto")]
    [InlineData("junk")]
    public void FiscalRegimes_IsKnown_AnythingElse_ReturnsFalse(string? regime)
    {
        Assert.False(FiscalRegimes.IsKnown(regime));
    }

    [Theory]
    [InlineData("Monotributo",          true)]
    [InlineData("ResponsableInscripto", true)]
    [InlineData("Exento",               true)]
    // Known but uninformative: the value a partial answer must never downgrade a known regime to.
    [InlineData("Desconocido",          false)]
    [InlineData("desconocido",          false)]
    [InlineData("monotributo",          false)]
    [InlineData(null,                   false)]
    [InlineData("",                     false)]
    [InlineData("junk",                 false)]
    public void FiscalRegimes_IsDeterminate_IsTrueOnlyForAKnownValueOtherThanDesconocido(
        string? regime, bool expected)
    {
        Assert.Equal(expected, FiscalRegimes.IsDeterminate(regime));
    }

    public static TheoryData<string, string> CategoryLetters()
    {
        var data = new TheoryData<string, string>();

        for (var letter = 'A'; letter <= 'K'; letter++)
        {
            data.Add(letter.ToString(), letter.ToString());
            data.Add(char.ToLowerInvariant(letter).ToString(), letter.ToString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CategoryLetters))]
    public void NormalizeMonotributoCategory_LetterAToKInAnyCase_ReturnsTheUpperCaseLetter(
        string raw, string expected)
    {
        Assert.Equal(expected, FiscalRegimes.NormalizeMonotributoCategory(raw));
    }

    [Theory]
    [InlineData(" h ",  "H")]
    [InlineData("\tK\n", "K")]
    public void NormalizeMonotributoCategory_PaddedLetter_IsTrimmed(string raw, string expected)
    {
        Assert.Equal(expected, FiscalRegimes.NormalizeMonotributoCategory(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("L")]           // the first letter past K
    [InlineData("l")]
    [InlineData("Z")]
    [InlineData("HH")]
    [InlineData("1")]
    [InlineData("A1")]
    [InlineData("Categoria A")] // pulling the letter out of descripcionCategoria is the parser's job
    [InlineData("Ñ")]      // Ñ
    public void NormalizeMonotributoCategory_AnythingButOneLetterAToK_ReturnsNull(string? raw)
    {
        Assert.Null(FiscalRegimes.NormalizeMonotributoCategory(raw));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static bool Dissents(string? other, string prefixType)
        => other is not null && !string.Equals(other, prefixType, StringComparison.Ordinal);

    /// <summary>
    /// Asserts that no log entry carries the CUIT in any of the forms that would identify the
    /// person: as given, as its eleven bare digits, or as its eight-digit body (a persona humana's
    /// DNI). Checks structured property values as well as the rendered text, because a property
    /// that is not rendered still reaches Application Insights.
    /// </summary>
    private static void AssertNoCuitLogged(RecordingLogger<CuitHelperTests> logger, string cuit)
    {
        var digits = new string(cuit.Where(char.IsAsciiDigit).ToArray());
        Assert.Equal(11, digits.Length);

        string[] forms = [cuit.Trim(), digits, digits[2..10]];

        foreach (var logged in logger.Warnings.Concat(logger.Others).Concat(logger.PropertyValues))
        {
            foreach (var form in forms)
            {
                Assert.DoesNotContain(form, logged, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Captures every entry, so a test can assert "exactly N Warnings and nothing else", and keeps
    /// the structured property values beside the rendered text for the no-CUIT assertion.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings       { get; } = [];
        public List<string> Others         { get; } = [];
        public List<string> PropertyValues { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            if (logLevel == LogLevel.Warning) Warnings.Add(message);
            else                              Others.Add(message);

            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                PropertyValues.AddRange(properties.Select(p => p.Value?.ToString() ?? string.Empty));
            }
        }
    }
}
