// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The evaluation of one FIADOR persona humana on his own legajo (M10 Phase 5a-2): the solicitante's BCRA and
/// ARCA pre-filters, his adjusted patrimonial coverage and a stress cuota/ingreso. PURE, like
/// <see cref="Evaluate"/>: no I/O, no clock.
/// </summary>
/// <remarks>
/// <para>
/// EXECUTABLE PATRIMONY = Σ (value × (1 − haircut − currency haircut)) − liabilities − guarantees given. The
/// value is the manifestación's (Declarado); Bienes Personales is its floor and existence proof and is used
/// only when the manifestación does not list the asset ("solo valor fiscal", Verificado). The values already
/// are the person's share — both extraction prompts ask for it and the reconciliation never multiplies by
/// OwnershipPct — so OwnershipPct is NOT applied again.
/// </para>
/// <para>
/// Every haircut is a PROVISIONAL pf_ key, one per kind (engine 0.6 added Deposito, Inversion, Credito and Otro,
/// which 0.5 listed without counting), and since engine 0.9 one per investment class: títulos públicos
/// (pf_haircut_titulo_publico), acciones and ON con cotización (pf_haircut_accion_on) and FCI (pf_haircut_fci); an
/// unclassified investment (Otra or no class) keeps pf_haircut_inversion. The currency-mismatch haircut adds on top.
/// A Deposito, Inversion, Credito or Otro at a 100 % haircut is Excluido with its own reason rather than counted at
/// zero; only a kind the engine does not know stays NoComputado. Cash at hand
/// (Efectivo, engine 0.8) has no haircut: it is always Excluido with efectivo_sin_respaldo.
/// </para>
/// </remarks>
public static partial class PersonEvaluationService
{
    private const string ValueSourceManifestacion    = "manifestacion";
    private const string ValueSourceBienesPersonales = "bienes_personales";

    /// <summary>The frozen text of <see cref="FiadorAssetReasons.EfectivoSinRespaldo"/>.</summary>
    private const string EfectivoSinRespaldoText = "Efectivo declarado sin respaldo documental: no se computa.";

    /// <summary>The match key kinds the reconciliation pairs on; the only ones a cross-legajo de-duplication trusts.</summary>
    public static readonly IReadOnlySet<string> DeduplicableKeyKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        LegajoMatchKeyKinds.Partida, LegajoMatchKeyKinds.Dominio, LegajoMatchKeyKinds.Cuit, LegajoMatchKeyKinds.Cbu
    };

    /// <summary>The de-duplication key of an item ("Partida:123"), or null when its key cannot be trusted across legajos.</summary>
    public static string? DeduplicationKey(string? matchKeyKind, string? matchKey)
        => matchKeyKind is not null && DeduplicableKeyKinds.Contains(matchKeyKind) && !string.IsNullOrWhiteSpace(matchKey)
            ? $"{matchKeyKind}:{matchKey}"
            : null;

    /// <summary>Evaluates one fiador.</summary>
    public static FiadorEvaluationResult FiadorEvaluation(FiadorEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Person);

        var person = input.Person;
        var asOf   = DateOnly.FromDateTime(ArgentineTime.ToLocal(person.AsOfUtc));
        var facts  = IncomeFacts.From(person);

        // The solicitante's checks by CUIT snapshot, unchanged. NOT the MiPyME certificate nor the activity
        // filters: they qualify the borrower's business, not a guarantor.
        var filters = new List<PersonFilterResult>
        {
            BcraSituacion(person),
            BcraEventosGraves(person),
            BcraChequesImpagos(person, asOf),
            ArcaSucesion(person),
            ArcaCuitActivo(person)
        };

        var coverage   = FiadorCoverage(input);
        var indicators = new List<PersonIndicatorResult>
        {
            CoberturaPatrimonialFiador(input, coverage),
            CuotaIngresoEstres(person, facts)
        };

        return new FiadorEvaluationResult
        {
            Version    = Version,
            Suggestion = Suggest(filters),
            Filters    = filters,
            Indicators = indicators,
            Coverage   = coverage
        };
    }

    // ── Executable patrimony ─────────────────────────────────────────────────────

    internal static FiadorCoverageResult FiadorCoverage(FiadorEvaluationInput input)
    {
        var person     = input.Person;
        var p          = person.Parameters;
        var isSpouse   = input.GuaranteeType == GuaranteeTypes.Conyuge;
        var solicitanteCuit = LegajoCuit.Digits(input.SolicitanteCuit);

        var solicitanteKeys = isSpouse
            ? ActivoItems(input.SolicitanteInventory)
                .Select(i => DeduplicationKey(i.MatchKeyKind, i.MatchKey))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var housingExecutable = input.SpouseConsent || input.HousingInopposable;

        var assets = ActivoItems(person.Inventory)
            .Select(i => Asset(i, FiadorAssetOrigins.Propio, person, solicitanteCuit, solicitanteKeys, housingExecutable))
            .ToList();

        // The spouse's consent makes the solicitante's housing executable through this guarantee.
        if (isSpouse && input.SpouseConsent)
        {
            assets.AddRange(ActivoItems(input.SolicitanteInventory)
                .Where(i => i.IsHousing && i.Kind == LegajoItemKinds.Inmueble)
                .Select(i => Asset(i, FiadorAssetOrigins.Solicitante, person, solicitanteCuit, NoKeys, housingExecutable: true)));
        }

        var gross = assets.Where(a => a.Treatment == FiadorAssetTreatments.Computado).Sum(a => a.ExecutableValue);
        var (liabilities, fromBcra) = PasivoOf(person);
        var guarantees = Math.Max(0m, input.GuaranteesGivenAmount);

        return new FiadorCoverageResult
        {
            OperationCurrency     = person.Currency,
            Comparable            = string.Equals(person.Currency, "ARS", StringComparison.Ordinal),
            AmountAtCharge        = person.RequestedAmount,
            Assets                = assets,
            GrossExecutable       = gross,
            Liabilities           = liabilities,
            LiabilitiesSource     = fromBcra ? "bcra" : liabilities > 0m ? ValueSourceManifestacion : "ninguno",
            LiabilityItemIds      = person.Inventory.Groups
                                        .Where(g => g.Side == LegajoItemSides.Pasivo)
                                        .SelectMany(g => g.Items)
                                        .Select(i => i.ItemId)
                                        .ToList(),
            GuaranteesGivenAmount = guarantees,
            ExecutablePatrimony   = gross - liabilities - guarantees,
            CoberturaRojo         = p.ResolvedCoberturaRojo,
            CoberturaVerde        = p.ResolvedCoberturaVerde
        };
    }

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    private static IEnumerable<LegajoItemResponse> ActivoItems(LegajoInventoryResponse inventory)
        => inventory.Groups.Where(g => g.Side == LegajoItemSides.Activo).SelectMany(g => g.Items);

    private static FiadorAssetResult Asset(
        LegajoItemResponse item, string origin, PersonEvaluationInput person, string? solicitanteCuit,
        IReadOnlySet<string> solicitanteKeys, bool housingExecutable)
    {
        var p = person.Parameters;

        // The value: the manifestación's, else Bienes Personales ("solo valor fiscal"). The fiscal value is a
        // floor and an existence proof; it never replaces a manifestación value.
        decimal? valueUsed;
        string? valueSource;
        Guid? sourceDocument;
        if (item.MarketValue is { } market)
        {
            (valueUsed, valueSource, sourceDocument) = (market, ValueSourceManifestacion, item.MarketValueDocumentId);
        }
        else if (item.FiscalValue is { } fiscal)
        {
            (valueUsed, valueSource, sourceDocument) = (fiscal, ValueSourceBienesPersonales, item.FiscalValueDocumentId);
        }
        else
        {
            (valueUsed, valueSource, sourceDocument) = (null, null, null);
        }

        var soloFiscal   = item.MarketValue is null && item.FiscalValue is not null;
        var evidenceKind = soloFiscal ? PersonEvidenceKinds.Verificado : PersonEvidenceKinds.Declarado;

        var asset = new FiadorAssetResult
        {
            ItemId           = item.ItemId,
            Origin           = origin,
            Kind             = item.Kind,
            InvestmentClass  = LegajoInvestmentClasses.Normalize(item.Kind, item.InvestmentClass),
            Description      = item.Description,
            MatchKeyKind     = item.MatchKeyKind,
            MatchKey         = item.MatchKey,
            OwnershipPct     = item.OwnershipPct,
            MarketValue      = item.MarketValue,
            FiscalValue      = item.FiscalValue,
            ValueUsed        = valueUsed,
            ValueSource      = valueSource,
            SoloValorFiscal  = soloFiscal,
            SourceDocumentId = sourceDocument,
            Currency         = item.Currency,
            IsHousing        = item.IsHousing,
            ExecutableValue  = 0m,
            Treatment        = FiadorAssetTreatments.Excluido,
            Reason           = FiadorAssetReasons.SinValor,
            ReasonText       = "Sin valor en la manifestación de bienes ni en Bienes Personales.",
            EvidenceKind     = evidenceKind,
            Alerts           = AssetAlerts(item, p.ResolvedRatioMercadoFiscalAlerta)
        };

        // Correlated collateral: shares in, or a credit against, the guaranteed person or PyME.
        if (solicitanteCuit is not null && LegajoCuit.Digits(item.LinkedCuit) == solicitanteCuit)
        {
            return asset with
            {
                Reason     = FiadorAssetReasons.GarantiaCorrelacionada,
                ReasonText = "Vinculado a la CUIT del solicitante (participación en él o crédito contra él): está " +
                             "correlacionado con el deudor y no se computa."
            };
        }

        // The spouse's independent coverage: an asset also in the solicitante's inventory is the debtor's too.
        if (origin == FiadorAssetOrigins.Propio
            && DeduplicationKey(item.MatchKeyKind, item.MatchKey) is { } key
            && solicitanteKeys.Contains(key))
        {
            return asset with
            {
                Reason     = FiadorAssetReasons.EnInventarioDelSolicitante,
                ReasonText = "También figura en el inventario del solicitante: está correlacionado con el deudor y no " +
                             "suma a la cobertura propia del cónyuge."
            };
        }

        // Cash at hand is declared without documentary support: it never counts (decision 7), whatever the
        // haircuts. Only a legajo reconciled since engine 0.8 tells it apart; before, it was an Otro item.
        if (item.Kind == LegajoItemKinds.Efectivo)
        {
            return asset with
            {
                Reason     = FiadorAssetReasons.EfectivoSinRespaldo,
                ReasonText = EfectivoSinRespaldoText
            };
        }

        decimal? kindHaircut = item.Kind switch
        {
            LegajoItemKinds.Inmueble when item.IsHousing => p.ResolvedHaircutInmuebleVivienda,
            LegajoItemKinds.Inmueble                     => p.ResolvedHaircutInmueble,
            LegajoItemKinds.Rodado                       => p.ResolvedHaircutRodado,
            LegajoItemKinds.Participacion                => p.ResolvedHaircutParticipacion,
            LegajoItemKinds.Deposito                     => p.ResolvedHaircutDeposito,
            LegajoItemKinds.Inversion                    => InvestmentHaircut(p, asset.InvestmentClass),
            LegajoItemKinds.Credito                      => p.ResolvedHaircutCredito,
            LegajoItemKinds.Otro                         => p.ResolvedHaircutOtro,
            _                                            => null
        };

        if (kindHaircut is not { } haircut)
        {
            return asset with
            {
                Treatment  = FiadorAssetTreatments.NoComputado,
                Reason     = FiadorAssetReasons.TipoNoReconocido,
                ReasonText = $"{KindLabel(item.Kind)}: tipo de bien no reconocido, no se computa."
            };
        }

        if (haircut >= 1m && FullHaircutReason(item.Kind, asset.InvestmentClass) is { } fullHaircut)
            return asset with { Reason = fullHaircut.Reason, ReasonText = fullHaircut.Text };

        if (valueUsed is not { } value)
            return asset;

        if (item.Kind == LegajoItemKinds.Inmueble && item.IsHousing && !housingExecutable)
        {
            return asset with
            {
                Reason     = FiadorAssetReasons.ViviendaSinAsentimiento,
                ReasonText = "Vivienda sin asentimiento conyugal ni inoponibilidad de su protección a este acreedor: " +
                             "no es ejecutable."
            };
        }

        var mismatch = !string.IsNullOrWhiteSpace(item.Currency)
                       && !string.Equals(item.Currency, person.Currency, StringComparison.OrdinalIgnoreCase);
        decimal? currencyHaircut = mismatch ? p.ResolvedHaircutDescalceMoneda : null;
        var factor     = Math.Max(0m, 1m - haircut - (currencyHaircut ?? 0m));
        var executable = Math.Round(value * factor, 2);

        var prefix = origin == FiadorAssetOrigins.Solicitante
            ? "Vivienda del solicitante, ejecutable por el asentimiento del cónyuge: computada"
            : item.Kind == LegajoItemKinds.Inmueble && item.IsHousing
                ? "Vivienda con asentimiento conyugal o inoponibilidad de su protección: computada"
                : "Computado";
        var basis = soloFiscal
            ? "al valor de Bienes Personales (solo valor fiscal: la manifestación no lo lista)"
            : "al valor de la manifestación de bienes";
        var text = $"{prefix} {basis} con un descuento de {Pct(haircut)}" +
                   (currencyHaircut is { } c
                       ? $" más {Pct(c)} por descalce de moneda ({item.Currency} frente a {person.Currency})"
                       : string.Empty) +
                   $": {Pesos(executable)}.";

        return asset with
        {
            Haircut         = haircut,
            CurrencyHaircut = currencyHaircut,
            ExecutableValue = executable,
            Treatment       = FiadorAssetTreatments.Computado,
            Reason          = soloFiscal ? FiadorAssetReasons.SoloValorFiscal : FiadorAssetReasons.ValorManifestacion,
            ReasonText      = text
        };
    }

    /// <summary>
    /// The alerts of one asset: today's reconciliation flags as they are (BelowFiscalFloor, in only one document)
    /// plus the manifestación value far above the fiscal one.
    /// </summary>
    private static IReadOnlyList<string> AssetAlerts(LegajoItemResponse item, int ratioMercadoFiscal)
    {
        var alerts = new List<string>();

        if (item.ConflictReasons.Contains(LegajoItemConflictReasons.BelowFiscalFloor))
            alerts.Add(FiadorAssetAlerts.BelowFiscalFloor);

        if (item.ReconciliationStatus == LegajoReconciliationStatuses.OnlyInManifestacion)
            alerts.Add(FiadorAssetAlerts.OnlyInManifestacion);
        else if (item.ReconciliationStatus == LegajoReconciliationStatuses.OnlyInBienesPersonales)
            alerts.Add(FiadorAssetAlerts.OnlyInBienesPersonales);

        if (item is { MarketValue: > 0m, FiscalValue: > 0m }
            && item.MarketValue.Value > ratioMercadoFiscal * item.FiscalValue.Value)
        {
            alerts.Add(FiadorAssetAlerts.MercadoMuySuperiorAlFiscal);
        }

        return alerts;
    }

    /// <summary>
    /// The haircut of an investment by its class (engine 0.9): títulos públicos, acciones and ON con cotización, FCI.
    /// Otra, no class and a class this code does not know take pf_haircut_inversion, the haircut of every investment
    /// before 0.9.
    /// </summary>
    private static decimal InvestmentHaircut(PersonEvaluationParameters p, string? investmentClass) => investmentClass switch
    {
        LegajoInvestmentClasses.TituloPublico        => p.ResolvedHaircutTituloPublico,
        LegajoInvestmentClasses.AccionCotizante      => p.ResolvedHaircutAccionOn,
        LegajoInvestmentClasses.ObligacionNegociable => p.ResolvedHaircutAccionOn,
        LegajoInvestmentClasses.CuotaparteFci        => p.ResolvedHaircutFci,
        _                                            => p.ResolvedHaircutInversion
    };

    /// <summary>
    /// Why a Deposito, Inversion, Credito or Otro at a 100 % haircut is left out; null for the other kinds, which
    /// keep counting at zero as they always have. A classified investment names its class in the text
    /// ("Inversión · Título público: …"); an unclassified one keeps the text of engine 0.8.
    /// </summary>
    private static (string Reason, string Text)? FullHaircutReason(string kind, string? investmentClass) => kind switch
    {
        LegajoItemKinds.Credito => (FiadorAssetReasons.NoEjecutableSinDocumentacion,
            "Crédito a favor de la persona: no es ejecutable sin documentación (descuento del 100 %)."),
        LegajoItemKinds.Otro => (FiadorAssetReasons.BienNoAdmitido,
            "Otro bien: no se admite como respaldo de la garantía (descuento del 100 %)."),
        LegajoItemKinds.Inversion when investmentClass is not null => (FiadorAssetReasons.DescuentoTotal,
            $"{PersonLabels.AssetKind(kind, investmentClass)}: el descuento configurado es del 100 %, no suma al patrimonio ejecutable."),
        LegajoItemKinds.Deposito or LegajoItemKinds.Inversion => (FiadorAssetReasons.DescuentoTotal,
            $"{KindLabel(kind)}: el descuento configurado es del 100 %, no suma al patrimonio ejecutable."),
        _ => null
    };

    private static string KindLabel(string kind) => kind switch
    {
        LegajoItemKinds.Deposito  => "Depósito",
        LegajoItemKinds.Inversion => "Inversión",
        LegajoItemKinds.Credito   => "Crédito",
        LegajoItemKinds.Otro      => "Otro bien",
        _                         => kind
    };

    // ── Indicators ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Adjusted patrimonial coverage: executable patrimony / amount at the fiador's charge, banded by
    /// pf_cobertura_rojo (below: Rojo) and pf_cobertura_verde (from it: Verde).
    /// </summary>
    internal static PersonIndicatorResult CoberturaPatrimonialFiador(FiadorEvaluationInput input, FiadorCoverageResult coverage)
    {
        const string id = "cobertura_patrimonial_fiador";

        var counted = coverage.Assets.Where(a => a.Treatment == FiadorAssetTreatments.Computado).ToList();
        var inputs = new Dictionary<string, string?>
        {
            ["corteRojo"]           = F(coverage.CoberturaRojo),
            ["corteVerde"]          = F(coverage.CoberturaVerde),
            ["montoACargo"]         = F(coverage.AmountAtCharge),
            ["activoEjecutable"]    = F(coverage.GrossExecutable),
            ["pasivo"]              = F(coverage.Liabilities),
            ["pasivoFuente"]        = coverage.LiabilitiesSource,
            ["garantiasOtorgadas"]  = F(coverage.GuaranteesGivenAmount),
            ["patrimonioEjecutable"] = F(coverage.ExecutablePatrimony),
            ["bienesComputados"]    = F(counted.Count),
            ["bienesExcluidos"]     = F(coverage.Assets.Count(a => a.Treatment == FiadorAssetTreatments.Excluido)),
            ["bienesNoComputados"]  = F(coverage.Assets.Count(a => a.Treatment == FiadorAssetTreatments.NoComputado))
        };
        var documents = counted.Select(a => a.SourceDocumentId).OfType<Guid>().Distinct().ToList();

        if (coverage.Assets.Count == 0 && coverage.Liabilities == 0m)
            return NoData(id, UnitRatio, "El inventario conciliado del legajo del fiador está vacío.", inputs);

        if (!coverage.Comparable)
        {
            return NoData(id, UnitRatio,
                $"La operación está en {coverage.OperationCurrency} y el patrimonio del legajo en pesos: no se convierte moneda.",
                inputs, documents);
        }

        var ratio = Math.Round(coverage.ExecutablePatrimony / coverage.AmountAtCharge, 4);
        var band  = ratio < coverage.CoberturaRojo ? PersonIndicatorBands.Rojo
                  : ratio >= coverage.CoberturaVerde ? PersonIndicatorBands.Verde
                  : PersonIndicatorBands.Amarillo;

        var kinds = counted.Select(a => a.EvidenceKind).ToList();
        if (coverage.Liabilities > 0m)
            kinds.Add(coverage.LiabilitiesSource == "bcra" ? PersonEvidenceKinds.Verificado : PersonEvidenceKinds.Declarado);
        if (coverage.GuaranteesGivenAmount > 0m)
            kinds.Add(PersonEvidenceKinds.Declarado);

        var pasivoTexto = coverage.LiabilitiesSource == "bcra"
            ? "Central de Deudores, la manifestación no declara deudas"
            : "manifestación de bienes";

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = ratio,
            Unit              = UnitRatio,
            Band              = band,
            Severity          = band == PersonIndicatorBands.Rojo ? PersonSeverities.Normal : null,
            EvidenceKind      = kinds.Count == 0 ? PersonEvidenceKinds.Declarado : PersonEvidenceKinds.Combine(kinds),
            Evidence          = $"Patrimonio ejecutable {Pesos(coverage.ExecutablePatrimony)} (bienes computados " +
                                $"{Pesos(coverage.GrossExecutable)}, menos pasivo {Pesos(coverage.Liabilities)} ({pasivoTexto}), " +
                                $"menos garantías otorgadas {Pesos(coverage.GuaranteesGivenAmount)}) sobre el monto a su cargo " +
                                $"{Pesos(coverage.AmountAtCharge)}: {F(ratio)} veces.",
            Inputs            = inputs,
            SourceDocumentIds = documents
        };
    }

    /// <summary>
    /// Stress cuota/ingreso: (cuotas vigentes — declared, else estimated — + the FULL cuota of the operation) / the fiador's monthly
    /// income, on the solicitante's income base. Above pf_cuota_ingreso_estres_max it is an alert.
    /// </summary>
    internal static PersonIndicatorResult CuotaIngresoEstres(PersonEvaluationInput person, IncomeFacts facts)
    {
        const string id = "cuota_ingreso_estres";
        var max = person.Parameters.ResolvedCuotaIngresoEstresMax;

        var inputs = new Dictionary<string, string?> { ["corteMaximo"] = F(max) };

        if (person.InstallmentAmount is not { } cuota)
            return NoData(id, UnitRatio, "No se informó la cuota de la operación.", inputs);

        inputs["cuota"] = F(cuota);

        if (AnnualIncomeBase(facts) is not { } incomeBase)
            return NoData(id, UnitRatio, "El legajo del fiador no tiene ingresos para comparar con la cuota.", inputs);

        var (annual, basis, basisKind, documents) = incomeBase;
        inputs["baseIngreso"] = basis;

        if (!string.Equals(person.Currency, "ARS", StringComparison.Ordinal))
        {
            return NoData(id, UnitRatio,
                $"La cuota está en {person.Currency} y los ingresos del legajo en pesos: no se convierte moneda.",
                inputs, documents, basisKind);
        }

        if (CuotasVigentesDe(person) is not { } vigentes)
        {
            return NoData(id, UnitRatio,
                BcraDeudoresNoDisponible(person) + " Las cuotas vigentes no se pueden estimar.",
                inputs, documents, basisKind);
        }

        CuotasVigentesInputs(inputs, person, vigentes);

        var monthly = annual / 12m;
        inputs["ingresoMensual"] = F(Math.Round(monthly, 2));

        if (monthly <= 0m)
            return NoData(id, UnitRatio, $"El ingreso mensual según {basis} no es positivo.", inputs, documents, basisKind);

        var ratio = Math.Round((cuota + vigentes.Amount) / monthly, 4);
        var alert = ratio > max;

        return new PersonIndicatorResult
        {
            Id                = id,
            Status            = PersonIndicatorStatuses.Ok,
            Value             = ratio,
            Unit              = UnitRatio,
            Band              = alert ? PersonIndicatorBands.Rojo : PersonIndicatorBands.Verde,
            Severity          = alert ? PersonSeverities.Normal : null,
            EvidenceKind      = PersonEvidenceKinds.Combine([basisKind, CuotasVigentesEvidenceKind(vigentes)]),
            Evidence          = $"Estrés: cuota completa de la operación {Pesos(cuota)} más {CuotasVigentesTexto(person, vigentes)} " +
                                $"sobre ingreso mensual {Pesos(monthly)} según {basis}: {Pct(ratio)}" +
                                (alert ? $", por encima del máximo ({Pct(max)})." : "."),
            Inputs            = inputs,
            SourceDocumentIds = documents
        };
    }
}
