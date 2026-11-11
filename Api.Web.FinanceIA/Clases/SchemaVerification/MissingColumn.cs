namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// A column the EF model maps on an existing table that the database does not have.
/// This is the exact shape of the failure the deploy order exists to prevent: EF emits
/// the column in every SELECT and INSERT against the table, so the database answers
/// "Invalid column name" until the matching DDL runs.
/// </summary>
/// <param name="Schema">Schema of the table holding the column.</param>
/// <param name="Table">Table the model maps the column on.</param>
/// <param name="Column">Column name the model expects.</param>
public sealed record MissingColumn(string Schema, string Table, string Column)
{
    /// <summary>
    /// Schema-qualified name for reporting, e.g. [dbo].[ChatMessages].[AttachmentFileName].
    /// </summary>
    public string QualifiedName => $"[{Schema}].[{Table}].[{Column}]";
}
