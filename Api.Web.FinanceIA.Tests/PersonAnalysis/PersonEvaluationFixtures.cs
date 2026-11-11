// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Builders for persona humana evaluation inputs. Incomes and items go through the real #253 read model
/// (<see cref="LegajoReconciliationReadModel"/>), so every indicator is fed exactly what production feeds it.
/// </summary>
internal static class PersonEvaluationFixtures
{
    public const string Tenant = "tenant-person";

    /// <summary>The evaluation instant: 2026-10-20 12:00 ART.</summary>
    public static readonly DateTime AsOfUtc = new(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);

    public static readonly Guid LegajoId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>An input with nothing in it but the regime: every source missing.</summary>
    public static PersonEvaluationInput Empty(string regime = FiscalRegimes.ResponsableInscripto) => new()
    {
        AsOfUtc         = AsOfUtc,
        FiscalRegime    = regime,
        Inventory       = Inventory(),
        Incomes         = Incomes(),
        Documents       = [],
        Parameters      = PersonEvaluationParameters.Defaults,
        RequestedAmount = 1_000_000m,
        Currency        = "ARS"
    };

    // ── BCRA ──────────────────────────────────────────────────────────────────────

    public static BcraData Bcra(BcraDeudaData? deuda = null, BcraChequesData? cheques = null, BcraMacroData? macro = null)
        => new() { Deuda = deuda, Cheques = cheques, Macro = macro };

    /// <summary>A Central de Deudores answer; history entries are (months back from 202609, situación).</summary>
    public static BcraDeudaData Deuda(
        BcraDeudaConsultaEstado estado = BcraDeudaConsultaEstado.DebtFound,
        int? situacion = 1,
        IEnumerable<(int MonthsBack, int Situacion)>? history = null,
        IEnumerable<BcraEntidadDeudaDetalle>? detalle = null,
        decimal? montoTotal = 100m)
    {
        var reference = new DateOnly(2026, 9, 1);
        return new BcraDeudaData
        {
            Estado               = estado,
            SituacionMaxima      = situacion,
            MontoTotal           = montoTotal,
            PeriodoConsultado    = "202609",
            HistorialSituaciones = (history ?? [])
                .Select(h => new BcraSituacionPeriodo
                {
                    Periodo         = reference.AddMonths(-h.MonthsBack).ToString("yyyyMM"),
                    SituacionMaxima = h.Situacion
                })
                .ToList(),
            EntidadesDetalle     = (detalle ?? []).ToList()
        };
    }

    public static BcraChequesData Cheques(
        BcraChequesConsultaEstado estado = BcraChequesConsultaEstado.ChequesFound,
        int rechazados = 0, int impagos = 0, decimal montoImpago = 0m)
        => new()
        {
            Estado              = estado,
            TotalRechazados     = rechazados,
            TotalImpagos        = impagos,
            MontoTotalImpago    = montoImpago,
            MontoTotalRechazado = montoImpago
        };

    /// <summary>A CPI series of <paramref name="months"/> months ending at 202609, every month at <paramref name="rate"/> %.</summary>
    public static Dictionary<string, double> Cpi(int months = 26, double rate = 0d)
    {
        var series = new Dictionary<string, double>();
        var end = new DateOnly(2026, 9, 1);
        for (var i = 0; i < months; i++)
            series[end.AddMonths(-i).ToString("yyyyMM")] = rate;
        return series;
    }

    // ── ARCA ──────────────────────────────────────────────────────────────────────

    public static ArcaFiscalData Arca(bool? esSucesion = false, params string[] impuestoPeriodos)
        => new(
            FechaContratoSocial: null,
            EsSucesion:          esSucesion,
            MesCierre:           null,
            Actividades:         [],
            Impuestos:           impuestoPeriodos.Select(p => new ArcaImpuesto("30", "IVA", "AC", null, p)).ToList(),
            Regimenes:           []);

    /// <summary>A snapshot carrying ARCA's key status, read 10 days before <see cref="AsOfUtc"/>.</summary>
    public static ArcaFiscalData ArcaWithKey(string? estadoClave)
        => Arca(false, "201901") with { EstadoClave = estadoClave, EstadoClaveConsultadoAt = AsOfUtc.AddDays(-10) };

    /// <summary>An evaluation instant in 2026, mid-day in Argentina (15:00 UTC).</summary>
    public static DateTime On(int month, int day) => new(2026, month, day, 15, 0, 0, DateTimeKind.Utc);

    // ── Incomes (#253 read model) ────────────────────────────────────────────────

    public static LegajoIncome Income(
        string source, string concept, DateOnly from, DateOnly to, decimal amount, string? period = null, Guid? documentId = null)
        => new()
        {
            IncomeId       = Guid.NewGuid(),
            TenantId       = Tenant,
            PersonLegajoId = LegajoId,
            Period         = period,
            PeriodFrom     = from,
            PeriodTo       = to,
            Source         = source,
            Concept        = concept,
            Amount         = amount,
            Currency       = "ARS",
            DocumentId     = documentId ?? Guid.NewGuid(),
            ExtractionId   = Guid.NewGuid()
        };

    /// <summary>One monthly IVA sales row per month, from <paramref name="first"/> for <paramref name="count"/> months.</summary>
    public static IEnumerable<LegajoIncome> MonthlyIva(DateOnly first, int count, Func<int, decimal> amount, Guid? documentId = null)
        => Enumerable.Range(0, count).Select(i =>
        {
            var from = first.AddMonths(i);
            return Income(LegajoIncomeSources.Iva, LegajoIncomeConcepts.Ventas, from, from.AddMonths(1).AddDays(-1),
                amount(i), from.ToString("yyyy-MM"), documentId);
        });

    /// <summary>A calendar-year row.</summary>
    public static LegajoIncome Year(string source, string concept, int year, decimal amount, Guid? documentId = null)
        => Income(source, concept, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), amount, year.ToString(), documentId);

    public static LegajoIncomesResponse Incomes(params LegajoIncome[] rows)
        => LegajoReconciliationReadModel.BuildIncomes(LegajoId, rows);

    public static LegajoIncomesResponse Incomes(IEnumerable<LegajoIncome> rows)
        => LegajoReconciliationReadModel.BuildIncomes(LegajoId, rows.ToList());

    // ── Inventory (#253 read model) ──────────────────────────────────────────────

    public static LegajoItem Item(string side, string kind, decimal? market, bool activity = false, Guid? documentId = null)
        => new()
        {
            ItemId                = Guid.NewGuid(),
            TenantId              = Tenant,
            PersonLegajoId        = LegajoId,
            ItemKey               = Guid.NewGuid().ToString("N"),
            Side                  = side,
            Kind                  = kind,
            MarketValue           = market,
            MarketValueDocumentId = documentId,
            IsActivityAsset       = activity,
            UpdatedAt             = AsOfUtc.AddDays(-3)
        };

    public static LegajoInventoryResponse Inventory(params LegajoItem[] items)
        => LegajoReconciliationReadModel.BuildInventory(LegajoId, items, new Dictionary<Guid, string>());

    // ── Documents ────────────────────────────────────────────────────────────────

    public static PersonEvaluationDocument Doc(string type, string status = LegajoStatuses.Ready, DateOnly? periodEnd = null, Guid? id = null)
        => new(id ?? Guid.NewGuid(), type, status, periodEnd);

    public static PersonEvaluationParameters With(Action<Dictionary<string, decimal>> set)
    {
        var rows = new Dictionary<string, decimal>();
        set(rows);
        return PersonEvaluationParameters.From(rows);
    }
}
