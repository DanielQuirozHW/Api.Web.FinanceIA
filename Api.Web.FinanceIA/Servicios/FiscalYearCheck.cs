// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Globalization;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Compares the fiscal year the extraction read off the document (<c>fiscal_year_detected</c>) with
/// the fiscal year the analysis was requested for (<c>Analysis.FiscalYear</c>). Pure and
/// deterministic; a FLAG for the analyst that never moves the score.
/// </summary>
/// <remarks>
/// <para>
/// Convention: a fiscal year is identified by the year of its CLOSING date — the extraction prompt
/// asks for the column "whose closing date matches fiscal_year_detected", and the legajo integrity
/// check holds a declared fiscal year to the year of the document's period end. A balance closing on
/// 31/08/2025 is therefore ejercicio 2025.
/// </para>
/// <para>
/// No ±1 tolerance. The commonest real error is the off-by-one one (last year's balance analysed
/// as this year's), and the extraction does not report the closing month, so a non-calendar close
/// cannot be told apart from a calendar one. When the document is exactly one year AHEAD of the
/// request — what a non-calendar ejercicio labelled with its STARTING year looks like — the anomaly
/// adds a hint stating the convention instead of silently forgiving it.
/// </para>
/// </remarks>
public static class FiscalYearCheck
{
    /// <summary>The detected year equals the requested one.</summary>
    public const string Match = "match";

    /// <summary>The document states a different fiscal year than the analysis.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>The extraction reported no fiscal year.</summary>
    public const string NotDetected = "not_detected";

    /// <summary>
    /// <see cref="NotDetected"/> when <paramref name="detected"/> is null, <see cref="Match"/> when
    /// it equals <paramref name="requested"/>, <see cref="Mismatch"/> otherwise.
    /// </summary>
    public static string Evaluate(int requested, int? detected) =>
        detected is not { } year ? NotDetected
        : year == requested       ? Match
        :                           Mismatch;

    /// <summary>
    /// The es-AR anomaly for a mismatch, or null when the years do not mismatch. No "%" — the
    /// percentage reconciler rewrites "X% del …" clauses in the anomalies list and must leave this alone.
    /// </summary>
    public static string? Anomaly(int requested, int? detected)
    {
        if (Evaluate(requested, detected) != Mismatch)
            return null;

        var text =
            $"El ejercicio detectado en el documento ({detected!.Value.ToString(CultureInfo.InvariantCulture)}) " +
            $"no coincide con el ejercicio del análisis ({requested.ToString(CultureInfo.InvariantCulture)}).";

        return detected.Value == requested + 1
            ? text + " Si el ejercicio no cierra en diciembre, el ejercicio del análisis se identifica por el año de cierre."
            : text;
    }
}
