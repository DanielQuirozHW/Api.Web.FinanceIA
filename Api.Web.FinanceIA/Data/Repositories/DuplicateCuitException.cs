// Project : Api.Web.FinanceIA
// Created : 2026-09-27

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Thrown by <see cref="EntityRepository"/> and <see cref="PersonLegajoRepository"/> when a write
/// loses a race against one of the per-tenant CUIT uniqueness indexes of DDL 069. The caller's
/// pre-check reads cannot close that race — two requests can both read "absent" before either
/// writes — so the index decides, and this tells the controller WHICH index did.
/// </summary>
/// <remarks>
/// Deliberately carries NO inner exception and no provider text. SQL Server's message for a unique
/// violation ends with "The duplicate key value is (…)", which for these indexes is the tenant and
/// the CUIT — the CUIL of a natural person. Anything that let that message reach
/// GlobalExceptionHandler would persist it into ErrorLogs.StackTrace. The failed entries are
/// detached before this is thrown, so the request-scoped context stays usable for a retry.
/// </remarks>
public sealed class DuplicateCuitException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="DuplicateCuitException"/>.
    /// </summary>
    /// <param name="scope">The uniqueness rule the write lost against.</param>
    public DuplicateCuitException(DuplicateCuitScope scope)
        : base($"A row with this CUIT already exists in the tenant ({scope}).")
    {
        Scope = scope;
    }

    /// <summary>The uniqueness rule the write lost against.</summary>
    public DuplicateCuitScope Scope { get; }
}
