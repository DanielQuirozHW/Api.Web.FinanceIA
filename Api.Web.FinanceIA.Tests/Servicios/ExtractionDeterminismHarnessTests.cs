// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Opt-in measurement harness, not a regression guard: it analyses the SAME balance-sheet PDF
/// N times and reports which outputs differ, so the alert rule engine can be designed from
/// evidence rather than assumption. Its two products are (a) how much the extraction varies
/// run to run, and (b) the seed catalogue of alert codes — which are stable enough to become
/// deterministic rules, and which severities move on their own.
///
/// Gated by <see cref="EnvironmentFactAttribute"/>: with FINANCIA_DETERMINISM_PDF unset it
/// reports as *skipped*, so a normal <c>dotnet test</c> pass never calls the paid API. No file
/// path, client name, CUIT or figure appears in this source — every input arrives by
/// environment variable, and the report is written outside the repository.
///
/// The assertion is deliberately weak. It checks that the harness ran and produced a report;
/// it never asserts the model was stable, because measuring that instability is the point.
/// </summary>
public sealed class ExtractionDeterminismHarnessTests
{
    // ── Environment contract ──────────────────────────────────────────────────

    /// <summary>The single switch. Unset ⇒ the test skips.</summary>
    private const string PdfPathVariable    = "FINANCIA_DETERMINISM_PDF";
    private const string RunsVariable       = "FINANCIA_DETERMINISM_RUNS";
    private const string OutputVariable     = "FINANCIA_DETERMINISM_OUT";
    private const string DocumentTypeVar    = "FINANCIA_DETERMINISM_DOCTYPE";
    private const string FiscalYearVariable = "FINANCIA_DETERMINISM_FISCALYEAR";
    private const string ApiKeyFallbackVar  = "ANTHROPIC_API_KEY";

    private const int    DefaultRuns         = 5;
    private const string DefaultDocumentType = "Balance";

    private readonly ITestOutputHelper _output;

    public ExtractionDeterminismHarnessTests(ITestOutputHelper output) => _output = output;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [EnvironmentFact(PdfPathVariable)]
    public async Task AnalyzeDocument_RepeatedOnTheSamePdf_ReportsOutputVariance()
    {
        var pdfPath = Environment.GetEnvironmentVariable(PdfPathVariable)!;

        if (!File.Exists(pdfPath))
        {
            throw new InvalidOperationException(
                $"{PdfPathVariable} is set but points to a file that does not exist. " +
                "Point it at a readable balance-sheet PDF.");
        }

        var configuration = BuildConfiguration();

        if (string.IsNullOrWhiteSpace(configuration["Anthropic:ApiKey"]))
        {
            throw new InvalidOperationException(
                $"No Anthropic API key. Set Anthropic__ApiKey or {ApiKeyFallbackVar} in the environment. " +
                $"({PdfPathVariable} is set, so this run was requested deliberately and is not skipped.)");
        }

        // Read once: every run must see byte-identical input, or the comparison is meaningless.
        var pdfBytes     = await File.ReadAllBytesAsync(pdfPath).ConfigureAwait(false);
        var runCount     = ReadRunCount();
        var documentType = Environment.GetEnvironmentVariable(DocumentTypeVar) is { Length: > 0 } dt
            ? dt
            : DefaultDocumentType;
        var fiscalYear   = ReadFiscalYear();

        using var httpClientFactory = new SingleClientHttpClientFactory();

        var claudeService = new ClaudeService(
            httpClientFactory,
            configuration,
            NullLogger<ClaudeService>.Instance,
            new BalanceIntegrityService(),
            // The default model: this harness measures run-to-run variance of today's extraction.
            // Comparing models is ExtractionModelComparisonHarnessTests' job.
            Options.Create(new ExtractionModelOptions()),
            // As production: a scan's second pass asks the live orientation detector.
            new ClaudeScanOrientationDetector(httpClientFactory, NullLogger<ClaudeScanOrientationDetector>.Instance));

        // The very service the controller uses, so this measures extraction variance
        // rather than a reimplementation of the ratio maths.
        IRatioCalculationService ratioService = new RatioCalculationService();

        var outcomes = new List<RunOutcome>(runCount);

        for (var index = 1; index <= runCount; index++)
        {
            outcomes.Add(await RunOnceAsync(claudeService, ratioService, pdfBytes, documentType, fiscalYear, index)
                .ConfigureAwait(false));
        }

        var header = new DeterminismReportHeader(
            GeneratedUtc : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            RequestedRuns: runCount,
            PromptVersion: ClaudeService.ExtractionPromptVersion,
            DocumentType : documentType,
            FiscalYear   : fiscalYear,
            PdfByteLength: pdfBytes.LongLength,
            PdfSha256    : Sha256Hex(pdfBytes));

        var reportPath = ResolveReportPath();
        var report     = ExtractionDeterminismReport.Build(header, outcomes);

        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(reportPath, report).ConfigureAwait(false);

        // Short summary only — the detail belongs in the file, not the runner log.
        var succeeded = outcomes.Count(o => o.Succeeded);
        _output.WriteLine($"Determinism harness: {succeeded}/{runCount} runs succeeded.");
        _output.WriteLine($"Prompt version {ClaudeService.ExtractionPromptVersion}, report written to:");
        _output.WriteLine(reportPath);

        Assert.True(File.Exists(reportPath), $"The report was not written to {reportPath}.");
        Assert.True(
            succeeded > 0,
            $"All {runCount} runs failed; see {reportPath} for the per-run errors.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One extraction plus its deterministic ratio recompute. Fault-isolated: a failed run is
    /// recorded and the batch carries on, so a transient API error costs one run, not the lot.
    /// </summary>
    private static async Task<RunOutcome> RunOnceAsync(
        IClaudeService           claudeService,
        IRatioCalculationService ratioService,
        byte[]                   pdfBytes,
        string                   documentType,
        int?                     fiscalYear,
        int                      index)
    {
        ClaudeAnalysisResult result;

        try
        {
            result = await claudeService
                .AnalyzeDocumentAsync(pdfBytes, documentType, fiscalYear, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RunOutcome(
                Index: index, Succeeded: false, Error: DescribeFailure(ex),
                BalanceJson: null, AlertsJson: null, AnomaliesJson: null,
                ConfidenceScore: null, HighestAlertLevel: null,
                ExtractionMethod: null, ModelUsed: null, OutputHash: null,
                InputTokens: null, OutputTokens: null, ComputedRatios: null);
        }

        return new RunOutcome(
            Index            : index,
            Succeeded        : true,
            Error            : null,
            BalanceJson      : result.BalanceJson,
            AlertsJson       : result.AlertsJson,
            AnomaliesJson    : result.AnomaliesJson,
            ConfidenceScore  : result.ConfidenceScore,
            HighestAlertLevel: result.HighestAlertLevel,
            ExtractionMethod : result.ExtractionMethod,
            ModelUsed        : result.ModelUsed,
            OutputHash       : result.OutputHash,
            InputTokens      : result.InputTokens,
            OutputTokens     : result.OutputTokens,
            ComputedRatios   : ComputeRatios(result.BalanceJson, ratioService));
    }

    /// <summary>
    /// ClaudeApiException's Message is the client-safe Spanish string, which alone cannot tell
    /// a misconfigured key (401) from a transient upstream fault (5xx). The status code and any
    /// inner exception are folded in so the report says why a run was lost.
    /// </summary>
    private static string DescribeFailure(Exception ex)
    {
        var status = ex is ClaudeApiException { ApiStatusCode: { } code }
            ? $" (API status {code.ToString(CultureInfo.InvariantCulture)})"
            : string.Empty;

        var inner = ex.InnerException is null
            ? string.Empty
            : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";

        return $"{ex.GetType().Name}: {ex.Message}{status}{inner}";
    }

    /// <summary>
    /// Mirrors AnalysisController.cs:366-372 — deserialise BalanceJson, then Calculate.
    /// A malformed payload yields null ratios rather than losing the run's alert evidence.
    /// </summary>
    private static RatiosData? ComputeRatios(string? balanceJson, IRatioCalculationService ratioService)
    {
        if (string.IsNullOrWhiteSpace(balanceJson)) return null;

        try
        {
            var accounts = JsonSerializer.Deserialize<BalanceAccounts>(
                balanceJson, ExtractionDeterminismReport.JsonOpts);

            return accounts is null ? null : ratioService.Calculate(accounts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = Environment.GetEnvironmentVariable(ApiKeyFallbackVar)
            })
            // Last wins, so Anthropic__ApiKey overrides the convenience fallback above.
            .AddEnvironmentVariables()
            .Build();

    private static int ReadRunCount()
    {
        var raw = Environment.GetEnvironmentVariable(RunsVariable);

        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
                ? parsed
                : DefaultRuns;
    }

    private static int? ReadFiscalYear()
    {
        var raw = Environment.GetEnvironmentVariable(FiscalYearVariable);

        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    /// <summary>Defaults to a temp path — never inside the repository.</summary>
    private static string ResolveReportPath()
    {
        var configured = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(Path.GetTempPath(), $"financia-determinism-{stamp}.md");
    }

    private static string Sha256Hex(byte[] value)
    {
        var bytes = SHA256.HashData(value);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hands ClaudeService a real client with the same 120-second timeout Program.cs:140-143
    /// configures for "ClaudeClient". One instance is reused across runs so the batch does not
    /// churn sockets; unlike the other HTTP stubs in this project it deliberately does NOT
    /// intercept — this harness must reach the live API.
    /// </summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(120) };

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }
}
