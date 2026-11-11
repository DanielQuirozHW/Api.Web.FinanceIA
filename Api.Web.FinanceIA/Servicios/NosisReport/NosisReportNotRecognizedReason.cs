// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>Why a file is not taken for a NOSIS Manager "Informe Individual". Logged by name, never with content.</summary>
public enum NosisReportNotRecognizedReason
{
    /// <summary>PdfPig cannot open it (damaged, or encrypted with a non-empty user password).</summary>
    Unopenable,

    /// <summary>Opened, but its text layer could not be read.</summary>
    Unreadable,

    /// <summary>Page 1 has no "Informe Individual > CUIT" title.</summary>
    NoTitle,

    /// <summary>Page 1 has no "NOSIS.Manager" footer.</summary>
    NoFooter,

    /// <summary>Page 1's footer has no "dd.MM.yyyy HH:mm" date.</summary>
    NoReportDate,

    /// <summary>The footers name a CUIT other than the title's (e.g. two reports merged).</summary>
    CuitDisagreement,
}
