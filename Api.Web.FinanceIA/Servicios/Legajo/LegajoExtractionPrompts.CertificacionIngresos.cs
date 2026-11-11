// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

public static partial class LegajoExtractionPrompts
{
    // ── Certificación de ingresos (RT 37) — Phase 3a ────────────────────────────

    /// <summary>
    /// Certificación de ingresos: a contador público certifies the person's income for a period. As for the
    /// manifestación, an unstated unit is null (the document goes to review with amountsUnit missing), and
    /// the accountant is read only as matrícula + consejo, never by name or CUIT.
    /// </summary>
    public static LegajoExtractionSchema CertificacionIngresos { get; } = new()
    {
        DocumentType    = DocumentTypes.CertificacionIngresos,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "ci-1.0",
        ToolName        = "record_certificacion_ingresos",
        ToolDescription = "Records the figures of a certificación de ingresos exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of ONE Argentine certificación de ingresos of a natural person, issued by a contador " +
            "público (RT 37), usually with the consejo profesional's legalization. Record your reading by calling the " +
            "record_certificacion_ingresos tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsNull + TaxReadingRulesTail + "\n" +
            "\n" +
            "Certificación de ingresos specifics:\n" +
            "- header: periodFrom and periodTo are the first and the last day of the period the certified ingresos cover " +
            "(when only months are printed, the first day of the first month and the last day of the last month). " +
            "engagementType: Certificacion for a certificación, Compilacion for an informe de compilación, Ninguno when no " +
            "accountant signed it, null when you cannot tell. accountant: only the accountant's matrícula (tomo/folio or " +
            "number) and consejo profesional, never the accountant's name or CUIT. legalization: the consejo that " +
            "legalized the signature and the legalization code or number, when printed.\n" +
            "- rows: every certified amount, one per printed row, EXCEPT the subtotals and totals (those go in totals). " +
            "period: the month the amount is for, or null when the amounts are not monthly. concept: as printed (ventas, " +
            "honorarios, sueldos …).\n" +
            "- totals: totalIngresos, the total of the certified ingresos, as printed.",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("holderCuit",     Field(LegajoValueSpec.Cuit)),
                new("periodFrom",     Field(LegajoValueSpec.Date)),
                new("periodTo",       Field(LegajoValueSpec.Date)),
                new("amountsUnit",    Field(LegajoValueSpec.OneOf([.. V.AmountsUnits]))),
                new("engagementType", Field(LegajoValueSpec.OneOf([.. EngagementTypes.Known]))),
                new("accountant", new LegajoSchemaObject(
                [
                    new("matricula", Field(LegajoValueSpec.Text(60))),
                    new("consejo",   Field(LegajoValueSpec.Text(120)))
                ])),
                new("legalization", new LegajoSchemaObject(
                [
                    new("consejo", Field(LegajoValueSpec.Text(120))),
                    new("code",    Field(LegajoValueSpec.Text(60)))
                ]))
            ])),
            new("rows", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period",  Value(LegajoValueSpec.Period)),
                new("concept", Value(LegajoValueSpec.Text(200))),
                new("amount",  Value(LegajoValueSpec.Money))
            ], ItemProvenance: true))),
            new("totals", new LegajoSchemaObject(
            [
                new("totalIngresos", Field(LegajoValueSpec.Money))
            ]))
        ]),
        RequiredFieldPaths = ["/header/periodFrom", "/header/periodTo", V.AmountsUnitPath],
        // The income: its printed total, or at least one certified amount.
        Requirements       =
        [
            new("/totals/totalIngresos", tree => LegajoJsonValues.FieldDecimal(tree, "/totals/totalIngresos") is not null
                                                 || LegajoJsonValues.ArrayAt(tree, "/rows").OfType<JsonObject>()
                                                        .Any(r => LegajoJsonValues.Decimal(r["amount"]) is not null))
        ]
    };
}
