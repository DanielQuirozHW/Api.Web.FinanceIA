// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoItems].[MatchKeyKind] — CK_LegajoItems_MatchKeyKind (DDL 071). The identifier an asset is matched by across documents; only the first four match.</summary>
public static class LegajoMatchKeyKinds
{
    /// <summary>Partida inmobiliaria / nomenclatura catastral.</summary>
    public const string Partida = "Partida";

    /// <summary>Vehicle registration.</summary>
    public const string Dominio = "Dominio";

    /// <summary>The CUIT of the company, issuer or debtor.</summary>
    public const string Cuit = "Cuit";

    /// <summary>The CBU of an account.</summary>
    public const string Cbu = "Cbu";

    /// <summary>Another identifier: never matched.</summary>
    public const string Otro = "Otro";

    /// <summary>No identifier: never matched.</summary>
    public const string Ninguno = "Ninguno";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Partida, Dominio, Cuit, Cbu, Otro, Ninguno];
}
