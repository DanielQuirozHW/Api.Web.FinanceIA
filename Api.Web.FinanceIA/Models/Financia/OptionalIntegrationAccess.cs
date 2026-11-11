// Project : Api.Web.FinanceIA
// Created : 2026-10-24

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Which OPTIONAL integrations a tenant can actually use: entitled by its <see cref="TenantProfile"/>
/// AND available in this installation. Returned by
/// <c>IIntegrationAccessService.ResolveUsableAsync</c>.
/// </summary>
/// <param name="Lufe">LUFE is entitled and the tenant has a LUFE API key.</param>
/// <param name="Casfog">CASFOG is entitled and its credentials are configured.</param>
/// <param name="Nosis">NOSIS is entitled and the installation has it switched on and configured.</param>
public sealed record OptionalIntegrationAccess(bool Lufe, bool Casfog, bool Nosis);
