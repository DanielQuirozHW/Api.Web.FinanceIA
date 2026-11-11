// Project : Api.Web.FinanceIA
// Created : 2026-10-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// [dbo].[LegajoItems].[InvestmentClass] — CK_LegajoItems_InvestmentClass (DDL 080, engine 0.9). The class of an
/// <see cref="LegajoItemKinds.Inversion"/> item, which picks its fiador haircut. Only an Inversion row may carry one;
/// NULL means "not classified" (every item reconciled before 080, or a document that gives no hint) and takes the
/// same haircut as <see cref="Otra"/>, pf_haircut_inversion.
/// </summary>
public static class LegajoInvestmentClasses
{
    /// <summary>Títulos públicos: bonos, letras, títulos de deuda soberana, provincial o municipal (pf_haircut_titulo_publico).</summary>
    public const string TituloPublico = "TituloPublico";

    /// <summary>Acciones con cotización: the document prints an exchange, a ticker or a market price (pf_haircut_accion_on).</summary>
    public const string AccionCotizante = "AccionCotizante";

    /// <summary>Obligaciones negociables con cotización (pf_haircut_accion_on).</summary>
    public const string ObligacionNegociable = "ObligacionNegociable";

    /// <summary>Cuotapartes de fondos comunes de inversión (pf_haircut_fci).</summary>
    public const string CuotaparteFci = "CuotaparteFci";

    /// <summary>An investment the document does not let one classify (pf_haircut_inversion).</summary>
    public const string Otra = "Otra";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [TituloPublico, AccionCotizante, ObligacionNegociable, CuotaparteFci, Otra];

    /// <summary>
    /// The classes that say WHAT the investment is: every one of <see cref="Known"/> except <see cref="Otra"/>. Otra is
    /// a doubt ("fits none of the four, or cannot tell"), and a doubt must never make the fiador coverage less
    /// conservative — so it never promotes a Bienes Personales row with a CUIT out of Participacion (0,60) into an
    /// unclassified Inversion (0,30).
    /// </summary>
    public static readonly IReadOnlyList<string> Concrete = [TituloPublico, AccionCotizante, ObligacionNegociable, CuotaparteFci];

    /// <summary>True when <paramref name="value"/> is one of <see cref="Concrete"/> (exact, ordinal); false for Otra, null and anything else.</summary>
    public static bool IsConcrete(string? value) => value is not null && Concrete.Contains(value);

    /// <summary>
    /// <paramref name="value"/> when it is one of <see cref="Known"/> (exact, ordinal) and <paramref name="kind"/> is
    /// <see cref="LegajoItemKinds.Inversion"/>; null otherwise. A stored value this code does not know reads as
    /// unclassified, never as an error.
    /// </summary>
    public static string? Normalize(string? kind, string? value)
        => kind == LegajoItemKinds.Inversion && value is not null && Known.Contains(value) ? value : null;
}
