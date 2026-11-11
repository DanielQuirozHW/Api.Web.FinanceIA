// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// The es-AR labels of the persona humana result, for the BFF's own printed output (the persona PDF). The
/// BFF stores and returns ids; the words for them live in the Portal, and this class is a VERBATIM port of
/// them so a printed report names everything exactly as the screen does.
/// <para>
/// Source — PortalesHWA/Portal-FinanceIA, branch <c>develop</c> at ae98e55:
/// <list type="bullet">
/// <item><c>apps/portal/src/components/analysis/person/person-labels.ts</c> — outcomes, bands, severities,
/// evidence kinds, suggestions, filters, indicators, cannot-conclude fallbacks, snapshot sources, inputs.</item>
/// <item><c>apps/portal/src/lib/legajo-labels.ts</c> — document types.</item>
/// <item><c>apps/portal/src/lib/person-type.ts</c> — fiscal regimes.</item>
/// <item><c>apps/portal/src/components/entities/detail/EntityAnalysesTable.tsx</c> (<c>mapDecisionStatus</c>) —
/// the analysis decision status.</item>
/// <item><c>apps/portal/src/lib/credit-line-status.ts</c> — the credit-line status.</item>
/// <item><c>apps/portal/src/components/analysis/person/person-decision.ts</c> and
/// <c>AnalysisDecisionChecksCard.tsx</c> — the two approval confirmations and their records.</item>
/// </list>
/// When a text changes there, change it here in the same words.
/// </para>
/// <para>
/// An id this class does not know prints as itself — never as an empty string, never as a guess and never
/// as an exception: a report must still render when the engine gains a filter before this file does.
/// </para>
/// </summary>
public static class PersonLabels
{
    /// <summary>The chip of an indicator whose value has no band (<c>PROVISIONAL_LABEL</c>).</summary>
    public const string ProvisionalLabel = "Umbral provisorio";

    /// <summary><c>NOT_AVAILABLE</c>.</summary>
    public const string NotAvailable = "No disponible";

    /// <summary>The label of a filter or indicator with no result.</summary>
    public const string SinDatos = "Sin datos";

    // ── Filter outcomes ──────────────────────────────────────────────────────────

    /// <summary>
    /// The chip a filter outcome renders as (<c>filterOutcomeChip</c>). "Alerta fuerte" is outcome Alerta
    /// with severity Fuerte. A blank outcome is "Sin datos"; an unknown one prints as itself.
    /// </summary>
    public static string FilterOutcome(string? outcome, string? severity) => outcome switch
    {
        PersonFilterOutcomes.Rechazo  => "Rechazo",
        PersonFilterOutcomes.Alerta   => severity == PersonSeverities.Fuerte ? "Alerta fuerte" : "Alerta",
        PersonFilterOutcomes.Ok       => "Ok",
        PersonFilterOutcomes.SinDatos => SinDatos,
        _                             => string.IsNullOrWhiteSpace(outcome) ? SinDatos : outcome
    };

    // ── Indicator bands and severities ───────────────────────────────────────────

    /// <summary>The chip of a band (<c>indicatorBandChip</c>); null when there is no band.</summary>
    public static string? Band(string? band) => band switch
    {
        PersonIndicatorBands.Verde    => "Verde",
        PersonIndicatorBands.Amarillo => "Amarillo",
        PersonIndicatorBands.Naranja  => "Naranja",
        PersonIndicatorBands.Rojo     => "Rojo",
        null or ""                    => null,
        _                             => band
    };

    /// <summary>An alert an indicator raises on its own, when it has no band (<c>indicatorSeverityChip</c>).</summary>
    public static string? IndicatorSeverity(string? severity) => severity switch
    {
        PersonSeverities.Fuerte => "Alerta fuerte",
        PersonSeverities.Normal => "Alerta",
        _                       => null
    };

    // ── Evidence kind ────────────────────────────────────────────────────────────

    /// <summary>The badge of an evidence kind (<c>evidenceKindLabel</c>); null when missing.</summary>
    public static string? EvidenceKind(string? kind) => kind switch
    {
        PersonEvidenceKinds.Verificado => "Verificado",
        PersonEvidenceKinds.Declarado  => "Declarado",
        null or ""                     => null,
        _                              => kind
    };

    /// <summary><c>EVIDENCE_KIND_HINTS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> EvidenceKindHints = new Dictionary<string, string>
    {
        [PersonEvidenceKinds.Verificado] = "Surge de declaraciones presentadas ante ARCA o de registros (BCRA, ARCA, LUFE).",
        [PersonEvidenceKinds.Declarado]  = "Incluye lo que la persona declara al acreedor o un parámetro de referencia."
    };

    // ── Suggestion ───────────────────────────────────────────────────────────────

    /// <summary><c>SUGGESTION_LABELS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> SuggestionLabels = new Dictionary<string, string>
    {
        [PersonSuggestions.RechazoSugerido]         = "Rechazo sugerido",
        [PersonSuggestions.ParaDecisionDelAnalista] = "Para decisión del analista"
    };

    /// <summary>The label of a suggestion; an unknown one prints as itself.</summary>
    public static string Suggestion(string? suggestion) => Lookup(SuggestionLabels, suggestion);

    // ── Filters and indicators ───────────────────────────────────────────────────

    /// <summary><c>FILTER_LABELS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> FilterLabels = new Dictionary<string, string>
    {
        ["certificado_mipyme"]       = "Certificado MiPyME",
        ["bcra_situacion"]           = "Situación en la Central de Deudores",
        ["bcra_eventos_graves"]      = "Eventos graves en el BCRA",
        ["bcra_cheques_impagos"]     = "Cheques rechazados impagos",
        ["arca_sucesion"]            = "Sucesión",
        ["arca_cuit_activo"]         = "CUIT activo en ARCA",
        ["antiguedad_actividad"]     = "Antigüedad de la actividad",
        ["monotributo_tope"]         = "Tope de la categoría de monotributo",
        ["consumo_declarado"]        = "Consumo declarado",
        ["ventas_acreedor_vs_fisco"] = "Ventas declaradas al acreedor vs. al fisco"
    };

    /// <summary>The label of a filter; an unknown id prints as itself.</summary>
    public static string Filter(string? id) => Lookup(FilterLabels, id);

    /// <summary><c>INDICATOR_LABELS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> IndicatorLabels = new Dictionary<string, string>
    {
        ["ventas_nivel"]             = "Ventas de 12 meses",
        ["ventas_tendencia"]         = "Tendencia real de las ventas",
        ["flujo_actividad"]          = "Flujo de la actividad",
        ["cuota_ingreso"]            = "Cuota sobre ingreso",
        ["cobertura_servicio_deuda"] = "Cobertura del servicio de deuda",
        ["solvencia_pn"]             = "Patrimonio neto",
        ["endeudamiento"]            = "Endeudamiento",
        ["patrimonio_operativo"]     = "Patrimonio operativo",
        ["ventas_coherencia"]        = "Coherencia de ventas entre fuentes",
        ["ventas_acreedor_vs_fisco"] = "Ventas al acreedor vs. al fisco",
        // The fiador's two indicators. BFF-first: no Portal label existed when the persona PDF gained its
        // fiador section; keep them in step with the Portal once it names them.
        ["cobertura_patrimonial_fiador"] = "Cobertura patrimonial del fiador",
        ["cuota_ingreso_estres"]         = "Cuota sobre ingreso (estrés)"
    };

    /// <summary>The label of an indicator; an unknown id prints as itself.</summary>
    public static string Indicator(string? id) => Lookup(IndicatorLabels, id);

    // ── Coverage ─────────────────────────────────────────────────────────────────

    /// <summary><c>CANNOT_CONCLUDE_FALLBACKS</c>: only for a statement whose stored text is blank.</summary>
    public static readonly IReadOnlyDictionary<string, string> CannotConcludeFallbacks = new Dictionary<string, string>
    {
        ["regimen_desconocido"]                     = "No se conoce el régimen fiscal: la matriz documental es la general.",
        ["flujo_solo_declarado"]                    = "El flujo surge sólo de documentos declarados por la persona.",
        ["sin_cuadro_resultados"]                   = "Sin cuadro de resultados: no se puede medir el resultado de la actividad.",
        ["gastos_personales_no_disponibles"]        = "Los gastos personales no están en el legajo.",
        ["consumo_sin_definir"]                     = "El consumo personal no está definido.",
        ["sin_resultado_actividad"]                 = "No hay un resultado de la actividad en el legajo.",
        ["amortizaciones_intereses_no_disponibles"] = "Las amortizaciones y los intereses no están en el legajo.",
        ["ventas_solo_nominales"]                   = "Las ventas sólo se pueden leer en pesos nominales.",
        ["bcra_no_disponible"]                      = "La consulta al BCRA no está disponible.",
        ["cheques_no_disponibles"]                  = "La consulta de cheques rechazados no está disponible.",
        ["cuit_estado_no_disponible"]               = "No se puede confirmar que el CUIT esté activo.",
        ["cuota_no_informada"]                      = "No se informó la cuota de la operación.",
        ["impuestos_no_disponibles"]                = "Los impuestos no están en el legajo.",
        ["cuotas_vigentes_estimadas"]               = "Las cuotas vigentes son una estimación.",
        ["lufe_no_disponible"]                      = "La consulta a LUFE no está disponible."
    };

    /// <summary>The stored text when there is one (<c>cannotConcludeText</c>), else the fallback, else the code.</summary>
    public static string CannotConclude(string? code, string? text)
        => !string.IsNullOrWhiteSpace(text) ? text : Lookup(CannotConcludeFallbacks, code);

    /// <summary><c>SNAPSHOT_SOURCE_LABELS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> SnapshotSourceLabels = new Dictionary<string, string>
    {
        ["BCRA"] = "BCRA · Central de Deudores",
        ["ARCA"] = "ARCA · constancia",
        ["LUFE"] = "LUFE · Registro MiPyME"
    };

    /// <summary>The label of a snapshot source; an unknown one prints as itself.</summary>
    public static string SnapshotSource(string? source) => Lookup(SnapshotSourceLabels, source);

    // ── Inputs ───────────────────────────────────────────────────────────────────

    /// <summary><c>INPUT_LABELS</c>. Keys are the engine's verbatim.</summary>
    public static readonly IReadOnlyDictionary<string, string> InputLabels = new Dictionary<string, string>
    {
        ["activo"]                     = "Activo",
        ["activoFiscal"]               = "Activo a valor fiscal",
        ["activoMercado"]              = "Activo a valor de mercado",
        ["alertaDesdeMes"]             = "Alerta desde el mes",
        ["alertaHastaMes"]             = "Alerta hasta el mes",
        ["amortizaciones"]             = "Amortizaciones",
        ["anualizado"]                 = "Anualizado",
        ["aproximado"]                 = "Fecha aproximada",
        ["baseIngreso"]                = "Base del ingreso",
        ["bienesActividad"]            = "Bienes de la actividad",
        ["bienesSinValorMercado"]      = "Bienes sin valor de mercado",
        ["categoria"]                  = "Categoría",
        ["coberturaMarcadores"]        = "Cobertura de los marcadores",
        ["consumoAnual"]               = "Consumo anual",
        ["consumoBase"]                = "Base del consumo",
        ["consumoDeclaradoMensual"]    = "Consumo declarado mensual",
        ["consumoMensual"]             = "Consumo mensual",
        ["consumoMensualPiso"]         = "Piso de consumo mensual",
        ["consumoMesBase"]             = "Mes base del piso de consumo",
        ["consumoPisoBase"]            = "Piso de consumo del mes base",
        ["corteAlto"]                  = "Corte alto",
        ["corteBajo"]                  = "Corte bajo",
        ["corteMedio"]                 = "Corte medio",
        ["cuota"]                      = "Cuota de la operación",
        ["cuotaEstimadaPctSaldo"]      = "Cuota estimada sobre el saldo",
        ["cuotasVigentesEsEstimacion"] = "Cuotas vigentes estimadas",
        ["cuotasVigentes"]             = "Cuotas vigentes",
        ["cuotasVigentesEstimadas"]    = "Cuotas vigentes",
        ["defectosFormales"]           = "Rechazos por defectos formales",
        ["desde"]                      = "Desde",
        ["dscrRojo"]                   = "Corte rojo",
        ["dscrVerde"]                  = "Corte verde",
        ["enRevision"]                 = "Datos en revisión",
        ["consultadoEl"]               = "Consultado el",
        ["esSucesion"]                 = "Es sucesión",
        ["estado"]                     = "Estado",
        ["estadoClave"]                = "Estado de la clave",
        ["factorIpcConsumo"]           = "Factor de IPC del piso",
        ["fechaConsulta"]              = "Fecha de consulta",
        ["fechaUltimoRechazo"]         = "Último rechazo",
        ["flujoMensual"]               = "Flujo mensual",
        ["fuente"]                     = "Fuente",
        ["fuenteAcreedor"]             = "Fuente del acreedor",
        ["fuenteAlta"]                 = "Fuente más alta",
        ["fuenteBaja"]                 = "Fuente más baja",
        ["fuenteFisco"]                = "Fuente fiscal",
        ["fuentes"]                    = "Fuentes",
        ["gastosPersonales"]           = "Gastos personales",
        ["granularidad"]               = "Granularidad",
        ["hasta"]                      = "Hasta",
        ["impagosComputados"]          = "Impagos computados",
        ["impuestos"]                  = "Impuestos",
        ["ingresoMensual"]             = "Ingreso mensual",
        ["inicio"]                     = "Inicio de la actividad",
        ["intereses"]                  = "Intereses",
        ["mesReferenciaIpc"]           = "Mes de referencia del IPC",
        ["meses"]                      = "Antigüedad",
        ["mesesDisponibles"]           = "Meses disponibles",
        ["mesesInformados"]            = "Meses informados",
        ["minimoMeses"]                = "Antigüedad mínima",
        ["montoConsumido"]             = "Monto consumido (F.711)",
        ["montoTotalImpago"]           = "Monto total impago",
        ["pasivo"]                     = "Pasivo",
        ["pasivoFuente"]               = "Fuente del pasivo",
        ["pasivoMercado"]              = "Pasivo a valor de mercado",
        ["patrimonioNeto"]             = "Patrimonio neto",
        ["peorSituacionVentana"]       = "Peor situación en la ventana",
        ["periodo"]                    = "Período",
        ["periodoConsultado"]          = "Período consultado",
        ["periodosCompar"]             = "Períodos comparados",
        ["periodosFueraTol"]           = "Períodos fuera de tolerancia",
        ["pesoSobreActivo"]            = "Peso sobre el activo",
        ["pisoAplicado"]               = "Piso aplicado",
        ["pisoIndexado"]               = "Piso indexado por IPC",
        ["plazoEstimadoDeudaMeses"]    = "Plazo estimado de la deuda",
        ["procesoJudicial"]            = "Proceso judicial",
        ["rechazoMeses"]               = "Rechazo por debajo de",
        ["refinanciaciones"]           = "Refinanciaciones",
        ["renta3ra"]                   = "Renta de tercera categoría",
        ["requiereCertificado"]        = "Requiere certificado",
        ["resultadoActividad"]         = "Resultado de la actividad",
        ["servicioMensual"]            = "Servicio de deuda mensual",
        ["sinFondosPagadosRecientes"]  = "Cheques sin fondos pagados recientemente",
        ["situacion5"]                 = "Situación 5",
        ["situacionActual"]            = "Situación actual",
        ["situacionJuridica"]          = "Situación jurídica",
        ["tablaProvisional"]           = "Tabla provisoria",
        ["tablaVigenteDesde"]          = "Tabla vigente desde",
        ["tolerancia"]                 = "Tolerancia",
        ["topeAnual"]                  = "Tope anual de la categoría",
        ["totalImpagos"]               = "Cheques impagos",
        ["totalRechazados"]            = "Cheques rechazados",
        ["umbralAlerta"]               = "Umbral de alerta",
        ["umbralRechazo"]              = "Umbral de rechazo",
        ["variacionNominal"]           = "Variación nominal",
        ["ventanaMeses"]               = "Ventana",
        ["ventanaPagadosMeses"]        = "Ventana de pagados",
        ["ventas12Meses"]              = "Ventas de 12 meses",
        ["ventas12MesesNominal"]       = "Ventas de 12 meses (nominal)",
        ["ventas12MesesReal"]          = "Ventas de 12 meses (real)",
        ["ventasAcreedor"]             = "Ventas declaradas al acreedor",
        ["ventasFisco"]                = "Ventas declaradas al fisco",
        ["ventasPrevios12Nominal"]     = "Ventas de los 12 meses previos (nominal)",
        ["ventasPrevios12Real"]        = "Ventas de los 12 meses previos (real)",
        ["ventasUltimos12Nominal"]     = "Ventas de los últimos 12 meses (nominal)",
        ["ventasUltimos12Real"]        = "Ventas de los últimos 12 meses (real)",
        ["verificado"]                 = "Verificado",
        // Engine 0.7: agro annual mode and the cuota's periodicity
        ["campaniaAlineada"]           = "Ventana alineada a la campaña",
        ["claePrincipal"]              = "Actividad principal (CLAE)",
        ["cuotasPorAnio"]              = "Cuotas por año",
        ["flujoAnual"]                 = "Flujo anual",
        ["ingresoAnual"]               = "Ingreso anual",
        ["mesInicioCampania"]          = "Mes de inicio de la campaña",
        ["modoAgro"]                   = "Evaluación anual agropecuaria",
        ["origenModoAgro"]             = "Origen del modo anual",
        ["servicioAnual"]              = "Servicio de deuda anual",
        ["ventanaCampania"]            = "Ventana de ventas",
        // Engine 0.8: cuotas vigentes declared by the analyst
        ["cuotasVigentesDeclaradas"]   = "Cuotas vigentes declaradas",
        ["cuotasVigentesOrigen"]       = "Origen de las cuotas vigentes",
    };

    /// <summary>The label of an input key; an unknown key prints as itself.</summary>
    public static string Input(string? key) => Lookup(InputLabels, key);

    /// <summary><c>INPUT_TEXT_VALUES</c>: text values the engine writes as tokens rather than sentences.</summary>
    public static readonly IReadOnlyDictionary<string, string> InputTextValues = new Dictionary<string, string>
    {
        ["sin_historial"]  = "Sin historial",
        ["ultimo_periodo"] = "Último período",
        ["actividad"]      = "Actividad en ARCA",
        ["analista"]       = "Analista",
        ["declarada"]      = "Declaradas por el analista",
        ["estimada"]       = "Estimadas por la Central de Deudores"
    };

    /// <summary>A token value as words; any other text prints as itself.</summary>
    public static string InputTextValue(string value)
        => InputTextValues.TryGetValue(value, out var label) ? label : value;

    // ── Document types (legajo-labels.ts) ────────────────────────────────────────

    /// <summary><c>LEGAJO_DOCUMENT_TYPE_LABELS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> DocumentTypeLabels = new Dictionary<string, string>
    {
        ["ManifestacionBienes"]   = "Manifestación de bienes",
        ["BienesPersonales"]      = "DDJJ Bienes Personales",
        ["Reibp"]                 = "REIBP",
        ["Ganancias"]             = "DDJJ Ganancias",
        ["CertificacionIngresos"] = "Certificación de ingresos",
        ["DdjjIva"]               = "DDJJ IVA",
        ["DdjjIibb"]              = "DDJJ Ingresos Brutos",
        ["Monotributo"]           = "Monotributo",
        ["FlujoProyectado"]       = "Flujo de fondos proyectado",
        ["AutorizacionDatos"]     = "Autorización de datos",
        ["Otro"]                  = "Otro",
        ["Desconocido"]           = "Tipo no reconocido"
    };

    /// <summary>The label of a document type (<c>documentTypeLabel</c>); an unknown one prints as itself.</summary>
    public static string DocumentType(string? documentType) => Lookup(DocumentTypeLabels, documentType);

    // ── Fiscal regimes (person-type.ts) ──────────────────────────────────────────

    /// <summary><c>FISCAL_REGIME_LABEL</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> FiscalRegimeLabels = new Dictionary<string, string>
    {
        ["Monotributo"]          = "Monotributo",
        ["ResponsableInscripto"] = "Responsable inscripto",
        ["Exento"]               = "Exento",
        ["Desconocido"]          = "Sin determinar"
    };

    /// <summary>
    /// The regime in words (<c>formatFiscalRegime</c>): "Monotributo · Cat. B" when a monotributo category is
    /// known. Blank is "Sin datos"; an unknown regime prints as itself.
    /// </summary>
    public static string FiscalRegime(string? regime, string? categoria)
    {
        if (string.IsNullOrWhiteSpace(regime))
            return SinDatos;

        var label = Lookup(FiscalRegimeLabels, regime);
        var cat   = categoria?.Trim().ToUpperInvariant();

        return regime == FiscalRegimes.Monotributo &&!string.IsNullOrEmpty(cat) ? $"{label} · Cat. {cat}" : label;
    }

    // ── Decision (EntityAnalysesTable.tsx, credit-line-status.ts) ────────────────

    /// <summary>
    /// The decision status of an analysis (<c>mapDecisionStatus</c>): Aprobado, Rechazado, Pre-aprobado, and
    /// "Pendiente" for anything still open.
    /// </summary>
    public static string AnalysisStatus(string? status) => status switch
    {
        AnalysisStatuses.Approved    => "Aprobado",
        AnalysisStatuses.Rejected    => "Rechazado",
        AnalysisStatuses.PreApproved => "Pre-aprobado",
        _                            => "Pendiente"
    };

    /// <summary>The credit-line status labels (<c>creditLineStatusLabel</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> CreditLineStatusLabels = new Dictionary<string, string>
    {
        ["Active"]     = "Activo",
        ["Suspended"]  = "Suspendido",
        ["Superseded"] = "Reemplazado"
    };

    /// <summary>The label of a credit-line status; an unknown one prints as itself.</summary>
    public static string CreditLineStatus(string? status) => Lookup(CreditLineStatusLabels, status);

    /// <summary>The counter-guarantee confirmation (<c>personConfirmationCopy</c>), naming the tenant's coverage.</summary>
    public static string CounterGuaranteesConfirmation(int coveragePct)
        => $"Las contragarantías ofrecidas cubren al menos el {coveragePct} % del aval.";

    /// <summary>The Fondo de Riesgo confirmation (<c>personConfirmationCopy</c>).</summary>
    public const string FdrLimitConfirmation =
        "La exposición del socio y su grupo, con esta garantía, no supera el 5 % del Fondo de Riesgo de la SGR.";

    /// <summary>The title of the pre-approval record (<c>AnalysisDecisionChecksCard</c>).</summary>
    public const string PreApprovalTitle = "Preaprobación";

    /// <summary>The title of the final record: "Confirmación" after a pre-approval, "Aprobación" otherwise.</summary>
    public static string ConfirmationTitle(bool hadPreApproval) => hadPreApproval ? "Confirmación" : "Aprobación";

    /// <summary>A record with no reason.</summary>
    public const string NoReasonRecorded = "Sin motivo registrado.";

    // ── Fiadores ─────────────────────────────────────────────────────────────────
    // BFF-first: the persona PDF's "Fiadores y contragarantía" section needed these words before the Portal had
    // them. When the Portal names any of them, change them here to its words. The asset reason itself is
    // frozen on each asset (FiadorAssetResult.ReasonText) and prints as stored; AssetReasonLabels only stand
    // in for a blank one.

    /// <summary>The kind of guarantee a fiador gives (<see cref="GuaranteeTypes"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> GuaranteeTypeLabels = new Dictionary<string, string>
    {
        [GuaranteeTypes.Fianza]  = "Fianza",
        [GuaranteeTypes.Conyuge] = "Cónyuge o conviviente inscripto"
    };

    /// <summary>The label of a guarantee type; an unknown one prints as itself.</summary>
    public static string GuaranteeType(string? guaranteeType) => Lookup(GuaranteeTypeLabels, guaranteeType);

    /// <summary>The kind of an asset of a fiador's breakdown (<see cref="LegajoItemKinds"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetKindLabels = new Dictionary<string, string>
    {
        [LegajoItemKinds.Inmueble]      = "Inmueble",
        [LegajoItemKinds.Rodado]        = "Rodado",
        [LegajoItemKinds.Participacion] = "Participación societaria",
        [LegajoItemKinds.Deposito]      = "Depósito",
        [LegajoItemKinds.Efectivo]      = "Dinero en efectivo",
        [LegajoItemKinds.Inversion]     = "Inversión",
        [LegajoItemKinds.Credito]       = "Crédito",
        [LegajoItemKinds.Deuda]         = "Deuda",
        [LegajoItemKinds.Otro]          = "Otro bien"
    };

    /// <summary>The label of an asset kind; an unknown one prints as itself.</summary>
    public static string AssetKind(string? kind) => Lookup(AssetKindLabels, kind);

    /// <summary>The class of an investment (<see cref="LegajoInvestmentClasses"/>, engine 0.9).</summary>
    public static readonly IReadOnlyDictionary<string, string> InvestmentClassLabels = new Dictionary<string, string>
    {
        [LegajoInvestmentClasses.TituloPublico]        = "Título público",
        [LegajoInvestmentClasses.AccionCotizante]      = "Acción con cotización",
        [LegajoInvestmentClasses.ObligacionNegociable] = "Obligación negociable",
        [LegajoInvestmentClasses.CuotaparteFci]        = "Cuotaparte de FCI",
        [LegajoInvestmentClasses.Otra]                 = "Inversión sin clasificar"
    };

    /// <summary>The label of an investment class; an unknown one prints as itself.</summary>
    public static string InvestmentClass(string? investmentClass) => Lookup(InvestmentClassLabels, investmentClass);

    /// <summary>
    /// An asset's kind with its investment class: "Inversión · Título público" for the four classes, "Inversión sin
    /// clasificar" for Otra, and the kind alone otherwise — an unclassified Inversion (every asset frozen before engine
    /// 0.9) and every other kind print as they always have. A class this code does not know is ignored.
    /// </summary>
    public static string AssetKind(string? kind, string? investmentClass)
        => LegajoInvestmentClasses.Normalize(kind, investmentClass) switch
        {
            null                          => AssetKind(kind),
            LegajoInvestmentClasses.Otra  => InvestmentClass(LegajoInvestmentClasses.Otra),
            var known                     => $"{AssetKind(kind)} · {InvestmentClass(known)}"
        };

    /// <summary>What the evaluation did with an asset (<see cref="FiadorAssetTreatments"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetTreatmentLabels = new Dictionary<string, string>
    {
        [FiadorAssetTreatments.Computado]   = "Computado",
        [FiadorAssetTreatments.Excluido]    = "Excluido",
        [FiadorAssetTreatments.NoComputado] = "No computado"
    };

    /// <summary>The label of an asset treatment; an unknown one prints as itself.</summary>
    public static string AssetTreatment(string? treatment) => Lookup(AssetTreatmentLabels, treatment);

    /// <summary>Where the value an asset was counted at came from (<c>FiadorAssetResult.ValueSource</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetValueSourceLabels = new Dictionary<string, string>
    {
        ["manifestacion"]     = "Manifestación de bienes",
        ["bienes_personales"] = "Bienes Personales"
    };

    /// <summary>The label of an asset's value source; an unknown one prints as itself.</summary>
    public static string AssetValueSource(string? source) => Lookup(AssetValueSourceLabels, source);

    /// <summary>Where a fiador's liabilities came from (<c>FiadorCoverageResult.LiabilitiesSource</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> LiabilitiesSourceLabels = new Dictionary<string, string>
    {
        ["manifestacion"] = "según la manifestación de bienes",
        ["bcra"]          = "según la deuda informada al BCRA",
        ["ninguno"]       = "sin pasivos informados"
    };

    /// <summary>The label of a liabilities source; an unknown one prints as itself.</summary>
    public static string LiabilitiesSource(string? source) => Lookup(LiabilitiesSourceLabels, source);

    /// <summary>An alert on one asset of a fiador (<see cref="FiadorAssetAlerts"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> AssetAlertLabels = new Dictionary<string, string>
    {
        [FiadorAssetAlerts.BelowFiscalFloor]           = "Valor declarado por debajo del fiscal",
        [FiadorAssetAlerts.OnlyInManifestacion]        = "Sólo en la manifestación de bienes",
        [FiadorAssetAlerts.OnlyInBienesPersonales]     = "Sólo en Bienes Personales",
        [FiadorAssetAlerts.MercadoMuySuperiorAlFiscal] = "Valor declarado muy superior al fiscal"
    };

    /// <summary>The label of an asset alert; an unknown one prints as itself.</summary>
    public static string AssetAlert(string? alert) => Lookup(AssetAlertLabels, alert);

    /// <summary>
    /// Why an asset counted the way it did (<see cref="FiadorAssetReasons"/>). Only a stand-in for a blank
    /// <c>FiadorAssetResult.ReasonText</c>: the frozen text is what the detail shows, so it is what prints.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AssetReasonLabels = new Dictionary<string, string>
    {
        [FiadorAssetReasons.ValorManifestacion]           = "Computado al valor de la manifestación de bienes.",
        [FiadorAssetReasons.SoloValorFiscal]              = "Computado al valor de Bienes Personales: la manifestación no lo lista.",
        [FiadorAssetReasons.ViviendaSinAsentimiento]      = "Vivienda sin asentimiento conyugal ni inoponibilidad: no es ejecutable.",
        [FiadorAssetReasons.GarantiaCorrelacionada]       = "Vinculado a la CUIT del solicitante: correlacionado con el deudor.",
        [FiadorAssetReasons.PendienteDeDefinicion]        = "Tipo de bien sin descuento definido: no se computa.",
        [FiadorAssetReasons.TipoNoReconocido]             = "Tipo de bien no reconocido: no se computa.",
        [FiadorAssetReasons.NoEjecutableSinDocumentacion] = "Crédito a favor de la persona: no es ejecutable sin documentación (descuento del 100 %).",
        [FiadorAssetReasons.BienNoAdmitido]               = "Otro bien: no se admite como respaldo de la garantía (descuento del 100 %).",
        [FiadorAssetReasons.DescuentoTotal]               = "El descuento configurado es del 100 %: no suma al patrimonio ejecutable.",
        [FiadorAssetReasons.EfectivoSinRespaldo]          = "Efectivo declarado sin respaldo documental: no se computa.",
        [FiadorAssetReasons.SinValor]                     = "Sin valor en la manifestación de bienes ni en Bienes Personales.",
        [FiadorAssetReasons.EnInventarioDelSolicitante]   = "También figura en el inventario del solicitante: correlacionado con el deudor.",
        [FiadorAssetReasons.DuplicadoOtroFiador]          = "Ya se computa para otro fiador.",
        [FiadorAssetReasons.RechazoSugerido]              = "Fiador con rechazo sugerido: fuera de la cobertura."
    };

    /// <summary>The label of an asset reason code; an unknown one prints as itself.</summary>
    public static string AssetReason(string? reason) => Lookup(AssetReasonLabels, reason);

    // ── Shared ───────────────────────────────────────────────────────────────────

    private static string Lookup(IReadOnlyDictionary<string, string> labels, string? id)
    {
        if (id is null)
            return string.Empty;

        return labels.TryGetValue(id, out var label) ? label : id;
    }
}
