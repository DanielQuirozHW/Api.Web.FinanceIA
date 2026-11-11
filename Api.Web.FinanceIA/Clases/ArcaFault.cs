// Project : Api.Web.FinanceIA
// Created : 2026-10-28

using System.Xml;
using System.Xml.Linq;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The one rule that reads an ARCA SOAP Fault as "ARCA answered, and registers no persona for this
/// CUIT". ws_sr_constancia_inscripcion answers an unknown CUIT with a Fault — over HTTP 500 in
/// production — instead of a <c>personaNoRegistrada</c> element; every OTHER fault (expired or invalid
/// token, cuitRepresentada not authorised, computador no autorizado, internal errors) is a failure.
/// </summary>
/// <remarks>
/// The match is an exact faultstring, never a substring: a fault that merely mentions a persona must not
/// turn an outage into "no data". The list holds only the wording observed in production
/// (ApiCallLogs, 2026-10); the ARCA manual could not be checked for other not-found wordings, so a
/// verified variant is added here and nowhere else.
/// </remarks>
public static class ArcaFault
{
    private static readonly string[] PersonNotFoundFaultStrings =
    [
        "No existe persona con ese Id"
    ];

    /// <summary>True only for ARCA's "no such person" faultstring (trimmed, case-insensitive, exact).</summary>
    public static bool IsPersonNotFound(string? faultString)
    {
        if (string.IsNullOrWhiteSpace(faultString)) return false;

        var trimmed = faultString.Trim();
        return PersonNotFoundFaultStrings.Any(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The <c>faultstring</c> of a SOAP Fault body, or null when the body is not XML or carries no Fault.
    /// Never throws.
    /// </summary>
    internal static string? ReadFaultString(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            return ReadFaultString(XDocument.Parse(xml));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>The <c>faultstring</c> of the document's SOAP Fault, or null when it carries no Fault.</summary>
    internal static string? ReadFaultString(XDocument doc)
    {
        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");

        return fault?.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
    }

    /// <summary>True when the document carries a SOAP Fault, whatever it says.</summary>
    internal static bool HasFault(XDocument doc)
        => doc.Descendants().Any(e => e.Name.LocalName == "Fault");
}
