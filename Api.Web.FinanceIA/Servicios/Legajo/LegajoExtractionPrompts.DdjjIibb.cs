// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

public static partial class LegajoExtractionPrompts
{
    // ── DDJJ Ingresos Brutos (CM03 / CM05 / local) — Phase 3a ───────────────────

    /// <summary>
    /// DDJJ Ingresos Brutos: Convenio Multilateral (CM03 monthly, CM05 annual) or a provincial local regime,
    /// one DDJJ or several. One row per DDJJ with its totals, and the determinación lines per jurisdiction and
    /// activity, keyed to their DDJJ by period + rectificativaNumber. An annual DDJJ has no period: its year is
    /// header.fiscalYear.
    /// </summary>
    public static LegajoExtractionSchema DdjjIibb { get; } = new()
    {
        DocumentType    = DocumentTypes.DdjjIibb,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "iibb-1.0",
        ToolName        = "record_ddjj_iibb",
        ToolDescription = "Records the figures of one or more DDJJ de Ingresos Brutos exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of the Argentine DDJJ del Impuesto sobre los Ingresos Brutos of a natural person: " +
            "Convenio Multilateral (CM03 monthly, CM05 annual) or a provincial local regime; ONE DDJJ or several. Record " +
            "your reading by calling the record_ddjj_iibb tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsPesos + TaxReadingRulesTail + "\n" +
            "\n" +
            "DDJJ Ingresos Brutos specifics:\n" +
            "- header: regimen is ConvenioMultilateral or Local, null when you cannot tell. fiscalYear: the year an ANNUAL " +
            "DDJJ declares (CM05, or an annual local DDJJ); null for a monthly one.\n" +
            "- periods: one row per DDJJ in the document. period: its month (null for an annual DDJJ). " +
            "rectificativaNumber: the number of a rectificativa, null for an original. ingresosTotales: the total " +
            "ingresos of the DDJJ (all jurisdictions, the monto imponible total), when printed. impuestoTotal: the total " +
            "impuesto determinado of the DDJJ, when printed.\n" +
            "- rows: every printed line of the determinación of the tax, per jurisdiction and activity, with the period " +
            "and rectificativaNumber of its DDJJ; never a subtotal or a total. jurisdiction: as printed (the province or " +
            "its code). activity: the activity code and/or description, as printed. baseImponible, alicuotaPct (the " +
            "alícuota as printed, for example 3.5) and impuestoDeterminado: as printed, or null.",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("holderCuit",  Field(LegajoValueSpec.Cuit)),
                new("regimen",     Field(LegajoValueSpec.OneOf([.. V.IibbRegimes]))),
                new("fiscalYear",  Field(LegajoValueSpec.Integer)),
                new("amountsUnit", Field(LegajoValueSpec.OneOf([.. V.AmountsUnits])))
            ])),
            new("periods", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period",              Value(LegajoValueSpec.Period)),
                new("rectificativaNumber", Value(LegajoValueSpec.Integer)),
                new("ingresosTotales",     Value(LegajoValueSpec.Money)),
                new("impuestoTotal",       Value(LegajoValueSpec.Money))
            ], ItemProvenance: true))),
            new("rows", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("period",              Value(LegajoValueSpec.Period)),
                new("rectificativaNumber", Value(LegajoValueSpec.Integer)),
                new("jurisdiction",        Value(LegajoValueSpec.Text(60))),
                new("activity",            Value(LegajoValueSpec.Text(200))),
                new("baseImponible",       Value(LegajoValueSpec.Money)),
                new("alicuotaPct",         Value(LegajoValueSpec.Percent)),
                new("impuestoDeterminado", Value(LegajoValueSpec.Money))
            ], ItemProvenance: true)))
        ]),
        RequiredFieldPaths = [V.AmountsUnitPath],
        // The sales or the tax: a determinación line with its base or its tax, or a DDJJ's total ingresos.
        Requirements       =
        [
            new("/rows", tree => LegajoJsonValues.ArrayAt(tree, "/rows").OfType<JsonObject>()
                                     .Any(r => LegajoJsonValues.Decimal(r["baseImponible"]) is not null
                                               || LegajoJsonValues.Decimal(r["impuestoDeterminado"]) is not null)
                                 || LegajoJsonValues.ArrayAt(tree, "/periods").OfType<JsonObject>()
                                     .Any(p => LegajoJsonValues.Decimal(p["ingresosTotales"]) is not null))
        ],
        // Twelve monthly CM03 with several jurisdictions run to a few hundred rows with row provenance.
        MaxTokens          = 24000
    };
}
