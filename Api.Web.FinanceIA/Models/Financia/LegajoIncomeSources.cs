// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoIncomes].[Source] — CK_LegajoIncomes_Source (DDL 071). The document an income figure comes from.</summary>
public static class LegajoIncomeSources
{
    /// <summary>DDJJ IVA.</summary>
    public const string Iva = "Iva";

    /// <summary>Invoicing (no extractor yet; admitted by the design).</summary>
    public const string Facturacion = "Facturacion";

    /// <summary>DDJJ Ganancias (F.711/F.715).</summary>
    public const string Ganancias = "Ganancias";

    /// <summary>Certificación de ingresos.</summary>
    public const string Certificacion = "Certificacion";

    /// <summary>The manifestación de bienes' estado de resultados.</summary>
    public const string Manifestacion = "Manifestacion";

    /// <summary>DDJJ Ingresos Brutos.</summary>
    public const string Iibb = "Iibb";

    /// <summary>Monotributo payments.</summary>
    public const string Monotributo = "Monotributo";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Iva, Facturacion, Ganancias, Certificacion, Manifestacion, Iibb, Monotributo];
}
