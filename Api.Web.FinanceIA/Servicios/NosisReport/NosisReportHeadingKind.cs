// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// What a heading of the report opens. The data kinds feed a section parser; <see cref="Skip"/> opens a
/// section whose lines are DROPPED as they are read and never reach any parser — the personal-data
/// sections (payroll, relationship tree, cheque signers, contact data, query detail, companies,
/// homonyms), the ones the contract does not carry, and any heading the parser does not know.
/// </summary>
internal enum NosisReportHeadingKind
{
    Skip,
    Profile,
    Arca,
    MiPyme,
    Resumen,
    AportesPatronales,
    Evolucion,
    CriteriosAceptacion,
    BureauCredito,
    Atrasos,
    ChequesRechazados,
    Sisa,
    Srt,
    ReferenciasComerciales,
    Juicios,
    BalancesReducidos,
    Mav,
    AntecedentesFiscales,
    ComunicadosBcra,
    Marcas,
    ComercioExterior,
    Importaciones,
    Exportaciones,
}
