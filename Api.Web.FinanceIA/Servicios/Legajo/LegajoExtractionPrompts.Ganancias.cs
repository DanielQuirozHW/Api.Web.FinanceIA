// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

public static partial class LegajoExtractionPrompts
{
    // ── Ganancias (F.711 and its papeles de trabajo) — Phase 3a ─────────────────

    /// <summary>
    /// Ganancias: the DDJJ anual of a natural person (F.711 / F.715) and its papeles de trabajo — the net
    /// result per category, the liquidación totals and the justificación de las variaciones patrimoniales,
    /// whose monto consumido Phase 4 scores.
    /// </summary>
    public static LegajoExtractionSchema Ganancias { get; } = new()
    {
        DocumentType    = DocumentTypes.Ganancias,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "gan-1.0",
        ToolName        = "record_ganancias",
        ToolDescription = "Records the figures of a Ganancias DDJJ exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of ONE Argentine DDJJ del Impuesto a las Ganancias of a natural person (F.711 or " +
            "F.715 and its papeles de trabajo). Record your reading by calling the record_ganancias tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsPesos + TaxReadingRulesTailNoPeriods + "\n" +
            "\n" +
            "Ganancias specifics:\n" +
            SignedAmountRule +
            "- header: fiscalYear is the período fiscal declared; isRectificativa is true for a DDJJ rectificativa and " +
            "rectificativaNumber is its number (null for an original).\n" +
            "- categoryResults: the net result (ganancia neta or quebranto) of each category as the liquidación prints it, " +
            "one row per category and source, NOT each income source or each item of the papeles de trabajo. category: " +
            "Primera, Segunda, Tercera or Cuarta. source: Argentina for fuente argentina, Exterior for fuente extranjera. " +
            "isCedular: true for a result taxed by the impuesto cedular (rentas financieras, enajenación de acciones, " +
            "dividendos), false otherwise. label: as printed.\n" +
            "- totals, each as printed or null: gananciaNeta (the ganancia neta before the deducciones personales), " +
            "deduccionesPersonales (their total), gananciaNetaSujetaAImpuesto, impuestoDeterminado (of the general " +
            "schedule, not the impuesto cedular), retencionesYPercepciones (only when the document prints them as one " +
            "figure; null when it prints retenciones, percepciones or pagos a cuenta only separately), anticipos (the " +
            "anticipos ingresados), saldoAmount and saldoSide (the final saldo: Fisco for a saldo a favor de AFIP/ARCA, " +
            "Contribuyente for a saldo a favor del contribuyente).\n" +
            "- justification: only when the document includes the justificación de las variaciones patrimoniales; " +
            "otherwise null. patrimonioInicio and patrimonioCierre: the patrimonio neto at the start and at the close " +
            "of the year. variacionPatrimonial: the variación patrimonial, when printed. montoConsumido: the monto " +
            "consumido the document prints, what the person spent on living during the year; read it, never compute " +
            "it. lines: every line with an amount printed inside either side or column of the justification, EXCEPT " +
            "that side's total: side Origen for the concepts that justify erogaciones or patrimonial increases " +
            "(ingresos, orígenes de fondos), Aplicacion for the concepts that do not (erogaciones, aplicaciones de " +
            "fondos). A patrimonio, variación or monto consumido line printed inside a side is one of its lines too. " +
            "totalOrigenes and totalAplicaciones: the total printed for each side.",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("fiscalYear",          Field(LegajoValueSpec.Integer)),
                new("holderCuit",          Field(LegajoValueSpec.Cuit)),
                new("isRectificativa",     Field(LegajoValueSpec.Boolean)),
                new("rectificativaNumber", Field(LegajoValueSpec.Integer)),
                new("amountsUnit",         Field(LegajoValueSpec.OneOf([.. V.AmountsUnits])))
            ])),
            new("categoryResults", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("category",  Value(LegajoValueSpec.OneOf([.. V.GananciasCategories]))),
                new("source",    Value(LegajoValueSpec.OneOf([.. V.IncomeSources]))),
                new("isCedular", Value(LegajoValueSpec.Boolean)),
                new("label",     Value(LegajoValueSpec.Text(200))),
                new("amount",    Value(LegajoValueSpec.Money))
            ], ItemProvenance: true))),
            new("totals", new LegajoSchemaObject(
            [
                new("gananciaNeta",                Field(LegajoValueSpec.Money)),
                new("deduccionesPersonales",       Field(LegajoValueSpec.Money)),
                new("gananciaNetaSujetaAImpuesto", Field(LegajoValueSpec.Money)),
                new("impuestoDeterminado",         Field(LegajoValueSpec.Money)),
                new("retencionesYPercepciones",    Field(LegajoValueSpec.Money)),
                new("anticipos",                   Field(LegajoValueSpec.Money)),
                new("saldoAmount",                 Field(LegajoValueSpec.Money)),
                new("saldoSide",                   Field(LegajoValueSpec.OneOf([.. V.SaldoSides])))
            ])),
            new("justification", new LegajoSchemaObject(
            [
                new("patrimonioInicio",     Field(LegajoValueSpec.Money)),
                new("patrimonioCierre",     Field(LegajoValueSpec.Money)),
                new("variacionPatrimonial", Field(LegajoValueSpec.Money)),
                new("totalOrigenes",        Field(LegajoValueSpec.Money)),
                new("totalAplicaciones",    Field(LegajoValueSpec.Money)),
                new("montoConsumido",       Field(LegajoValueSpec.Money)),
                new("lines", new LegajoSchemaArray(new LegajoSchemaObject(
                [
                    new("side",   Value(LegajoValueSpec.OneOf([.. V.JustificationSides]))),
                    new("label",  Value(LegajoValueSpec.Text(200))),
                    new("amount", Value(LegajoValueSpec.Money))
                ], ItemProvenance: true)))
            ], Nullable: true))
        ]),
        RequiredFieldPaths = ["/header/fiscalYear", V.AmountsUnitPath],
        // The income itself: the ganancia neta, or at least the result of one category.
        Requirements       =
        [
            new("/totals/gananciaNeta", tree => LegajoJsonValues.FieldDecimal(tree, "/totals/gananciaNeta") is not null
                                                || LegajoJsonValues.ArrayAt(tree, "/categoryResults").OfType<JsonObject>().Any())
        ]
    };
}
