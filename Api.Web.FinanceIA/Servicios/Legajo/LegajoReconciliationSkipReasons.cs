// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>Why an approved document contributed nothing to a rebuild. Stable strings the Portal may branch on.</summary>
public static class LegajoReconciliationSkipReasons
{
    /// <summary>Another approved document of the legajo names it in SupersedesDocumentId.</summary>
    public const string Superseded = "superseded";

    /// <summary>Its amountsUnit is missing or unknown, so no figure can be brought to pesos.</summary>
    public const string AmountsUnitMissing = "amounts_unit_missing";

    /// <summary>A newer Bienes Personales or manifestación is the inventory's snapshot.</summary>
    public const string OlderSnapshot = "older_snapshot";

    /// <summary>Every figure it holds was replaced by a rectificativa or a newer document.</summary>
    public const string Replaced = "replaced";

    /// <summary>It holds no figure the reconciliation reads.</summary>
    public const string NoFigures = "no_figures";
}
