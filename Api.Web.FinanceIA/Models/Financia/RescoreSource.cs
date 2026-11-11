// Project : Api.Web.FinanceIA
// Created : 2026-10-22

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The date of one source behind a score: <c>{ source, asOf, live }</c>. <see cref="Live"/> is true
/// only for a source queried at scoring time (the BCRA); the others are the entity snapshots (for a
/// persona humana analysis, the legajo's), and
/// <see cref="AsOf"/> is when that snapshot was refreshed — null when there was none, or when its
/// date can no longer be known.
/// </summary>
public sealed record RescoreSource(string Source, DateTime? AsOf, bool Live)
{
    public const string Bcra   = "BCRA";
    public const string Lufe   = "LUFE";
    public const string Casfog = "CASFOG";
    public const string Arca   = "ARCA";
    public const string Nosis  = "NOSIS";

    /// <summary>
    /// A persona humana re-evaluation only: the person's legajo — <see cref="AsOf"/> is the last change to
    /// its reconciled inventory.
    /// </summary>
    public const string Legajo = "LEGAJO";
}
