namespace Api.Web.FinanceIA.Clases.SchemaVerification;

/// <summary>
/// A column the gate checks by type (<see cref="ExpectedColumnType"/>) that exists with another type.
/// </summary>
/// <param name="Expected">The required column and type.</param>
/// <param name="ActualType">The <c>sys.types</c> name the database reports.</param>
public sealed record WrongTypeColumn(ExpectedColumnType Expected, string ActualType)
{
    /// <summary>Schema-qualified column name for reporting.</summary>
    public string QualifiedName => Expected.QualifiedName;
}
