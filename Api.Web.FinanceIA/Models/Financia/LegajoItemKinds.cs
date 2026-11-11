// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// [dbo].[LegajoItems].[Kind] — CK_LegajoItems_Kind (DDL 071, widened by 079). The manifestación's annex kinds,
/// onto which a Bienes Personales rubro is mapped, plus <see cref="Efectivo"/>, which only Bienes Personales
/// produces.
/// </summary>
public static class LegajoItemKinds
{
    /// <summary>Real estate.</summary>
    public const string Inmueble = "Inmueble";

    /// <summary>A vehicle.</summary>
    public const string Rodado = "Rodado";

    /// <summary>Shares or a stake in a company.</summary>
    public const string Participacion = "Participacion";

    /// <summary>A bank deposit or account.</summary>
    public const string Deposito = "Deposito";

    /// <summary>
    /// Cash at hand: the Bienes Personales rubro DineroEfectivo (DDL 079, engine 0.8). Not a manifestación annex
    /// kind. A legajo reconciled before 079 still holds its cash as <see cref="Otro"/> until it is rebuilt.
    /// </summary>
    public const string Efectivo = "Efectivo";

    /// <summary>Securities, funds and other investments.</summary>
    public const string Inversion = "Inversion";

    /// <summary>A receivable.</summary>
    public const string Credito = "Credito";

    /// <summary>A debt (the only Pasivo kind).</summary>
    public const string Deuda = "Deuda";

    /// <summary>Anything else (registrable movables, household goods …).</summary>
    public const string Otro = "Otro";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Inmueble, Rodado, Participacion, Deposito, Efectivo, Inversion, Credito, Deuda, Otro];
}
