// Project : Api.Web.FinanceIA
// Created : 2026-08-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Outcome of reading an uploaded spreadsheet. Every rejection is a distinct member so the caller
/// can answer with a message that says what is actually wrong, rather than a generic "bad file".
/// </summary>
public enum ExcelParseStatus
{
    /// <summary>The workbook opened and at least one sheet with data was rendered.</summary>
    Ok,

    /// <summary>
    /// The bytes are not an OOXML spreadsheet workbook. This is the gate that does the real work:
    /// the magic-byte check upstream can only prove the file is a ZIP, so a .docx, a .pptx, a .jar
    /// or a plain .zip reaches this point and is rejected here, as is a corrupt package.
    /// </summary>
    NotAValidWorkbook,

    /// <summary>
    /// The package is macro-enabled or a template (.xlsm, .xltm, .xltx, add-in), judged by its
    /// OOXML document type rather than its file name — renaming a .xlsm to .xlsx does not get past
    /// this. Rejected outright: a cash flow never needs macros, and accepting them would add attack
    /// surface for nothing.
    /// </summary>
    MacroEnabledOrTemplate,

    /// <summary>The workbook is valid but no worksheet carries any data.</summary>
    NoWorksheets,

    /// <summary>
    /// The package declares far more uncompressed content than its size can honestly hold — the
    /// decompression-bomb shape. Rejected before the workbook is materialised, because the reader
    /// loads the whole thing into memory and by then it is too late.
    /// </summary>
    DecompressionBombSuspected
}

/// <summary>
/// The Markdown rendering of an uploaded spreadsheet plus the counts describing what was kept.
/// The counts are what was INCLUDED in <paramref name="Markdown"/>, not what the file contained —
/// when <paramref name="Truncated"/> is true, <paramref name="Note"/> says what was left out.
/// On any non-<see cref="ExcelParseStatus.Ok"/> status the text fields are empty and the counts
/// are zero.
/// </summary>
/// <param name="Status">Whether the file was readable, and if not, why not.</param>
/// <param name="Markdown">One Markdown table per rendered sheet, plus the truncation note.</param>
/// <param name="SheetCount">Sheets rendered.</param>
/// <param name="RowCount">Rows rendered across all sheets, header rows included.</param>
/// <param name="CellCount">Cells rendered across all sheets.</param>
/// <param name="Truncated">True when any cap was reached and content was left out.</param>
/// <param name="Note">es-AR note naming what was cut; null when nothing was.</param>
public sealed record ExcelParseResult(
    ExcelParseStatus Status,
    string           Markdown,
    int              SheetCount,
    int              RowCount,
    int              CellCount,
    bool             Truncated,
    string?          Note)
{
    /// <summary>A rejection carrying no content — used for every non-Ok status.</summary>
    public static ExcelParseResult Rejected(ExcelParseStatus status) =>
        new(status, string.Empty, 0, 0, 0, false, null);
}
