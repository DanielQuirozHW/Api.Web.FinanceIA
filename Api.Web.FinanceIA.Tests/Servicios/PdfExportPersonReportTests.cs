// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using System.Collections.Concurrent;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using iText.IO.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Fixtures for the persona humana PDF: an evaluation built by the REAL engine, stored and read back through
/// the same <see cref="PersonEvaluationJson"/> path GET /api/analyses/{id} uses, and the analysis around it in
/// each decision state.
/// </summary>
internal static class PersonPdfFixtures
{
    public static readonly Guid ResultId     = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid AnalysisId   = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid PreApproverId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid ReviewerId   = Guid.Parse("66666666-6666-6666-6666-666666666666");

    /// <summary>A responsable inscripto with BCRA, ARCA (ACTIVO), a year of IVA sales, a CPI series and a cuota.</summary>
    public static PersonEvaluationInput ResponsableInscripto() => Empty(FiscalRegimes.ResponsableInscripto) with
    {
        Bcra              = Bcra(Deuda(), Cheques()),
        BcraUpdatedAt     = AsOfUtc.AddDays(-3),
        Arca              = ArcaWithKey("ACTIVO"),
        ArcaUpdatedAt     = AsOfUtc.AddDays(-10),
        Incomes           = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1_500_000m)),
        InflationSeries   = Cpi(),
        InstallmentAmount = 80_000m,
        Documents         =
        [
            new(Guid.NewGuid(), DocumentTypes.DdjjIva, LegajoStatuses.Ready, new DateOnly(2026, 9, 30)),
            new(Guid.NewGuid(), DocumentTypes.Ganancias, LegajoStatuses.Ready, new DateOnly(2024, 12, 31)),
            new(Guid.NewGuid(), DocumentTypes.ManifestacionBienes, LegajoStatuses.NeedsReview, new DateOnly(2026, 6, 30))
        ]
    };

    /// <summary>A monotributista, category B.</summary>
    public static PersonEvaluationInput Monotributo() => Empty(FiscalRegimes.Monotributo) with
    {
        MonotributoCategory = "B",
        Bcra                = Bcra(Deuda(), Cheques()),
        BcraUpdatedAt       = AsOfUtc.AddDays(-3),
        Arca                = ArcaWithKey("ACTIVO"),
        ArcaUpdatedAt       = AsOfUtc.AddDays(-10),
        InstallmentAmount   = 30_000m
    };

    /// <summary>The engine's result for <paramref name="input"/>, stored and read back as the detail does.</summary>
    public static PersonEvaluationResponse Evaluate(
        PersonEvaluationInput input,
        Func<PersonEvaluationResult, PersonEvaluationResult>? mutate = null)
    {
        AnalysisParticipant participant = SolicitanteRow(input, mutate);
        PersonDecisionRules rules = PersonEvaluationJson.DecisionRules(new Dictionary<string, decimal>(), participant.FiltersJson);
        return PersonEvaluationJson.ToDetailResponse(rules, participant)!;
    }

    /// <summary>The solicitante row the POST writes for <paramref name="input"/>, evaluated by the real engine.</summary>
    public static AnalysisParticipant SolicitanteRow(
        PersonEvaluationInput input,
        Func<PersonEvaluationResult, PersonEvaluationResult>? mutate = null)
    {
        PersonEvaluationResult result = PersonEvaluationService.Evaluate(input);
        if (mutate is not null)
            result = mutate(result);

        return new AnalysisParticipant
        {
            ParticipantId     = Guid.NewGuid(),
            TenantId          = Tenant,
            AnalysisId        = AnalysisId,
            PersonLegajoId    = LegajoId,
            Role              = ParticipantRoles.Solicitante,
            AmountAtCharge    = input.RequestedAmount,
            Currency          = input.Currency,
            InstallmentAmount = input.InstallmentAmount,
            FiltersJson       = PersonEvaluationJson.Serialize(result.Filters),
            IndicatorsJson    = PersonEvaluationJson.Serialize(result.Indicators),
            CoverageJson      = PersonEvaluationJson.Serialize(result.Coverage),
            DocumentVersionsJson = PersonEvaluationJson.Serialize(new PersonEvaluationSources
            {
                Documents         = [],
                BcraDataUpdatedAt = input.BcraUpdatedAt,
                ArcaDataUpdatedAt = input.ArcaUpdatedAt,
                LufeDataUpdatedAt = input.LufeUpdatedAt,
                MacroSource       = input.InflationSeries is null ? null : "legajo",
                MacroFetchedAt    = input.InflationSeries is null ? null : input.BcraUpdatedAt,
                Parameters        = input.Parameters
            }),
            EvaluationVersion = result.Version,
            CreatedAt         = input.AsOfUtc
        };
    }

    /// <summary>Every filter and indicator with no data.</summary>
    public static PersonEvaluationResult AllSinDatos(PersonEvaluationResult result) => result with
    {
        Filters    = result.Filters.Select(f => f with { Outcome = PersonFilterOutcomes.SinDatos, Severity = null }).ToList(),
        Indicators = result.Indicators.Select(i => i with { Status = PersonIndicatorStatuses.SinDatos, Value = null, Band = null }).ToList()
    };

    public static Analysis Undecided() => new()
    {
        AnalysisId      = AnalysisId,
        TenantId        = Tenant,
        AnalysisKind    = AnalysisKinds.PersonaHumana,
        Status          = AnalysisStatuses.PendingReview,
        RequestedAmount = 1_000_000m,
        FiscalYear      = 2026,
        CreatedAt       = AsOfUtc
    };

    public static AnalysisDecisionChecks PreChecks() => new()
    {
        CounterGuaranteesConfirmed = true,
        FdrLimitConfirmed          = true,
        Reason                     = "Ingresos estables y garantía real",
        By                         = PreApproverId,
        At                         = AsOfUtc.AddDays(1)
    };

    public static AnalysisDecisionChecks ConfirmChecks() => new()
    {
        CounterGuaranteesConfirmed = true,
        FdrLimitConfirmed          = true,
        Reason                     = "Confirmo la preaprobación",
        By                         = ReviewerId,
        At                         = AsOfUtc.AddDays(2)
    };

    public static Analysis PreApproved()
    {
        Analysis a = Undecided();
        a.Status              = AnalysisStatuses.PreApproved;
        a.PreApprovedBy       = PreApproverId;
        a.PreApprovedAt       = AsOfUtc.AddDays(1);
        a.PreApprovedAmount   = 900_000m;
        a.PreApprovedCurrency = "ARS";
        a.PreApprovalChecksJson = PersonEvaluationJson.Serialize(PreChecks());
        return a;
    }

    public static Analysis Approved()
    {
        Analysis a = PreApproved();
        a.Status                 = AnalysisStatuses.Approved;
        a.ReviewedBy             = ReviewerId;
        a.ReviewedAt             = AsOfUtc.AddDays(2);
        a.DecisionAmount         = 900_000m;
        a.DecisionCurrency       = "ARS";
        a.ReviewNotes            = "Revisar en seis meses";
        a.ConfirmationChecksJson = PersonEvaluationJson.Serialize(ConfirmChecks());
        return a;
    }

    public static PersonPdfReport Report(Analysis analysis, PersonEvaluationResponse? evaluation, IReadOnlyList<CreditLine>? lines = null)
        => new()
        {
            ResultId           = ResultId,
            Analysis           = analysis,
            Evaluation         = evaluation,
            EntityName         = "Persona de Prueba",
            EntityCuit         = "20123456789",
            PreApprovalChecks  = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(analysis.PreApprovalChecksJson),
            ConfirmationChecks = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(analysis.ConfirmationChecksJson),
            UserNames          = new Dictionary<Guid, string> { [PreApproverId] = "Ana Analista", [ReviewerId] = "Gerardo Gerente" },
            CreditLines        = lines ?? []
        };

    public static CreditLine ActiveLine() => new()
    {
        CreditLineId     = Guid.NewGuid(),
        TenantId         = Tenant,
        Amount           = 900_000m,
        Currency         = "ARS",
        Status           = "Active",
        OriginAnalysisId = AnalysisId,
        CreatedAt        = AsOfUtc.AddDays(2)
    };

    public static string Text(byte[] pdf)
    {
        using var reader = new PdfReader(new MemoryStream(pdf));
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(i)));
        return sb.ToString();
    }
}

/// <summary>
/// Render smoke tests of the persona humana report: every section prints for each decision state and fiscal
/// regime, the summary counts an "alerta fuerte" only once, an undecided analysis says so, an all-SinDatos
/// evaluation still renders, and the balance-only header pieces never appear.
/// </summary>
public sealed class PdfExportPersonReportTests
{
    private static async Task<string> RenderAsync(PersonPdfReport report, params string[] sections)
    {
        byte[] pdf = await PdfExportServiceRenderTests.NewService()
            .GeneratePersonAsync(report, sections, CancellationToken.None);

        Assert.True(pdf.Length > 1000, $"PDF suspiciously small: {pdf.Length} bytes");
        return PersonPdfFixtures.Text(pdf);
    }

    [Fact]
    public async Task Undecided_RendersEverySection_AndSaysPendingDecision()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation));

        Assert.Contains("Informe de análisis · Persona humana", text);
        Assert.Contains("Persona de Prueba", text);
        Assert.Contains("20-12345678-9", text);
        Assert.Contains("20/10/2026", text);                 // participant.CreatedAt in ART
        Assert.Contains(PersonEvaluationService.Version, text);
        Assert.Contains("33333333", text);
        Assert.DoesNotContain("Ejercicio", text);

        foreach (string heading in new[]
                 {
                     "Resumen del resultado", "Elegibilidad e identidad", "Filtros previos", "Capacidad de pago",
                     "Solvencia", "Coherencia entre fuentes", "Cobertura documental", "Fuentes consultadas", "Decisión"
                 })
        {
            Assert.Contains(heading, text);
        }

        Assert.Contains("Sin puntaje: Financia propone y una persona decide.", text);
        Assert.Contains("Pendiente de decisión", text);
        Assert.Contains(PersonLabels.ProvisionalLabel, text);
        Assert.Contains("documentos obligatorios", text);
        Assert.Contains("Responsable inscripto", text);
        Assert.DoesNotContain("Línea de crédito originada", text);
    }

    [Theory]
    [InlineData(1, "Reevaluado el 28/10/2026 (1 vez)")]
    [InlineData(3, "Reevaluado el 28/10/2026 (3 veces)")]
    public async Task Decision_OfAReevaluatedAnalysis_SaysWhenAndHowManyTimes(int count, string expected)
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        var report = PersonPdfFixtures.Report(PersonPdfFixtures.PreApproved(), evaluation) with
        {
            RescoredAt   = new DateTime(2026, 10, 28, 15, 0, 0, DateTimeKind.Utc),   // 12:00 in Buenos Aires
            RescoreCount = count
        };

        string text = await RenderAsync(report, "persona_decision");

        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Decision_OfANeverReevaluatedAnalysis_SaysNothingAboutIt()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.PreApproved(), evaluation), "persona_decision");

        Assert.DoesNotContain("Reevaluado", text);
    }

    [Fact]
    public async Task PreApproved_PrintsThePreApprovalRecord_AndTheFinalActStillPending()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.PreApproved(), evaluation), "persona_decision");

        Assert.Contains("Pre-aprobado", text);
        Assert.Contains("Preaprobación", text);
        Assert.Contains("Ana Analista", text);
        Assert.Contains("$ 900.000", text);
        Assert.Contains("Ingresos estables y garantía real", text);
        Assert.Contains("(confirmado)", text);
        Assert.Contains("Pendiente de decisión", text);
        Assert.DoesNotContain("Gerardo Gerente", text);
    }

    [Fact]
    public async Task Approved_PrintsBothRecords_TheNotes_AndTheOriginatedCreditLine()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        string text = await RenderAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), evaluation, [PersonPdfFixtures.ActiveLine()]),
            "persona_decision");

        Assert.Contains("Aprobado", text);
        Assert.Contains("Confirmación", text);
        Assert.Contains("Ana Analista", text);
        Assert.Contains("Gerardo Gerente", text);
        Assert.Contains("Confirmo la preaprobación", text);
        Assert.Contains("Revisar en seis meses", text);      // notes differ from the reason
        Assert.Contains("Línea de crédito originada", text);
        Assert.Contains("Activo", text);
        Assert.DoesNotContain("Pendiente de decisión", text);
    }

    [Fact]
    public async Task Approved_NotesEqualToTheReason_ArePrintedOnce()
    {
        Analysis analysis = PersonPdfFixtures.Approved();
        analysis.ReviewNotes = "Confirmo la preaprobación";
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());

        string text = await RenderAsync(PersonPdfFixtures.Report(analysis, evaluation), "persona_decision");

        Assert.DoesNotContain("Notas de revisión", text);
        Assert.Contains("Sin línea de crédito originada.", text);
    }

    [Fact]
    public async Task AllSinDatos_Renders_WithNoEvidenceKindAndNoFigures()
    {
        var evaluation = PersonPdfFixtures.Evaluate(Empty(), PersonPdfFixtures.AllSinDatos);
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation));

        Assert.Contains("Sin datos", text);
        Assert.Contains($"{evaluation.Filters.Count} sin datos", text);
        Assert.Contains("0 rechazo", text);
        Assert.Contains("Pendiente de decisión", text);

        // A filter with no result is never attributed as Verificado / Declarado — even when the stored row
        // carries a kind.
        Assert.All(evaluation.Filters, f =>
            Assert.Equal("\u2014", PdfExportService.FilterEvidenceKindText(f with { EvidenceKind = PersonEvidenceKinds.Verificado })));
    }

    [Fact]
    public async Task Monotributo_PrintsTheCategoryAndTheTopeFilter()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.Monotributo());
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation),
            "persona_elegibilidad", "persona_capacidad", "persona_cobertura");

        Assert.Contains("Monotributo · Cat. B", text);
        Assert.Contains("Tope de la categoría de monotributo", text);
        Assert.DoesNotContain("Consumo declarado", text.Replace("Consumo declarado mensual", string.Empty));
        Assert.Contains("régimen monotributo", text);
    }

    [Fact]
    public async Task ResponsableInscripto_PrintsTheDeclaredConsumptionFilter_AndTheFormulas()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation), "persona_capacidad");

        Assert.Contains("Consumo declarado", text);
        Assert.DoesNotContain("Tope de la categoría de monotributo", text);
        Assert.Contains("Cobertura del servicio de deuda", text);
        Assert.Contains("Cuota sobre ingreso", text);
        Assert.Contains("Flujo de la actividad", text);
        Assert.Contains("servicio de deuda", text);
        Assert.Contains("$ 80.000", text);                    // the cuota
        Assert.Contains("$ 1.000.000", text);                 // the requested amount
    }

    [Fact]
    public async Task AnEvaluationFrozenBefore06_PrintsItsPlazoInput_AsMonths()
    {
        // A 0.5 cuota_ingreso, with the retired plazo input, replaces the engine's own.
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto(), result => result with
        {
            Indicators = result.Indicators
                .Select(i => i.Id == "cuota_ingreso" ? Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationPre06Fixtures.CuotaIngreso() : i)
                .ToList()
        });

        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation), "persona_capacidad");

        Assert.Contains(PersonLabels.InputLabels["plazoEstimadoDeudaMeses"], text);
        Assert.Contains("24 meses", text);
        Assert.Contains("en 24 meses", text);
    }

    [Fact]
    public async Task AFreshEvaluation_PrintsTheCuotaEstimateShare_AsAPercentage()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());

        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation), "persona_capacidad");

        Assert.Contains(PersonLabels.InputLabels["cuotaEstimadaPctSaldo"], text);
        Assert.DoesNotContain(PersonLabels.InputLabels["plazoEstimadoDeudaMeses"], text);
    }

    [Fact]
    public void FilterCounts_CountAlertaFuerteOnce_AndNotAsAlerta()
    {
        PersonFilterResult F(string outcome, string? severity = null)
            => new() { Id = "x", Outcome = outcome, Severity = severity, Evidence = "e", Source = "s" };

        var counts = PdfExportService.CountFilterOutcomes(
        [
            F(PersonFilterOutcomes.Rechazo),
            F(PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte),
            F(PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte),
            F(PersonFilterOutcomes.Alerta, PersonSeverities.Normal),
            F(PersonFilterOutcomes.Ok), F(PersonFilterOutcomes.Ok), F(PersonFilterOutcomes.Ok),
            F(PersonFilterOutcomes.SinDatos),
            F("Desconocido")                                    // an unknown outcome counts as Sin datos
        ]);

        Assert.Equal(
            new[] { ("Rechazo", 1), ("Alerta fuerte", 2), ("Alerta", 1), ("Ok", 3), ("Sin datos", 2) },
            counts.Select(c => (c.Label, c.Count)));
    }

    [Fact]
    public async Task SummaryText_CountsAlertaFuerteSeparately()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto(), r => r with
        {
            Filters = r.Filters
                .Select((f, i) => i switch
                {
                    0 => f with { Outcome = PersonFilterOutcomes.Alerta, Severity = PersonSeverities.Fuerte },
                    1 => f with { Outcome = PersonFilterOutcomes.Alerta, Severity = PersonSeverities.Fuerte },
                    2 => f with { Outcome = PersonFilterOutcomes.Alerta, Severity = PersonSeverities.Normal },
                    _ => f with { Outcome = PersonFilterOutcomes.Ok, Severity = null }
                })
                .ToList()
        });

        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation), "persona_resumen");

        Assert.Contains("2 alerta fuerte", text);
        Assert.Contains("1 alerta", text);
        Assert.DoesNotContain("3 alerta", text);
        Assert.Contains("0 rechazo", text);
        Assert.Contains("Para decisión del analista", text);
    }

    [Fact]
    public void FilterConsultedAt_PrefersTheFiltersOwnDate_ThenTheSnapshotOfItsSource()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());

        PersonFilterResult situacion = evaluation.Filters.Single(f => f.Id == "bcra_situacion");
        PersonFilterResult cuit      = evaluation.Filters.Single(f => f.Id == "arca_cuit_activo");

        // arca_cuit_activo has no fechaConsulta → the ARCA snapshot's UpdatedAt (AsOf − 10 days).
        Assert.NotNull(PdfExportService.FilterConsultedAt(situacion, evaluation));
        Assert.False(cuit.Inputs.ContainsKey("fechaConsulta"));
        Assert.Equal("10/10/2026", PdfExportService.FilterConsultedAt(cuit, evaluation));

        PersonFilterResult withDate = situacion with
        {
            Inputs = new Dictionary<string, string?> { ["fechaConsulta"] = "2026-10-05" }
        };
        Assert.Equal("05/10/2026", PdfExportService.FilterConsultedAt(withDate, evaluation));

        PersonFilterResult documentBased = situacion with { Source = "Legajo · DDJJ IVA", Inputs = new Dictionary<string, string?>() };
        Assert.Null(PdfExportService.FilterConsultedAt(documentBased, evaluation));
    }

    [Fact]
    public async Task NoEvaluation_StillPrintsTheDecision()
    {
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), null));

        Assert.Contains("El análisis no trae la evaluación de la persona.", text);
        Assert.Contains("Pendiente de decisión", text);
    }

    [Fact]
    public async Task SectionOrder_IsFixed_WhateverTheRequestOrder()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        string text = await RenderAsync(PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation),
            "persona_decision", "persona_resumen");

        Assert.True(text.IndexOf("Resumen del resultado", StringComparison.Ordinal)
                    < text.IndexOf("Decisión", text.IndexOf("Resumen del resultado", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.DoesNotContain("Filtros previos", text);
    }
}

/// <summary>
/// Two-plus persona exports at once, on the same brand-font path the balance report's concurrency guard
/// pins (<see cref="PdfExportConcurrencyTests"/>): every one must succeed, parse and embed the brand faces.
/// </summary>
public sealed class PdfExportPersonConcurrencyTests
{
    private const int ConcurrentExports = 16;
    private const int Rounds = 3;

    private static readonly string[] BrandFamilies = ["Sora", "DMSans", "JetBrainsMono"];

    [Fact]
    public async Task GeneratePersonAsync_ConcurrentExports_AllSucceedAndEmbedBrandFonts()
    {
        var evaluation = PersonPdfFixtures.Evaluate(PersonPdfFixtures.ResponsableInscripto());
        PersonPdfReport report = PersonPdfFixtures.Report(PersonPdfFixtures.Approved(), evaluation, [PersonPdfFixtures.ActiveLine()]);
        var pdfs = new ConcurrentBag<byte[]>();

        for (int round = 0; round < Rounds; round++)
        {
            FontCache.ClearSavedFonts();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, ConcurrentExports),
                new ParallelOptions { MaxDegreeOfParallelism = ConcurrentExports },
                async (_, ct) =>
                {
                    byte[] bytes = await PdfExportServiceRenderTests.NewService()
                        .GeneratePersonAsync(report, Array.Empty<string>(), ct);
                    pdfs.Add(bytes);
                });
        }

        Assert.Equal(ConcurrentExports * Rounds, pdfs.Count);
        foreach (byte[] bytes in pdfs)
        {
            Assert.Equal("%PDF"u8.ToArray(), bytes.AsSpan(0, 4).ToArray());

            using PigDocument doc = PigDocument.Open(bytes);
            HashSet<string> fonts = doc.GetPages()
                .SelectMany(p => p.Letters)
                .Select(l => l.FontName)
                .OfType<string>()
                .Where(n => n.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string family in BrandFamilies)
                Assert.Contains(fonts, n => n.Contains(family, StringComparison.Ordinal));
        }
    }
}
