// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Shared fixtures for the ScoringService suites that fill the gaps left by
/// <see cref="ScoringServiceTests"/> (which pins ratio renormalisation and the BCRA
/// exclusion-reason split, and is deliberately NOT modified).
/// <para>
/// Same harness shape as that file: the EF Core in-memory provider and a hand-rolled setup — no
/// mocking library. The difference is <see cref="BuildAsync"/>, which can SEED <c>ScoringConfig</c>
/// rows. No test in the project did that before, so <c>LoadConfigAsync</c>, the <c>__sector__</c>
/// overlay and every <c>Get(cfg, key, default)</c> hit-arm ran unexercised.
/// </para>
/// <para>
/// With no seeded row every weight/threshold falls back to <see cref="ScoringDefaults"/>: ratio
/// weights sum to 1.00, component weights are 0.50 / 0.35 / 0.15.
/// </para>
/// </summary>
internal static class ScoringTestHarness
{
    /// <summary>The tenant every fixture scores under.</summary>
    public const string Tenant = "tenant-scoring";

    /// <summary>A second tenant, used to prove config rows do not leak across tenants.</summary>
    public const string OtherTenant = "tenant-other";

    // ── SUT construction ──────────────────────────────────────────────────────

    /// <summary>Builds the service over an empty config table, or one seeded for <see cref="Tenant"/>.</summary>
    public static Task<ScoringService> BuildAsync(params (string Key, decimal Value)[] config)
        => BuildFromRowsAsync(config.Select(c => Row(Tenant, c.Key, c.Value)).ToArray());

    /// <summary>
    /// Builds the service over explicitly supplied rows — needed when a row must belong to another
    /// tenant, or when two rows must deliberately collide on (TenantId, ConfigKey).
    /// </summary>
    public static async Task<ScoringService> BuildFromRowsAsync(params ScoringConfig[] rows)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"scoring-{Guid.NewGuid()}")
            .Options;

        var db = new FinanciaDbContext(options);

        if (rows.Length > 0)
        {
            db.ScoringConfigs.AddRange(rows);
            db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
            await db.SaveChangesAsync();
        }

        return new ScoringService(db, NullLogger<ScoringService>.Instance);
    }

    /// <summary>A single <c>ScoringConfig</c> row. ConfigId is set explicitly rather than generated.</summary>
    public static ScoringConfig Row(string tenantId, string key, decimal value) => new()
    {
        ConfigId    = Guid.NewGuid(),
        TenantId    = tenantId,
        ConfigKey   = key,
        ConfigValue = value,
        Description = "seeded by ScoringTestHarness",
        UpdatedAt   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>
    /// Scores under <see cref="Tenant"/>. <paramref name="ventas"/> defaults to null — no LUFE sales
    /// series at all — which keeps the §7.3a penalty dormant, so every fixture written before it
    /// existed observes the same score, exactly as <paramref name="bcra"/>'s null cheques block does
    /// for §7.1. <paramref name="casfog"/> defaults to null for the same reason: no per-SGR guarantee
    /// rows, so the §7.4 arrears penalty cannot fire.
    /// </summary>
    public static Task<ScoringResult> Score(
        ScoringService sut,
        RatiosData     ratios,
        BcraData?      bcra         = null,
        BcraMacroData? macro        = null,
        string?        entitySector = null,
        List<LufeVentaCompraMes>? ventas = null,
        List<CasfogGarantiaSgr>?  casfog = null)
        => sut.CalculateAsync(Tenant, entitySector, ratios, bcra, macro, ventas, casfog, CancellationToken.None);

    // ── LUFE sales fixtures (§7.3a) ───────────────────────────────────────────

    /// <summary>
    /// A monthly sales series in the shape <c>LufeService.MergeVentasComprasMensual</c> produces:
    /// one entry per period, ascending, ragged. A null amount is a month LUFE reported with no sales
    /// declaration — the real shape when only <c>Compras</c> was filed — and is deliberately NOT the
    /// same thing as a zero.
    /// </summary>
    public static List<LufeVentaCompraMes> Ventas(params (string Periodo, decimal? Ventas)[] meses)
        => meses
            .Select(m => new LufeVentaCompraMes { Periodo = m.Periodo, Ventas = m.Ventas })
            .ToList();

    /// <summary>
    /// Thirteen consecutive months ending at <paramref name="ultimoPeriodo"/>, flat at
    /// <paramref name="baseVentas"/> except the final month, which is <paramref name="baseVentas"/>
    /// moved by <paramref name="variacion"/>. Gives an exact interanual reading of
    /// <paramref name="variacion"/> with no rounding anywhere.
    /// </summary>
    public static List<LufeVentaCompraMes> VentasInteranual(
        decimal variacion,
        decimal baseVentas    = 100_000m,
        string  ultimoPeriodo = "202601")
    {
        int year  = int.Parse(ultimoPeriodo[..4]);
        int month = int.Parse(ultimoPeriodo[4..]);
        int index = (year * 12) + (month - 1);

        var meses = new List<LufeVentaCompraMes>();
        for (int back = 12; back >= 0; back--)
        {
            int at = index - back;
            var periodo = $"{at / 12:D4}{(at % 12) + 1:D2}";

            meses.Add(new LufeVentaCompraMes
            {
                Periodo = periodo,
                Ventas  = back == 0 ? baseVentas * (1m + variacion) : baseVentas
            });
        }

        return meses;
    }

    // ── CASFOG guarantee fixtures (§7.4) ──────────────────────────────────────

    /// <summary>
    /// Per-SGR guarantee rows in the shape <c>CasfogService</c> produces: one entry per SGR, with a
    /// null <c>SaldoBrutoVigente</c> standing for the vendor's <c>"-"</c> sentinel and a null
    /// <c>CantidadEnMora</c> for a count that did not parse. Neither is the same thing as a zero.
    /// </summary>
    public static List<CasfogGarantiaSgr> Garantias(params (decimal? Saldo, int? EnMora)[] filas)
        => filas
            .Select((f, i) => new CasfogGarantiaSgr
            {
                Sgr               = $"SGR {i + 1}",
                SaldoBrutoVigente = f.Saldo,
                CantidadEnMora    = f.EnMora,
                // Deliberately populated and deliberately never read by the engine: the CASFOG
                // situación is a 0-10 vendor scale and must not be confused with the BCRA 1-6 one.
                SituacionDeuda    = 3,
                DiasAtraso        = 45,
            })
            .ToList();

    /// <summary>
    /// Two SGR rows totalling <paramref name="saldoTotal"/>, of which exactly
    /// <paramref name="proporcion"/> sits in the single row reporting arrears. Gives an exact reading
    /// of <paramref name="proporcion"/> with no rounding, from a fixed ONE row in arrears — so a test
    /// can vary the share while holding the arrears COUNT constant.
    /// </summary>
    public static List<CasfogGarantiaSgr> GarantiasProporcion(
        decimal proporcion,
        decimal saldoTotal = 1_000_000m)
    {
        decimal enMora = saldoTotal * proporcion;
        return Garantias((enMora, 2), (saldoTotal - enMora, 0));
    }

    // ── BCRA fixtures ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reported "no debt": the BCRA base scores 0 and is NOT excluded, so it pins the BCRA term at a
    /// constant. <paramref name="cheques"/> defaults to null — no cheques block at all — which keeps
    /// the §7.1 penalty dormant, so every fixture written before it existed observes the same score.
    /// </summary>
    public static BcraData BcraNoDebt(
        List<BcraSituacionPeriodo>? historial = null,
        BcraChequesData?            cheques   = null) =>
        new()
        {
            Deuda = new BcraDeudaData
            {
                Estado               = BcraDeudaConsultaEstado.NoDebtReported,
                HistorialSituaciones = historial ?? new List<BcraSituacionPeriodo>(),
            },
            Cheques = cheques
        };

    /// <summary>
    /// DebtFound with the given situación. <paramref name="historial"/> defaults to EMPTY, which
    /// keeps the trend block dormant — it needs Count >= 2 — so the fixture observes the raw
    /// situación → score map with no trend adjustment layered on top. <paramref name="cheques"/>
    /// defaults to null for the same reason: no cheques block, so the §7.1 penalty cannot fire and
    /// every pre-existing fixture keeps the score it pinned.
    /// </summary>
    public static BcraData BcraDebtFound(
        int? situacionMaxima,
        List<BcraSituacionPeriodo>? historial = null,
        BcraChequesData?            cheques   = null) =>
        new()
        {
            Deuda = new BcraDeudaData
            {
                Estado               = BcraDeudaConsultaEstado.DebtFound,
                SituacionMaxima      = situacionMaxima,
                HistorialSituaciones = historial ?? new List<BcraSituacionPeriodo>(),
            },
            Cheques = cheques
        };

    /// <summary>
    /// Deuda LookupFailed — the shape BcraService returns when the Central de Deudores call fails while
    /// something else in the payload survived. The engine EXCLUDES the BCRA component on this input, so
    /// it is how a fixture proves the cheques penalty cannot reach a score that carries no BCRA weight.
    /// </summary>
    public static BcraData BcraDeudaLookupFailed(BcraChequesData? cheques = null) =>
        new()
        {
            Deuda = new BcraDeudaData
            {
                Estado               = BcraDeudaConsultaEstado.LookupFailed,
                HistorialSituaciones = new List<BcraSituacionPeriodo>(),
            },
            Cheques = cheques
        };

    // ── Cheques fixtures (§7.1) ───────────────────────────────────────────────

    /// <summary>
    /// A cheques block carrying rejected cheques, described by the two AMOUNTS the penalty reads —
    /// recovery is measured by amount, never by the per-cheque <c>Detalle</c> list, which the engine
    /// never touches and this fixture therefore leaves empty.
    /// <para>
    /// <paramref name="montoRechazado"/> is the total rejected amount and <paramref name="montoImpago"/>
    /// the part still unpaid, so the recovered share the engine computes is
    /// <c>(rechazado − impago) / rechazado</c>. The counts are set to something coherent with the
    /// amounts (1, or 0 when nothing is impago) purely so the fixture is not self-contradictory —
    /// scoring reads neither.
    /// </para>
    /// </summary>
    public static BcraChequesData ChequesFound(decimal montoRechazado, decimal montoImpago) =>
        new()
        {
            Estado              = BcraChequesConsultaEstado.ChequesFound,
            TotalRechazados     = 1,
            TotalImpagos        = montoImpago > 0m ? 1 : 0,
            MontoTotalRechazado = montoRechazado,
            MontoTotalImpago    = montoImpago,
            Causales            = new(),
            Detalle             = new(),
        };

    /// <summary>
    /// A cheques block with the given non-found outcome, in the exact shape
    /// <c>BcraService.BuildEmptyCheques</c> produces: empty collections and every amount left at zero.
    /// That zeroed shape is why the engine gates on <c>Estado</c> BEFORE the amounts — an outage
    /// (<see cref="BcraChequesConsultaEstado.LookupFailed"/>) is numerically indistinguishable here
    /// from a clean record (<see cref="BcraChequesConsultaEstado.NoChequesReported"/>).
    /// </summary>
    public static BcraChequesData ChequesEmpty(BcraChequesConsultaEstado estado) =>
        new()
        {
            Estado   = estado,
            Causales = new(),
            Detalle  = new(),
        };

    /// <summary>
    /// Builds a situación history.
    /// <para>
    /// WHAT MATTERS IS RELATIVE SPACING, NOT THE YEAR. The engine's trend cutoff is
    /// <c>anchor - window</c>, where the anchor is the ORDINAL MAX <c>Periodo</c> in this very list
    /// (finding D2 removed the <c>DateTime.UtcNow</c> anchor). The cutoff is therefore derived from
    /// the fixture itself, which makes every fixture date-independent by construction — no sentinel
    /// year is needed and none can rot.
    /// </para>
    /// <para>
    /// The branch a fixture lands on follows from its own span: a history reaching at least
    /// <c>window</c> months back from its newest period exercises the normal
    /// "latest period at or before the cutoff" selection, while a history spanning less than that has
    /// nothing at or before the cutoff and falls through to <c>?? historial[^1]</c>. Choose the
    /// periods for the branch the test means to pin.
    /// </para>
    /// <para>
    /// Order is deliberately NOT imposed here: the engine takes its anchor by ordinal max precisely
    /// so a list that is not newest-first still anchors correctly, and several fixtures rely on being
    /// able to shuffle entries.
    /// </para>
    /// </summary>
    public static List<BcraSituacionPeriodo> Historial(params (string Periodo, int Situacion)[] entries)
        => entries
            .Select(e => new BcraSituacionPeriodo { Periodo = e.Periodo, SituacionMaxima = e.Situacion })
            .ToList();

    // ── Macro fixtures ────────────────────────────────────────────────────────

    /// <summary>
    /// Macro present, inflation below the penalty threshold → scoreMacro 30 (base, no penalty), and
    /// deliberately carrying NO monthly CPI series. That second half is why every fixture written
    /// for §7.3a still observes its original figures under §7.3b: with no series to deflate by, the
    /// sales-drop penalty reports the nominal variation, exactly as it did.
    /// </summary>
    public static BcraMacroData MacroBenign() => new() { InflacionMensual = 5.0 };

    /// <summary>
    /// Macro payload. <paramref name="tasaReferencia"/> is TAMAR (BCRA variable 135) — scoring reads
    /// it NOWHERE; it is here only so the "inflation missing" fixture is the shape BcraService
    /// actually produces (GetMacroAsync returns null only when BOTH its variables fail).
    /// </summary>
    public static BcraMacroData Macro(double? inflacionMensual, double? tasaReferencia = null)
        => new() { InflacionMensual = inflacionMensual, TasaReferencia = tasaReferencia };

    // ── CPI fixtures (§7.3b) ──────────────────────────────────────────────────

    /// <summary>
    /// Macro carrying a monthly CPI series — the payload §7.3b deflates by.
    /// <para>
    /// <paramref name="inflacionMensual"/> stays at the benign 5.0 by default so <c>scoreMacro</c>
    /// remains 30 and the composite remains exactly 15.00, keeping every arithmetic fixture readable
    /// against the same baseline. That deliberately makes the scalar and the series inconsistent
    /// with each other: scoring reads them for unrelated purposes (the scalar is the macro
    /// component's own threshold test, the series is the sales deflator) and no code compares them.
    /// </para>
    /// </summary>
    public static BcraMacroData MacroConIpc(
        IReadOnlyDictionary<string, double> serie,
        double? inflacionMensual = 5.0)
        => new()
        {
            InflacionMensual      = inflacionMensual,
            InflacionMensualSerie = new Dictionary<string, double>(serie, StringComparer.Ordinal)
        };

    /// <summary>An explicit CPI series: period key → that month's inflation as a PERCENT (2.5 = 2.5 %).</summary>
    public static Dictionary<string, double> Ipc(params (string Periodo, double Pct)[] meses)
        => meses.ToDictionary(m => m.Periodo, m => m.Pct, StringComparer.Ordinal);

    /// <summary>
    /// A flat CPI series of <paramref name="meses"/> consecutive months ENDING at
    /// <paramref name="ultimoPeriodo"/>, every month at <paramref name="pct"/>.
    /// <para>
    /// Deflation spans are half-open — the months AFTER the comparison month up to and including the
    /// recent one — so a span from <c>P</c> to <c>P+n</c> needs exactly the <c>n</c> months ending at
    /// <c>P+n</c>, which is what this builds. Percentages here are chosen so <c>(1 + pct/100)</c> is
    /// exact in <c>decimal</c> (10 → 1.1, 25 → 1.25, −20 → 0.8), which makes the compounded factor
    /// exact too and every asserted figure reproducible to the last digit.
    /// </para>
    /// </summary>
    public static Dictionary<string, double> IpcPlano(double pct, string ultimoPeriodo, int meses)
    {
        int year  = int.Parse(ultimoPeriodo[..4]);
        int month = int.Parse(ultimoPeriodo[4..]);
        int index = (year * 12) + (month - 1);

        var serie = new Dictionary<string, double>(meses, StringComparer.Ordinal);

        for (int back = meses - 1; back >= 0; back--)
        {
            int at = index - back;
            serie[$"{at / 12:D4}{(at % 12) + 1:D2}"] = pct;
        }

        return serie;
    }

    // ── Ratio fixtures ────────────────────────────────────────────────────────

    /// <summary>
    /// All eight computable, composite exactly 21.00 — the same figure the already-green
    /// <c>Calculate_BcraIncluded_PinsCalibration_AndReasonIsNull</c> reaches independently.
    /// <code>
    ///   liquidez_corriente  1.50 = ok   →  0   (w .20)
    ///   prueba_acida        1.00 = ok   →  0   (w .10)
    ///   endeudamiento       0.70 = warn → 50   (w .20) → 10.00
    ///   leverage            1.00 = ok   →  0   (w .10)
    ///   cobertura_intereses 1.50 = warn → 50   (w .15) →  7.50
    ///   roe                15.0  = ok   →  0   (w .10)
    ///   roa                 8.0  = ok   →  0   (w .08)
    ///   margen_neto         4.0  = warn → 50   (w .07) →  3.50
    ///                                     scoreRatios = 21.00
    /// </code>
    /// No partial reaches the critical trigger (100 since v9), so no floor clamps the composite and the
    /// downstream assertions stay falsifiable.
    /// </summary>
    public static RatiosData BaselineRatios() => new()
    {
        CurrentRatio     = 1.50,
        AcidTest         = 1.00,
        DebtRatio        = 0.70,
        Leverage         = 1.00,
        InterestCoverage = 1.50,
        Roe              = 15.0,
        Roa              = 8.0,
        NetMargin        = 4.0,
    };

    /// <summary>
    /// Every ratio strictly BEYOND its danger anchor → every partial clamps to 100, every floor
    /// triggers. Used to exercise the clamps and the floor's max-selection at once.
    /// </summary>
    public static RatiosData BeyondDangerRatios() => new()
    {
        CurrentRatio     = 0.10,   // << danger 0.70
        AcidTest         = 0.05,   // << danger 0.50
        DebtRatio        = 2.00,   // >> danger 0.85
        Leverage         = 9.00,   // >> danger 5.67 (2.50 before v9)
        InterestCoverage = 0.10,   // << danger 1.00
        Roe              = -50.0,  // << danger 0
        Roa              = -20.0,  // << danger 0
        NetMargin        = -30.0,  // << danger 0
    };

    /// <summary>Every ratio comfortably better than its ok anchor → every partial 0, no floor.</summary>
    public static RatiosData HealthyRatios() => new()
    {
        CurrentRatio     = 2.00,
        AcidTest         = 1.50,
        DebtRatio        = 0.30,
        Leverage         = 0.50,
        InterestCoverage = 5.00,
        Roe              = 20.0,
        Roa              = 10.0,
        NetMargin        = 15.0,
    };

    // ── Assertion helpers ─────────────────────────────────────────────────────

    public static RatioBreakdownEntry Entry(ScoringResult r, string key)
    {
        Xunit.Assert.True(r.Breakdown.ContainsKey(key), $"breakdown must list '{key}'");
        return r.Breakdown[key];
    }
}
