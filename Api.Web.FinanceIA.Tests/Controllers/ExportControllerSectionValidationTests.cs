// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-25

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the section-key validation at the top of <see cref="ExportController.ExportPdfAsync"/>.
/// <para>
/// "charts" used to be whitelisted as a deprecated no-op because an older Portal still sent it. The
/// Portal no longer can, so the key was removed and must now take exactly the path of any other unknown
/// key: a 400 ProblemDetails, before any repository or the PDF service is touched. Those dependencies
/// are passed as <c>null!</c> on purpose — reaching one would throw and fail the test.
/// </para>
/// </summary>
public sealed class ExportControllerSectionValidationTests
{
    private static readonly Guid AnalysisId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExportController BuildSut()
    {
        var sut = new ExportController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = "tenant-1",
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new ExportTenantSettingsStub(),
            new ExportComparisonStub(),
            new ExportBrandingStub(),
            new ExportAlertCatalogueStub(),
            NullLogger<ExportController>.Instance);

        sut.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return sut;
    }

    private static async Task<ProblemDetails> ExportProblemAsync(string sections)
    {
        IActionResult result = await BuildSut().ExportPdfAsync(
            AnalysisId, sections, template: "", cancellationToken: CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("charts")]
    [InlineData("CHARTS")]            // the whitelist is OrdinalIgnoreCase — no casing sneaks it back in
    [InlineData("balance,charts")]    // one bad key rejects the whole request, as for any unknown key
    public async Task ChartsKey_IsRejectedExactlyLikeAnUnknownKey(string sections)
    {
        ProblemDetails unknown = await ExportProblemAsync("totally_unknown_key");
        ProblemDetails charts  = await ExportProblemAsync(sections);

        Assert.Equal(unknown.Status, charts.Status);
        Assert.Equal(unknown.Title,  charts.Title);
        Assert.Equal(unknown.Detail, charts.Detail);
    }

    [Fact]
    public async Task InvalidSectionProblem_NoLongerAdvertisesCharts()
    {
        ProblemDetails problem = await ExportProblemAsync("charts");

        Assert.DoesNotContain("charts", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ── theme ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("blue")]
    [InlineData("1")]            // Enum.TryParse would take a number — the export does not
    [InlineData("dark,light")]
    [InlineData("oscuro")]
    public async Task UnknownTheme_Is400_InSpanish_BeforeAnyDependency(string theme)
    {
        // Every repository and the PDF service are null!: reaching one would throw.
        IActionResult result = await BuildSut().ExportPdfAsync(
            AnalysisId, sections: "", template: "", theme: theme, cancellationToken: CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Bad Request", problem.Title);
        Assert.Equal($"El tema '{theme}' no existe. Temas válidos: light, dark.", problem.Detail);
    }

    [Theory]
    [InlineData(null,     true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData("",       true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData("   ",    true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData("light",  true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData("LIGHT",  true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData("dark",   true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Dark)]
    [InlineData(" Dark ", true,  Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Dark)]
    [InlineData("blue",   false, Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    [InlineData("1",      false, Api.Web.FinanceIA.Servicios.Pdf.PdfTheme.Light)]
    public void TryParseTheme_AcceptsLightAndDarkInAnyCase_BlankIsLight(
        string? raw, bool valid, Api.Web.FinanceIA.Servicios.Pdf.PdfTheme expected)
    {
        Assert.Equal(valid, ExportController.TryParseTheme(raw, out var theme));
        Assert.Equal(expected, theme);
    }
}
