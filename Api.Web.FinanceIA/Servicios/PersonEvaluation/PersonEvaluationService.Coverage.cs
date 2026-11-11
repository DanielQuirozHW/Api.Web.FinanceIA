// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>The documentary coverage index: separate from any score, and it blocks nothing by itself.</summary>
public static partial class PersonEvaluationService
{
    /// <summary>
    /// The monthly DDJJ whose gap may be seasonal in agro annual mode (engine 0.7): a recency gap made only of
    /// off-season months is not stale.
    /// </summary>
    private static readonly IReadOnlySet<string> MonthlyReturnTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb
    };

    /// <summary>Statuses of a document that will never become usable without a new upload.</summary>
    private static readonly IReadOnlySet<string> DeadStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        LegajoStatuses.Blocked, LegajoStatuses.Failed
    };

    /// <remarks>
    /// The lines are the tenant's matrix for the regime (<see cref="PersonEvaluationInput.ExpectedDocuments"/>,
    /// engine 0.10), or the code default when the caller states none. A line's recency cut is its own
    /// <see cref="LegajoExpectedDocument.MaxAgeMonths"/>, else its class key (annual returns:
    /// pf_ddjj_anual_antiguedad_max_meses; everything else: pf_doc_antiguedad_max_meses); the consent and the
    /// projected flow have none. The agro seasonal rule stays code-driven and applies to whichever cut is in force.
    /// </remarks>
    /// <param name="filters">
    /// The pre-filters of the same evaluation: a statement that restates a filter's missing data reads its
    /// outcome, so the two can never disagree.
    /// </param>
    internal static PersonCoverageResult Coverage(
        PersonEvaluationInput input, IncomeFacts facts, DateOnly asOf, IReadOnlyList<PersonFilterResult> filters)
    {
        var p = input.Parameters;

        var expectedLines = input.ExpectedDocuments ?? LegajoExpectedDocuments.For(input.FiscalRegime);

        var items = expectedLines
            .Select(expected =>
            {
                var matching = input.Documents.Where(d => expected.IsSatisfiedBy(d.DocumentType)).ToList();
                var ready    = matching.Where(d => d.LegajoStatus == LegajoStatuses.Ready).ToList();
                var pending  = matching.Where(d => d.LegajoStatus != LegajoStatuses.Ready
                                                && (d.LegajoStatus is null || !DeadStatuses.Contains(d.LegajoStatus)))
                                       .ToList();

                var maxAge = LegajoDocumentAgeClasses.EffectiveMaxAge(expected, p);

                var latest = ready.Select(d => d.PeriodEnd).OfType<DateOnly>().DefaultIfEmpty().Max();
                DateOnly? latestEnd = ready.Any(d => d.PeriodEnd is not null) ? latest : null;
                int? age = latestEnd is { } end ? Math.Max(0, MonthsBetween(end, asOf)) : null;
                var isStale = age is { } a && maxAge is { } max && a > max;

                bool? seasonalGap = null;
                if (isStale
                    && input.Agro is { Active: true }
                    && MonthlyReturnTypes.Contains(expected.DocumentType)
                    && latestEnd is { } last
                    && IsOffSeasonGap(last, asOf, facts.OffSeasonMonths))
                {
                    isStale     = false;
                    seasonalGap = true;
                }

                return new PersonCoverageItem
                {
                    DocumentType       = expected.DocumentType,
                    Required           = expected.Required,
                    Alternatives       = expected.Alternatives,
                    Present            = ready.Count > 0,
                    ReadyDocumentIds   = ready.Select(d => d.DocumentId).ToList(),
                    PendingDocumentIds = pending.Select(d => d.DocumentId).ToList(),
                    LatestPeriodEnd    = latestEnd,
                    AgeMonths          = age,
                    MaxAgeMonths       = maxAge,
                    IsStale            = isStale,
                    SeasonalGap        = seasonalGap
                };
            })
            .ToList();

        var requiredTotal   = items.Count(i => i.Required);
        var requiredPresent = items.Count(i => i.Required && i.Present);

        var snapshots = new List<PersonCoverageSnapshot>
        {
            Snapshot("BCRA", input.BcraUpdatedAt, input.AsOfUtc, p.InformeMaxDias),
            Snapshot("ARCA", input.ArcaUpdatedAt, input.AsOfUtc, p.InformeMaxDias),
            Snapshot("LUFE", input.LufeUpdatedAt, input.AsOfUtc, p.InformeMaxDias)
        };

        return new PersonCoverageResult
        {
            FiscalRegime    = input.FiscalRegime,
            Items           = items,
            RequiredPresent = requiredPresent,
            RequiredTotal   = requiredTotal,
            Index           = requiredTotal == 0 ? 1m : Math.Round((decimal)requiredPresent / requiredTotal, 4),
            Snapshots       = snapshots,
            CannotConclude  = CannotConclude(input, facts, filters)
        };
    }

    /// <summary>
    /// True when every month that could have been declared after <paramref name="latestEnd"/> — through the month
    /// before <paramref name="asOf"/>'s, since a month's DDJJ is filed the next — falls in an off-season
    /// calendar month. False without an off-season.
    /// </summary>
    private static bool IsOffSeasonGap(DateOnly latestEnd, DateOnly asOf, IReadOnlySet<int> offSeasonMonths)
    {
        if (offSeasonMonths.Count == 0)
            return false;

        var first = MonthIndex(latestEnd) + 1;
        var last  = MonthIndex(asOf) - 1;

        return Enumerable.Range(first, Math.Max(0, last - first + 1))
            .All(index => offSeasonMonths.Contains(index % 12 + 1));
    }

    private static PersonCoverageSnapshot Snapshot(string source, DateTime? updatedAt, DateTime asOfUtc, int maxDays)
    {
        int? age = updatedAt is { } at ? Math.Max(0, (int)(asOfUtc - at).TotalDays) : null;

        return new PersonCoverageSnapshot
        {
            Source     = source,
            UpdatedAt  = updatedAt,
            AgeDays    = age,
            MaxAgeDays = maxDays,
            IsStale    = age is not { } a || a > maxDays
        };
    }

    /// <summary>The conclusions this legajo does not support, said out loud.</summary>
    private static IReadOnlyList<PersonCannotConclude> CannotConclude(
        PersonEvaluationInput input, IncomeFacts facts, IReadOnlyList<PersonFilterResult> filters)
    {
        var statements = new List<PersonCannotConclude>();
        var isMonotributo = input.FiscalRegime == FiscalRegimes.Monotributo;

        if (input.FiscalRegime == FiscalRegimes.Desconocido)
        {
            statements.Add(new("regimen_desconocido",
                "Régimen fiscal desconocido: se usó la matriz de documentos de Exento/Desconocido."));
        }

        if (isMonotributo && !facts.HasInvoicedSales)
        {
            statements.Add(new("flujo_solo_declarado",
                "Monotributista sin facturación en el legajo: el flujo es solo declarado."));
        }

        if (isMonotributo && facts.ManifestacionResultado is null)
        {
            statements.Add(new("sin_cuadro_resultados",
                "Monotributista sin cuadro de resultados: el flujo no se puede estimar."));
        }

        if (isMonotributo)
        {
            statements.Add(new("gastos_personales_no_disponibles",
                "El cuadro de resultados no separa gastos personales: el consumo es el piso de referencia del tenant."));
        }

        if (isMonotributo && input.Parameters.ConsumoMensualMinimo <= 0m)
        {
            statements.Add(new("consumo_sin_definir",
                "Sin F.711 y sin piso de consumo definido para el tenant: el flujo neto de consumo no se puede calcular."));
        }

        if (!isMonotributo && facts.Ganancias?.Renta3ra is null)
        {
            statements.Add(new("sin_resultado_actividad",
                "Sin renta de tercera categoría en Ganancias: el resultado de la actividad no se puede calcular."));
        }

        if (!isMonotributo && facts.Ganancias?.Renta3ra is not null)
        {
            statements.Add(new("amortizaciones_intereses_no_disponibles",
                "Las amortizaciones y los intereses no se leen del legajo: el flujo no los suma."));
        }

        if (facts.SalesLevel is null)
        {
            statements.Add(new("sin_ventas_12_meses",
                "Sin ventas suficientes para los últimos 12 meses: nivel, tendencia y tope de categoría no se pueden evaluar."));
        }
        else if (facts.CpiReferenceMonth is null)
        {
            statements.Add(new("ventas_solo_nominales",
                "Sin serie de IPC del BCRA: las ventas se muestran solo en pesos nominales y la tendencia real no se mide."));
        }

        var deuda = input.Bcra?.Deuda;
        if (deuda is null || deuda.Estado == BcraDeudaConsultaEstado.LookupFailed)
        {
            statements.Add(new("bcra_no_disponible",
                BcraDeudoresNoDisponible(input) + " La situación crediticia no se puede concluir."));
        }

        if (input.Bcra?.Cheques is null || input.Bcra.Cheques.Estado == BcraChequesConsultaEstado.LookupFailed)
        {
            statements.Add(new("cheques_no_disponibles",
                BcraChequesNoDisponible(input)));
        }

        // Only when the CUIT filter could not read the key status: a snapshot that says ACTIVO (or anything
        // else) has concluded, one way or the other.
        if (filters.Any(f => f.Id == "arca_cuit_activo" && f.Outcome == PersonFilterOutcomes.SinDatos))
        {
            statements.Add(new("cuit_estado_no_disponible",
                "El estado de la CUIT en ARCA no está en el legajo: verificalo antes de decidir."));
        }

        if (input.InstallmentAmount is null)
        {
            statements.Add(new("cuota_no_informada",
                "No se informó la cuota de la operación: cuota/ingreso y cobertura del servicio de deuda no se pueden calcular."));
        }
        else
        {
            statements.Add(new("impuestos_no_disponibles",
                "Los impuestos no se leen del legajo: la cobertura del servicio de deuda no los descuenta."));
            statements.Add(new("cuotas_vigentes_estimadas",
                "Las cuotas vigentes se estiman desde el saldo de la Central de Deudores, no desde los contratos."));
        }

        if (input.Lufe?.Entidad is null)
        {
            statements.Add(new("lufe_no_disponible",
                "La entidad no tiene datos de LUFE: el certificado MiPyME no se puede verificar."));
        }

        return statements;
    }
}
