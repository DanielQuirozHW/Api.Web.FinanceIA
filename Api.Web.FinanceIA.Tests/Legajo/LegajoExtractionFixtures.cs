// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;

namespace Api.Web.FinanceIA.Tests.Legajo;

// Synthetic extraction tool inputs (Phase 2b and 3a). Every figure, name and identifier is invented. The
// "clean" inputs are internally consistent, so every integrity check passes on them; tests mutate a copy
// to make one check fail.

/// <summary>Tool inputs as the model would send them (camelCase, provenance objects).</summary>
internal static class LegajoExtractionFixtures
{
    public static JsonObject F(JsonNode? value, int? page = 1, string? quote = "texto", decimal confidence = 0.95m) => new()
    {
        ["value"]      = value,
        ["page"]       = value is null ? null : page,
        ["quote"]      = value is null ? null : quote,
        ["confidence"] = confidence
    };

    private static JsonObject MatchKey(string kind, string? value) => new() { ["kind"] = kind, ["value"] = value };

    private static JsonObject BpItem(string rubro, string location, decimal value, decimal? pct = 100m,
                                     string matchKind = "Ninguno", string? matchValue = null, decimal confidence = 0.95m) => new()
    {
        ["rubro"]           = rubro,
        ["location"]        = location,
        ["description"]     = "Bien sintetico " + rubro,
        ["matchKey"]        = MatchKey(matchKind, matchValue),
        ["ownershipPct"]    = pct,
        ["acquisitionDate"] = "2020-03-15",
        ["fiscalValue"]     = value,
        ["currency"]        = location == "Exterior" ? "USD" : "ARS",
        ["isExempt"]        = false,
        ["isHousing"]       = rubro == "Inmuebles",
        ["page"]            = 2,
        ["quote"]           = rubro + " " + value,
        ["confidence"]      = confidence
    };

    private static JsonObject RubroTotal(string rubro, string location, decimal total) => new()
    {
        ["rubro"] = rubro, ["location"] = location, ["total"] = total, ["page"] = 3, ["quote"] = "Total " + rubro, ["confidence"] = 0.95m
    };

    /// <summary>A clean Bienes Personales reading: every check passes.</summary>
    public static JsonObject BienesPersonales(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["fiscalYear"]          = F(2025),
            ["holderCuit"]          = F(holderCuit),
            ["isRectificativa"]     = F(false),
            ["rectificativaNumber"] = F(null),
            ["amountsUnit"]         = F("Pesos")
        },
        ["items"] = new JsonArray
        {
            BpItem("Inmuebles", "Pais", 1_000_000m, pct: 50m, matchKind: "Partida", matchValue: "PART-0001"),
            BpItem("Rodados", "Pais", 250_000m, matchKind: "Dominio", matchValue: "AAA000"),
            BpItem("Depositos", "Pais", 150_000.50m),
            BpItem("Depositos", "Exterior", 300_000m)
        },
        ["rubroTotals"] = new JsonArray
        {
            RubroTotal("Inmuebles", "Pais", 1_000_000m),
            RubroTotal("Rodados", "Pais", 250_000m),
            RubroTotal("Depositos", "Pais", 150_000.50m),
            RubroTotal("Depositos", "Exterior", 300_000m)
        },
        ["totals"] = new JsonObject
        {
            ["bienesPais"]          = F(1_400_000.50m),
            ["bienesExterior"]      = F(300_000m),
            ["totalBienes"]         = F(1_700_000.50m),
            ["bienesExentos"]       = F(0m),
            ["minimoNoImponible"]   = F(null),
            ["baseImponible"]       = F(1_700_000.50m),
            ["impuestoDeterminado"] = F(8_500m)
        }
    };

    private static JsonObject Line(string section, string label, decimal amount, string? annexRef = null) => new()
    {
        ["section"] = section, ["label"] = label, ["amount"] = amount, ["annexRef"] = annexRef,
        ["page"] = 2, ["quote"] = label + " " + amount, ["confidence"] = 0.95m
    };

    private static JsonObject AnnexItem(string annexRef, string kind, decimal value) => new()
    {
        ["annexRef"]        = annexRef,
        ["kind"]            = kind,
        ["description"]     = "Item sintetico " + kind,
        ["matchKey"]        = MatchKey("Ninguno", null),
        ["ownershipPct"]    = 100m,
        ["marketValue"]     = value,
        ["currency"]        = "ARS",
        ["isActivityAsset"] = false,
        ["isHousing"]       = kind == "Inmueble",
        ["page"]            = 4,
        ["quote"]           = kind + " " + value,
        ["confidence"]      = 0.95m
    };

    /// <summary>A clean manifestación de bienes, "en miles": every check passes.</summary>
    public static JsonObject ManifestacionBienes(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["measurementDate"] = F("2025-12-31"),
            ["holderCuit"]      = F(holderCuit),
            ["amountsUnit"]     = F("Miles"),
            ["engagementType"]  = F("Certificacion"),
            ["accountant"]      = new JsonObject { ["matricula"] = F("T 100 F 20"), ["consejo"] = F("Consejo sintetico") },
            ["legalization"]    = new JsonObject { ["consejo"] = F("Consejo sintetico"), ["code"] = F("LEG-0001") }
        },
        ["lines"] = new JsonArray
        {
            Line("ActivoCorriente", "Caja y bancos", 500m),
            Line("ActivoCorriente", "Inversiones", 1_500m, "B"),
            Line("ActivoNoCorriente", "Inmuebles", 8_000m, "A"),
            Line("ActivoNoCorriente", "Rodados", 2_000m, "A"),
            Line("PasivoCorriente", "Deudas comerciales", 700m),
            Line("PasivoNoCorriente", "Prestamo hipotecario", 3_000m, "C")
        },
        ["totals"] = new JsonObject
        {
            ["activoCorriente"]   = F(2_000m),
            ["activoNoCorriente"] = F(10_000m),
            ["totalActivo"]       = F(12_000m),
            ["pasivoCorriente"]   = F(700m),
            ["pasivoNoCorriente"] = F(3_000m),
            ["totalPasivo"]       = F(3_700m),
            ["patrimonioNeto"]    = F(8_300m)
        },
        ["annexItems"] = new JsonArray
        {
            AnnexItem("A", "Inmueble", 8_000m),
            AnnexItem("A", "Rodado", 2_000m),
            AnnexItem("B", "Inversion", 1_500m),
            AnnexItem("C", "Deuda", 3_000m)
        },
        ["incomeStatement"] = new JsonObject
        {
            ["periodFrom"] = F("2025-01-01"),
            ["periodTo"]   = F("2025-12-31"),
            ["ingresos"]   = F(6_000m),
            ["gastos"]     = F(4_500m),
            ["resultado"]  = F(1_500m),
            ["lines"]      = new JsonArray
            {
                new JsonObject { ["label"] = "Honorarios", ["amount"] = 6_000m, ["page"] = 5, ["quote"] = "Honorarios", ["confidence"] = 0.9m },
                new JsonObject { ["label"] = "Gastos personales", ["amount"] = 4_500m, ["page"] = 5, ["quote"] = "Gastos", ["confidence"] = 0.9m }
            }
        }
    };

    // ── Investment classes (mb-1.3 / bp-1.1, engine 0.9) ────────────────────────
    // Synthetic tool outputs as the harness would read them off a manifestación and a Bienes Personales that list
    // investments: a listed share carrying the issuer's CUIT, a bono, an FCI (and, in BP, an unlisted share). Each stays
    // internally consistent, so every integrity check still passes. Issuers and figures are invented.

    /// <summary>The CUIT of the issuer of the synthetic listed share (an invented, valid-check-digit company CUIT).</summary>
    public const string ListedIssuerCuit = "30-71234567-1";

    /// <summary>The CUIT of the issuer of the synthetic UNLISTED share (BP only).</summary>
    public const string UnlistedIssuerCuit = "30-70000001-6";

    private static JsonObject Investment(JsonObject row, string description, JsonObject matchKey, string? investmentClass)
    {
        row["description"]     = description;
        row["matchKey"]        = matchKey;
        row["investmentClass"] = investmentClass;
        return row;
    }

    /// <summary>
    /// <see cref="ManifestacionBienes"/> with annex B (1.500 "en miles") split into a listed share (700, issuer CUIT,
    /// AccionCotizante), a bono (500, TituloPublico) and an FCI (300, CuotaparteFci).
    /// </summary>
    public static JsonObject ManifestacionBienesWithInvestments()
    {
        var mb = ManifestacionBienes();
        var annex = mb["annexItems"]!.AsArray();
        annex.RemoveAt(2);   // the single Inversion of annex B
        annex.Insert(2, Investment(AnnexItem("B", "Inversion", 700m), "Acciones Sintetica SA (BYMA: SINT)",
            MatchKey("Cuit", ListedIssuerCuit), "AccionCotizante"));
        annex.Insert(3, Investment(AnnexItem("B", "Inversion", 500m), "Bono sintetico 2030",
            MatchKey("Ninguno", null), "TituloPublico"));
        annex.Insert(4, Investment(AnnexItem("B", "Inversion", 300m), "FCI sintetico renta fija",
            MatchKey("Ninguno", null), "CuotaparteFci"));
        return mb;
    }

    /// <summary>
    /// <see cref="BienesPersonales"/> plus a TitulosYAcciones rubro (País, $ 1.000.000): the listed share (300.000,
    /// issuer CUIT, AccionCotizante), the bono (200.000, TituloPublico), the FCI (100.000, CuotaparteFci) and an unlisted
    /// share (400.000, its company's CUIT, no class). The rubro total and the totals include it.
    /// </summary>
    public static JsonObject BienesPersonalesWithInvestments()
    {
        var bp = BienesPersonales();
        var items = bp["items"]!.AsArray();
        items.Add(Investment(BpItem("TitulosYAcciones", "Pais", 300_000m), "Acciones Sintetica SA (BYMA: SINT)",
            MatchKey("Cuit", ListedIssuerCuit), "AccionCotizante"));
        items.Add(Investment(BpItem("TitulosYAcciones", "Pais", 200_000m), "Bono sintetico 2030",
            MatchKey("Ninguno", null), "TituloPublico"));
        items.Add(Investment(BpItem("TitulosYAcciones", "Pais", 100_000m), "FCI sintetico renta fija",
            MatchKey("Ninguno", null), "CuotaparteFci"));
        items.Add(Investment(BpItem("TitulosYAcciones", "Pais", 400_000m, pct: 20m), "Acciones Cerrada Sintetica SA",
            MatchKey("Cuit", UnlistedIssuerCuit), null));

        bp["rubroTotals"]!.AsArray().Add(RubroTotal("TitulosYAcciones", "Pais", 1_000_000m));
        var totals = bp["totals"]!.AsObject();
        totals["bienesPais"]    = F(2_400_000.50m);
        totals["totalBienes"]   = F(2_700_000.50m);
        totals["baseImponible"] = F(2_700_000.50m);
        return bp;
    }

    // ── Phase 3a ────────────────────────────────────────────────────────────────
    // Each clean input is internally consistent for the PeriodEnd 2025-12-31: every check of its type runs
    // and passes. Figures are invented.

    private static JsonObject Provenance(JsonObject row, int page, string quote)
    {
        row["page"]       = page;
        row["quote"]      = quote;
        row["confidence"] = 0.95m;
        return row;
    }

    private static JsonObject CategoryResult(string category, string source, bool isCedular, string label, decimal amount)
        => Provenance(new JsonObject
        {
            ["category"] = category, ["source"] = source, ["isCedular"] = isCedular, ["label"] = label, ["amount"] = amount
        }, 2, label + " " + amount);

    private static JsonObject JustificationLine(string side, string label, decimal amount)
        => Provenance(new JsonObject { ["side"] = side, ["label"] = label, ["amount"] = amount }, 5, label + " " + amount);

    /// <summary>
    /// A clean Ganancias DDJJ (Pesos). The justification: orígenes 12.500.000,50 = 12.000.000,50 + 500.000;
    /// aplicaciones 1.700.000 = 1.500.000 + 200.000; variación 6.000.000 = 56.000.000 − 50.000.000; monto
    /// consumido 4.800.000,50 (not checked).
    /// </summary>
    public static JsonObject Ganancias(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["fiscalYear"]          = F(2025),
            ["holderCuit"]          = F(holderCuit),
            ["isRectificativa"]     = F(false),
            ["rectificativaNumber"] = F(null),
            ["amountsUnit"]         = F("Pesos")
        },
        ["categoryResults"] = new JsonArray
        {
            CategoryResult("Primera", "Argentina", false, "Alquileres", 1_200_000m),
            CategoryResult("Tercera", "Argentina", false, "Actividad comercial", 8_500_000.50m),
            CategoryResult("Cuarta", "Argentina", false, "Honorarios", 2_300_000m),
            CategoryResult("Segunda", "Argentina", true, "Dividendos", 150_000m),
            CategoryResult("Primera", "Exterior", false, "Alquiler exterior", -50_000m)
        },
        ["totals"] = new JsonObject
        {
            ["gananciaNeta"]                = F(12_000_000.50m),
            ["deduccionesPersonales"]       = F(4_000_000m),
            ["gananciaNetaSujetaAImpuesto"] = F(8_000_000.50m),
            ["impuestoDeterminado"]         = F(1_500_000m),
            ["retencionesYPercepciones"]    = F(300_000m),
            ["anticipos"]                   = F(600_000m),
            ["saldoAmount"]                 = F(600_000m),
            ["saldoSide"]                   = F("Fisco")
        },
        ["justification"] = new JsonObject
        {
            ["patrimonioInicio"]     = F(50_000_000m),
            ["patrimonioCierre"]     = F(56_000_000m),
            ["variacionPatrimonial"] = F(6_000_000m),
            ["totalOrigenes"]        = F(12_500_000.50m),
            ["totalAplicaciones"]    = F(1_700_000m),
            ["montoConsumido"]       = F(4_800_000.50m),
            ["lines"]                = new JsonArray
            {
                JustificationLine("Origen", "Ganancia neta", 12_000_000.50m),
                JustificationLine("Origen", "Ganancias exentas", 500_000m),
                JustificationLine("Aplicacion", "Impuesto ingresado", 1_500_000m),
                JustificationLine("Aplicacion", "Gastos no deducibles", 200_000m)
            }
        }
    };

    private static JsonObject IvaPeriod(string period, decimal ventas, decimal exentas, decimal debito, decimal credito,
                                        decimal anterior, decimal saldo, string side, decimal aPagar)
        => Provenance(new JsonObject
        {
            ["period"]                  = period,
            ["rectificativaNumber"]     = null,
            ["ventasNetoGravado"]       = ventas,
            ["ventasExentasNoGravadas"] = exentas,
            ["debitoFiscal"]            = debito,
            ["creditoFiscal"]           = credito,
            ["saldoTecnicoAnterior"]    = anterior,
            ["saldoTecnico"]            = new JsonObject { ["amount"] = saldo, ["side"] = side },
            ["saldoAPagar"]             = aPagar
        }, 1, "Periodo " + period);

    private static JsonObject IvaLine(string period, string side, string label, decimal? pct, decimal? neto, decimal impuesto)
        => Provenance(new JsonObject
        {
            ["period"] = period, ["rectificativaNumber"] = null, ["side"] = side, ["label"] = label,
            ["alicuotaPct"] = pct, ["netoGravado"] = neto, ["impuesto"] = impuesto
        }, 2, label + " " + impuesto);

    /// <summary>
    /// A clean DDJJ IVA file with two originals (Pesos). 2025-11: débito 230.000 = 210.000 + 21.000 − 1.000,
    /// saldo técnico a favor del fisco 100.000 = 230.000 − 126.000 − 4.000. 2025-12: débito 168.000, saldo
    /// técnico a favor del contribuyente 42.000 = 168.000 − 210.000 − 0.
    /// </summary>
    public static JsonObject DdjjIva(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["holderCuit"]  = F(holderCuit),
            ["amountsUnit"] = F("Pesos")
        },
        ["periods"] = new JsonArray
        {
            IvaPeriod("2025-11", 1_200_000m, 50_000m, 230_000m, 126_000m, 4_000m, 100_000m, "Fisco", 90_000m),
            IvaPeriod("2025-12", 800_000m, 0m, 168_000m, 210_000m, 0m, 42_000m, "Contribuyente", 0m)
        },
        ["alicuotaLines"] = new JsonArray
        {
            IvaLine("2025-11", "Debito", "Ventas 21", 21m, 1_000_000m, 210_000m),
            IvaLine("2025-11", "Debito", "Ventas 10,5", 10.5m, 200_000m, 21_000m),
            IvaLine("2025-11", "Debito", "Notas de credito emitidas", null, null, -1_000m),
            IvaLine("2025-11", "Credito", "Compras 21", 21m, 600_000m, 126_000m),
            IvaLine("2025-12", "Debito", "Ventas 21", 21m, 800_000m, 168_000m),
            IvaLine("2025-12", "Credito", "Compras 21", 21m, 1_000_000m, 210_000m)
        }
    };

    private static JsonObject IibbPeriod(string? period, decimal ingresos, decimal impuesto)
        => Provenance(new JsonObject
        {
            ["period"] = period, ["rectificativaNumber"] = null, ["ingresosTotales"] = ingresos, ["impuestoTotal"] = impuesto
        }, 1, "Periodo " + period);

    private static JsonObject IibbRow(string? period, string jurisdiction, decimal baseImponible, decimal pct, decimal impuesto)
        => Provenance(new JsonObject
        {
            ["period"]              = period,
            ["rectificativaNumber"] = null,
            ["jurisdiction"]        = jurisdiction,
            ["activity"]            = "620100 Servicios de consultoria informatica",
            ["baseImponible"]       = baseImponible,
            ["alicuotaPct"]         = pct,
            ["impuestoDeterminado"] = impuesto
        }, 2, jurisdiction + " " + baseImponible);

    /// <summary>
    /// A clean Convenio Multilateral file with two monthly CM03 (Pesos): each row's tax is base × alícuota
    /// and each month's impuestoTotal is the sum of its rows.
    /// </summary>
    public static JsonObject DdjjIibb(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["holderCuit"]  = F(holderCuit),
            ["regimen"]     = F("ConvenioMultilateral"),
            ["fiscalYear"]  = F(null),
            ["amountsUnit"] = F("Pesos")
        },
        ["periods"] = new JsonArray
        {
            IibbPeriod("2025-11", 1_500_000m, 60_000m),
            IibbPeriod("2025-12", 1_800_000m, 72_000m)
        },
        ["rows"] = new JsonArray
        {
            IibbRow("2025-11", "Buenos Aires", 1_000_000m, 3.5m, 35_000m),
            IibbRow("2025-11", "CABA", 500_000m, 5m, 25_000m),
            IibbRow("2025-12", "Buenos Aires", 1_200_000m, 3.5m, 42_000m),
            IibbRow("2025-12", "CABA", 600_000m, 5m, 30_000m)
        }
    };

    private static JsonObject CiRow(string? period, string concept, decimal amount)
        => Provenance(new JsonObject { ["period"] = period, ["concept"] = concept, ["amount"] = amount }, 1, concept + " " + amount);

    /// <summary>A clean certificación de ingresos, "en miles": three monthly amounts adding up to the total.</summary>
    public static JsonObject CertificacionIngresos(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["holderCuit"]     = F(holderCuit),
            ["periodFrom"]     = F("2025-10-01"),
            ["periodTo"]       = F("2025-12-31"),
            ["amountsUnit"]    = F("Miles"),
            ["engagementType"] = F("Certificacion"),
            ["accountant"]     = new JsonObject { ["matricula"] = F("T 200 F 40"), ["consejo"] = F("Consejo sintetico") },
            ["legalization"]   = new JsonObject { ["consejo"] = F("Consejo sintetico"), ["code"] = F("LEG-0002") }
        },
        ["rows"] = new JsonArray
        {
            CiRow("2025-10", "Honorarios", 1_000m),
            CiRow("2025-11", "Honorarios", 1_200m),
            CiRow("2025-12", "Honorarios", 1_300.50m)
        },
        ["totals"] = new JsonObject
        {
            ["totalIngresos"] = F(3_500.50m)
        }
    };

    private static JsonObject MonoPayment(string period, decimal amount, string? paymentDate)
        => Provenance(new JsonObject { ["period"] = period, ["amount"] = amount, ["paymentDate"] = paymentDate }, 2, "Pago " + period);

    /// <summary>A clean monotributo constancia with three monthly payments, the latest for 2025-12.</summary>
    public static JsonObject Monotributo(string? holderCuit = LegajoTestData.LegajoCuitDigits) => new()
    {
        ["header"] = new JsonObject
        {
            ["holderCuit"]   = F(holderCuit),
            ["categoria"]    = F("D"),
            ["activityType"] = F("Servicios"),
            ["adhesionDate"] = F("2021-05-10"),
            ["cuotaMensual"] = F(42_000.50m),
            ["amountsUnit"]  = F("Pesos")
        },
        ["payments"] = new JsonArray
        {
            MonoPayment("2025-10", 42_000.50m, "2025-10-20"),
            MonoPayment("2025-11", 42_000.50m, "2025-11-20"),
            MonoPayment("2025-12", 42_000.50m, "2025-12-19")
        }
    };

    /// <summary>The clean input of <paramref name="documentType"/>, for every type with a schema.</summary>
    public static JsonObject Clean(string documentType, string? holderCuit = LegajoTestData.LegajoCuitDigits) => documentType switch
    {
        DocumentTypes.BienesPersonales      => BienesPersonales(holderCuit),
        DocumentTypes.ManifestacionBienes   => ManifestacionBienes(holderCuit),
        DocumentTypes.Ganancias             => Ganancias(holderCuit),
        DocumentTypes.DdjjIva               => DdjjIva(holderCuit),
        DocumentTypes.DdjjIibb              => DdjjIibb(holderCuit),
        DocumentTypes.CertificacionIngresos => CertificacionIngresos(holderCuit),
        DocumentTypes.Monotributo           => Monotributo(holderCuit),
        _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "No fixture.")
    };

    /// <summary>The schema of every type with an extractor, by type (Phase 2b and 3a).</summary>
    public static IReadOnlyDictionary<string, LegajoExtractionSchema> Schemas { get; } = new Dictionary<string, LegajoExtractionSchema>
    {
        [DocumentTypes.BienesPersonales]      = LegajoExtractionPrompts.BienesPersonales,
        [DocumentTypes.ManifestacionBienes]   = LegajoExtractionPrompts.ManifestacionBienes,
        [DocumentTypes.Ganancias]             = LegajoExtractionPrompts.Ganancias,
        [DocumentTypes.DdjjIva]               = LegajoExtractionPrompts.DdjjIva,
        [DocumentTypes.DdjjIibb]              = LegajoExtractionPrompts.DdjjIibb,
        [DocumentTypes.CertificacionIngresos] = LegajoExtractionPrompts.CertificacionIngresos,
        [DocumentTypes.Monotributo]           = LegajoExtractionPrompts.Monotributo
    };

    /// <summary>The normalized clean reading of <paramref name="documentType"/>, optionally mutated first.</summary>
    public static JsonObject NormalizedClean(string documentType, Action<JsonObject>? mutate = null)
    {
        var input = Clean(documentType);
        mutate?.Invoke(input);
        return Normalize(Schemas[documentType], input);
    }

    /// <summary>A Messages envelope carrying the clean input of <paramref name="documentType"/> (or <paramref name="input"/>).</summary>
    public static string EnvelopeFor(string documentType, JsonObject? input = null, string stopReason = "tool_use")
        => Envelope(Schemas[documentType].ToolName, input ?? Clean(documentType), stopReason);

    /// <summary>The normalized reading of <paramref name="input"/> (what ResultJson stores).</summary>
    public static JsonObject Normalize(LegajoExtractionSchema schema, JsonObject input)
    {
        using var document = JsonDocument.Parse(input.ToJsonString());
        var reading = LegajoToolInputReader.Read(schema.Root, document.RootElement);
        if (!reading.IsValid)
            throw new InvalidOperationException("Fixture is invalid: " + string.Join("; ", reading.Violations));
        return reading.Result;
    }

    /// <summary>A Messages envelope whose tool_use block carries <paramref name="input"/>.</summary>
    public static string Envelope(
        string toolName, JsonNode input, string stopReason = "tool_use", bool leadingThinking = false,
        int inputTokens = 20000, int outputTokens = 4000)
    {
        var content = new JsonArray();
        if (leadingThinking)
        {
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig" });
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "Registro la lectura." });
        }

        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_x", ["name"] = toolName, ["input"] = input.DeepClone() });

        return new JsonObject
        {
            ["id"]          = "msg_x",
            ["type"]        = "message",
            ["role"]        = "assistant",
            ["model"]       = "claude-sonnet-4-6",
            ["stop_reason"] = stopReason,
            ["content"]     = content,
            ["usage"]       = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens }
        }.ToJsonString();
    }

    public static string BpEnvelope(JsonObject? input = null, bool leadingThinking = false)
        => Envelope(LegajoExtractionPrompts.BienesPersonales.ToolName, input ?? BienesPersonales(), leadingThinking: leadingThinking);

    public static string MbEnvelope(JsonObject? input = null)
        => Envelope(LegajoExtractionPrompts.ManifestacionBienes.ToolName, input ?? ManifestacionBienes());
}
