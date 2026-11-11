// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Why an item of a fiador's asset breakdown was counted the way it was, or why a fiador did not count toward
/// the aggregate coverage (M10 Phase 5a-2). Stable codes; the es-AR text travels beside them.
/// </summary>
public static class FiadorAssetReasons
{
    /// <summary>Counted at the manifestación value.</summary>
    public const string ValorManifestacion = "valor_manifestacion";

    /// <summary>Counted at the Bienes Personales value: the manifestación does not list it ("solo valor fiscal").</summary>
    public const string SoloValorFiscal = "solo_valor_fiscal";

    /// <summary>The housing, without spouse consent nor inopponibility: not executable.</summary>
    public const string ViviendaSinAsentimiento = "vivienda_sin_asentimiento";

    /// <summary>Shares in, or a credit against, the solicitante (LinkedCuit = the solicitante's CUIT).</summary>
    public const string GarantiaCorrelacionada = "garantia_correlacionada";

    /// <summary>
    /// A kind with no decided haircut yet. Only on evaluations frozen before engine 0.6, which then left Deposito,
    /// Inversion, Credito and Otro uncounted; 0.6 gives each its own pf_haircut_ key.
    /// </summary>
    public const string PendienteDeDefinicion = "pendiente_de_definicion";

    /// <summary>A kind the engine does not know (none of <see cref="LegajoItemKinds.Known"/>): listed, not counted.</summary>
    public const string TipoNoReconocido = "tipo_no_reconocido";

    /// <summary>A receivable at pf_haircut_credito = 1: not executable without its documents.</summary>
    public const string NoEjecutableSinDocumentacion = "no_ejecutable_sin_documentacion";

    /// <summary>An "otro" asset at pf_haircut_otro = 1: not admitted as guarantee.</summary>
    public const string BienNoAdmitido = "bien_no_admitido";

    /// <summary>A deposit or an investment whose haircut the tenant set to 1 (100 %).</summary>
    public const string DescuentoTotal = "descuento_total";

    /// <summary>
    /// Cash at hand (<see cref="LegajoItemKinds.Efectivo"/>, engine 0.8): declared without documentary support, so
    /// never counted, whatever the haircuts say (decision 7). Bank deposits are Deposito and keep their haircut.
    /// </summary>
    public const string EfectivoSinRespaldo = "efectivo_sin_respaldo";

    /// <summary>Neither a manifestación nor a Bienes Personales value.</summary>
    public const string SinValor = "sin_valor";

    /// <summary>The spouse's asset is also in the solicitante's inventory (same match key): correlated with the debtor.</summary>
    public const string EnInventarioDelSolicitante = "en_inventario_del_solicitante";

    /// <summary>Aggregate only: the same asset (match key) is already counted for another fiador.</summary>
    public const string DuplicadoOtroFiador = "duplicado_otro_fiador";

    /// <summary>Aggregate only: the fiador's own pre-filters suggest a rejection, so the headline coverage leaves him out.</summary>
    public const string RechazoSugerido = "rechazo_sugerido";
}
