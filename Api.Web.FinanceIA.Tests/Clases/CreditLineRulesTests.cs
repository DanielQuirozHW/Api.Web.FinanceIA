// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="CreditLineRules"/>, extracted from CreditLinesController unchanged. The controller's own
/// suites (CreditLinesController*Tests) still pin the behaviour through its endpoints; these pin the rules
/// directly, and the parity of the C# predicate with the expression SQL Server sums under.
/// </summary>
public sealed class CreditLineRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static CreditLineOperation Op(string status, DateTime? validUntil = null)
        => new() { OperationId = Guid.NewGuid(), Status = status, ValidUntil = validUntil };

    [Theory]
    [InlineData("Aprobada", true)]
    [InlineData("Vigente", true)]
    [InlineData("Cancelada", false)]
    [InlineData("Vencida", true)]
    [InlineData("cancelada", true)]   // ordinal: only the stored spelling frees cupo
    [InlineData("", true)]
    public void OccupiesCupo_AndItsSqlTwin_AgreeOnEveryStatus(string status, bool expected)
    {
        var op = Op(status);

        Assert.Equal(expected, CreditLineRules.OccupiesCupo(op));
        Assert.Equal(expected, CreditLineRules.OccupiesCupoPredicate.Compile()(op));
    }

    public static TheoryData<string, int?, string> EffectiveCases => new()
    {
        // stored status, ValidUntil as days from Now's UTC date (null = open-ended), expected
        { "Cancelada", -5,   "Cancelada" },
        { "Cancelada", null, "Cancelada" },
        { "Aprobada",  -5,   "Aprobada"  },   // an approval nobody confirmed never expires into Vencida
        { "Aprobada",  null, "Aprobada"  },
        { "Vigente",   -1,   "Vencida"   },
        { "Vigente",   0,    "Vigente"   },   // due today is still in force
        { "Vigente",   1,    "Vigente"   },
        { "Vigente",   null, "Vigente"   },
    };

    [Theory]
    [MemberData(nameof(EffectiveCases))]
    public void EffectiveStatus_DerivesVencidaOnlyFromAPastVigente(string stored, int? days, string expected)
    {
        var op = Op(stored, days is int d ? Now.Date.AddDays(d) : null);

        Assert.Equal(expected, CreditLineRules.EffectiveStatus(op, Now));
    }

    [Fact]
    public void EffectiveStatus_DecidesOnTheUtcDate_NotTheInstant()
    {
        // Due on the 23rd: Vigente all through the 23rd UTC, Vencida from 00:00 UTC on the 24th — which is
        // 21:00 on the 23rd in Buenos Aires. That is the cupo screen's rule, kept as it was.
        var op = Op("Vigente", new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Vigente", CreditLineRules.EffectiveStatus(op, new DateTime(2026, 10, 23, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal("Vencida", CreditLineRules.EffectiveStatus(op, new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [MemberData(nameof(EffectiveCases))]
    public void EffectiveStatus_WithoutAnInstant_IsTheOverloadAtUtcNow(string stored, int? days, string _)
    {
        var op = Op(stored, days is int d ? DateTime.UtcNow.Date.AddDays(d) : null);

        Assert.Equal(CreditLineRules.EffectiveStatus(op, DateTime.UtcNow), CreditLineRules.EffectiveStatus(op));
    }
}
