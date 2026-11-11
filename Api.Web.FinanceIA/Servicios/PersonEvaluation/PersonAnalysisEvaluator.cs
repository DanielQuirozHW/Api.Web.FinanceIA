// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// Everything a persona humana evaluation does around the pure <see cref="PersonEvaluationService"/>: the
/// readiness checks of the solicitante and of each fiador, the one live BCRA retry, the legajo reads and the
/// evaluation itself, frozen into the column values the analysis stores. The ONE implementation behind
/// POST /api/person-analyses and the in-place re-evaluation of POST /api/analyses/{id}/rescore, so the two
/// cannot check, read or refuse differently.
/// </summary>
/// <remarks>
/// <para>
/// Built with <c>new</c> from its caller's own dependencies, like <c>AnalysisScoringStage</c>: it holds no
/// state of its own, and the callers keep their constructors. It logs through the caller's logger.
/// </para>
/// <para>
/// PRIVACY: logs carry ids and counts only — never the person's name or CUIT.
/// </para>
/// </remarks>
public sealed class PersonAnalysisEvaluator
{
    /// <summary>Completes "Actualizá BCRA …" in a fiador's SinDatos texts: his snapshot is refreshed on his legajo.</summary>
    private const string FiadorBcraRefreshTarget = "en el legajo del fiador";

    private static readonly JsonSerializerOptions SnapshotJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly IPersonLegajoRepository             _personLegajoRepository;
    private readonly ILegajoDocumentRepository           _legajoDocumentRepository;
    private readonly ILegajoReconciliationRepository     _reconciliationRepository;
    private readonly IBcraService                        _bcraService;
    private readonly IFiadorRepository                   _fiadorRepository;
    private readonly IPersonLegajoSnapshotRefreshService _snapshotRefreshService;
    private readonly ILogger                             _logger;

    /// <summary>Initializes a new instance of <see cref="PersonAnalysisEvaluator"/>.</summary>
    public PersonAnalysisEvaluator(
        IPersonLegajoRepository             personLegajoRepository,
        ILegajoDocumentRepository           legajoDocumentRepository,
        ILegajoReconciliationRepository     reconciliationRepository,
        IBcraService                        bcraService,
        IFiadorRepository                   fiadorRepository,
        IPersonLegajoSnapshotRefreshService snapshotRefreshService,
        ILogger                             logger)
    {
        _personLegajoRepository   = personLegajoRepository;
        _legajoDocumentRepository = legajoDocumentRepository;
        _reconciliationRepository = reconciliationRepository;
        _bcraService              = bcraService;
        _fiadorRepository         = fiadorRepository;
        _snapshotRefreshService   = snapshotRefreshService;
        _logger                   = logger;
    }

    // ── Readiness ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the solicitante can be evaluated: a persona humana with a legajo in this tenant, an active
    /// data-processing authorization and a Ready document with an extractor. Returns the refusal, or the
    /// legajo and its live documents.
    /// </summary>
    public async Task<(PersonEvaluationRefusal? Refusal, PersonLegajo? Legajo, List<Document>? Documents)> CheckSolicitanteAsync(
        Entity entity, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.PersonType != PersonTypes.Humana || entity.PersonLegajoId is not { } legajoId)
        {
            return (new PersonEvaluationRefusal(400, "Bad Request", PersonEvaluationErrorCodes.EntityNotPerson,
                "La entidad no es una persona humana con legajo: analizala con un balance (POST /api/analyses)."), null, null);
        }

        var summary = await _legajoDocumentRepository.GetLegajoSummaryAsync(legajoId, tenantId, ct).ConfigureAwait(false);
        var legajo = summary is null
            ? null
            : await _personLegajoRepository.GetByIdAsync(legajoId, tenantId, ct).ConfigureAwait(false);
        if (summary is null || legajo is null)
        {
            return (new PersonEvaluationRefusal(400, "Bad Request", PersonEvaluationErrorCodes.EntityNotPerson,
                "La entidad no tiene un legajo de persona humana en este tenant."), null, null);
        }

        if (!summary.HasActiveConsent)
        {
            return (new PersonEvaluationRefusal(409, "Conflict", PersonEvaluationErrorCodes.ConsentRequired,
                "La persona no tiene autorización de tratamiento de datos vigente. Registrala en su legajo antes de evaluarla."),
                null, null);
        }

        var documents = (await _legajoDocumentRepository.GetLegajoDocumentsAsync(legajoId, tenantId, ct)
            .ConfigureAwait(false)).Where(d => !d.IsDeleted).ToList();
        if (!PersonEvaluationService.HasEvaluableDocument(documents))
        {
            return (new PersonEvaluationRefusal(409, "Conflict", PersonEvaluationErrorCodes.LegajoNotReady,
                "El legajo no tiene ningún documento listo (Ready) para evaluar. Esperá a que termine la extracción o aprobá la revisión."),
                null, null);
        }

        return (null, legajo, documents);
    }

    /// <summary>
    /// Whether one fiador can be evaluated: an active fiador of THIS entity who is not the solicitante, with a
    /// legajo that has an active consent and a Ready document with an extractor. Returns the refusal, or the
    /// fiador and his live legajo documents.
    /// </summary>
    public async Task<(PersonEvaluationRefusal? Refusal, Fiador? Fiador, List<Document>? Documents)> CheckFiadorAsync(
        Guid fiadorId, Entity entity, string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var fiador = await _fiadorRepository.GetByIdAsync(fiadorId, tenantId, ct).ConfigureAwait(false);
        if (fiador is null || fiador.EntityId != entity.EntityId)
            return (FiadorNotReady(fiadorId, PersonEvaluationErrorCodes.FiadorReasons.NotFound, "El fiador no es de esta entidad."), null, null);

        var digits = LegajoCuit.Digits(fiador.CUIT);
        if ((digits is not null && digits == LegajoCuit.Digits(entity.CUIT))
            || (fiador.PersonLegajoId is { } own && own == entity.PersonLegajoId))
        {
            return (new PersonEvaluationRefusal(409, "Conflict", PersonEvaluationErrorCodes.FiadorIsSolicitante,
                "El fiador es el propio solicitante: no puede garantizar su propia operación.", FiadorId: fiadorId), null, null);
        }

        if (!string.Equals(fiador.Status, "Active", StringComparison.Ordinal))
            return (FiadorNotReady(fiadorId, PersonEvaluationErrorCodes.FiadorReasons.NotActive, "El fiador no está activo."), null, null);

        if (fiador.PersonLegajoId is not { } legajoId)
        {
            return (FiadorNotReady(fiadorId, PersonEvaluationErrorCodes.FiadorReasons.NoLegajo,
                "El fiador no tiene legajo. Crealo desde el fiador antes de evaluarlo."), null, null);
        }

        var summary = await _legajoDocumentRepository.GetLegajoSummaryAsync(legajoId, tenantId, ct).ConfigureAwait(false);
        if (summary is null)
        {
            return (FiadorNotReady(fiadorId, PersonEvaluationErrorCodes.FiadorReasons.NoLegajo,
                "El fiador no tiene legajo en este tenant. Crealo desde el fiador antes de evaluarlo."), null, null);
        }

        if (!summary.HasActiveConsent)
        {
            return (FiadorNotReady(fiadorId, PersonEvaluationErrorCodes.FiadorReasons.ConsentRequired,
                "El fiador no tiene autorización de tratamiento de datos vigente. Registrala en su legajo antes de evaluarlo."),
                null, null);
        }

        var documents = (await _legajoDocumentRepository.GetLegajoDocumentsAsync(legajoId, tenantId, ct).ConfigureAwait(false))
            .Where(d => !d.IsDeleted).ToList();
        if (!PersonEvaluationService.HasEvaluableDocument(documents))
        {
            return (FiadorNotReady(fiadorId, PersonEvaluationErrorCodes.FiadorReasons.NoReadyDocument,
                "El legajo del fiador no tiene ningún documento listo (Ready) para evaluar: hace falta la manifestación de " +
                "bienes o Bienes Personales, entre otros, con la extracción terminada."), null, null);
        }

        return (null, fiador, documents);
    }

    /// <summary>409 fiador_legajo_not_ready, naming the fiador and why.</summary>
    public static PersonEvaluationRefusal FiadorNotReady(Guid fiadorId, string reason, string detail)
        => new(409, "Conflict", PersonEvaluationErrorCodes.FiadorLegajoNotReady, detail, fiadorId, reason);

    // ── BCRA ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The legajo's BCRA snapshot is written when the entity is created and on refresh-bcra, never by the
    /// evaluation. A Central de Deudores or cheques query that failed at that moment (an outage, the BCRA rate
    /// limit) would otherwise freeze SinDatos into every evaluation of the person, which on screen reads close to
    /// a clean "no registra". So when the snapshot is missing or either query failed, the BCRA is asked ONCE more
    /// and the answer is kept only when its Central de Deudores part is real (DebtFound or NoDebtReported): it is
    /// then saved on the entity and the legajo exactly as refresh-bcra saves it, and the evaluation reads it.
    /// </summary>
    /// <remarks>
    /// The BCRA call is fault-isolated: a failure leaves the snapshot as it was and the evaluation reports
    /// SinDatos for what is still missing. The save is not — it shares the DbContext the analysis is written
    /// with, so a failed save fails the request before anything of the analysis is written.
    /// </remarks>
    /// <returns>True when a fresh answer replaced the snapshot.</returns>
    public async Task<bool> RefreshUnusableBcraSnapshotAsync(
        Entity entity, PersonLegajo legajo, string tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(legajo);

        if (IsBcraSnapshotUsable(TryDeserialize<BcraData>(legajo.BcraDataJson)) || string.IsNullOrWhiteSpace(entity.CUIT))
            return false;

        BcraData? fresh;
        try
        {
            fresh = await _bcraService.GetBcraDataForAnalysisAsync(entity.CUIT, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "BCRA re-query failed for the persona humana evaluation of legajo {PersonLegajoId}, tenant {TenantId}; " +
                "the stored snapshot is kept.",
                legajo.PersonLegajoId, tenantId);
            return false;
        }

        if (fresh?.Deuda is not { Estado: not BcraDeudaConsultaEstado.LookupFailed })
        {
            _logger.LogWarning(
                "BCRA re-query for the persona humana evaluation of legajo {PersonLegajoId}, tenant {TenantId}: the " +
                "Central de Deudores is still unavailable; the stored snapshot is kept.",
                legajo.PersonLegajoId, tenantId);
            return false;
        }

        var now = DateTime.UtcNow;
        entity.BcraDataJson      = JsonSerializer.Serialize(fresh);
        entity.BcraDataUpdatedAt = now;
        legajo.BcraDataJson      = entity.BcraDataJson;
        legajo.BcraDataUpdatedAt = now;
        legajo.UpdatedAt         = now;

        await _personLegajoRepository
            .SaveEntityWithLegajoAsync(entity, entityIsNew: false, legajo, legajoIsNew: false, CancellationToken.None)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "BCRA snapshot of legajo {PersonLegajoId} (entity {EntityId}) refreshed for a persona humana evaluation, " +
            "tenant {TenantId}: deuda={DeudaEstado}, cheques={ChequesEstado}",
            legajo.PersonLegajoId, entity.EntityId, tenantId, fresh.Deuda.Estado, fresh.Cheques?.Estado);
        return true;
    }

    /// <summary>
    /// Whether both BCRA queries of a snapshot gave a real answer — data found or nothing reported. A missing
    /// snapshot, a missing block or a LookupFailed is not usable.
    /// </summary>
    public static bool IsBcraSnapshotUsable(BcraData? bcra) => PersonSnapshotWriter.IsBcraSnapshotUsable(bcra);

    // ── Evaluation ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates the solicitante on his legajo as it is NOW, with <paramref name="parameters"/>, and freezes the
    /// result into the column values of his row and of the AnalysisResults row. Call it after
    /// <see cref="RefreshUnusableBcraSnapshotAsync"/>, which may have replaced the legajo's BCRA snapshot.
    /// </summary>
    /// <param name="expectedDocuments">
    /// The tenant's expected documents for the legajo's regime, as resolved NOW (engine 0.10): the coverage index
    /// measures against them and the sources freeze them. Null reads the code default and freezes nothing.
    /// </param>
    public async Task<PersonSolicitanteEvaluation> EvaluateSolicitanteAsync(
        Entity entity,
        PersonLegajo legajo,
        IReadOnlyList<Document> documents,
        PersonOperationTerms terms,
        PersonEvaluationParameters parameters,
        PersonExpectedDocumentsForRegime? expectedDocuments,
        string tenantId,
        DateTime now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(legajo);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(parameters);

        var (runs, corrections, inventory, incomeRowCount, incomesView) =
            await LoadLegajoInputsAsync(legajo.PersonLegajoId, documents, tenantId, ct).ConfigureAwait(false);

        var bcra = TryDeserialize<BcraData>(legajo.BcraDataJson);
        var arca = TryDeserialize<ArcaFiscalData>(legajo.ArcaSnapshotJson);
        var lufe = TryDeserialize<Models.Lufe.LufeData>(entity.LufeDataJson);
        var (macro, macroSource) = await ResolveMacroAsync(bcra, tenantId, ct).ConfigureAwait(false);
        var agro = PersonAgroModeResolver.Resolve(arca, terms, parameters);

        var input = new PersonEvaluationInput
        {
            AsOfUtc                  = now,
            FiscalRegime             = FiscalRegimes.IsKnown(legajo.FiscalRegime) ? legajo.FiscalRegime : FiscalRegimes.Desconocido,
            MonotributoCategory      = legajo.MonotributoCategory,
            ActivityStartDate        = legajo.ActivityStartDate,
            Bcra                     = bcra,
            BcraUpdatedAt            = legajo.BcraDataUpdatedAt,
            Arca                     = arca,
            ArcaUpdatedAt            = legajo.ArcaDataUpdatedAt,
            Lufe                     = lufe,
            LufeUpdatedAt            = entity.LufeDataUpdatedAt,
            Inventory                = inventory,
            Incomes                  = incomesView,
            Documents                = EvaluationDocuments(documents),
            InflationSeries          = macro?.InflacionMensualSerie,
            Parameters               = parameters,
            RequestedAmount          = terms.RequestedAmount,
            Currency                 = terms.Currency,
            InstallmentAmount        = terms.InstallmentAmount,
            CuotasVigentesDeclaradas = terms.CuotasVigentesDeclaradas,
            Agro                     = agro,
            ExpectedDocuments        = expectedDocuments?.Lines
        };

        var evaluation = PersonEvaluationService.Evaluate(input);
        var sources    = PersonEvaluationService.Sources(
            documents, runs, corrections, legajo, entity.LufeDataUpdatedAt, inventory, incomeRowCount,
            macro?.FetchedAt, macroSource, parameters, agro, expectedDocuments);

        var documentVersionsJson = PersonEvaluationJson.Serialize(sources);
        var filtersJson          = PersonEvaluationJson.Serialize(evaluation.Filters);
        var indicatorsJson       = PersonEvaluationJson.Serialize(evaluation.Indicators);
        var coverageJson         = PersonEvaluationJson.Serialize(evaluation.Coverage);

        var bcraComplete = IsBcraSnapshotUsable(bcra);

        // The legajo's snapshots — and the entity's LUFE one, which carries the MiPyME certificate — are
        // frozen on the result exactly as the balance pipeline freezes the entity's, so /bcra/cheques and
        // /context read the BCRA, ARCA and LUFE data this evaluation saw.
        return new PersonSolicitanteEvaluation
        {
            Evaluation           = evaluation,
            Sources              = sources,
            Inventory            = inventory,
            Macro                = macro,
            MacroSource          = macroSource,
            DocumentVersionsJson = documentVersionsJson,
            FiltersJson          = filtersJson,
            IndicatorsJson       = indicatorsJson,
            CoverageJson         = coverageJson,
            BcraDataJson         = legajo.BcraDataJson,
            MacroDataJson        = macro is null ? null : JsonSerializer.Serialize(macro),
            FiscalDataJson       = legajo.ArcaSnapshotJson,
            LufeDataJson         = entity.LufeDataJson,
            AiInputHash          = Sha256(documentVersionsJson),
            AiOutputHash         = Sha256(filtersJson + indicatorsJson + coverageJson),
            EnrichmentStatus     = bcraComplete
                ? nameof(BcraEnrichmentOutcome.Complete)
                : nameof(BcraEnrichmentOutcome.Degraded),
            EnrichmentNotes      = bcraComplete
                ? null
                : "BCRA snapshot of the legajo (deuda or cheques) missing or failed, also on the re-query at evaluation; those filters read SinDatos."
        };
    }

    /// <summary>
    /// Evaluates one fiador on his own legajo: the same one live BCRA retry the solicitante gets, through the
    /// legajo-scoped refresh service, then the same inputs, read the same way.
    /// </summary>
    public async Task<PersonFiadorEvaluation> EvaluateFiadorAsync(
        PersonFiadorTerms fiadorTerms,
        Fiador fiador,
        IReadOnlyList<Document> documents,
        PersonOperationTerms terms,
        string? solicitanteCuit,
        PersonSolicitanteEvaluation solicitante,
        PersonEvaluationParameters parameters,
        string tenantId,
        DateTime now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fiadorTerms);
        ArgumentNullException.ThrowIfNull(fiador);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(solicitante);

        var legajoId = fiador.PersonLegajoId!.Value;

        var refresh = await _snapshotRefreshService.RefreshUnusableBcraAsync(legajoId, tenantId, ct).ConfigureAwait(false);
        var legajo = refresh.Legajo
            ?? await _personLegajoRepository.GetByIdAsync(legajoId, tenantId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The legajo {legajoId} of fiador {fiador.FiadorId} vanished during the evaluation.");

        var (runs, corrections, inventory, incomeRowCount, incomesView) =
            await LoadLegajoInputsAsync(legajoId, documents, tenantId, ct).ConfigureAwait(false);

        var macro = solicitante.Macro;
        var person = new PersonEvaluationInput
        {
            AsOfUtc                  = now,
            FiscalRegime             = FiscalRegimes.IsKnown(legajo.FiscalRegime) ? legajo.FiscalRegime : FiscalRegimes.Desconocido,
            MonotributoCategory      = legajo.MonotributoCategory,
            ActivityStartDate        = legajo.ActivityStartDate,
            Bcra                     = TryDeserialize<BcraData>(legajo.BcraDataJson),
            BcraUpdatedAt            = legajo.BcraDataUpdatedAt,
            Arca                     = TryDeserialize<ArcaFiscalData>(legajo.ArcaSnapshotJson),
            ArcaUpdatedAt            = legajo.ArcaDataUpdatedAt,
            Lufe                     = null,   // the MiPyME certificate is the borrower's, not the guarantor's
            LufeUpdatedAt            = null,
            Inventory                = inventory,
            Incomes                  = incomesView,
            Documents                = EvaluationDocuments(documents),
            InflationSeries          = macro?.InflacionMensualSerie,
            Parameters               = parameters,
            RequestedAmount          = fiadorTerms.AmountAtCharge,
            Currency                 = terms.Currency,
            InstallmentAmount        = terms.InstallmentAmount,
            CuotasVigentesDeclaradas = fiadorTerms.CuotasVigentesDeclaradas,
            BcraRefreshTarget        = FiadorBcraRefreshTarget
        };

        var evaluation = PersonEvaluationService.FiadorEvaluation(new FiadorEvaluationInput
        {
            Person                = person,
            SolicitanteCuit       = solicitanteCuit,
            SolicitanteInventory  = solicitante.Inventory,
            GuaranteeType         = fiadorTerms.GuaranteeType,
            SpouseConsent         = fiadorTerms.SpouseConsent == true,
            HousingInopposable    = fiadorTerms.HousingInopposable == true,
            GuaranteesGivenAmount = fiadorTerms.GuaranteesGivenAmount ?? 0m
        });

        var sources = PersonEvaluationService.Sources(
            documents, runs, corrections, legajo, null, inventory, incomeRowCount, macro?.FetchedAt, solicitante.MacroSource, parameters);

        _logger.LogInformation(
            "Fiador {FiadorId} (legajo {PersonLegajoId}) evaluated for tenant {TenantId}: suggestion={Suggestion}, " +
            "assets={AssetCount}, counted={CountedCount}, bcraRefresh={BcraRefresh}",
            fiador.FiadorId, legajoId, tenantId, evaluation.Suggestion, evaluation.Coverage.Assets.Count,
            evaluation.Coverage.Assets.Count(a => a.Treatment == FiadorAssetTreatments.Computado), refresh.Status);

        return new PersonFiadorEvaluation
        {
            Evaluation           = evaluation,
            Sources              = sources,
            PersonLegajoId       = legajoId,
            DocumentVersionsJson = PersonEvaluationJson.Serialize(sources),
            FiltersJson          = PersonEvaluationJson.Serialize(evaluation.Filters),
            IndicatorsJson       = PersonEvaluationJson.Serialize(evaluation.Indicators),
            CoverageJson         = PersonEvaluationJson.Serialize(evaluation.Coverage)
        };
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What an evaluation reads of a legajo besides its snapshots: the current extraction runs and their
    /// corrections, and the reconciled inventory and incomes — only what the live documents support. A soft-delete
    /// triggers a rebuild that removes the rows, but that rebuild never fails the delete; if it did not run, a
    /// deleted document's figures would still be in the tables while missing from DocumentVersionsJson. Filtering
    /// here keeps the two in step.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, LegajoExtraction> Runs, IReadOnlyList<LegajoCorrection> Corrections,
        LegajoInventoryResponse Inventory, int IncomeRowCount, LegajoIncomesResponse Incomes)> LoadLegajoInputsAsync(
        Guid legajoId, IReadOnlyList<Document> documents, string tenantId, CancellationToken ct)
    {
        var documentIds = documents.Select(d => d.DocumentId).ToList();
        var runs = await _legajoDocumentRepository.GetCurrentExtractionsAsync(documentIds, tenantId, ct)
            .ConfigureAwait(false);
        var corrections = await _legajoDocumentRepository
            .GetCorrectionsAsync(runs.Values.Select(r => r.ExtractionId).ToList(), tenantId, ct)
            .ConfigureAwait(false);

        var liveIds = documentIds.ToHashSet();
        var items = (await _reconciliationRepository.GetItemsAsync(legajoId, tenantId, ct).ConfigureAwait(false))
            .Where(i => (i.MarketValueDocumentId is null || liveIds.Contains(i.MarketValueDocumentId.Value))
                     && (i.FiscalValueDocumentId is null || liveIds.Contains(i.FiscalValueDocumentId.Value)))
            .ToList();
        var incomes = (await _reconciliationRepository.GetIncomesAsync(legajoId, tenantId, ct).ConfigureAwait(false))
            .Where(i => liveIds.Contains(i.DocumentId))
            .ToList();

        return (runs, corrections,
                LegajoReconciliationReadModel.BuildInventory(legajoId, items, new Dictionary<Guid, string>()),
                incomes.Count,
                LegajoReconciliationReadModel.BuildIncomes(legajoId, incomes));
    }

    private static List<PersonEvaluationDocument> EvaluationDocuments(IEnumerable<Document> documents)
        => documents
            .Select(d => new PersonEvaluationDocument(
                d.DocumentId, d.DocumentType, d.LegajoStatus,
                d.PeriodEnd is { } end ? DateOnly.FromDateTime(end) : null))
            .ToList();

    /// <summary>
    /// The CPI series for the real sales: the one already in the legajo's BCRA snapshot, else one cached
    /// read of the BCRA macro variables. Fault-isolated — without it the sales are nominal only.
    /// </summary>
    private async Task<(BcraMacroData? Macro, string? Source)> ResolveMacroAsync(
        BcraData? bcra, string tenantId, CancellationToken cancellationToken)
    {
        if (bcra?.Macro is { InflacionMensualSerie.Count: > 0 } snapshot)
            return (snapshot, "legajo");

        try
        {
            var macro = await _bcraService.GetMacroAsync(cancellationToken).ConfigureAwait(false);
            return macro is { InflacionMensualSerie.Count: > 0 } ? (macro, "bcra") : (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "BCRA macro read failed for a persona humana evaluation of tenant {TenantId}; sales stay nominal.",
                tenantId);
            return (null, null);
        }
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, SnapshotJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
