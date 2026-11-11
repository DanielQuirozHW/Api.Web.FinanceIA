namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// One table an EF Core model expects to find in the database, with every column
/// the model maps on it.
/// </summary>
/// <param name="Schema">Schema name. EF reports null for the provider default, which is normalised to "dbo" here.</param>
/// <param name="Name">Table name exactly as the model maps it.</param>
/// <param name="Columns">Every column name the model maps on this table.</param>
public sealed record ExpectedTable(
    string Schema,
    string Name,
    IReadOnlyCollection<string> Columns)
{
    /// <summary>
    /// Schema-qualified name for reporting, e.g. [dbo].[ChatMessages].
    /// </summary>
    public string QualifiedName => $"[{Schema}].[{Name}]";
}
