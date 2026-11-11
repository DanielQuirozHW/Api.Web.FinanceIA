// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="BusinessNameNormalizer"/>: wrapping quotes go, quotes inside the name stay, whitespace
/// collapses, control characters disappear, and nothing else (case included) changes. Synthetic names.
/// </summary>
public sealed class BusinessNameNormalizerTests
{
    public static TheoryData<string, string> Cases() => new()
    {
        // The reported case: straight quotes stored around the whole name. Case is NOT changed.
        { "\"silo Chico S.A.\"",                     "silo Chico S.A." },
        { "  \"SILO CHICO S.A.\"  ",                 "SILO CHICO S.A." },
        // Curly, low-high and angle quotes, and mixed members of one family.
        { "“Agro Norte S.R.L.”",           "Agro Norte S.R.L." },
        { "„Agro Norte S.R.L.“",           "Agro Norte S.R.L." },
        { "«Agro Norte S.R.L.»",           "Agro Norte S.R.L." },
        { "“Agro Norte S.R.L.\"",               "Agro Norte S.R.L." },
        { "'Agro Norte S.R.L.'",                     "Agro Norte S.R.L." },
        { "‘Agro Norte S.R.L.’",           "Agro Norte S.R.L." },
        { "‹Agro Norte S.R.L.›",           "Agro Norte S.R.L." },
        // Repeated layers, including across families.
        { "\"\"Agro Norte S.R.L.\"\"",               "Agro Norte S.R.L." },
        { "«\"Agro Norte\"»",              "Agro Norte" },
        { "'\"Agro Norte\"'",                        "Agro Norte" },
        { "\" “ Agro Norte ” \"",          "Agro Norte" },
        // Quotes INSIDE the name stay.
        { "Sociedad \"La Esperanza\" S.A.",          "Sociedad \"La Esperanza\" S.A." },
        { "\"Sociedad \"La Esperanza\" S.A.\"",      "Sociedad \"La Esperanza\" S.A." },
        { "\"La Esperanza\" S.A.",                   "\"La Esperanza\" S.A." },
        { "\"Uno\" y \"Dos\"",                       "\"Uno\" y \"Dos\"" },
        { "\"Hotel \"Sol\"",                         "\"Hotel \"Sol\"" },
        // A one-sided quote is not a wrap.
        { "\"Agro Norte S.A.",                       "\"Agro Norte S.A." },
        // Apostrophes inside words are not quotes.
        { "O'Brien Hnos. S.A.",                      "O'Brien Hnos. S.A." },
        { "'O'Brien Hnos. S.A.'",                    "O'Brien Hnos. S.A." },
        // Quotes of DIFFERENT families at the two ends do not pair.
        { "\"Agro Norte'",                           "\"Agro Norte'" },
        // Whitespace: runs, tabs, line breaks and no-break spaces collapse to one space.
        { "  Agro   Norte \t S.A.  ",                "Agro Norte S.A." },
        { "Agro\r\nNorte\nS.A.",                     "Agro Norte S.A." },
        { "Agro  Norte S.A.",              "Agro Norte S.A." },
        // Control and zero-width characters are removed.
        { "Agro\u0000 Norte\u0007 S.A.\u001F",       "Agro Norte S.A." },
        { "﻿Agro​ Norte S.A.‍",       "Agro Norte S.A." },
        { "\u0001\"Agro Norte S.A.\"\u0002",         "Agro Norte S.A." },
        // A clean name passes through untouched.
        { "Agro Norte S.A.",                         "Agro Norte S.A." },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Normalize(string raw, string expected)
    {
        Assert.Equal(expected, BusinessNameNormalizer.Normalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("\"")]
    [InlineData("\" \"")]
    [InlineData("«»")]
    [InlineData("''")]
    [InlineData("\u0000\u0001\t")]
    [InlineData("​")]
    public void Normalize_NothingLeft_IsEmpty(string? raw)
    {
        Assert.Equal(string.Empty, BusinessNameNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        foreach (var raw in Cases().Select(row => (string)row[0]))
        {
            var once = BusinessNameNormalizer.Normalize(raw);
            Assert.Equal(once, BusinessNameNormalizer.Normalize(once));
        }
    }
}
