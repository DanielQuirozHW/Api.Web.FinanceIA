// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-05

using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Pins the "Datos CASFOG" PDF section — the documentary evidence behind the §7.4 guarantee-arrears
/// penalty that the risk section states as a one-line verdict.
/// <para>
/// WHY THIS MATTERS BEYOND COSMETICS: the section reports a third party's payload, so every one of its
/// honesty rules is a place where a rendering bug would invent a fact the vendor never reported.
/// A missing balance must read "N/D" and never "$ 0" — CASFOG's "-" sentinel becomes null in
/// <c>CasfogService.ParseString</c>, and "not reported" is not "no exposure" (the same distinction
/// <c>ScoringService.ComputeProporcionEnMora</c> relies on, since a null balance joins neither side of
/// its proportion). Arrears must be driven by <c>CantidadEnMora</c> alone, the exact fact the score
/// uses, never by <c>DiasAtraso</c> or <c>SituacionDeuda</c>. And the CASFOG situación is a 0-10 vendor
/// scale that must be shown but never coloured with the BCRA 1-6 thresholds the BCRA section uses —
/// two vendors, two scales, and mixing them would mis-state the row.
/// </para>
/// <para>
/// The absent-data cases are equally load-bearing. NoDataReported and LookupFailed are different facts
/// and must not collapse into one another; a null or malformed snapshot must degrade to the standard
/// placeholder rather than throw, because the section ships in the "tecnico" template for every tenant
/// and most tenants have no CASFOG data at all.
/// </para>
/// <para>
/// Fixtures are real <see cref="CasfogData"/> objects serialised with a bare
/// <c>JsonSerializer.Serialize</c> — byte-for-byte how <c>EntitiesController</c> persists the column —
/// so the test exercises the true stored encoding rather than a hand-written guess at it.
/// </para>
/// <para>
/// PDF assertions pin ASCII substrings: iText's extractor is the wrong place to bet on how an accented
/// glyph round-trips. Numbers are es-AR, matching the rest of the document.
/// </para>
/// </summary>
public sealed class PdfExportCasfogSectionTests
{
    // Section and sub-heading markers, all accent-free by construction or truncated before the accent.
    private const string SectionMarker  = "Datos CASFOG";
    private const string TotalsMarker   = "Central de garant";   // "Central de garantías"
    private const string PerSgrMarker   = "Reparto por SGR";
    private const string NotAvailable   = "Datos no disponibles.";
    private const string NoDataMarker   = "Sin datos informados";
    private const string FailedMarker   = "No se pudo consultar";

    // Estado-cell markers, matched on the RAW extracted text. "al d" is the accent-free head of
    // "al día"; note it must NOT be packed, because space-stripping would make it collide with the
    // "Saldo" in the column headers. "en mora" has no such neighbour either way.
    private const string EnMoraMarker = "en mora";
    private const string AlDiaMarker  = "al d";

    // Numbers and money live inside laid-out table rows, where iText inserts and drops spaces
    // unpredictably, so every numeric assertion goes through Pack().
    private const string NotDeterminedMarker = "N/D";

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static CasfogGarantiaSgr Sgr(
        string   name,
        decimal? saldo,
        int?     cantidadEnMora = null,
        int?     situacion      = null,
        int?     diasAtraso     = null) =>
        new()
        {
            Sgr               = name,
            SaldoBrutoVigente = saldo,
            CantidadEnMora    = cantidadEnMora,
            SituacionDeuda    = situacion,
            DiasAtraso        = diasAtraso
        };

    /// <summary>
    /// Serialises exactly as <c>EntitiesController</c> does — no options — so the fixture carries the
    /// stored encoding of the enum and of every numeric leaf.
    /// </summary>
    private static string Json(CasfogData data) => JsonSerializer.Serialize(data);

    private static CasfogData Found(
        CasfogGarantias garantias) =>
        new() { Estado = CasfogConsultaEstado.DataFound, Garantias = garantias };

    // ── Render helper ─────────────────────────────────────────────────────────

    private static async Task<string> RenderCasfogSectionText(string? casfogDataJson) =>
        await RenderText(
            new AnalysisResult
            {
                ResultId       = new Guid("88888888-8888-8888-8888-888888888888"),
                CasfogDataJson = casfogDataJson
            },
            new[] { "casfog" });

    private static async Task<string> RenderText(AnalysisResult result, IReadOnlyList<string> sections)
    {
        var service = new PdfExportService(
            new CashFlowNarrativeService(),
            new CashFlowReconciliationService(),
            new AlertRatioReconciliationService(),
            NullLogger<PdfExportService>.Instance);

        byte[] pdf = await service.GenerateAsync(
            result, new Analysis { FiscalYear = 2025 }, "tenant-x",
            sections, CancellationToken.None);

        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }

    private static string Pack(string text) => text.Replace(" ", "");

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // ── Totals + per-SGR breakdown ────────────────────────────────────────────

    [Fact]
    public async Task CasfogSection_WithTotalsAndSeveralSgrs_RendersBothTables()
    {
        string json = Json(Found(new CasfogGarantias
        {
            SaldoBrutoGarantiasVigentes = 25_000_000m,
            CantidadGarantiasOtorgadas  = 14,
            MontoGarantiasOtorgadas     = 90_000_000m,
            CantidadGarantiasVigentes   = 6,
            PorSgr =
            [
                Sgr("Garantizar SGR", 15_000_000m, situacion: 1),
                Sgr("Acindar Pymes SGR", 10_000_000m, cantidadEnMora: 2, situacion: 4, diasAtraso: 75)
            ]
        }));

        string text   = await RenderCasfogSectionText(json);
        string packed = Pack(text);

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(TotalsMarker,  text, StringComparison.Ordinal);
        Assert.Contains(PerSgrMarker,  text, StringComparison.Ordinal);

        // es-AR money and counts, packed because they sit inside laid-out table rows.
        Assert.Contains("$25.000.000", packed, StringComparison.Ordinal);
        Assert.Contains("$90.000.000", packed, StringComparison.Ordinal);
        Assert.Contains("$15.000.000", packed, StringComparison.Ordinal);
        Assert.Contains("$10.000.000", packed, StringComparison.Ordinal);

        Assert.Contains("Garantizar", text, StringComparison.Ordinal);
        Assert.Contains("Acindar",    text, StringComparison.Ordinal);

        Assert.DoesNotContain(NotAvailable, text, StringComparison.Ordinal);
    }

    // ── Exposure order ────────────────────────────────────────────────────────

    [Fact]
    public async Task CasfogSection_SgrRows_RenderLargestBalanceFirst()
    {
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr =
            [
                Sgr("Mediana SGR", 5_000_000m),
                Sgr("Grande SGR", 40_000_000m),
                Sgr("Chica SGR", 250_000m)
            ]
        }));

        string text = await RenderCasfogSectionText(json);

        int grande  = text.IndexOf("Grande",  StringComparison.Ordinal);
        int mediana = text.IndexOf("Mediana", StringComparison.Ordinal);
        int chica   = text.IndexOf("Chica",   StringComparison.Ordinal);

        Assert.True(grande >= 0 && mediana >= 0 && chica >= 0,
            $"All three SGR rows must render. Extracted text:\n{text}");
        Assert.True(grande < mediana && mediana < chica,
            $"SGRs must render largest-balance-first, got Grande={grande}, Mediana={mediana}, " +
            $"Chica={chica}. Extracted text:\n{text}");
    }

    // ── A "-" (null) balance is N/D, never 0, and sorts last ──────────────────

    [Fact]
    public async Task CasfogSection_NullSaldo_RendersNotAvailableAndSortsLast()
    {
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr =
            [
                Sgr("Sin Saldo SGR", null),
                Sgr("Con Saldo SGR", 3_000_000m)
            ]
        }));

        string text   = await RenderCasfogSectionText(json);
        string packed = Pack(text);

        Assert.Contains(NotDeterminedMarker, packed, StringComparison.Ordinal);

        // The unreported balance must never be printed as a zero amount.
        Assert.DoesNotContain("$0", packed, StringComparison.Ordinal);

        int conSaldo = text.IndexOf("Con Saldo", StringComparison.Ordinal);
        int sinSaldo = text.IndexOf("Sin Saldo", StringComparison.Ordinal);
        Assert.True(conSaldo >= 0 && sinSaldo >= 0,
            $"Both SGR rows must render. Extracted text:\n{text}");
        Assert.True(conSaldo < sinSaldo,
            $"A row with no reported balance must sort last, not as if it were zero. " +
            $"Con Saldo={conSaldo}, Sin Saldo={sinSaldo}. Extracted text:\n{text}");
    }

    // ── Arrears comes from CantidadEnMora, and only from it ───────────────────

    [Fact]
    public async Task CasfogSection_CantidadEnMoraPositive_RendersEnMora()
    {
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr =
            [
                Sgr("Mora Uno SGR", 8_000_000m, cantidadEnMora: 1),
                Sgr("Mora Dos SGR", 6_000_000m, cantidadEnMora: 5),
                Sgr("Sana SGR",     4_000_000m, cantidadEnMora: 0)
            ]
        }));

        string text = await RenderCasfogSectionText(json);

        Assert.Equal(2, Count(text, EnMoraMarker));
        Assert.Equal(1, Count(text, AlDiaMarker));
    }

    [Fact]
    public async Task CasfogSection_CantidadEnMoraNullOrZero_RendersAlDiaEvenWithAtrasoAndSituacion()
    {
        // Both rows carry a high situación and a long días-de-atraso. Neither is arrears: the score
        // reads CantidadEnMora and nothing else, and so must the report.
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr =
            [
                Sgr("Nula SGR", 9_000_000m, cantidadEnMora: null, situacion: 9, diasAtraso: 400),
                Sgr("Cero SGR", 7_000_000m, cantidadEnMora: 0,    situacion: 8, diasAtraso: 300)
            ]
        }));

        string text   = await RenderCasfogSectionText(json);
        string packed = Pack(text);

        Assert.Equal(0, Count(text, EnMoraMarker));
        Assert.Equal(2, Count(text, AlDiaMarker));

        // The situación and días-de-atraso figures are still reported — shown, just not interpreted.
        // Packing pins each row's saldo/situación/atraso triple in order, so this cannot pass on a
        // stray digit from elsewhere in the document.
        Assert.Contains("$9.000.0009400", packed, StringComparison.Ordinal);
        Assert.Contains("$7.000.0008300", packed, StringComparison.Ordinal);
    }

    // ── Situación is shown, never interpreted with the BCRA scale ─────────────

    [Fact]
    public async Task CasfogSection_SituacionDeuda_IsLabelledWithItsOwnScaleAndNotTreatedAsBcra()
    {
        // Situación 9 would be far past the BCRA "danger" threshold (that scale only runs 1-6). On the
        // CASFOG 0-10 scale it is just a number, and with no arrears reported the row stays "al día".
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr = [Sgr("Escala SGR", 12_000_000m, cantidadEnMora: null, situacion: 9)]
        }));

        string text   = await RenderCasfogSectionText(json);
        string packed = Pack(text);

        // The column header names the vendor's scale, so the figure cannot be read as a BCRA situación.
        Assert.Contains("(0-10)", packed, StringComparison.Ordinal);
        Assert.Contains("$12.000.0009", packed, StringComparison.Ordinal);

        Assert.Contains(AlDiaMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(EnMoraMarker, text, StringComparison.Ordinal);

        // The BCRA section's own scale legend must not leak into this section.
        Assert.DoesNotContain("1–6", text, StringComparison.Ordinal);
    }

    // ── Tri-state: nothing on file vs. the call failed ────────────────────────

    [Fact]
    public async Task CasfogSection_NoDataReported_RendersHonestNoteAndNoTables()
    {
        string json = Json(new CasfogData { Estado = CasfogConsultaEstado.NoDataReported });

        string text = await RenderCasfogSectionText(json);

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(NoDataMarker,  text, StringComparison.Ordinal);
        Assert.DoesNotContain(PerSgrMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TotalsMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FailedMarker, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CasfogSection_LookupFailed_RendersHonestNoteAndNoTables()
    {
        string json = Json(new CasfogData { Estado = CasfogConsultaEstado.LookupFailed });

        string text = await RenderCasfogSectionText(json);

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(FailedMarker,  text, StringComparison.Ordinal);
        Assert.DoesNotContain(PerSgrMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TotalsMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(NoDataMarker, text, StringComparison.Ordinal);
    }

    // ── Absent / malformed / empty snapshots degrade, never throw ─────────────

    [Fact]
    public async Task CasfogSection_NullSnapshot_RendersPlaceholderAndDoesNotThrow()
    {
        string text = await RenderCasfogSectionText(null);

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(NotAvailable,  text, StringComparison.Ordinal);
        Assert.DoesNotContain(PerSgrMarker, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CasfogSection_MalformedSnapshot_RendersPlaceholderAndDoesNotThrow()
    {
        string text = await RenderCasfogSectionText("{not json");

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(NotAvailable,  text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CasfogSection_DataFoundButEmpty_RendersPlaceholderNotAnEmptyTable()
    {
        string json = Json(Found(new CasfogGarantias()));

        string text = await RenderCasfogSectionText(json);

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(NotAvailable,  text, StringComparison.Ordinal);
        Assert.DoesNotContain(TotalsMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PerSgrMarker, text, StringComparison.Ordinal);
    }

    // ── The section is safe to ship in "tecnico" for every tenant ─────────────

    [Fact]
    public async Task TecnicoTemplate_ForEntityWithoutCasfog_StillSucceeds()
    {
        var result = new AnalysisResult
        {
            ResultId         = new Guid("88888888-8888-8888-8888-888888888889"),
            AiSuggestedScore = 55.00m,
            AiSuggestedLevel = "Medium",
            CasfogDataJson   = null
        };

        string text = await RenderText(result, ExportController.Templates["tecnico"]);

        Assert.Contains(SectionMarker, text, StringComparison.Ordinal);
        Assert.Contains(NotAvailable,  text, StringComparison.Ordinal);
    }

    // ── QA Q54: a column the vendor left empty on every row is not printed ─────

    [Fact]
    public async Task CasfogSection_SituacionAndAtrasoEmptyOnEveryRow_DropsBothColumns()
    {
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr = [Sgr("Primera SGR", 9_000_000m, cantidadEnMora: 0), Sgr("Segunda SGR", 7_000_000m, cantidadEnMora: 1)]
        }));

        string text = await RenderCasfogSectionText(json);

        Assert.Contains(PerSgrMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("(0-10)", Pack(text), StringComparison.Ordinal);
        Assert.DoesNotContain("atraso", text, StringComparison.Ordinal);
        Assert.Contains("Saldo bruto vigente", text, StringComparison.Ordinal);
        Assert.Contains(EnMoraMarker, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CasfogSection_OneColumnEmptyOnEveryRow_DropsOnlyThatColumn()
    {
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr = [Sgr("Primera SGR", 9_000_000m, diasAtraso: 30), Sgr("Segunda SGR", 7_000_000m)]
        }));

        string text = await RenderCasfogSectionText(json);

        Assert.DoesNotContain("(0-10)", Pack(text), StringComparison.Ordinal);
        Assert.Contains("atraso", text, StringComparison.Ordinal);
        Assert.Contains("$9.000.00030", Pack(text), StringComparison.Ordinal);   // saldo, then días de atraso
    }

    [Fact]
    public async Task CasfogSection_AColumnWithOneReportedValue_IsKept()
    {
        string json = Json(Found(new CasfogGarantias
        {
            PorSgr = [Sgr("Primera SGR", 9_000_000m, situacion: 4), Sgr("Segunda SGR", 7_000_000m)]
        }));

        string text = await RenderCasfogSectionText(json);

        Assert.Contains("(0-10)", Pack(text), StringComparison.Ordinal);
        Assert.Contains("$9.000.0004", Pack(text), StringComparison.Ordinal);
        Assert.DoesNotContain("atraso", text, StringComparison.Ordinal);
    }
}
