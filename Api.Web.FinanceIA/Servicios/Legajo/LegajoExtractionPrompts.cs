// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The legajo extraction prompts and schemas, one <see cref="LegajoExtractionSchema"/> per supported
/// document type: Bienes Personales and Manifestación de bienes (Phase 2b, this file) and the Phase 3a
/// types (Ganancias, DDJJ IVA, DDJJ IIBB, certificación de ingresos, monotributo — one partial file each).
/// Versioned: LegajoExtractionPromptTests fingerprints each system prompt + generated schema + tool
/// description against its <see cref="LegajoExtractionSchema.PromptVersion"/>, so none of them can change
/// without a version bump.
/// </summary>
/// <remarks>
/// ⚠ PERSONAL DATA. Nothing here, and nothing the request builder appends, names the legajo's person:
/// no name, no CUIT, no declared data. The model reads the document; C# compares the holder CUIT it
/// read with the legajo afterwards. A test asserts the serialized request contains neither the
/// legajo's CUIT nor its FullName.
/// <para>
/// Types without an extractor (REIBP, flujo proyectado …) are not in <see cref="SupportedTypes"/>, so the
/// worker never claims them and they stay ReadyForExtraction until an extractor exists.
/// </para>
/// </remarks>
public static partial class LegajoExtractionPrompts
{
    /// <summary>The shape version both v1 schemas share.</summary>
    public const string SchemaVersion = "1.0";

    // ── Shared rule text (part of each fingerprinted prompt) ───────────────────
    // Split around the amountsUnit rule, which differs by type: a Bienes Personales DDJJ is an AFIP form,
    // always in pesos, so an unstated unit is Pesos; a manifestación must print its unit, so an unstated
    // one is null and the document goes to review with amountsUnit missing. The shared reading rules are
    // byte-identical to bp-1.0's; bp-1.1 only added investmentClass to the items.

    private const string ReadingRulesHead =
        "Rules for every figure:\n" +
        "- Read, do not compute. Copy every amount exactly as printed, as a JSON number with a dot as the decimal " +
        "separator and no thousands separators (1.234.567,89 on the page is 1234567.89). Never add, subtract, multiply, " +
        "convert currencies or apply percentages. A figure that is not printed is null: never infer it from other figures.\n" +
        "- amountsUnit: the unit the document states for its amounts: Pesos, Miles (\"en miles de pesos\") or Millones. ";

    private const string UnstatedUnitIsPesos = "When no unit is stated, Pesos. ";

    private const string UnstatedUnitIsNull = "When the document does not state a unit, null. ";

    // The tail is cut into its rules so the Phase 3a types can drop matchKey (they have no assets) and add the
    // period rule. ReadingRulesTail concatenates to exactly the bp-1.0 / mb-1.2 text (unchanged in bp-1.1 / mb-1.3).

    private const string UnitReportRule = "Report every amount in that same unit, as printed.\n";

    private const string FieldProvenanceRuleBody =
        " is an object {value, page, quote, confidence}: page is the 1-based page it was " +
        "read on, quote is the literal text it was read from (one line at most), confidence is from 0 to 1 how sure you " +
        "are of the value. When value is null, page and quote are null.\n";

    private const string RowProvenanceRule =
        "- Each array item is ONE printed row: page and quote are that row as printed, confidence is how sure you are of " +
        "the whole row.\n";

    private const string MatchKeyRule =
        "- matchKey: the identifier printed for the asset: Partida (partida inmobiliaria / nomenclatura catastral), " +
        "Dominio (vehicle registration), Cuit (of the company, issuer or debtor), Cbu (of an account), Otro, or Ninguno " +
        "with value null.\n";

    /// <summary>
    /// The investmentClass values (mb-1.3 and bp-1.1). Never guessed: null when the document gives no hint, Otra when it
    /// shows an investment that fits none of the four.
    /// </summary>
    private const string InvestmentClassRule =
        "Its values: TituloPublico for títulos públicos (bonos, letras del Tesoro, títulos de deuda soberana, provincial or " +
        "municipal); AccionCotizante for a share for which the document prints a stock exchange, a ticker or a market " +
        "price; ObligacionNegociable for obligaciones negociables; CuotaparteFci for cuotapartes de fondos comunes de " +
        "inversión; Otra for an investment the document shows but that fits none of these or that you cannot tell apart. " +
        "Never guess: when the document gives no hint of what the investment is, investmentClass is null. ";

    private const string LabelsAndHolderRules =
        "- Descriptions and labels: short, as printed, without the name of any person.\n" +
        "- holderCuit: the CUIT/CUIL of the person the document is ABOUT, as 11 digits without hyphens. Never the CUIT " +
        "of an accountant, a consejo profesional, an employer, a bank, a company in which the person holds shares, or " +
        "AFIP/ARCA. Null when it is not printed or you cannot tell which CUIT is the holder's.";

    private const string ReadingRulesTail =
        UnitReportRule + "- Each header and totals field" + FieldProvenanceRuleBody + RowProvenanceRule + MatchKeyRule +
        LabelsAndHolderRules;

    // ── Phase 3a shared rule text ──────────────────────────────────────────────

    /// <summary>The tail of the Phase 3a types: no matchKey (they list no assets), fields outside arrays.</summary>
    private const string TaxReadingRulesTailNoPeriods =
        UnitReportRule + "- Each field outside an array" + FieldProvenanceRuleBody + RowProvenanceRule + LabelsAndHolderRules;

    /// <summary><see cref="TaxReadingRulesTailNoPeriods"/> plus the period rule, for the types with monthly rows.</summary>
    private const string TaxReadingRulesTail =
        UnitReportRule + "- Each field outside an array" + FieldProvenanceRuleBody + RowProvenanceRule + PeriodRule +
        LabelsAndHolderRules;

    private const string PeriodRule =
        "- period: the month a row refers to, as YYYY-MM (03/2025, 3/2025, marzo 2025 and 202503 on the page are " +
        "2025-03). Null when the row is not about one month.\n";

    private const string SignedAmountRule =
        "- A negative amount (printed with a minus sign, in parentheses, or labelled as a quebranto) is a negative number.\n";

    // ── Bienes Personales (F.762 papeles de trabajo) ───────────────────────────

    /// <summary>Bienes Personales: the DDJJ (F.762/A) and its papeles de trabajo.</summary>
    public static LegajoExtractionSchema BienesPersonales { get; } = new()
    {
        DocumentType    = DocumentTypes.BienesPersonales,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "bp-1.1",
        ToolName        = "record_bienes_personales",
        ToolDescription = "Records the figures of a Bienes Personales DDJJ exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of ONE Argentine DDJJ del Impuesto sobre los Bienes Personales of a natural person " +
            "(F.762/A and its papeles de trabajo). Record your reading by calling the record_bienes_personales tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsPesos + ReadingRulesTail + "\n" +
            "\n" +
            "Bienes Personales specifics:\n" +
            "- The values of this tax are FISCAL values (valuación impositiva, base imponible), never market values. For each " +
            "asset report the fiscal value printed for the declarant; when the form prints both the total valuation and the " +
            "declarant's share, report the share.\n" +
            "- header: fiscalYear is the período fiscal declared; isRectificativa is true for a DDJJ rectificativa and " +
            "rectificativaNumber is its number (null for an original).\n" +
            "- items: every asset of the detail of assets, one per printed row, in the country and abroad. rubro: Inmuebles; " +
            "Rodados; DineroEfectivo (dinero en efectivo); Depositos (depósitos en dinero); TitulosYAcciones (títulos, acciones, " +
            "cuotas y participaciones sociales, fondos comunes de inversión); Creditos; BienesMueblesRegistrables (aeronaves, " +
            "embarcaciones and other registrable movables); OtrosBienes (bienes muebles no registrables, bienes del hogar, " +
            "other assets); Otro (none of these). location: Pais or Exterior. ownershipPct: the ownership percentage as " +
            "printed (100 for full ownership), or null. acquisitionDate: YYYY-MM-DD or null. fiscalValue: the fiscal value " +
            "as printed. currency: the ISO code of the asset's currency when printed (ARS, USD, EUR), otherwise null. " +
            "isExempt: true when the asset is declared exempt. isHousing: true when it is declared the casa-habitación. " +
            "investmentClass: only for a TitulosYAcciones row, otherwise null. " + InvestmentClassRule + "For a TitulosYAcciones " +
            "row that is a share without a quotation, or cuotas or participaciones sociales of a company, investmentClass is " +
            "null.\n" +
            "- rubroTotals: every subtotal printed per rubro, with its location.\n" +
            "- totals: bienesPais, bienesExterior, totalBienes, bienesExentos, minimoNoImponible, baseImponible and " +
            "impuestoDeterminado, each as printed, or null when the document does not print it.",
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
            new("items", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("rubro",           Value(LegajoValueSpec.OneOf([.. V.Rubros]))),
                new("location",        Value(LegajoValueSpec.OneOf([.. V.Locations]))),
                new("description",     Value(LegajoValueSpec.Text(200))),
                new("matchKey",        MatchKey()),
                new("ownershipPct",    Value(LegajoValueSpec.Percent)),
                new("acquisitionDate", Value(LegajoValueSpec.Date)),
                new("fiscalValue",     Value(LegajoValueSpec.Money)),
                new("currency",        Value(LegajoValueSpec.Text(10))),
                new("isExempt",        Value(LegajoValueSpec.Boolean)),
                new("isHousing",       Value(LegajoValueSpec.Boolean)),
                // bp-1.1: the class of a TitulosYAcciones row; null when the form gives no hint.
                new("investmentClass", NullableValue(LegajoValueSpec.OneOf([.. V.InvestmentClasses])))
            ], ItemProvenance: true))),
            new("rubroTotals", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("rubro",    Value(LegajoValueSpec.OneOf([.. V.Rubros]))),
                new("location", Value(LegajoValueSpec.OneOf([.. V.Locations]))),
                new("total",    Value(LegajoValueSpec.Money))
            ], ItemProvenance: true))),
            new("totals", new LegajoSchemaObject(
            [
                new("bienesPais",          Field(LegajoValueSpec.Money)),
                new("bienesExterior",      Field(LegajoValueSpec.Money)),
                new("totalBienes",         Field(LegajoValueSpec.Money)),
                new("bienesExentos",       Field(LegajoValueSpec.Money)),
                new("minimoNoImponible",   Field(LegajoValueSpec.Money)),
                new("baseImponible",       Field(LegajoValueSpec.Money)),
                new("impuestoDeterminado", Field(LegajoValueSpec.Money))
            ]))
        ]),
        // amountsUnit is required: a null unit must not pass on an assumed Pesos (the checks would scale
        // nothing and could pass on the wrong unit).
        RequiredFieldPaths    = ["/header/fiscalYear", "/header/amountsUnit"],
        RequiredAnyArrayPaths = ["/items", "/rubroTotals"],
        // The total of assets: printed, or both of its parts printed (a printed 0 counts). Real forms often
        // print bienesPais and bienesExterior without a grand total. Outside the fingerprint: bp-1.1 stands.
        Requirements          =
        [
            new("/totals/totalBienes", tree => LegajoJsonValues.FieldDecimal(tree, "/totals/totalBienes") is not null
                                               || (LegajoJsonValues.FieldDecimal(tree, "/totals/bienesPais") is not null
                                                   && LegajoJsonValues.FieldDecimal(tree, "/totals/bienesExterior") is not null))
        ]
    };

    // ── Manifestación de bienes ─────────────────────────────────────────────────

    /// <summary>Manifestación de bienes: a person's statement of assets and liabilities.</summary>
    public static LegajoExtractionSchema ManifestacionBienes { get; } = new()
    {
        DocumentType    = DocumentTypes.ManifestacionBienes,
        SchemaVersion   = SchemaVersion,
        PromptVersion   = "mb-1.3",
        ToolName        = "record_manifestacion_bienes",
        ToolDescription = "Records the figures of a manifestación de bienes exactly as printed, each with the page and text it was read from.",
        SystemPrompt    =
            "You extract the figures of ONE Argentine manifestación de bienes (estado de situación patrimonial) of a natural " +
            "person, usually with an accountant's report and annexes. Record your reading by calling the " +
            "record_manifestacion_bienes tool exactly once.\n" +
            "\n" +
            ReadingRulesHead + UnstatedUnitIsNull + ReadingRulesTail + "\n" +
            "\n" +
            "Manifestación de bienes specifics:\n" +
            "- header: measurementDate is the date the statement is measured at. engagementType: Certificacion for a " +
            "certificación, Compilacion for an informe de compilación, Ninguno when no accountant signed it, null when you " +
            "cannot tell. accountant: only the accountant's matrícula (tomo/folio or number) and consejo profesional, never " +
            "the accountant's name. legalization: the consejo that legalized the signature and the legalization code or " +
            "number, when printed.\n" +
            "- lines: every line of the statement that carries an amount, EXCEPT the subtotals and totals (those go in " +
            "totals). section: ActivoCorriente, ActivoNoCorriente, PasivoCorriente or PasivoNoCorriente. label: as printed. " +
            "annexRef: the annex the line refers to, as printed (for example \"A\" or \"Anexo I\"), or null.\n" +
            "- totals: activoCorriente, activoNoCorriente, totalActivo, pasivoCorriente, pasivoNoCorriente, totalPasivo and " +
            "patrimonioNeto, each as printed.\n" +
            "- annexItems: every item listed in an annex, one per printed row. annexRef: the annex it is listed in, written " +
            "exactly as the lines refer to it. kind: Inmueble, Rodado, Participacion, Deposito, Inversion, Credito, Deuda or " +
            "Otro. A share for which the document prints a stock exchange, a ticker or a market price (a listed share) is " +
            "Inversion, even when the issuer's CUIT is printed; a share without any of those, or cuotas or participaciones " +
            "sociales of a company, is Participacion. Títulos públicos, obligaciones negociables and cuotapartes de fondos " +
            "comunes de inversión are Inversion. investmentClass: only when kind is Inversion, otherwise null. " +
            InvestmentClassRule + "ownershipPct: as printed, or null. marketValue: the value the statement attributes to the person, as " +
            "printed. currency: the ISO code when printed, otherwise null. isActivityAsset: true when the item is assigned " +
            "to the person's business activity. isHousing: true for the casa-habitación.\n" +
            "- incomeStatement: only when the document includes a statement of income and expenses (estado de ingresos y " +
            "egresos, or de resultados): periodFrom, periodTo, ingresos, gastos, resultado and its lines; otherwise null.\n" +
            "- contingencies: guaranteesGiven is the amount the document declares for the avales y/o coobligaciones " +
            "otorgados (guarantees, sureties and co-obligations the person granted to third parties), as printed, usually " +
            "in a note or among the cuentas de orden. It is not a liability of the person: never list it in lines. When the " +
            "document prints no such line, guaranteesGiven is null.",
        Root = new LegajoSchemaObject(
        [
            new("header", new LegajoSchemaObject(
            [
                new("measurementDate", Field(LegajoValueSpec.Date)),
                new("holderCuit",      Field(LegajoValueSpec.Cuit)),
                new("amountsUnit",     Field(LegajoValueSpec.OneOf([.. V.AmountsUnits]))),
                new("engagementType",  Field(LegajoValueSpec.OneOf([.. EngagementTypes.Known]))),
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
            new("lines", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("section",  Value(LegajoValueSpec.OneOf([.. V.Sections]))),
                new("label",    Value(LegajoValueSpec.Text(200))),
                new("amount",   Value(LegajoValueSpec.Money)),
                new("annexRef", Value(LegajoValueSpec.Text(20)))
            ], ItemProvenance: true))),
            new("totals", new LegajoSchemaObject(
            [
                new("activoCorriente",   Field(LegajoValueSpec.Money)),
                new("activoNoCorriente", Field(LegajoValueSpec.Money)),
                new("totalActivo",       Field(LegajoValueSpec.Money)),
                new("pasivoCorriente",   Field(LegajoValueSpec.Money)),
                new("pasivoNoCorriente", Field(LegajoValueSpec.Money)),
                new("totalPasivo",       Field(LegajoValueSpec.Money)),
                new("patrimonioNeto",    Field(LegajoValueSpec.Money))
            ])),
            new("annexItems", new LegajoSchemaArray(new LegajoSchemaObject(
            [
                new("annexRef",        Value(LegajoValueSpec.Text(20))),
                new("kind",            Value(LegajoValueSpec.OneOf([.. V.AnnexKinds]))),
                // mb-1.3: the class of an Inversion item; null for every other kind and when the document gives no hint.
                new("investmentClass", NullableValue(LegajoValueSpec.OneOf([.. V.InvestmentClasses]))),
                new("description",     Value(LegajoValueSpec.Text(200))),
                new("matchKey",        MatchKey()),
                new("ownershipPct",    Value(LegajoValueSpec.Percent)),
                new("marketValue",     Value(LegajoValueSpec.Money)),
                new("currency",        Value(LegajoValueSpec.Text(10))),
                new("isActivityAsset", Value(LegajoValueSpec.Boolean)),
                new("isHousing",       Value(LegajoValueSpec.Boolean))
            ], ItemProvenance: true))),
            new("incomeStatement", new LegajoSchemaObject(
            [
                new("periodFrom", Field(LegajoValueSpec.Date)),
                new("periodTo",   Field(LegajoValueSpec.Date)),
                new("ingresos",   Field(LegajoValueSpec.Money)),
                new("gastos",     Field(LegajoValueSpec.Money)),
                new("resultado",  Field(LegajoValueSpec.Money)),
                new("lines", new LegajoSchemaArray(new LegajoSchemaObject(
                [
                    new("label",  Value(LegajoValueSpec.Text(200))),
                    new("amount", Value(LegajoValueSpec.Money))
                ], ItemProvenance: true)))
            ], Nullable: true)),
            // mb-1.2: the avales y/o coobligaciones otorgados, exposed on the legajo inventory read. Never applied
            // to the evaluation: the analyst still states guaranteesGivenAmount on the analysis.
            new("contingencies", new LegajoSchemaObject(
            [
                new("guaranteesGiven", Field(LegajoValueSpec.Money))
            ], Nullable: true))
        ]),
        RequiredFieldPaths = ["/header/measurementDate", "/header/amountsUnit", "/totals/totalActivo", "/totals/totalPasivo",
                              "/totals/patrimonioNeto"]
    };

    /// <summary>
    /// The document types with an extractor, in the order the worker lists them. The claim and the recovery
    /// filter on it: a type joins the pipeline — and every ReadyForExtraction document of that type with
    /// active consent is picked up at the next startup — the moment it is listed here.
    /// </summary>
    public static IReadOnlyList<string> SupportedTypes { get; } =
    [
        DocumentTypes.BienesPersonales, DocumentTypes.ManifestacionBienes,
        DocumentTypes.Ganancias, DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb, DocumentTypes.CertificacionIngresos,
        DocumentTypes.Monotributo
    ];

    /// <summary>The schema for <paramref name="documentType"/>, or null when it has no extractor yet.</summary>
    public static LegajoExtractionSchema? For(string? documentType) => documentType switch
    {
        DocumentTypes.BienesPersonales      => BienesPersonales,
        DocumentTypes.ManifestacionBienes   => ManifestacionBienes,
        DocumentTypes.Ganancias             => Ganancias,
        DocumentTypes.DdjjIva               => DdjjIva,
        DocumentTypes.DdjjIibb              => DdjjIibb,
        DocumentTypes.CertificacionIngresos => CertificacionIngresos,
        DocumentTypes.Monotributo           => Monotributo,
        _ => null
    };

    /// <summary>The closing text block of the user turn. Carries the page count only.</summary>
    public static string BuildUserText(int pages)
        => $"Extract this document. It has {pages} page{(pages == 1 ? string.Empty : "s")}.";

    private static LegajoSchemaField Field(LegajoValueSpec spec) => new(spec);

    private static LegajoSchemaValue Value(LegajoValueSpec spec) => new(spec);

    /// <summary>An item value that may be null even when it is an enum (<see cref="LegajoSchemaValue.NullableEnum"/>).</summary>
    private static LegajoSchemaValue NullableValue(LegajoValueSpec spec) => new(spec, NullableEnum: true);

    // Nullable: a row with no identifier may come back as null rather than {kind: Ninguno}.
    private static LegajoSchemaObject MatchKey() => new(
    [
        new("kind",  Value(LegajoValueSpec.OneOf([.. V.MatchKeyKinds]))),
        new("value", Value(LegajoValueSpec.Text(60)))
    ], Nullable: true);
}
