// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>How a label reads: its hue family on the active <see cref="PdfPalette"/>.</summary>
public enum PdfTone
{
    Good,
    Caution,
    Bad,
    Neutral,
    Muted
}

/// <summary>The icon a reliability verdict carries beside its label.</summary>
public enum PdfReliabilityIcon
{
    Check,
    Info,
    Alert
}

/// <summary>
/// Every word the company report shares with the Portal's screen, ported so the PDF and the screen say the
/// same thing. Each map names its Portal source (PortalesHWA/portal-financeia, apps/portal/src).
/// </summary>
internal static class PdfLabels
{
    /// <summary>
    /// Under the executive summary of an analysis whose balance was corrected by hand: the correction's re-score
    /// rewrites the justification, never the summary, which still describes the original reading. Portal:
    /// INFORME_CORRECTED_NOTE_LEAD, printed under the summary on the same condition (the detail's
    /// <c>balanceCorrections</c> is not null).
    /// </summary>
    internal const string ExecutiveSummaryOriginalReadingNote =
        "Este resumen se gener\u00f3 con la lectura original del balance.";

    private static readonly ScoreLevelCutoffs DefaultLevelCutoffs =
        ScoreLevelCutoffs.Resolve(new Dictionary<string, decimal>());

    /// <summary>
    /// The tone of a score component (0–100, 0 = no risk) under the cutoffs its analysis was scored with — the
    /// breakdown's <c>levelCutoffs</c>, the same values that classified the score's level — falling back to the
    /// engine defaults (<see cref="ScoreLevelCutoffs.Resolve"/> over no tenant rows) only for a row scored before
    /// they were recorded. Low → good, Medium → caution, High and Critical → bad.
    /// </summary>
    internal static PdfTone ComponentTone(decimal score, ScoreLevelCutoffs? cutoffs) =>
        (cutoffs ?? DefaultLevelCutoffs).Classify(score) switch
        {
            "Low"    => PdfTone.Good,
            "Medium" => PdfTone.Caution,
            _        => PdfTone.Bad,
        };

    // lib/score-reliability.ts:73-78 and the tone at 168-193 (provisional is the Portal's alert tone; the
    // light print theme reads it in the loss red).
    internal static (string Label, PdfTone Tone, PdfReliabilityIcon Icon) Reliability(string? level) =>
        (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            ScoreReliabilityLevels.Complete    => ("Datos completos",  PdfTone.Good,    PdfReliabilityIcon.Check),
            ScoreReliabilityLevels.WithCaveats => ("Con notas",        PdfTone.Caution, PdfReliabilityIcon.Info),
            ScoreReliabilityLevels.Provisional => ("Score provisorio", PdfTone.Bad,     PdfReliabilityIcon.Alert),
            _                                  => ("Sin score",        PdfTone.Muted,   PdfReliabilityIcon.Info),
        };

    // packages/types/src/analysis.ts:17-22; RiskLevelBadge.tsx:29 for no level.
    internal static string RiskLevel(string? rawLevel) => Normalize(rawLevel) switch
    {
        "low"      => "Riesgo Bajo",
        "medium"   => "Riesgo Medio",
        "high"     => "Riesgo Alto",
        "critical" => "Riesgo Crítico",
        _          => "Sin clasificar"
    };

    // RiskLevelBadge.tsx:19-24 (Medium is the Portal's warning: the caution amber here).
    internal static PdfTone RiskTone(string? rawLevel) => Normalize(rawLevel) switch
    {
        "low"                => PdfTone.Good,
        "medium"             => PdfTone.Caution,
        "high" or "critical" => PdfTone.Bad,
        _                    => PdfTone.Muted
    };

    // packages/types/src/analysis.ts:24-32.
    internal static string AnalysisStatus(string? status) => status switch
    {
        "Queued"                      => "En cola",
        "Processing"                  => "Procesando",
        AnalysisStatuses.PendingReview => "Pendiente de revisión",
        "Reviewed"                    => "Revisado",
        AnalysisStatuses.PreApproved  => "Pre-aprobado",
        AnalysisStatuses.Approved     => "Aprobado",
        AnalysisStatuses.Rejected     => "Rechazado",
        _                             => "—"
    };

    // AnalysisStatusBadge.tsx:12-19 (PreApproved is orange there; the caution amber here, like every open status).
    internal static PdfTone StatusTone(string? status) => status switch
    {
        AnalysisStatuses.Approved => PdfTone.Good,
        AnalysisStatuses.Rejected => PdfTone.Bad,
        _                         => PdfTone.Caution
    };

    // AnalysisDetailView.tsx:238-245. An unknown profile prints as stored.
    internal static string SectorProfile(string? profile) => profile switch
    {
        null or ""     => "General",
        "Construccion" => "Construcción",
        _              => profile
    };

    // lib/alert-severity.ts:64-69 and the order at 30-47 (unknown → Low).
    internal static (string Label, int Rank, PdfTone Tone) AlertSeverity(string? level) => Normalize(level) switch
    {
        "critical" => ("Crítica", 4, PdfTone.Bad),
        "high"     => ("Alta",    3, PdfTone.Bad),
        "medium"   => ("Media",   2, PdfTone.Caution),
        _          => ("Baja",    1, PdfTone.Neutral)
    };

    // lib/bcra-situacion.ts:27-34.
    internal static string BcraSituacion(int situacion) => situacion switch
    {
        1 => "Normal",
        2 => "Con seguimiento especial",
        3 => "Con problemas",
        4 => "Con alto riesgo de insolvencia",
        5 => "Irrecuperable",
        6 => "Irrecuperable por disposición técnica",
        _ => "Desconocida"
    };

    // Colour bands of the same file: 1 good, 2–3 caution, 4+ bad.
    internal static PdfTone BcraSituacionTone(int situacion) =>
        situacion <= 1 ? PdfTone.Good : situacion <= 3 ? PdfTone.Caution : PdfTone.Bad;

    // Ratio status words, as the API persists them (ASCII) → the Portal's accented display.
    internal static (string Label, PdfTone Tone) RatioStatus(string? status) => status switch
    {
        "Saludable" => ("Saludable", PdfTone.Good),
        "Atencion"  => ("Atención",  PdfTone.Caution),
        "Critico"   => ("Crítico",   PdfTone.Bad),
        _           => ("N/D",       PdfTone.Muted)
    };

    /// <summary>The document a company analysis was read from.</summary>
    internal static string DocumentKind(bool isTrialBalance, bool hasManualCorrections) =>
        (isTrialBalance ? "Balance de sumas y saldos" : "Estados contables")
        + (hasManualCorrections ? " · corregido" : string.Empty);

    // lib/credit-line-types.ts (code 0 kept for historical operations).
    internal static string? CreditLineType(int code) => code switch
    {
        0  => "General",
        1  => "Acreedor otro",
        2  => "Acreedor socio protector",
        3  => "Aval técnico",
        4  => "Cheque de Pago Diferido",
        5  => "Fideicomiso Financiero",
        6  => "Leasing",
        7  => "Mercado de futuros y opciones",
        8  => "Obligaciones negociables",
        9  => "Organismos Internacionales",
        10 => "Pagaré bursátil",
        11 => "Préstamo",
        12 => "Públicas",
        13 => "Valores de corto plazo",
        14 => "Factura de Crédito Electrónica",
        15 => "Fintech",
        16 => "Tarjeta de Crédito",
        17 => "Cheque de Pago Diferido Acreedor Bancario",
        _  => null
    };

    // PersonLabels.cs:398-402 — the credit-line status words the persona report already prints.
    internal static string CreditLineStatus(string? status) => status switch
    {
        "Active"     => "Activa",
        "Suspended"  => "Suspendida",
        "Superseded" => "Reemplazada",
        _            => status ?? "—"
    };

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
