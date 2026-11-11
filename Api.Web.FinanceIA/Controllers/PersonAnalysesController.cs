// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// The persona humana analysis (M10 Phase 4a "v0", DDL 072): an evaluation of the applicant on their
/// legajo — pre-filters, indicators and documentary coverage — with NO score, NO risk level and NO model
/// call. The analysis it creates then follows the normal flow: decision, approval limits, pre-approval and
/// quorum, credit line. Tenant-scoped throughout.
///
/// <para>
/// PRIVACY. Nothing here reaches Claude: the evaluation is deterministic C#, the chat and the PDF export
/// refuse a persona analysis, and logs carry ids only — never the person's name or CUIT.
/// </para>
/// </summary>
[ApiController]
[Route("api/person-analyses")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class PersonAnalysesController : ControllerBase
{
    /// <summary>Analyses.AiModel of a persona analysis: no model answered it.</summary>
    public const string NoModel = "none";

    /// <summary>Analyses.PromptVersion of a persona analysis: the engine, not a prompt.</summary>
    public const string PromptVersion = "pf-" + PersonEvaluationService.Version;

    /// <summary>
    /// AnalysisResults.ValidationStatus as STORED for a persona analysis. The column is the balance-sheet
    /// arithmetic check: it is NOT NULL and CK_AnalysisResults_ValidationStatus admits only Passed / Warning /
    /// Failed, so a row needs one of the three even though no balance was checked. Never exposed as such —
    /// see <see cref="ValidationStatusNotApplicable"/>.
    /// </summary>
    public const string StoredValidationStatus = "Passed";

    /// <summary>
    /// The ValidationStatus every API response carries for a persona analysis: empty, because the check does
    /// not apply. The stored "Passed" read as "Estado de validación: Aprobado" — plus an "Estado actualizado ·
    /// Aprobado" timeline event — on an analysis nobody had decided. The Portal hides both when it is empty.
    /// </summary>
    public const string ValidationStatusNotApplicable = "";

    /// <summary>The most fiadores one request may evaluate.</summary>
    public const int MaxFiadores = 10;

    /// <summary>The 400 detail of a negative cuotasVigentesDeclaradas, for the solicitante and for a fiador.</summary>
    private const string CuotasVigentesNegativas = "Las cuotas vigentes declaradas no pueden ser negativas.";

    private readonly ITenantContextAccessor         _tenantContextAccessor;
    private readonly IEntityRepository              _entityRepository;
    private readonly IPersonLegajoRepository        _personLegajoRepository;
    private readonly ILegajoDocumentRepository      _legajoDocumentRepository;
    private readonly ILegajoReconciliationRepository _reconciliationRepository;
    private readonly IPersonAnalysisRepository      _personAnalysisRepository;
    private readonly IUserRepository                _userRepository;
    private readonly IBcraService                   _bcraService;
    private readonly IFiadorRepository              _fiadorRepository;
    private readonly IPersonLegajoSnapshotRefreshService _snapshotRefreshService;
    private readonly IAuditService                  _auditService;
    private readonly IPersonExpectedDocumentsService _expectedDocuments;
    private readonly ILogger<PersonAnalysesController> _logger;

    /// <summary>Initializes a new instance of <see cref="PersonAnalysesController"/>.</summary>
    public PersonAnalysesController(
        ITenantContextAccessor         tenantContextAccessor,
        IEntityRepository              entityRepository,
        IPersonLegajoRepository        personLegajoRepository,
        ILegajoDocumentRepository      legajoDocumentRepository,
        ILegajoReconciliationRepository reconciliationRepository,
        IPersonAnalysisRepository      personAnalysisRepository,
        IUserRepository                userRepository,
        IBcraService                   bcraService,
        IFiadorRepository              fiadorRepository,
        IPersonLegajoSnapshotRefreshService snapshotRefreshService,
        IAuditService                  auditService,
        IPersonExpectedDocumentsService expectedDocuments,
        ILogger<PersonAnalysesController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _entityRepository         = entityRepository;
        _personLegajoRepository   = personLegajoRepository;
        _legajoDocumentRepository = legajoDocumentRepository;
        _reconciliationRepository = reconciliationRepository;
        _personAnalysisRepository = personAnalysisRepository;
        _userRepository           = userRepository;
        _bcraService              = bcraService;
        _fiadorRepository         = fiadorRepository;
        _snapshotRefreshService   = snapshotRefreshService;
        _auditService             = auditService;
        _expectedDocuments        = expectedDocuments;
        _logger                   = logger;
    }

    // ── POST /api/person-analyses ───────────────────────────────────────────────

    /// <summary>
    /// Evaluates a persona humana applicant on their legajo and creates the analysis, synchronously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// QUOTA: a persona evaluation consumes ONE analysis unit of the tenant's monthly plan, exactly like
    /// POST /api/analyses — the same [BillingQuota] filter gates it, and the Analyses row it writes is what
    /// the usage count counts.
    /// </para>
    /// <para>
    /// Refusals, all before anything is written: 400 <c>validation_error</c>; 404 <c>entity_not_found</c>;
    /// 400 <c>entity_not_person</c> (not a persona humana with a legajo); 409 <c>consent_required</c> (no
    /// active data-processing authorization); 409 <c>legajo_not_ready</c> (no Ready document with an
    /// extractor — the consent alone does not count); 409 <c>person_analysis_open</c> (an earlier persona
    /// analysis of the entity still awaits its decision); 429 quota.
    /// </para>
    /// <para>
    /// FIADORES (M10 Phase 5a-2, optional): each one is evaluated on his own legajo and frozen on his own
    /// AnalysisParticipants row; their aggregate counter-guarantee coverage is recomputed from those rows on
    /// every read. Their refusals come after the solicitante's, still before anything is written: 400
    /// <c>validation_error</c> (empty or duplicate fiadorId, unknown guaranteeType, amountAtCharge ≤ 0 or above the
    /// requested amount, negative guaranteesGivenAmount, more than one Conyuge, more than
    /// <see cref="MaxFiadores"/>); 409 <c>fiador_is_solicitante</c>; 409 <c>fiador_legajo_not_ready</c> with the
    /// <c>fiadorId</c> and a <c>reason</c> (not_found, not_active, no_legajo, consent_required, no_ready_document).
    /// Without <c>fiadores</c> the endpoint behaves exactly as before.
    /// </para>
    /// <para>
    /// BCRA: when the legajo's snapshot is missing or holds a failed query, the BCRA is asked once more and a real
    /// answer replaces the snapshot on the entity and the legajo (see <see cref="PersonAnalysisEvaluator.RefreshUnusableBcraSnapshotAsync"/>).
    /// </para>
    /// </remarks>
    /// <param name="request">Entity, requested amount, currency and the optional cuota.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>201 Created with <see cref="AnalysisResultResponse"/> (kind PersonaHumana, with the evaluation).</returns>
    [HttpPost]
    [Authorize(Roles = Roles.AnalysisCreators)]
    [BillingQuota(BillingQuotaKind.Analysis)]
    [ProducesResponseType(typeof(AnalysisResultResponse), 201)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreatePersonAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContextAccessor.TenantContext?.TenantId;
        if (tenantId is null)
            return Problem(statusCode: 500, title: "Internal Server Error", detail: "Tenant context not resolved.");

        // ── Validate the request ──────────────────────────────────────────────────
        if (request is null || request.EntityId == Guid.Empty)
            return ValidationError("entityId", "La entidad es obligatoria.");
        if (request.RequestedAmount <= 0m)
            return ValidationError("requestedAmount", "El monto solicitado debe ser mayor a cero.");
        if (!Currencies.IsKnown(request.Currency))
            return ValidationError("currency", $"La moneda debe ser una de: {Currencies.KnownList}.");
        if (request.InstallmentAmount is <= 0m)
            return ValidationError("installmentAmount", "La cuota, si se informa, debe ser mayor a cero.");
        if (request.CuotasVigentesDeclaradas is < 0m)
            return ValidationError("cuotasVigentesDeclaradas", CuotasVigentesNegativas);
        if (request.CuotasPorAnio is { } cuotasPorAnio && !PersonEvaluationDefaults.AllowedCuotasPorAnio.Contains(cuotasPorAnio))
        {
            return ValidationError("cuotasPorAnio",
                $"La periodicidad de la cuota debe ser {PersonEvaluationDefaults.EsArList(PersonEvaluationDefaults.AllowedCuotasPorAnio)} " +
                "cuotas por año.");
        }

        var fiadorRequests = request.Fiadores ?? [];
        if (ValidateFiadores(fiadorRequests, request.RequestedAmount) is { } fiadorValidation)
            return fiadorValidation;

        // ── Entity and legajo ─────────────────────────────────────────────────────
        var entity = await _entityRepository.GetByIdAsync(request.EntityId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
            return CodedProblem(404, "Not Found", PersonEvaluationErrorCodes.EntityNotFound, "No se encontró la entidad.");

        var evaluator = Evaluator();
        var (solicitanteRefusal, legajo, documents) = await evaluator
            .CheckSolicitanteAsync(entity, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (solicitanteRefusal is not null)
            return Refusal(solicitanteRefusal);

        var legajoId = legajo!.PersonLegajoId;

        if (await _personAnalysisRepository.HasOpenPersonAnalysisAsync(entity.EntityId, tenantId, cancellationToken)
                .ConfigureAwait(false))
        {
            return CodedProblem(409, "Conflict", "person_analysis_open",
                "La entidad ya tiene un análisis de persona humana pendiente de decisión. Decidilo antes de evaluarla de nuevo.");
        }

        // ── Fiadores: each one ready on his own legajo ──────────────────────────────
        var fiadores = new List<(CreatePersonAnalysisFiadorRequest Request, Fiador Fiador, List<Document> Documents)>();
        foreach (var fiadorRequest in fiadorRequests)
        {
            var (refusal, fiador, fiadorDocuments) = await evaluator
                .CheckFiadorAsync(fiadorRequest.FiadorId, entity, tenantId, cancellationToken)
                .ConfigureAwait(false);
            if (refusal is not null)
                return Refusal(refusal);

            fiadores.Add((fiadorRequest, fiador!, fiadorDocuments!));
        }

        // ── Requesting user ───────────────────────────────────────────────────────
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository.GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);
        if (currentUser is null)
            return Unauthorized();

        // ── BCRA: one live retry when the legajo's snapshot is missing or failed ────
        await evaluator.RefreshUnusableBcraSnapshotAsync(entity, legajo, tenantId, cancellationToken).ConfigureAwait(false);

        // ── Evaluate the solicitante on the tenant's current pf_ parameters ─────────
        var configRows = await _personAnalysisRepository.GetEvaluationConfigAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var parameters = PersonEvaluationParameters.From(configRows);
        // ...and on its CURRENT expected-documents matrix for the legajo's regime (engine 0.10), frozen in the
        // sources. Never throws: a settings fault reads the code defaults, so it can never fail the POST.
        var expectedDocuments = await _expectedDocuments
            .ForAsync(tenantId, legajo!.FiscalRegime, cancellationToken)
            .ConfigureAwait(false);

        var now   = DateTime.UtcNow;
        var terms = new PersonOperationTerms(
            request.RequestedAmount, request.Currency!, request.InstallmentAmount, request.CuotasPorAnio, request.AgroMode,
            request.CuotasVigentesDeclaradas);
        var solicitante = await evaluator
            .EvaluateSolicitanteAsync(entity, legajo, documents!, terms, parameters, expectedDocuments, tenantId, now, cancellationToken)
            .ConfigureAwait(false);
        var evaluation = solicitante.Evaluation;

        // ── Fiadores: each one on his own legajo, frozen like the solicitante ──────
        var fiadorRows = new List<AnalysisParticipant>();
        var fiadorSuggestions = new List<string>();
        foreach (var (fiadorRequest, fiador, fiadorDocuments) in fiadores)
        {
            var fiadorTerms = new PersonFiadorTerms(
                fiador.FiadorId,
                fiadorRequest.AmountAtCharge ?? request.RequestedAmount,
                fiadorRequest.GuaranteeType!,
                fiadorRequest.SpouseConsent,
                fiadorRequest.HousingInopposable,
                fiadorRequest.GuaranteesGivenAmount,
                fiadorRequest.CuotasVigentesDeclaradas);

            var fiadorEvaluation = await evaluator
                .EvaluateFiadorAsync(fiadorTerms, fiador, fiadorDocuments, terms, entity.CUIT, solicitante, parameters,
                    tenantId, now, cancellationToken)
                .ConfigureAwait(false);

            fiadorRows.Add(new AnalysisParticipant
            {
                ParticipantId              = Guid.NewGuid(),
                TenantId                   = tenantId,
                AnalysisId                 = Guid.Empty,
                PersonLegajoId             = fiadorEvaluation.PersonLegajoId,
                Role                       = ParticipantRoles.Fiador,
                AmountAtCharge             = fiadorTerms.AmountAtCharge,
                Currency                   = terms.Currency,
                InstallmentAmount          = terms.InstallmentAmount,
                DocumentVersionsJson       = fiadorEvaluation.DocumentVersionsJson,
                FiltersJson                = fiadorEvaluation.FiltersJson,
                IndicatorsJson             = fiadorEvaluation.IndicatorsJson,
                CoverageJson               = fiadorEvaluation.CoverageJson,
                ScoreJson                  = null,
                EvaluationVersion          = PersonEvaluationService.Version,
                CreatedAt                  = now,
                FiadorId                   = fiador.FiadorId,
                GuaranteeType              = fiadorTerms.GuaranteeType,
                SpouseConsent              = fiadorTerms.SpouseConsent,
                HousingInopposable         = fiadorTerms.HousingInopposable,
                GuaranteesGivenAmount      = fiadorTerms.GuaranteesGivenAmount,
                // The declared cuotas vigentes are a term of the analysis: a re-evaluation reuses them (DDL 079).
                ExistingInstallmentsAmount = fiadorTerms.CuotasVigentesDeclaradas
            });
            fiadorSuggestions.Add(fiadorEvaluation.Evaluation.Suggestion);
        }

        // ── Persist: Analyses + AnalysisResults + AnalysisParticipants, as one unit ──
        var analysisId = Guid.NewGuid();
        var analysis = new Analysis
        {
            AnalysisId            = analysisId,
            TenantId              = tenantId,
            EntityId              = entity.EntityId,
            DocumentId            = null,
            AnalysisKind          = AnalysisKinds.PersonaHumana,
            RequestedBy           = currentUser.UserId,
            RequestedAmount       = request.RequestedAmount,
            Status                = AnalysisStatuses.PendingReview,
            FiscalYear            = Clases.Time.ArgentineTime.ToLocal(now).Year,
            AiModel               = NoModel,
            AiTemperature         = 0.00m,
            PromptVersion         = PromptVersion,
            SentToSGR             = false,
            ProcessingStartedAt   = now,
            ProcessingCompletedAt = now,
            ProcessingDurationMs  = 0,
            CreatedAt             = now
        };

        // The evaluation's snapshot columns are write-once here: every balance-only column (balance, ratios,
        // score, narrative, alerts) stays NULL. A re-evaluation (POST /api/analyses/{id}/rescore) overwrites
        // the same set in place and keeps the previous values in AnalysisRescores.
        var result = new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = analysisId,
            TenantId         = tenantId,
            BcraDataJson     = solicitante.BcraDataJson,
            MacroDataJson    = solicitante.MacroDataJson,
            FiscalDataJson   = solicitante.FiscalDataJson,
            LufeDataJson     = solicitante.LufeDataJson,
            AiInputHash      = solicitante.AiInputHash,
            AiOutputHash     = solicitante.AiOutputHash,
            ValidationStatus = StoredValidationStatus,
            EnrichmentStatus = solicitante.EnrichmentStatus,
            EnrichmentNotes  = solicitante.EnrichmentNotes,
            GeneratedAt      = now
        };

        var participant = new AnalysisParticipant
        {
            ParticipantId              = Guid.NewGuid(),
            TenantId                   = tenantId,
            AnalysisId                 = analysisId,
            PersonLegajoId             = legajoId,
            Role                       = ParticipantRoles.Solicitante,
            AmountAtCharge             = request.RequestedAmount,
            Currency                   = request.Currency!,
            InstallmentAmount          = request.InstallmentAmount,
            // The periodicity is a term of the operation: frozen as RESOLVED, so a re-evaluation keeps it even
            // when the tenant later changes pf_agro_cuotas_por_anio. The override as stated (null = detect).
            CuotasPorAnio              = (byte?)solicitante.Sources.Agro?.CuotasPorAnio,
            AgroModeOverride           = request.AgroMode,
            // The declared cuotas vigentes, as stated (null = estimate); a re-evaluation reuses them (DDL 079).
            ExistingInstallmentsAmount = request.CuotasVigentesDeclaradas,
            DocumentVersionsJson       = solicitante.DocumentVersionsJson,
            FiltersJson                = solicitante.FiltersJson,
            IndicatorsJson             = solicitante.IndicatorsJson,
            CoverageJson               = solicitante.CoverageJson,
            ScoreJson                  = null,
            EvaluationVersion          = PersonEvaluationService.Version,
            CreatedAt                  = now
        };

        foreach (var row in fiadorRows)
            row.AnalysisId = analysisId;

        await _personAnalysisRepository.CreateAsync(analysis, result, participant, fiadorRows, cancellationToken)
            .ConfigureAwait(false);

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       "Analysis.PersonEvaluated",
            entityType:   "Analysis",
            entityId:     analysisId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                resultId          = result.ResultId,
                personLegajoId    = legajoId,
                evaluationVersion = PersonEvaluationService.Version,
                suggestion        = evaluation.Suggestion,
                fiadorCount       = fiadorRows.Count,
                fiadorIds         = fiadorRows.Select(f => f.FiadorId).ToList()
            }),
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Persona humana evaluation {AnalysisId} (result {ResultId}) of legajo {PersonLegajoId} for tenant {TenantId}: " +
            "suggestion={Suggestion}, filters={FilterCount}, coverage={RequiredPresent}/{RequiredTotal}, " +
            "fiadores={FiadorCount}, fiadoresRechazoSugerido={FlaggedFiadores}",
            analysisId, result.ResultId, legajoId, tenantId, evaluation.Suggestion, evaluation.Filters.Count,
            evaluation.Coverage.RequiredPresent, evaluation.Coverage.RequiredTotal,
            fiadorRows.Count, fiadorSuggestions.Count(s => s == PersonSuggestions.RechazoSugerido));

        // The same pure reader GET, PATCH .../decision and the PDF go through, over the rows just written.
        var orderedFiadores = PersonEvaluationJson.FiadorRowsOf(fiadorRows);
        var decisionRules = PersonEvaluationJson.DecisionRules(configRows, participant, orderedFiadores);
        var fiadorIdentities = fiadores.ToDictionary(
            f => f.Fiador.FiadorId, f => new FiadorIdentity(f.Fiador.Denominacion, f.Fiador.CUIT));
        var response = new AnalysisResultResponse
        {
            ResultId         = result.ResultId,
            AnalysisId       = analysisId,
            TenantId         = tenantId,
            AnalysisKind     = AnalysisKinds.PersonaHumana,
            DocumentId       = Guid.Empty,
            EntityId         = entity.EntityId,
            EntityName       = entity.BusinessName,
            EntityCuit       = entity.CUIT,
            RequestedAmount  = analysis.RequestedAmount,
            FiscalYear       = analysis.FiscalYear,
            Status           = analysis.Status,
            RequestedBy      = currentUser.UserId.ToString(),
            RequestedByName  = currentUser.DisplayName,
            ValidationStatus = ValidationStatusNotApplicable,
            EnrichmentStatus = result.EnrichmentStatus,
            EnrichmentNotes  = result.EnrichmentNotes,
            GeneratedAt      = result.GeneratedAt,
            SolaFirmaAllowed               = decisionRules.SolaFirmaAllowed,
            MinCounterGuaranteeCoveragePct = decisionRules.MinCounterGuaranteeCoveragePct,
            PersonEvaluation = PersonEvaluationJson.ToDetailResponse(
                decisionRules, participant, PersonEvaluationJson.ToFiadorResponses(orderedFiadores, fiadorIdentities))
        };

        return Created($"/api/analyses/{result.ResultId}", response);
    }

    // ── Fiadores (M10 Phase 5a-2) ────────────────────────────────────────────────

    /// <summary>The shape checks of the fiadores, before any read: a 400 or null.</summary>
    private IActionResult? ValidateFiadores(IReadOnlyList<CreatePersonAnalysisFiadorRequest> fiadores, decimal requestedAmount)
    {
        if (fiadores.Count > MaxFiadores)
            return ValidationError("fiadores", $"Se pueden evaluar hasta {MaxFiadores} fiadores por análisis.");

        var seen = new HashSet<Guid>();
        for (var i = 0; i < fiadores.Count; i++)
        {
            var f = fiadores[i];
            if (f is null || f.FiadorId == Guid.Empty)
                return ValidationError($"fiadores[{i}].fiadorId", "El fiador es obligatorio.");
            if (!seen.Add(f.FiadorId))
                return ValidationError($"fiadores[{i}].fiadorId", "El fiador está repetido en la solicitud.");
            if (!GuaranteeTypes.IsKnown(f.GuaranteeType))
            {
                return ValidationError($"fiadores[{i}].guaranteeType",
                    $"El tipo de garantía debe ser uno de: {string.Join(", ", GuaranteeTypes.Known)}.");
            }
            if (f.AmountAtCharge is <= 0m)
                return ValidationError($"fiadores[{i}].amountAtCharge", "El monto a cargo del fiador, si se informa, debe ser mayor a cero.");
            if (f.AmountAtCharge > requestedAmount)
                return ValidationError($"fiadores[{i}].amountAtCharge", "El monto a cargo del fiador no puede superar el monto solicitado.");
            if (f.GuaranteesGivenAmount is < 0m)
                return ValidationError($"fiadores[{i}].guaranteesGivenAmount", "Las garantías ya otorgadas no pueden ser negativas.");
            if (f.CuotasVigentesDeclaradas is < 0m)
                return ValidationError($"fiadores[{i}].cuotasVigentesDeclaradas", CuotasVigentesNegativas);
        }

        if (fiadores.Count(f => f.GuaranteeType == GuaranteeTypes.Conyuge) > 1)
            return ValidationError("fiadores", "Solo un fiador puede ser el cónyuge del solicitante.");

        return null;
    }

    /// <summary>The shared evaluation, over this request's repositories, logging through this controller's logger.</summary>
    private PersonAnalysisEvaluator Evaluator()
        => new(_personLegajoRepository, _legajoDocumentRepository, _reconciliationRepository, _bcraService,
               _fiadorRepository, _snapshotRefreshService, _logger);

    /// <summary>An evaluation refusal as ProblemDetails: errorCode, plus fiadorId and reason when it names a fiador.</summary>
    private ObjectResult Refusal(PersonEvaluationRefusal refusal)
    {
        var result = CodedProblem(refusal.StatusCode, refusal.Title, refusal.ErrorCode, refusal.Detail);
        var pd = (ProblemDetails)result.Value!;
        foreach (var (key, value) in refusal.Extensions())
            pd.Extensions[key] = value;
        return result;
    }

    private IActionResult ValidationError(string field, string message)
    {
        var pd = new ProblemDetails
        {
            Status   = 400,
            Title    = "Validation Error",
            Detail   = "Se produjeron uno o más errores de validación.",
            Type     = "https://serviciosfinancieros.api/errors/validation_error",
            Instance = HttpContext.Request.Path
        };
        pd.Extensions["errorCode"] = "validation_error";
        pd.Extensions["errors"]    = new[] { new { field, message } };
        return StatusCode(400, pd);
    }

    /// <summary>
    /// <see cref="ControllerBase.Problem(string, string, int?, string, string)"/> plus a stable
    /// <c>errorCode</c> extension the Portal can branch on: type, title, detail and traceId stay exactly
    /// what Problem() produces, so the code is purely additive.
    /// </summary>
    private ObjectResult CodedProblem(int status, string title, string errorCode, string detail)
    {
        var result = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = errorCode;
        return result;
    }
}
