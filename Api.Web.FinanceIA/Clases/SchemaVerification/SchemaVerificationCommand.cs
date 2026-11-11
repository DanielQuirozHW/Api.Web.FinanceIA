using Api.Web.FinanceIA.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// The deploy gate. Run as <c>Api.Web.FinanceIA --verify-schema</c>, it checks that every
/// table and column this build's EF models map already exists in each target database, then
/// exits without ever starting the web host.
///
/// WHY THIS EXISTS
/// This project is Database-First: schema changes are numbered DDL scripts in
/// docs/database/ that a DBA runs by hand, and they must run BEFORE the matching deploy.
/// Publishing first breaks production with "Invalid column name" on every SELECT and INSERT
/// against the affected table. Until now the only thing standing in the way was the
/// `ddl_aplicada` checkbox on the deploy workflow — an operator's promise, which is not
/// evidence. This command turns that promise into a check against the database itself.
///
/// It deliberately does NOT execute any DDL. It opens a read-only connection, reads the sys
/// catalog views and compares. Applying the scripts stays the DBA's job, so no pipeline ever
/// holds ALTER rights on production.
///
/// INPUT (never passed on the command line — a connection string carries a password and the
/// command line is visible in process listings and CI logs):
///   FINANCIA_SCHEMA_VERIFY_CONNECTIONS         one tenant business database per line.
///                                              Falls back to ConnectionStrings:FinanciaDefault.
///   FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION  the shared auth/config database.
///                                              Falls back to ConnectionStrings:Default.
///   FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT        "true" leaves the shared database out entirely,
///                                              whatever the two Default settings above hold.
///                                              The deploy workflow sets it: that database is
///                                              shared by every portal and is not changed by
///                                              this repo's releases. Any other non-boolean
///                                              value is a configuration error (exit 2).
///
/// EXIT CODES: 0 every database satisfies the model · 1 something is missing (do not deploy)
/// · 2 the check could not run (bad configuration, or a database it could not reach).
/// </summary>
public static class SchemaVerificationCommand
{
    /// <summary>Argument that selects this mode instead of starting the API.</summary>
    public const string Switch = "--verify-schema";

    private const string TenantConnectionsKey = "FINANCIA_SCHEMA_VERIFY_CONNECTIONS";
    private const string DefaultConnectionKey = "FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION";
    private const string SkipDefaultKey = "FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT";

    private const int ExitSatisfied = 0;
    private const int ExitSchemaMissing = 1;
    private const int ExitCouldNotRun = 2;

    /// <summary>
    /// Placeholder used only so the SQL Server provider can be configured while a model is
    /// built. EF builds a model from configuration alone, so nothing ever connects with it.
    /// </summary>
    private const string ModelOnlyConnectionString = "Server=(model-only);Database=(model-only);";

    /// <summary>
    /// True when the process was started to verify the schema rather than to serve requests.
    /// </summary>
    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs the check against every configured database and returns the process exit code.
    /// </summary>
    /// <param name="configuration">Application configuration, already including environment variables.</param>
    /// <param name="logger">Where the report goes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<int> RunAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var reader = new SqlSchemaReader();

        return RunAsync(configuration, logger, reader.ReadAsync, cancellationToken);
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo): the same run with the catalog read injected, so a test can
    /// see which databases the gate asked for without a SQL Server.
    /// </summary>
    internal static async Task<int> RunAsync(
        IConfiguration configuration,
        ILogger logger,
        Func<string, CancellationToken, Task<SchemaSnapshot>> readSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(readSnapshot);

        var skipDefaultSetting = configuration[SkipDefaultKey];
        var isDefaultSkipped = false;

        // Parsed strictly: a typo must not silently change which databases the gate checks.
        if (!string.IsNullOrWhiteSpace(skipDefaultSetting) &&
            !bool.TryParse(skipDefaultSetting.Trim(), out isDefaultSkipped))
        {
            logger.LogError(
                "Schema verification could not run: {SkipKey} must be \"true\" or \"false\".",
                SkipDefaultKey);

            return ExitCouldNotRun;
        }

        var tenantConnections = ReadConnections(
            configuration[TenantConnectionsKey],
            configuration.GetConnectionString("FinanciaDefault"));

        // Skipped explicitly rather than by leaving the connection empty: ConnectionStrings:Default
        // can still arrive from appsettings.json, user-secrets or an environment variable.
        var defaultConnections = isDefaultSkipped
            ? []
            : ReadConnections(
                configuration[DefaultConnectionKey],
                configuration.GetConnectionString("Default"));

        if (isDefaultSkipped)
        {
            logger.LogInformation(
                "Shared Default database: NOT verified ({SkipKey}=true). It is shared by every portal and " +
                "outside this repo's releases; its scripts are run and checked by hand.",
                SkipDefaultKey);

            if (tenantConnections.Count == 0)
            {
                logger.LogError(
                    "Schema verification could not run: no tenant business connection string was configured. " +
                    "Set {TenantKey} (one tenant business database per line) or ConnectionStrings:FinanciaDefault.",
                    TenantConnectionsKey);

                return ExitCouldNotRun;
            }
        }

        if (tenantConnections.Count == 0 && defaultConnections.Count == 0)
        {
            logger.LogError(
                "Schema verification could not run: no connection string was configured. Set {TenantKey} " +
                "(one tenant business database per line) and {DefaultKey}, or provide ConnectionStrings:FinanciaDefault " +
                "and ConnectionStrings:Default.",
                TenantConnectionsKey,
                DefaultConnectionKey);

            return ExitCouldNotRun;
        }

        logger.LogInformation(
            "Verifying the deployed model against {TenantCount} tenant business database(s) and " +
            "{DefaultCount} shared database(s). Read-only: no DDL is executed.",
            tenantConnections.Count,
            defaultConnections.Count);

        var results = new List<SchemaVerificationResult>();
        var unreachable = 0;

        var tenantExpectation = SchemaExpectation.FromModel(BuildFinanciaModel());
        var defaultExpectation = SchemaExpectation.FromModel(BuildAppModel());

        foreach (var connection in tenantConnections)
        {
            var result = await VerifyOneAsync(
                readSnapshot, tenantExpectation, connection, "tenant business", logger, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                unreachable++;
            }
            else
            {
                results.Add(result);
            }
        }

        foreach (var connection in defaultConnections)
        {
            var result = await VerifyOneAsync(
                readSnapshot, defaultExpectation, connection, "shared auth/config", logger, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                unreachable++;
            }
            else
            {
                results.Add(result);
            }
        }

        // A database we could not reach is not a pass. Report it as "could not run" rather
        // than letting a network failure read as a green gate.
        if (unreachable > 0)
        {
            logger.LogError(
                "Schema verification is INCONCLUSIVE: {Unreachable} database(s) could not be read. " +
                "Treat this as a blocked deploy, not a pass.",
                unreachable);

            return ExitCouldNotRun;
        }

        if (results.All(result => result.IsSatisfied))
        {
            logger.LogInformation(
                "Schema verification PASSED on all {Count} database(s): every mapped table and column exists.",
                results.Count);

            return ExitSatisfied;
        }

        var failed = results.Where(result => !result.IsSatisfied).ToArray();

        logger.LogError(
            "Schema verification FAILED on {FailedCount} of {TotalCount} database(s). " +
            "Run the pending DDL from Api.Web.FinanceIA/docs/database/ on the databases listed above, " +
            "then re-run this check. DO NOT PUBLISH: EF Core emits every mapped column in each SELECT " +
            "and INSERT, so the API would fail with \"Invalid column name\" / \"Invalid object name\".",
            failed.Length,
            results.Count);

        return ExitSchemaMissing;
    }

    /// <summary>
    /// Verifies one database. Returns null when it could not be read at all.
    /// </summary>
    private static async Task<SchemaVerificationResult?> VerifyOneAsync(
        Func<string, CancellationToken, Task<SchemaSnapshot>> readSnapshot,
        SchemaExpectation expectation,
        string connectionString,
        string role,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var databaseName = DescribeDatabase(connectionString);

        try
        {
            var snapshot = await readSnapshot(connectionString, cancellationToken).ConfigureAwait(false);
            var result = SchemaComparer.Compare(expectation, snapshot, databaseName);

            if (result.IsSatisfied)
            {
                logger.LogInformation(
                    "OK  {Database} ({Role}): {TableCount} mapped table(s) all present.",
                    databaseName,
                    role,
                    expectation.Tables.Count);

                return result;
            }

            foreach (var table in result.MissingTables)
            {
                logger.LogError(
                    "MISSING TABLE  {Database} ({Role}): {Table} does not exist.",
                    databaseName,
                    role,
                    table.QualifiedName);
            }

            foreach (var column in result.MissingColumns)
            {
                logger.LogError(
                    "MISSING COLUMN {Database} ({Role}): {Column} does not exist.",
                    databaseName,
                    role,
                    column.QualifiedName);
            }

            foreach (var wrong in result.WrongTypes)
            {
                logger.LogError(
                    "WRONG TYPE     {Database} ({Role}): {Column} is {ActualType}, the code needs {ExpectedType}.",
                    databaseName,
                    role,
                    wrong.QualifiedName,
                    wrong.ActualType,
                    wrong.Expected.SqlType);
            }

            return result;
        }
        catch (SqlException ex)
        {
            // Never log the exception message here: SQL Server error text can echo back the
            // server and login name. The error number is enough to look the cause up.
            logger.LogError(
                "UNREACHABLE {Database} ({Role}): the database could not be read (SQL error {Number}). " +
                "Check the credentials, the firewall rule for this runner, and that the database exists.",
                databaseName,
                role,
                ex.Number);

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            // A malformed connection string throws before any connection is attempted. Caught so
            // the gate reports "could not verify" instead of crashing with a stack trace that
            // would very likely carry the connection string itself.
            logger.LogError(
                "UNREACHABLE {Database} ({Role}): the connection string is not usable ({ExceptionType}). " +
                "Check the entry in the schema-verification secret.",
                databaseName,
                role,
                ex.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// Builds the FinanciaDbContext model without opening a connection. EF builds a model
    /// from the configuration alone, so the placeholder connection string is never reached.
    /// </summary>
    private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildFinanciaModel()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer(ModelOnlyConnectionString)
            .Options;

        using var context = new FinanciaDbContext(options);
        return context.Model;
    }

    /// <summary>
    /// Builds the AppDbContext model without opening a connection, as above.
    /// </summary>
    private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildAppModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ModelOnlyConnectionString)
            .Options;

        using var context = new AppDbContext(options);
        return context.Model;
    }

    /// <summary>
    /// Splits a multi-line setting into connection strings, falling back to a single one.
    /// Blank lines and comment lines are ignored so a secret can be annotated.
    /// </summary>
    private static IReadOnlyCollection<string> ReadConnections(string? multiLine, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(multiLine))
        {
            // Explicit char[]: a collection expression here is ambiguous between the
            // Split(char[], ...) and Split(string, ...) overloads.
            var parsed = multiLine
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .ToArray();

            if (parsed.Length > 0)
            {
                return parsed;
            }
        }

        return string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
    }

    /// <summary>
    /// Extracts just the catalog name for reporting. The server, user and password are
    /// deliberately dropped: this string lands in CI logs.
    /// </summary>
    private static string DescribeDatabase(string connectionString)
    {
        try
        {
            var catalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            return string.IsNullOrWhiteSpace(catalog) ? "(unnamed database)" : catalog;
        }
        catch (ArgumentException)
        {
            return "(unparseable connection string)";
        }
    }
}
