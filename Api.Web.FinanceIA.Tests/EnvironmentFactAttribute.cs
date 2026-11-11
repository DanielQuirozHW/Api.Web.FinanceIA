// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System;
using Xunit;

namespace Api.Web.FinanceIA.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that reports as *skipped* unless every named environment
/// variable is set. Keeps opt-in harnesses — the ones that call a real paid API or need a
/// real document on disk — out of a normal <c>dotnet test</c> pass without hiding them.
///
/// This is the project's first gating mechanism: there was no prior convention, and xUnit
/// here is v2 (2.9.3), so the dynamic <c>Assert.Skip()</c> of v3 is not available. Setting
/// <see cref="FactAttribute.Skip"/> from the attribute constructor is the v2 idiom and
/// yields a genuine "Skipped" result rather than a silently-passing empty test.
///
/// The skip reason names the missing variable, so the runner output tells an operator
/// exactly what to set in order to run it.
/// </summary>
public sealed class EnvironmentFactAttribute : FactAttribute
{
    /// <param name="variables">
    /// Environment variables that must all be non-empty for the test to run.
    /// The first missing one is named in the skip reason.
    /// </param>
    public EnvironmentFactAttribute(params string[] variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                Skip = $"Set {variable} to run this test.";
                return;
            }
        }
    }
}
