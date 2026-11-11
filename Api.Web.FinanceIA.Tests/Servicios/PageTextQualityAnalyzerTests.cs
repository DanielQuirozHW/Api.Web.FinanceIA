// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="PageTextQualityAnalyzer"/>: the pure per-page judgement on text samples, and the PdfPig
/// path on small iText-generated PDFs. Synthetic text only.
/// </summary>
public sealed class PageTextQualityAnalyzerTests
{
    // ── Pure judgement on text ──────────────────────────────────────────────────

    [Fact]
    public void AnalyzeText_OrdinaryText_IsGood()
    {
        var quality = PageTextQualityAnalyzer.AnalyzeText(1, LegajoPdfFactory.SampleText);

        Assert.Equal(PageTextVerdicts.Good, quality.Verdict);
        Assert.Equal(0m, quality.SuspiciousRatio);
        Assert.True(quality.Digits > 0);
        Assert.True(quality.Letters > quality.Digits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("Pagina 3")]
    public void AnalyzeText_NoOrAlmostNoText_IsNoText(string? text)
    {
        Assert.Equal(PageTextVerdicts.NoText, PageTextQualityAnalyzer.AnalyzeText(1, text).Verdict);
    }

    [Fact]
    public void AnalyzeText_ReplacementCharacters_AreSuspect()
    {
        var text = "Declaracion jurada " + new string('�', 20) + " periodo fiscal 2025 bienes personales";

        var quality = PageTextQualityAnalyzer.AnalyzeText(1, text);

        Assert.Equal(PageTextVerdicts.Suspect, quality.Verdict);
        Assert.True(quality.SuspiciousRatio > PageTextQualityAnalyzer.MaxSuspiciousRatio);
    }

    [Fact]
    public void AnalyzeText_PrivateUseAndControlCharacters_AreSuspect()
    {
        var text = "Ingresos brutos " + new string('', 10) + "\u0001\u0002\u0003\u0004\u0005 total 1.234,00 periodo";

        Assert.Equal(PageTextVerdicts.Suspect, PageTextQualityAnalyzer.AnalyzeText(1, text).Verdict);
    }

    [Fact]
    public void AnalyzeText_CidSequences_AreSuspect()
    {
        var text = "(cid:12)(cid:45)(cid:3) (cid:88)(cid:17)(cid:2) Ganancias (cid:9)(cid:10)(cid:11) periodo";

        var quality = PageTextQualityAnalyzer.AnalyzeText(1, text);

        Assert.Equal(PageTextVerdicts.Suspect, quality.Verdict);
        Assert.True(quality.SuspiciousRatio > 0.5m);
    }

    [Fact]
    public void AnalyzeText_GluedWords_AreSuspect()
    {
        var text = "declaracionjuradadelimpuestosobrelosbienespersonalesperiodofiscaldosmilveinticinco "
                 + "totaldebienesenelpaisdocemillonestrescientoscuarentaycinco";

        Assert.Equal(PageTextVerdicts.Suspect, PageTextQualityAnalyzer.AnalyzeText(1, text).Verdict);
    }

    [Fact]
    public void AnalyzeText_LetterSpacedText_IsSuspect()
    {
        var text = "G a n a n c i a s   p e r i o d o   f i s c a l   d o s   m i l";

        Assert.Equal(PageTextVerdicts.Suspect, PageTextQualityAnalyzer.AnalyzeText(1, text).Verdict);
    }

    [Fact]
    public void AnalyzeText_MostlySymbols_IsSuspect()
    {
        var text = "$$$ ### %%% &&& *** @@@ !!! ??? +++ === ~~~ ^^^ ab cd";

        Assert.Equal(PageTextVerdicts.Suspect, PageTextQualityAnalyzer.AnalyzeText(1, text).Verdict);
    }

    // ── PdfPig path on generated PDFs ──────────────────────────────────────────

    [Fact]
    public void Analyze_NativePdf_EveryPageGood_PdfTypeNative()
    {
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText, LegajoPdfFactory.SampleText);

        var report = PageTextQualityAnalyzer.Analyze(pdf);

        Assert.True(report.IsReadable);
        Assert.Equal(2, report.PageCount);
        Assert.True(report.AllPagesGood);
        Assert.Equal(PdfTypes.Native, report.PdfType);
        Assert.Equal([1, 2], report.Pages.Select(p => p.Page));
    }

    [Fact]
    public void Analyze_PagesWithoutText_PdfTypeScanned()
    {
        var report = PageTextQualityAnalyzer.Analyze(LegajoPdfFactory.BlankPdf(3));

        Assert.Equal(3, report.PageCount);
        Assert.Equal(3, report.NoTextPages);
        Assert.False(report.AllPagesGood);
        Assert.Equal(PdfTypes.Scanned, report.PdfType);
    }

    [Fact]
    public void Analyze_TextPageAndEmptyPage_PdfTypeMixed()
    {
        var report = PageTextQualityAnalyzer.Analyze(LegajoPdfFactory.MixedPdf(LegajoPdfFactory.SampleText));

        Assert.Equal(2, report.PageCount);
        Assert.Equal(1, report.GoodPages);
        Assert.Equal(1, report.NoTextPages);
        Assert.Equal(PdfTypes.Mixed, report.PdfType);
    }

    [Fact]
    public void Analyze_UnreadableBytes_DoNotThrow_AndReportUnreadable()
    {
        Exception? seen = null;

        var report = PageTextQualityAnalyzer.Analyze(
            "%PDF-1.4 this is not really a pdf"u8.ToArray(), (ex, _) => seen = ex);

        Assert.False(report.IsReadable);
        Assert.Null(report.PageCount);
        Assert.Null(report.PdfType);
        Assert.Null(report.ToJson());
        Assert.False(report.AllPagesGood);
        Assert.NotNull(seen);
    }

    [Fact]
    public void ToJson_HasThePageQualityShape_AndNoText()
    {
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);

        var json = PageTextQualityAnalyzer.Analyze(pdf).ToJson()!;

        using var document = JsonDocument.Parse(json);
        var page = Assert.Single(document.RootElement.EnumerateArray().ToList());
        Assert.Equal(
            ["chars", "digits", "page", "suspiciousRatio", "verdict"],
            page.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(PageTextVerdicts.Good, page.GetProperty("verdict").GetString());

        // Figures only: no word of the page leaks into the stored JSON.
        Assert.DoesNotContain("bienes", json, StringComparison.OrdinalIgnoreCase);
    }
}
