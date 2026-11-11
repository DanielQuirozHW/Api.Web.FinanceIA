// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Alerts on one asset of a fiador (M10 Phase 5a-2). The first three are read from the reconciliation as it
/// is (<see cref="LegajoItemConflictReasons"/>, <see cref="LegajoReconciliationStatuses"/>); the last is new.
/// </summary>
public static class FiadorAssetAlerts
{
    /// <summary>The manifestación value is below the Bienes Personales value, its floor.</summary>
    public const string BelowFiscalFloor = LegajoItemConflictReasons.BelowFiscalFloor;

    /// <summary>Only the manifestación lists the asset.</summary>
    public const string OnlyInManifestacion = LegajoReconciliationStatuses.OnlyInManifestacion;

    /// <summary>Only Bienes Personales lists the asset.</summary>
    public const string OnlyInBienesPersonales = LegajoReconciliationStatuses.OnlyInBienesPersonales;

    /// <summary>The manifestación value is more than pf_ratio_mercado_fiscal_alerta times the fiscal value.</summary>
    public const string MercadoMuySuperiorAlFiscal = "MercadoMuySuperiorAlFiscal";
}
