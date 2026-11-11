// Project : Api.Web.FinanceIA
// Created : 2026-11-05

namespace Api.Web.FinanceIA.Servicios.Privacy;

/// <summary>
/// What identifies the evaluated company, as <see cref="EntityIdentifierRedactor"/> looks for it. Values
/// are taken as stored — blank, duplicate and malformed entries are dropped by the redactor, not here.
/// </summary>
/// <param name="CompanyNames">
/// The company's own names: <c>Entities.BusinessName</c> (the ARCA razón social), <c>TradeName</c>, and the
/// LUFE registry name. Replaced by "la empresa".
/// </param>
/// <param name="RelatedCompanyNames">
/// Legal persons among its LUFE authorities/partners (CUIT prefix 30/33/34). Same matching rules as a
/// company name, replaced by "[persona vinculada]".
/// </param>
/// <param name="PersonNames">Natural persons among its LUFE authorities/partners. Replaced by "[persona vinculada]".</param>
/// <param name="Cuits">The company's CUIT and those of its authorities, in any format. Replaced by "[CUIT]".</param>
public sealed record EntityIdentifiers(
    IReadOnlyList<string> CompanyNames,
    IReadOnlyList<string> RelatedCompanyNames,
    IReadOnlyList<string> PersonNames,
    IReadOnlyList<string> Cuits);
