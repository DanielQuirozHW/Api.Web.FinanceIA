namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// What a database actually contains: the tables and views present, and the columns on
/// each. Built from sys.objects / sys.columns, never from EF's model.
///
/// Lookups are case-insensitive because SQL Server object names are compared under the
/// database collation, which is case-insensitive on every environment this project targets.
/// </summary>
public sealed class SchemaSnapshot
{
    private readonly Dictionary<string, HashSet<string>> _columnsByTable;
    private readonly Dictionary<string, string>          _typeByColumn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a snapshot from rows read out of the database.
    /// </summary>
    /// <param name="rows">One entry per column: its schema, its table and its own name.</param>
    public SchemaSnapshot(IEnumerable<(string Schema, string Table, string Column)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _columnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var key = Key(row.Schema, row.Table);

            if (!_columnsByTable.TryGetValue(key, out var columns))
            {
                columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _columnsByTable[key] = columns;
            }

            columns.Add(row.Column);
        }
    }

    /// <summary>
    /// Builds a snapshot that also knows each column's <c>sys.types</c> name, so the gate can check
    /// <see cref="SchemaExpectation.ColumnTypes"/>. A snapshot built by the constructor has no types and
    /// skips that check.
    /// </summary>
    /// <param name="rows">One entry per column: its schema, its table, its own name and its type name.</param>
    public static SchemaSnapshot WithTypes(IReadOnlyCollection<(string Schema, string Table, string Column, string Type)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var snapshot = new SchemaSnapshot(rows.Select(row => (row.Schema, row.Table, row.Column)));
        foreach (var row in rows)
        {
            snapshot._typeByColumn[$"{Key(row.Schema, row.Table)}.{row.Column}"] = row.Type;
        }

        return snapshot;
    }

    /// <summary>
    /// Number of distinct tables and views the snapshot holds.
    /// </summary>
    public int TableCount => _columnsByTable.Count;

    /// <summary>
    /// True when the database has a table or view with this schema and name.
    /// </summary>
    public bool HasTable(string schema, string table)
    {
        return _columnsByTable.ContainsKey(Key(schema, table));
    }

    /// <summary>
    /// True when the named table exists AND carries this column.
    /// Returns false for a column on a table that does not exist at all.
    /// </summary>
    public bool HasColumn(string schema, string table, string column)
    {
        return _columnsByTable.TryGetValue(Key(schema, table), out var columns)
            && columns.Contains(column);
    }

    /// <summary>
    /// The column's <c>sys.types</c> name, or null when the column is absent or the snapshot was built
    /// without types.
    /// </summary>
    public string? ColumnType(string schema, string table, string column)
    {
        return _typeByColumn.TryGetValue($"{Key(schema, table)}.{column}", out var type) ? type : null;
    }

    private static string Key(string schema, string table)
    {
        return $"{schema}.{table}";
    }
}
