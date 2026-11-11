// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoIncomes].[Concept] — CK_LegajoIncomes_Concept (DDL 071).</summary>
public static class LegajoIncomeConcepts
{
    /// <summary>Sales or total ingresos of the activity.</summary>
    public const string Ventas = "Ventas";

    /// <summary>Ganancias, third category (the activity) result.</summary>
    public const string Renta3ra = "Renta3ra";

    /// <summary>Ganancias, fourth category (employment) result.</summary>
    public const string Renta4ta = "Renta4ta";

    /// <summary>Ganancias' monto consumido: what the person spent on living.</summary>
    public const string MontoConsumido = "MontoConsumido";

    /// <summary>Ingresos certified by an accountant.</summary>
    public const string IngresoCertificado = "IngresoCertificado";

    /// <summary>Anything else (other Ganancias categories, a manifestación's result, monotributo payments).</summary>
    public const string Otro = "Otro";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Ventas, Renta3ra, Renta4ta, MontoConsumido, IngresoCertificado, Otro];
}
