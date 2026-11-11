// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The enum vocabularies and field pointers of the legajo extraction schemas (Phase 2b, extended in 3a), named once: the schema
/// trees, the integrity checks and the review read model all route on them, and a typo inside a string
/// literal is invisible to the compiler.
/// </summary>
public static class LegajoExtractionVocabulary
{
    // ── amountsUnit ─────────────────────────────────────────────────────────────
    public const string Pesos    = "Pesos";
    public const string Miles    = "Miles";
    public const string Millones = "Millones";

    /// <summary>The printed units, and the factor that brings each to pesos.</summary>
    public static readonly IReadOnlyList<string> AmountsUnits = [Pesos, Miles, Millones];

    // ── location (BP) ───────────────────────────────────────────────────────────
    public const string Pais     = "Pais";
    public const string Exterior = "Exterior";

    public static readonly IReadOnlyList<string> Locations = [Pais, Exterior];

    // ── rubro (BP) ──────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> Rubros =
    [
        "Inmuebles", "Rodados", "DineroEfectivo", "Depositos", "TitulosYAcciones", "Creditos",
        "BienesMueblesRegistrables", "OtrosBienes", "Otro"
    ];

    // ── matchKey.kind (BP and MB) ───────────────────────────────────────────────
    public static readonly IReadOnlyList<string> MatchKeyKinds = ["Partida", "Dominio", "Cuit", "Cbu", "Otro", "Ninguno"];

    // ── section (MB) ────────────────────────────────────────────────────────────
    public const string ActivoCorriente   = "ActivoCorriente";
    public const string ActivoNoCorriente = "ActivoNoCorriente";
    public const string PasivoCorriente   = "PasivoCorriente";
    public const string PasivoNoCorriente = "PasivoNoCorriente";

    public static readonly IReadOnlyList<string> Sections =
        [ActivoCorriente, ActivoNoCorriente, PasivoCorriente, PasivoNoCorriente];

    // ── annex kind (MB) ─────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> AnnexKinds =
        ["Inmueble", "Rodado", "Participacion", "Deposito", "Inversion", "Credito", "Deuda", "Otro"];

    // ── investmentClass (MB annex Inversion items, BP TitulosYAcciones items) — mb-1.3 / bp-1.1 ─
    /// <summary>The classes of an investment; the extraction may also leave it null.</summary>
    public static readonly IReadOnlyList<string> InvestmentClasses = LegajoInvestmentClasses.Known;

    // ── saldo side (Ganancias totals, IVA saldo técnico) — Phase 3a ─────────────
    /// <summary>A saldo a favor del contribuyente / del responsable.</summary>
    public const string Contribuyente = "Contribuyente";

    /// <summary>A saldo a favor de AFIP/ARCA: the taxpayer owes it.</summary>
    public const string Fisco = "Fisco";

    public static readonly IReadOnlyList<string> SaldoSides = [Contribuyente, Fisco];

    // ── Ganancias ───────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> GananciasCategories = ["Primera", "Segunda", "Tercera", "Cuarta"];

    /// <summary>Fuente argentina (fuente extranjera is <see cref="Exterior"/>).</summary>
    public const string Argentina = "Argentina";

    public static readonly IReadOnlyList<string> IncomeSources = [Argentina, Exterior];

    /// <summary>A justification line that justifies erogaciones or patrimonial increases (ingresos, orígenes).</summary>
    public const string Origen = "Origen";

    /// <summary>A justification line that does not (erogaciones, aplicaciones).</summary>
    public const string Aplicacion = "Aplicacion";

    public static readonly IReadOnlyList<string> JustificationSides = [Origen, Aplicacion];

    // ── DDJJ IVA ────────────────────────────────────────────────────────────────
    public const string Debito  = "Debito";
    public const string Credito = "Credito";

    public static readonly IReadOnlyList<string> IvaSides = [Debito, Credito];

    // ── DDJJ Ingresos Brutos ────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> IibbRegimes = ["ConvenioMultilateral", "Local"];

    // ── Monotributo ─────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<string> MonotributoCategories = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K"];

    public static readonly IReadOnlyList<string> MonotributoActivities = ["Servicios", "VentaDeBienes"];

    // ── Pointers every type shares ──────────────────────────────────────────────

    /// <summary>The holder CUIT the extraction read. Never returned; never correctable.</summary>
    public const string HolderCuitPath = "/header/holderCuit";

    /// <summary>The printed unit of the amounts.</summary>
    public const string AmountsUnitPath = "/header/amountsUnit";

    /// <summary>The factor to pesos of a canonical unit; null when the unit is not one of <see cref="AmountsUnits"/>.</summary>
    public static decimal? Factor(string? unit) => unit switch
    {
        Pesos    => 1m,
        Miles    => 1_000m,
        Millones => 1_000_000m,
        _        => null
    };
}
