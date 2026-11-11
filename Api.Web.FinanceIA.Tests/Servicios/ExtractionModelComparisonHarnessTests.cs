// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Opt-in LIVE harness, never a CI test: runs the REAL balance extraction over a folder of real
/// balance PDFs with each configured model (and effort), then the deterministic ratios and a score
/// with BCRA and macro held FIXED, and compares every variant against the first model listed — so the
/// decision to move a client to another <c>Anthropic:ExtractionModel</c> is made on evidence.
///
/// <para>
/// Gated by <see cref="EnvironmentFactAttribute"/> on FINANCIA_MODELCOMPARE_DIR: unset, it reports as
/// skipped and calls nothing. It spends real money, so before the first call it prints the plan and an
/// upper-bound cost and stops unless FINANCIA_MODELCOMPARE_CONFIRM=yes — a typo never spends anything.
/// </para>
///
/// <para>
/// ⚠ The PDFs are clients' balances and the three output files contain their figures: the input folder
/// and the output folder must be OUTSIDE the repository (enforced), and the files open with the warning
/// "datos de clientes: no subir al repo ni compartir". The test output carries only counts, aliases
/// (doc-01…), error types and the paths written — never a file name, a figure or a response body.
/// </para>
///
/// <para>
/// To compare two COMMITS (e.g. a prompt change) rather than two models: run the "before" commit, then the
/// "after" commit with FINANCIA_MODELCOMPARE_BASELINE_RUNS pointing at the first run's <c>…-runs.json</c>.
/// The earlier run becomes the baseline group (same PDFs enforced by SHA-256, checked before anything is
/// spent) and the output lists, per run, escalation, text around the JSON, integrity, and per key figure
/// whether it moved — never a value.
/// </para>
///
/// <para>
/// The assertion is deliberately weak: that the harness ran and wrote its report. Whether the models
/// agree is the measurement, not a pass/fail condition.
/// </para>
/// </summary>
public sealed class ExtractionModelComparisonHarnessTests
{
    // ── Environment contract ──────────────────────────────────────────────────

    private const string DirVariable        = "FINANCIA_MODELCOMPARE_DIR";
    private const string OutVariable        = "FINANCIA_MODELCOMPARE_OUT";
    private const string ModelsVariable     = "FINANCIA_MODELCOMPARE_MODELS";
    private const string EffortsVariable    = "FINANCIA_MODELCOMPARE_EFFORTS";
    private const string RunsVariable       = "FINANCIA_MODELCOMPARE_RUNS";
    private const string DocumentTypeVar    = "FINANCIA_MODELCOMPARE_DOCTYPE";
    private const string FiscalYearVariable = "FINANCIA_MODELCOMPARE_FISCALYEAR";
    private const string ConfirmVariable    = "FINANCIA_MODELCOMPARE_CONFIRM";
    private const string BaselineVariable   = "FINANCIA_MODELCOMPARE_BASELINE_RUNS";
    private const string ApiKeyFallbackVar  = "ANTHROPIC_API_KEY";

    private const string DefaultModels       = "claude-sonnet-4-6,claude-sonnet-5-5";
    private const string DefaultDocumentType = "Balance";
    private const string SolutionFileName    = "Api.Web.ServiciosFinancieros.slnx";

    private readonly ITestOutputHelper _output;

    public ExtractionModelComparisonHarnessTests(ITestOutputHelper output) => _output = output;

    // ── Test ──────────────────────────────────────────────────────────────────

    [EnvironmentFact(DirVariable)]
    public async Task CompareExtractionModels_OverAFolderOfBalances_WritesTheReport()
    {
        // ── 1. Validate everything BEFORE any money is spent ────────────────────
        var inputDir  = Path.GetFullPath(Environment.GetEnvironmentVariable(DirVariable)!);
        var outputDir = Environment.GetEnvironmentVariable(OutVariable);

        if (!Directory.Exists(inputDir))
            throw new InvalidOperationException($"{DirVariable} does not point to an existing folder.");
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new InvalidOperationException($"Set {OutVariable} to a folder OUTSIDE the repository: the report contains clients' figures.");

        outputDir = Path.GetFullPath(outputDir);
        GuardOutsideRepository(inputDir, DirVariable);
        GuardOutsideRepository(outputDir, OutVariable);

        var variants = ExtractionModelComparisonReport.ParseVariants(
            Environment.GetEnvironmentVariable(ModelsVariable),
            Environment.GetEnvironmentVariable(EffortsVariable),
            DefaultModels);

        var runs         = ReadPositiveInt(RunsVariable, 1);
        var documentType = Environment.GetEnvironmentVariable(DocumentTypeVar) is { Length: > 0 } dt ? dt : DefaultDocumentType;
        var fiscalYear   = ReadOptionalInt(FiscalYearVariable);

        var configuration = BuildConfiguration();
        if (string.IsNullOrWhiteSpace(configuration["Anthropic:ApiKey"]))
            throw new InvalidOperationException($"Set Anthropic__ApiKey or {ApiKeyFallbackVar}.");

        var pdfPaths = Directory.EnumerateFiles(inputDir)
            .Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
        if (pdfPaths.Count == 0)
            throw new InvalidOperationException($"{DirVariable} holds no .pdf file.");

        var documents = new List<ComparisonDocument>(pdfPaths.Count);
        var bytesByAlias = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < pdfPaths.Count; i++)
        {
            var bytes = await File.ReadAllBytesAsync(pdfPaths[i]);
            var alias = $"doc-{(i + 1).ToString("00", CultureInfo.InvariantCulture)}";
            documents.Add(new ComparisonDocument(alias, Path.GetFileName(pdfPaths[i]), Sha256Hex(bytes), bytes.LongLength, CountPages(bytes)));
            bytesByAlias[alias] = bytes;
        }

        // A previous run's snapshot as the baseline: validated here, before any money is spent.
        ComparisonVariant? baselineVariant = null;
        IReadOnlyList<ComparisonRun> baselineRuns = [];
        var baselinePath = Environment.GetEnvironmentVariable(BaselineVariable);
        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            baselinePath = Path.GetFullPath(baselinePath);
            GuardOutsideRepository(baselinePath, BaselineVariable);
            (baselineVariant, baselineRuns) = ExtractionModelComparisonReport.LoadBaseline(
                await File.ReadAllTextAsync(baselinePath), documents);
            if (variants.Any(v => v.Label == baselineVariant.Label))
                throw new InvalidOperationException($"The baseline label '{baselineVariant.Label}' collides with a variant.");
        }

        // ── 2. The plan, the upper bound, the confirmation ──────────────────────
        var upperBound = variants.Sum(v => documents.Sum(d =>
            ExtractionModelComparisonReport.UpperBoundUsdPerRun(v.Spec, d.Pages, ClaudeService.SystemPrompt.Length))) * runs;

        var plan = new StringBuilder()
            .AppendLine("Model comparison plan:")
            .AppendLine($"  {documents.Count} PDF(s), {documents.Sum(d => d.Pages)} page(s), {documents.Sum(d => d.Bytes) / 1024} KB")
            .AppendLine($"  × {variants.Count} variant(s): {string.Join(", ", variants.Select(v => v.Label))} (baseline: {variants[0].Label})")
            .AppendLine($"  × {runs} run(s) = {documents.Count * variants.Count * runs} AnalyzeDocumentAsync call(s), up to 2 passes each")
            .AppendLine($"  Per-attempt timeout: {configuration["Anthropic:ExtractionTimeoutSeconds"] ?? "the model's own"} s (Anthropic__ExtractionTimeoutSeconds)")
            .AppendLine(baselineVariant is null
                ? "  Baseline: the first variant"
                : $"  Baseline: '{baselineVariant.Label}' from {BaselineVariable} ({baselineRuns.Count} stored run(s), nothing re-sent)")
            .AppendLine($"  Upper-bound cost: USD {upperBound.ToString("0.00", CultureInfo.InvariantCulture)} " +
                        "(both passes at full max_tokens, 5.x tokenizer; retries not included)")
            .ToString();
        _output.WriteLine(plan);

        if (!string.Equals(Environment.GetEnvironmentVariable(ConfirmVariable), "yes", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Nothing was sent. Review the plan above and set {ConfirmVariable}=yes to spend it.{Environment.NewLine}{plan}");
        }

        // ── 3. Run: every PDF × variant × run, in sequence ──────────────────────
        using var httpClientFactory = new LiveHttpClientFactory();
        var ratioService  = new RatioCalculationService();
        var scoring       = await ScoringTestHarness.BuildAsync();
        var fixedBcra     = ScoringTestHarness.BcraNoDebt();
        var fixedMacro    = ScoringTestHarness.MacroBenign();
        var logger        = new TrimWarningRecorder();
        var timeoutSeconds = int.TryParse(configuration["Anthropic:ExtractionTimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
            ? t
            : (int?)null;

        var services = variants.ToDictionary(
            v => v.Label,
            v => new ClaudeService(
                httpClientFactory,
                configuration,
                logger,
                new BalanceIntegrityService(),
                // Anthropic__ExtractionTimeoutSeconds, when set, is honoured as on the App Service: without it Sonnet
                // 4.6 runs at its 120 s code default, and a long balance times out instead of reproducing production.
                Options.Create(new ExtractionModelOptions
                {
                    ExtractionModel          = v.Spec.Id,
                    ExtractionEffort         = v.Effort,
                    ExtractionTimeoutSeconds = timeoutSeconds
                }),
                // As production: a scan's second pass asks the live orientation detector.
                new ClaudeScanOrientationDetector(httpClientFactory, NullLogger<ClaudeScanOrientationDetector>.Instance)),
            StringComparer.Ordinal);

        var results = new List<ComparisonRun>();
        foreach (var doc in documents)
        {
            foreach (var variant in variants)
            {
                for (var run = 1; run <= runs; run++)
                {
                    var outcome = await RunOnceAsync(
                        services[variant.Label], logger, bytesByAlias[doc.Alias], doc.Alias, variant, run,
                        documentType, fiscalYear, ratioService, scoring, fixedBcra, fixedMacro);
                    results.Add(outcome);

                    _output.WriteLine(ExtractionModelComparisonReport.RunSummaryLine(outcome));
                }
            }
        }

        // ── 4. Compare and write ────────────────────────────────────────────────
        IReadOnlyList<ComparisonVariant> allVariants = baselineVariant is null ? variants : [baselineVariant, .. variants];
        IReadOnlyList<ComparisonRun>     allRuns     = [.. baselineRuns, .. results];
        var comparisons = ExtractionModelComparisonReport.CompareAll(allVariants, allRuns);

        Directory.CreateDirectory(outputDir);
        var stamp    = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var mdPath   = Path.Combine(outputDir, $"model-comparison-{stamp}.md");
        var csvPath  = Path.Combine(outputDir, $"model-comparison-{stamp}.csv");
        var mapPath  = Path.Combine(outputDir, $"model-comparison-{stamp}-mapa.csv");
        var runsPath = Path.Combine(outputDir, $"model-comparison-{stamp}-runs.json");

        var header = new ComparisonHeader(
            DateTime.UtcNow, ClaudeService.ExtractionPromptVersion, documentType, fiscalYear, runs, upperBound,
            Path.GetFileName(csvPath), Path.GetFileName(mapPath));

        await File.WriteAllTextAsync(mdPath, ExtractionModelComparisonReport.BuildMarkdown(header, documents, allVariants, allRuns, comparisons), new UTF8Encoding(false));
        await File.WriteAllTextAsync(csvPath, ExtractionModelComparisonReport.BuildCsv(allRuns, comparisons), new UTF8Encoding(true));
        await File.WriteAllTextAsync(mapPath, ExtractionModelComparisonReport.BuildMap(documents), new UTF8Encoding(true));
        // This run only (never the loaded baseline), so the next commit can use it as ITS baseline.
        await File.WriteAllTextAsync(runsPath, ExtractionModelComparisonReport.BuildSnapshot(ClaudeService.ExtractionPromptVersion, documents, results), new UTF8Encoding(false));

        // Anonymised summary, safe to paste: aliases, counts and statuses — no file name, no figure.
        _output.WriteLine($"Prompt {ClaudeService.ExtractionPromptVersion}. Runs:");
        foreach (var r in allRuns.OrderBy(r => r.DocAlias, StringComparer.Ordinal).ThenBy(r => r.Variant.Label, StringComparer.Ordinal).ThenBy(r => r.RunIndex))
            _output.WriteLine(ExtractionModelComparisonReport.RunSummaryLine(r));
        _output.WriteLine("Key figures (Identical / WithinTolerance / Different / NullFlip):");
        foreach (var c in comparisons)
            _output.WriteLine(ExtractionModelComparisonReport.KeyFieldLine(c));

        _output.WriteLine($"{results.Count(r => r.Succeeded)}/{results.Count} call(s) succeeded; {comparisons.Count} comparison(s).");
        _output.WriteLine($"Report : {mdPath}");
        _output.WriteLine($"CSV    : {csvPath}");
        _output.WriteLine($"Map    : {mapPath}");
        _output.WriteLine($"Runs   : {runsPath}");

        // ── 5. Only that it ran and wrote the report ────────────────────────────
        Assert.Equal(documents.Count * variants.Count * runs, results.Count);
        foreach (var path in new[] { mdPath, csvPath, mapPath, runsPath })
            Assert.True(new FileInfo(path).Length > 0, $"{Path.GetFileName(path)} is empty.");
    }

    // ── One call ──────────────────────────────────────────────────────────────

    private static async Task<ComparisonRun> RunOnceAsync(
        ClaudeService service, TrimWarningRecorder logger, byte[] pdf, string alias, ComparisonVariant variant, int run,
        string documentType, int? fiscalYear, IRatioCalculationService ratioService,
        ScoringService scoring, BcraData fixedBcra, BcraMacroData fixedMacro)
    {
        logger.Reset();
        var stopwatch = Stopwatch.StartNew();
        ClaudeAnalysisResult result;
        try
        {
            result = await service.AnalyzeDocumentAsync(pdf, documentType, fiscalYear, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Type, status and OUR OWN diagnostic text only: an inner exception's message could quote
            // a fragment of the response, which is client data.
            var (type, detail) = ex switch
            {
                ClaudeApiException c => (
                    c.ApiStatusCode is { } s ? $"ClaudeApiException HTTP {s.ToString(CultureInfo.InvariantCulture)}" : "ClaudeApiException",
                    c.DiagnosticMessage),
                _ => (ex.GetType().Name, (string?)null)
            };

            return new ComparisonRun
            {
                DocAlias      = alias,
                Variant       = variant,
                RunIndex      = run,
                Succeeded     = false,
                FailureType   = type,
                FailureDetail = detail,
                FailureCode   = (ex as ClaudeApiException)?.FailureCode,
                LatencyMs     = stopwatch.Elapsed.TotalMilliseconds,
                PreambleChars = logger.PreambleChars,
                TrailerChars  = logger.TrailerChars
            };
        }

        var latency = stopwatch.Elapsed.TotalMilliseconds;

        ExtractionDeterminismReport.TryFlattenNumericLeaves(result.BalanceJson, out var leaves);
        var fields = leaves
            .Where(kv => !kv.Key.StartsWith("integrity", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var ratios = ComputeRatios(result.BalanceJson, ratioService);
        var (integrityPassed, failedChecks, answerTrimmed) = ExtractionModelComparisonReport.ReadIntegrity(result.BalanceJson);
        ScoringResult? score = null;
        if (ratios is not null)
            score = await ScoringTestHarness.Score(scoring, ratios, fixedBcra, fixedMacro);

        return new ComparisonRun
        {
            DocAlias     = alias,
            Variant      = variant,
            RunIndex     = run,
            Succeeded    = true,
            Confidence   = result.ConfidenceScore,
            Method       = result.ExtractionMethod,
            InputTokens  = result.InputTokens,
            OutputTokens = result.OutputTokens,
            CostUsd      = ClaudeModelPricing.EstimateCostUsd(result.ModelUsed, result.InputTokens, result.OutputTokens),
            LatencyMs    = latency,
            Fields       = fields,
            Ratios       = ratios,
            ScoreRatios  = score?.ScoreRatios,
            Score        = score?.Score,
            Level        = score?.Level,
            Escalated           = result.ExtractionPasses.Any(p => p.Escalated),
            MaxPassOutputTokens = result.ExtractionPasses.Count == 0 ? null : result.ExtractionPasses.Max(p => p.OutputTokens),
            AnswerTrimmed       = answerTrimmed,
            PreambleChars       = logger.PreambleChars,
            TrailerChars        = logger.TrailerChars,
            IntegrityPassed     = integrityPassed,
            FailedChecks        = failedChecks,
            // Codes only — never a title or a description, which quote the client's figures.
            AlertCodes          = ExtractionModelComparisonReport.ReadAlertCodes(result.AlertsJson)
        };
    }

    /// <summary>The main branch of AnalysisScoringStage.ComputeRatios: BalanceJson → BalanceAccounts → Calculate.</summary>
    private static RatiosData? ComputeRatios(string? balanceJson, IRatioCalculationService ratioService)
    {
        if (string.IsNullOrWhiteSpace(balanceJson))
            return null;

        try
        {
            var accounts = JsonSerializer.Deserialize<BalanceAccounts>(balanceJson, ExtractionDeterminismReport.JsonOpts);
            return accounts is null ? null : ratioService.Calculate(accounts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Environment helpers ───────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = Environment.GetEnvironmentVariable(ApiKeyFallbackVar)
            })
            // Last wins: Anthropic__ApiKey (and Anthropic__ExtractionConfidenceFallbackThreshold, if
            // set) override the defaults, exactly as on the App Service.
            .AddEnvironmentVariables()
            .Build();

    /// <summary>Refuses a folder inside the repository the test assembly was built from.</summary>
    private static void GuardOutsideRepository(string path, string variable)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            dir = dir.Parent;

        if (dir is null)
            return;

        var root       = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate  = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (candidate.StartsWith(root, comparison))
            throw new InvalidOperationException($"{variable} is inside the repository. Clients' balances and figures must stay outside it.");
    }

    private static int CountPages(byte[] pdf)
    {
        try
        {
#pragma warning disable CA1416 // PDFtoImage supports Windows, Linux and macOS
            return PDFtoImage.Conversion.GetPageCount(pdf);
#pragma warning restore CA1416
        }
        catch (Exception)
        {
            // Unreadable here (ClaudeService may still read it): estimate generously for the bound.
            return (int)Math.Max(1, pdf.LongLength / 50_000);
        }
    }

    private static int ReadPositiveInt(string variable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
                ? parsed
                : fallback;
    }

    private static int? ReadOptionalInt(string variable)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static string Sha256Hex(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    // ── Trim Warning capture ──────────────────────────────────────────────────

    /// <summary>
    /// Keeps only the LENGTHS ClaudeService logs when it drops text around the JSON object (the structured
    /// PreambleLength / TrailerLength values of that Warning) — never a message, never the text. Everything
    /// else is discarded, as NullLogger did. The largest value of the call wins (a call can have two passes).
    /// </summary>
    private sealed class TrimWarningRecorder : ILogger<ClaudeService>
    {
        public int? PreambleChars { get; private set; }

        public int? TrailerChars { get; private set; }

        public void Reset()
        {
            PreambleChars = null;
            TrailerChars  = null;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                return;

            foreach (var (key, value) in values)
            {
                if (value is not int length)
                    continue;

                if (key == "PreambleLength")
                    PreambleChars = Math.Max(PreambleChars ?? 0, length);
                else if (key == "TrailerLength")
                    TrailerChars = Math.Max(TrailerChars ?? 0, length);
            }
        }
    }

    // ── Live HTTP ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One real client with NO client-wide timeout — the same registration Program.cs gives
    /// <see cref="ClaudeService.ExtractionHttpClientName"/>: each attempt carries the model spec's own
    /// timeout. Deliberately does not intercept: this harness must reach the live API.
    /// </summary>
    private sealed class LiveHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }
}
