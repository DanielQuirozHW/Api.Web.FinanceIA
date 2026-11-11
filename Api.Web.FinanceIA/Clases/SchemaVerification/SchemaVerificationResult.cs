namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// The outcome of comparing one EF model against one database.
/// </summary>
/// <param name="DatabaseName">
/// The database this result describes, for reporting. Only the catalog name — never the
/// server, the user or the password, so a result is safe to print in a CI log.
/// </param>
/// <param name="MissingTables">Tables the model maps that the database does not have.</param>
/// <param name="MissingColumns">
/// Columns missing from tables that DO exist. A table listed in <paramref name="MissingTables"/>
/// contributes no entries here — one line saying the table is absent is more useful than one
/// line per column it would have carried.
/// </param>
/// <param name="WrongTypeColumns">
/// Type-checked columns (<see cref="SchemaExpectation.ColumnTypes"/>) present with another type.
/// </param>
public sealed record SchemaVerificationResult(
    string DatabaseName,
    IReadOnlyCollection<ExpectedTable> MissingTables,
    IReadOnlyCollection<MissingColumn> MissingColumns,
    IReadOnlyCollection<WrongTypeColumn>? WrongTypeColumns = null)
{
    /// <summary>The wrong-type columns, never null.</summary>
    public IReadOnlyCollection<WrongTypeColumn> WrongTypes => WrongTypeColumns ?? [];

    /// <summary>
    /// True when the database carries every table and column the model maps, and every type-checked
    /// column has its required type.
    /// </summary>
    public bool IsSatisfied => MissingTables.Count == 0 && MissingColumns.Count == 0 && WrongTypes.Count == 0;
}
