namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// A column whose SQL Server TYPE the deploy gate checks, on top of its existence.
///
/// The gate is name-based by design: a missing table or column is the failure the deploy order
/// prevents. One case slips through that: a table that already EXISTS with the right column names
/// but a type the mapped code cannot write. [dbo].[AnalysisEdits] was created by 001 with a
/// uniqueidentifier TenantId and only DDL 081 makes it NVARCHAR — before 081 every name matches,
/// and the first insert of a tenant slug fails at runtime. An entry here makes that a gate failure.
/// </summary>
/// <param name="Schema">Schema of the table.</param>
/// <param name="Table">Table name.</param>
/// <param name="Column">Column name.</param>
/// <param name="SqlType">The required <c>sys.types</c> name, e.g. <c>nvarchar</c>.</param>
public sealed record ExpectedColumnType(string Schema, string Table, string Column, string SqlType)
{
    /// <summary>Schema-qualified column name for reporting.</summary>
    public string QualifiedName => $"[{Schema}].[{Table}].[{Column}]";
}
