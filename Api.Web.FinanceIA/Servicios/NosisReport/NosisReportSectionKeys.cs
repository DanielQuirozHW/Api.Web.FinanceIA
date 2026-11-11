// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// The section keys of <c>NosisManagerReport.sections</c> and their statuses. Every key is always
/// reported. A key is "absent" when the report does not carry it, "unreadable" when it does but its
/// layout was not understood, "ok" otherwise.
/// </summary>
public static class NosisReportSectionKeys
{
    public const string Summary                 = "summary";
    public const string Evolucion24m            = "evolucion24m";
    public const string DeudaPorEntidad         = "deudaPorEntidad";
    public const string Atrasos                 = "atrasos";
    public const string AportesPatronales       = "aportesPatronales";
    public const string ChequesRechazadosPorMes = "chequesRechazadosPorMes";
    public const string CriteriosAceptacion     = "criteriosAceptacion";
    public const string Juicios                 = "juicios";
    public const string Sisa                    = "sisa";
    public const string Srt                     = "srt";
    public const string ReferenciasComerciales  = "referenciasComerciales";
    public const string BalancesReducidos       = "balancesReducidos";
    public const string Mav                     = "mav";
    public const string AntecedentesFiscales    = "antecedentesFiscales";
    public const string ComunicadosBcra         = "comunicadosBcra";
    public const string ComercioExterior        = "comercioExterior";
    public const string MarcasCantidad          = "marcasCantidad";

    /// <summary>Every key, in contract order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Summary, Evolucion24m, DeudaPorEntidad, Atrasos, AportesPatronales, ChequesRechazadosPorMes,
        CriteriosAceptacion, Juicios, Sisa, Srt, ReferenciasComerciales,
        BalancesReducidos, Mav, AntecedentesFiscales, ComunicadosBcra, ComercioExterior, MarcasCantidad
    ];

    public const string StatusOk         = "ok";
    public const string StatusUnreadable = "unreadable";
    public const string StatusAbsent     = "absent";
}
