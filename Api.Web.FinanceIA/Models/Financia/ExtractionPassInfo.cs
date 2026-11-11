// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Provenance of the kept balance extraction, persisted at <c>BalanceJson.integrity.extraction</c>.
/// Names and counts only — never a figure.
/// </summary>
public sealed record ExtractionPassInfo
{
    /// <summary>"first" — the kept pass is the first one.</summary>
    public const string FirstPass = "first";

    /// <summary>"second" — the kept pass is the differently-read second one.</summary>
    public const string SecondPass = "second";

    /// <summary>Trigger: the first pass failed a totals/structure integrity check.</summary>
    public const string IntegrityTrigger = "integrity";

    /// <summary>Trigger: the first pass's effective confidence was below the fallback threshold.</summary>
    public const string LowConfidenceTrigger = "low_confidence";

    /// <summary>
    /// Trigger: the first pass's net_profit_reconciliation failed as a column mix — result before tax and income
    /// tax read from the comparative column (<c>BalanceIntegrityCheck.ColumnMix</c>).
    /// </summary>
    public const string ColumnMixTrigger = "column_mix";

    /// <summary>Orientation source: only the PDF's glyphs decided pages.</summary>
    public const string GlyphsOrientation = "glyphs";

    /// <summary>Orientation source: only the image detector decided pages (a scan with no text layer).</summary>
    public const string ImageOrientation = "image";

    /// <summary>Orientation source: some pages were decided by the glyphs, others by the image detector.</summary>
    public const string MixedOrientation = "mixed";

    /// <summary>Orientation source: no page's orientation could be decided; every page went as it is.</summary>
    public const string NoOrientation = "none";

    /// <summary>
    /// <see cref="EmbeddedAttachment"/>: the uploaded PDF carries the balance as an embedded attachment
    /// that could not be opened, so the extraction read only the wrapper.
    /// </summary>
    public const string UnreadAttachment = "unread";

    /// <summary>
    /// <see cref="EmbeddedAttachmentSource"/>: the attachment was found through the orphaned file
    /// specification fallback (<c>PdfAttachmentExtractor.OrphanedFileSpecSource</c>).
    /// </summary>
    public const string OrphanedFileSpecSource = "orphaned_filespec";

    /// <summary>
    /// The kept pass's method: "document", "image", "image-fallback" or "image-oriented"
    /// (<see cref="ClaudeAnalysisResult.ExtractionMethod"/>).
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>True when a second pass was sent and answered (whether or not it was kept).</summary>
    [JsonPropertyName("secondPassRan")]
    public required bool SecondPassRan { get; init; }

    /// <summary>
    /// Why a second pass was wanted: any of <see cref="IntegrityTrigger"/>, <see cref="ColumnMixTrigger"/> and
    /// <see cref="LowConfidenceTrigger"/>, in that order.
    /// </summary>
    [JsonPropertyName("secondPassTriggers")]
    public IReadOnlyList<string> SecondPassTriggers { get; init; } = [];

    /// <summary><see cref="FirstPass"/> or <see cref="SecondPass"/>.</summary>
    [JsonPropertyName("keptPass")]
    public required string KeptPass { get; init; }

    /// <summary>
    /// Why that pass was kept: "only_pass", "integrity_passed", "fewer_structure_failures",
    /// "higher_confidence", "tie_first", "second_pass_skipped" or "second_pass_failed".
    /// </summary>
    [JsonPropertyName("keptReason")]
    public required string KeptReason { get; init; }

    /// <summary>Pages the second pass rendered rotated to upright (0 when none, or no second pass).</summary>
    [JsonPropertyName("rotatedPages")]
    public int RotatedPages { get; init; }

    /// <summary>
    /// Pages the second pass sent as three images — the full page, its left half and its right half —
    /// because they were wide once upright (0 when none, or no second pass).
    /// </summary>
    [JsonPropertyName("splitPages")]
    public int SplitPages { get; init; }

    /// <summary>
    /// What decided the pages' orientation for the second pass: <see cref="GlyphsOrientation"/>,
    /// <see cref="ImageOrientation"/>, <see cref="MixedOrientation"/> or <see cref="NoOrientation"/>;
    /// null when orientation was never read (no second pass was wanted, or it failed before).
    /// </summary>
    [JsonPropertyName("orientationSource")]
    public string? OrientationSource { get; init; }

    /// <summary>
    /// <see cref="UnreadAttachment"/> when the uploaded PDF carries embedded files but none could be
    /// taken as the balance (one failed to open, the choice was ambiguous, or the one named as the
    /// statements did not qualify), so what was extracted is the wrapper. Set by the analysis
    /// pipeline, never by ClaudeService; omitted otherwise, so every other BalanceJson is
    /// byte-identical to what it was before this field existed.
    /// </summary>
    [JsonPropertyName("embeddedAttachment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmbeddedAttachment { get; init; }

    /// <summary>
    /// <see cref="OrphanedFileSpecSource"/> when the attachment taken as the balance — or, with
    /// <see cref="EmbeddedAttachment"/> = "unread", the one left unread — was found through the orphaned
    /// file specification fallback: a re-saved PDF whose live catalog no longer reaches its attachments.
    /// Set by the analysis pipeline, never by ClaudeService; omitted otherwise, so every other
    /// BalanceJson is byte-identical to what it was before this field existed.
    /// </summary>
    [JsonPropertyName("embeddedAttachmentSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EmbeddedAttachmentSource { get; init; }

    /// <summary>
    /// True when the KEPT pass's answer had prose (or a second value) around its JSON object, which was
    /// dropped before reading. Set by ClaudeService only then; omitted otherwise, so every clean answer's
    /// BalanceJson is byte-identical to what it was before this field existed.
    /// </summary>
    [JsonPropertyName("answerTrimmed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AnswerTrimmed { get; init; }
}
