// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[Documents].[EngagementType] values — the vocabulary of CK_Documents_EngagementType
/// (DDL 070): what kind of work a contador público signed on the document, as the classification
/// read it. NULL when nothing has been read.
/// </summary>
public static class EngagementTypes
{
    /// <summary>A certificación (the accountant certifies the figures against records).</summary>
    public const string Certificacion = "Certificacion";

    /// <summary>An informe de compilación (the accountant compiled, did not verify).</summary>
    public const string Compilacion = "Compilacion";

    /// <summary>No accountant engagement on the document.</summary>
    public const string Ninguno = "Ninguno";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Certificacion, Compilacion, Ninguno];
}
