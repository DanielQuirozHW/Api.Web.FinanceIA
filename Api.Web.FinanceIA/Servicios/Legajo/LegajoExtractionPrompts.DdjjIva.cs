// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

public static partial class LegajoExtractionPrompts
{
    // ── DDJJ IVA (F.2002 / F.731) — Phase 3a ────────────────────────────────────

    /// <summary>
    /// DDJJ IVA: one monthly DDJJ or several in one PDF. One row per DDJJ (an original and a rectificativa of
    /// the same month are two rows), and every débito/crédito line carrying an IVA amount, keyed to its DDJJ by
    /// period + rectificativaNumber.
    /// </summary>
    /// <remarks>
    /// The saldo técnico is a nullable {amount, side} pair: an enum inside a row may not be null, and a DDJJ
    /// that prints no saldo técnico has neither. saldoTecnicoAnterior is read because the F.2002 liquidación
    /// subtracts the previous period's saldo técnico a favor del responsable.
    /// </remarks>
    public static LegajoExtractionSchema DdjjIva { get; } = new()
    {
        DocumentType    = DocumentTypes.DdjjIva,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "iva-1.0",
        ToolName        = "record_ddjj_iva",
        ToolDescription = "Records the figures of one or more DDJJ de IVA exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of the Argentine DDJJ del IVA (F.2002, or the older F.731) of a natural person: ONE " +
            "monthly DDJJ or several. Record your reading by calling the record_ddjj_iva tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsPesos + TaxReadingRulesTail + "\n" +
            "\n" +
            "DDJJ IVA specifics:\n" +
            "- periods: one row per DDJJ in the document; a period declared twice (an original and a rectificativa) is two " +
            "rows. period: the período fiscal. rectificativaNumber: the number of a rectificativa, null for an original. " +
            "Each amount as printed for that DDJJ, or null: ventasNetoGravado (the total neto gravado of the sales, only " +
            "when printed as a total), ventasExentasNoGravadas (the operaciones exentas y no gravadas), debitoFiscal (the " +
            "total del débito fiscal), creditoFiscal (the total del crédito fiscal), saldoTecnicoAnterior (the saldo técnico " +
            "a favor del responsable del período anterior), saldoTecnico (the saldo técnico of the period: its amount, and " +
            "side Fisco when it is a favor de AFIP/ARCA or Contribuyente when it is a favor del responsable; null when not " +
            "printed) and saldoAPagar (the saldo del impuesto a favor de AFIP/ARCA to pay).\n" +
            "- alicuotaLines: every line of the débito fiscal and crédito fiscal detail that carries an IVA amount, one per " +
            "printed row, with the period and rectificativaNumber of its DDJJ. side: Debito or Credito. label: the line's " +
            "concept, short. alicuotaPct: the alícuota as printed (21, 10.5, 27, 5, 2.5), or null for a line without one " +
            "(restituciones, notas de crédito, other adjustments). netoGravado: the neto gravado of the line, never an " +
            "amount IVA incluido (null when the line prints only an IVA-incluido total). impuesto: the IVA amount of the " +
            "line, negative when the line reduces its total (for example notas de crédito).",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("holderCuit",  Field(LegajoValueSpec.Cuit)),
                new("amountsUnit", Field(LegajoValueSpec.OneOf([.. V.AmountsUnits])))
            ])),
            new("periods", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period",                  Value(LegajoValueSpec.Period)),
                new("rectificativaNumber",     Value(LegajoValueSpec.Integer)),
                new("ventasNetoGravado",       Value(LegajoValueSpec.Money)),
                new("ventasExentasNoGravadas", Value(LegajoValueSpec.Money)),
                new("debitoFiscal",            Value(LegajoValueSpec.Money)),
                new("creditoFiscal",           Value(LegajoValueSpec.Money)),
                new("saldoTecnicoAnterior",    Value(LegajoValueSpec.Money)),
                new("saldoTecnico", new LegajoSchemaObject(
                [
                    new("amount", Value(LegajoValueSpec.Money)),
                    new("side",   Value(LegajoValueSpec.OneOf([.. V.SaldoSides])))
                ], Nullable: true)),
                new("saldoAPagar",             Value(LegajoValueSpec.Money))
            ], ItemProvenance: true))),
            new("alicuotaLines", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period",              Value(LegajoValueSpec.Period)),
                new("rectificativaNumber", Value(LegajoValueSpec.Integer)),
                new("side",                Value(LegajoValueSpec.OneOf([.. V.IvaSides]))),
                new("label",               Value(LegajoValueSpec.Text(120))),
                new("alicuotaPct",         Value(LegajoValueSpec.Percent)),
                new("netoGravado",         Value(LegajoValueSpec.Money)),
                new("impuesto",            Value(LegajoValueSpec.Money))
            ], ItemProvenance: true)))
        ]),
        RequiredFieldPaths = [V.AmountsUnitPath],
        // At least one DDJJ with its period and either its sales or its débito fiscal.
        Requirements       =
        [
            new("/periods", tree => LegajoJsonValues.ArrayAt(tree, "/periods").OfType<JsonObject>()
                .Any(p => LegajoJsonValues.Period(p["period"]) is not null
                          && (LegajoJsonValues.Decimal(p["ventasNetoGravado"]) is not null
                              || LegajoJsonValues.Decimal(p["debitoFiscal"]) is not null)))
        ],
        // A 12-month PDF with rectificativas runs to ~200 lines with row provenance (~20k output tokens worst
        // case); 16k would truncate it.
        MaxTokens          = 24000
    };
}
