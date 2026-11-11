// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The light reports are pinned: the two hashes below must never move because of a theme or branding change. A
/// deliberate change to the light layout or palette updates them in a commit that says why.
/// <para>
/// History: first taken on b08a23a + the test-only clock seam (before the dark theme). Re-pinned by the PDF polish
/// (attention points, BCRA trend on the display scale, light contrast + default ink, legible footer mark) — the
/// company pin through all four of those, the persona pin through the palette, the ink and the footer. Re-pinned
/// both by the header lockup fix: page 1's symbol + wordmark on the Portal's FLogo rule (title and date drawn on
/// explicit baselines, unmoved), the symbol's ring closed before stroking, and the footer without the mark.
/// Re-pinned both by the November PDF review: the company pin through Q47, Q48, Q49, Q52, Q53+Q56, Q54, Q57, Q55 and
/// the fixture's placeholder CUIT (30-00000000-0); the persona pin through Q55 alone ("Página X de Y" on the margin,
/// in the footer both reports share).
/// Re-pinned both by the final PDF touches: the company pin through the cash-flow chart as its own block, the table
/// headers aligned like their columns and the amber caution colour; the persona pin through the last two (its table
/// headers and its caution states). Q47's code list and the scoring-profile block moved neither.
/// Re-pinned the company pin by the pagination polish: the LUFE summary box kept whole with its heading, the LUFE
/// tables' row breaks placed by TableRowBreaks, and the status labels moved onto their row's baseline. The persona
/// pin did not move; neither did the "lectura original" note (the fixture carries no manual correction).
/// </para>
/// </summary>
public sealed class PdfLightFingerprintTests
{
    // Synthetic company report, every section (PdfSyntheticReport), clock at PdfContentFingerprint.FixedUtc.
    internal const string CompanyLight = "DF8589A931BE1467436696E5F62B6E847FC4987D40ABD7FE1D60B73F5DF38C5F";

    // Synthetic persona report, every section (PersonPdfFixtures approved, responsable inscripto).
    internal const string PersonaLight = "75C655A05D94EEDD2750B3004793D9FA60BF0B8EE75DD35C32A6B8AB7597BE97";

    internal static async Task<byte[]> RenderCompanyAsync() =>
        await PdfContentFingerprint.FixedClockService().GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic",
            Array.Empty<string>(), CancellationToken.None, PdfSyntheticReport.Report());

    internal static async Task<byte[]> RenderPersonaAsync() =>
        await PdfContentFingerprint.FixedClockService().GeneratePersonAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto())),
            [], CancellationToken.None);

    [Fact]
    public async Task CompanyReport_Light_DrawsExactlyWhatItDrewBeforeTheDarkTheme()
    {
        string first  = PdfContentFingerprint.Of(await RenderCompanyAsync());
        string second = PdfContentFingerprint.Of(await RenderCompanyAsync());

        Assert.Equal(first, second);   // the fingerprint itself is reproducible
        Assert.Equal(CompanyLight, first);
    }

    [Fact]
    public async Task PersonaReport_Light_DrawsExactlyWhatItDrewBeforeTheDarkTheme()
    {
        string first  = PdfContentFingerprint.Of(await RenderPersonaAsync());
        string second = PdfContentFingerprint.Of(await RenderPersonaAsync());

        Assert.Equal(first, second);
        Assert.Equal(PersonaLight, first);
    }
}
