// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>The download name: an ASCII slug for every client, the UTF-8 name for filename*.</summary>
public sealed class ExportFileNameTests
{
    private static readonly DateOnly Day = new(2026, 11, 8);

    [Theory]
    [InlineData("Metalúrgica Ejemplo S.A.",          "Metalurgica-Ejemplo-S-A")]
    [InlineData("  Ñandú   Agro  S.R.L. ",           "Nandu-Agro-S-R-L")]
    [InlineData("\"La Rural\" S.A.",                 "La-Rural-S-A")]
    [InlineData("O’Higgins & Cía — Construcciones",  "OHiggins-Cia-Construcciones")]
    [InlineData("Transportes/Logística: Sur*Norte?", "Transportes-Logistica-Sur-Norte")]
    [InlineData("Straße Café",                        "Strase-Cafe")]
    [InlineData("",                                   "analisis")]
    [InlineData("¡¿!?",                               "analisis")]
    public void Slug_IsAsciiHyphenated_WithoutAccentsOrQuotes(string name, string expected)
    {
        Assert.Equal(expected, ExportFileName.Slug(name));
    }

    [Fact]
    public void ALongName_IsCutAtAHyphen_AtMost80Characters()
    {
        string name = string.Join(' ', Enumerable.Repeat("Cooperativa Agropecuaria Limitada", 8));

        string slug = ExportFileName.Slug(name);

        Assert.True(slug.Length <= ExportFileName.MaxSlugLength, slug);
        Assert.False(slug.EndsWith('-'));
        Assert.StartsWith("Cooperativa-Agropecuaria-Limitada-Cooperativa", slug);
        Assert.True(slug.Length > 60, "the cut lands at a hyphen close to the limit");
    }

    [Fact]
    public void Company_FollowsTheConvention()
    {
        ExportFileName.Names names = ExportFileName.ForCompany("Metalúrgica Ejemplo S.A.", 2025, Day);

        Assert.Equal("Informe-crediticio_Metalurgica-Ejemplo-S-A_Ejercicio-2025_2026-11-08.pdf", names.Ascii);
        Assert.Equal("Informe crediticio_Metalúrgica Ejemplo S.A._Ejercicio 2025_2026-11-08.pdf", names.Utf8);
    }

    [Fact]
    public void Persona_FollowsTheConvention()
    {
        ExportFileName.Names names = ExportFileName.ForPerson("José María Núñez", Day);

        Assert.Equal("Evaluacion-crediticia_Jose-Maria-Nunez_2026-11-08.pdf", names.Ascii);
        Assert.Equal("Evaluación crediticia_José María Núñez_2026-11-08.pdf", names.Utf8);
    }

    [Fact]
    public void AMissingName_StillProducesAValidName()
    {
        Assert.Equal("Informe-crediticio_analisis_Ejercicio-2025_2026-11-08.pdf", ExportFileName.ForCompany(null, 2025, Day).Ascii);
        Assert.Equal("Evaluacion-crediticia_analisis_2026-11-08.pdf", ExportFileName.ForPerson("   ", Day).Ascii);
    }

    [Fact]
    public void TheUtf8Name_DropsCharactersAHeaderOrFileSystemRejects()
    {
        ExportFileName.Names names = ExportFileName.ForCompany("\"A/B\" <C>: D?", 2025, Day);

        Assert.Equal("Informe crediticio_AB C D_Ejercicio 2025_2026-11-08.pdf", names.Utf8);
    }

    [Fact]
    public void TheHeader_CarriesBothForms_AndRoundTrips()
    {
        ExportFileName.Names names = ExportFileName.ForCompany("Metalúrgica \"Ejemplo\" S.A.", 2025, Day);

        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(names.Ascii);
        disposition.FileNameStar = names.Utf8;

        ContentDispositionHeaderValue parsed = ContentDispositionHeaderValue.Parse(disposition.ToString());
        Assert.Equal(names.Ascii, HeaderUtilities.RemoveQuotes(parsed.FileName).ToString());
        Assert.Equal(names.Utf8, parsed.FileNameStar.ToString());
    }
}
