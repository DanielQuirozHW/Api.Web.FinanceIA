// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-02

using System.Reflection;
using Api.Web.FinanceIA.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Pins that there is no self-registration. POST api/bff/auth/register turned any Firebase user of
/// the tenant's project (the Web API key is public, so anyone can make one) into an Analyst, and
/// POST api/bff/auth/complete-registration was anonymous and bound whatever Firebase uid the body
/// named. Both were removed; POST api/bff/auth/register-invited is the only way a Users row is born.
/// <para>
/// Reflection over every controller in the API assembly, so re-adding either route on ANY controller
/// fails here, not just on <see cref="BffController"/>.
/// </para>
/// </summary>
public sealed class SelfRegistrationClosedTests
{
    [Theory]
    [InlineData("api/bff/auth/register")]
    [InlineData("api/bff/auth/complete-registration")]
    public void RemovedRegistrationRoute_IsNotMappedByAnyController(string route)
    {
        Assert.DoesNotContain(route, AllPostRoutes());
    }

    [Fact]
    public void RegisterInvited_IsStillMapped()
    {
        Assert.Contains("api/bff/auth/register-invited", AllPostRoutes());
    }

    [Theory]
    [InlineData("RegisterAsync")]
    [InlineData("CompleteRegistrationAsync")]
    public void BffController_HasNoRegistrationAction(string method)
    {
        Assert.False(AuthorizationTestHelpers.HasAction(typeof(BffController), method));
    }

    [Theory]
    [InlineData("Api.Web.FinanceIA.Models.Financia.RegisterRequest")]
    [InlineData("Api.Web.FinanceIA.Models.Auth.CompleteRegistrationRequest")]
    public void RemovedRequestModel_IsGone(string typeName)
    {
        Assert.Null(typeof(BffController).Assembly.GetType(typeName));
    }

    // Every POST route in the API assembly, as "<controller [Route]>/<action template>",
    // case-folded to lower so a re-added route cannot slip past on casing.
    private static HashSet<string> AllPostRoutes()
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<Type> controllers = typeof(BffController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (Type controller in controllers)
        {
            string[] prefixes = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template)
                .DefaultIfEmpty(string.Empty)
                .ToArray();

            IEnumerable<string> templates = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                .Where(a => a.HttpMethods.Contains("POST"))
                .Select(a => a.Template ?? string.Empty);

            foreach (string template in templates)
            {
                foreach (string prefix in prefixes)
                {
                    string full = template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)
                        ? template.TrimStart('~', '/')
                        : $"{prefix.Trim('/')}/{template}".Trim('/');

                    routes.Add(full.ToLowerInvariant());
                }
            }
        }

        return routes;
    }
}
