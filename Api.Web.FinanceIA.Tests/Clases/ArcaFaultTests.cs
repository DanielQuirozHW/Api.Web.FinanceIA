// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Tests.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="ArcaFault"/>: only ARCA's exact "no such person" faultstring means no person; every other
/// fault — token, authorisation, computador, internal — stays a failure.
/// </summary>
public sealed class ArcaFaultTests
{
    [Theory]
    [InlineData("No existe persona con ese Id")]
    [InlineData("  No existe persona con ese Id \n")]
    [InlineData("NO EXISTE PERSONA CON ESE ID")]
    public void IsPersonNotFound_TheNotFoundFaultString_IsTrue(string faultString)
        => Assert.True(ArcaFault.IsPersonNotFound(faultString));

    [Theory]
    [InlineData("El token ha expirado")]
    [InlineData("Token invalido")]
    [InlineData("La CUIT representada no se encuentra autorizada")]
    [InlineData("Computador no autorizado a acceder al servicio")]
    [InlineData("Error interno de base de datos")]
    [InlineData("No existe persona con ese Id 20111111112")]   // contains the phrase: not the answer
    [InlineData("Error: No existe persona con ese Id")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsPersonNotFound_AnyOtherText_IsFalse(string? faultString)
        => Assert.False(ArcaFault.IsPersonNotFound(faultString));

    [Fact]
    public void ReadFaultString_ReadsTheFaultstringOfASoapFault()
        => Assert.Equal(
            ArcaSoapFakes.NotFoundFaultString,
            ArcaFault.ReadFaultString(ArcaSoapFakes.FaultBody(ArcaSoapFakes.NotFoundFaultString)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("Service Unavailable")]                 // not XML
    [InlineData("<soap:Envelope")]                       // truncated XML
    public void ReadFaultString_WithoutAFault_IsNull(string? body)
        => Assert.Null(ArcaFault.ReadFaultString(body));
}
