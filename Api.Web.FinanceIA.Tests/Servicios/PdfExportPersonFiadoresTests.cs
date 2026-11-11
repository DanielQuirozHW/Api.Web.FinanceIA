// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.Extensions.Logging;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The persona report's "Fiadores y contragarantía" section (persona_fiadores). Every fiador is evaluated by the
/// REAL engine, stored the way the POST stores it and read back through <see cref="PersonEvaluationJson"/>, the
/// one reader behind GET /api/analyses/{id}: the section prints what the detail returns, the aggregate first,
/// then one block per fiador, and a fiador that cannot be read costs one line, never the report.
/// </summary>
public sealed class PdfExportPersonFiadoresTests
{
    private const string Heading = "Fiadores y contragarantía";

    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    private static readonly LegajoItem[] SolicitanteHome =
    [
        PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Inmueble, 2_000_000m, housing: true, keyKind: LegajoMatchKeyKinds.Partida, key: "P1")
    ];

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    /// <summary>One fiador of the analysis: his engine input, his identity on the Fiadores row, an optional row edit.</summary>
    private sealed record FiadorCase(
        FiadorEvaluationInput Input, string Name, string Cuit, Func<AnalysisParticipant, AnalysisParticipant>? Mutate = null);

    /// <summary>
    /// The detail of a persona analysis with <paramref name="fiadores"/>: the rows written as the POST writes
    /// them, then read by DecisionRules + ToFiadorResponses + ToDetailResponse, exactly as ResolveSolicitanteAsync does.
    /// </summary>
    private static PersonEvaluationResponse Detail(PersonEvaluationInput solicitanteInput, params FiadorCase[] fiadores)
    {
        AnalysisParticipant solicitante = PersonPdfFixtures.SolicitanteRow(solicitanteInput);

        var rows       = new List<AnalysisParticipant>();
        var identities = new Dictionary<Guid, FiadorIdentity>();
        foreach (FiadorCase f in fiadores)
        {
            AnalysisParticipant row = PersonFiadorFixtures.FiadorRow(PersonPdfFixtures.AnalysisId, Tenant, f.Input);
            row = f.Mutate?.Invoke(row) ?? row;
            rows.Add(row);
            identities[row.FiadorId!.Value] = new FiadorIdentity(f.Name, f.Cuit);
        }

        IReadOnlyList<AnalysisParticipant> ordered = PersonEvaluationJson.FiadorRowsOf(rows);
        PersonDecisionRules rules = PersonEvaluationJson.DecisionRules(new Dictionary<string, decimal>(), solicitante, ordered);
        return PersonEvaluationJson.ToDetailResponse(rules, solicitante, PersonEvaluationJson.ToFiadorResponses(ordered, identities))!;
    }

    /// <summary>A Fianza whose BCRA situation suggests a rejection: his housing without consent, a credit and a car.</summary>
    private static FiadorCase FlaggedFianza() => new(
        PersonEvaluationFiadorTests.Fiador(
            [
                PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Inmueble, 1_000_000m, housing: true),
                PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Credito, 500_000m),
                PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Rodado, 1_000_000m)
            ],
            person: Empty() with { Bcra = Bcra(Deuda(situacion: 5), Cheques(BcraChequesConsultaEstado.NoChequesReported)) }),
        "Pedro Fiador", "20111222333");

    /// <summary>The spouse, consenting: the solicitante's housing becomes executable through her guarantee.</summary>
    private static FiadorCase ConsentingSpouse() => new(
        PersonEvaluationFiadorTests.Fiador([], GuaranteeTypes.Conyuge, spouseConsent: true, solicitante: SolicitanteHome),
        "Ana Cónyuge", "27444555666");

    private static PersonEvaluationInput Solicitante(string currency = "ARS")
        => PersonPdfFixtures.ResponsableInscripto() with { RequestedAmount = 1_000_000m, Currency = currency };

    private static async Task<string> RenderAsync(PersonEvaluationResponse evaluation, params string[] sections)
        => await RenderAsync(PdfExportServiceRenderTests.NewService(), evaluation, sections);

    private static async Task<string> RenderAsync(PdfExportService service, PersonEvaluationResponse evaluation, params string[] sections)
    {
        byte[] pdf = await service.GeneratePersonAsync(
            PersonPdfFixtures.Report(PersonPdfFixtures.Undecided(), evaluation), sections, CancellationToken.None);

        Assert.True(pdf.Length > 1000, $"PDF suspiciously small: {pdf.Length} bytes");

        // Table cells wrap: one space for every run of whitespace, so a sentence reads as one.
        return System.Text.RegularExpressions.Regex.Replace(PersonPdfFixtures.Text(pdf), @"\s+", " ");
    }

    /// <summary>
    /// The words of <paramref name="phrase"/> appear in <paramref name="text"/> in order. A long cell wraps, and
    /// the extracted text interleaves the lines of the cells beside it, so a wrapped sentence is never contiguous.
    /// </summary>
    private static void AssertWordsInOrder(string phrase, string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = 0;
        foreach (string word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            while (at < words.Length && words[at] != word)
                at++;
            Assert.True(at < words.Length, $"'{word}' of \"{phrase}\" not found in order.");
            at++;
        }
    }

    private static string Pesos(decimal value) => "$ " + Math.Round(value, MidpointRounding.AwayFromZero).ToString("N0", EsAr);

    // ── Registration and order ───────────────────────────────────────────────────

    [Fact]
    public async Task DefaultSections_PrintTheFiadores_RightAfterSolvencia()
    {
        string text = await RenderAsync(Detail(Solicitante(), ConsentingSpouse()));

        int solvencia  = text.IndexOf(" Solvencia ", StringComparison.Ordinal);
        int fiadores   = text.IndexOf(Heading, StringComparison.Ordinal);
        int coherencia = text.IndexOf("Coherencia entre fuentes", StringComparison.Ordinal);
        Assert.True(solvencia >= 0 && fiadores > solvencia && coherencia > fiadores, $"{solvencia} < {fiadores} < {coherencia}");
    }

    [Fact]
    public async Task TheCallersOrder_DoesNotMoveTheSection()
    {
        string text = await RenderAsync(Detail(Solicitante(), ConsentingSpouse()),
            "persona_decision", "persona_coherencia", "persona_fiadores", "persona_solvencia");

        int solvencia  = text.IndexOf(" Solvencia ", StringComparison.Ordinal);
        int fiadores   = text.IndexOf(Heading, StringComparison.Ordinal);
        int coherencia = text.IndexOf("Coherencia entre fuentes", StringComparison.Ordinal);
        int decision   = text.IndexOf("Decisión", StringComparison.Ordinal);
        Assert.True(solvencia >= 0 && fiadores > solvencia && coherencia > fiadores && decision > coherencia);
    }

    [Fact]
    public async Task OnlyTheSection_WhenAskedAlone()
    {
        string text = await RenderAsync(Detail(Solicitante(), ConsentingSpouse()), "persona_fiadores");

        Assert.Contains(Heading, text);
        Assert.DoesNotContain("Resumen del resultado", text);
        Assert.DoesNotContain("Decisión", text);
    }

    // ── Two fiadores, one flagged ─────────────────────────────────────────────────

    [Fact]
    public async Task TwoFiadores_OneFlagged_PrintTheAggregateAndEveryBlock()
    {
        PersonEvaluationResponse detail = Detail(Solicitante(), FlaggedFianza(), ConsentingSpouse());
        CounterGuaranteeCoverage coverage = detail.CounterGuaranteeCoverage!;
        Assert.Equal(1, coverage.FlaggedFiadoresCount);   // the fixture is what the test says it is

        string text = await RenderAsync(detail, "persona_fiadores", "persona_decision");

        // The aggregate, from the very object the detail returns.
        Assert.Contains("Cobertura de contragarantías", text);
        Assert.Contains($"{(coverage.Ratio!.Value * 100m).ToString("0.#", EsAr)} % del monto solicitado · mínimo {coverage.MinPct} %", text);
        Assert.Contains(coverage.MeetsMin ? "Cumple el mínimo" : "No cumple el mínimo", text);
        Assert.Contains(PersonLabels.Band(coverage.Band)!, text);
        Assert.Contains(Pesos(coverage.ExecutableTotal), text);
        Assert.Contains(Pesos(1_000_000m), text);
        decimal extra = coverage.ExecutableTotalIncludingFlagged - coverage.ExecutableTotal;
        Assert.True(extra > 0m);
        Assert.Contains($"1 fiador con rechazo sugerido fuera del total (sumaría {Pesos(extra)})", text);

        // The persona_decision line stays.
        Assert.Contains("Cobertura de contragarantías", text[text.IndexOf("Decisión", StringComparison.Ordinal)..]);

        // Both identities, the CUIT formatted as the solicitante's.
        Assert.Contains("Pedro Fiador", text);
        Assert.Contains("20-11122233-3", text);
        Assert.Contains("Ana Cónyuge", text);
        Assert.Contains("27-44455566-6", text);

        // The guarantee terms.
        Assert.Contains("Fianza", text);
        Assert.Contains("Cónyuge o conviviente inscripto", text);
        Assert.Contains("Asentimiento conyugal", text);
        Assert.Contains("Fuera del total: rechazo sugerido", text);
        Assert.Contains(PersonLabels.Suggestion(PersonSuggestions.RechazoSugerido), text);

        // The flagged fiador's filters, in the solicitante's table.
        // (a wrapped row's cells interleave in the extracted text, so the evidence is what is matched whole)
        PersonFilterResult situacion = detail.Fiadores!.Single(f => f.Name == "Pedro Fiador").Filters.Single(f => f.Id == "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.Rechazo, situacion.Outcome);
        Assert.Contains("Situación 5 en la Central de Deudores", text);
        Assert.Contains("Rechazo Verificado", text);

        // His two indicators.
        Assert.Contains("Cobertura patrimonial del fiador", text);
        Assert.Contains("Cuota sobre ingreso (estrés)", text);

        // The assets: the excluded housing, the credit at 100 %, the car counted, the spouse's reach into the
        // solicitante's housing — with their frozen reasons and evidence kinds.
        PersonFiadorEvaluationResponse pedro = detail.Fiadores!.Single(f => f.Name == "Pedro Fiador");
        FiadorAssetResult housing = pedro.Coverage!.Assets.Single(a => a.IsHousing);
        FiadorAssetResult credit  = pedro.Coverage.Assets.Single(a => a.Kind == LegajoItemKinds.Credito);
        Assert.Equal(FiadorAssetReasons.ViviendaSinAsentimiento, housing.Reason);
        Assert.Equal(FiadorAssetReasons.NoEjecutableSinDocumentacion, credit.Reason);

        Assert.Contains("Excluido", text);
        Assert.Contains("Computado", text);
        Assert.Contains("40,0%", text);                          // the car's haircut; an excluded asset has none
        Assert.Null(credit.Haircut);
        AssertWordsInOrder(housing.ReasonText, text);            // the frozen reason, as the detail returns it
        AssertWordsInOrder(credit.ReasonText, text);
        Assert.Contains("descuento del 100 %", credit.ReasonText);
        AssertWordsInOrder("Del inventario del solicitante", text);
        AssertWordsInOrder("Manifestación de bienes", text);
        Assert.Contains("Declarado", text);

        // The totals of each fiador.
        Assert.Contains("Patrimonio ejecutable bruto", text);
        Assert.Contains("Pasivos", text);
        Assert.Contains("Garantías otorgadas", text);
        Assert.Contains(Pesos(pedro.Coverage.GrossExecutable), text);
    }

    [Fact]
    public void Headline_BelowTheMinimum_SaysSo_AndPluralisesTheFlagged()
    {
        var coverage = new CounterGuaranteeCoverage
        {
            ExecutableTotal                 = 600_000m,
            ExecutableTotalIncludingFlagged = 1_850_000m,
            FlaggedFiadoresCount            = 2,
            RequestedAmount                 = 1_000_000m,
            Currency                        = "ARS",
            Ratio                           = 0.6m,
            MinPct                          = 100,
            MeetsMin                        = false,
            Band                            = PersonIndicatorBands.Rojo,
            FiadoresCount                   = 3,
            Contributions                   = []
        };

        var rows = PdfExportService.CounterGuaranteeHeadline(coverage).ToDictionary(r => r.Label, r => r.Value);

        Assert.Equal("60 % del monto solicitado · mínimo 100 %", rows["Cobertura"]);
        Assert.Equal("No cumple el mínimo · Rojo", rows["Resultado"]);
        Assert.Equal("$ 600.000", rows["Total ejecutable"]);
        Assert.Equal("$ 1.000.000", rows["Monto solicitado"]);
        Assert.Equal("2 fiadores con rechazo sugerido fuera del total (sumarían $ 1.250.000)", rows["Rechazo sugerido"]);
    }

    [Fact]
    public async Task FiadorIndicators_PrintTheCoverageAsAMultiple_AndTheStressAsAShare()
    {
        PersonEvaluationResponse detail = Detail(Solicitante(), ConsentingSpouse());
        PersonIndicatorResult cobertura = detail.Fiadores![0].Indicators.Single(i => i.Id == "cobertura_patrimonial_fiador");
        Assert.NotNull(cobertura.Value);

        string text = await RenderAsync(detail, "persona_fiadores");

        Assert.Contains(cobertura.Value!.Value.ToString("#,##0.00", EsAr) + "×", text);
    }

    // ── Currency ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NonPesoOperation_SaysTheCoverageIsNotComputed()
    {
        FiadorCase spouse = ConsentingSpouse();
        spouse = spouse with { Input = spouse.Input with { Person = spouse.Input.Person with { Currency = "USD" } } };
        PersonEvaluationResponse detail = Detail(Solicitante("USD"), spouse);
        Assert.Null(detail.CounterGuaranteeCoverage!.Ratio);

        string text = await RenderAsync(detail, "persona_fiadores");

        Assert.Contains("La contragarantía no se calcula: la operación es en USD y no hay conversión de moneda.", text);
        Assert.DoesNotContain("del monto solicitado · mínimo", text);
        Assert.DoesNotContain("Cumple el mínimo", text);
        Assert.Contains("US$ 1.000.000", text);
    }

    // ── No fiadores ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoFiadores_TheRequestedSectionSaysSo()
    {
        PersonEvaluationResponse detail = Detail(Solicitante());
        Assert.Null(detail.Fiadores);

        string text = await RenderAsync(detail, "persona_fiadores");

        Assert.Contains(Heading, text);
        Assert.Contains("La operación no tiene fiadores evaluados.", text);
    }

    [Fact]
    public async Task NoFiadores_ByDefault_TheSectionStillPrints_AndTheDecisionLineIsUnchanged()
    {
        string text = await RenderAsync(Detail(Solicitante()));

        Assert.Contains("La operación no tiene fiadores evaluados.", text);
        Assert.Contains("Sin fiadores evaluados", text);   // persona_decision's own line
    }

    // ── Fault isolation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnreadableFiadorRow_PrintsOneLine_AndTheRestOfTheReportStillRenders()
    {
        var broken = FlaggedFianza() with { Mutate = row => { row.CoverageJson = "{no es json"; return row; } };
        PersonEvaluationResponse detail = Detail(Solicitante(), broken, ConsentingSpouse());
        var logger = new CapturingLogger();

        var service = new PdfExportService(
            new CashFlowNarrativeService(), new CashFlowReconciliationService(), new AlertRatioReconciliationService(), logger);

        string text = await RenderAsync(service, detail);

        Assert.Contains("No se pudo leer la evaluación de este fiador.", text);
        Assert.Contains("Pedro Fiador", text);                  // named, so the reader knows which one
        Assert.Contains("Ana Cónyuge", text);                   // the other fiador prints whole
        Assert.Contains("Cónyuge o conviviente inscripto", text);
        Assert.Contains("Coherencia entre fuentes", text);      // so does the rest of the report
        Assert.Contains("Decisión", text);

        // The log names ids, never the fiador's name or CUIT.
        string logged = Assert.Single(logger.Messages, m => m.Contains("unreadable", StringComparison.Ordinal));
        Assert.DoesNotContain("Pedro", logged);
        Assert.DoesNotContain("20111222333", logged);
        Assert.Contains(detail.Fiadores!.Single(f => f.Name == "Pedro Fiador").ParticipantId.ToString(), logged);
    }

    [Theory]
    [InlineData("filters")]
    [InlineData("indicators")]
    public async Task AnUnreadableFilterOrIndicatorColumn_IsUnreadableToo(string column)
    {
        var broken = ConsentingSpouse() with
        {
            Mutate = row =>
            {
                if (column == "filters") row.FiltersJson = "[oops";
                else row.IndicatorsJson = "[oops";
                return row;
            }
        };

        string text = await RenderAsync(Detail(Solicitante(), broken), "persona_fiadores");

        Assert.Contains("No se pudo leer la evaluación de este fiador.", text);
    }

    // ── Investment classes (engine 0.9) ───────────────────────────────────────────

    private static LegajoItem Investment(string? investmentClass, decimal market, string description)
    {
        LegajoItem item = PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Inversion, market);
        item.InvestmentClass = investmentClass;
        item.Description     = description;
        return item;
    }

    [Fact]
    public async Task TheAssetRow_NamesTheInvestmentClass()
    {
        var fiador = new FiadorCase(
            PersonEvaluationFiadorTests.Fiador(
            [
                Investment(LegajoInvestmentClasses.TituloPublico, 1_000_000m, "Bono sintetico"),
                Investment(LegajoInvestmentClasses.CuotaparteFci, 600_000m, "FCI sintetico"),
                Investment(LegajoInvestmentClasses.Otra, 500_000m, "Cartera sintetica"),
                Investment(null, 200_000m, "Inversion sin dato")
            ]),
            "Pedro Fiador", "20111222333");

        string text = await RenderAsync(Detail(Solicitante(), fiador));

        AssertWordsInOrder("Inversión · Título público Bono sintetico", text);
        AssertWordsInOrder("Inversión · Cuotaparte de FCI FCI sintetico", text);
        AssertWordsInOrder("Inversión sin clasificar Cartera sintetica", text);
        Assert.DoesNotContain("Inversión · Inversión", text);
        // The haircut each class took: 15 % on the bono, 30 % on the rest.
        AssertWordsInOrder($"Computado al valor de la manifestación de bienes con un descuento de 15 %: {Pesos(850_000m)}.", text);
    }

    // ── Labels ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryFiadorCode_HasItsOwnWords()
    {
        static IEnumerable<string> Constants(Type type) => type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.All(GuaranteeTypes.Known, k => Assert.True(PersonLabels.GuaranteeTypeLabels.ContainsKey(k), k));
        Assert.All(FiadorAssetTreatments.Known, k => Assert.True(PersonLabels.AssetTreatmentLabels.ContainsKey(k), k));
        Assert.All(Constants(typeof(LegajoItemKinds)), k => Assert.True(PersonLabels.AssetKindLabels.ContainsKey(k), k));
        Assert.All(Constants(typeof(LegajoInvestmentClasses)), k => Assert.True(PersonLabels.InvestmentClassLabels.ContainsKey(k), k));
        Assert.All(Constants(typeof(FiadorAssetReasons)), k => Assert.True(PersonLabels.AssetReasonLabels.ContainsKey(k), k));
        Assert.All(Constants(typeof(FiadorAssetAlerts)), k => Assert.True(PersonLabels.AssetAlertLabels.ContainsKey(k), k));
        Assert.Equal("Cónyuge o conviviente inscripto", PersonLabels.GuaranteeType(GuaranteeTypes.Conyuge));
        Assert.Equal("algo_nuevo", PersonLabels.AssetReason("algo_nuevo"));
    }

    private sealed class CapturingLogger : ILogger<PdfExportService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
