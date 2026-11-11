// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

public static partial class LegajoExtractionPrompts
{
    // ── Monotributo — Phase 3a ──────────────────────────────────────────────────

    /// <summary>
    /// Monotributo: the constancia de inscripción or credencial de pago, a recategorización and/or payment
    /// receipts — the category, the activity, the cuota and the payments. Whether the category fits the
    /// person's income is scoring (Phase 4), not a check here.
    /// </summary>
    public static LegajoExtractionSchema Monotributo { get; } = new()
    {
        DocumentType    = DocumentTypes.Monotributo,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "mono-1.0",
        ToolName        = "record_monotributo",
        ToolDescription = "Records the figures of monotributo documents exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of the Argentine monotributo (Régimen Simplificado) documents of a natural person: " +
            "the constancia de inscripción or credencial de pago, a recategorización, and/or payment receipts. Record your " +
            "reading by calling the record_monotributo tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsPesos + TaxReadingRulesTail + "\n" +
            "\n" +
            "Monotributo specifics:\n" +
            "- header: categoria is the category letter, A to K, as printed (the latest one when the document shows a " +
            "recategorización). activityType: Servicios for locaciones y prestaciones de servicios, VentaDeBienes for " +
            "venta de cosas muebles, null when not printed. adhesionDate: the fecha de adhesión or alta. cuotaMensual: " +
            "the monthly cuota of the category as printed; the total cuota (impuesto integrado plus aportes) when the " +
            "document prints it.\n" +
            "- payments: every payment printed (a receipt, a VEP, a list of payments), one per row. period: the month " +
            "the payment is for. amount: as printed. paymentDate: the date it was paid, or null.",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("holderCuit",   Field(LegajoValueSpec.Cuit)),
                new("categoria",    Field(LegajoValueSpec.OneOf([.. V.MonotributoCategories]))),
                new("activityType", Field(LegajoValueSpec.OneOf([.. V.MonotributoActivities]))),
                new("adhesionDate", Field(LegajoValueSpec.Date)),
                new("cuotaMensual", Field(LegajoValueSpec.Money)),
                new("amountsUnit",  Field(LegajoValueSpec.OneOf([.. V.AmountsUnits])))
            ])),
            new("payments", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period",      Value(LegajoValueSpec.Period)),
                new("amount",      Value(LegajoValueSpec.Money)),
                new("paymentDate", Value(LegajoValueSpec.Date))
            ], ItemProvenance: true)))
        ]),
        RequiredFieldPaths = [V.AmountsUnitPath],
        // What the document is about: the category, or at least one payment.
        Requirements       =
        [
            new("/header/categoria", tree => LegajoJsonValues.FieldString(tree, "/header/categoria") is not null
                                             || LegajoJsonValues.ArrayAt(tree, "/payments").Count > 0)
        ]
    };
}
