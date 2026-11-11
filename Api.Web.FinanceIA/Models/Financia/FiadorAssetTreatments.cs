// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>What the fiador evaluation did with one item of the asset breakdown (M10 Phase 5a-2).</summary>
public static class FiadorAssetTreatments
{
    /// <summary>Counted in the executable patrimony, after its haircut.</summary>
    public const string Computado = "Computado";

    /// <summary>Left out by a rule (housing without consent, correlated collateral, no value…); see the reason.</summary>
    public const string Excluido = "Excluido";

    /// <summary>A kind with no haircut: listed, not counted. From engine 0.6 only an unknown kind.</summary>
    public const string NoComputado = "NoComputado";

    /// <summary>Every value.</summary>
    public static readonly IReadOnlyList<string> Known = [Computado, Excluido, NoComputado];
}
