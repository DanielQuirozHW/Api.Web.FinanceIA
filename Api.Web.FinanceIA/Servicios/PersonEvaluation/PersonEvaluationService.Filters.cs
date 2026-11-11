// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>The pre-filters: cheap checks by CUIT snapshot, before any indicator.</summary>
public static partial class PersonEvaluationService
{
    private const string SourceDeudores = "BCRA · Central de Deudores (snapshot del legajo)";
    private const string SourceCheques  = "BCRA · Cheques rechazados (snapshot del legajo)";
    private const string SourceArca     = "ARCA · constancia (snapshot del legajo)";

    /// <summary>Worst BCRA situación, current or within pf_bcra_ventana_meses, against pf_bcra_sit_rechazo.</summary>
    internal static PersonFilterResult BcraSituacion(PersonEvaluationInput input)
    {
        const string id = "bcra_situacion";
        var p     = input.Parameters;
        var deuda = input.Bcra?.Deuda;

        if (deuda is null || deuda.Estado == BcraDeudaConsultaEstado.LookupFailed)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                BcraDeudoresNoDisponible(input) + " La situación no se puede evaluar.",
                SourceDeudores, new()
                {
                    ["umbralRechazo"] = F(p.BcraSituacionRechazo),
                    ["fechaConsulta"] = BcraFechaConsultaInput(input)
                });
        }

        if (deuda.Estado == BcraDeudaConsultaEstado.NoDebtReported)
        {
            // A real answer — the BCRA said the CUIT has no debt — so it is a verified Ok, never SinDatos.
            return Filter(id, PersonFilterOutcomes.Ok, null,
                NoRegistraDeudas(input) + " Sin historial crediticio: no equivale a bajo riesgo.",
                SourceDeudores, new()
                {
                    ["estado"]         = "sin_historial",
                    ["fechaConsulta"]  = BcraFechaConsultaInput(input),
                    ["cuotasVigentes"] = "0",
                    ["umbralRechazo"]  = F(p.BcraSituacionRechazo)
                });
        }

        var current = deuda.SituacionMaxima
            ?? (deuda.EntidadesDetalle is { Count: > 0 } detalle ? detalle.Max(e => e.Situacion) : null);

        var window  = HistoryWithin(deuda, p.BcraVentanaMeses);
        int? worstInWindow = window.Count > 0 ? window.Max(h => h.SituacionMaxima) : null;
        int? worst = (current, worstInWindow) switch
        {
            (null, null)     => null,
            ({ } c, null)    => c,
            (null, { } w)    => w,
            ({ } c, { } w)   => Math.Max(c, w)
        };

        var inputs = new Dictionary<string, string?>
        {
            ["situacionActual"]      = current is { } cur ? F(cur) : null,
            ["peorSituacionVentana"] = worstInWindow is { } wiw ? F(wiw) : null,
            ["ventanaMeses"]         = F(p.BcraVentanaMeses),
            ["mesesInformados"]      = F(window.Count),
            ["periodoConsultado"]    = deuda.PeriodoConsultado,
            ["umbralRechazo"]        = F(p.BcraSituacionRechazo)
        };

        if (worst is null)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "La Central de Deudores informó deuda pero sin situación legible.", SourceDeudores, inputs);
        }

        return worst >= p.BcraSituacionRechazo
            ? Filter(id, PersonFilterOutcomes.Rechazo, null,
                $"Situación {worst} en la Central de Deudores (actual o en los últimos {p.BcraVentanaMeses} meses); " +
                $"el umbral de rechazo es {p.BcraSituacionRechazo}.", SourceDeudores, inputs)
            : Filter(id, PersonFilterOutcomes.Ok, null,
                $"Peor situación {worst}, actual o en los últimos {p.BcraVentanaMeses} meses; por debajo del umbral " +
                $"de rechazo ({p.BcraSituacionRechazo}).", SourceDeudores, inputs);
    }

    /// <summary>
    /// Situación 5 within pf_bcra_eventos_ventana_meses, situación jurídica, refinanciación or proceso judicial:
    /// a strong alert. Data under review (enRevision) alone is a plain alert ("impugnado").
    /// </summary>
    internal static PersonFilterResult BcraEventosGraves(PersonEvaluationInput input)
    {
        const string id = "bcra_eventos_graves";
        var p     = input.Parameters;
        var deuda = input.Bcra?.Deuda;

        if (deuda is null || deuda.Estado == BcraDeudaConsultaEstado.LookupFailed)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                BcraDeudoresNoDisponible(input), SourceDeudores,
                new()
                {
                    ["ventanaMeses"]  = F(p.BcraEventosVentanaMeses),
                    ["fechaConsulta"] = BcraFechaConsultaInput(input)
                });
        }

        if (deuda.Estado == BcraDeudaConsultaEstado.NoDebtReported)
        {
            return Filter(id, PersonFilterOutcomes.Ok, null,
                NoRegistraDeudas(input) + " Sin situación 5, situación jurídica, refinanciaciones ni proceso judicial.",
                SourceDeudores,
                new()
                {
                    ["estado"]        = "sin_historial",
                    ["fechaConsulta"] = BcraFechaConsultaInput(input),
                    ["ventanaMeses"]  = F(p.BcraEventosVentanaMeses)
                });
        }

        var window       = HistoryWithin(deuda, p.BcraEventosVentanaMeses);
        var detalle      = deuda.EntidadesDetalle ?? [];
        var situacion5   = window.Any(h => h.SituacionMaxima >= 5) || deuda.SituacionMaxima >= 5;
        var juridica     = deuda.TieneSituacionJuridica || detalle.Any(e => e.SituacionJuridica);
        var refinanciada = detalle.Any(e => e.Refinanciaciones);
        var judicial     = deuda.TieneProcesoJudicial || detalle.Any(e => e.ProcesoJud);
        var enRevision   = deuda.EnRevision || detalle.Any(e => e.EnRevision);

        var inputs = new Dictionary<string, string?>
        {
            ["situacion5"]          = situacion5 ? "true" : "false",
            ["situacionJuridica"]   = juridica ? "true" : "false",
            ["refinanciaciones"]    = refinanciada ? "true" : "false",
            ["procesoJudicial"]     = judicial ? "true" : "false",
            ["enRevision"]          = enRevision ? "true" : "false",
            ["ventanaMeses"]        = F(p.BcraEventosVentanaMeses),
            ["mesesInformados"]     = F(window.Count),
            ["coberturaMarcadores"] = "ultimo_periodo"
        };

        // What the BCRA actually returns: 12 months of situación history, and the per-entity flags of the
        // latest period only. The evidence says so, so a 24-month window is never read as fully covered.
        var coverage = $" Cobertura: {window.Count} meses de historial de situación dentro de la ventana de " +
                       $"{p.BcraEventosVentanaMeses}; situación jurídica, refinanciaciones y proceso judicial solo " +
                       "del último período informado.";

        var events = new List<string>();
        if (situacion5)   events.Add("situación 5");
        if (juridica)     events.Add("situación jurídica");
        if (refinanciada) events.Add("refinanciación");
        if (judicial)     events.Add("proceso judicial");

        if (events.Count > 0)
        {
            return Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte,
                "Eventos graves en la Central de Deudores: " + string.Join(", ", events) + "." +
                (enRevision ? " Hay datos impugnados (en revisión)." : string.Empty) + coverage,
                SourceDeudores, inputs);
        }

        if (enRevision)
        {
            return Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Normal,
                "Datos impugnados en la Central de Deudores (en revisión)." + coverage, SourceDeudores, inputs);
        }

        return Filter(id, PersonFilterOutcomes.Ok, null,
            "Sin situación 5, situación jurídica, refinanciaciones ni proceso judicial." + coverage,
            SourceDeudores, inputs);
    }

    /// <summary>
    /// The Argentine date the Central de Deudores answered: the deuda block's own FetchedAt, else the legajo's
    /// BcraDataUpdatedAt (a block stored without FetchedAt deserialises to the default). Null when neither is known.
    /// </summary>
    internal static DateOnly? BcraConsultaDate(PersonEvaluationInput input)
    {
        var fetched = input.Bcra?.Deuda?.FetchedAt;
        var utc = fetched is { } at && at > DateTime.MinValue ? at : input.BcraUpdatedAt;
        return utc is { } instant ? DateOnly.FromDateTime(ArgentineTime.ToLocal(instant)) : null;
    }

    /// <summary>The <see cref="BcraConsultaDate"/> as an ISO input value, null when unknown.</summary>
    private static string? BcraFechaConsultaInput(PersonEvaluationInput input)
        => BcraConsultaDate(input) is { } date ? F(date) : null;

    /// <summary>The verified "no debt" sentence of a NoDebtReported answer, with the query date when known.</summary>
    private static string NoRegistraDeudas(PersonEvaluationInput input)
        => BcraConsultaDate(input) is { } date
            ? $"No registra deudas en el sistema financiero (Central de Deudores, consulta del {date:dd/MM/yyyy})."
            : "No registra deudas en el sistema financiero (Central de Deudores).";

    /// <summary>
    /// Why the Central de Deudores cannot be read — two different causes, both SinDatos: the legajo has no
    /// snapshot at all, or it holds a query that FAILED (LookupFailed). A clean "no registra" answer never gets
    /// here. Shared by the filters, the indicators and the coverage statement so they all name the same cause.
    /// </summary>
    internal static string BcraDeudoresNoDisponible(PersonEvaluationInput input)
    {
        if (input.Bcra?.Deuda is null)
            return $"El legajo no tiene consulta a la Central de Deudores del BCRA. Actualizá BCRA {input.BcraRefreshTarget}.";

        return BcraConsultaDate(input) is { } date
            ? $"La consulta a la Central de Deudores del BCRA del {date:dd/MM/yyyy} falló. Actualizá BCRA {input.BcraRefreshTarget}."
            : $"La consulta a la Central de Deudores del BCRA falló. Actualizá BCRA {input.BcraRefreshTarget}.";
    }

    /// <summary>The same distinction as <see cref="BcraDeudoresNoDisponible"/>, for the rejected-cheques query.</summary>
    internal static string BcraChequesNoDisponible(PersonEvaluationInput input)
    {
        var cheques = input.Bcra?.Cheques;
        if (cheques is null)
            return $"El legajo no tiene consulta de cheques rechazados del BCRA. Actualizá BCRA {input.BcraRefreshTarget}.";

        return cheques.FetchedAt > DateTime.MinValue
            ? $"La consulta de cheques rechazados del BCRA del {ArgentineTime.ToLocal(cheques.FetchedAt):dd/MM/yyyy} falló. " +
              $"Actualizá BCRA {input.BcraRefreshTarget}."
            : $"La consulta de cheques rechazados del BCRA falló. Actualizá BCRA {input.BcraRefreshTarget}.";
    }

    /// <summary>
    /// Rejected cheques. A SIN FONDOS cheque still unpaid → suggested rejection; one already paid but rejected
    /// within pf_cheques_pagados_alerta_meses → alert; older paid ones → Ok. A formal-defect rejection says
    /// nothing about solvency and is reported as an informative Ok. Any other unpaid causal → rejection.
    /// </summary>
    internal static PersonFilterResult BcraChequesImpagos(PersonEvaluationInput input, DateOnly asOf)
    {
        const string id = "bcra_cheques_impagos";
        var cheques = input.Bcra?.Cheques;
        var months  = input.Parameters.ChequesPagadosAlertaMeses;

        if (cheques is null || cheques.Estado == BcraChequesConsultaEstado.LookupFailed)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                BcraChequesNoDisponible(input), SourceCheques);
        }

        if (cheques.Estado == BcraChequesConsultaEstado.NoChequesReported)
        {
            return Filter(id, PersonFilterOutcomes.Ok, null,
                "Sin cheques rechazados informados.", SourceCheques,
                new() { ["totalRechazados"] = "0", ["totalImpagos"] = "0" });
        }

        var detalle = cheques.Detalle ?? [];
        static bool IsSinFondos(BcraChequeDetalle d) => d.Causal?.Contains("FONDOS", StringComparison.OrdinalIgnoreCase) == true;
        static bool IsFormal(BcraChequeDetalle d) => d.Causal?.Contains("FORMAL", StringComparison.OrdinalIgnoreCase) == true;
        static bool IsPaid(BcraChequeDetalle d) => d.Pagado || d.FechaPago is not null;

        var since          = asOf.AddMonths(-months);
        var unpaid         = detalle.Where(d => !IsFormal(d) && !IsPaid(d)).ToList();
        var recentPaid     = detalle.Where(d => IsSinFondos(d) && IsPaid(d)
                                && d.FechaRechazo is { } at && DateOnly.FromDateTime(at) >= since).ToList();
        var formalCount    = detalle.Count(IsFormal);

        var inputs = new Dictionary<string, string?>
        {
            ["totalRechazados"]          = F(cheques.TotalRechazados),
            ["totalImpagos"]             = F(cheques.TotalImpagos),
            ["montoTotalImpago"]         = F(cheques.MontoTotalImpago),
            ["impagosComputados"]        = F(unpaid.Count),
            ["sinFondosPagadosRecientes"] = F(recentPaid.Count),
            ["defectosFormales"]         = F(formalCount),
            ["ventanaPagadosMeses"]      = F(months),
            ["fechaUltimoRechazo"]       = cheques.FechaUltimoRechazo is { } last ? F(DateOnly.FromDateTime(last)) : null
        };

        var formalNote = formalCount > 0 ? $" {formalCount} rechazo(s) por defectos formales (informativo)." : string.Empty;

        // The detail is the only place the causal and the payment live. Without it, the BCRA's own total of
        // unpaid cheques still decides — the fallback this filter always had.
        if (detalle.Count == 0 && cheques.TotalImpagos > 0)
        {
            return Filter(id, PersonFilterOutcomes.Rechazo, null,
                $"{cheques.TotalImpagos} cheque(s) rechazado(s) sin pagar por {Pesos(cheques.MontoTotalImpago)} (sin detalle de causal).",
                SourceCheques, inputs);
        }

        if (unpaid.Count > 0)
        {
            return Filter(id, PersonFilterOutcomes.Rechazo, null,
                $"{unpaid.Count} cheque(s) rechazado(s) sin pagar por {Pesos(unpaid.Sum(d => d.Monto))}." + formalNote,
                SourceCheques, inputs);
        }

        if (recentPaid.Count > 0)
        {
            return Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Normal,
                $"{recentPaid.Count} cheque(s) rechazado(s) sin fondos en los últimos {months} meses, ya pagado(s)." + formalNote,
                SourceCheques, inputs);
        }

        return Filter(id, PersonFilterOutcomes.Ok, null,
            (cheques.TotalRechazados > 0
                ? $"{cheques.TotalRechazados} cheque(s) rechazado(s), ninguno impago ni sin fondos reciente."
                : "Sin cheques rechazados impagos.") + formalNote,
            SourceCheques, inputs);
    }

    /// <summary>A sucesión → suggested rejection.</summary>
    internal static PersonFilterResult ArcaSucesion(PersonEvaluationInput input)
    {
        const string id = "arca_sucesion";

        if (input.Arca?.EsSucesion is not { } esSucesion)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "El snapshot de ARCA no está en el legajo o no informa si la CUIT es una sucesión.", SourceArca);
        }

        var inputs = new Dictionary<string, string?> { ["esSucesion"] = esSucesion ? "true" : "false" };

        return esSucesion
            ? Filter(id, PersonFilterOutcomes.Rechazo, null, "La CUIT corresponde a una sucesión.", SourceArca, inputs)
            : Filter(id, PersonFilterOutcomes.Ok, null, "La CUIT no es una sucesión.", SourceArca, inputs);
    }

    /// <summary>
    /// Whether the CUIT is active, from the estadoClave stored in the legajo's ARCA snapshot: ACTIVO → Ok; any
    /// other value → suggested rejection, with ARCA's raw status in the evidence; none stored (a snapshot read
    /// before the key status was kept, or ARCA did not send it) → SinDatos.
    /// </summary>
    internal static PersonFilterResult ArcaCuitActivo(PersonEvaluationInput input)
    {
        const string id = "arca_cuit_activo";
        const int maxStatusLength = 40;

        var raw = input.Arca?.EstadoClave?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "El snapshot de ARCA del legajo no tiene el estado de la clave: actualizá ARCA o verificá " +
                "en ARCA que la CUIT esté activa.", SourceArca);
        }

        // ARCA's own text, bounded: it is not identifying data, but it is external input.
        var status = raw.Length > maxStatusLength ? raw[..maxStatusLength] : raw;
        var readAt = input.Arca!.EstadoClaveConsultadoAt is { } at
            ? DateOnly.FromDateTime(ArgentineTime.ToLocal(at))
            : (DateOnly?)null;

        var inputs = new Dictionary<string, string?>
        {
            ["estadoClave"]  = status,
            ["consultadoEl"] = readAt is { } d ? F(d) : null
        };
        var when = readAt is { } r ? $" (consultado el {r:dd/MM/yyyy})" : string.Empty;

        return string.Equals(status, "ACTIVO", StringComparison.OrdinalIgnoreCase)
            ? Filter(id, PersonFilterOutcomes.Ok, null, $"ARCA informa la clave de la CUIT como ACTIVO{when}.",
                SourceArca, inputs)
            : Filter(id, PersonFilterOutcomes.Rechazo, null,
                $"ARCA informa la clave de la CUIT como \"{status}\"{when}: no está activa.", SourceArca, inputs);
    }

    /// <summary>
    /// Activity age, below pf_antiguedad_min_meses → alert. Reads PersonLegajo.ActivityStartDate (written from
    /// ARCA on every legajo ARCA write), else the same rule over the legajo's ARCA snapshot
    /// (<see cref="ArcaActivityStart"/>). BOTH are the earliest registration period ARCA reports — getPersona_v2
    /// has no inscription date — and ARCA re-dates periods (F.150 updates, nomenclator migrations), so the real
    /// start can only be EARLIER than the derived one: an APPROXIMATION that can only make the activity look
    /// younger. Below pf_antiguedad_rechazo_meses it is therefore capped at a strong alert and never suggests
    /// rejection on its own.
    /// </summary>
    internal static PersonFilterResult AntiguedadActividad(PersonEvaluationInput input, DateOnly asOf)
    {
        const string id = "antiguedad_actividad";
        var p = input.Parameters;

        DateOnly? start = null;
        string source;

        if (input.ActivityStartDate is { } fromLegajo)
        {
            start  = DateOnly.FromDateTime(fromLegajo);
            source = "ARCA · período de alta más antiguo (aproximación, legajo)";
        }
        else
        {
            if (ArcaActivityStart.From(input.Arca) is { } proxy)
            {
                start = DateOnly.FromDateTime(proxy);
            }

            source = "ARCA · período de alta más antiguo de impuestos y actividades (aproximación)";
        }

        // A registry fact, but an approximate one (see the summary).
        const string kind = PersonEvidenceKinds.Verificado;

        if (start is not { } since)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "No hay fecha de inicio de actividad en el legajo ni períodos de alta en el snapshot de ARCA.",
                source, new() { ["minimoMeses"] = F(p.AntiguedadMinMeses), ["rechazoMeses"] = F(p.AntiguedadRechazoMeses) },
                kind);
        }

        var months = Math.Max(0, MonthsBetween(since, asOf));
        var inputs = new Dictionary<string, string?>
        {
            ["inicio"]       = F(since),
            ["aproximado"]   = "true",
            ["meses"]        = F(months),
            ["rechazoMeses"] = F(p.AntiguedadRechazoMeses),
            ["minimoMeses"]  = F(p.AntiguedadMinMeses)
        };

        const string basis =
            " (aproximada por el período de alta más antiguo en ARCA; el inicio real puede ser anterior)";

        if (months < p.AntiguedadRechazoMeses)
        {
            // SEAM: a Rechazo here is justified ONLY by a date whose origin is a true inscription / start of
            // activity date, which can be trusted not to postdate the real start. No source provides one today
            // (getPersona_v2 has none; ActivityStartDate is the earliest-periodo approximation), so every date
            // stops at a strong alert. A future real date must arrive with its origin — and only that origin may
            // return Rechazo from this branch.
            return Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte,
                $"Antigüedad de la actividad: {months} meses{basis}, por debajo de {p.AntiguedadRechazoMeses}. " +
                "Al ser una aproximación no sugiere rechazo: confirmá la fecha de inicio.", source, inputs, kind);
        }

        return months < p.AntiguedadMinMeses
            ? Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Normal,
                $"Antigüedad de la actividad: {months} meses{basis}; el mínimo es {p.AntiguedadMinMeses}.", source, inputs, kind)
            : Filter(id, PersonFilterOutcomes.Ok, null,
                $"Antigüedad de la actividad: {months} meses{basis}.", source, inputs, kind);
    }

    /// <summary>Monotributo only: last-12-month sales above the category's cap → strong alert.</summary>
    internal static PersonFilterResult MonotributoTope(PersonEvaluationInput input, IncomeFacts facts)
    {
        const string id = "monotributo_tope";
        const string source = "ARCA (categoría) + ventas del legajo";

        var category = FiscalRegimes.NormalizeMonotributoCategory(input.MonotributoCategory)
            ?? FiscalRegimes.NormalizeMonotributoCategory(input.Arca?.MonotributoCategoria);

        if (category is null || !MonotributoCategoryCaps.AnnualCaps.TryGetValue(category, out var cap))
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "La categoría de monotributo no está en el legajo.", source);
        }

        var inputs = new Dictionary<string, string?>
        {
            ["categoria"]         = category,
            ["topeAnual"]         = F(cap),
            ["tablaVigenteDesde"] = F(MonotributoCategoryCaps.VigenteDesde),
            ["tablaProvisional"]  = "true"
        };

        if (facts.SalesLevel is not { } sales)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                $"Categoría {category}, pero el legajo no tiene ventas de los últimos 12 meses para comparar con el tope.",
                source, inputs);
        }

        var kind = PersonEvidenceKinds.Combine(sales.Sources.Select(PersonEvidenceKinds.OfIncomeSource).Append(PersonEvidenceKinds.Verificado));
        inputs["ventas12Meses"] = F(sales.Nominal);
        inputs["anualizado"]    = sales.Annualized ? "true" : "false";
        inputs["fuentes"]       = string.Join(",", sales.Sources);

        return sales.Nominal > cap
            ? Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte,
                $"Ventas de 12 meses por {Pesos(sales.Nominal)}, por encima del tope de la categoría {category} " +
                $"({Pesos(cap)}, tabla ARCA vigente desde {MonotributoCategoryCaps.VigenteDesde:dd/MM/yyyy}).",
                source, inputs, kind)
            : Filter(id, PersonFilterOutcomes.Ok, null,
                $"Ventas de 12 meses por {Pesos(sales.Nominal)}, dentro del tope de la categoría {category} " +
                $"({Pesos(cap)}, tabla ARCA vigente desde {MonotributoCategoryCaps.VigenteDesde:dd/MM/yyyy}).",
                source, inputs, kind);
    }

    /// <summary>
    /// The MiPyME certificate, from the entity's LUFE snapshot (<c>Entidad.certificado_pyme.hasta</c>). A
    /// persona humana's certificate expires on 30/04: from pf_certificado_alerta_desde_mes through
    /// pf_certificado_alerta_hasta_mes, one that expired on the 30/04 of the evaluation year is an alert of
    /// vigencia (renewal pending) rather than expired; after that window it is expired.
    /// Missing or expired otherwise → Rechazo when pf_requiere_certificado_mipyme is on, else an alert. No LUFE
    /// data → SinDatos.
    /// </summary>
    internal static PersonFilterResult CertificadoMipyme(PersonEvaluationInput input, DateOnly asOf)
    {
        const string id = "certificado_mipyme";
        const string source = "LUFE · Registro MiPyME (snapshot de la entidad)";
        var p = input.Parameters;

        // Null only on parameters frozen before the key existed: no upper bound, the rule of that time.
        var hastaMes = p.CertificadoAlertaHastaMes ?? 12;

        var inputs = new Dictionary<string, string?>
        {
            ["requiereCertificado"] = p.RequiereCertificadoMipyme ? "true" : "false",
            ["alertaDesdeMes"]      = F(p.CertificadoAlertaDesdeMes),
            ["alertaHastaMes"]      = F(hastaMes)
        };
        var window = $"ventana de renovación: meses {p.CertificadoAlertaDesdeMes} a {hastaMes}";

        if (input.Lufe?.Entidad is not { } entidad)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "La entidad no tiene datos de LUFE: el certificado MiPyME no se puede verificar.", source, inputs);
        }

        var missingOutcome = p.RequiereCertificadoMipyme ? PersonFilterOutcomes.Rechazo : PersonFilterOutcomes.Alerta;

        if (entidad.CertificadoPyme is not { } certificado)
        {
            return Filter(id, missingOutcome, PersonSeverities.Normal,
                "LUFE no informa certificado MiPyME para la entidad.", source, inputs);
        }

        inputs["categoria"] = certificado.Categoria;
        inputs["desde"]     = certificado.Desde;
        inputs["hasta"]     = certificado.Hasta;

        if (ParseLufeDate(certificado.Hasta) is not { } hasta)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "LUFE informa un certificado MiPyME sin fecha de vigencia legible.", source, inputs);
        }

        if (hasta >= asOf)
        {
            return Filter(id, PersonFilterOutcomes.Ok, null,
                $"Certificado MiPyME vigente hasta {hasta:dd/MM/yyyy}" +
                (certificado.Categoria is { } cat ? $" (categoría {cat})." : "."), source, inputs);
        }

        var expiredThisApril = hasta.Year == asOf.Year && hasta.Month == 4;

        if (expiredThisApril && asOf.Month >= p.CertificadoAlertaDesdeMes && asOf.Month <= hastaMes)
        {
            return Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Normal,
                $"Certificado MiPyME vencido el {hasta:dd/MM/yyyy}: renovación pendiente (vigencia; {window}). " +
                "Confirmá la renovación.", source, inputs);
        }

        return Filter(id, missingOutcome, PersonSeverities.Normal,
            $"Certificado MiPyME vencido el {hasta:dd/MM/yyyy}" +
            (expiredThisApril ? $", fuera de la {window}." : "."), source, inputs);
    }

    /// <summary>
    /// Responsable inscripto (and any regime with Ganancias): the F.711 monto consumido against the reference
    /// floor pf_consumo_mensual_minimo_miles, CPI-indexed by <see cref="ResolveConsumoFloor"/>. Declaring less than
    /// the floor is an alert, and the flujo uses the floor instead. No F.711 → SinDatos; floor 0 (undefined) → Ok
    /// without comparison.
    /// </summary>
    internal static PersonFilterResult ConsumoDeclarado(PersonEvaluationInput input, IncomeFacts facts)
    {
        const string id = "consumo_declarado";
        const string source = "Ganancias (F.711) · monto consumido";
        var piso  = ResolveConsumoFloor(input, facts);
        var floor = piso.Monthly;

        var inputs = new Dictionary<string, string?>();
        AddConsumoFloorInputs(inputs, piso);

        if (facts.Ganancias?.MontoConsumido is not { } montoConsumido)
        {
            return Filter(id, PersonFilterOutcomes.SinDatos, null,
                "El legajo no tiene el monto consumido de una DDJJ de Ganancias (F.711).", source, inputs);
        }

        var monthly = Math.Round(montoConsumido / 12m, 2);
        inputs["periodo"]               = facts.Ganancias.Period;
        inputs["consumoDeclaradoMensual"] = F(monthly);

        if (floor <= 0m)
        {
            return Filter(id, PersonFilterOutcomes.Ok, null,
                $"Consumo declarado {Pesos(monthly)} por mes; sin canasta de referencia definida para comparar.", source, inputs);
        }

        return monthly < floor
            ? Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Normal,
                $"Consumo declarado por debajo de la canasta de referencia: {Pesos(monthly)} por mes contra " +
                $"{ConsumoFloorTexto(piso)}. El flujo usa la canasta.", source, inputs)
            : Filter(id, PersonFilterOutcomes.Ok, null,
                $"Consumo declarado {Pesos(monthly)} por mes, por encima de la canasta de referencia: {ConsumoFloorTexto(piso)}.",
                source, inputs);
    }

    /// <summary>Parses a LUFE date (ISO date, ISO date-time or dd/MM/yyyy); null when unreadable.</summary>
    internal static DateOnly? ParseLufeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff",
                            "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:sszzz"];
        if (DateTime.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exact))
        {
            return DateOnly.FromDateTime(exact);
        }

        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var any)
            ? DateOnly.FromDateTime(any.Date)
            : null;
    }

    /// <summary>The creditor-vs-fisco indicator, restated as a pre-filter so it shows with the other flags.</summary>
    private static PersonFilterResult AcreedorVsFiscoFilter(PersonIndicatorResult indicator)
    {
        const string id = "ventas_acreedor_vs_fisco";
        const string source = "Ventas del legajo (certificación / manifestación vs. DDJJ fiscales)";

        if (indicator.Status == PersonIndicatorStatuses.SinDatos)
            return Filter(id, PersonFilterOutcomes.SinDatos, null, indicator.Evidence, source,
                new Dictionary<string, string?>(indicator.Inputs), PersonEvidenceKinds.Declarado);

        return indicator.Severity == PersonSeverities.Fuerte
            ? Filter(id, PersonFilterOutcomes.Alerta, PersonSeverities.Fuerte, indicator.Evidence, source,
                new Dictionary<string, string?>(indicator.Inputs), PersonEvidenceKinds.Declarado)
            : Filter(id, PersonFilterOutcomes.Ok, null, indicator.Evidence, source,
                new Dictionary<string, string?>(indicator.Inputs), PersonEvidenceKinds.Declarado);
    }

    /// <summary>
    /// The situación history entries within <paramref name="months"/> of the most recent informed period
    /// (the reference is the latest period the BCRA returned, not today, so a stale snapshot does not empty
    /// the window).
    /// </summary>
    private static IReadOnlyList<BcraSituacionPeriodo> HistoryWithin(BcraDeudaData deuda, int months)
    {
        var history = (deuda.HistorialSituaciones ?? [])
            .Select(h => (Entry: h, Month: ParseYearMonth(h.Periodo)))
            .Where(h => h.Month is not null)
            .ToList();

        var reference = ParseYearMonth(deuda.PeriodoConsultado)
            ?? (history.Count > 0 ? history.Max(h => h.Month!.Value) : (DateOnly?)null);

        if (reference is not { } latest)
            return [];

        var latestIndex = MonthIndex(latest);
        return history
            .Where(h =>
            {
                var age = latestIndex - MonthIndex(h.Month!.Value);
                return age >= 0 && age < months;
            })
            .Select(h => h.Entry)
            .ToList();
    }
}
