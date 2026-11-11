// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Diagnostics;
using Api.Web.FinanceIA.Clases.SchemaVerification;
using Api.Web.FinanceIA.Clases.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The <c>--verify-schema</c> deploy gate keeps exiting cleanly now that the app can register Application
/// Insights: the mode returns before the web host (and so the telemetry registration) exists, and an
/// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> in its environment — as on an App Service — changes nothing.
/// </summary>
public sealed class SchemaVerificationTelemetryTests
{
    private const string DummyConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:9/";

    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task RunAsync_with_a_telemetry_connection_string_and_no_target_still_returns_could_not_run()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TelemetryRegistration.ConnectionStringSetting] = DummyConnectionString,
                ["FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT"]          = "true",
                ["FINANCIA_SCHEMA_VERIFY_CONNECTIONS"]           = "",
                ["ConnectionStrings:FinanciaDefault"]            = "",
            })
            .Build();

        var stopwatch = Stopwatch.StartNew();
        var exitCode  = await SchemaVerificationCommand.RunAsync(configuration, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, exitCode); // could not run: no Financia database to compare
        Assert.True(stopwatch.Elapsed < ExitBound);
    }

    [Fact]
    public async Task The_built_binary_in_verify_schema_mode_exits_promptly_with_telemetry_configured()
    {
        // The API's own build output, copied beside the tests by the project reference.
        var dll = Path.Combine(AppContext.BaseDirectory, "Api.Web.FinanceIA.dll");
        Assert.True(File.Exists(dll), $"{dll} not found");
        Assert.True(File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json")), "the API's runtimeconfig is not beside the tests");

        var start = new ProcessStartInfo(DotnetHost(), $"\"{dll}\" --verify-schema")
        {
            WorkingDirectory       = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        start.Environment[TelemetryRegistration.ConnectionStringSetting]   = DummyConnectionString;
        start.Environment["FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT"]           = "true";
        start.Environment["FINANCIA_SCHEMA_VERIFY_CONNECTIONS"]            = "";
        start.Environment["ConnectionStrings__FinanciaDefault"]            = "";
        start.Environment["DOTNET_ENVIRONMENT"]                            = "Testing";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(ExitBound);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"--verify-schema did not exit within {ExitBound}");
        }

        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 2, $"exit code {process.ExitCode}; output:\n{output}");
        Assert.DoesNotContain("Application Insights telemetry is", output, StringComparison.Ordinal);
    }

    private static string DotnetHost()
    {
        // `dotnet test` exports the host it runs under; fall back to the one on PATH.
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet";
    }
}
