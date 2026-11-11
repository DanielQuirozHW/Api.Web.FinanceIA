// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Outcome of one persona humana pre-filter (M10 Phase 4a).</summary>
public static class PersonFilterOutcomes
{
    /// <summary>The filter fires a suggested rejection.</summary>
    public const string Rechazo = "Rechazo";

    /// <summary>The filter raises an alert for the analyst; see <see cref="PersonSeverities"/>.</summary>
    public const string Alerta = "Alerta";

    /// <summary>The source answered and the filter does not fire.</summary>
    public const string Ok = "Ok";

    /// <summary>The source is missing or failed. Never read as <see cref="Ok"/>.</summary>
    public const string SinDatos = "SinDatos";

    /// <summary>Every outcome.</summary>
    public static readonly IReadOnlyList<string> Known = [Rechazo, Alerta, Ok, SinDatos];
}
