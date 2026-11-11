// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>The indicators of the applicant, from the #253 read model (the flujo leads).</summary>
public static partial class PersonEvaluationService
{
    private const string UnitPesos  = "ARS";
    private const string UnitRatio  = "ratio";

    /// <summary>Sales of the last 12 months, nominal (the value) and in pesos of the CPI reference month.</summary>
    internal static PersonIndicatorResult VentasNivel(IncomeFacts facts)
    {
        const string id = "ventas_nivel";

        if (facts.SalesLevel is not { } sales)
        {
            return NoData(id, UnitPesos,
                "El legajo no tiene ventas suficientes para armar los últimos 12 meses (hacen falta al menos " +
                $"{IncomeFacts.MinMonthsPerWindow} meses o una DDJJ anual).",
                facts.Campaign is { } none ? CampaignInputs(new Dictionary<string, string?>(), none) : null);
        }

        var inputs = SalesInputs(sales, facts);
        var real   = sales.Real is { } r ? $" ({Pesos(r)} en pesos de {facts.CpiReferenceMonth})" : " (sin serie de IPC: solo nominal)";
        var window = string.Empty;
        if (facts.Campaign is { } campaign)
        {
            CampaignInputs(inputs, campaign);
            window = $" ({campaign.Text})";
        }

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = sales.Nominal,
            Unit              = UnitPesos,
            EvidenceKind      = PersonEvidenceKinds.Combine(sales.Sources.Select(PersonEvidenceKinds.OfIncomeSource)),
            Evidence          = $"Ventas de 12 meses{window}: {Pesos(sales.Nominal)}{real}" +
                                (sales.Annualized ? $", anualizadas sobre {sales.MonthsAvailable} meses." : "."),
            Inputs            = inputs,
            SourceDocumentIds = sales.Documents
        };
    }

    /// <summary>Real change of the last 12 months against the 12 before; needs the CPI series.</summary>
    internal static PersonIndicatorResult VentasTendencia(IncomeFacts facts)
    {
        const string id = "ventas_tendencia";

        if (facts.SalesLevel is not { } level || facts.PreviousSales is not { } previous)
        {
            if (facts.Campaign is { Aligned: true } aligned)
            {
                return NoData(id, UnitRatio,
                    "Hacen falta dos campañas completas de ventas mensuales para medir la tendencia de una actividad agropecuaria.",
                    CampaignInputs(new Dictionary<string, string?>(), aligned));
            }

            return NoData(id, UnitRatio,
                "Hacen falta dos períodos de 12 meses de ventas para medir la tendencia.",
                facts.Campaign is { } none ? CampaignInputs(new Dictionary<string, string?>(), none) : null);
        }

        var inputs = new Dictionary<string, string?>
        {
            ["ventasUltimos12Nominal"] = F(level.Nominal),
            ["ventasPrevios12Nominal"] = F(previous.Nominal),
            ["variacionNominal"]       = previous.Nominal > 0 ? F(Math.Round(level.Nominal / previous.Nominal - 1m, 4)) : null,
            ["mesReferenciaIpc"]       = facts.CpiReferenceMonth,
            ["desde"]                  = F(previous.From),
            ["hasta"]                  = F(level.To)
        };
        if (facts.Campaign is { } campaign)
            CampaignInputs(inputs, campaign);
        var documents = level.Documents.Concat(previous.Documents).Distinct().ToList();

        if (level.Real is not { } realLevel || previous.Real is not { } realPrevious || realPrevious <= 0)
        {
            return NoData(id, UnitRatio,
                "Sin serie de IPC que cubra los dos períodos: la tendencia real no se puede medir (la variación nominal va en los datos).",
                inputs, documents);
        }

        var change = Math.Round(realLevel / realPrevious - 1m, 4);
        inputs["ventasUltimos12Real"] = F(realLevel);
        inputs["ventasPrevios12Real"] = F(realPrevious);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = change,
            Unit              = UnitRatio,
            EvidenceKind      = PersonEvidenceKinds.Combine(level.Sources.Concat(previous.Sources).Select(PersonEvidenceKinds.OfIncomeSource)),
            Evidence          = $"Variación real de las ventas de 12 meses: {Pct(change)} (pesos de {facts.CpiReferenceMonth}).",
            Inputs            = inputs,
            SourceDocumentIds = documents
        };
    }

    /// <summary>
    /// The consumo the flujo subtracts, monthly pesos, and how it was reached.
    /// <list type="bullet">
    /// <item>Monotributo: max(gastos personales del cuadro de resultados, the floor). The #253 read model does not
    /// separate gastos personales, so the floor (<see cref="ResolveConsumoFloor"/>) is what is used.</item>
    /// <item>Everyone else: the F.711 monto consumido / 12, raised to the floor when below it.</item>
    /// </list>
    /// Null when there is nothing to use: a monotributista with the floor undefined, or no F.711.
    /// </summary>
    internal static ConsumoResolution ResolveConsumo(PersonEvaluationInput input, IncomeFacts facts)
    {
        var piso  = ResolveConsumoFloor(input, facts);
        var floor = piso.Monthly;

        if (input.FiscalRegime == FiscalRegimes.Monotributo)
        {
            return floor > 0m
                ? new ConsumoResolution(floor, null, FloorApplied: true, PersonEvidenceKinds.Declarado,
                    $"piso de consumo de referencia, {ConsumoFloorTexto(piso)} (el cuadro de resultados no separa gastos personales)",
                    piso)
                : new ConsumoResolution(null, null, FloorApplied: false, PersonEvidenceKinds.Declarado,
                    "consumo sin definir: sin gastos personales en el legajo y sin piso de consumo del tenant", piso);
        }

        if (facts.Ganancias?.MontoConsumido is not { } montoConsumido)
            return new ConsumoResolution(null, null, FloorApplied: false, PersonEvidenceKinds.Verificado, "sin monto consumido (F.711)", piso);

        var declared = Math.Round(montoConsumido / 12m, 2);
        return floor > 0m && declared < floor
            ? new ConsumoResolution(floor, declared, FloorApplied: true, PersonEvidenceKinds.Declarado,
                $"piso de consumo de referencia, {ConsumoFloorTexto(piso)} (el F.711 declara menos)", piso)
            : new ConsumoResolution(declared, declared, FloorApplied: false, PersonEvidenceKinds.Verificado,
                "monto consumido del F.711 / 12", piso, DeclaredAnnual: montoConsumido);
    }

    /// <summary>
    /// The consumo floor of this evaluation (engine 0.6): pf_consumo_mensual_minimo_miles, in pesos of
    /// pf_consumo_mes_base, re-expressed by the CPI series the ventas use to its reference month
    /// (<see cref="IncomeFacts.CpiReferenceMonth"/>). It stays nominal, and says why, without a series, when the
    /// series does not reach the base month, or when the base month is after the series' last month.
    /// </summary>
    internal static ConsumoFloor ResolveConsumoFloor(PersonEvaluationInput input, IncomeFacts facts)
    {
        var floor     = input.Parameters.ConsumoMensualMinimo;
        var yearMonth = input.Parameters.ResolvedConsumoMesBase;
        var baseMonth = new DateOnly(yearMonth / 100, Math.Clamp(yearMonth % 100, 1, 12), 1);
        var reference = facts.CpiReferenceMonth;

        if (floor <= 0m)
            return new ConsumoFloor(floor, baseMonth, reference, null, floor, null);

        if (facts.CpiReferenceDate is not { } referenceDate)
            return new ConsumoFloor(floor, baseMonth, null, null, floor, "sin serie de IPC: piso nominal");

        if (baseMonth > referenceDate)
        {
            return new ConsumoFloor(floor, baseMonth, reference, null, floor,
                $"mes base posterior al último IPC de la serie ({reference}): piso nominal");
        }

        if (facts.ToCpiReference(baseMonth, 1m) is not { } level)
        {
            return new ConsumoFloor(floor, baseMonth, reference, null, floor,
                $"sin serie de IPC para {YearMonthLabel(baseMonth)}: piso nominal");
        }

        var factor = Math.Round(level, 4);
        return new ConsumoFloor(floor, baseMonth, reference, factor, Math.Round(floor * factor, 2), null);
    }

    /// <summary>How the floor was reached, for the evidence: "$ 2.507.330 ($ 2.450.000 de 2026-09 × 1,0234 por IPC a 2026-10)".</summary>
    private static string ConsumoFloorTexto(ConsumoFloor floor)
        => floor.Factor is { } factor
            ? $"{Pesos(floor.Monthly)} ({Pesos(floor.Base)} de {YearMonthLabel(floor.BaseMonth)} × " +
              $"{factor.ToString("0.####", CultureInfo.GetCultureInfo("es-AR"))} por IPC a {floor.ReferenceMonth})"
            : $"{Pesos(floor.Monthly)} ({Pesos(floor.Base)} de {YearMonthLabel(floor.BaseMonth)}, {floor.NominalNote})";

    /// <summary>The floor's inputs, shared by the flujo and the consumo_declarado filter.</summary>
    private static void AddConsumoFloorInputs(Dictionary<string, string?> inputs, ConsumoFloor floor)
    {
        inputs["consumoMensualPiso"] = F(floor.Monthly);
        inputs["consumoPisoBase"]    = F(floor.Base);
        inputs["consumoMesBase"]     = YearMonthLabel(floor.BaseMonth);
        inputs["mesReferenciaIpc"]   = floor.ReferenceMonth;
        inputs["factorIpcConsumo"]   = F(floor.Factor);
        inputs["pisoIndexado"]       = floor.Indexed ? "true" : "false";
    }

    private static string YearMonthLabel(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Flujo de la actividad minus consumo, annual pesos. Responsable inscripto: Ganancias tercera categoría
    /// less 12 × the consumo of <see cref="ResolveConsumo"/>. Monotributo: the manifestación's resultado (its
    /// cuadro de resultados) less 12 × the consumo — gross income less consumo is never a flujo.
    /// Amortizaciones e intereses are not read from the legajo yet and are recorded as such.
    /// </summary>
    internal static PersonIndicatorResult FlujoActividad(PersonEvaluationInput input, IncomeFacts facts)
    {
        const string id = "flujo_actividad";

        if (input.FiscalRegime == FiscalRegimes.Monotributo)
            return FlujoMonotributo(input, facts);

        if (facts.Ganancias is not { Renta3ra: { } renta3ra } ganancias)
        {
            return NoData(id, UnitPesos,
                "Sin renta de tercera categoría en una DDJJ de Ganancias: el resultado de la actividad no se puede calcular.",
                evidenceKind: PersonEvidenceKinds.Verificado);
        }

        var consumo = ResolveConsumo(input, facts);
        var inputs = new Dictionary<string, string?>
        {
            ["periodo"]               = ganancias.Period,
            ["renta3ra"]              = F(renta3ra),
            ["montoConsumido"]        = F(ganancias.MontoConsumido),
            ["consumoMensual"]        = F(consumo.Monthly),
            ["consumoBase"]           = consumo.Basis,
            ["pisoAplicado"]          = consumo.FloorApplied ? "true" : "false",
            ["amortizaciones"]        = "no disponible en el legajo",
            ["intereses"]             = "no disponible en el legajo",
            ["fuente"]                = LegajoIncomeSources.Ganancias
        };
        AddConsumoFloorInputs(inputs, consumo.Floor);

        if (consumo.Annual is not { } consumoAnual)
        {
            return NoData(id, UnitPesos,
                $"Renta de tercera categoría {ganancias.Period}: {Pesos(renta3ra)}, pero la DDJJ no trae el monto consumido (F.711).",
                inputs, ganancias.Documents, PersonEvidenceKinds.Verificado);
        }

        var flujo = renta3ra - consumoAnual;

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = flujo,
            Unit              = UnitPesos,
            EvidenceKind      = PersonEvidenceKinds.Combine([PersonEvidenceKinds.Verificado, consumo.EvidenceKind]),
            Evidence          = $"Ganancias {ganancias.Period}: renta de tercera categoría {Pesos(renta3ra)} menos consumo " +
                                $"{Pesos(consumoAnual)} ({consumo.Basis}) = {Pesos(flujo)}. Sin amortizaciones ni intereses (no se leen del legajo).",
            Inputs            = inputs,
            SourceDocumentIds = ganancias.Documents
        };
    }

    private static PersonIndicatorResult FlujoMonotributo(PersonEvaluationInput input, IncomeFacts facts)
    {
        const string id = "flujo_actividad";
        var consumo = ResolveConsumo(input, facts);

        var inputs = new Dictionary<string, string?>
        {
            ["fuente"]             = LegajoIncomeSources.Manifestacion,
            ["consumoMensual"]     = F(consumo.Monthly),
            ["consumoBase"]        = consumo.Basis,
            ["gastosPersonales"]   = "no disponible en el legajo",
            ["verificado"]         = facts.HasInvoicedSales ? "true" : "false"
        };
        AddConsumoFloorInputs(inputs, consumo.Floor);

        if (facts.ManifestacionResultado is not { } resultado)
        {
            return NoData(id, UnitPesos,
                "Monotributista sin cuadro de resultados: el flujo no se puede estimar.", inputs);
        }

        inputs["resultadoActividad"] = F(resultado.Amount);
        inputs["periodo"]            = resultado.Period;
        inputs["anualizado"]         = resultado.Annualized ? "true" : "false";

        if (consumo.Monthly is not { } consumoMensual)
        {
            return NoData(id, UnitPesos,
                "Monotributista: el cuadro de resultados no separa gastos personales y el piso de consumo " +
                "(pf_consumo_mensual_minimo_miles) no está definido para el tenant.",
                inputs, resultado.Documents);
        }

        var consumoAnual = consumoMensual * 12m;
        var flujo = resultado.Amount - consumoAnual;
        inputs["consumoAnual"] = F(consumoAnual);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = flujo,
            Unit              = UnitPesos,
            EvidenceKind      = PersonEvidenceKinds.Declarado,
            Evidence          = $"Monotributista: resultado de la actividad según la manifestación ({resultado.Period}) " +
                                $"{Pesos(resultado.Amount)} menos consumo {Pesos(consumoAnual)} ({consumo.Basis}) = {Pesos(flujo)}." +
                                (facts.HasInvoicedSales ? string.Empty : " Flujo solo declarado: sin facturación en el legajo."),
            Inputs            = inputs,
            SourceDocumentIds = resultado.Documents
        };
    }

    /// <summary>
    /// The cuotas the person already pays a month, estimated: pf_cuota_estimada_pct_saldo of the Central de
    /// Deudores total balance (engine 0.6; before, the balance over a fixed term). Zero when the BCRA reports no
    /// debt; null when it is missing or failed.
    /// </summary>
    internal static decimal? CuotasVigentesEstimadas(PersonEvaluationInput input)
    {
        var deuda = input.Bcra?.Deuda;
        return deuda?.Estado switch
        {
            BcraDeudaConsultaEstado.NoDebtReported => 0m,
            BcraDeudaConsultaEstado.DebtFound when deuda.MontoTotal is { } miles
                => Math.Round(miles * 1000m * input.Parameters.ResolvedCuotaEstimadaPctSaldo, 2),   // thousands of pesos
            _ => null
        };
    }

    /// <summary>
    /// The cuotas vigentes every debt-service ratio reads (engine 0.8): the analyst's declared figure whenever it
    /// is stated — zero included, and with or without a BCRA snapshot — else <see cref="CuotasVigentesEstimadas"/>.
    /// Null only when nothing is declared and the estimate is unavailable.
    /// </summary>
    internal static CuotasVigentes? CuotasVigentesDe(PersonEvaluationInput input)
    {
        var estimate = CuotasVigentesEstimadas(input);

        if (input.CuotasVigentesDeclaradas is { } declared)
            return new CuotasVigentes(declared, estimate, Declared: true);

        return estimate is { } estimated ? new CuotasVigentes(estimated, estimated, Declared: false) : null;
    }

    /// <summary>
    /// Whether the estimate is one (DebtFound: pf_cuota_estimada_pct_saldo of the balance) or a fact
    /// (NoDebtReported: the BCRA says there is no debt, so zero).
    /// </summary>
    private static bool CuotasVigentesSonEstimacion(PersonEvaluationInput input)
        => input.Bcra?.Deuda?.Estado != BcraDeudaConsultaEstado.NoDebtReported;

    /// <summary>The cuotas vigentes clause of the cuota/ingreso and cobertura evidence.</summary>
    private static string CuotasVigentesTexto(PersonEvaluationInput input, CuotasVigentes vigentes)
    {
        if (vigentes.Declared)
        {
            var estimacion = vigentes.Estimate is { } estimate
                ? $"era {Pesos(estimate)}"
                : "no estaba disponible";
            return $"cuotas vigentes declaradas por el analista {Pesos(vigentes.Amount)} (la estimación por la Central " +
                   $"de Deudores {estimacion})";
        }

        return CuotasVigentesSonEstimacion(input)
            ? $"cuotas vigentes estimadas {Pesos(vigentes.Amount)} ({Pct(input.Parameters.ResolvedCuotaEstimadaPctSaldo)} del " +
              "saldo de la Central de Deudores)"
            : "cuotas vigentes $ 0 (no registra deudas en la Central de Deudores)";
    }

    /// <summary>
    /// The cuotas vigentes inputs of a debt-service ratio: where the figure came from, the declared one when
    /// stated, the estimate (kept beside a declaration for comparison) and how it was estimated.
    /// </summary>
    private static void CuotasVigentesInputs(
        Dictionary<string, string?> inputs, PersonEvaluationInput input, CuotasVigentes vigentes)
    {
        inputs["cuotasVigentesOrigen"] = vigentes.Declared ? CuotasVigentesOrigenes.Declarada : CuotasVigentesOrigenes.Estimada;
        if (vigentes.Declared)
            inputs["cuotasVigentesDeclaradas"] = F(vigentes.Amount);
        if (vigentes.Estimate is { } estimate)
            inputs["cuotasVigentesEstimadas"] = F(estimate);
        inputs["cuotaEstimadaPctSaldo"]      = F(input.Parameters.ResolvedCuotaEstimadaPctSaldo);
        inputs["cuotasVigentesEsEstimacion"] = !vigentes.Declared && CuotasVigentesSonEstimacion(input) ? "true" : "false";
    }

    /// <summary>The evidence kind of the cuotas vigentes: declared by the analyst, or read off the BCRA.</summary>
    private static string CuotasVigentesEvidenceKind(CuotasVigentes vigentes)
        => vigentes.Declared ? PersonEvidenceKinds.Declarado : PersonEvidenceKinds.Verificado;

    /// <summary>
    /// The annual income a cuota is compared with: Ganancias (3ª + 4ª) when there is a return, else the
    /// certified income, else gross sales — each labelled, because only the first is a net figure. Null when the
    /// legajo has none of the three. Shared by the solicitante's cuota/ingreso and the fiador's stress one.
    /// </summary>
    internal static (decimal Annual, string Basis, string BasisKind, IReadOnlyList<Guid> Documents)? AnnualIncomeBase(
        IncomeFacts facts)
    {
        if (facts.Ganancias is { } g && (g.Renta3ra is not null || g.Renta4ta is not null))
        {
            return ((g.Renta3ra ?? 0m) + (g.Renta4ta ?? 0m),
                    $"Ganancias {g.Period} (rentas de 3ª y 4ª categoría)",
                    PersonEvidenceKinds.Verificado,
                    g.Documents);
        }

        if (facts.Certificacion is { } cert)
        {
            return (cert.Amount,
                    $"certificación de ingresos {cert.Period} (ingreso bruto declarado)",
                    PersonEvidenceKinds.Declarado,
                    cert.Documents);
        }

        if (facts.SalesLevel is { } sales)
        {
            return (sales.Nominal,
                    "ventas de 12 meses (ingreso bruto declarado)",
                    PersonEvidenceKinds.Combine(sales.Sources.Select(PersonEvidenceKinds.OfIncomeSource)),
                    sales.Documents);
        }

        return null;
    }

    /// <summary>
    /// (Cuota + cuotas vigentes) / monthly income, banded by the three pf_cuota_ingreso cuts. The cuotas vigentes are
    /// the analyst's declared figure when stated, else the estimate (<see cref="CuotasVigentesDe"/>).
    /// </summary>
    internal static PersonIndicatorResult CuotaIngreso(PersonEvaluationInput input, IncomeFacts facts)
    {
        const string id = "cuota_ingreso";
        var p = input.Parameters;

        var cuts = new Dictionary<string, string?>
        {
            ["corteBajo"]  = F(p.CuotaIngresoBajo),
            ["corteMedio"] = F(p.CuotaIngresoMedio),
            ["corteAlto"]  = F(p.CuotaIngresoAlto)
        };

        var annualized = input.Agro is { Annualized: true } agro ? agro : null;
        if (annualized is not null)
        {
            AgroInputs(cuts, annualized);
            if (facts.Campaign is { } campaign)
                CampaignInputs(cuts, campaign);
        }

        if (input.InstallmentAmount is not { } cuota)
            return NoData(id, UnitRatio, "No se informó la cuota de la operación.", cuts);

        cuts["cuota"] = F(cuota);

        if (AnnualIncomeBase(facts) is not { } incomeBase)
            return NoData(id, UnitRatio, "El legajo no tiene ingresos para comparar con la cuota.", cuts);

        var (annual, basis, basisKind, documents) = incomeBase;

        cuts["baseIngreso"] = basis;

        if (!string.Equals(input.Currency, "ARS", StringComparison.Ordinal))
        {
            return NoData(id, UnitRatio,
                $"La cuota está en {input.Currency} y los ingresos del legajo en pesos: no se convierte moneda.",
                cuts, documents, basisKind);
        }

        if (CuotasVigentesDe(input) is not { } vigentes)
        {
            return NoData(id, UnitRatio,
                BcraDeudoresNoDisponible(input) + " Las cuotas vigentes no se pueden estimar.",
                cuts, documents, basisKind);
        }

        CuotasVigentesInputs(cuts, input, vigentes);

        if (annualized is not null)
            return CuotaIngresoAnual(input, annualized, cuts, cuota, vigentes, incomeBase);

        var monthly = annual / 12m;
        cuts["ingresoMensual"] = F(Math.Round(monthly, 2));

        if (monthly <= 0m)
            return NoData(id, UnitRatio, $"El ingreso mensual según {basis} no es positivo.", cuts, documents, basisKind);

        var numerator = cuota + vigentes.Amount;
        var ratio = Math.Round(numerator / monthly, 4);
        var band = CuotaIngresoBand(ratio, p);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = ratio,
            Unit              = UnitRatio,
            Band              = band,
            EvidenceKind      = PersonEvidenceKinds.Combine([basisKind, CuotasVigentesEvidenceKind(vigentes)]),
            Evidence          = $"Cuota {Pesos(cuota)} más {CuotasVigentesTexto(input, vigentes)} sobre ingreso mensual " +
                                $"{Pesos(monthly)} según {basis}: {Pct(ratio)}.",
            Inputs            = cuts,
            SourceDocumentIds = documents
        };
    }

    /// <summary>
    /// cuota_ingreso on an ANNUAL base (engine 0.7: agro annual mode, or a cuota that is not monthly): (cuota ×
    /// cuotas a year + cuotas vigentes × 12) / annual income, banded by the same three cuts. The cuotas vigentes stay
    /// monthly — declared or estimated, they are a monthly figure.
    /// </summary>
    private static PersonIndicatorResult CuotaIngresoAnual(
        PersonEvaluationInput input,
        PersonAgroMode agro,
        Dictionary<string, string?> cuts,
        decimal cuota,
        CuotasVigentes vigentes,
        (decimal Annual, string Basis, string BasisKind, IReadOnlyList<Guid> Documents) incomeBase)
    {
        const string id = "cuota_ingreso";
        var (annual, basis, basisKind, documents) = incomeBase;

        cuts["ingresoAnual"] = F(Math.Round(annual, 2));

        if (annual <= 0m)
            return NoData(id, UnitRatio, $"El ingreso anual según {basis} no es positivo.", cuts, documents, basisKind);

        var servicioAnual = ServicioAnual(cuota, vigentes, agro);
        cuts["servicioAnual"] = F(servicioAnual);

        var ratio = Math.Round(servicioAnual / annual, 4);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = ratio,
            Unit              = UnitRatio,
            Band              = CuotaIngresoBand(ratio, input.Parameters),
            EvidenceKind      = PersonEvidenceKinds.Combine([basisKind, CuotasVigentesEvidenceKind(vigentes)]),
            Evidence          = $"Servicio de deuda anual {Pesos(servicioAnual)} (cuota {Pesos(cuota)} × {Periodicidad(agro.CuotasPorAnio)} " +
                                $"más {CuotasVigentesTexto(input, vigentes)} × 12) sobre ingreso anual {Pesos(annual)} según " +
                                $"{basis}: {Pct(ratio)}.{AgroEvidence(agro)}",
            Inputs            = cuts,
            SourceDocumentIds = documents
        };
    }

    private static string CuotaIngresoBand(decimal ratio, PersonEvaluationParameters p)
        => ratio <= p.CuotaIngresoBajo ? PersonIndicatorBands.Verde
         : ratio <= p.CuotaIngresoMedio ? PersonIndicatorBands.Amarillo
         : ratio <= p.CuotaIngresoAlto ? PersonIndicatorBands.Naranja
         : PersonIndicatorBands.Rojo;

    /// <summary>The debt service of a year: the cuota times its cuotas a year, plus 12 monthly cuotas vigentes.</summary>
    private static decimal ServicioAnual(decimal cuota, CuotasVigentes vigentes, PersonAgroMode agro)
        => vigentes.Amount * 12m + cuota * agro.CuotasPorAnio;

    /// <summary>"2 cuotas por año", "1 pago por año".</summary>
    private static string Periodicidad(int cuotasPorAnio)
        => cuotasPorAnio == 1 ? "1 pago por año" : $"{F(cuotasPorAnio)} cuotas por año";

    /// <summary>The agro mode's evidence sentence, appended to an annualised indicator; empty when it has none.</summary>
    private static string AgroEvidence(PersonAgroMode agro)
        => agro.Evidence is { } evidence ? $" {evidence}." : string.Empty;

    /// <summary>The inputs every annualised indicator carries: which time base it ran on and why.</summary>
    private static Dictionary<string, string?> AgroInputs(Dictionary<string, string?> inputs, PersonAgroMode agro)
    {
        inputs["modoAgro"]          = agro.Active ? "true" : "false";
        inputs["origenModoAgro"]    = agro.Origin;
        inputs["claePrincipal"]     = agro.Clae;
        inputs["cuotasPorAnio"]     = F(agro.CuotasPorAnio);
        inputs["mesInicioCampania"] = F(agro.MesInicioCampania);
        return inputs;
    }

    /// <summary>The sales window of agro mode: the campaign it was aligned to, or why it was not.</summary>
    private static Dictionary<string, string?> CampaignInputs(Dictionary<string, string?> inputs, CampaignWindow campaign)
    {
        inputs["ventanaCampania"]  = campaign.Text;
        inputs["campaniaAlineada"] = campaign.Aligned ? "true" : "false";
        return inputs;
    }

    /// <summary>
    /// Debt-service coverage, monthly: (flujo / 12 − impuestos) / (cuotas vigentes + cuota). The flujo
    /// is already net of consumo; impuestos are not in the legajo yet and are recorded as such. Banded by
    /// pf_dscr_rojo / pf_dscr_verde.
    /// </summary>
    internal static PersonIndicatorResult CoberturaServicioDeuda(PersonEvaluationInput input, PersonIndicatorResult flujo)
    {
        const string id = "cobertura_servicio_deuda";
        var p = input.Parameters;

        var inputs = new Dictionary<string, string?>
        {
            ["dscrRojo"]  = F(p.DscrRojo),
            ["dscrVerde"] = F(p.DscrVerde),
            ["impuestos"] = "no disponible en el legajo"
        };

        var annualized = input.Agro is { Annualized: true } agro ? agro : null;
        if (annualized is not null)
            AgroInputs(inputs, annualized);

        if (input.InstallmentAmount is not { } cuota)
            return NoData(id, UnitRatio, "No se informó la cuota de la operación.", inputs);

        inputs["cuota"] = F(cuota);

        if (flujo.Status != PersonIndicatorStatuses.Ok || flujo.Value is not { } flujoAnual)
        {
            return NoData(id, UnitRatio, "Sin flujo de la actividad: la cobertura del servicio de deuda no se puede calcular.",
                inputs, flujo.SourceDocumentIds, flujo.EvidenceKind);
        }

        if (!string.Equals(input.Currency, "ARS", StringComparison.Ordinal))
        {
            return NoData(id, UnitRatio,
                $"La cuota está en {input.Currency} y el flujo en pesos: no se convierte moneda.",
                inputs, flujo.SourceDocumentIds, flujo.EvidenceKind);
        }

        if (CuotasVigentesDe(input) is not { } vigentes)
        {
            return NoData(id, UnitRatio,
                BcraDeudoresNoDisponible(input) + " Las cuotas vigentes no se pueden estimar.",
                inputs, flujo.SourceDocumentIds, flujo.EvidenceKind);
        }

        if (annualized is not null)
            return CoberturaServicioDeudaAnual(input, annualized, flujo, inputs, flujoAnual, cuota, vigentes);

        var flujoMensual = Math.Round(flujoAnual / 12m, 2);
        var servicio     = vigentes.Amount + cuota;
        var dscr         = Math.Round(flujoMensual / servicio, 4);
        var band = dscr < p.DscrRojo ? PersonIndicatorBands.Rojo
                 : dscr >= p.DscrVerde ? PersonIndicatorBands.Verde
                 : PersonIndicatorBands.Amarillo;

        inputs["flujoMensual"] = F(flujoMensual);
        CuotasVigentesInputs(inputs, input, vigentes);
        inputs["servicioMensual"] = F(servicio);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = dscr,
            Unit              = UnitRatio,
            Band              = band,
            EvidenceKind      = PersonEvidenceKinds.Combine([flujo.EvidenceKind, CuotasVigentesEvidenceKind(vigentes)]),
            Evidence          = $"Flujo mensual {Pesos(flujoMensual)} sobre servicio de deuda {Pesos(servicio)} (cuota {Pesos(cuota)} " +
                                $"más {CuotasVigentesTexto(input, vigentes)}): {F(dscr)}. Sin impuestos (no se leen del legajo).",
            Inputs            = inputs,
            SourceDocumentIds = flujo.SourceDocumentIds
        };
    }

    /// <summary>
    /// Debt-service coverage on an ANNUAL base (engine 0.7): (annual flujo − impuestos) / (cuota × cuotas a year
    /// + cuotas vigentes × 12), so a semiannual or single-payment cuota meets the flujo of the year it
    /// is paid from. Same bands. Impuestos are still not in the legajo, recorded as such.
    /// </summary>
    private static PersonIndicatorResult CoberturaServicioDeudaAnual(
        PersonEvaluationInput input,
        PersonAgroMode agro,
        PersonIndicatorResult flujo,
        Dictionary<string, string?> inputs,
        decimal flujoAnual,
        decimal cuota,
        CuotasVigentes vigentes)
    {
        const string id = "cobertura_servicio_deuda";
        var p = input.Parameters;

        var servicioAnual = ServicioAnual(cuota, vigentes, agro);
        var dscr = Math.Round(flujoAnual / servicioAnual, 4);
        var band = dscr < p.DscrRojo ? PersonIndicatorBands.Rojo
                 : dscr >= p.DscrVerde ? PersonIndicatorBands.Verde
                 : PersonIndicatorBands.Amarillo;

        inputs["flujoAnual"] = F(flujoAnual);
        CuotasVigentesInputs(inputs, input, vigentes);
        inputs["servicioAnual"] = F(servicioAnual);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = dscr,
            Unit              = UnitRatio,
            Band              = band,
            EvidenceKind      = PersonEvidenceKinds.Combine([flujo.EvidenceKind, CuotasVigentesEvidenceKind(vigentes)]),
            Evidence          = $"Flujo anual {Pesos(flujoAnual)} sobre servicio de deuda anual {Pesos(servicioAnual)} (cuota " +
                                $"{Pesos(cuota)} × {Periodicidad(agro.CuotasPorAnio)} más {CuotasVigentesTexto(input, vigentes)} × 12): " +
                                $"{F(dscr)}. Sin impuestos (no se leen del legajo).{AgroEvidence(agro)}",
            Inputs            = inputs,
            SourceDocumentIds = flujo.SourceDocumentIds
        };
    }

    /// <summary>Patrimonio neto at market value, from the reconciled inventory.</summary>
    internal static PersonIndicatorResult SolvenciaPn(PersonEvaluationInput input)
    {
        const string id = "solvencia_pn";
        var inventory = input.Inventory;

        if (inventory.Groups.Count == 0)
            return NoData(id, UnitPesos, "El inventario conciliado del legajo está vacío.");

        var t = inventory.Totals;
        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = t.PatrimonioNetoMarketValue,
            Unit              = UnitPesos,
            EvidenceKind      = PersonEvidenceKinds.Declarado,
            Evidence          = $"Patrimonio neto a valor de mercado: {Pesos(t.PatrimonioNetoMarketValue)} " +
                                $"(activo {Pesos(t.ActivoMarketValue)}, pasivo {Pesos(t.PasivoMarketValue)})." +
                                (t.ActivoWithoutMarketValue > 0 ? $" {t.ActivoWithoutMarketValue} bien(es) sin valor de mercado." : string.Empty),
            Inputs            = new Dictionary<string, string?>
            {
                ["activoMercado"]         = F(t.ActivoMarketValue),
                ["activoFiscal"]          = F(t.ActivoFiscalValue),
                ["pasivoMercado"]         = F(t.PasivoMarketValue),
                ["bienesSinValorMercado"] = F(t.ActivoWithoutMarketValue)
            },
            SourceDocumentIds = InventoryDocuments(inventory)
        };
    }

    /// <summary>
    /// The person's liabilities: the manifestación's, or — when it lists no debt but the Central de Deudores
    /// does — the BCRA total (reported in thousands of pesos). Shared by the solicitante's endeudamiento and the
    /// fiador's executable patrimony.
    /// </summary>
    internal static (decimal Pasivo, bool FromBcra) PasivoOf(PersonEvaluationInput input)
    {
        var pasivo = input.Inventory.Totals.PasivoMarketValue;
        var deuda  = input.Bcra?.Deuda;
        if (pasivo == 0m && deuda is { Estado: BcraDeudaConsultaEstado.DebtFound, MontoTotal: > 0m } && deuda.MontoTotal is { } miles)
            return (miles * 1000m, true);   // the Central de Deudores reports thousands of pesos

        return (pasivo, false);
    }

    /// <summary>
    /// Pasivo / activo at market value. When the manifestación lists no debt but the Central de Deudores does,
    /// the BCRA total is used as the pasivo, and the source says so.
    /// </summary>
    internal static PersonIndicatorResult Endeudamiento(PersonEvaluationInput input)
    {
        const string id = "endeudamiento";
        var t = input.Inventory.Totals;

        if (t.ActivoMarketValue <= 0m)
            return NoData(id, UnitRatio, "Sin activos a valor de mercado en el inventario conciliado.");

        var (pasivo, fromBcra) = PasivoOf(input);
        var pasivoSource = fromBcra
            ? "Central de Deudores (la manifestación no declara deudas)"
            : "manifestación de bienes";

        var ratio = Math.Round(pasivo / t.ActivoMarketValue, 4);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = ratio,
            Unit              = UnitRatio,
            EvidenceKind      = PersonEvidenceKinds.Declarado,
            Evidence          = $"Pasivo {Pesos(pasivo)} ({pasivoSource}) sobre activo {Pesos(t.ActivoMarketValue)}: {Pct(ratio)}.",
            Inputs            = new Dictionary<string, string?>
            {
                ["pasivo"]       = F(pasivo),
                ["pasivoFuente"] = pasivoSource,
                ["activo"]       = F(t.ActivoMarketValue)
            },
            SourceDocumentIds = InventoryDocuments(input.Inventory)
        };
    }

    /// <summary>
    /// Weight of the activity's assets in the patrimonio neto, at market value. At or above
    /// pf_patrimonio_operativo_alerta_pct it is an alert: the person's solvency is mostly the business itself.
    /// </summary>
    internal static PersonIndicatorResult PatrimonioOperativo(PersonEvaluationInput input)
    {
        const string id = "patrimonio_operativo";
        var t = input.Inventory.Totals;
        var threshold = input.Parameters.PatrimonioOperativoAlertaPct;

        var inputs = new Dictionary<string, string?>
        {
            ["bienesActividad"]        = F(t.ActivityAssetsMarketValue),
            ["patrimonioNeto"]         = F(t.PatrimonioNetoMarketValue),
            ["activo"]                 = F(t.ActivoMarketValue),
            ["pesoSobreActivo"]        = t.ActivoMarketValue > 0m ? F(Math.Round(t.ActivityAssetsMarketValue / t.ActivoMarketValue, 4)) : null,
            ["umbralAlerta"]           = F(threshold)
        };

        if (input.Inventory.Groups.Count == 0)
            return NoData(id, UnitRatio, "El inventario conciliado del legajo está vacío.", inputs);

        if (t.PatrimonioNetoMarketValue <= 0m)
        {
            return NoData(id, UnitRatio,
                "Patrimonio neto no positivo: el peso del patrimonio operativo no se puede medir.",
                inputs, InventoryDocuments(input.Inventory));
        }

        var share = Math.Round(t.ActivityAssetsMarketValue / t.PatrimonioNetoMarketValue, 4);
        var alert = share >= threshold;

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = share,
            Unit              = UnitRatio,
            Severity          = alert ? PersonSeverities.Normal : null,
            EvidenceKind      = PersonEvidenceKinds.Declarado,
            Evidence          = $"Bienes de la actividad {Pesos(t.ActivityAssetsMarketValue)}: {Pct(share)} del patrimonio neto" +
                                (alert ? $", por encima del umbral de alerta ({Pct(threshold)})." : "."),
            Inputs            = inputs,
            SourceDocumentIds = InventoryDocuments(input.Inventory)
        };
    }

    /// <summary>The sales coherence of the latest period with two or more sources (#253's coherence summary).</summary>
    internal static PersonIndicatorResult VentasCoherencia(PersonEvaluationInput input)
    {
        const string id = "ventas_coherencia";

        var period = input.Incomes.Periods.FirstOrDefault(p => p.Coherence.Sources >= 2);
        if (period is null)
            return NoData(id, UnitRatio, "Ningún período tiene ventas de dos o más fuentes para comparar.");

        var c = period.Coherence;
        var inputs = new Dictionary<string, string?>
        {
            ["periodo"]    = period.Period ?? $"{F(period.PeriodFrom)}/{F(period.PeriodTo)}",
            ["fuentes"]    = F(c.Sources),
            ["fuenteAlta"] = c.HighSource,
            ["fuenteBaja"] = c.LowSource
        };
        var documents = period.Entries.Select(e => e.DocumentId).Distinct().ToList();

        if (c.MaxRelativeDiff is not { } diff)
            return NoData(id, UnitRatio, "Las ventas del período son cero: la diferencia relativa no se puede calcular.", inputs, documents);

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = diff,
            Unit              = UnitRatio,
            EvidenceKind      = PersonEvidenceKinds.Combine(period.Entries.Select(e => e.Source).Distinct().Select(PersonEvidenceKinds.OfIncomeSource)),
            Evidence          = $"Período {inputs["periodo"]}: {c.Sources} fuentes de ventas; la mayor ({c.HighSource}) supera a la menor ({c.LowSource}) en {Pct(diff)}.",
            Inputs            = inputs,
            SourceDocumentIds = documents
        };
    }

    /// <summary>
    /// Sales declared to the creditor (certificación, manifestación) above those declared to the fisco (IVA,
    /// facturación, IIBB) by more than pf_ventas_acreedor_fisco_tolerancia, in any period that has both:
    /// a strong alert. The period with the widest gap is reported.
    /// </summary>
    internal static PersonIndicatorResult VentasAcreedorVsFisco(PersonEvaluationInput input, IncomeFacts facts)
    {
        const string id = "ventas_acreedor_vs_fisco";
        var tolerance = input.Parameters.VentasAcreedorFiscoTolerancia;

        var comparisons = input.Incomes.Periods
            .Select(period =>
            {
                // DefaultIfEmpty: MaxBy over an empty sequence of value tuples throws rather than
                // returning default, and most periods carry only one side.
                var creditor = IncomeFacts.CreditorSources
                    .Select(s => (Source: s, Sales: IncomeFacts.SalesOf(period, s)))
                    .Where(s => s.Sales is not null)
                    .DefaultIfEmpty()
                    .MaxBy(s => s.Sales?.Amount ?? decimal.MinValue);
                var fiscal = IncomeFacts.FiscalSources
                    .Select(s => (Source: s, Sales: IncomeFacts.SalesOf(period, s)))
                    .Where(s => s.Sales is not null)
                    .DefaultIfEmpty()
                    .MaxBy(s => s.Sales?.Amount ?? decimal.MinValue);
                return (Period: period, Creditor: creditor, Fiscal: fiscal);
            })
            .Where(c => c.Creditor.Sales is not null && c.Fiscal.Sales is not null && c.Creditor.Sales.Value.Amount > 0m)
            .Select(c => (c.Period, c.Creditor, c.Fiscal,
                          Diff: Math.Round((c.Creditor.Sales!.Value.Amount - c.Fiscal.Sales!.Value.Amount) / c.Creditor.Sales.Value.Amount, 4)))
            .ToList();

        if (comparisons.Count == 0)
        {
            return NoData(id, UnitRatio,
                "Ningún período tiene a la vez ventas declaradas al acreedor y al fisco.",
                new() { ["tolerancia"] = F(tolerance) });
        }

        var widest = comparisons.MaxBy(c => c.Diff);
        var label  = widest.Period.Period ?? $"{F(widest.Period.PeriodFrom)}/{F(widest.Period.PeriodTo)}";
        var fires  = widest.Diff > tolerance;
        var creditorAmount = widest.Creditor.Sales!.Value.Amount;
        var fiscalAmount   = widest.Fiscal.Sales!.Value.Amount;

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = widest.Diff,
            Unit              = UnitRatio,
            Band              = fires ? PersonIndicatorBands.Rojo : null,
            EvidenceKind      = PersonEvidenceKinds.Declarado,
            Severity          = fires ? PersonSeverities.Fuerte : null,
            Evidence          = fires
                ? $"Período {label}: ventas declaradas al acreedor ({widest.Creditor.Source}, {Pesos(creditorAmount)}) superan a las " +
                  $"declaradas al fisco ({widest.Fiscal.Source}, {Pesos(fiscalAmount)}) en {Pct(widest.Diff)}; la tolerancia es {Pct(tolerance)}."
                : $"Ventas declaradas al acreedor y al fisco dentro de la tolerancia ({Pct(tolerance)}); mayor diferencia: {Pct(widest.Diff)} en {label}.",
            Inputs            = new Dictionary<string, string?>
            {
                ["periodo"]          = label,
                ["fuenteAcreedor"]   = widest.Creditor.Source,
                ["ventasAcreedor"]   = F(creditorAmount),
                ["fuenteFisco"]      = widest.Fiscal.Source,
                ["ventasFisco"]      = F(fiscalAmount),
                ["tolerancia"]       = F(tolerance),
                ["periodosCompar"]   = F(comparisons.Count),
                ["periodosFueraTol"] = F(comparisons.Count(c => c.Diff > tolerance))
            },
            SourceDocumentIds = widest.Creditor.Sales.Value.Documents.Concat(widest.Fiscal.Sales.Value.Documents).Distinct().ToList()
        };
    }

    private static Dictionary<string, string?> SalesInputs(SalesWindow sales, IncomeFacts facts) => new()
    {
        ["ventas12MesesNominal"] = F(sales.Nominal),
        ["ventas12MesesReal"]    = F(sales.Real),
        ["mesReferenciaIpc"]     = facts.CpiReferenceMonth,
        ["desde"]                = F(sales.From),
        ["hasta"]                = F(sales.To),
        ["mesesDisponibles"]     = F(sales.MonthsAvailable),
        ["anualizado"]           = sales.Annualized ? "true" : "false",
        ["granularidad"]         = sales.Granularity,
        ["fuentes"]              = string.Join(",", sales.Sources),
        ["verificado"]           = sales.Sources.Any(s => IncomeFacts.FiscalSources.Contains(s)) ? "true" : "false"
    };

    private static IReadOnlyList<Guid> InventoryDocuments(LegajoInventoryResponse inventory)
        => inventory.ManifestacionDocumentIds.Concat(inventory.BienesPersonalesDocumentIds).Distinct().ToList();
}
