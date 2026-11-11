using System.Diagnostics;
using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;

namespace Api.Web.FinanceIA.Clases.Telemetry;

/// <summary>
/// Wires Azure Application Insights — the 3.x SDK (<c>Microsoft.ApplicationInsights.AspNetCore</c>), which
/// is OpenTelemetry underneath: ASP.NET Core requests, HttpClient and SqlClient dependencies, ILogger logs
/// (the <c>OpenTelemetry</c> logging provider, levels under <c>Logging:OpenTelemetry:LogLevel</c>) and the
/// Azure Monitor exporter. Guide: docs/guides/telemetry.md.
///
/// ON ONLY WITH A CONNECTION STRING. The 3.x SDK throws when it has none, so without one nothing is
/// registered at all and the app runs exactly as before (local runs, tests, CI). The connection string is
/// read only from configuration — the <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> App Setting that linking
/// an Application Insights resource sets, then <c>ApplicationInsights:ConnectionString</c>, the order the
/// SDK itself uses — and never from appsettings.json.
///
/// NOTHING IS SAMPLED. The 3.x defaults are rate-limited sampling (5 traces/s) plus a trace-based log
/// sampler that drops every log — Warnings and Errors included — of a request that was sampled out. This
/// sets <c>SamplingRatio = 1</c> and <c>EnableTraceBasedLogsSampler = false</c> unless the
/// <c>ApplicationInsights</c> configuration section says otherwise.
///
/// NO IDENTIFIER, TOKEN OR CONNECTION STRING. Query strings never leave (OpenTelemetry's redaction is
/// forced on, and the enrichers below drop the query outright); path segments holding a CUIT/DNI become
/// <c>{id}</c> (<see cref="UrlRedaction"/>); SQL parameter values are forced off; headers and bodies are
/// never captured (none of the instrumentations do by default and nothing here turns them on).
///
/// The <c>--verify-schema</c> mode returns before the web host is built, so it never gets here.
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>The App Setting linking an Application Insights resource sets; read first.</summary>
    public const string ConnectionStringSetting = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>The configuration key read when <see cref="ConnectionStringSetting"/> is absent.</summary>
    public const string ConnectionStringConfigKey = "ApplicationInsights:ConnectionString";

    /// <summary>The request-telemetry custom dimension that carries the resolved tenant.</summary>
    public const string TenantIdTag = "TenantId";

    /// <summary>The key TenantResolutionMiddleware and TenantFromJwtMiddleware store the context under.</summary>
    internal const string TenantContextItemKey = "TenantContext";

    /// <summary>
    /// Values forced into configuration whenever telemetry is on, last so no App Setting can undo them:
    /// OpenTelemetry's query-value redaction stays on for requests and dependencies, and SQL parameter
    /// values are never recorded. (The SDK writes <c>ASPNETCORE_DISABLE_URL_QUERY_REDACTION</c>, a key
    /// OpenTelemetry 1.15 does not read; these are the ones it does.)
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string?> ForcedSettings = new Dictionary<string, string?>
    {
        ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"]     = "false",
        ["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"]     = "false",
        ["OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_DB_QUERY_PARAMETERS"] = "false",
    };

    private const string UrlPathTag  = "url.path";
    private const string UrlQueryTag = "url.query";
    private const string UrlFullTag  = "url.full";

    /// <summary>
    /// The Application Insights connection string, or null when none is configured (whitespace counts as
    /// none): <see cref="ConnectionStringSetting"/> first, then <see cref="ConnectionStringConfigKey"/>.
    /// </summary>
    public static string? ResolveConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var value = configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(value))
            value = configuration[ConnectionStringConfigKey];

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Registers Application Insights when a connection string is configured, and nothing otherwise.
    /// Returns whether telemetry is on, so Program.cs can say so once the logger exists.
    /// </summary>
    public static bool AddFinanciaTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return AddFinanciaTelemetry(builder.Services, builder.Configuration);
    }

    /// <summary>
    /// <see cref="AddFinanciaTelemetry(WebApplicationBuilder)"/> on its parts: <paramref name="configuration"/>
    /// must be the app's own configuration (the forced settings are added to it).
    /// </summary>
    internal static bool AddFinanciaTelemetry(IServiceCollection services, IConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = ResolveConnectionString(configuration);
        if (connectionString is null)
            return false;

        configuration.AddInMemoryCollection(ForcedSettings);

        // The SDK binds the "ApplicationInsights" section onto these options BEFORE this action runs, so
        // ??= keeps an operator's ApplicationInsights__SamplingRatio / __EnableTraceBasedLogsSampler.
        services.AddApplicationInsightsTelemetry(options =>
        {
            options.ConnectionString              = connectionString;
            options.SamplingRatio               ??= 1.0f;
            options.EnableTraceBasedLogsSampler ??= false;
        });

        services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.EnrichWithHttpRequest  = (activity, _) => ScrubRequestUrl(activity);
            options.EnrichWithHttpResponse = (activity, response) => TagTenant(activity, response.HttpContext);
        });

        services.Configure<HttpClientTraceInstrumentationOptions>(options =>
        {
            options.EnrichWithHttpRequestMessage = (activity, request) => ScrubDependencyUrl(activity, request.RequestUri);
        });

        return true;
    }

    /// <summary>
    /// Writes the one startup line that says whether telemetry is on. Never the connection string.
    /// </summary>
    public static void LogStatus(ILogger logger, bool telemetryEnabled)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (telemetryEnabled)
            logger.LogInformation(
                "Application Insights telemetry is on (requests, dependencies, exceptions and logs; no sampling)");
        else
            logger.LogInformation(
                "Application Insights telemetry is off: no {Setting} configured", ConnectionStringSetting);
    }

    /// <summary>
    /// The inbound request's URL as exported: <c>url.path</c> scrubbed of identifiers, <c>url.query</c>
    /// removed. The request NAME is the route template and needs nothing.
    /// </summary>
    internal static void ScrubRequestUrl(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (activity.GetTagItem(UrlPathTag) is string path)
            activity.SetTag(UrlPathTag, UrlRedaction.ScrubPath(path));

        if (activity.GetTagItem(UrlQueryTag) is not null)
            activity.SetTag(UrlQueryTag, null);
    }

    /// <summary>
    /// Sets <see cref="TenantIdTag"/> from the TenantContext either tenant middleware stored, whether it came
    /// from the X-Tenant-ID header or the JWT fallback. Read at response time, so the middleware order is
    /// untouched; a request no tenant was resolved for gets no tag.
    /// </summary>
    internal static void TagTenant(Activity activity, HttpContext? httpContext)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (httpContext?.Items.TryGetValue(TenantContextItemKey, out var item) == true
            && item is TenantContext { TenantId: { Length: > 0 } tenantId })
        {
            activity.SetTag(TenantIdTag, tenantId);
        }
    }

    /// <summary>
    /// The outbound call's URL as exported (the dependency's data and name): scheme, host, port and the
    /// scrubbed path, without the query string.
    /// </summary>
    internal static void ScrubDependencyUrl(Activity activity, Uri? requestUri)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var scrubbed = UrlRedaction.ScrubAbsoluteUrl(requestUri);
        if (scrubbed is not null)
            activity.SetTag(UrlFullTag, scrubbed);
    }
}
