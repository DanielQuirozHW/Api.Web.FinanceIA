// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Diagnostics;
using Api.Web.FinanceIA.Clases.Telemetry;
using Api.Web.FinanceIA.Models;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases.Telemetry;

/// <summary>
/// <see cref="TelemetryRegistration"/>: Application Insights 3.x is registered only with a connection string
/// (the app starts and stops either way), nothing is sampled, and what leaves carries no query string, no
/// CUIT in a path and the resolved tenant.
/// </summary>
public sealed class TelemetryRegistrationTests
{
    // A syntactically valid connection string whose endpoints refuse at once: nothing leaves the machine.
    private const string DummyConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:9/;LiveEndpoint=http://127.0.0.1:9/";

    private static readonly TimeSpan StopBound = TimeSpan.FromSeconds(30);

    // ── On / off ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_connection_string_nothing_is_registered_and_the_app_starts_and_stops()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            // Hermetic: a value present in the machine's environment must not turn it on here.
            [TelemetryRegistration.ConnectionStringSetting]   = " ",
            [TelemetryRegistration.ConnectionStringConfigKey] = ""
        });

        var enabled = builder.AddFinanciaTelemetry();

        Assert.False(enabled);
        await using var app = builder.Build();
        Assert.Null(app.Services.GetService<TracerProvider>());
        Assert.Null(app.Services.GetService<LoggerProvider>());

        await StartServeAndStopAsync(app);
    }

    [Fact]
    public async Task With_a_connection_string_it_is_registered_unsampled_and_the_app_starts_and_stops()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            [TelemetryRegistration.ConnectionStringSetting]          = DummyConnectionString,
            ["ApplicationInsights:EnableQuickPulseMetricStream"]     = "false",
        });

        var enabled = builder.AddFinanciaTelemetry();
        // Test-only: a refused export must not leave files in the machine's temp directory.
        builder.Services.PostConfigure<AzureMonitorExporterOptions>(o => o.DisableOfflineStorage = true);
        var spans = new List<Activity>();
        builder.Services.ConfigureOpenTelemetryTracerProvider(b => b.AddProcessor(new CapturingSpanProcessor(spans)));

        Assert.True(enabled);
        await using var app = builder.Build();
        Assert.NotNull(app.Services.GetService<TracerProvider>());

        var exporter = app.Services.GetRequiredService<IOptionsMonitor<AzureMonitorExporterOptions>>().Get(Options.DefaultName);
        Assert.Equal(DummyConnectionString, exporter.ConnectionString);
        Assert.Equal(1.0f, exporter.SamplingRatio);
        Assert.Null(exporter.TracesPerSecond);               // the rate-limited sampler is off
        Assert.False(exporter.EnableTraceBasedLogsSampler);  // no log is dropped with its request

        await StartServeAndStopAsync(app);

        // The live request span, as the exporter reads it: no CUIT in the path, no query string.
        Activity request;
        lock (spans)
            request = Assert.Single(spans, a => a.Kind == ActivityKind.Server);
        Assert.Equal("/api/entities/cuit/{id}", request.GetTagItem("url.path"));
        Assert.Null(request.GetTagItem("url.query"));
        Assert.Null(request.GetTagItem(TelemetryRegistration.TenantIdTag)); // no tenant middleware here
    }

    [Fact]
    public void An_operator_override_in_the_ApplicationInsights_section_wins()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            [TelemetryRegistration.ConnectionStringSetting]      = DummyConnectionString,
            ["ApplicationInsights:SamplingRatio"]                = "0.5",
            ["ApplicationInsights:EnableTraceBasedLogsSampler"]  = "true",
            ["ApplicationInsights:EnableQuickPulseMetricStream"] = "false",
        });

        builder.AddFinanciaTelemetry();
        using var provider = builder.Services.BuildServiceProvider();

        var exporter = provider.GetRequiredService<IOptionsMonitor<AzureMonitorExporterOptions>>().Get(Options.DefaultName);
        Assert.Equal(0.5f, exporter.SamplingRatio);
        Assert.True(exporter.EnableTraceBasedLogsSampler);
    }

    [Fact]
    public void Redaction_and_sql_parameter_settings_are_forced_over_any_app_setting()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            [TelemetryRegistration.ConnectionStringSetting]                          = DummyConnectionString,
            ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"]     = "true",
            ["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"]     = "true",
            ["OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_DB_QUERY_PARAMETERS"] = "true",
        });

        builder.AddFinanciaTelemetry();

        foreach (var (key, value) in TelemetryRegistration.ForcedSettings)
            Assert.Equal(value, builder.Configuration[key]);
    }

    [Theory]
    [InlineData("cs-env", "cs-config", "cs-env")]
    [InlineData(null, "cs-config", "cs-config")]
    [InlineData("  ", "cs-config", "cs-config")]
    [InlineData(" cs-env ", null, "cs-env")]
    [InlineData(null, null, null)]
    [InlineData("", " ", null)]
    public void ResolveConnectionString_reads_the_App_Setting_first_then_the_section(string? env, string? section, string? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TelemetryRegistration.ConnectionStringSetting]   = env,
                [TelemetryRegistration.ConnectionStringConfigKey] = section
            })
            .Build();

        Assert.Equal(expected, TelemetryRegistration.ResolveConnectionString(configuration));
    }

    // ── Startup line ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, "Application Insights telemetry is off: no APPLICATIONINSIGHTS_CONNECTION_STRING configured")]
    [InlineData(true, "Application Insights telemetry is on")]
    public void LogStatus_writes_one_information_line(bool enabled, string expectedStart)
    {
        var logger = new RecordingLogger();

        TelemetryRegistration.LogStatus(logger, enabled);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith(expectedStart, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("InstrumentationKey", entry.Message, StringComparison.Ordinal);
    }

    // ── Logging levels ────────────────────────────────────────────────────────

    [Fact]
    public void The_logging_provider_reads_its_levels_from_Logging_OpenTelemetry()
    {
        // By name: ProviderAliasAttribute is defined in two assemblies of this test's graph.
        var alias = typeof(OpenTelemetryLoggerProvider).GetCustomAttributesData()
            .Single(a => a.AttributeType.Name == "ProviderAliasAttribute")
            .ConstructorArguments[0].Value;

        Assert.Equal("OpenTelemetry", alias);
    }

    [Fact]
    public void Appsettings_sends_our_namespace_at_information_and_microsoft_and_system_at_warning()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.Equal("Warning",     configuration["Logging:OpenTelemetry:LogLevel:Default"]);
        Assert.Equal("Information", configuration["Logging:OpenTelemetry:LogLevel:Api.Web.FinanceIA"]);
        Assert.Equal("Warning",     configuration["Logging:OpenTelemetry:LogLevel:Microsoft"]);
        Assert.Equal("Warning",     configuration["Logging:OpenTelemetry:LogLevel:System"]);
        Assert.Null(configuration[TelemetryRegistration.ConnectionStringSetting]);
        Assert.Null(configuration[TelemetryRegistration.ConnectionStringConfigKey]);
    }

    [Fact]
    public async Task With_those_levels_an_information_line_of_ours_passes_and_one_of_Microsoft_does_not()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            [TelemetryRegistration.ConnectionStringSetting]           = DummyConnectionString,
            ["ApplicationInsights:EnableQuickPulseMetricStream"]      = "false",
            ["Logging:OpenTelemetry:LogLevel:Default"]                = "Warning",
            ["Logging:OpenTelemetry:LogLevel:Api.Web.FinanceIA"]      = "Information",
            ["Logging:OpenTelemetry:LogLevel:Microsoft"]              = "Warning",
            ["Logging:OpenTelemetry:LogLevel:System"]                 = "Warning",
        });
        builder.AddFinanciaTelemetry();
        builder.Services.PostConfigure<AzureMonitorExporterOptions>(o => o.DisableOfflineStorage = true);
        var exported = new List<string>();
        builder.Services.ConfigureOpenTelemetryLoggerProvider(b => b.AddProcessor(new CapturingLogProcessor(exported)));

        await using (var app = builder.Build())
        {
            var factory = app.Services.GetRequiredService<ILoggerFactory>();
            factory.CreateLogger("Api.Web.FinanceIA.Servicios.ClaudeService").LogInformation("ours-info");
            factory.CreateLogger("Api.Web.FinanceIA.Servicios.ClaudeService").LogError("ours-error");
            factory.CreateLogger("Microsoft.AspNetCore.Routing").LogInformation("microsoft-info");
            factory.CreateLogger("Microsoft.AspNetCore.Routing").LogWarning("microsoft-warning");
            factory.CreateLogger("System.Net.Http.HttpClient").LogInformation("system-info");
        }

        Assert.Contains("ours-info", exported);
        Assert.Contains("ours-error", exported);
        Assert.Contains("microsoft-warning", exported);
        Assert.DoesNotContain("microsoft-info", exported);
        Assert.DoesNotContain("system-info", exported);
    }

    // ── What leaves: URLs and the tenant ─────────────────────────────────────

    [Fact]
    public void A_request_url_loses_its_query_and_the_cuit_in_its_path()
    {
        using var activity = new Activity("request");
        activity.SetTag("url.path", "/api/entities/cuit/30712345678");
        activity.SetTag("url.query", "?token=Redacted");

        TelemetryRegistration.ScrubRequestUrl(activity);

        Assert.Equal("/api/entities/cuit/{id}", activity.GetTagItem("url.path"));
        Assert.Null(activity.GetTagItem("url.query"));
    }

    [Theory]
    [InlineData("https://api.bcra.gob.ar/centraldedeudores/v1.0/Deudas/30712345678?x=1",
                "https://api.bcra.gob.ar/centraldedeudores/v1.0/Deudas/{id}")]
    [InlineData("https://ws01.nosis.com/rest/variables?Documento=30712345678&VR=1",
                "https://ws01.nosis.com/rest/variables")]
    [InlineData("https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=AIzaSecret",
                "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword")]
    [InlineData("https://legajounicoapi.produccion.gob.ar/lufe/entidades/30-71234567-8/balances",
                "https://legajounicoapi.produccion.gob.ar/lufe/entidades/{id}/balances")]
    [InlineData("https://user:pass@api.anthropic.com:8443/v1/messages",
                "https://api.anthropic.com:8443/v1/messages")]
    public void A_dependency_url_keeps_host_and_path_but_no_query_identifier_or_user_info(string url, string expected)
    {
        using var activity = new Activity("dependency");
        activity.SetTag("url.full", url);

        TelemetryRegistration.ScrubDependencyUrl(activity, new Uri(url));

        Assert.Equal(expected, activity.GetTagItem("url.full"));
    }

    [Fact]
    public void The_tenant_resolved_by_either_middleware_becomes_the_TenantId_dimension()
    {
        using var activity = new Activity("request");
        var context = new DefaultHttpContext();
        context.Items["TenantContext"] = Tenant("SgrOneClickSandboxFB2");

        TelemetryRegistration.TagTenant(activity, context);

        Assert.Equal("SgrOneClickSandboxFB2", activity.GetTagItem(TelemetryRegistration.TenantIdTag));
    }

    [Fact]
    public void A_request_without_a_tenant_gets_no_TenantId()
    {
        using var activity = new Activity("request");

        TelemetryRegistration.TagTenant(activity, new DefaultHttpContext());
        TelemetryRegistration.TagTenant(activity, null);

        Assert.Null(activity.GetTagItem(TelemetryRegistration.TenantIdTag));
    }

    [Fact]
    public void The_enrichers_are_wired_into_the_instrumentation_options()
    {
        var builder = NewBuilder(new Dictionary<string, string?>
        {
            [TelemetryRegistration.ConnectionStringSetting]      = DummyConnectionString,
            ["ApplicationInsights:EnableQuickPulseMetricStream"] = "false",
        });
        builder.AddFinanciaTelemetry();
        using var provider = builder.Services.BuildServiceProvider();

        var aspNetCore = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(Options.DefaultName);
        var http       = provider.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>().Get(Options.DefaultName);

        using var request = new Activity("request");
        request.SetTag("url.path", "/api/bcra/deudores/20123456789");
        aspNetCore.EnrichWithHttpRequest!(request, new DefaultHttpContext().Request);
        Assert.Equal("/api/bcra/deudores/{id}", request.GetTagItem("url.path"));

        var context = new DefaultHttpContext();
        context.Items["TenantContext"] = Tenant("tenant-a");
        aspNetCore.EnrichWithHttpResponse!(request, context.Response);
        Assert.Equal("tenant-a", request.GetTagItem(TelemetryRegistration.TenantIdTag));

        using var dependency = new Activity("dependency");
        using var message    = new HttpRequestMessage(HttpMethod.Get, "https://sgr.casfog.com.ar/api/v1.1/central_deudores/30712345678");
        http.EnrichWithHttpRequestMessage!(dependency, message);
        Assert.Equal("https://sgr.casfog.com.ar/api/v1.1/central_deudores/{id}", dependency.GetTagItem("url.full"));
    }

    [Theory]
    [InlineData("/api/analyses/12/runs", "/api/analyses/12/runs")]
    [InlineData("/api/analyses/2024", "/api/analyses/2024")]
    [InlineData("/api/entities/cuit/30712345678", "/api/entities/cuit/{id}")]
    [InlineData("/dni/12345678/", "/dni/{id}/")]
    [InlineData("/a/1234567", "/a/{id}")]
    [InlineData("/a/123456", "/a/123456")]
    [InlineData("", "")]
    public void ScrubPath_hides_runs_of_seven_or_more_digits(string path, string expected) =>
        Assert.Equal(expected, UrlRedaction.ScrubPath(path));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TenantContext Tenant(string tenantId) => new()
    {
        TenantId               = tenantId,
        FirebaseProjectId      = "project",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "key",
    };

    private static WebApplicationBuilder NewBuilder(IDictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder;
    }

    private static async Task StartServeAndStopAsync(WebApplication app)
    {
        app.MapGet("/api/entities/cuit/{cuit}", (string cuit) => Results.Ok());

        using var cts = new CancellationTokenSource(StopBound);
        await app.StartAsync(cts.Token);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using (var client = new HttpClient { BaseAddress = new Uri(address) })
        {
            var response = await client.GetAsync("/api/entities/cuit/30712345678?token=abc", cts.Token);
            Assert.True(response.IsSuccessStatusCode);
        }

        var stopwatch = Stopwatch.StartNew();
        await app.StopAsync(cts.Token);
        Assert.True(stopwatch.Elapsed < StopBound, $"StopAsync took {stopwatch.Elapsed}");
    }

    private sealed class CapturingSpanProcessor(List<Activity> spans) : OpenTelemetry.BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (spans)
                spans.Add(data);
        }
    }

    private sealed class CapturingLogProcessor(List<string> exported) : OpenTelemetry.BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            lock (exported)
                exported.Add(data.FormattedMessage ?? data.Body ?? string.Empty);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
