// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// Everything <see cref="PersonEvaluationService.Evaluate"/> reads, already loaded and parsed. Holds no
/// identity: no name, no CUIT.
/// </summary>
public sealed record PersonEvaluationInput
{
    /// <summary>The evaluation instant (UTC). Every age is measured to it.</summary>
    public required DateTime AsOfUtc { get; init; }

    /// <summary>The legajo's regime (<see cref="FiscalRegimes"/>).</summary>
    public required string FiscalRegime { get; init; }

    /// <summary>The monotributo category letter, when known.</summary>
    public string? MonotributoCategory { get; init; }

    /// <summary>PersonLegajo.ActivityStartDate, when written.</summary>
    public DateTime? ActivityStartDate { get; init; }

    /// <summary>The legajo's BCRA snapshot, parsed; null when absent or unreadable.</summary>
    public BcraData? Bcra { get; init; }

    /// <summary>Its stamp.</summary>
    public DateTime? BcraUpdatedAt { get; init; }

    /// <summary>The legajo's ARCA snapshot, parsed; null when absent or unreadable.</summary>
    public ArcaFiscalData? Arca { get; init; }

    /// <summary>Its stamp.</summary>
    public DateTime? ArcaUpdatedAt { get; init; }

    /// <summary>The entity's LUFE snapshot, parsed (the MiPyME certificate); null when absent or unreadable.</summary>
    public LufeData? Lufe { get; init; }

    /// <summary>Its stamp.</summary>
    public DateTime? LufeUpdatedAt { get; init; }

    /// <summary>The reconciled inventory (#253 read model).</summary>
    public required LegajoInventoryResponse Inventory { get; init; }

    /// <summary>The reconciled incomes by period (#253 read model).</summary>
    public required LegajoIncomesResponse Incomes { get; init; }

    /// <summary>The legajo's live documents.</summary>
    public required IReadOnlyList<PersonEvaluationDocument> Documents { get; init; }

    /// <summary>Monthly CPI variation by YYYYMM, in percent (2.5 = 2.5 %); null when unavailable.</summary>
    public IReadOnlyDictionary<string, double>? InflationSeries { get; init; }

    /// <summary>The thresholds.</summary>
    public required PersonEvaluationParameters Parameters { get; init; }

    /// <summary>
    /// The expected documents of the legajo's regime as the tenant configured them (engine 0.10,
    /// IPersonExpectedDocumentsService), each line with its optional recency cut. Null — a caller that does
    /// not state them — reads the code default <see cref="LegajoExpectedDocuments.For"/>.
    /// </summary>
    public IReadOnlyList<LegajoExpectedDocument>? ExpectedDocuments { get; init; }

    /// <summary>The requested amount.</summary>
    public required decimal RequestedAmount { get; init; }

    /// <summary>ISO code of the requested amount and the cuota.</summary>
    public required string Currency { get; init; }

    /// <summary>The cuota of the requested operation, when stated.</summary>
    public decimal? InstallmentAmount { get; init; }

    /// <summary>
    /// The monthly cuotas vigentes the person already pays, declared by the analyst (engine 0.8). When present
    /// — zero included — it replaces the estimate from the Central de Deudores everywhere the estimate is read;
    /// null estimates, as before.
    /// </summary>
    public decimal? CuotasVigentesDeclaradas { get; init; }

    /// <summary>
    /// The solicitante's agro annual mode and the cuotas a year of the cuota (engine 0.7,
    /// <see cref="PersonAgroModeResolver"/>). Null — every fiador, and any caller that does not state it — runs
    /// the 0.6 formulas: a monthly cuota, the last 12 months of sales, the 6-month recency of the monthly DDJJ.
    /// </summary>
    public PersonAgroMode? Agro { get; init; }

    /// <summary>
    /// Where the analyst refreshes this person's BCRA snapshot, completing "Actualizá BCRA …" in the SinDatos
    /// texts: the entity for the solicitante, the fiador's legajo for a fiador.
    /// </summary>
    public string BcraRefreshTarget { get; init; } = "en la entidad";
}
