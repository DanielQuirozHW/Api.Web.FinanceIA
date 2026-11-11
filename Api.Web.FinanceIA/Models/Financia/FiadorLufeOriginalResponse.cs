// Project : Api.Web.FinanceIA
// Created : 2026-10-23

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What LUFE CURRENTLY reports for a fiador's CUIT, read from the entity's persisted LUFE snapshot —
/// shown beside a <c>Lufe</c> fiador so the analyst can see what was corrected. Same PII rule as
/// <see cref="FiadorResponse"/>.
/// </summary>
public sealed class FiadorLufeOriginalResponse
{
    /// <summary>Name as LUFE informs it, or null when it does not.</summary>
    public string? Denominacion { get; set; }

    /// <summary>Role as LUFE informs it, or null when it does not.</summary>
    public string? Cargo { get; set; }

    /// <summary>Shareholding percentage as LUFE informs it, or null when it does not.</summary>
    public decimal? PorcAccionista { get; set; }
}
