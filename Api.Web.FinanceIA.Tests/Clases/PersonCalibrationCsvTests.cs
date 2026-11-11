// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text;
using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>The es-AR Excel CSV: BOM, ';', CRLF, RFC 4180 quoting, the injection guard, decimals and dates.</summary>
public sealed class PersonCalibrationCsvTests
{
    [Fact]
    public void Write_StartsWithTheUtf8Bom_AndSeparatesWithSemicolonAndCrlf()
    {
        var bytes = PersonCalibrationCsv.Write(["a", "b"], [["1", "2"], ["ñ", null]]);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("a;b\r\n1;2\r\nñ;\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("con;separador", "\"con;separador\"")]
    [InlineData("con \"comillas\"", "\"con \"\"comillas\"\"\"")]
    [InlineData("dos\nlíneas", "\"dos\nlíneas\"")]
    [InlineData("coma, decimal", "coma, decimal")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Cell_QuotesPerRfc4180(string? value, string expected)
        => Assert.Equal(expected, PersonCalibrationCsv.Cell(value));

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+54 11", "'+54 11")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tTAB", "'\tTAB")]
    [InlineData("\rCR", "\"'\rCR\"")]
    [InlineData("=1;2", "\"'=1;2\"")]
    [InlineData("a=b", "a=b")]
    public void Cell_NeutralisesFormulaInjection_BeforeQuoting(string value, string expected)
        => Assert.Equal(expected, PersonCalibrationCsv.Cell(value));

    [Fact]
    public void Decimal_UsesTheDecimalComma_AndNoThousandsSeparator()
    {
        Assert.Equal("1234567,5", PersonCalibrationCsv.Decimal(1_234_567.5m));
        Assert.Equal("25,5", PersonCalibrationCsv.Decimal(25.50m));
        Assert.Equal("60", PersonCalibrationCsv.Decimal(60.00m));
        Assert.Equal("0,33", PersonCalibrationCsv.Decimal(0.3333m));
        Assert.Equal("", PersonCalibrationCsv.Decimal(null));
    }

    [Fact]
    public void Date_IsTheArgentineDay_InDdMmYyyy()
    {
        // 02:30 UTC on the 24th is 23:30 ART on the 23rd.
        Assert.Equal("23/10/2026", PersonCalibrationCsv.Date(new DateTime(2026, 10, 24, 2, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("", PersonCalibrationCsv.Date(null));
    }

    [Fact]
    public void Flag_IsSiNo_AndEmptyForNull()
    {
        Assert.Equal("Sí", PersonCalibrationCsv.Flag(true));
        Assert.Equal("No", PersonCalibrationCsv.Flag(false));
        Assert.Equal("", PersonCalibrationCsv.Flag(null));
    }

    [Fact]
    public void Headers_AreTheEsArColumns_InOrder()
        => Assert.Equal(
            [
                "analysisId", "resultId", "Fecha de alta", "Analista", "Equipo", "Entidad", "Estado", "Sugerencia", "Decisión",
                "Decidido por", "Fecha de decisión", "Motivo", "Filtros en Rechazo", "Filtros sin datos",
                "Indicadores en Rojo", "Cobertura de contragarantía (%)", "Mínimo congelado (%)", "Documentos faltantes",
                "Documentos vencidos", "Cantidad de reanálisis", "Versión del motor", "Aprobado con rechazo sugerido",
                "Aprobado con banda roja", "Rechazado en verde", "Contragarantía bajo el mínimo"
            ],
            PersonCalibrationService.CsvHeaders);
}
