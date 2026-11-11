// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-26

using Api.Web.FinanceIA.Clases.SchemaVerification;
using Api.Web.FinanceIA.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers which databases <see cref="SchemaVerificationCommand"/> asks for, and the exit code it
/// returns, through its injected catalog read — so no SQL Server is needed.
///
/// <para>
/// The deploy workflow sets FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT=true: the shared Default database
/// is shared by every portal and is not changed by this repo's releases. These tests pin that the
/// skip is explicit (a Default connection string still present in configuration is NOT read),
/// that the tenant business database is still compared with the same 0 / 1 / 2 exit codes, and
/// that a missing or unusable Financia connection still fails closed.
/// </para>
/// </summary>
public sealed class SchemaVerificationCommandTests
{
    private const string ModelOnly = "Server=(model-only);Database=(model-only);";
    private const string FinanciaConnection = "Server=tcp:sql.test;Database=financia-cliente-a;";
    private const string DefaultConnection = "Server=tcp:sql.test;Database=default-compartida;";
    private const string ConfiguredDefaultConnection = "Server=tcp:sql.test;Database=default-appsettings;";

    [Fact]
    public async Task RunAsync_WhenDefaultIsSkipped_ReadsOnlyTheFinanciaDatabase()
    {
        var configuration = Configuration(
            ("FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT", "true"),
            ("FINANCIA_SCHEMA_VERIFY_CONNECTIONS", FinanciaConnection),
            ("FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION", DefaultConnection),
            ("ConnectionStrings:Default", ConfiguredDefaultConnection));
        var reader = new RecordingReader(FullFinanciaSnapshot());

        var exitCode = await RunAsync(configuration, reader);

        Assert.Equal(0, exitCode);
        Assert.Equal([FinanciaConnection], reader.Requested);
    }

    /// <summary>Skipping Default must not soften the Financia check: a missing column still blocks.</summary>
    [Fact]
    public async Task RunAsync_WhenDefaultIsSkipped_AMissingFinanciaColumnStillFails()
    {
        var configuration = Configuration(
            ("FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT", "true"),
            ("FINANCIA_SCHEMA_VERIFY_CONNECTIONS", FinanciaConnection));
        var reader = new RecordingReader(FinanciaSnapshotMissingOneColumn());

        var exitCode = await RunAsync(configuration, reader);

        Assert.Equal(1, exitCode);
        Assert.Equal([FinanciaConnection], reader.Requested);
    }

    /// <summary>
    /// With Default skipped, a Default connection alone must not be enough to pass: no Financia
    /// connection means the gate could not run.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenDefaultIsSkippedAndNoFinanciaConnection_CouldNotRun()
    {
        var configuration = Configuration(
            ("FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT", "true"),
            ("FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION", DefaultConnection),
            ("ConnectionStrings:Default", ConfiguredDefaultConnection));
        var reader = new RecordingReader(FullFinanciaSnapshot());

        var exitCode = await RunAsync(configuration, reader);

        Assert.Equal(2, exitCode);
        Assert.Empty(reader.Requested);
    }

    [Fact]
    public async Task RunAsync_WhenDefaultIsSkippedAndFinanciaIsUnusable_CouldNotRun()
    {
        var configuration = Configuration(
            ("FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT", "true"),
            ("FINANCIA_SCHEMA_VERIFY_CONNECTIONS", FinanciaConnection));
        var reader = new RecordingReader(failure: new InvalidOperationException("unusable"));

        var exitCode = await RunAsync(configuration, reader);

        Assert.Equal(2, exitCode);
        Assert.Equal([FinanciaConnection], reader.Requested);
    }

    /// <summary>A typo in the flag must not silently change which databases are checked.</summary>
    [Fact]
    public async Task RunAsync_WhenSkipFlagIsNotABoolean_CouldNotRun()
    {
        var configuration = Configuration(
            ("FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT", "yes"),
            ("FINANCIA_SCHEMA_VERIFY_CONNECTIONS", FinanciaConnection));
        var reader = new RecordingReader(FullFinanciaSnapshot());

        var exitCode = await RunAsync(configuration, reader);

        Assert.Equal(2, exitCode);
        Assert.Empty(reader.Requested);
    }

    /// <summary>Without the flag the command still reads Default, so it can be checked by hand.</summary>
    [Fact]
    public async Task RunAsync_WhenSkipFlagIsAbsent_StillReadsTheDefaultDatabase()
    {
        var configuration = Configuration(
            ("FINANCIA_SCHEMA_VERIFY_CONNECTIONS", FinanciaConnection),
            ("FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION", DefaultConnection));
        var reader = new RecordingReader(FullFinanciaSnapshot());

        await RunAsync(configuration, reader);

        Assert.Equal([FinanciaConnection, DefaultConnection], reader.Requested);
    }

    private static Task<int> RunAsync(IConfiguration configuration, RecordingReader reader)
    {
        return SchemaVerificationCommand.RunAsync(
            configuration,
            NullLogger.Instance,
            reader.ReadAsync,
            CancellationToken.None);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
    }

    /// <summary>Every table and column the real FinanciaDbContext model maps.</summary>
    private static SchemaSnapshot FullFinanciaSnapshot()
    {
        return new SchemaSnapshot(FinanciaRows());
    }

    private static SchemaSnapshot FinanciaSnapshotMissingOneColumn()
    {
        var rows = FinanciaRows().ToList();
        rows.RemoveAt(rows.Count - 1);

        return new SchemaSnapshot(rows);
    }

    private static IEnumerable<(string Schema, string Table, string Column)> FinanciaRows()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseSqlServer(ModelOnly)
            .Options;

        using var context = new FinanciaDbContext(options);
        var expectation = SchemaExpectation.FromModel(context.Model);

        return expectation.Tables
            .SelectMany(table => table.Columns.Select(column => (table.Schema, table.Name, column)))
            .ToArray();
    }

    /// <summary>Hand-rolled stand-in for the SQL catalog read: records every connection asked for.</summary>
    private sealed class RecordingReader
    {
        private readonly SchemaSnapshot? _snapshot;
        private readonly Exception? _failure;

        public RecordingReader(SchemaSnapshot? snapshot = null, Exception? failure = null)
        {
            _snapshot = snapshot;
            _failure = failure;
        }

        public List<string> Requested { get; } = [];

        public Task<SchemaSnapshot> ReadAsync(string connectionString, CancellationToken cancellationToken)
        {
            Requested.Add(connectionString);

            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(_snapshot ?? new SchemaSnapshot([]));
        }
    }
}
