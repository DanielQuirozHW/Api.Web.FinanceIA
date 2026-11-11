namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// Compares what an EF model expects against what a database actually has.
/// Pure and side-effect free: it opens no connection and writes no log, so the
/// deploy gate's decision can be unit-tested without a database.
/// </summary>
public static class SchemaComparer
{
    /// <summary>
    /// Reports every table and column the model maps that the database is missing, and every
    /// type-checked column (<see cref="SchemaExpectation.ColumnTypes"/>) that has another type.
    /// </summary>
    /// <param name="expectation">What the model will ask the database for.</param>
    /// <param name="snapshot">What the database currently holds.</param>
    /// <param name="databaseName">Catalog name, carried onto the result for reporting.</param>
    public static SchemaVerificationResult Compare(
        SchemaExpectation expectation,
        SchemaSnapshot snapshot,
        string databaseName)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(snapshot);

        var missingTables = new List<ExpectedTable>();
        var missingColumns = new List<MissingColumn>();

        foreach (var table in expectation.Tables)
        {
            if (!snapshot.HasTable(table.Schema, table.Name))
            {
                // The whole table is absent. Listing every column it would have carried
                // buries the one fact that matters, so record the table and move on.
                missingTables.Add(table);
                continue;
            }

            foreach (var column in table.Columns)
            {
                if (!snapshot.HasColumn(table.Schema, table.Name, column))
                {
                    missingColumns.Add(new MissingColumn(table.Schema, table.Name, column));
                }
            }
        }

        // A column checked by type: only when it exists (a missing one is already reported above) and
        // the snapshot knows its type.
        var wrongTypes = new List<WrongTypeColumn>();
        foreach (var required in expectation.ColumnTypes)
        {
            var actual = snapshot.ColumnType(required.Schema, required.Table, required.Column);
            if (!string.IsNullOrEmpty(actual)
                && !string.Equals(actual, required.SqlType, StringComparison.OrdinalIgnoreCase))
            {
                wrongTypes.Add(new WrongTypeColumn(required, actual));
            }
        }

        return new SchemaVerificationResult(databaseName, missingTables, missingColumns, wrongTypes);
    }
}
