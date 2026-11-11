// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What the NOSIS Manager report import needs to know about the entity before reading the file: its
/// CUIT (compared with the report's, never logged) and its person type (companies only). A repository
/// projection that names no <c>nvarchar(max)</c> column.
/// </summary>
public sealed class NosisReportSubject
{
    public Guid EntityId { get; set; }

    /// <summary>As stored, <c>XX-XXXXXXXX-X</c>. PII: compare only.</summary>
    public string CUIT { get; set; } = string.Empty;

    /// <summary><see cref="PersonTypes.Juridica"/> or <see cref="PersonTypes.Humana"/>.</summary>
    public string PersonType { get; set; } = PersonTypes.Juridica;
}
