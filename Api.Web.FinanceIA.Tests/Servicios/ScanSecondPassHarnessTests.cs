// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Opt-in LIVE harness, never a CI test: the extraction's second pass on SCANNED balances (no text
/// layer, a landscape table printed sideways on a portrait page), run twice per case — the BASELINE
/// second pass as it was before (no image orientation, no halves) and the NEW one (the image detector
/// for pages without glyphs, wide pages also as halves) — so the change is judged on real documents and
/// its latency is measured against the ~230 s the App Service front end allows a synchronous analysis.
///
/// <para>
/// Gated by <see cref="EnvironmentFactAttribute"/> on FINANCIA_SCAN_CASES_DIR, a LOCAL folder holding
/// case-a.pdf, case-b.pdf and/or case-c.pdf: unset, it reports as skipped and calls nothing. It spends
/// real money, so it prints the plan and an upper-bound cost and stops unless
/// FINANCIA_SCAN_CASES_CONFIRM=yes. Each file goes through <see cref="PdfAttachmentExtractor"/> first,
/// exactly as production does (a CPCE wrapper carries the balance as an embedded PDF).
/// </para>
///
/// <para>
/// ⚠ The PDFs are clients' balances: the input folder and FINANCIA_SCAN_CASES_OUT must be OUTSIDE the
/// repository (enforced). The report holds counts, timings, costs, check NAMES and rotations — never a
/// figure, a file name or a response body. The assertion is weak on purpose: that it ran and wrote.
/// </para>
/// </summary>
public sealed class ScanSecondPassHarnessTests
{
    private const string DirVariable        = "FINANCIA_SCAN_CASES_DIR";
    private const string OutVariable        = "FINANCIA_SCAN_CASES_OUT";
    private const string ConfirmVariable    = "FINANCIA_SCAN_CASES_CONFIRM";
    private const string DocumentTypeVar    = "FINANCIA_SCAN_CASES_DOCTYPE";
    private const string FiscalYearVariable = "FINANCIA_SCAN_CASES_FISCALYEAR";
    private const string ApiKeyFallbackVar  = "ANTHROPIC_API_KEY";
    private const string SolutionFileName   = "Api.Web.ServiciosFinancieros.slnx";
    private const string DefaultDocumentType = "Balance";

    private static readonly string[] CaseFiles = ["case-a.pdf", "case-b.pdf", "case-c.pdf"];

    private readonly ITestOutputHelper _output;

    public ScanSecondPassHarnessTests(ITestOutputHelper output) => _output = output;

    [EnvironmentFact(DirVariable)]
    public async Task SecondPass_OnScannedBalances_BaselineVersusNew_WritesTheReport()
    {
        // ── 1. Validate everything BEFORE any money is spent ────────────────────
        var inputDir  = Path.GetFullPath(Environment.GetEnvironmentVariable(DirVariable)!);
        var outputDir = Environment.GetEnvironmentVariable(OutVariable);
        if (!Directory.Exists(inputDir))
            throw new InvalidOperationException($"{DirVariable} does not point to an existing folder.");
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new InvalidOperationException($"Set {OutVariable} to a folder OUTSIDE the repository.");

        outputDir = Path.GetFullPath(outputDir);
        GuardOutsideRepository(inputDir, DirVariable);
        GuardOutsideRepository(outputDir, OutVariable);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = Environment.GetEnvironmentVariable(ApiKeyFallbackVar) })
            .AddEnvironmentVariables()
            .Build();
        if (string.IsNullOrWhiteSpace(configuration["Anthropic:ApiKey"]))
            throw new InvalidOperationException($"Set Anthropic__ApiKey or {ApiKeyFallbackVar}.");

        var documentType = Environment.GetEnvironmentVariable(DocumentTypeVar) is { Length: > 0 } dt ? dt : DefaultDocumentType;
        var fiscalYear   = int.TryParse(Environment.GetEnvironmentVariable(FiscalYearVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fy)
            ? fy : (int?)null;

        var cases = new List<(string Alias, byte[] Bytes, int Pages)>();
        foreach (var file in CaseFiles)
        {
            var path = Path.Combine(inputDir, file);
            if (!File.Exists(path))
                continue;

            // Exactly as production: the CPCE wrapper is opened and its embedded balance read.
            var unwrapped = PdfAttachmentExtractor.UnwrapEmbeddedBalance(
                await File.ReadAllBytesAsync(path), NullLogger.Instance, Guid.Empty, "scan-harness");
            cases.Add((Path.GetFileNameWithoutExtension(file), unwrapped, CountPages(unwrapped)));
        }

        if (cases.Count == 0)
            throw new InvalidOperationException($"{DirVariable} holds none of {string.Join(", ", CaseFiles)}.");

        // ── 2. The plan, the upper bound, the confirmation ──────────────────────
        // Per variant: two Sonnet 4.6 passes at ≤1,600 tokens a page (+3 per split page, bounded by 2×),
        // the system prompt, full max_tokens out; plus the detector (≤300 tokens a thumbnail).
        var upperBound = cases.Sum(c =>
            2 * (2 * ((c.Pages * 1_600m * 3) + 8_000m) * 3m / 1_000_000m + 2 * 8_000m * 15m / 1_000_000m)
            + (c.Pages * 300m + 1_024m * 5m) / 1_000_000m);

        var plan = new StringBuilder()
            .AppendLine("Scan second-pass plan:")
            .AppendLine($"  {cases.Count} case(s): {string.Join(", ", cases.Select(c => $"{c.Alias} ({c.Pages} p, {c.Bytes.Length / 1024} KB)"))}")
            .AppendLine("  × 2 variants (baseline, new) = up to 4 Sonnet passes + 1 detector call per case")
            .AppendLine($"  Upper-bound cost: USD {upperBound.ToString("0.00", CultureInfo.InvariantCulture)} (retries not included)")
            .ToString();
        _output.WriteLine(plan);

        if (!string.Equals(Environment.GetEnvironmentVariable(ConfirmVariable), "yes", StringComparison.Ordinal))
            throw new InvalidOperationException($"Nothing was sent. Review the plan above and set {ConfirmVariable}=yes to spend it.{Environment.NewLine}{plan}");

        // ── 3. Run every case through both variants ─────────────────────────────
        var runs = new List<CaseRun>();
        foreach (var (alias, bytes, _) in cases)
        {
            foreach (var variant in new[] { "baseline", "new" })
            {
                var run = await RunOnceAsync(alias, variant, bytes, documentType, fiscalYear, configuration);
                runs.Add(run);
                _output.WriteLine(run.Error is null
                    ? $"{alias} {variant}: kept={run.KeptPass} ({run.KeptReason}) rotated={run.RotatedPages} split={run.SplitPages} " +
                      $"source={run.OrientationSource} total={run.TotalMs / 1000d:0.0}s"
                    : $"{alias} {variant}: FAILED {run.Error}");
            }
        }

        // ── 4. Write ────────────────────────────────────────────────────────────
        Directory.CreateDirectory(outputDir);
        var stamp  = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var mdPath = Path.Combine(outputDir, $"scan-second-pass-{stamp}.md");
        await File.WriteAllTextAsync(mdPath, BuildMarkdown(runs), new UTF8Encoding(false));
        _output.WriteLine($"Report : {mdPath}");

        Assert.Equal(cases.Count * 2, runs.Count);
        Assert.True(new FileInfo(mdPath).Length > 0);
    }

    // ── One case, one variant ─────────────────────────────────────────────────

    private static async Task<CaseRun> RunOnceAsync(
        string alias, string variant, byte[] bytes, string documentType, int? fiscalYear, IConfiguration configuration)
    {
        using var factory = new RecordingHttpClientFactory();
        var isNew = variant == "new";

        IScanOrientationDetector detector = isNew
            ? new ClaudeScanOrientationDetector(factory, NullLogger<ClaudeScanOrientationDetector>.Instance)
            : NullScanOrientationDetector.Instance;

        var service = new ClaudeService(
            factory, configuration, NullLogger<ClaudeService>.Instance, new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions()), detector)
        {
            SplitWidePages = isNew
        };

        var stopwatch = Stopwatch.StartNew();
        ClaudeAnalysisResult? result = null;
        string? error = null;
        try
        {
            result = await service.AnalyzeDocumentAsync(bytes, documentType, fiscalYear, CancellationToken.None);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }

        stopwatch.Stop();

        var extraction = factory.Calls.Where(c => c.Client == ClaudeService.ExtractionHttpClientName).ToList();
        var detection  = factory.Calls.Where(c => c.Client == ClaudeScanOrientationDetector.HttpClientName).ToList();
        var info       = result is null ? null : JsonNode.Parse(result.BalanceJson)?["integrity"]?["extraction"];
        var integrity  = result is null ? null : JsonNode.Parse(result.BalanceJson)?["integrity"];

        return new CaseRun
        {
            Alias             = alias,
            Variant           = variant,
            Error             = error,
            TotalMs           = stopwatch.ElapsedMilliseconds,
            Attempts          = extraction.Select(c => (c.Ms, c.RequestBytes, c.Images, c.Status)).ToList(),
            DetectorMs        = detection.Sum(c => c.Ms),
            DetectorAnswer    = detection.Select(c => c.ToolInput).FirstOrDefault(),
            GlyphPages        = PdfPageOrientation.DetectPageOrientations(bytes, 100)
                                    .Select(p => p.Decided ? p.Rotation.ToString(CultureInfo.InvariantCulture) : "?").ToList(),
            KeptPass          = info?["keptPass"]?.GetValue<string>(),
            KeptReason        = info?["keptReason"]?.GetValue<string>(),
            SecondPassRan     = info?["secondPassRan"]?.GetValue<bool>() ?? false,
            Method            = result?.ExtractionMethod,
            RotatedPages      = info?["rotatedPages"]?.GetValue<int>() ?? 0,
            SplitPages        = info?["splitPages"]?.GetValue<int>() ?? 0,
            OrientationSource = info?["orientationSource"]?.GetValue<string>(),
            FailedChecks      = FailedChecks(integrity),
            ExtractionCostUsd = result is null ? null : ClaudeModelPricing.EstimateCostUsd(result.ModelUsed, result.InputTokens, result.OutputTokens),
            DetectorCostUsd   = result?.OrientationDetection is { } d ? ClaudeModelPricing.EstimateCostUsd(d.Model, d.InputTokens, d.OutputTokens) : null,
            DetectorTokens    = result?.OrientationDetection is { } u ? $"{u.InputTokens}/{u.OutputTokens}" : null,
        };
    }

    // NAMES of the kept pass's failed checks (integrity.checks[].name where passed is false) — never
    // their expected or actual figures.
    private static List<string> FailedChecks(JsonNode? integrity) =>
        integrity?["checks"] is JsonArray checks
            ? checks.Where(c => c?["passed"]?.GetValue<bool>() == false)
                    .Select(c => c?["name"]?.GetValue<string>() ?? "?")
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            : [];

    private static string BuildMarkdown(IReadOnlyList<CaseRun> runs)
    {
        var md = new StringBuilder()
            .AppendLine("# Scan second pass — baseline vs new")
            .AppendLine()
            .AppendLine("Counts, timings, costs and check names only. No figures, no file names. Do not commit.")
            .AppendLine()
            .AppendLine("| case | variant | 2nd pass ran | kept | reason | method | rotated | split | orientation | failed checks (kept) | 1st pass s | detector s | 2nd pass s | total s | 2nd req MB | 2nd images | Sonnet $ | detector $ |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var r in runs)
        {
            var first  = r.Attempts.FirstOrDefault();
            var second = r.Attempts.Skip(1).LastOrDefault();
            md.Append(CultureInfo.InvariantCulture,
                $"| {r.Alias} | {r.Variant} | {r.SecondPassRan} | {r.KeptPass ?? r.Error} | {r.KeptReason} | {r.Method} | {r.RotatedPages} | {r.SplitPages} | {r.OrientationSource} | ")
              .Append(string.Join(", ", r.FailedChecks)).Append(" | ")
              .Append(CultureInfo.InvariantCulture, $"{first.Ms / 1000d:0.0} | {r.DetectorMs / 1000d:0.0} | {(r.Attempts.Count > 1 ? second.Ms / 1000d : 0):0.0} | {r.TotalMs / 1000d:0.0} | ")
              .Append(CultureInfo.InvariantCulture, $"{(r.Attempts.Count > 1 ? second.RequestBytes / 1048576d : 0):0.0} | {(r.Attempts.Count > 1 ? second.Images : 0)} | ")
              .Append(CultureInfo.InvariantCulture, $"{r.ExtractionCostUsd:0.0000} | {r.DetectorCostUsd:0.0000} |")
              .AppendLine();
        }

        md.AppendLine().AppendLine("## Orientation per page").AppendLine()
          .AppendLine("Glyph decision per page (`?` = no text layer), then the detector's answer (image number → clockwise degrees).").AppendLine();
        foreach (var r in runs.Where(r => r.Variant == "new"))
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {r.Alias}: glyphs [{string.Join(", ", r.GlyphPages)}]; detector {r.DetectorAnswer ?? "not called"} ({r.DetectorTokens ?? "-"} tokens)");
        }

        md.AppendLine().AppendLine("## Extraction attempts").AppendLine();
        foreach (var r in runs)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"- {r.Alias} {r.Variant}: {string.Join("; ", r.Attempts.Select(a => $"{a.Ms / 1000d:0.0}s {a.RequestBytes / 1048576d:0.0}MB {a.Images} img HTTP {a.Status}"))}");
        }

        return md.ToString();
    }

    private static void GuardOutsideRepository(string path, string variable)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            dir = dir.Parent;

        if (dir is null)
            return;

        var root       = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate  = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (candidate.StartsWith(root, comparison))
            throw new InvalidOperationException($"{variable} is inside the repository. Clients' balances must stay outside it.");
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
            return (int)Math.Max(1, pdf.LongLength / 50_000);
        }
    }

    // ── Records ───────────────────────────────────────────────────────────────

    private sealed record CaseRun
    {
        public required string Alias { get; init; }
        public required string Variant { get; init; }
        public string? Error { get; init; }
        public long TotalMs { get; init; }
        public List<(long Ms, long RequestBytes, int Images, int? Status)> Attempts { get; init; } = [];
        public long DetectorMs { get; init; }
        public string? DetectorAnswer { get; init; }
        public string? DetectorTokens { get; init; }
        public List<string> GlyphPages { get; init; } = [];
        public string? KeptPass { get; init; }
        public string? KeptReason { get; init; }
        public bool SecondPassRan { get; init; }
        public string? Method { get; init; }
        public int RotatedPages { get; init; }
        public int SplitPages { get; init; }
        public string? OrientationSource { get; init; }
        public List<string> FailedChecks { get; init; } = [];
        public decimal? ExtractionCostUsd { get; init; }
        public decimal? DetectorCostUsd { get; init; }
    }

    private sealed record Call(string Client, long Ms, long RequestBytes, int Images, int? Status, string? ToolInput);

    // ── Live HTTP, timed ──────────────────────────────────────────────────────

    /// <summary>
    /// Real clients — no client-wide timeout for the extraction (its attempts carry their own), 120 s for
    /// "ClaudeClient" as Program.cs registers it — each behind a handler that times every request and
    /// counts its images. It keeps the detector's tool input (image numbers and rotations, nothing else)
    /// and never an extraction response.
    /// </summary>
    private sealed class RecordingHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClientHandler _inner = new();
        private readonly List<HttpClient>  _clients = [];

        public List<Call> Calls { get; } = [];

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Recorder(name, this) { InnerHandler = _inner }, disposeHandler: false)
            {
                Timeout = name == ClaudeService.ExtractionHttpClientName ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(120)
            };
            lock (_clients) _clients.Add(client);
            return client;
        }

        public void Dispose()
        {
            lock (_clients) _clients.ForEach(c => c.Dispose());
            _inner.Dispose();
        }

        private sealed class Recorder(string name, RecordingHttpClientFactory owner) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body   = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                var images = CountImages(body);
                var sw     = Stopwatch.StartNew();
                try
                {
                    var response = await base.SendAsync(request, cancellationToken);
                    sw.Stop();

                    string? toolInput = null;
                    if (name == ClaudeScanOrientationDetector.HttpClientName && response.IsSuccessStatusCode)
                    {
                        var text = await response.Content.ReadAsStringAsync(cancellationToken);
                        toolInput = ToolInput(text);
                        response.Content = new StringContent(text, Encoding.UTF8, "application/json");
                    }

                    lock (owner.Calls) owner.Calls.Add(new Call(name, sw.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(body), images, (int)response.StatusCode, toolInput));
                    return response;
                }
                catch
                {
                    lock (owner.Calls) owner.Calls.Add(new Call(name, sw.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(body), images, null, null));
                    throw;
                }
            }

            private static int CountImages(string body)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    return doc.RootElement.GetProperty("messages")[0].GetProperty("content")
                        .EnumerateArray().Count(b => b.GetProperty("type").GetString() == "image");
                }
                catch (Exception)
                {
                    return 0;
                }
            }

            private static string? ToolInput(string body)
            {
                try
                {
                    var pages = JsonNode.Parse(body)?["content"]?.AsArray()
                        .FirstOrDefault(b => b?["type"]?.ToString() == "tool_use")?["input"]?["pages"]?.AsArray();
                    return pages is null
                        ? null
                        : string.Join(", ", pages.Select(p => $"{p?["index"]}→{p?["rotation"]}"));
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
