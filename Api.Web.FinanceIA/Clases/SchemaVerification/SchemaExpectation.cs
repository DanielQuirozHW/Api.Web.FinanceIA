// Microsoft.EntityFrameworkCore carries the relational extension methods this file reads the
// mapping through — GetTableName, GetSchema and GetColumnName — and is NOT one of the SDK's
// implicit usings, so it has to be declared even though no type from it is named directly.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// Everything an EF Core model will ask a database for: every mapped table and,
/// on each, every mapped column.
///
/// This is deliberately derived from the MODEL rather than from the DDL scripts in
/// docs/database/. EF Core emits every mapped column in each SELECT and INSERT
/// unconditionally, so the model — not the script folder — is the exact statement of
/// what the database must already contain for the deployed code to work. Parsing the
/// .sql files would only tell us what someone intended to run.
/// </summary>
public sealed class SchemaExpectation
{
    private const string DefaultSchema = "dbo";

    /// <summary>
    /// The columns whose TYPE the gate checks, beyond their existence — deliberately a short, explicit
    /// list (see <see cref="ExpectedColumnType"/>), so adding the type check changed no other table's
    /// verdict. An entry only applies while the model maps its table.
    /// </summary>
    public static readonly IReadOnlyList<ExpectedColumnType> RequiredColumnTypes =
    [
        // DDL 081: created uniqueidentifier by 001, never migrated by 005, mapped as a slug since 10-F3a.
        new(DefaultSchema, "AnalysisEdits", "TenantId", "nvarchar"),
    ];

    private SchemaExpectation(IReadOnlyCollection<ExpectedTable> tables, IReadOnlyCollection<ExpectedColumnType> columnTypes)
    {
        Tables      = tables;
        ColumnTypes = columnTypes;
    }

    /// <summary>
    /// Every table the model maps, each carrying its mapped columns.
    /// </summary>
    public IReadOnlyCollection<ExpectedTable> Tables { get; }

    /// <summary>
    /// The <see cref="RequiredColumnTypes"/> entries whose table and column this expectation maps.
    /// </summary>
    public IReadOnlyCollection<ExpectedColumnType> ColumnTypes { get; }

    /// <summary>
    /// Builds an expectation from tables stated directly, for tests that exercise the
    /// comparison rather than the model reading. Internal on purpose: production code must
    /// derive the expectation from the model, which is the only thing that actually predicts
    /// the SQL EF will emit.
    /// </summary>
    internal static SchemaExpectation FromTables(
        IReadOnlyCollection<ExpectedTable> tables, IReadOnlyCollection<ExpectedColumnType>? columnTypes = null)
    {
        ArgumentNullException.ThrowIfNull(tables);

        return new SchemaExpectation(tables, columnTypes ?? []);
    }

    /// <summary>
    /// Reads the mapped tables and columns out of an EF Core model.
    /// Building a model does not open a connection, so this is safe to call against a
    /// context configured with a connection string that is never reached.
    /// </summary>
    /// <param name="model">The model to read, e.g. <c>context.Model</c>.</param>
    public static SchemaExpectation FromModel(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Entity types that share a table (owned types, TPH hierarchies) must contribute
        // their columns to ONE entry, so accumulate by qualified table name.
        var byTable = new Dictionary<(string Schema, string Name), SortedSet<string>>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();

            // Null for a type mapped to a view, a function or a raw SQL query rather than
            // a table. Those have no table for this check to look for.
            if (string.IsNullOrWhiteSpace(tableName))
            {
                continue;
            }

            var schema = entityType.GetSchema() ?? DefaultSchema;
            var key = (schema, tableName);

            if (!byTable.TryGetValue(key, out var columns))
            {
                columns = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                byTable[key] = columns;
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject);

                // Null when the property is not mapped to a column on THIS table — for
                // instance a property of an entity split across tables.
                if (!string.IsNullOrWhiteSpace(columnName))
                {
                    columns.Add(columnName);
                }
            }
        }

        var tables = byTable
            .Select(pair => new ExpectedTable(pair.Key.Schema, pair.Key.Name, pair.Value.ToArray()))
            .OrderBy(table => table.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // A type check only applies to a column the model actually maps.
        var columnTypes = RequiredColumnTypes
            .Where(required => tables.Any(table =>
                string.Equals(table.Schema, required.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(table.Name, required.Table, StringComparison.OrdinalIgnoreCase)
                && table.Columns.Contains(required.Column, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        return new SchemaExpectation(tables, columnTypes);
    }
}
