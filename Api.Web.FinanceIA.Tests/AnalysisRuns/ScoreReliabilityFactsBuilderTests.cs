// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// <see cref="ScoreReliabilityFactsBuilder"/> — the one place the reliability facts are assembled
/// (score credibility 1b-2a): S8 ratios source, S5 fiscal year, the integrity verdict, V6/S3 BCRA,
/// S9/V7 LUFE, S9 CASFOG, the JSON it persists and the caveat codes it logs. Pure, so no harness.
/// </summary>
public sealed class ScoreReliabilityFactsBuilderTests
{
    private const string Cuit = "30712345678";
    private const string Name = "Empresa Confidencial SA";

    private static readonly DateTime DeudaFetched  = new(2026, 10, 30, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LufeSnapshot  = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LufeCarriedAt = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CasfogFetched = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    private const string PassedVerifiedBalance = """
        {"assets":1000,"fiscal_year_detected":2024,
         "integrity":{"passed":true,"amountsUnit":"pesos","amountsUnitAssumed":false,
           "checks":[{"name":"assets_components","passed":true},{"name":"liabilities_components","passed":true},
                     {"name":"accounting_equation","passed":true}]}}
        """;

    private const string FailedBalance = """
        {"assets":1000,"fiscal_year_detected":2023,
         "integrity":{"passed":false,"amountsUnitAssumed":true,"affectedRatios":["endeudamiento"],
           "checks":[{"name":"assets_components","passed":false},{"name":"liabilities_components","passed":true},
                     {"name":"accounting_equation","passed":false},{"name":"column_swap","passed":true}]}}
        """;

    private const string NetProfitOnlyBalance = """
        {"assets":1000,"fiscal_year_detected":2024,"net_profit":12280839,"result_before_tax":15193935,"income_tax":-5317877,
         "integrity":{"passed":false,"affectedRatios":["roe","roa","margen_neto"],
           "checks":[{"name":"assets_components","passed":true},{"name":"liabilities_components","passed":true},
                     {"name":"accounting_equation","passed":true},{"name":"net_profit_reconciliation","passed":false}]}}
        """;

    private const string UnverifiedBalance = """
        {"assets":1000,"fiscal_year_detected":2024,
         "integrity":{"passed":true,"checks":[{"name":"accounting_equation","passed":true}],
           "skipped":["assets_components","liabilities_components"]}}
        """;

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static ScoringResult Scoring(bool chequesEvaluated = true) => new()
    {
        Score              = 30m,
        Level              = "Low",
        ChequesEvaluated   = chequesEvaluated,
        ConfigSource       = ScoringConfigSources.Defaults,
        ConfigHash         = "a1b2c3d4e5f60718",
        NegativeEquity     = false,
        ScoringAlgoVersion = "9",
    };

    private static BcraData Bcra(
        BcraDeudaConsultaEstado deuda = BcraDeudaConsultaEstado.DebtFound,
        BcraChequesConsultaEstado? cheques = BcraChequesConsultaEstado.NoChequesReported) => new()
    {
        Deuda = new BcraDeudaData
        {
            Estado            = deuda,
            Identificacion    = long.Parse(Cuit),
            Denominacion      = Name,
            SituacionMaxima   = 1,
            MontoTotal        = 987_654m,
            PeriodoConsultado = "202609",
            FetchedAt         = DeudaFetched,
        },
        Cheques = cheques is { } estado
            ? new BcraChequesData
            {
                Estado              = estado,
                Identificacion      = long.Parse(Cuit),
                Denominacion        = Name,
                MontoTotalRechazado = 123_456m,
            }
            : null,
    };

    private static LufeData Lufe(string? estado = null, bool withSeries = true, DateTime? asOf = null) => new()
    {
        Entidad = new LufeEntidad { Cuit = long.Parse(Cuit), Nombre = Name },
        VentasComprasMensual = withSeries
            ?
            [
                new LufeVentaCompraMes { Periodo = "202606", Ventas = 100m },
                new LufeVentaCompraMes { Periodo = "202608", Ventas = 120m },
                new LufeVentaCompraMes { Periodo = "202609", Ventas = null },    // compras only: not a datum
                new LufeVentaCompraMes { Periodo = "2026-10", Ventas = 130m },   // malformed key: ignored
            ]
            : null,
        VentasComprasMensualEstado = estado,
        VentasComprasMensualAsOf   = asOf,
    };

    private static CasfogData Casfog(CasfogConsultaEstado estado = CasfogConsultaEstado.DataFound) => new()
    {
        Estado    = estado,
        FetchedAt = CasfogFetched,
        Garantias = new CasfogGarantias
        {
            PorSgr = [new CasfogGarantiaSgr { Sgr = "SGR Confidencial", SaldoBrutoVigente = 555_000m, CantidadEnMora = 1 }],
        },
        Socios = new CasfogSocios
        {
            SocioProtector = [new CasfogSocio { BusinessName = "Socio Confidencial", Cuit = "20111222333" }],
        },
    };

    private static ScoreReliabilityInputs Inputs(
        bool ratiosComputed = true,
        string? balanceJson = PassedVerifiedBalance,
        BcraData? bcra = null,
        LufeData? lufe = null,
        CasfogData? casfog = null,
        bool omitBcra = false)
        => new(ratiosComputed, balanceJson, 2024, omitBcra ? null : bcra ?? Bcra(), lufe, LufeSnapshot, casfog);

    private static ScoreReliabilityFacts Build(ScoreReliabilityInputs inputs, ScoringResult? scoring = null)
        => ScoreReliabilityFactsBuilder.Build(scoring ?? Scoring(), inputs);

    private static JsonElement Json(ScoreReliabilityFacts facts)
        => JsonDocument.Parse(JsonSerializer.Serialize(facts)).RootElement;

    // ── S8 + S5 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RatiosSource_SaysWhetherTheRecomputeOrTheModelSuppliedTheRatios()
    {
        Assert.Equal("computed", Build(Inputs(ratiosComputed: true)).RatiosSource);
        Assert.Equal("model",    Build(Inputs(ratiosComputed: false)).RatiosSource);
    }

    [Fact]
    public void FiscalYear_IsTheRequestedOne()
        => Assert.Equal(2024, Build(Inputs()).FiscalYear);

    // ── Integrity ───────────────────────────────────────────────────────────────

    [Fact]
    public void Integrity_FromAPassedVerifiedBalance()
    {
        var integrity = Build(Inputs(balanceJson: PassedVerifiedBalance)).Integrity!;

        Assert.True(integrity.Passed);
        Assert.True(integrity.Verified);
        Assert.Empty(integrity.NotVerifiedReasons);
        Assert.Equal(FiscalYearCheck.Match, integrity.FiscalYearStatus);
        Assert.False(integrity.AmountsUnitAssumed);
        Assert.Equal(0, integrity.FailedChecks);
    }

    [Fact]
    public void Integrity_FromAFailedBalance_CountsTheFailedChecks()
    {
        var integrity = Build(Inputs(balanceJson: FailedBalance)).Integrity!;

        Assert.False(integrity.Passed);
        Assert.True(integrity.Verified);
        Assert.Equal(2, integrity.FailedChecks);
        Assert.Equal(FiscalYearCheck.Mismatch, integrity.FiscalYearStatus);
        Assert.True(integrity.AmountsUnitAssumed);
    }

    /// <summary>
    /// Score credibility 1b-5: the integrity block carries the balance's affectedRatios — ratio KEYS, no figure —
    /// and writes an empty list (known: none) rather than omitting it, so a reader can tell it from a pre-1b-5
    /// block, which has no such key at all.
    /// </summary>
    [Fact]
    public void Integrity_CarriesTheAffectedRatioKeys_AndAnEmptyListWhenNoneIsAffected()
    {
        var failed = Build(Inputs(balanceJson: FailedBalance));
        Assert.Equal(["endeudamiento"], failed.Integrity!.AffectedRatios);
        Assert.Equal(["endeudamiento"],
                     Json(failed).GetProperty("integrity").GetProperty("affectedRatios").EnumerateArray().Select(e => e.GetString()));

        var passed = Build(Inputs(balanceJson: PassedVerifiedBalance));
        Assert.Empty(passed.Integrity!.AffectedRatios!);
        Assert.Equal(JsonValueKind.Array, Json(passed).GetProperty("integrity").GetProperty("affectedRatios").ValueKind);
        Assert.Equal(6, ScoreReliabilityFacts.CurrentVersion);  // facts version 6 added netProfitColumnMix
    }

    /// <summary>
    /// Reliability policy 5 (facts version 4): the block names the failed checks — check CODES, no figure — and
    /// writes an empty list when nothing failed, so a reader can tell it from a pre-v4 block (no key at all).
    /// </summary>
    [Fact]
    public void Integrity_CarriesTheFailedCheckNames_AndAnEmptyListWhenNothingFailed()
    {
        var failed = Build(Inputs(balanceJson: FailedBalance));
        Assert.Equal(["assets_components", "accounting_equation"], failed.Integrity!.FailedCheckNames);
        Assert.Equal(["assets_components", "accounting_equation"],
                     Json(failed).GetProperty("integrity").GetProperty("failedCheckNames").EnumerateArray().Select(e => e.GetString()));

        var passed = Build(Inputs(balanceJson: PassedVerifiedBalance));
        Assert.Empty(passed.Integrity!.FailedCheckNames!);
        Assert.Equal(JsonValueKind.Array, Json(passed).GetProperty("integrity").GetProperty("failedCheckNames").ValueKind);
    }

    [Fact]
    public void Integrity_CarriesTheTrialBalanceKind_AndCaveatCodesFlagIt()
    {
        var scoring    = Scoring();
        var trial      = Build(Inputs(balanceJson: PassedVerifiedBalance.Replace("{\"assets\":1000,",
                             "{\"assets\":1000,\"document_kind\":\"trial_balance\",", StringComparison.Ordinal)), scoring);
        var statements = Build(Inputs(balanceJson: PassedVerifiedBalance), scoring);

        Assert.Equal("trial_balance", trial.Integrity!.DocumentKind);
        Assert.Equal(["trial_balance"], ScoreReliabilityFactsBuilder.CaveatCodes(scoring, trial));
        Assert.Equal(6, trial.Version);  // facts version 6 added netProfitColumnMix

        // Financial statements: the member is omitted, so their facts block is unchanged.
        Assert.Null(statements.Integrity!.DocumentKind);
        Assert.False(Json(statements).GetProperty("integrity").TryGetProperty("documentKind", out _));
        Assert.Empty(ScoreReliabilityFactsBuilder.CaveatCodes(scoring, statements));
    }

    [Fact]
    public void CaveatCodes_FlagANetProfitOnlyFailureAsPresentation_AndAStructureFailureAsFailed()
    {
        var scoring = Scoring();

        var presentation = Build(Inputs(balanceJson: NetProfitOnlyBalance), scoring);
        Assert.False(presentation.Integrity!.NetProfitSignFlip);
        Assert.Equal(["integrity_presentation"], ScoreReliabilityFactsBuilder.CaveatCodes(scoring, presentation));

        // The same failure on a net profit read with the wrong sign is a misread.
        var flipped = Build(Inputs(balanceJson: NetProfitOnlyBalance.Replace("\"net_profit\":12280839", "\"net_profit\":-9876058",
                                                                             StringComparison.Ordinal)), scoring);
        Assert.True(flipped.Integrity!.NetProfitSignFlip);
        Assert.Equal(["integrity_failed"], ScoreReliabilityFactsBuilder.CaveatCodes(scoring, flipped));

        // Policy 6: the facts carry the recorded column mix; true is a misread, false keeps the caveat, and a block
        // without it (above) leaves the member out.
        Assert.Null(presentation.Integrity!.NetProfitColumnMix);
        Assert.False(Json(presentation).GetProperty("integrity").TryGetProperty("netProfitColumnMix", out _));

        var mixed = Build(Inputs(balanceJson: NetProfitOnlyBalance.Replace(
            "{\"name\":\"net_profit_reconciliation\",\"passed\":false}",
            "{\"name\":\"net_profit_reconciliation\",\"passed\":false,\"columnMix\":true}", StringComparison.Ordinal)), scoring);
        Assert.True(mixed.Integrity!.NetProfitColumnMix);
        Assert.True(Json(mixed).GetProperty("integrity").GetProperty("netProfitColumnMix").GetBoolean());
        Assert.Equal(["integrity_failed"], ScoreReliabilityFactsBuilder.CaveatCodes(scoring, mixed));

        var unmixed = Build(Inputs(balanceJson: NetProfitOnlyBalance.Replace(
            "{\"name\":\"net_profit_reconciliation\",\"passed\":false}",
            "{\"name\":\"net_profit_reconciliation\",\"passed\":false,\"columnMix\":false}", StringComparison.Ordinal)), scoring);
        Assert.False(unmixed.Integrity!.NetProfitColumnMix);
        Assert.Equal(["integrity_presentation"], ScoreReliabilityFactsBuilder.CaveatCodes(scoring, unmixed));

        var failed = Build(Inputs(balanceJson: FailedBalance), scoring);
        var codes  = ScoreReliabilityFactsBuilder.CaveatCodes(scoring, failed);
        Assert.Contains("integrity_failed", codes);
        Assert.DoesNotContain("integrity_presentation", codes);
    }

    [Fact]
    public void Integrity_FromAnUnverifiedBalance_ListsWhatDidNotRun()
    {
        var integrity = Build(Inputs(balanceJson: UnverifiedBalance)).Integrity!;

        Assert.True(integrity.Passed);
        Assert.False(integrity.Verified);
        Assert.Equal(["assets_components_skipped", "liabilities_components_skipped"], integrity.NotVerifiedReasons);
        Assert.Null(integrity.AmountsUnitAssumed);
    }

    [Fact]
    public void Integrity_WrittenAsJsonNull_IsUnavailable()
    {
        var integrity = Build(Inputs(balanceJson: """{"integrity":null,"fiscal_year_detected":2024}""")).Integrity!;

        Assert.False(integrity.Passed);
        Assert.False(integrity.Verified);
        Assert.Equal([ExtractionIntegrityResponse.IntegrityUnavailableReason], integrity.NotVerifiedReasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"assets":1000}""")]   // extracted before prompt 2.18: no integrity key
    [InlineData("not json")]
    public void Integrity_IsOmitted_WhenTheBalanceCarriesNone(string? balanceJson)
    {
        var facts = Build(Inputs(balanceJson: balanceJson));

        Assert.Null(facts.Integrity);
        Assert.False(Json(facts).TryGetProperty("integrity", out _));
    }

    // ── BCRA (V6, S3) ───────────────────────────────────────────────────────────

    [Fact]
    public void Bcra_DebtFound_CarriesEstadoPeriodAndFetchDate()
    {
        var bcra = Build(Inputs()).Bcra!;

        Assert.Equal("DebtFound", bcra.DeudaEstado);
        Assert.Equal("202609", bcra.PeriodoConsultado);
        Assert.Equal(DeudaFetched, bcra.DeudaFetchedAt);
        Assert.Equal("NoChequesReported", bcra.ChequesEstado);
        Assert.True(bcra.ChequesEvaluated);
    }

    [Fact]
    public void Bcra_EstadosAreWrittenAsNames_NotNumbers()
    {
        var bcra = Json(Build(Inputs(bcra: Bcra(BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.LookupFailed))))
            .GetProperty("bcra");

        Assert.Equal("LookupFailed", bcra.GetProperty("deudaEstado").GetString());
        Assert.Equal("LookupFailed", bcra.GetProperty("chequesEstado").GetString());
    }

    [Fact]
    public void Bcra_ChequesEvaluated_ComesFromTheEngine()
    {
        var facts = Build(Inputs(bcra: Bcra(cheques: BcraChequesConsultaEstado.LookupFailed)), Scoring(chequesEvaluated: false));

        Assert.False(facts.Bcra!.ChequesEvaluated);
        Assert.Equal("LookupFailed", facts.Bcra.ChequesEstado);
    }

    [Fact]
    public void Bcra_WithNoChequesBlock_OmitsItsEstado()
    {
        var bcra = Json(Build(Inputs(bcra: Bcra(cheques: null)))).GetProperty("bcra");

        Assert.False(bcra.TryGetProperty("chequesEstado", out _));
    }

    [Fact]
    public void Bcra_WithNoPayload_IsOmitted()
        => Assert.Null(Build(Inputs(omitBcra: true)).Bcra);

    // ── LUFE (S9/V7) ────────────────────────────────────────────────────────────

    [Fact]
    public void Lufe_FreshSeries()
    {
        var lufe = Build(Inputs(lufe: Lufe())).Lufe!;

        Assert.Equal(ScoreReliabilityLufeVentasEstados.Fresh, lufe.VentasEstado);
        Assert.Null(lufe.VentasAsOf);
        Assert.Equal("202608", lufe.VentasUltimoPeriodo);   // 202609 has no sales; "2026-10" is malformed
        Assert.Equal(LufeSnapshot, lufe.SnapshotAt);
    }

    [Fact]
    public void Lufe_CarriedForward_CarriesTheDateTheSeriesWasActuallyFetched()
    {
        var lufe = Build(Inputs(lufe: Lufe(LufeVentasComprasEstados.CarriedForward, asOf: LufeCarriedAt))).Lufe!;

        Assert.Equal("carried_forward", lufe.VentasEstado);
        Assert.Equal(LufeCarriedAt, lufe.VentasAsOf);
        Assert.Equal("202608", lufe.VentasUltimoPeriodo);
        Assert.Equal(LufeSnapshot, lufe.SnapshotAt);
    }

    [Fact]
    public void Lufe_LookupFailed()
    {
        var lufe = Build(Inputs(lufe: Lufe(LufeVentasComprasEstados.LookupFailed, withSeries: false))).Lufe!;

        Assert.Equal("lookup_failed", lufe.VentasEstado);
        Assert.Null(lufe.VentasUltimoPeriodo);
    }

    [Fact]
    public void Lufe_AFreshAnswerWithNoSeries_IsNone()
        => Assert.Equal("none", Build(Inputs(lufe: Lufe(withSeries: false))).Lufe!.VentasEstado);

    [Fact]
    public void Lufe_WithNoSnapshot_IsOmitted()
        => Assert.Null(Build(Inputs(lufe: null)).Lufe);

    // ── CASFOG (S9) ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CasfogConsultaEstado.DataFound)]
    [InlineData(CasfogConsultaEstado.NoDataReported)]
    [InlineData(CasfogConsultaEstado.LookupFailed)]
    public void Casfog_CarriesEstadoNameAndFetchDate(CasfogConsultaEstado estado)
    {
        var casfog = Build(Inputs(casfog: Casfog(estado))).Casfog!;

        Assert.Equal(estado.ToString(), casfog.Estado);
        Assert.Equal(CasfogFetched, casfog.FetchedAt);
    }

    [Fact]
    public void Casfog_WithNoSnapshot_IsOmitted()
        => Assert.Null(Build(Inputs(casfog: null)).Casfog);

    // ── The persisted JSON ──────────────────────────────────────────────────────

    [Fact]
    public void Json_CopiesNoNameCuitOrAmount()
    {
        var json = JsonSerializer.Serialize(Build(Inputs(lufe: Lufe(), casfog: Casfog())));

        foreach (var forbidden in new[] { Cuit, Name, "Confidencial", "20111222333", "987654", "123456", "555000" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public void Json_UsesTheDocumentedKeys_AndOmitsUnknowns()
    {
        var facts = Build(Inputs(ratiosComputed: false, lufe: Lufe(), casfog: Casfog()),
            new ScoringResult { ChequesEvaluated = true, NegativeEquity = null });

        var root = Json(facts);
        var keys = root.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(
            ["version", "bcraSituacionUnreadable", "macroImputed", "sectorFallback",
             "ratiosSource", "fiscalYear", "integrity", "bcra", "lufe", "casfog"],
            keys);
        Assert.Equal(ScoreReliabilityFacts.CurrentVersion, root.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Json_SectorRaw_TravelsOnlyWithAFallback()
    {
        var fallback = Build(Inputs(), new ScoringResult { SectorFallback = true,  SectorRaw = "Agropecuario" });
        var known    = Build(Inputs(), new ScoringResult { SectorFallback = false, SectorRaw = "stray" });

        Assert.Equal("Agropecuario", Json(fallback).GetProperty("sectorRaw").GetString());
        Assert.False(Json(known).TryGetProperty("sectorRaw", out _));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var inputs = Inputs(lufe: Lufe(LufeVentasComprasEstados.CarriedForward, asOf: LufeCarriedAt), casfog: Casfog());

        Assert.Equal(JsonSerializer.Serialize(Build(inputs)), JsonSerializer.Serialize(Build(inputs)));
    }

    // ── Caveat codes (the log line) ─────────────────────────────────────────────

    [Fact]
    public void CaveatCodes_AreEmpty_WhenEverySourceIsFine()
    {
        var scoring = Scoring();
        var facts   = Build(Inputs(lufe: Lufe(), casfog: Casfog()), scoring);

        Assert.Empty(ScoreReliabilityFactsBuilder.CaveatCodes(scoring, facts));
    }

    [Fact]
    public void CaveatCodes_ListEveryFlaggedFact()
    {
        var scoring = new ScoringResult
        {
            RatiosLowCoverageFallback = true,
            NegativeEquity            = true,
            BcraSituacionUnreadable   = true,
            MacroImputed              = true,
            SectorFallback            = true,
            SectorRaw                 = "Agropecuario",
            ChequesEvaluated          = true,
            ThresholdFallbacks        = ["leverage"],
        };
        var facts = Build(
            Inputs(
                ratiosComputed: false,
                balanceJson:    FailedBalance,
                bcra:           Bcra(BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.LookupFailed),
                lufe:           Lufe(LufeVentasComprasEstados.CarriedForward, asOf: LufeCarriedAt),
                casfog:         Casfog(CasfogConsultaEstado.LookupFailed)),
            scoring);

        var codes = ScoreReliabilityFactsBuilder.CaveatCodes(scoring, facts);

        Assert.Equal(
            ["ratios_low_coverage", "ratios_from_model", "negative_equity", "bcra_situacion_unreadable",
             "bcra_deuda_lookup_failed", "cheques_lookup_failed", "macro_imputed", "sector_fallback",
             "config_threshold_fallback", "integrity_failed", "fiscal_year_mismatch", "amounts_unit_assumed",
             "lufe_carried_forward", "casfog_lookup_failed"],
            codes);
        Assert.DoesNotContain(codes, c => c.Contains("Agropecuario", StringComparison.Ordinal));
    }

    [Fact]
    public void CaveatCodes_FlagAnUnverifiedIntegrityAndAFailedLufeLookup()
    {
        var scoring = Scoring();
        var facts   = Build(Inputs(balanceJson: UnverifiedBalance, lufe: Lufe(LufeVentasComprasEstados.LookupFailed, withSeries: false)), scoring);

        Assert.Equal(["integrity_unverified", "lufe_lookup_failed"], ScoreReliabilityFactsBuilder.CaveatCodes(scoring, facts));
    }

    [Fact]
    public void CaveatCodes_IgnoreAFailedChequesLookup_ThePenaltyDidNotRead()
    {
        var scoring = Scoring(chequesEvaluated: false);
        var facts   = Build(Inputs(bcra: Bcra(cheques: BcraChequesConsultaEstado.LookupFailed)), scoring);

        Assert.DoesNotContain("cheques_lookup_failed", ScoreReliabilityFactsBuilder.CaveatCodes(scoring, facts));
    }
}
