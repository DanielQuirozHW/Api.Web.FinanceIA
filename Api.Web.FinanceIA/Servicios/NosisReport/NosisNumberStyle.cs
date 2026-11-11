// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// How a section writes its numbers, inferred from the tokens whose shape alone decides: US
/// ("1,939,384,960", "31,472.6"), Argentine ("1.527.384.960", "5,00"), both (<see cref="Mixed"/>, e.g.
/// the MAV block and its total), or neither. It only resolves AMBIGUOUS tokens ("1,234", "1.234").
/// </summary>
internal enum NosisNumberStyle
{
    Unknown,
    Us,
    Ar,
    Mixed,
}
