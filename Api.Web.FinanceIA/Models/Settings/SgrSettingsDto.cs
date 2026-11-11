// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// The SGR Fondo de Riesgo settings of a tenant, as GET/PUT /api/bff/settings/sgr return them. Every field
/// is null while it is not configured. The portfolio dashboard's sgr section reads the same values.
/// </summary>
public sealed record SgrSettingsDto
{
    /// <summary>Fondo de Riesgo disponible, in ARS; null when not configured.</summary>
    public required decimal? FondoRiesgoDisponible { get; init; }

    /// <summary>The date the Fondo de Riesgo is stated at.</summary>
    public required DateOnly? FondoRiesgoFecha { get; init; }

    /// <summary>ARS per USD the dashboard converts USD guarantees with; null when not set (USD is then excluded).</summary>
    public required decimal? TipoCambioUsd { get; init; }

    /// <summary>The date the exchange rate is stated at.</summary>
    public required DateOnly? TipoCambioFecha { get; init; }
}

/// <summary>
/// Request body of PUT /api/bff/settings/sgr. Replaces all four values: <see cref="FondoRiesgoDisponible"/>
/// and <see cref="FondoRiesgoFecha"/> are required; <see cref="TipoCambioUsd"/> and
/// <see cref="TipoCambioFecha"/> go together, and sending both null removes the exchange rate.
/// </summary>
public sealed record UpdateSgrSettingsRequest
{
    /// <summary>Fondo de Riesgo disponible, in ARS. Greater than zero.</summary>
    public decimal? FondoRiesgoDisponible { get; init; }

    /// <summary>The date it is stated at (yyyy-MM-dd). Not in the future (Argentine calendar).</summary>
    public DateOnly? FondoRiesgoFecha { get; init; }

    /// <summary>ARS per USD. Greater than zero, or null together with <see cref="TipoCambioFecha"/>.</summary>
    public decimal? TipoCambioUsd { get; init; }

    /// <summary>The date the rate is stated at (yyyy-MM-dd). Not in the future (Argentine calendar).</summary>
    public DateOnly? TipoCambioFecha { get; init; }
}
