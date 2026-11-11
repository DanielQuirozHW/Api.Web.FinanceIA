// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// No audit severity outside CK_AuditLogs_Severity may come back. SQL Server rejects such a row and
/// AuditService swallows the rejection, so the row is simply lost — which is how both Analysis.Failed rows
/// (severity "Error") went unstored in production. This scans every <c>LogAsync</c> call on an audit
/// service in the API project, and checks that AuditService keeps a row whose severity slipped through.
/// </summary>
public sealed class AuditSeverityGuardTests
{
    // `<receiver>.LogAsync(` where the receiver names an audit service: _auditService, auditService, _audit …
    private static readonly Regex AuditLogCall =
        new(@"\b(?<receiver>\w*[aA]udit\w*)\s*\.\s*LogAsync\s*\(", RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new("\"(?<value>[^\"\\\\]*)\"", RegexOptions.Compiled);

    // What may remain of a severity expression once its literals and AuditSeverities.X references are
    // removed: a ternary (its condition is any identifier, member access or negation) and whitespace.
    private static readonly Regex AllowedResidue = new(@"^[\w\s\.\?:!()=&|]*$", RegexOptions.Compiled);

    [Fact]
    public void AuditSeverities_mirrors_CK_AuditLogs_Severity_exactly()
    {
        Assert.Equal(["Critical", "Info", "Warning"], AuditSeverities.All.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Error", AuditSeverities.All);
        Assert.False(AuditSeverities.All.Contains("warning"));   // case-sensitive, as stored
    }

    [Fact]
    public void Every_audit_LogAsync_call_passes_a_severity_the_constraint_accepts()
    {
        var calls     = FindAuditLogCalls();
        var offenders = new List<string>();

        foreach (var call in calls)
        {
            var arguments = SplitTopLevel(call.Arguments);

            // Positional arguments would hide the severity from this scan: severity is the 4th parameter.
            if (arguments.Count >= 4 && !IsNamed(arguments[3]))
                offenders.Add($"{call.Where}: severity passed positionally");

            var severity = arguments.FirstOrDefault(a => Regex.IsMatch(a, @"^\s*severity\s*:"));
            if (severity is null)
                continue;   // the default, Info

            var expression = severity[(severity.IndexOf(':') + 1)..];

            foreach (Match literal in StringLiteral.Matches(expression))
            {
                if (!AuditSeverities.All.Contains(literal.Groups["value"].Value))
                    offenders.Add($"{call.Where}: severity \"{literal.Groups["value"].Value}\"");
            }

            var residue = Regex.Replace(StringLiteral.Replace(expression, " "), @"\bAuditSeverities\.\w+", " ");
            var hasValue = StringLiteral.IsMatch(expression) || expression.Contains("AuditSeverities.", StringComparison.Ordinal);
            if (!hasValue || !AllowedResidue.IsMatch(residue))
                offenders.Add($"{call.Where}: severity is not a literal or an AuditSeverities constant ({expression.Trim()})");
        }

        Assert.Empty(offenders);

        // Not vacuous: the API writes audit rows from dozens of places.
        Assert.True(calls.Count >= 40, $"Only {calls.Count} audit LogAsync calls found; the scan is broken.");
        // And it does see named severities, including the two that used to be "Error".
        Assert.Contains(calls, c => c.Where.StartsWith("AnalysisPipeline.cs", StringComparison.Ordinal) && c.Arguments.Contains("severity", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.Where.StartsWith("AnalysisJobWorker.cs", StringComparison.Ordinal) && c.Arguments.Contains("severity", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scan_catches_the_severity_that_lost_the_rows()
    {
        // The guard itself, on the exact call that went unstored.
        const string source = """
            _ = _auditService.LogAsync(
                tenantId:   tenantId,
                action:     "Analysis.Failed",
                severity:   "Error",
                ct:         CancellationToken.None);
            """;

        var match     = AuditLogCall.Match(source);
        var arguments = SplitTopLevel(ReadBalanced(source, match.Index + match.Length));
        var severity  = arguments.Single(a => a.TrimStart().StartsWith("severity", StringComparison.Ordinal));

        Assert.Contains(StringLiteral.Matches(severity).Select(m => m.Groups["value"].Value),
            v => !AuditSeverities.All.Contains(v));
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("error")]
    [InlineData("")]
    public async Task AuditService_stores_an_unknown_severity_as_Warning_instead_of_losing_the_row(string severity)
    {
        var services = new ServiceCollection();
        var database = $"audit-severity-{Guid.NewGuid()}";
        services.AddDbContext<FinanciaDbContext>(o => o.UseInMemoryDatabase(database));
        await using var provider = services.BuildServiceProvider();

        var sut = new AuditService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditService>.Instance);
        await sut.LogAsync("TenantA", null, "Analysis.Failed", severity: severity, ct: CancellationToken.None);
        await sut.LogAsync("TenantA", null, "Analysis.Viewed", ct: CancellationToken.None);

        using var scope = provider.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<FinanciaDbContext>().AuditLogs
            .AsNoTracking().OrderBy(x => x.Action).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Analysis.Failed", AuditSeverities.Warning), (rows[0].Action, rows[0].Severity));
        Assert.Equal(("Analysis.Viewed", AuditSeverities.Info), (rows[1].Action, rows[1].Severity));
    }

    // ── Scan helpers ─────────────────────────────────────────────────────────────

    private sealed record AuditCall(string Where, string Arguments);

    private static List<AuditCall> FindAuditLogCalls()
    {
        var separator = Path.DirectorySeparatorChar;
        var calls     = new List<AuditCall>();

        foreach (var path in Directory.EnumerateFiles(ProjectDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}bin{separator}") || path.Contains($"{separator}obj{separator}"))
                continue;

            var source = File.ReadAllText(path);
            foreach (Match match in AuditLogCall.Matches(source))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                calls.Add(new AuditCall($"{Path.GetFileName(path)}:{line}", ReadBalanced(source, match.Index + match.Length)));
            }
        }

        return calls;
    }

    // The text between an opening parenthesis (just before `start`) and its matching close.
    private static string ReadBalanced(string source, int start)
    {
        var depth = 1;
        var i     = start;
        var inString = false;
        for (; i < source.Length && depth > 0; i++)
        {
            var c = source[i];
            if (c == '"' && source[i - 1] != '\\')
                inString = !inString;
            else if (!inString && c == '(')
                depth++;
            else if (!inString && c == ')')
                depth--;
        }

        return source[start..(i - 1)];
    }

    // Arguments split on the commas at nesting depth 0 (not inside (), [], {} or a string).
    private static List<string> SplitTopLevel(string arguments)
    {
        var parts    = new List<string>();
        var depth    = 0;
        var inString = false;
        var from     = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '"' && (i == 0 || arguments[i - 1] != '\\'))
                inString = !inString;
            else if (inString)
                continue;
            else if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(arguments[from..i]);
                from = i + 1;
            }
        }

        if (!string.IsNullOrWhiteSpace(arguments[from..]))
            parts.Add(arguments[from..]);

        return parts;
    }

    private static bool IsNamed(string argument) => Regex.IsMatch(argument, @"^\s*\w+\s*:(?!:)");

    private static string ProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Api.Web.FinanceIA");
            if (File.Exists(Path.Combine(candidate, "Api.Web.FinanceIA.csproj")))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Api.Web.FinanceIA/Api.Web.FinanceIA.csproj was not found above {AppContext.BaseDirectory}.");
    }
}
