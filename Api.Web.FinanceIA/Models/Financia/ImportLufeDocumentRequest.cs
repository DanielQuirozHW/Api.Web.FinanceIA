// Project : Api.Web.FinanceIA
// Created : 2026-06-15

namespace Api.Web.FinanceIA.Models.Financia;

public sealed class ImportLufeDocumentRequest
{
    /// <summary>Zero-based index into the entity's LufeData.Documentos array.</summary>
    public int DocIndex { get; set; }
}
