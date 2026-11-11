// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Clases.Filters;

/// <summary>
/// Gates an operator-only setup endpoint on the <c>Setup:BootstrapKey</c> App Setting and the
/// <c>X-Setup-Key</c> header. See <see cref="SetupKeyGateFilter"/>.
/// <para>
/// A resource filter, not the first lines of the action: it runs BEFORE model binding, so neither a
/// malformed body nor model validation can answer 400 and reveal a route that should look absent.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SetupKeyGateAttribute : TypeFilterAttribute
{
    public SetupKeyGateAttribute()
        : base(typeof(SetupKeyGateFilter))
    {
    }
}
