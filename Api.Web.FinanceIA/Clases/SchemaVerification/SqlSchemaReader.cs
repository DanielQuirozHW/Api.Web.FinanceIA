using Microsoft.Data.SqlClient;

namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// Reads a <see cref="SchemaSnapshot"/> out of a SQL Server database.
///
/// Strictly read-only: it issues one SELECT against the sys catalog views and never
/// creates, alters or drops anything. The account this runs as therefore needs no DDL
/// permission — VIEW DEFINITION on the database is enough.
/// </summary>
public sealed class SqlSchemaReader
{
    // sys.columns covers user tables and views alike; type 'U' is a user table and 'V' a
    // view. Views are included so a model mapped onto one is not reported as missing. The type
    // name (the SYSTEM type, so an alias type reads as its base) feeds SchemaExpectation.ColumnTypes.
    private const string CatalogQuery = """
        SELECT s.name AS SchemaName, o.name AS ObjectName, c.name AS ColumnName,
               TYPE_NAME(c.system_type_id) AS TypeName
        FROM sys.columns AS c
        INNER JOIN sys.objects AS o ON o.object_id = c.object_id
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U', 'V');
        """;

    /// <summary>
    /// Connects to the database and reads every table, view and column it holds.
    /// </summary>
    /// <param name="connectionString">Connection string for the database to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SchemaSnapshot> ReadAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }

        var rows = new List<(string Schema, string Table, string Column, string Type)>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(CatalogQuery, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                      reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return SchemaSnapshot.WithTypes(rows);
    }
}
