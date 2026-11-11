// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Globalization;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Opt-in LIVE harness for the legajo extraction (M10 Phase 2b and 3a): runs the real extraction (the same service,
/// read-mode choice, second-pass rule and C# assessment the worker uses) on ONE local PDF and prints
/// the passes, the checks and the review queues. It calls the paid API and never runs in CI: with
/// FINANCIA_LEGAJO_PDF unset it reports as skipped.
///
/// <para>
/// Environment:
/// <list type="bullet">
/// <item>FINANCIA_LEGAJO_PDF — path to the PDF. ⚠ A real person's paperwork: keep it OUTSIDE the
/// repository and never commit it.</item>
/// <item>FINANCIA_LEGAJO_TYPE — one of LegajoExtractionPrompts.SupportedTypes (required): BienesPersonales,
/// ManifestacionBienes, Ganancias, DdjjIva, DdjjIibb, CertificacionIngresos or Monotributo.</item>
/// <item>FINANCIA_LEGAJO_CUIT — optional: the legajo's CUIT to compare the holder with. Without it the
/// holder comparison is reported as "not compared".</item>
/// <item>FINANCIA_LEGAJO_PERIOD_END — optional YYYY-MM-DD, for the fiscal-year and period checks.</item>
/// <item>Anthropic__ApiKey or ANTHROPIC_API_KEY; optionally Anthropic__LegajoExtractionModel.</item>
/// </list>
/// </para>
///
/// <para>
/// The printout shows check messages, which quote figures from the document: it is personal data on
/// your screen. Do not paste it into an issue, a PR or a chat. No figure, name or CUIT is in this source.
/// </para>
/// </summary>
public sealed class LegajoExtractionHarnessTests
{
    private const string PdfVariable       = "FINANCIA_LEGAJO_PDF";
    private const string TypeVariable      = "FINANCIA_LEGAJO_TYPE";
    private const string CuitVariable      = "FINANCIA_LEGAJO_CUIT";
    private const string PeriodEndVariable = "FINANCIA_LEGAJO_PERIOD_END";

    private readonly ITestOutputHelper _output;

    public LegajoExtractionHarnessTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(PdfVariable)]
    public async Task ExtractOneRealDocument_PrintsPassesChecksAndQueues()
    {
        var path = Environment.GetEnvironmentVariable(PdfVariable)!;
        if (!File.Exists(path))
            throw new InvalidOperationException($"{PdfVariable} points to a file that does not exist.");

        var schema = LegajoExtractionPrompts.For(Environment.GetEnvironmentVariable(TypeVariable))
            ?? throw new InvalidOperationException(
                $"Set {TypeVariable} to one of: {string.Join(", ", LegajoExtractionPrompts.SupportedTypes)}.");

        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
                     ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Set Anthropic__ApiKey or ANTHROPIC_API_KEY (the run was requested deliberately).");

        var model = Environment.GetEnvironmentVariable("Anthropic__LegajoExtractionModel");
        var legajoOptions = new LegajoOptions();

        using var factory = new LiveHttpClientFactory(TimeSpan.FromSeconds(legajoOptions.ExtractionTimeoutSeconds));
        var apiCalls = new RecordingApiCallLogService();
        var service = new LegajoExtractionService(
            new LegajoMessagesClient(factory, apiCalls, NullLogger<LegajoMessagesClient>.Instance),
            new PdfToImagePageRenderer(),
            Options.Create(new LegajoExtractionOptions
            {
                ApiKey = apiKey,
                LegajoExtractionModel = string.IsNullOrWhiteSpace(model) ? LegajoExtractionOptions.DefaultModel : model
            }),
            Options.Create(legajoOptions),
            NullLogger<LegajoExtractionService>.Instance);

        var bytes     = await File.ReadAllBytesAsync(path);
        var quality   = PageTextQualityAnalyzer.Analyze(bytes);
        var context   = new LegajoCallContext("harness", Guid.NewGuid());
        var cuit      = Environment.GetEnvironmentVariable(CuitVariable) ?? string.Empty;
        var periodEnd = DateOnly.TryParseExact(Environment.GetEnvironmentVariable(PeriodEndVariable), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ? end : (DateOnly?)null;
        var integrity = new LegajoIntegrityService();

        LegajoExtractionAssessment? Assess(LegajoExtractionPassResult pass)
            => pass is { Succeeded: true, Reading: { } reading }
                ? LegajoExtractionAssessor.Assess(schema, reading.Result, integrity, periodEnd, cuit,
                                                  legajoOptions.FieldConfidenceThreshold)
                : null;

        _output.WriteLine($"{schema.DocumentType} {schema.PromptVersion}, max_tokens " +
                          LegajoExtractionService.MaxTokensFor(schema, legajoOptions).ToString(CultureInfo.InvariantCulture));

        // The worker's own sequence: pass 1 in the chosen mode, pass 2 as images when the rule says so.
        var readMode = LegajoExtractionService.ChooseReadMode(bytes.LongLength, quality);
        var first = await service.ExtractAsync(context, schema, bytes, quality, readMode, _ => Task.FromResult(true), CancellationToken.None);
        var firstAssessment = Assess(first);
        Print("Pass 1", first, firstAssessment);

        var kept = firstAssessment;
        if (first.ErrorCode == LegajoExtractionErrors.InvalidOutput
            || (firstAssessment is { FailedChecks: > 0 } && first.ReadMode == LegajoReadModes.Document))
        {
            var second = await service.ExtractAsync(context, schema, bytes, quality, LegajoReadModes.Images,
                                                    _ => Task.FromResult(true), CancellationToken.None);
            var secondAssessment = Assess(second);
            Print("Pass 2", second, secondAssessment);

            if (LegajoExtractionSelection.IsBetter(firstAssessment, secondAssessment))
                kept = secondAssessment;
        }

        var totalCost = apiCalls.Calls.Sum(c => c.Cost ?? 0m);
        _output.WriteLine(string.Empty);
        _output.WriteLine($"Pages: {quality.PageCount?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}, " +
                          $"{apiCalls.Calls.Count} call(s), estimated USD {totalCost.ToString("0.0000", CultureInfo.InvariantCulture)}");

        if (kept is null)
        {
            _output.WriteLine("Outcome: Failed (no usable pass).");
            return;
        }

        // Without a legajo CUIT there is nothing to compare the holder with: decide on the queues alone.
        var status = string.IsNullOrWhiteSpace(cuit)
            ? kept.Missing.Count == 0 && kept.Flagged.Count == 0 ? LegajoStatuses.Ready : LegajoStatuses.NeedsReview
            : kept.Status;
        _output.WriteLine($"Outcome: {status} (holder CUIT: {(string.IsNullOrWhiteSpace(cuit) ? "not compared" : kept.HolderCuitMatches?.ToString() ?? "not read")})");

        Assert.NotEmpty(apiCalls.Calls);
    }

    private void Print(string label, LegajoExtractionPassResult pass, LegajoExtractionAssessment? assessment)
    {
        _output.WriteLine($"── {label}: {pass.ReadMode}, {(pass.Succeeded ? "ok" : pass.ErrorCode)}, " +
                          $"{pass.InputTokens}/{pass.OutputTokens} tokens, {pass.DurationMs} ms");

        if (pass.Reading is { IsValid: false } rejected)
            foreach (var violation in rejected.Violations)
                _output.WriteLine($"   violation {violation}");

        if (assessment is null)
            return;

        _output.WriteLine($"   unit {assessment.Checks.AmountsUnit}{(assessment.Checks.AmountsUnitAssumed ? " (assumed)" : string.Empty)}, " +
                          $"mean confidence {assessment.MeanConfidence?.ToString(CultureInfo.InvariantCulture) ?? "-"}");

        foreach (var check in assessment.Checks.Checks)
        {
            _output.WriteLine($"   {(check.Passed ? "PASS" : "FAIL")} {check.Name}{(check.Subject is null ? string.Empty : $" [{check.Subject}]")}" +
                              (check.Message is null ? string.Empty : $" — {check.Message}"));
            if (!check.Passed)
                _output.WriteLine($"        flags {string.Join(", ", check.PathsToFlag)}");
        }

        foreach (var skipped in assessment.Checks.Skipped)
            _output.WriteLine($"   SKIP {skipped.Name}{(skipped.Subject is null ? string.Empty : $" [{skipped.Subject}]")}");

        foreach (var missing in assessment.Missing)
            _output.WriteLine($"   missing {missing}");

        foreach (var flagged in assessment.Flagged)
            _output.WriteLine($"   flagged {flagged.FieldPath}: {string.Join(", ", flagged.Reasons)}");
    }

    /// <summary>A real client (it must reach the live API), one per run.</summary>
    private sealed class LiveHttpClientFactory(TimeSpan timeout) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new() { Timeout = timeout };

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }
}
