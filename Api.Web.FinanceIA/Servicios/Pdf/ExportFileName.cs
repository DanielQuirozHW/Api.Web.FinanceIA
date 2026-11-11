// Project : Api.Web.FinanceIA
// Created : 2026-11-08

using System.Globalization;
using System.Text;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The download name of an exported report: an ASCII file name safe for every browser and file system, plus
/// the same name in UTF-8 for the RFC 5987 <c>filename*</c> parameter.
/// <list type="bullet">
/// <item>Company: <c>Informe-crediticio_&lt;Razon-social&gt;_Ejercicio-&lt;año&gt;_&lt;aaaa-mm-dd&gt;.pdf</c></item>
/// <item>Persona humana: <c>Evaluacion-crediticia_&lt;Nombre&gt;_&lt;aaaa-mm-dd&gt;.pdf</c></item>
/// </list>
/// </summary>
internal static class ExportFileName
{
    internal const int MaxSlugLength = 80;

    private const string FallbackSlug = "analisis";

    /// <summary>The ASCII name and its UTF-8 counterpart (accents and spaces kept, unsafe characters dropped).</summary>
    internal sealed record Names(string Ascii, string Utf8);

    internal static Names ForCompany(string? businessName, int fiscalYear, DateOnly exportDate)
    {
        string date = exportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string year = fiscalYear.ToString(CultureInfo.InvariantCulture);

        return new Names(
            $"Informe-crediticio_{Slug(businessName)}_Ejercicio-{year}_{date}.pdf",
            $"Informe crediticio_{Readable(businessName)}_Ejercicio {year}_{date}.pdf");
    }

    internal static Names ForPerson(string? personName, DateOnly exportDate)
    {
        string date = exportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new Names(
            $"Evaluacion-crediticia_{Slug(personName)}_{date}.pdf",
            $"Evaluación crediticia_{Readable(personName)}_{date}.pdf");
    }

    /// <summary>
    /// ASCII slug: accents stripped, quotes dropped, every other run of non-alphanumerics → one hyphen, at
    /// most <see cref="MaxSlugLength"/> characters cut at a hyphen when one is close, "analisis" when empty.
    /// </summary>
    internal static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return FallbackSlug;

        var builder = new StringBuilder(value.Length);
        bool pendingHyphen = false;

        foreach (char c in value.Normalize(NormalizationForm.FormD))
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;           // the accent of a decomposed letter
            if (IsQuote(c)) continue;                                           // "La Rural" S.A. → La-Rural-S-A

            char ascii = c switch { 'ß' => 's', 'æ' or 'Æ' => 'a', 'ø' or 'Ø' => 'o', _ => c };
            if (ascii < 128 && char.IsLetterOrDigit(ascii))
            {
                if (pendingHyphen && builder.Length > 0) builder.Append('-');
                builder.Append(ascii);
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        string slug = builder.ToString();
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength];
            int cut = slug.LastIndexOf('-');
            if (cut >= MaxSlugLength - 20) slug = slug[..cut];
            slug = slug.TrimEnd('-');
        }

        return slug.Length == 0 ? FallbackSlug : slug;
    }

    // The UTF-8 name: the original text without characters a file system or a header would choke on.
    private static string Readable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return FallbackSlug;

        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            if (char.IsControl(c) || IsQuote(c) || "\\/:*?<>|".Contains(c)) continue;
            builder.Append(c);
        }

        string readable = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (readable.Length > MaxSlugLength) readable = readable[..MaxSlugLength].TrimEnd();

        return readable.Length == 0 ? FallbackSlug : readable;
    }

    private static bool IsQuote(char c) => c is '"' or '\'' or '“' or '”' or '‘' or '’' or '«' or '»' or '`' or '´';
}
