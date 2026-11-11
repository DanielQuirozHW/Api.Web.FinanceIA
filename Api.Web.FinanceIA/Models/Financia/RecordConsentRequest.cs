// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Body of POST /api/person-legajos/{legajoId}/consent: the AutorizacionDatos document of that legajo
/// the data-processing authorization is recorded against.
/// </summary>
public sealed record RecordConsentRequest
{
    /// <summary>A non-deleted AutorizacionDatos document of the same legajo.</summary>
    public Guid DocumentId { get; init; }
}
