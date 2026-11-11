// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The values <c>[dbo].[AuditLogs].[Severity]</c> accepts — a mirror of the
/// <c>CK_AuditLogs_Severity</c> CHECK constraint, which allows exactly these three and nothing else.
///
/// <para>
/// A row whose severity is outside this set is REJECTED by SQL Server, and because the audit write is
/// fire-and-forget (<see cref="AuditService"/> swallows its own failures) the row is lost without the
/// caller ever knowing: both <c>Analysis.Failed</c> rows were written with "Error" and never stored.
/// There is no "Error" level here on purpose — a failure the system handled is a Warning.
/// <c>AuditSeverityGuardTests</c> scans every <c>LogAsync</c> call site against <see cref="All"/>, and
/// <see cref="AuditService"/> writes an unknown value as <see cref="Warning"/> rather than lose the row.
/// </para>
/// </summary>
public static class AuditSeverities
{
    /// <summary>A normal event. The default.</summary>
    public const string Info = "Info";

    /// <summary>An unexpected but handled event (a failed run, an override).</summary>
    public const string Warning = "Warning";

    /// <summary>A system-level failure.</summary>
    public const string Critical = "Critical";

    /// <summary>Every value <c>CK_AuditLogs_Severity</c> allows. Case-sensitive, as stored.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Info, Warning, Critical };
}
