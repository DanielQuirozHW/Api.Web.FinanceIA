// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The es-AR wording for the scoring-algorithm-10 and -11 cases, shared by the analyst chat, the scoring
/// narrative prompt and the PDF so the three surfaces cannot drift apart (their ratio glossaries
/// already diverged once). Display text only — never persisted, never compared.
/// </summary>
public static class ScoringNoticeTexts
{
    /// <summary>
    /// How a ratio carrying <see cref="RatioExclusionReasons.NoCurrentLiabilities"/> reads in place of
    /// "n/d" / "no aplica": no current liabilities, so liquidity is at its best.
    /// </summary>
    public const string NoCurrentLiabilities = "Sin pasivo corriente: liquidez máxima";

    /// <summary>
    /// The sentence that explains <see cref="ScoringResult.ProfitabilityFloorWaived"/>: ROE, ROA or margen
    /// neto reached danger, but the operating result is positive, so the loss arises from items below the
    /// operating result — the rule knows only that much, not which of them — and the profitability floor
    /// was not applied.
    /// </summary>
    public const string ProfitabilityFloorWaived =
        "No se aplicó el piso de rentabilidad: el resultado operativo es positivo; la pérdida surge de " +
        "partidas posteriores al resultado operativo (resultados financieros y por tenencia, incluido " +
        "RECPAM, u otros egresos).";

    /// <summary>
    /// The sentence that explains <see cref="ScoringResult.AcidTestFloorWaived"/> (algorithm version 11):
    /// prueba ácida reached danger, but liquidez corriente is not below its warning threshold — "por encima"
    /// here includes sitting exactly on it, since the rule is a strict "below" — so the stock covers the
    /// current liabilities and the acid-test floor was not applied.
    /// </summary>
    public const string AcidTestFloorWaived =
        "Piso por prueba ácida no aplicado: la liquidez corriente está por encima del umbral de atención, " +
        "así que el stock cubre el pasivo corriente.";
}
