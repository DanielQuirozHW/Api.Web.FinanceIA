// Project : Api.Web.FinanceIA
// Created : 2026-08-16

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Unit of a rule's <c>ThresholdValue</c>, declared next to the rule code itself so configuration
/// can validate a threshold without restating per-rule knowledge that belongs to the engine.
/// </summary>
public enum RuleThresholdUnit
{
    /// <summary>
    /// A proportion in (0, 1] — 0,55 means 55 %. Sending 55 for such a rule does not error, it
    /// makes the rule unfireable, which is why the distinction is declared rather than inferred.
    /// </summary>
    Proportion,

    /// <summary>
    /// An absolute figure in the metric's own unit, bounded only by the column's DECIMAL(10,4)
    /// precision. No rule uses this today; it exists so that adding one does not silently inherit
    /// the (0, 1] range check that only <see cref="Proportion"/> should carry.
    /// </summary>
    Absolute,
}

/// <summary>
/// Marks a rule code constant on <see cref="AlertRuleEngine"/> as CONSUMING
/// <c>AlertRuleDefinition.ThresholdValue</c>, and states the unit it is read in.
///
/// An unmarked code takes no threshold: the engine never reads one for it, so a value stored
/// against it would be accepted, shown on the configuration screen, and silently ignored — the
/// analyst would believe she had changed something. Configuration rejects that by asking this
/// attribute, so a future rule that gains a threshold declares it here and needs no validation
/// edited anywhere else.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class RuleThresholdAttribute : Attribute
{
    /// <summary>Initializes the attribute with the unit the engine reads the threshold in.</summary>
    public RuleThresholdAttribute(RuleThresholdUnit unit) => Unit = unit;

    /// <summary>Unit the engine reads this rule's threshold in.</summary>
    public RuleThresholdUnit Unit { get; }
}

/// <summary>
/// The wire vocabulary for <see cref="RuleThresholdUnit"/>.
///
/// Mapped explicitly to constants rather than serialized with <c>JsonStringEnumConverter</c> (which
/// would emit the C# member name, so a rename would silently change the contract) and never as the
/// enum ordinal (which would silently change meaning if a member were inserted). Lowercase to match
/// the vocabulary already on this payload: <c>level</c> is low|medium|high|critical and
/// <c>metric_key</c> is snake_case.
/// </summary>
public static class RuleThresholdUnitWire
{
    /// <summary>Wire value for <see cref="RuleThresholdUnit.Proportion"/>.</summary>
    public const string Proportion = "proportion";

    /// <summary>Wire value for <see cref="RuleThresholdUnit.Absolute"/>.</summary>
    public const string Absolute = "absolute";

    /// <summary>
    /// Renders a unit for the wire. Throws on an unmapped member so that adding one to
    /// <see cref="RuleThresholdUnit"/> forces an explicit contract decision rather than emitting
    /// something a consumer cannot interpret.
    /// </summary>
    public static string ToWire(this RuleThresholdUnit unit) => unit switch
    {
        RuleThresholdUnit.Proportion => Proportion,
        RuleThresholdUnit.Absolute   => Absolute,
        _ => throw new ArgumentOutOfRangeException(
            nameof(unit), unit, "Threshold unit has no declared wire value."),
    };
}
