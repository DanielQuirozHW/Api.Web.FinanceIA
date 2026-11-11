// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The SGR Fondo de Riesgo settings of a tenant, parsed from its TenantSettings rows
/// (<see cref="TenantSettingKeys.SgrFondoRiesgoDisponible"/> and siblings). A value that is absent, does
/// not parse, or is not positive is null — "not configured" — never a guess.
/// </summary>
/// <param name="FondoRiesgoDisponible">Fondo de Riesgo disponible, ARS; null when not configured.</param>
/// <param name="FondoRiesgoFecha">The date it is stated at.</param>
/// <param name="TipoCambioUsd">ARS per USD; null when not set.</param>
/// <param name="TipoCambioFecha">The date the rate is stated at.</param>
public sealed record SgrFundSettings(
    decimal? FondoRiesgoDisponible, DateOnly? FondoRiesgoFecha, decimal? TipoCambioUsd, DateOnly? TipoCambioFecha)
{
    /// <summary>The stored date format: an ISO calendar date.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>The four keys, in the order they are read and written.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        TenantSettingKeys.SgrFondoRiesgoDisponible,
        TenantSettingKeys.SgrFondoRiesgoFecha,
        TenantSettingKeys.SgrTipoCambioUsd,
        TenantSettingKeys.SgrTipoCambioFecha
    ];

    // Invariant decimal with an optional point, no thousands separator: "1500000000.50".
    private const NumberStyles AmountStyle =
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    /// <summary>Nothing configured.</summary>
    public static SgrFundSettings Empty { get; } = new(null, null, null, null);

    /// <summary>
    /// Parses the stored rows. Returns the settings and the keys whose stored value was present but
    /// unusable, so the caller can log them (by key — the value is operator input of unknown shape).
    /// </summary>
    public static (SgrFundSettings Settings, IReadOnlyList<string> InvalidKeys) Parse(IReadOnlyDictionary<string, string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var invalid = new List<string>();

        decimal? Amount(string key)
        {
            if (!rows.TryGetValue(key, out var raw)) return null;
            if (decimal.TryParse(raw, AmountStyle, CultureInfo.InvariantCulture, out var value) && value > 0m) return value;
            invalid.Add(key);
            return null;
        }

        DateOnly? Date(string key)
        {
            if (!rows.TryGetValue(key, out var raw)) return null;
            if (DateOnly.TryParseExact(raw.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) return value;
            invalid.Add(key);
            return null;
        }

        var settings = new SgrFundSettings(
            Amount(TenantSettingKeys.SgrFondoRiesgoDisponible),
            Date(TenantSettingKeys.SgrFondoRiesgoFecha),
            Amount(TenantSettingKeys.SgrTipoCambioUsd),
            Date(TenantSettingKeys.SgrTipoCambioFecha));

        return (settings, invalid);
    }

    /// <summary>
    /// The validation errors of a PUT, in Spanish for the Portal; empty when it may be saved.
    /// <paramref name="today"/> is the Argentine calendar date no stated date may be after.
    /// </summary>
    public static IReadOnlyList<string> Validate(UpdateSgrSettingsRequest request, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        if (request.FondoRiesgoDisponible is not { } fondo || fondo <= 0m)
            errors.Add("fondoRiesgoDisponible es obligatorio y debe ser mayor a cero.");

        if (request.FondoRiesgoFecha is not { } fondoFecha)
            errors.Add("fondoRiesgoFecha es obligatoria.");
        else if (fondoFecha > today)
            errors.Add("fondoRiesgoFecha no puede ser posterior a hoy.");

        if (request.TipoCambioUsd.HasValue != request.TipoCambioFecha.HasValue)
        {
            errors.Add("tipoCambioUsd y tipoCambioFecha se informan juntos; envialos ambos vacíos para quitar el tipo de cambio.");
        }
        else if (request.TipoCambioUsd is { } rate)
        {
            if (rate <= 0m)
                errors.Add("tipoCambioUsd debe ser mayor a cero.");

            if (request.TipoCambioFecha > today)
                errors.Add("tipoCambioFecha no puede ser posterior a hoy.");
        }

        return errors;
    }

    /// <summary>A validated request, as the settings it stores.</summary>
    public static SgrFundSettings From(UpdateSgrSettingsRequest request)
        => new(request.FondoRiesgoDisponible, request.FondoRiesgoFecha, request.TipoCambioUsd, request.TipoCambioFecha);

    /// <summary>The rows to write: every key, null for a value to remove.</summary>
    public IReadOnlyDictionary<string, string?> ToRows() => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [TenantSettingKeys.SgrFondoRiesgoDisponible] = FondoRiesgoDisponible?.ToString(CultureInfo.InvariantCulture),
        [TenantSettingKeys.SgrFondoRiesgoFecha]      = FondoRiesgoFecha?.ToString(DateFormat, CultureInfo.InvariantCulture),
        [TenantSettingKeys.SgrTipoCambioUsd]         = TipoCambioUsd?.ToString(CultureInfo.InvariantCulture),
        [TenantSettingKeys.SgrTipoCambioFecha]       = TipoCambioFecha?.ToString(DateFormat, CultureInfo.InvariantCulture)
    };

    /// <summary>The settings as the endpoints return them.</summary>
    public SgrSettingsDto ToDto() => new()
    {
        FondoRiesgoDisponible = FondoRiesgoDisponible,
        FondoRiesgoFecha      = FondoRiesgoFecha,
        TipoCambioUsd         = TipoCambioUsd,
        TipoCambioFecha       = TipoCambioFecha
    };
}
