// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>The per-page verdicts <see cref="PageTextQualityAnalyzer"/> writes into Documents.PageQualityJson.</summary>
public static class PageTextVerdicts
{
    /// <summary>The page has a text layer that reads like text.</summary>
    public const string Good = "Good";

    /// <summary>The page has text, but it looks broken (bad encoding, glued or letter-spaced words).</summary>
    public const string Suspect = "Suspect";

    /// <summary>The page has (almost) no text layer — typically a scanned image.</summary>
    public const string NoText = "NoText";

    /// <summary>The page could not be read at all.</summary>
    public const string Unknown = "Unknown";
}
