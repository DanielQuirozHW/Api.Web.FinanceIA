// Project : Api.Web.FinanceIA
// Created : 2026-05-04
// Modified: 2026-11-01

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Clases.Formatting;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Servicios.Privacy;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using static Api.Web.FinanceIA.Servicios.AnalysisRuns.AnalysisPayloadJson;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Triggers AI-powered financial analysis and retrieves analysis results.
/// All endpoints require an internal JWT (<c>Internal</c> scheme).
/// Tenant isolation is enforced on every query and mutation.
/// </summary>
[ApiController]
[Route("api/analyses")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed partial class AnalysisController : ControllerBase
{
    // [dbo].[Analyses].Status values this controller writes or branches on, named because the
    // two-step decision flow compares against them in several places and a typo in one of them
    // would route a decision silently wrong. The other status literals in this file
    // ("Approved", "Rejected") are left inline: they are matched against the request payload and
    // reading them as the strings the API contract actually specifies is clearer there.
    //
    // Both are values of CK_Analyses_Status. 'PreApproved' was admitted by DDL 060; 'PendingReview'
    // has always been legal and is reused here as the return-for-rework destino rather than
    // inventing a second name for a state that already exists.
    // Aliased from AnalysisStatuses (Models/Financia) rather than spelled out here: since the quorum
    // engine landed, the same two values are also compared against by AnalysisApprovalQuorumService
    // and by AnalysisRepository's guarded confirm, so one source is the only way the three cannot
    // drift apart over a typo.
    private const string PreApprovedStatus   = AnalysisStatuses.PreApproved;
    private const string PendingReviewStatus = AnalysisStatuses.PendingReview;

    // Ceiling on AnalysisDecisionRequest.Operations. The blob lands in an nvarchar(max) column, so
    // this bounds a pathological payload rather than a real decision: a cupo is made up of a handful
    // of operations, and fifty is already far past anything an analyst enters by hand. Sized in the
    // spirit of DDL 055 — cap what can reach the column, at the only place that writes it.
    private const int MaxDecisionOperations = 50;

    // Audit actions for the two acts the quorum engine adds. Both are deliberately ABSENT from
    // AnalysisDecisionNotifier's routing switch, whose default arm emits nothing — a manager casting
    // one of several signatures is not news to anybody yet, and telling the analyst their
    // pre-approval was "confirmed" while it is still waiting would be worse than telling them
    // nothing. Re-notifying the remaining managers on each partial vote is a follow-up, not this
    // change; if it is ever added, it needs its own notification type rather than one of these.
    private const string ApprovalVotedAction     = "Analysis.ApprovalVoted";
    private const string ApprovalWithdrawnAction = "Analysis.ApprovalVoteWithdrawn";

    // Declared Content-Type for an .xlsx. Only a cheap early reject — see ChatAttachmentAsync.
    private const string XlsxMimeType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Ceiling on ChatRequest.AttachmentMarkdown. ExcelCashFlowParser's caps bound what
    // /chat/attachment can EMIT, but /chat takes that Markdown back as an ordinary JSON string, so
    // nothing stops a caller posting one the parser never produced. This is the only cap on that
    // path, and it bounds a pathological case, not a real workbook.
    //
    // Sized from ExcelCashFlowParser.MaxTotalCells (5.000 celdas). A Markdown table spends about
    // 3 chars of structure per cell (~15.000), leaving ~185.000 for content — ~37 chars per cell on
    // average, roughly 2,5x what a real cash flow spends (rótulos + importes ≈ 15) and comfortably
    // above the ~115 KB the parser can actually emit from a workbook of ordinary cells. At a
    // pessimistic 3 chars/token for es-AR that is ~67K tokens: one turn stays far inside the model's
    // window, and it cannot compound — the attachment never enters ChatMessages.Content and therefore
    // never enters a later turn's history. (Script 055 stores the capped Markdown in its own column on
    // the user turn so the analyst can read it back; nothing rebuilds a prompt from that column, and
    // history is rebuilt from Content alone.) Excel allows 32.767 chars in a SINGLE cell and the parser
    // caps no cell individually, so without this a legitimately-parsed workbook could reach hundreds of MB.
    //
    // "Cannot compound" is about HISTORY, and stays true. It is not true of the recorded token count:
    // ChatMessages.InputTokens is Anthropic's figure for the whole request, attachment included, and
    // the per-conversation context gate below reads it. So one attachment turn is PERSISTED measured
    // far above the transcript it will actually replay — which is why the assistant row also records
    // ChatMessages.HadEphemeralAttachment (script 053) and LastAssistantInputTokens skips the rows
    // that carry it, falling back to the last clean turn rather than gating on a number no later
    // prompt can reproduce.
    private const int AttachmentMarkdownMaxChars = 200_000;

    // Transport bound for /chat. AttachmentMarkdownMaxChars is applied AFTER model binding, so the
    // whole body is buffered and deserialised before anything is cut — without this, Kestrel's 30 MB
    // default would let an authenticated caller make the server materialise 30 MB of JSON per
    // request only to truncate it to 200.000 chars. 1 MB leaves ~4x headroom over the ceiling's
    // worst-case UTF-8 footprint (200.000 chars acentuados ≈ 220 KB escapados) plus the message.
    private const long ChatRequestMaxBytes = 1L * 1024 * 1024;

    // Matches dbo.ChatMessages.AttachmentFileName nvarchar(255) (script 056). Applied by TRIMMING,
    // never by rejecting: the file was already accepted and parsed by /chat/attachment, so refusing
    // the question over the length of its NAME would dead-end the analyst for nothing. Nothing about
    // the attachment's content depends on this value — it is a label the analyst reads back.
    private const int AttachmentFileNameMaxChars = 255;

    // Both separators, always, regardless of the platform this runs on. See
    // NormalizeAttachmentFileName for why Path.GetFileName is the wrong tool for this value.
    private static readonly char[] AttachmentPathSeparators = ['/', '\\'];

    // Duplicated from BillingQuotaFilter's private const of the same name rather than shared.
    // This is a WIRE contract the Portal parses, not code: hoisting 40 characters into a common
    // constant would couple a controller to a filter's internals, and both would still have to
    // agree if the base URI ever moved. If it moves, both change.
    private const string ErrorTypeBase = "https://serviciosfinancieros.api/errors/";

    // POST /api/analyses refusals before any paid call: the document is not the named entity's, or
    // it is not a balance. Wire contract (ProblemDetails.errorCode) the Portal may branch on.
    internal const string DocumentEntityMismatchErrorCode = "document_entity_mismatch";
    internal const string DocumentNotBalanceErrorCode     = "document_not_balance";

    // POST /api/analyses 409s from ReanalysisPolicy. Wire contract (ProblemDetails.errorCode).
    // extraction_budget_exhausted also carries lastFailureCode (AnalysisRunFailureCodes.BudgetRefusalCodes)
    // and a detail chosen by it: the document's fault ("unreadable") or ours (everything else).
    internal const string ExtractionBudgetExhaustedErrorCode = "extraction_budget_exhausted";
    internal const string DocumentProcessingErrorCode        = "document_processing";
    internal const string AnalysisNotRejectedErrorCode       = "analysis_not_rejected";
    internal const string AnalysisNotFoundErrorCode          = "analysis_not_found";

    internal const string ExtractionBudgetExhaustedTitle = "Se agotaron los intentos de lectura";

    internal const string ExtractionBudgetExhaustedUnreadableDetail =
        "No pudimos leer este balance en los intentos disponibles. Subí un archivo en mejor calidad " +
        "(un PDF digital o un escaneo más nítido). Volver a subir el mismo archivo no reinicia los intentos.";

    internal const string ExtractionBudgetExhaustedOurSideDetail =
        "No pudimos completar la lectura de este balance por un problema de nuestro lado. " +
        "Avisá a soporte para habilitar un nuevo intento.";

    // Every CK_Documents_DocumentType value, as a list for DocumentTypes.Canonical ("Otro" appears in
    // both vocabularies; the first match is the same spelling either way).
    private static readonly IReadOnlyList<string> KnownDocumentTypes =
        [.. DocumentTypes.LegacyTypes, .. DocumentTypes.LegajoTypes];

    // Retry-After on a background trigger refused at admission (503 queue full, 429 tenant cap).
    // Short on purpose: a slot frees as soon as any run finishes, and runs last minutes, not hours.
    private const int BackgroundRetryAfterSeconds = 30;

    private readonly ITenantContextAccessor       _tenantContextAccessor;
    private readonly IDocumentRepository          _documentRepository;
    private readonly IAnalysisResultRepository    _analysisResultRepository;
    private readonly IAnalysisRepository          _analysisRepository;
    private readonly IClaudeService               _claudeService;
    private readonly ICashFlowReconciliationService _cashFlowReconciliationService;
    private readonly ICashFlowNarrativeService     _cashFlowNarrativeService;
    private readonly IAlertRatioReconciliationService _alertRatioReconciliationService;
    private readonly IEntityRepository            _entityRepository;
    private readonly IUserRepository              _userRepository;
    private readonly IChatRepository              _chatRepository;
    private readonly ICreditLineRepository        _creditLineRepository;
    private readonly ICreditLineOperationRepository _operationRepository;
    private readonly IAuditService                _auditService;
    private readonly IApiCallLogService           _apiCallLogService;
    private readonly IExcelCashFlowParser         _excelCashFlowParser;
    private readonly IOptions<Models.BillingOptions> _billingOptions;
    private readonly ITenantBillingConfigService  _tenantBillingConfigService;
    private readonly IUserApprovalLimitRepository _userApprovalLimitRepository;
    private readonly IAnalysisDecisionNotifier   _decisionNotifier;
    private readonly IAnalysisApprovalQuorumService _approvalQuorumService;
    private readonly ILegajoDocumentRepository    _legajoDocumentRepository;
    private readonly IAnalysisPipeline            _analysisPipeline;
    private readonly IAnalysisJobQueue            _analysisJobQueue;
    private readonly AnalysisRunRegistry          _analysisRunRegistry;
    private readonly IAnalysisRunStateRepository  _analysisRunStateRepository;
    private readonly IPersonAnalysisRepository    _personAnalysisRepository;
    private readonly ILogger<AnalysisController>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisController"/>.
    /// </summary>
    public AnalysisController(
        ITenantContextAccessor      tenantContextAccessor,
        IDocumentRepository         documentRepository,
        IAnalysisResultRepository   analysisResultRepository,
        IAnalysisRepository         analysisRepository,
        IClaudeService              claudeService,
        ICashFlowReconciliationService cashFlowReconciliationService,
        ICashFlowNarrativeService   cashFlowNarrativeService,
        IAlertRatioReconciliationService alertRatioReconciliationService,
        IEntityRepository           entityRepository,
        IUserRepository             userRepository,
        IChatRepository             chatRepository,
        ICreditLineRepository       creditLineRepository,
        ICreditLineOperationRepository operationRepository,
        IAuditService               auditService,
        IApiCallLogService          apiCallLogService,
        IExcelCashFlowParser        excelCashFlowParser,
        IOptions<Models.BillingOptions> billingOptions,
        ITenantBillingConfigService tenantBillingConfigService,
        IUserApprovalLimitRepository userApprovalLimitRepository,
        IAnalysisDecisionNotifier   decisionNotifier,
        IAnalysisApprovalQuorumService approvalQuorumService,
        ILegajoDocumentRepository   legajoDocumentRepository,
        IAnalysisPipeline           analysisPipeline,
        IAnalysisJobQueue           analysisJobQueue,
        AnalysisRunRegistry         analysisRunRegistry,
        IAnalysisRunStateRepository analysisRunStateRepository,
        IPersonAnalysisRepository   personAnalysisRepository,
        ILogger<AnalysisController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _documentRepository       = documentRepository;
        _analysisResultRepository = analysisResultRepository;
        _analysisRepository       = analysisRepository;
        _claudeService            = claudeService;
        _cashFlowReconciliationService = cashFlowReconciliationService;
        _cashFlowNarrativeService = cashFlowNarrativeService;
        _alertRatioReconciliationService = alertRatioReconciliationService;
        _entityRepository         = entityRepository;
        _userRepository           = userRepository;
        _chatRepository           = chatRepository;
        _creditLineRepository     = creditLineRepository;
        _operationRepository      = operationRepository;
        _auditService             = auditService;
        _apiCallLogService        = apiCallLogService;
        _excelCashFlowParser      = excelCashFlowParser;
        _billingOptions           = billingOptions;
        _tenantBillingConfigService = tenantBillingConfigService;
        _userApprovalLimitRepository = userApprovalLimitRepository;
        _decisionNotifier         = decisionNotifier;
        _approvalQuorumService    = approvalQuorumService;
        _legajoDocumentRepository = legajoDocumentRepository;
        _analysisPipeline         = analysisPipeline;
        _analysisJobQueue         = analysisJobQueue;
        _analysisRunRegistry      = analysisRunRegistry;
        _analysisRunStateRepository = analysisRunStateRepository;
        _personAnalysisRepository = personAnalysisRepository;
        _logger                   = logger;
    }

    // ── POST /api/analyses ────────────────────────────────────────────────────────

    /// <summary>
    /// Triggers a new AI analysis for the specified document.
    /// </summary>
    /// <remarks>
    /// By default the analysis runs inside this request and the response is the finished result.
    /// With <c>Prefer: respond-async</c> (RFC 7240) the same checks run synchronously, the analysis is
    /// queued for a background worker, and the response is 202 with a Location to poll
    /// (GET /api/documents/{documentId}) and <c>Preference-Applied: respond-async</c>.
    /// </remarks>
    /// <param name="request">Request body containing the DocumentId.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 201 Created with <see cref="AnalysisResultResponse"/>; with <c>Prefer: respond-async</c>,
    /// 202 Accepted with <see cref="AnalysisAcceptedResponse"/>.
    /// </returns>
    [HttpPost]
    [Authorize(Roles = Roles.AnalysisCreators)]
    [BillingQuota(BillingQuotaKind.Analysis)]
    [ProducesResponseType(typeof(AnalysisResultResponse), 201)]
    [ProducesResponseType(typeof(AnalysisAcceptedResponse), 202)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public async Task<IActionResult> TriggerAsync(
        [FromBody] TriggerAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        // The monthly analysis quota was already checked by [BillingQuota] above; if the tenant
        // were out of quota this method would not be running. It is a filter and not a check here
        // on purpose: the gate MUST stay ahead of the "Processing" flip below and of the Claude
        // call after it, because a request refused after the document has moved and the API has
        // been paid for has not been refused. Do not move that work above the filter, and do not
        // reimplement the check inline.
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // ── Validate document ─────────────────────────────────────────────────────

        var document = await _documentRepository
            .GetByIdAsync(request.DocumentId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null || document.IsDeleted)
        {
            return Problem(statusCode: 404, title: "Not Found",
                detail: "No se encontró el documento.");
        }

        // ── A persona humana legajo document never reaches balance extraction (DDL 070) ──
        // Its bytes are a natural person's paperwork, and they may go to the model ONLY through the
        // legajo pipeline, which checks the recorded consent before every call. This path checks no
        // consent, so it refuses the document outright — before the budget read, the "Processing"
        // flip and any Claude call.
        // errorCode legajo_document_not_analyzable is additive: type, title, detail and traceId stay what
        // Problem() produced before, so a client that ignores the code sees the same 400.
        if (document.PersonLegajoId is not null)
        {
            var notAnalyzable = Problem(statusCode: 400, title: "Bad Request",
                detail: "El documento pertenece al legajo de una persona humana y no puede analizarse como balance.");
            ((ProblemDetails)notAnalyzable.Value!).Extensions["errorCode"] = "legajo_document_not_analyzable";
            return notAnalyzable;
        }

        // ── The document must be THIS entity's balance ────────────────────────────
        // Both before the consent read, the extraction budget, the "Processing" flip and any paid
        // call. The balance is read from the document while BCRA, LUFE and CASFOG come from the
        // request's entity (BuildRun): a document of another entity would score one company on
        // another's figures. And the extraction only knows how to read a balance.
        if (request.EntityId != document.EntityId)
        {
            _logger.LogWarning(
                "Trigger refused for document {DocumentId} tenant {TenantId}: the document belongs to entity " +
                "{DocumentEntityId}, the request names entity {RequestEntityId}.",
                request.DocumentId, tenantId, document.EntityId, request.EntityId);
            var mismatch = Problem(statusCode: 400, title: "Bad Request",
                detail: "El documento no pertenece a la entidad indicada en la solicitud.");
            ((ProblemDetails)mismatch.Value!).Extensions["errorCode"] = DocumentEntityMismatchErrorCode;
            return mismatch;
        }

        var (typeVerdict, canonicalType) = ClassifyDocumentType(document.DocumentType);
        switch (typeVerdict)
        {
            case DocumentTypeVerdict.NotBalance:
                _logger.LogWarning(
                    "Trigger refused for document {DocumentId} tenant {TenantId}: document type {DocumentType} is not a balance.",
                    request.DocumentId, tenantId, document.DocumentType);
                var notBalance = Problem(statusCode: 400, title: "Bad Request",
                    detail: $"El documento no es un balance (tipo «{canonicalType}»). Solo los documentos de tipo Balance pueden analizarse.");
                ((ProblemDetails)notBalance.Value!).Extensions["errorCode"] = DocumentNotBalanceErrorCode;
                return notBalance;

            case DocumentTypeVerdict.BalanceNonCanonical:
            case DocumentTypeVerdict.Unknown:
                // Legacy rows: the upload validated the type case-insensitively but stored it as sent
                // ("balance"), and CK_Documents_DocumentType is case-insensitive too. A value outside
                // the CK vocabulary cannot exist; if it ever does, the analysis proceeds as before.
                _logger.LogWarning(
                    "Document {DocumentId} tenant {TenantId} has document type {DocumentType}, not the canonical " +
                    "\"Balance\"; analysing it as a balance.",
                    request.DocumentId, tenantId, document.DocumentType);
                break;

            // DocumentTypeVerdict.Balance → proceed.
        }

        // The same rule for a persona humana's paperwork uploaded WITHOUT a legajo (e.g. as "Otro"):
        // when the document's entity is linked to a legajo, its bytes reach the model only if that
        // legajo has ACTIVE consent (recorded, and its authorization document still live). A company
        // entity has no legajo and is not affected.
        var (entityLegajoId, entityConsentActive) = await _legajoDocumentRepository
            .GetEntityLegajoConsentAsync(document.EntityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entityLegajoId is not null && !entityConsentActive)
        {
            var consentRequired = new ProblemDetails
            {
                Status   = 409,
                Title    = "Conflict",
                Detail   = "La entidad es una persona humana sin autorización de tratamiento de datos vigente. Registrá la autorización en su legajo antes de analizar el documento.",
                Type     = "https://serviciosfinancieros.api/errors/consent_required",
                Instance = HttpContext.Request.Path
            };
            consentRequired.Extensions["errorCode"] = "consent_required";
            return StatusCode(409, consentRequired);
        }

        // ── Extraction budget ─────────────────────────────────────────────────────
        // How much Claude spend this BALANCE FILE has already cost on failed extractions.
        // Summed by FileHashSHA256 across the tenant's non-deleted documents, not by document id:
        // a per-row budget would reset the moment the analyst re-uploaded the same bytes.
        //
        // Why this guard exists at all: the [dbo].[Analyses] row the monthly quota counts is
        // written only after Claude answers, so a failed extraction correctly consumes no
        // análisis — and therefore nothing else stood between an unreadable PDF and an unlimited
        // retry loop. Each retry is a real billable extraction, and not one call: ClaudeService
        // re-sends the pass on a timeout and runs a second full pass over high-DPI images when
        // confidence is low.
        //
        // Fault-isolated and FAIL-OPEN, matching BillingQuotaFilter's stated policy: if the count
        // cannot be read the request proceeds. Over-serving for the length of a blip is
        // recoverable; refusing an analyst's work because a COUNT failed is not.
        var maxExtractionAttempts = _billingOptions?.Value?.MaxExtractionAttemptsPerDocument ?? 0;
        var extractionAttempts    = 0;

        if (maxExtractionAttempts > 0)
        {
            try
            {
                extractionAttempts = await _documentRepository
                    .SumExtractionAttemptsByHashAsync(document.FileHashSHA256, tenantId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Extraction attempts could not be read for document {DocumentId} tenant {TenantId}; " +
                    "allowing the trigger through without the extraction budget check.",
                    request.DocumentId, tenantId);
            }
        }

        // ── Re-analysis guard ─────────────────────────────────────────────────────
        // A Processing document is always locked (no concurrent analyses). A Completed
        // document may be re-analysed ONLY when its most recent analysis was Rejected;
        // that prior Rejected analysis is preserved as history (a fresh analysisId is
        // created below). Pending/Failed documents fall through unchanged, unless the file has
        // spent its extraction budget.
        //
        // Everything here runs BEFORE the "Processing" flip and before the Claude call below, so
        // a refusal costs nothing. That ordering is the whole point — see the filter note above.

        string? latestAnalysisStatus     = null;
        var     latestAnalysisReadNothing = false;
        if (document.ProcessingStatus == "Completed")
        {
            var latestAnalysis = await _analysisRepository
                .GetLatestByDocumentIdAsync(request.DocumentId, tenantId, cancellationToken)
                .ConfigureAwait(false);
            latestAnalysisStatus = latestAnalysis?.Status;

            // §5.8: an analysis from before the no-financial-statements gate may hold a balance with none of
            // the seven base figures. Read only when it decides something (not Rejected), through JSON_VALUE.
            if (latestAnalysis is not null && latestAnalysis.Status != "Rejected")
            {
                latestAnalysisReadNothing = await LatestAnalysisReadNoBaseFiguresAsync(
                    latestAnalysis.AnalysisId, request.DocumentId, tenantId, cancellationToken).ConfigureAwait(false);
            }
        }

        switch (ReanalysisPolicy.Evaluate(
            document.ProcessingStatus, latestAnalysisStatus, latestAnalysisReadNothing,
            extractionAttempts, maxExtractionAttempts))
        {
            case ReanalysisDecision.DenyExtractionBudgetExhausted:
            {
                var lastFailureCode = await ResolveLastExtractionFailureCodeAsync(
                    document.FileHashSHA256, request.DocumentId, tenantId, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "Extraction budget exhausted for document {DocumentId} tenant {TenantId}: " +
                    "{Attempts}/{MaxAttempts} failed extractions on this file, last failure {LastFailureCode}. " +
                    "Trigger refused with 409 before any Claude call.",
                    request.DocumentId, tenantId, extractionAttempts, maxExtractionAttempts, lastFailureCode);

                var exhausted = new ProblemDetails
                {
                    Status   = 409,
                    Title    = ExtractionBudgetExhaustedTitle,
                    Detail   = lastFailureCode == AnalysisRunFailureCodes.Unreadable
                        ? ExtractionBudgetExhaustedUnreadableDetail
                        : ExtractionBudgetExhaustedOurSideDetail,
                    Type     = ErrorTypeBase + ExtractionBudgetExhaustedErrorCode,
                    Instance = HttpContext.Request.Path
                };
                exhausted.Extensions["errorCode"]       = ExtractionBudgetExhaustedErrorCode;
                exhausted.Extensions["lastFailureCode"] = lastFailureCode;
                return StatusCode(409, exhausted);
            }

            case ReanalysisDecision.DenyProcessing:
                return CodedConflict(DocumentProcessingErrorCode,
                    $"El documento ya se encuentra en el estado '{document.ProcessingStatus}'.");

            case ReanalysisDecision.DenyNotRejected:
                return CodedConflict(AnalysisNotRejectedErrorCode,
                    "El análisis vigente de este documento no fue rechazado, por lo que no puede reanalizarse. Para generar un análisis nuevo, vuelva a subir el balance: se registra como documento nuevo y el análisis vigente no se modifica.");

            case ReanalysisDecision.DenyNoAnalysis:
                _logger.LogWarning(
                    "Re-analysis blocked: Completed document {DocumentId} tenant {TenantId} has no analysis on record.",
                    request.DocumentId, tenantId);
                return CodedConflict(AnalysisNotFoundErrorCode,
                    "No se encontró un análisis previo para este documento; no puede reanalizarse.");

            case ReanalysisDecision.AllowAfterRejected:
                _logger.LogInformation(
                    "Re-analysis permitted for previously-rejected document {DocumentId} tenant {TenantId}.",
                    request.DocumentId, tenantId);
                break;

            case ReanalysisDecision.AllowAfterUnreadable:
                // The earlier analysis stays as it is, as history; a new analysisId is created below.
                _logger.LogInformation(
                    "Re-analysis permitted for document {DocumentId} tenant {TenantId}: its latest analysis read none of the base figures.",
                    request.DocumentId, tenantId);
                break;

            // ReanalysisDecision.Allow → proceed (Pending/Failed).
        }

        // ── Resolve requesting user ───────────────────────────────────────────────

        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(firebaseUid))
            return Unauthorized();

        var currentUser = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);
        if (currentUser is null)
            return Unauthorized();

        // ── Background run: Prefer: respond-async (RFC 7240) ─────────────────────
        // Opt-in. Everything above — quota, tenant, document, consent, extraction budget,
        // re-analysis policy, requesting user — has already run synchronously and refused what it
        // had to refuse. With the header, the long part goes to AnalysisJobWorker and the caller
        // gets 202 at once; without it nothing changes: the run happens inside this request,
        // exactly as before (the Portal in production depends on that).
        if (PreferHeader.RequestsRespondAsync(Request.Headers))
        {
            return await AcceptInBackgroundAsync(request, document, tenantId, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);
        }

        // ── Set status to Processing ──────────────────────────────────────────────

        // Tracked for the length of the run so this instance's stale-Processing sweep can never
        // fail a document this request is still working on. Invisible to the response.
        // The info (requester, claim time) is what GET /api/analyses/runs shows for a running document.
        _analysisRunRegistry.Track(
            request.DocumentId, new AnalysisRunInfo(tenantId, currentUser.UserId, DateTime.UtcNow));
        try
        {
            await _documentRepository
                .UpdateStatusAsync(request.DocumentId, tenantId, "Processing", null, cancellationToken)
                .ConfigureAwait(false);

            // The long part — storage read, Claude, enrichment, scoring, persistence, the
            // Completed/Failed transition, audit, ApiCallLogs and the requester's notification —
            // lives in AnalysisPipeline so the background path runs the very same code. It drives
            // the document to a terminal state and rethrows any fault it cannot absorb, exactly as
            // this method used to do inline.
            var outcome = await _analysisPipeline
                .RunAsync(BuildRun(request, document, tenantId, currentUser.UserId, isBackground: false), cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Status != AnalysisRunStatus.Completed)
            {
                // The model answered but no JSON could be read out of the answer: not an outage, so the
                // analyst is told to retry rather than that the service is down. A file with no financial
                // statements in it (a blank page, the legalization cover alone) asks for the complete PDF.
                // Same status as before: only the text names the cause.
                var detail = outcome.FailureCode switch
                {
                    AnalysisRunFailureCodes.StoredClaudeUnparseableAnswer     => ClaudeApiException.UnparseableAnswerClientMessage,
                    AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements => ClaudeApiException.NoFinancialStatementsClientMessage,
                    _                                                         => ClaudeApiException.DefaultClientMessage,
                };
                var failed = Problem(statusCode: 502, title: "Bad Gateway", detail: detail);

                // Additive, the same values GET /api/analyses/runs lists for this failure: the finer reason,
                // whose move it is, and what the file has left of its budget. Allowlisted — never the code.
                if (failed is ObjectResult { Value: ProblemDetails failedProblem })
                {
                    var failureReason = AnalysisRunFailureReasons.FromStoredReason(outcome.FailureCode);
                    failedProblem.Extensions["failureReason"]     = failureReason;
                    failedProblem.Extensions["causeGroup"]        = AnalysisRunFailureReasons.CauseGroupOf(failureReason);
                    failedProblem.Extensions["attemptsRemaining"] = await ReadAttemptsRemainingAfterFailureAsync(
                        document.FileHashSHA256, request.DocumentId, tenantId, cancellationToken).ConfigureAwait(false);
                }
                return failed;
            }

            var result     = outcome.Result!;
            var analysis   = outcome.Analysis!;
            var entityName = outcome.EntityName;

            var response = MapToResponse(result, request.DocumentId,
                string.IsNullOrWhiteSpace(entityName) ? null : entityName,
                fiscalYear: analysis.FiscalYear,
                status: analysis.Status,
                entityId: request.EntityId);
            ApplyDetailChannels(response, result.CatalogueAlerts, result.ScoreBreakdownJson, analysis.AnalysisKind, result.RescoredAt);
            return CreatedAtAction(nameof(GetById), new { id = result.ResultId }, response);
        }
        finally
        {
            _analysisRunRegistry.Release(request.DocumentId);
        }
    }

    /// <summary>
    /// The <c>Prefer: respond-async</c> half of <see cref="TriggerAsync"/>: claims the document and
    /// queues the run for <see cref="AnalysisJobWorker"/>, answering 202 with a Location the caller
    /// polls (GET /api/documents/{id}, whose ProcessingStatus the Portal already watches).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADMISSION BEFORE ANY WRITE. The per-instance capacity and per-tenant cap are checked before the
    /// document moves, so a refusal (503 / 429, both with Retry-After) leaves nothing to undo and spends
    /// nothing. The per-tenant cap also bounds how far a burst can overshoot the monthly quota, which
    /// counts [dbo].[Analyses] rows that a queued run has not written yet.
    /// </para>
    /// <para>
    /// IDEMPOTENCY: 409, NEVER A SECOND JOB. The move to "Processing" is a compare-and-set on the
    /// status and timestamp validated above. When it loses — another trigger got there first — the
    /// answer is the very 409 the re-analysis policy gives a document already in "Processing". It is
    /// not "the existing run's 202": that run may be a synchronous one, or live on another instance,
    /// so there is no job of ours to point at.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> AcceptInBackgroundAsync(
        TriggerAnalysisRequest request,
        Document               document,
        string                 tenantId,
        Guid                   userId,
        CancellationToken      cancellationToken)
    {
        switch (_analysisRunRegistry.TryAdmit(
                    tenantId, request.DocumentId, new AnalysisRunInfo(tenantId, userId, DateTime.UtcNow)))
        {
            case AnalysisAdmission.QueueFull:
                _logger.LogWarning(
                    "Background analysis queue is full; document {DocumentId} tenant {TenantId} refused with 503 before any write.",
                    request.DocumentId, tenantId);
                return BackgroundRefusal(503, "Service Unavailable", "analysis_queue_full",
                    "Hay demasiados análisis en curso en este momento. Intentá de nuevo en unos segundos.");

            case AnalysisAdmission.TenantLimit:
                _logger.LogWarning(
                    "Tenant {TenantId} already has the maximum background analyses in flight; document {DocumentId} refused with 429 before any write.",
                    tenantId, request.DocumentId);
                return BackgroundRefusal(429, "Too Many Requests", "analysis_queue_tenant_limit",
                    "Ya hay varios análisis en curso para tu organización. Esperá a que termine alguno e intentá de nuevo.");
        }

        var queued = false;
        try
        {
            var claimed = await _analysisRunStateRepository
                .TryBeginProcessingAsync(
                    request.DocumentId, tenantId, document.ProcessingStatus, document.ProcessedAt, cancellationToken)
                .ConfigureAwait(false);

            if (!claimed)
            {
                _logger.LogInformation(
                    "Background trigger for document {DocumentId} tenant {TenantId} lost the claim to a concurrent trigger; 409.",
                    request.DocumentId, tenantId);
                return Problem(statusCode: 409, title: "Conflict",
                    detail: "El documento ya se encuentra en el estado 'Processing'.");
            }

            var run = BuildRun(request, document, tenantId, userId, isBackground: true);

            if (!_analysisJobQueue.TryEnqueue(run))
            {
                // Unreachable while admission and the channel share one capacity. Kept so a full
                // channel can never strand a claimed document: put back what it was, then refuse.
                _logger.LogWarning(
                    "Background analysis channel refused document {DocumentId} tenant {TenantId} after the claim; restoring its status.",
                    request.DocumentId, tenantId);
                await _analysisRunStateRepository
                    .TryRestoreAsync(
                        request.DocumentId, tenantId, document.ProcessingStatus, document.ProcessedAt, CancellationToken.None)
                    .ConfigureAwait(false);
                return BackgroundRefusal(503, "Service Unavailable", "analysis_queue_full",
                    "Hay demasiados análisis en curso en este momento. Intentá de nuevo en unos segundos.");
            }

            queued = true;
        }
        finally
        {
            // Once queued, the slot belongs to the job and AnalysisJobWorker releases it.
            if (!queued)
                _analysisRunRegistry.ReleaseAdmission(tenantId, request.DocumentId);
        }

        _logger.LogInformation(
            "Analysis for document {DocumentId} tenant {TenantId} accepted for background processing.",
            request.DocumentId, tenantId);

        Response.Headers[PreferHeader.AppliedHeaderName] = PreferHeader.RespondAsync;
        return AcceptedAtAction(
            nameof(DocumentsController.GetById),
            "Documents",
            new { id = request.DocumentId },
            new AnalysisAcceptedResponse
            {
                DocumentId = request.DocumentId,
                EntityId   = request.EntityId,
                Status     = "Processing"
            });
    }

    /// <summary>
    /// Whether the stored balance of <paramref name="analysisId"/> read none of the seven base figures
    /// (<see cref="BalanceBaseFigures.ReadNone"/>). FAIL-CLOSED to today's rule: a read that throws answers
    /// false, so the trigger keeps its 409 rather than buying an extraction on an unverified premise.
    /// </summary>
    private async Task<bool> LatestAnalysisReadNoBaseFiguresAsync(
        Guid analysisId, Guid documentId, string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var figures = await _analysisRepository
                .GetBalanceBaseFiguresAsync(analysisId, tenantId, cancellationToken)
                .ConfigureAwait(false);
            return BalanceBaseFigures.ReadNone(figures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "The base figures of the latest analysis of document {DocumentId} tenant {TenantId} could not be read " +
                "({ExceptionType}); the re-analysis rule applies as if they were read.",
                documentId, tenantId, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// A 409 exactly as <c>Problem(409, "Conflict", detail)</c> has always shaped it, plus an
    /// <c>errorCode</c> extension the Portal can branch on (the document_entity_mismatch pattern).
    /// </summary>
    private ObjectResult CodedConflict(string errorCode, string detail)
    {
        var conflict = Problem(statusCode: 409, title: "Conflict", detail: detail);
        ((ProblemDetails)conflict.Value!).Extensions["errorCode"] = errorCode;
        return conflict;
    }

    /// <summary>
    /// Why this FILE spent its extraction budget, as the closed <c>lastFailureCode</c>
    /// (<see cref="AnalysisRunFailureCodes.ForBudgetRefusal"/>): read from the newest failure among the
    /// documents sharing its hash — the same rows the budget was summed over. Fail-open like that sum: a
    /// read that throws reports <see cref="AnalysisRunFailureCodes.Other"/> and the refusal still goes out.
    /// The stored code never leaves the API.
    /// </summary>
    private async Task<string> ResolveLastExtractionFailureCodeAsync(
        string? fileHashSha256, Guid documentId, string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _documentRepository
                .GetLatestExtractionErrorByHashAsync(fileHashSha256, tenantId, cancellationToken)
                .ConfigureAwait(false);
            return AnalysisRunFailureCodes.ForBudgetRefusal(stored);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "The last extraction failure could not be read for document {DocumentId} tenant {TenantId}; " +
                "reporting the spent budget as {LastFailureCode}.",
                documentId, tenantId, AnalysisRunFailureCodes.Other);
            return AnalysisRunFailureCodes.Other;
        }
    }

    /// <summary>
    /// The <c>attemptsRemaining</c> of the synchronous 502: what this FILE has left of its extraction budget
    /// AFTER the failed run, re-read because the run itself may or may not have spent an attempt (and its
    /// bookkeeping write may have failed). Null when the budget is off (Billing:MaxExtractionAttemptsPerDocument
    /// ≤ 0), the hash is blank, or the read fails — never a reason to lose the 502, so even a cancellation of
    /// the caller's token (the client left) only yields null.
    /// </summary>
    private async Task<int?> ReadAttemptsRemainingAfterFailureAsync(
        string? fileHashSha256, Guid documentId, string tenantId, CancellationToken cancellationToken)
    {
        var maxAttempts = _billingOptions?.Value?.MaxExtractionAttemptsPerDocument ?? 0;
        if (maxAttempts <= 0 || string.IsNullOrWhiteSpace(fileHashSha256))
            return null;

        try
        {
            var spent = await _documentRepository
                .SumExtractionAttemptsByHashAsync(fileHashSha256, tenantId, cancellationToken)
                .ConfigureAwait(false);
            return AnalysisRunFailureReasons.AttemptsRemaining(maxAttempts, spent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Extraction attempts could not be re-read after the failed run of document {DocumentId} tenant {TenantId} " +
                "({ExceptionType}); the 502 reports attemptsRemaining as null.",
                documentId, tenantId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Captures, while the request is alive, everything the long part needs — so the pipeline never
    /// reads an HttpContext, a claim or the tenant accessor, in either mode.
    /// </summary>
    private AnalysisRun BuildRun(
        TriggerAnalysisRequest request, Document document, string tenantId, Guid userId, bool isBackground) => new()
    {
        TenantId           = tenantId,
        RequestedBy        = userId,
        DocumentId         = request.DocumentId,
        EntityId           = request.EntityId,
        FiscalYear         = request.FiscalYear,
        RequestedAmount    = request.RequestedAmount,
        StoragePath        = document.StoragePath,
        DocumentType       = document.DocumentType,
        DocumentFiscalYear = document.FiscalYear,
        IpAddress          = HttpContext.Connection.RemoteIpAddress?.ToString(),
        RequestId          = HttpContext.TraceIdentifier,
        AcceptedAtUtc      = DateTime.UtcNow,
        IsBackground       = isBackground
    };

    /// <summary>What a document's stored type means for balance extraction.</summary>
    internal enum DocumentTypeVerdict
    {
        /// <summary>Exactly <see cref="DocumentTypes.Balance"/>.</summary>
        Balance,

        /// <summary>"Balance" in another casing or with surrounding blanks (legacy upload stored it as sent).</summary>
        BalanceNonCanonical,

        /// <summary>Another CK_Documents_DocumentType value: Complemento, Certificado, Otro or a legajo type.</summary>
        NotBalance,

        /// <summary>A value outside the CK vocabulary — cannot exist under the constraint; fail-open.</summary>
        Unknown,
    }

    /// <summary>
    /// Classifies <paramref name="documentType"/> case-insensitively against the CK vocabulary,
    /// returning the canonical spelling when known.
    /// </summary>
    internal static (DocumentTypeVerdict Verdict, string? Canonical) ClassifyDocumentType(string? documentType)
    {
        if (string.Equals(documentType, DocumentTypes.Balance, StringComparison.Ordinal))
            return (DocumentTypeVerdict.Balance, DocumentTypes.Balance);

        var canonical = DocumentTypes.Canonical(documentType, KnownDocumentTypes);
        return canonical switch
        {
            null                   => (DocumentTypeVerdict.Unknown, null),
            DocumentTypes.Balance  => (DocumentTypeVerdict.BalanceNonCanonical, canonical),
            _                      => (DocumentTypeVerdict.NotBalance, canonical),
        };
    }

    /// <summary>A 503/429 for a background trigger refused at admission, with Retry-After.</summary>
    private ObjectResult BackgroundRefusal(int statusCode, string title, string errorCode, string detail)
    {
        Response.Headers.RetryAfter = BackgroundRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status   = statusCode,
            Title    = title,
            Detail   = detail,
            Type     = ErrorTypeBase + errorCode,
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = errorCode;
        return StatusCode(statusCode, problem);
    }

    // ── GET /api/analyses/{id} ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a single analysis result by its primary key.
    /// </summary>
    /// <param name="id">The result identifier.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="AnalysisResultResponse"/>, or 404.</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AnalysisResultResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var response = await BuildDetailResponseAsync(id, tenantId, cancellationToken).ConfigureAwait(false);

        if (response is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        return Ok(response);
    }

    /// <summary>
    /// The GET /api/analyses/{id} body, or null when the result is not in this tenant. Shared with
    /// POST {id}/rescore, which answers with the same detail plus its <c>rescore</c> block.
    /// </summary>
    private async Task<AnalysisResultResponse?> BuildDetailResponseAsync(
        Guid id,
        string tenantId,
        CancellationToken cancellationToken)
    {
        // A PROJECTED read, not GetByIdAsync: the four frozen Contexto snapshots are never selected
        // here — see AnalysisResultDetailProjection — and are served by GET {id}/context instead.
        var detail = await _analysisResultRepository
            .GetDetailByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
            return null;

        var analysis = await _analysisRepository
            .GetByIdAsync(detail.AnalysisId, cancellationToken)
            .ConfigureAwait(false);

        Entity? entity = null;
        string? entityName = null;
        string? entityCuit = null;
        string? requestedByName = null;
        string? reviewedByName = null;
        string? preApprovedByName = null;
        string? rescoredByName = null;
        if (analysis is not null)
        {
            entity = await _entityRepository
                .GetByIdAsync(analysis.EntityId, tenantId, cancellationToken)
                .ConfigureAwait(false);
            entityName = entity?.BusinessName;
            entityCuit = entity?.CUIT;

            // ONE batched lookup for all four actors. This replaced two sequential single-id reads:
            // the pre-approver would have made it three, and the list endpoint, the chat list and the
            // credit-line payloads have all resolved names this way for a while. Note the fallback
            // changes with it — an id that no longer resolves now yields "Usuario" rather than null,
            // matching those other surfaces.
            var actorNames = await ResolveActorNamesAsync(
                [analysis.RequestedBy, analysis.ReviewedBy, analysis.PreApprovedBy, detail.RescoredBy],
                cancellationToken).ConfigureAwait(false);

            requestedByName   = ActorName(analysis.RequestedBy, actorNames);
            reviewedByName    = ActorName(analysis.ReviewedBy, actorNames);
            preApprovedByName = ActorName(analysis.PreApprovedBy, actorNames);
            rescoredByName    = ActorName(detail.RescoredBy, actorNames);
        }

        var response = MapToDetailResponse(
            detail,
            analysis?.DocumentId ?? Guid.Empty,
            entityName,
            entityCuit,
            analysis?.RequestedAmount,
            analysis?.RequestedBy.ToString(),
            requestedByName,
            analysis?.ReviewedBy?.ToString(),
            reviewedByName,
            fiscalYear: analysis?.FiscalYear,
            status: analysis?.Status,
            reviewNotes: analysis?.ReviewNotes,
            finalRiskScore: analysis?.FinalRiskScore,
            finalRiskLevel: analysis?.FinalRiskLevel,
            entityId: analysis?.EntityId ?? Guid.Empty,
            preApprovedBy: analysis?.PreApprovedBy?.ToString(),
            preApprovedByName: preApprovedByName,
            preApprovedAt: analysis?.PreApprovedAt,
            preApprovedAmount: analysis?.PreApprovedAmount,
            preApprovedCurrency: analysis?.PreApprovedCurrency,
            decisionAmount: analysis?.DecisionAmount,
            decisionCurrency: analysis?.DecisionCurrency,
            // The proposal is what a RiskManager opens this view to read before confirming, so it
            // has to be here and not only on the decision response.
            proposedOperationsJson: analysis?.ProposedOperationsJson,
            confirmedOperationsJson: analysis?.ConfirmedOperationsJson);

        // Persona humana analysis (DDL 072): no balance and no score — the frozen evaluation of the
        // solicitante takes their place. Read from AnalysisParticipants, never recomputed.
        await ApplyPersonFieldsAsync(response, analysis, tenantId, cancellationToken).ConfigureAwait(false);

        // Re-score (DDL 073, DETAIL ONLY): when and by whom, whether it may be re-scored now, and the
        // dates of the sources the current score was computed from.
        await ApplyRescoreFieldsAsync(response, detail, analysis, entity, rescoredByName, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Provenance (DETAIL ONLY): the cupo(s) this analysis originated, regardless of status. Set
        // here — never in the shared MapToResponse — so the list endpoint stays null and avoids an
        // N+1. Always a (possibly empty) array on this endpoint, never null.
        var originatedCupos = await _creditLineRepository
            .GetByOriginAnalysisAsync(detail.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        response.OriginatedCreditLines = originatedCupos
            .Select(c => new OriginatedCreditLineResponse
            {
                CreditLineId = c.CreditLineId,
                Currency     = c.Currency,
                Amount       = c.Amount,
                Status       = c.Status,
            })
            .ToList();

        // Approval tally (DETAIL ONLY, and only while the analysis is still pre-approved). Without
        // it the SECOND manager has no way to see that a colleague already signed, or that there is
        // an approval of their own to withdraw — the feature is unusable from a screen that cannot
        // show "aprobaron 1 de 2". Set here rather than in the shared MapToResponse so the list
        // endpoint stays null, the same reason the provenance above is.
        //
        // Gated on 'PreApproved' so it costs nothing on the analyses that make up almost all reads,
        // and fault-isolated because a tally is context: it must never cost an analyst the view of
        // an analysis. (The DECISION path deliberately does NOT isolate its quorum reads — there a
        // failure has to refuse the decision rather than confirm on a miscount.)
        if (string.Equals(analysis?.Status, PreApprovedStatus, StringComparison.Ordinal))
        {
            try
            {
                var tally = await _approvalQuorumService
                    .GetTallyAsync(tenantId, analysis!, cancellationToken)
                    .ConfigureAwait(false);

                ApplyApprovalTally(response, tally);
            }
            catch (Exception tallyEx)
            {
                _logger.LogWarning(
                    tallyEx,
                    "Failed to read the approval tally for analysis {AnalysisId} tenant {TenantId}. The analysis is returned without it.",
                    detail.AnalysisId, tenantId);
            }
        }

        // Cash-flow reading (DETAIL ONLY, NOT persisted, NOT a scoring input): computed at read time
        // so it covers analyses predating the feature and any wording fix applies retroactively. The
        // reconciliation state is RECOMPUTED here (not read from the blob) so the gate works even for
        // analyses whose persisted BalanceJson has no reconciliation node. Set here — never in the
        // shared MapToResponse — so the list endpoint pays no BalanceJson parse per row. Fault-isolated:
        // any failure leaves the field null and must never break GetById.
        try
        {
            var cashFlow = TryExtractCashFlow(detail.BalanceJson);
            if (cashFlow is not null)
            {
                var reconciliation = _cashFlowReconciliationService.Reconcile(cashFlow);
                response.CashFlowNarrative = _cashFlowNarrativeService.Read(cashFlow, reconciliation);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cash-flow narrative failed for result {ResultId}, tenant {TenantId}; omitting it.",
                detail.ResultId, tenantId);
        }

        // Alert/executive-summary percentage reconciliation (DETAIL ONLY, NOT persisted): re-applies
        // the same fix TriggerAsync applies at write time to whatever text is already stored, so rows
        // persisted before this fix — or under the old, narrower numerator gate — are repaired on
        // every read, with no backfill needed. Set here — never in the shared MapToResponse — so the
        // list endpoint pays no BalanceJson parse per row. Fault-isolated: any failure leaves the
        // persisted text as-is and must never break GetById.
        try
        {
            // InPrintedUnits: the prose quotes amounts as printed, BalanceJson persists pesos.
            var accounts = string.IsNullOrWhiteSpace(detail.BalanceJson)
                ? null
                : JsonSerializer.Deserialize<BalanceAccounts>(detail.BalanceJson, BcraJsonOpts)?.InPrintedUnits();

            if (accounts is not null)
            {
                if (!string.IsNullOrWhiteSpace(response.RiskAlerts))
                {
                    var alertList = JsonSerializer.Deserialize<List<ClaudeAlertJson>>(response.RiskAlerts, BcraJsonOpts);
                    if (alertList is { Count: > 0 })
                    {
                        var anyCorrected = false;
                        foreach (var al in alertList)
                        {
                            if (string.IsNullOrWhiteSpace(al.Description)) continue;

                            var rec = _alertRatioReconciliationService.Reconcile(al.Description, accounts);
                            if (!rec.Corrected) continue;

                            al.Description = rec.Description;
                            anyCorrected = true;
                            _logger.LogWarning(
                                "Alert percentage reconciled at read time [{Code}] result {ResultId} analysis {AnalysisId} " +
                                "tenant {TenantId}: clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                al.Code, detail.ResultId, detail.AnalysisId, tenantId,
                                rec.Clause, rec.StatedPercent, rec.ExpectedPercent, rec.Denominator);
                        }
                        if (anyCorrected)
                            response.RiskAlerts = JsonSerializer.Serialize(alertList, BcraJsonOpts);
                    }
                }

                if (!string.IsNullOrWhiteSpace(response.ExecutiveSummary))
                {
                    var narrative = _alertRatioReconciliationService.ReconcileNarrative(response.ExecutiveSummary, accounts);
                    if (narrative.Corrections.Count > 0)
                    {
                        response.ExecutiveSummary = narrative.Text;
                        foreach (var c in narrative.Corrections)
                            _logger.LogWarning(
                                "Executive summary percentage reconciled at read time result {ResultId} analysis {AnalysisId} " +
                                "tenant {TenantId}: clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                detail.ResultId, detail.AnalysisId, tenantId, c.Clause, c.StatedPercent, c.ExpectedPercent, c.Denominator);
                    }

                    if (narrative.UnresolvedClauses.Count > 0)
                        _logger.LogWarning(
                            "Executive summary has {Count} unverifiable percentage clause(s) at read time result " +
                            "{ResultId} analysis {AnalysisId} tenant {TenantId}: {Clauses}",
                            narrative.UnresolvedClauses.Count, detail.ResultId, detail.AnalysisId, tenantId,
                            string.Join(" | ", narrative.UnresolvedClauses));
                }

                if (!string.IsNullOrWhiteSpace(response.Anomalies))
                {
                    var anomalyList = JsonSerializer.Deserialize<List<string>>(response.Anomalies, BcraJsonOpts);
                    if (anomalyList is { Count: > 0 })
                    {
                        var anyAnomalyCorrected = false;
                        var allUnresolved       = new List<string>();

                        for (int i = 0; i < anomalyList.Count; i++)
                        {
                            if (string.IsNullOrWhiteSpace(anomalyList[i])) continue;

                            var anomalyNarrative = _alertRatioReconciliationService.ReconcileNarrative(anomalyList[i], accounts);

                            if (anomalyNarrative.Corrections.Count > 0)
                            {
                                anomalyList[i] = anomalyNarrative.Text;
                                anyAnomalyCorrected = true;
                                foreach (var c in anomalyNarrative.Corrections)
                                    _logger.LogWarning(
                                        "Anomaly percentage reconciled at read time result {ResultId} analysis {AnalysisId} " +
                                        "tenant {TenantId}: clause=\"{Clause}\" stated={Stated:F1}% expected={Expected:F1}% denominator={Denominator}",
                                        detail.ResultId, detail.AnalysisId, tenantId,
                                        c.Clause, c.StatedPercent, c.ExpectedPercent, c.Denominator);
                            }

                            if (anomalyNarrative.UnresolvedClauses.Count > 0)
                                allUnresolved.AddRange(anomalyNarrative.UnresolvedClauses);
                        }

                        if (anyAnomalyCorrected)
                            response.Anomalies = JsonSerializer.Serialize(anomalyList, BcraJsonOpts);

                        if (allUnresolved.Count > 0)
                            _logger.LogWarning(
                                "Anomalies have {Count} unverifiable percentage clause(s) at read time result " +
                                "{ResultId} analysis {AnalysisId} tenant {TenantId}: {Clauses}",
                                allUnresolved.Count, detail.ResultId, detail.AnalysisId, tenantId,
                                string.Join(" | ", allUnresolved));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Alert/executive-summary/anomaly reconciliation failed at read time for result {ResultId} tenant {TenantId}; " +
                "returning the persisted text unchanged.", detail.ResultId, tenantId);
        }

        // Set AFTER the read-time repair above, so `observations` carries the same corrected text
        // the legacy `riskAlerts` field does rather than the raw persisted wording.
        ApplyDetailChannels(response, detail.CatalogueAlerts, detail.ScoreBreakdownJson,
            analysis?.AnalysisKind ?? AnalysisKinds.Balance, detail.RescoredAt);

        // Manual corrections of the balance (Phase 10-F3a), beside the integrity verdict above.
        response.BalanceCorrections = await BuildBalanceCorrectionsAsync(response.BalanceJson, cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Fills the DETAIL-ONLY re-score fields: the latest re-score, <c>canRescore</c> with its blocking
    /// errorCode, and the dates of the sources behind the current score.
    /// </summary>
    /// <remarks>
    /// <c>canRescore</c> applies <see cref="RescoreEligibility"/>, the endpoint's own rules, minus the
    /// cooldown. The vote table is not read here: votes exist only while an analysis is PreApproved
    /// (they are wiped on a return to review), so the row's status and decision record already decide
    /// it; POST {id}/rescore still reads the table, inside its write transaction. The one extra query
    /// — the document's status — runs only for an analysis that passes every row rule, and the whole
    /// check is fault-isolated: it is context, and must never cost an analyst the detail.
    /// </remarks>
    private async Task ApplyRescoreFieldsAsync(
        AnalysisResultResponse response,
        AnalysisResultDetailProjection detail,
        Analysis? analysis,
        Entity? entity,
        string? rescoredByName,
        string tenantId,
        CancellationToken cancellationToken)
    {
        response.RescoredAt     = detail.RescoredAt;
        response.RescoredBy     = detail.RescoredBy?.ToString();
        response.RescoredByName = rescoredByName;
        response.RescoreCount   = detail.RescoreCount;
        response.CanRescore     = false;

        // Live only when the stored deuda is a real answer — a LookupFailed block is present but is not
        // one. A row with no readable estado keeps the earlier Complete fallback.
        var bcraAnswered = detail.BcraDeudaEstadoRaw is not null
            ? BcraSituacionRule.Read(detail.BcraDeudaEstadoRaw, null).Kind != BcraDeudaReadingKind.Excluded
            : string.Equals(detail.EnrichmentStatus, nameof(BcraEnrichmentOutcome.Complete), StringComparison.Ordinal);
        response.Sources = RescoreSources.ForCurrentResult(
            detail.LatestRescoreSourcesJson, detail.GeneratedAt, bcraAnswered, entity,
            RescoreSources.ParseFetchedAt(detail.BcraDeudaFetchedAtRaw));

        if (analysis is null)
            return;

        try
        {
            var hasBalance = !string.IsNullOrWhiteSpace(detail.BalanceJson);
            var blocked    = RescoreEligibility.Evaluate(analysis, hasBalance, hasVotes: false, documentProcessingStatus: null);

            if (blocked is null && analysis.DocumentId is { } documentId)
            {
                var document = await _documentRepository
                    .GetByIdAsync(documentId, tenantId, cancellationToken)
                    .ConfigureAwait(false);

                blocked = RescoreEligibility.Evaluate(analysis, hasBalance, hasVotes: false, document?.ProcessingStatus);
            }

            response.CanRescore           = blocked is null;
            response.RescoreBlockedReason = blocked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Re-score eligibility could not be read for result {ResultId} tenant {TenantId}; canRescore reported false.",
                detail.ResultId, tenantId);
        }
    }

    // ── POST /api/analyses/{id}/rescore ───────────────────────────────────────────

    /// <summary>
    /// "Reanalizar con datos actualizados": re-scores a balance analysis IN PLACE from its persisted
    /// extraction, with a live BCRA lookup and the entity's current LUFE / CASFOG / ARCA / NOSIS
    /// snapshots. No Claude extraction, no new Analyses row, no monthly quota; the Haiku narrative is
    /// logged in ApiCallLogs as usual. Every refusal leaves the analysis exactly as it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PERSONA HUMANA ("Reanalizar" on a persona analysis): the same button, eligibility, cooldown, history
    /// and error envelope, but the analysis is RE-EVALUATED in place — the solicitante and every fiador on
    /// their current legajos, the BCRA snapshot under the POST /api/person-analyses retry rule, the tenant's
    /// current pf_ parameters, the same terms and fiador inputs. It refuses, before writing anything, with
    /// the POST's own codes: 404 entity_not_found, 400 entity_not_person, 409 consent_required,
    /// legajo_not_ready, fiador_is_solicitante and fiador_legajo_not_ready (with fiadorId and reason,
    /// legajo_changed included). No quota either. See PersonAnalysisRescoreService.
    /// </para>
    /// <para>
    /// The service is taken with [FromServices] rather than through the constructor: this controller is
    /// built by hand in nineteen test files, and the action is the only place that needs it.
    /// </para>
    /// </remarks>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="rescoreService">The re-score service.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 with the GET /api/analyses/{id} body plus <c>rescore</c>; 404; 409 (person_analysis_not_rescorable,
    /// analysis_result_missing, analysis_not_pending, analysis_has_votes, analysis_processing,
    /// analysis_rescore_in_progress); 429 too_many_requests and 503 bcra_unavailable, both with
    /// Retry-After; 500 rescore_failed.
    /// </returns>
    [HttpPost("{id:guid}/rescore")]
    [Authorize(Roles = Roles.AnalysisCreators)]
    [ProducesResponseType(typeof(AnalysisResultResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public async Task<IActionResult> RescoreAsync(
        Guid id,
        [FromServices] IAnalysisRescoreService rescoreService,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null)
            return Unauthorized();

        var outcome = await rescoreService
            .RescoreAsync(
                id,
                tenantId,
                currentUser.UserId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                HttpContext.TraceIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        // A bare 404 is the result itself; a 404 with an errorCode is a persona refusal (entity_not_found).
        if (outcome.StatusCode == 404 && outcome.ErrorCode is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        if (!outcome.IsSuccess)
            return RescoreRefusal(outcome);

        // The write is committed: from here a failure to READ the detail back must not turn a done
        // re-score into an error the analyst would retry. CancellationToken.None for the same reason.
        var response = await BuildDetailResponseAsync(id, tenantId, CancellationToken.None).ConfigureAwait(false);
        if (response is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        response.Rescore = outcome.Summary;
        return Ok(response);
    }

    // ── GET /api/analyses/{id}/rescores ───────────────────────────────────────────

    /// <summary>
    /// The re-scores of an analysis, newest first: when, by whom, and the score, level and enrichment
    /// status before and after — for a persona humana re-evaluation, also the suggestion before and after.
    /// An empty array when it was never re-scored.
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="rescoreService">The re-score service.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with <see cref="AnalysisRescoreHistoryResponse"/> items, or 404.</returns>
    [HttpGet("{id:guid}/rescores")]
    [ProducesResponseType(typeof(IReadOnlyList<AnalysisRescoreHistoryResponse>), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetRescoresAsync(
        Guid id,
        [FromServices] IAnalysisRescoreService rescoreService,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var history = await rescoreService
            .GetHistoryAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (history is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        var names = await ResolveActorNamesAsync(
            history.Select(h => (Guid?)h.RescoredBy), cancellationToken).ConfigureAwait(false);

        return Ok(history
            .Select(h => new AnalysisRescoreHistoryResponse(
                h.RescoreId,
                h.RescoredAt,
                h.RescoredBy.ToString(),
                ActorName(h.RescoredBy, names),
                h.PreviousScore,
                h.NewScore,
                h.PreviousLevel,
                h.NewLevel,
                h.PreviousEnrichmentStatus,
                h.NewEnrichmentStatus,
                h.PreviousSuggestion,
                h.NewSuggestion))
            .ToList());
    }

    /// <summary>
    /// A refused re-score as ProblemDetails with <c>errorCode</c> and, for 429 / 503, Retry-After — the
    /// shape <see cref="BackgroundRefusal"/> gives the trigger's admission refusals.
    /// </summary>
    private ObjectResult RescoreRefusal(AnalysisRescoreOutcome outcome)
    {
        var errorCode = outcome.ErrorCode ?? RescoreErrorCodes.Failed;
        var detail    = outcome.Detail
            ?? (errorCode == RescoreErrorCodes.TooManyRequests
                ? $"Este análisis se reanalizó hace instantes. Probá de nuevo en {outcome.RetryAfterSeconds ?? 1} segundos."
                : RescoreEligibility.Detail(errorCode));

        if (outcome.RetryAfterSeconds is { } retryAfter)
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status   = outcome.StatusCode,
            Title    = outcome.StatusCode switch
            {
                400 => "Bad Request",
                404 => "Not Found",
                409 => "Conflict",
                429 => "Too Many Requests",
                503 => "Service Unavailable",
                _   => "Internal Server Error"
            },
            Detail   = detail,
            Type     = ErrorTypeBase + errorCode,
            Instance = HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = errorCode;
        if (outcome.RetryAfterSeconds is { } seconds)
            problem.Extensions["retryAfter"] = seconds;
        foreach (var (key, value) in outcome.Extensions ?? new Dictionary<string, object?>())
            problem.Extensions[key] = value;

        return StatusCode(outcome.StatusCode, problem);
    }

    // ── GET /api/analyses/{id}/context ────────────────────────────────────────────

    /// <summary>
    /// Returns the five STORED source payloads of an analysis — LUFE, Fiscal/ARCA, CASFOG, NOSIS and
    /// BCRA — the "Contexto" section of the analysis detail. Read-only and idempotent: it reads the
    /// stored columns and consults no external source, writes nothing, and cannot re-freeze a
    /// snapshot. These are point-in-time copies taken when the analysis ran, deliberately NOT the
    /// live entity values; those are GET /api/entities/{entityId}/bcra, /casfog and /nosis.
    ///
    /// <para>
    /// Split out of GET /api/analyses/{id} because none of them renders until the user opens that
    /// section, while all of them cost blob I/O on every detail request. The four snapshots went
    /// first (LufeDataJson alone is 26KB on a representative row, and together they were about half
    /// that request's blob I/O). BcraDataJson followed: it is by far the largest single one — #149
    /// measured 859.295 characters on a 2930-cheque profile — and the Score section that justified
    /// keeping it on the detail turned out to read exactly two scalars off it, which now arrive as
    /// <c>bcraSummary</c>.
    /// </para>
    ///
    /// <para>
    /// <c>bcra.cheques.detalle</c> is the one thing this endpoint does NOT carry. It is served by
    /// GET {id}/bcra/cheques instead — see <see cref="GetBcraChequesAsync"/> and
    /// <see cref="AnalysisContextResponse.Bcra"/>. Every cheques aggregate stays here, as do
    /// <c>deuda</c>, <c>macro</c> and <c>cambiario</c>.
    /// </para>
    ///
    /// <para>
    /// The detail response still declares all five keys; they simply come back null now.
    /// </para>
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="AnalysisContextResponse"/> — all five fields are null when the analysis
    /// exists but stored none of them, and <c>bcra.cheques.detalle</c> is always null — or 404 Not
    /// Found if there is no such result in this tenant.
    /// </returns>
    [HttpGet("{id:guid}/context")]
    [ProducesResponseType(typeof(AnalysisContextResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetContextAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var snapshot = await _analysisResultRepository
            .GetContextSnapshotByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        // The same helpers the detail used before each field was split off, so a blob that parsed
        // there parses identically here. Each is fault-isolated: unparseable JSON in one source
        // leaves that field null and never affects the other four.
        //
        // Bcra deliberately uses TryDeserializeBcraData/BcraJsonOpts rather than the SnapshotJsonOpts
        // the other four share. That is not an oversight: those four are copied verbatim from the
        // entity and need AllowReadingFromString for numerics stored as strings, while BcraDataJson
        // is written by our own serialiser and was always read with BcraJsonOpts on the detail.
        // Using the same options it has always been read with is what makes this move invisible.
        var bcra = snapshot.BcraDataJson is not null ? TryDeserializeBcraData(snapshot.BcraDataJson) : null;

        // cheques.detalle comes off this payload and is served by GET {id}/bcra/cheques instead.
        // It is one object per rejected cheque, ~295 bytes serialised, and on a cheque-heavy CUIT it
        // is very nearly the whole blob — #149 measured 859.295 characters on a 2930-cheque profile.
        // The Contexto tab renders the aggregates and the causal chart when it opens; the
        // row-by-row table is a separate thing the analyst scrolls to, so it now costs a separate
        // request rather than being paid for on every open.
        //
        // Every aggregate beside it survives untouched. They are NOT derived from this array at read
        // time: BcraService.MapCheques computes totalRechazados, totalImpagos, the two montos, the
        // first/last rejection dates and the judicial/revision flags once when the snapshot is
        // written, and persists them. Nulling the array here cannot disturb any of them, and
        // causales is a stored aggregate too.
        //
        // Mutating in place is safe: TryDeserializeBcraData is a plain JsonSerializer.Deserialize
        // with no memoisation, so this local is a fresh graph nobody else holds. MapToResponse calls
        // the same helper for its own instance and is unaffected.
        //
        // Null rather than []: BcraChequesData.Detalle documents [] as "no cheques reported", which
        // would flatly contradict a non-zero totalRechazados three keys above it. Null is documented
        // as "legacy stored data", but that reading cannot mislead here because the value is now
        // CONSTANT on this endpoint — no row ever comes back with the array populated — and
        // totalRechazados remains the authoritative count either way.
        if (bcra?.Cheques is not null)
            bcra.Cheques.Detalle = null;

        return Ok(new AnalysisContextResponse
        {
            Lufe   = snapshot.LufeDataJson   is not null ? TryDeserializeLufeData(snapshot.LufeDataJson)     : null,
            Fiscal = snapshot.FiscalDataJson is not null ? TryDeserializeFiscalData(snapshot.FiscalDataJson) : null,
            Casfog = snapshot.CasfogDataJson is not null ? TryDeserializeCasfogData(snapshot.CasfogDataJson) : null,
            Nosis  = snapshot.NosisDataJson  is not null ? TryDeserializeNosisData(snapshot.NosisDataJson)   : null,
            Bcra   = bcra
        });
    }

    // ── GET /api/analyses/{id}/bcra/cheques ───────────────────────────────────────

    /// <summary>
    /// Returns the per-cheque rows of an analysis's frozen BCRA snapshot — the cheque-by-cheque
    /// table of the "Contexto" BCRA tab. Read-only and idempotent: it reads ONE stored column,
    /// consults no external source, writes nothing, and cannot re-freeze a snapshot. These are
    /// point-in-time copies taken when the analysis ran, deliberately NOT the live entity values;
    /// those are GET /api/entities/{entityId}/bcra.
    ///
    /// <para>
    /// Split out of GET /api/analyses/{id}/context, which no longer carries <c>cheques.detalle</c>.
    /// That array is ~295 bytes per rejected cheque and dominates the BCRA payload — #149 measured
    /// 859.295 characters for the blob on a 2930-cheque profile — while the tab renders the
    /// aggregates and the causal chart on open and the table only when the analyst scrolls to it.
    /// This mirrors the per-source reads on GET /api/entities/{id}/bcra, /casfog and /nosis.
    /// </para>
    ///
    /// <para>
    /// The read names two columns —the key and <c>BcraDataJson</c>— and not the four frozen
    /// snapshots beside it, so opening this table does not pay for LufeDataJson.
    /// </para>
    ///
    /// <para>
    /// Each cheque's wire shape is unchanged: the element type is the same
    /// <see cref="BcraChequeDetalle"/> that was nested inside the blob, so a client that parsed it
    /// off <c>/context</c> parses it identically here.
    /// </para>
    ///
    /// <para>
    /// ── The default response is a SUMMARY, not the array ──────────────────────────────────
    /// Splitting the array onto its own URL fixed <c>/context</c> but only relocated the cost: this
    /// endpoint still shipped all 2936 rows, and nearly all of what the Portal did with them was
    /// fold them into a ~24-point monthly series in the browser. That fold now happens here
    /// (<see cref="BcraChequesEvolucion.Build"/>, a rule-for-rule port of the Portal's
    /// <c>buildChequesEvolucion</c>), so the default carries <c>evolucion</c>, <c>dateBounds</c>,
    /// the <see cref="AnalysisBcraChequesResponse.RecientesCount"/> most recent rows and the true
    /// <c>total</c> — and NOT the array. <c>?full=true</c> opts back into it for the "ver todos"
    /// table, the one view that genuinely needs every row.
    /// </para>
    ///
    /// <para>
    /// CONTRACT CHANGE: <c>cheques</c> is null unless <c>full=true</c> is passed. Null means "not
    /// requested", deliberately distinct from an empty array — which would assert that the analysis
    /// HAS no cheques, a claim this endpoint would then be making falsely on exactly the
    /// cheque-heavy snapshots the split exists for.
    /// </para>
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="full">
    /// When true the complete <c>detalle</c> array rides along under <c>cheques</c>, in addition to
    /// the summary. Defaults to false, which is the light response.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="AnalysisBcraChequesResponse"/> — a well-formed EMPTY summary (empty
    /// <c>meses</c>, null bounds, empty <c>recientes</c>, <c>total</c> 0) when the analysis froze no
    /// BCRA snapshot, the snapshot no longer parses, BCRA reported no cheques, or the stored
    /// document predates the field — or 404 Not Found if there is no such result in this tenant,
    /// which is the only 404 this endpoint returns.
    /// </returns>
    [HttpGet("{id:guid}/bcra/cheques")]
    [ProducesResponseType(typeof(AnalysisBcraChequesResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetBcraChequesAsync(
        Guid id,
        [FromQuery] bool full,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var snapshot = await _analysisResultRepository
            .GetBcraSnapshotByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Null means no such result in this tenant. A row that exists but froze no snapshot
        // materialises an instance with a null blob, which is a 200 below — the same 404-vs-null
        // split GetContextAsync makes, and the reason the repository projects into a class rather
        // than a bare string.
        if (snapshot is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        // Same helper and same options /context reads this column with, so a blob that renders
        // there yields the same rows here. An unparseable blob degrades to an empty list rather
        // than a 500, matching how /context nulls the field instead of failing the request.
        var bcra = snapshot.BcraDataJson is not null ? TryDeserializeBcraData(snapshot.BcraDataJson) : null;

        var detalle = bcra?.Cheques?.Detalle;

        // No rows is ordinary, never an error — the four cases the caller cannot tell apart, plus an
        // unparseable blob. The summary is still emitted in full rather than short-circuited to a
        // bare object, so the response shape does not depend on whether the snapshot had data: an
        // empty series, null bounds, no recientes, total 0. Build(null) is total and returns exactly
        // that all-zero result, so the empty path needs no special case of its own.
        var evolucion = BcraChequesEvolucion.Build(detalle);

        // Bounds come off the rejection date only. fechaPago can fall outside this range — it does
        // whenever a cheque was settled after the last rejection, which is why the series ends at
        // max(last rejection, last payment) while these bounds do not.
        var fechasRechazo = detalle?
            .Where(c => c.FechaRechazo is not null)
            .Select(c => c.FechaRechazo!.Value)
            .ToList();

        var dateBounds = fechasRechazo is { Count: > 0 }
            ? new ChequeDateBounds { Min = fechasRechazo.Min(), Max = fechasRechazo.Max() }
            : new ChequeDateBounds();

        return Ok(new AnalysisBcraChequesResponse
        {
            Evolucion  = evolucion,
            DateBounds = dateBounds,
            // The head of the stored fechaRechazo-descending order, so these really are the recent
            // ones. Take() on a null list would throw, hence the null-coalesce to an empty one.
            Recientes  = (detalle ?? []).Take(AnalysisBcraChequesResponse.RecientesCount).ToList(),
            Total      = detalle?.Count ?? 0,
            // Only on request. Left null otherwise — see the property's own note on why that is not
            // an empty array.
            Cheques    = full ? detalle ?? [] : null
        });
    }

    // ── GET /api/analyses/{id}/ratios ─────────────────────────────────────────────

    /// <summary>
    /// Returns the computed ratios and the score breakdown of one analysis — the "Ratios calculados
    /// por FinancIA" panel on the PyME (entity) detail, which renders them for the entity's latest
    /// analysis. Read-only: three columns, no external call, no write.
    ///
    /// <para>
    /// Split out of GET /api/analyses/{id} for the same reason /context was in #143. That panel needs
    /// two columns and was fetching the whole detail to get them: even after #143 the detail names
    /// nine <c>nvarchar(max)</c> columns — ~73KB on a representative row — so BalanceJson,
    /// BcraDataJson, RiskAlerts, CatalogueAlerts, Anomalies, ExecutiveSummary and ScoringNarrative
    /// all came off disk for a small table that shows none of them. On warm serverless Azure SQL that
    /// read was also the tail the PyME page's credit-lines and notes queued behind.
    /// </para>
    ///
    /// <para>
    /// <c>ratiosJson</c> and <c>scoreBreakdown</c> are byte-for-byte what GET /api/analyses/{id}
    /// already returns for those two fields — same names, same types, same null semantics — so a
    /// caller swaps the URL and changes nothing else. The detail response still carries both; this is
    /// purely additive.
    /// </para>
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="AnalysisRatiosResponse"/> — both fields are null when the analysis
    /// exists but was never scored — or 404 Not Found if there is no such result in this tenant.
    /// </returns>
    [HttpGet("{id:guid}/ratios")]
    [ProducesResponseType(typeof(AnalysisRatiosResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetRatiosAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var ratios = await _analysisResultRepository
            .GetRatiosByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (ratios is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        // RatiosJson is echoed verbatim — the client parses it, exactly as it does off the detail.
        // ScoreBreakdown goes through the SAME TryDeserializeBreakdown the detail mapper uses, so a
        // blob that parsed there parses identically here and re-serialises with its own keys intact.
        return Ok(new AnalysisRatiosResponse
        {
            RatiosJson     = ratios.RatiosJson,
            ScoreBreakdown = ratios.ScoreBreakdownJson is not null
                ? TryDeserializeBreakdown(ratios.ScoreBreakdownJson)
                : null
        });
    }

    // ── GET /api/analyses/{id}/previous-comparison ────────────────────────────────

    /// <summary>
    /// "Cambios respecto al análisis anterior": this analysis against the previous decided analysis
    /// (Approved, PreApproved or Rejected) of the same entity and kind, created before it, each row with its
    /// delta, deltaPct and the BFF's trend. Read-only.
    /// <para>
    /// Company (Balance): groups <c>score</c>, <c>ratios</c>, <c>balance</c>, <c>bcra</c>, <c>lufe</c>. The
    /// previous one must have a score and a balance that was read.
    /// </para>
    /// <para>
    /// Persona humana: groups <c>suggestion</c>, <c>filters</c>, <c>indicators</c> (cobertura_servicio_deuda,
    /// cuota_ingreso, solvencia_pn, patrimonio_operativo), <c>bcra</c>, <c>coverage</c>, <c>terms</c>; the
    /// previous one must have a stored evaluation. Each side header carries <c>currency</c>, the currency of
    /// its <c>amount</c> rows. Notes: <c>nominal_amounts</c>, <c>engine_changed</c> (evaluation engine
    /// version), <c>parameters_changed</c> (frozen pf_ cuts), <c>currency_changed</c> (amount rows then carry
    /// no delta, deltaPct or trend), <c>terms_changed</c> (amount, installment, periodicity or fiadores).
    /// </para>
    /// <para>
    /// deltaPct is null when previous ≤ 0 or when the two sides have opposite signs.
    /// </para>
    /// <para>
    /// Always 200 for a result of this tenant: <c>{ available: false, reason }</c> when there is nothing to
    /// compare (<c>no_previous</c>; <c>no_result</c> when this analysis has no score, or for a persona humana
    /// no stored evaluation; <c>unsupported_kind</c> for any other kind). Not the re-score "Qué cambió",
    /// which compares a re-score with its own earlier run.
    /// </para>
    /// <para>
    /// The service is taken with [FromServices] rather than through the constructor, for the reason given
    /// on <see cref="RescoreAsync"/>: this controller is built by hand across the test suite.
    /// </para>
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="comparisonService">The comparison service.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 with <see cref="AnalysisComparisonResponse"/>, or 404 when the result is not in this tenant.</returns>
    [HttpGet("{id:guid}/previous-comparison")]
    [Authorize(Roles = Roles.All)]
    [ProducesResponseType(typeof(AnalysisComparisonResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetPreviousComparisonAsync(
        Guid id,
        [FromServices] IAnalysisComparisonService comparisonService,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var comparison = await comparisonService
            .CompareWithPreviousAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (comparison is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        return Ok(comparison);
    }

    // ── GET /api/analyses/{id}/label ──────────────────────────────────────────────

    /// <summary>
    /// Returns just the analysed entity's name and id — the breadcrumb label on the analysis detail
    /// page. Read-only: five columns across three tables, no external call, no write.
    ///
    /// <para>
    /// Split out of GET /api/analyses/{id} for the same reason /ratios was in #145, but for a
    /// heavier caller. The Portal's breadcrumb hook runs in its shell, OUTSIDE the page transition,
    /// so it is a SECOND full read on every navigation to an analysis detail — and the detail is the
    /// heaviest read in the app: even after #143 moved four frozen snapshots to /context and the
    /// BCRA split moved a fifth, it still names seven <c>nvarchar(max)</c> columns. All of that came
    /// off disk so a breadcrumb could print one string.
    /// </para>
    ///
    /// <para>
    /// One statement, ResultId → AnalysisId → EntityId → BusinessName, rather than the
    /// resolve-then-fetch chain the detail runs: that one loads the Analyses row unprojected
    /// (<c>ReviewNotes</c> is <c>nvarchar(max)</c>) and then the Entities row unprojected (all five
    /// source snapshots). Three round-trips, two naming a blob, for a label.
    /// </para>
    ///
    /// <para>
    /// <c>entityName</c> and <c>entityId</c> are byte-for-byte what GET /api/analyses/{id} already
    /// returns for those two fields — same names, same types — so a caller swaps the URL and changes
    /// nothing else. The detail response still carries both; this is purely additive.
    /// </para>
    /// </summary>
    /// <param name="id">The result identifier, as returned by GET /api/analyses/{id}.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 OK with <see cref="AnalysisLabelResponse"/>, or 404 Not Found if there is no such result
    /// in this tenant. <c>entityName</c> is never null on a 200 — it arrives through an INNER JOIN
    /// over NOT NULL foreign keys onto a NOT NULL column.
    /// </returns>
    [HttpGet("{id:guid}/label")]
    [ProducesResponseType(typeof(AnalysisLabelResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetLabelAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var label = await _analysisResultRepository
            .GetLabelByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (label is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        return Ok(new AnalysisLabelResponse
        {
            EntityName = label.EntityName,
            EntityId   = label.EntityId
        });
    }

    // ── PATCH /api/analyses/{id}/decision ─────────────────────────────────────────

    /// <summary>
    /// Records one step of the two-step decision flow on an analysis result.
    ///
    /// <para>
    /// WHICH step it is depends on the caller's role and, for an Analyst, on their approval limit
    /// for the currency being committed:
    /// </para>
    /// <list type="bullet">
    /// <item>Analyst approving WITHIN their limit → 'Approved' in one step; the final record is
    /// written and there is no pre-approval record.</item>
    /// <item>Analyst approving ABOVE their limit (or with no limit configured for that currency)
    /// → 'PreApproved'; only the pre-approval record is written, and it waits for a second
    /// signature. This is not an error and not a 403 — it is the normal delegation path.</item>
    /// <item>RiskManager/Admin on a 'PreApproved' analysis → confirm (optionally changing the
    /// amount), reject, or return it for rework. They have no ceiling.</item>
    /// </list>
    /// <para>
    /// An Analyst may never confirm a pre-approval, including their own — that is the whole point of
    /// the second signature — and gets a 403. Re-deciding an analysis that is not pre-approved
    /// remains allowed, as before.
    /// </para>
    /// <para>
    /// Score credibility 1b-5, the decision review gate (<see cref="DecisionReviewGate"/>): an Approved on a
    /// balance analysis whose score reliability is <c>provisional</c> or <c>none</c> is 409
    /// <c>caveats_not_acknowledged</c> without <c>acknowledgedCaveats: true</c> (a Rejected never needs it); one
    /// whose figures failed integrity is 409 <c>figures_review_required</c> without a 20+ character note — the
    /// <c>figuresReviewNote</c> on an approval, that or (when empty) the <c>reviewNotes</c> on a rejection (both
    /// 409s carry <c>reliabilityLevel</c> and <c>reasonCodes</c>). Checked before any write, vote or notification.
    /// </para>
    /// </summary>
    /// <param name="id">The analysis result identifier (ResultId), as returned by GET /api/analyses/{id}.</param>
    /// <param name="request">The decision payload.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the updated <see cref="AnalysisResultResponse"/>, or 400/403/404/409.</returns>
    [HttpPatch("{id:guid}/decision")]
    [Authorize(Roles = Roles.Deciders)]
    [ProducesResponseType(typeof(AnalysisResultResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> DecideAsync(
        Guid id,
        [FromBody] AnalysisDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        // Validate the decision value (case-sensitive to match [dbo].[Analyses] CK_Analyses_Status).
        var decision   = request.Decision?.Trim();
        var isApproved = string.Equals(decision, "Approved", StringComparison.Ordinal);
        var isRejected = string.Equals(decision, "Rejected", StringComparison.Ordinal);
        var isReturned = string.Equals(decision, PendingReviewStatus, StringComparison.Ordinal);
        if (!isApproved && !isRejected && !isReturned)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "La decisión debe ser 'Approved', 'Rejected' o 'PendingReview'.");

        // The amount is what the limit is compared against, so on an approval it is not optional and
        // there is nothing sane to fall back to: RequestedAmount is nullable and [dbo].[Analyses]
        // carries no currency at all. Rejections and returns commit nothing, so both are ignored there.
        //
        // Normalised the same way CreditLinesController normalises a cupo currency, and for a sharper
        // reason here: the code is the KEY of the approval-limit lookup, so an un-normalised 'ars'
        // would miss a perfectly good ARS limit and silently escalate the decision to a pre-approval.
        var currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (isApproved)
        {
            if (request.Amount is not > 0m)
                return ValidationProblem("amount", "El monto es obligatorio y debe ser mayor a cero para aprobar.");

            if (!Currencies.IsKnown(currency))
                return ValidationProblem("currency", $"La moneda es obligatoria y debe ser una de: {Currencies.KnownList}.");
        }

        // Score credibility 1b-5: the figures-review note, measured trimmed. Only an approval or a rejection is
        // gated (a return is never), so only there is it read — and bounded — like the amount above.
        var figuresReviewNote = (isApproved || isRejected)
            ? DecisionReviewGate.NormaliseNote(request.FiguresReviewNote)
            : null;
        if (figuresReviewNote is { Length: > DecisionReviewGate.MaxNoteLength })
            return ValidationProblem("figuresReviewNote",
                $"La nota de revisión de cifras no puede superar los {DecisionReviewGate.MaxNoteLength} caracteres.");

        // The operations behind the figure. Optional, and validated only when supplied — a decision
        // with none is as valid as it was before this field existed. When they ARE supplied they are
        // checked and the caller is told what is wrong, because silently dropping a proposal an
        // analyst typed is the exact defect this field exists to fix.
        //
        // Only inside an approval, and for the same reason the amount and the currency above are:
        // a rejection and a return commit nothing, so they IGNORE the array outright. There is no
        // decision currency on either to validate an operation's currency against, and nothing
        // would be stored in any case — the confirmed column is nulled below.
        string? operationsJson = null;
        if (isApproved)
        {
            var (json, operationsError) = BuildDecisionOperationsJson(request.Operations, currency);
            if (operationsError is not null) return operationsError;
            operationsJson = json;
        }

        // The role comes off the DATABASE row, not the token's claim. Same reasoning as
        // CreditLinesController.ResolveCurrentUserAsync: a role changed through the settings screen
        // does not reach an already-issued token, and this check WIDENS access (it is what lets a
        // RiskManager settle someone else's pre-approval), so the row the change actually wrote is
        // the safer authority. The claim still guards the door via [Authorize(Roles = ...)] above.
        //
        // Roles.IsConfirmer rather than two inline comparisons since the quorum engine landed: the
        // engine uses the SAME predicate to decide whose signature counts, and if the two ever
        // disagreed the quorum would either count people this endpoint refuses — leaving an analysis
        // waiting on a signature nobody can give — or refuse people it counts.
        var isConfirmer = Roles.IsConfirmer(currentUser.Role);

        // Resolve the result tenant-scoped, then load the analysis it belongs to.
        var result = await _analysisResultRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        var analysis = await _analysisRepository
            .GetByIdAsync(result.AnalysisId, cancellationToken)
            .ConfigureAwait(false);
        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el análisis.");

        // Guard: an Approved analysis cannot be rejected while the credit it justifies is still
        // OPERATIVE — either an ACTIVE cupo (which still lends) or a non-cancelled operation (disbursed
        // money). A merely Suspended cupo lends nothing further, so it no longer blocks on its own; the
        // outstanding money it left behind is caught by the operation check. The remedies differ: an
        // active cupo can be suspended and then the analysis rejected; a live operation is cleared only
        // once it reaches 'Cancelada' (persisted status is 'Aprobada', 'Vigente' or 'Cancelada' — there
        // is no settled/expired state to wait for, and an 'Aprobada' operation blocks exactly like a
        // 'Vigente' one because it already reserves cupo). This block only returns
        // early for isRejected && currently-Approved && still-operative; every other transition (incl.
        // re-deciding an analysis nothing operative depends on) falls through unchanged to the write
        // below, so the 409 never mutates the analysis.
        if (isRejected && string.Equals(analysis.Status, "Approved", StringComparison.Ordinal))
        {
            var activeCupoIds = await _creditLineRepository
                .GetActiveIdsByOriginAnalysisAsync(analysis.AnalysisId, tenantId, cancellationToken)
                .ConfigureAwait(false);
            var hasLiveOperations = await _operationRepository
                .HasNonCancelledByAnalysisAsync(analysis.AnalysisId, tenantId, cancellationToken)
                .ConfigureAwait(false);

            if (activeCupoIds.Count > 0 || hasLiveOperations)
            {
                var activeCount = activeCupoIds.Count;
                // The analyst-facing detail names the COUNT, never the ids: the Portal shows it
                // verbatim in a banner, where a raw GUID is noise. It distinguishes the two causes
                // because the remedy differs, and promises nothing the guard will not honour — an
                // analyst who follows it (suspend the active cupo(s); the live operation reaching
                // 'Cancelada') actually gets through. The operation clause states the CONDITION rather
                // than an action, since the guard clears on cancellation regardless of who performs it.
                // The ids travel in the structured log (below) for diagnosis and in
                // Extensions["affectedCreditLineIds"] for the client to act on.
                var detail = (activeCount, hasLiveOperations) switch
                {
                    (1, false) =>
                        "No se puede rechazar el análisis: el crédito que justifica sigue operativo. " +
                        "Tiene 1 cupo activo; suspéndalo y luego podrá rechazar el análisis.",
                    (_, false) =>
                        $"No se puede rechazar el análisis: el crédito que justifica sigue operativo. " +
                        $"Tiene {activeCount} cupos activos; suspéndalos y luego podrá rechazar el análisis.",
                    (0, true) =>
                        "No se puede rechazar el análisis: el crédito que justifica sigue operativo. " +
                        "Existe al menos una operación vigente y el rechazo no la revierte; podrá rechazar el análisis " +
                        "una vez que esa operación esté cancelada.",
                    (1, true) =>
                        "No se puede rechazar el análisis: el crédito que justifica sigue operativo. " +
                        "Tiene 1 cupo activo y al menos una operación vigente; suspenda el cupo. Podrá rechazar el análisis " +
                        "cuando además la operación esté cancelada.",
                    (_, true) =>
                        $"No se puede rechazar el análisis: el crédito que justifica sigue operativo. " +
                        $"Tiene {activeCount} cupos activos y al menos una operación vigente; suspenda los cupos. Podrá " +
                        $"rechazar el análisis cuando además la operación esté cancelada.",
                };

                // Make the blocking reason diagnosable from logs, keeping the active cupo ids without
                // ever surfacing them in the UI. (false, false) is unreachable — the guard above
                // requires at least one operative cause.
                var blockReason = (activeCount > 0, hasLiveOperations) switch
                {
                    (true, false) => "ActiveCupos",
                    (false, true) => "LiveOperations",
                    _             => "ActiveCupos+LiveOperations",
                };
                _logger.LogInformation(
                    "Rejection blocked for analysis {AnalysisId} tenant {TenantId}: reason={BlockReason} activeCupos [{ActiveCupoIds}] liveOperations={HasLiveOperations}",
                    analysis.AnalysisId, tenantId, blockReason, string.Join(", ", activeCupoIds), hasLiveOperations);

                // Deliberate deviation from the uniform Problem(status,title,detail) helper: this 409
                // carries the ACTIVE cupo ids the client must act on (the ones to suspend). The Portal
                // links to the cupo from Extensions["affectedCreditLineIds"] — the only machine-readable
                // channel. The key is ALWAYS emitted: an empty array when the only cause is operations.
                var problem = new ProblemDetails { Status = 409, Title = "Conflict", Detail = detail };
                problem.Extensions["affectedCreditLineIds"] =
                    activeCupoIds.Select(cupoId => cupoId.ToString()).ToArray();
                return new ObjectResult(problem)
                {
                    StatusCode   = StatusCodes.Status409Conflict,
                    ContentTypes = { "application/problem+json" },
                };
            }
        }

        // ── Route the decision by role, and for an Analyst by their limit ─────────
        var wasPreApproved = string.Equals(analysis.Status, PreApprovedStatus, StringComparison.Ordinal);

        // Confirming a pre-approval is what the second signature IS, so it belongs to the roles the
        // pre-approval was escalated to. An Analyst is refused here even on an analysis they
        // pre-approved themselves — especially then.
        if (wasPreApproved && !isConfirmer)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "El análisis está pre-aprobado y sólo un RiskManager o un Admin puede confirmarlo, rechazarlo o devolverlo.");

        // Returning for rework is the confirmer's third option, alongside confirm and reject. It has
        // no meaning outside a pending confirmation, so it is refused anywhere else rather than
        // becoming a general-purpose way to reset an analysis.
        if (isReturned && !(wasPreApproved && isConfirmer))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "Devolver un análisis a revisión sólo es posible para un RiskManager o un Admin sobre un análisis pre-aprobado.");

        // ── Decision review gate (score credibility 1b-5) ─────────────────────────
        // A hard rule must never approve alone on unverified inputs: an approval (a pre-approval and a quorum
        // vote included) of a balance analysis whose score is 'provisional' or 'none' needs an explicit
        // acknowledgement; a rejection never does. One whose figures failed integrity also needs a written note:
        // the figures-review note on an approval, that or — when empty — the reason (reviewNotes) on a rejection.
        // The verdict is the detail's own — the one adapter, the same row scalars — and the rule is the one GET
        // /{id} exposes as decisionRequirements. Placed after the authorization 403s and BEFORE the limit
        // lookup, the quorum vote, the vote wipe, the write, the audit and the notification, so a refused
        // decision mutates nothing. A return for rework and a persona humana analysis are never gated.
        object? reliabilityReview = null;
        if ((isApproved || isRejected) && analysis.AnalysisKind == AnalysisKinds.Balance)
        {
            var reliability = ScoreReliabilityAdapter.Evaluate(
                new ScoreReliabilityRow(
                    AiSuggestedScore: result.AiSuggestedScore,
                    EnrichmentStatus: result.EnrichmentStatus,
                    AnalysisKind:     analysis.AnalysisKind,
                    FiscalYear:       analysis.FiscalYear,
                    GeneratedAt:      result.GeneratedAt,
                    RescoredAt:       result.RescoredAt),
                result.ScoreBreakdownJson).Reliability;

            if (reliability is not null && DecisionReviewGate.RequirementsFor(reliability) is { } requirements)
            {
                // The figures requirement is met by the figures-review note or, on a rejection whose figures-review
                // note is empty, by its reason. Whichever answered it is the note the audit row stores, and it is
                // capped like the figures-review note above — only when it is the one used, so an ungated
                // rejection's reason stays as unbounded as it always was.
                if (requirements.FiguresReviewNote)
                {
                    var (note, field) = DecisionReviewGate.ResolveNote(isRejected, figuresReviewNote, request.ReviewNotes);
                    if (note is { Length: > DecisionReviewGate.MaxNoteLength })
                    {
                        _logger.LogInformation(
                            "Decision refused by the review gate for result {ResultId} tenant {TenantId}: the {NoteField} note is too long, noteLength={NoteLength}",
                            result.ResultId, tenantId, field, note.Length);
                        return ValidationProblem(field,
                            $"El motivo no puede superar los {DecisionReviewGate.MaxNoteLength} caracteres.");
                    }

                    figuresReviewNote = note;
                }

                (string Code, string Detail)? refusal = null;
                if (isApproved && requirements.AcknowledgeCaveats && request.AcknowledgedCaveats != true)
                    refusal = (DecisionReviewGate.CaveatsNotAcknowledged, DecisionReviewGate.CaveatsNotAcknowledgedDetail);
                else if (requirements.FiguresReviewNote && (figuresReviewNote?.Length ?? 0) < DecisionReviewGate.MinNoteLength)
                    refusal = (DecisionReviewGate.FiguresReviewRequired,
                               isRejected
                                   ? DecisionReviewGate.RejectionReasonRequiredDetail
                                   : DecisionReviewGate.FiguresReviewRequiredDetail);

                var reasonCodes = reliability.Reasons.Select(r => r.Code).ToArray();

                if (refusal is { } refused)
                {
                    // The note is analyst free text: its LENGTH only, never its content.
                    _logger.LogInformation(
                        "Decision refused by the review gate for result {ResultId} tenant {TenantId}: errorCode={ErrorCode} level={ReliabilityLevel} noteLength={NoteLength}",
                        result.ResultId, tenantId, refused.Code, reliability.Level, figuresReviewNote?.Length ?? 0);

                    var problem = CodedDecisionProblem(409, "Conflict", refused.Code, refused.Detail);
                    var details = (ProblemDetails)problem.Value!;
                    details.Extensions["reliabilityLevel"] = reliability.Level;
                    details.Extensions["reasonCodes"]      = reasonCodes;
                    return problem;
                }

                // The audit record of what the decider was shown and confirmed. PascalCase, like every other
                // payload this controller audits. Written on the decision's EXISTING audit row below.
                reliabilityReview = new
                {
                    AcknowledgedCaveats = request.AcknowledgedCaveats,
                    ReliabilityLevel    = reliability.Level,
                    ReasonCodes         = reasonCodes,
                    PolicyVersion       = reliability.PolicyVersion,
                    FiguresReviewNote   = figuresReviewNote,
                };
            }
        }

        // An Analyst approving needs a limit that covers the amount. No row for the currency is the
        // default and means "not authorised to commit directly" — never "unlimited". A row with
        // MaxAmount NULL and CanApprove set is the explicit no-ceiling case.
        var mustPreApprove = false;
        if (isApproved && !isConfirmer)
        {
            var limit = await _userApprovalLimitRepository
                .GetAsync(currentUser.UserId, currency, tenantId, cancellationToken)
                .ConfigureAwait(false);

            mustPreApprove = limit is null
                          || !limit.CanApprove
                          || (limit.MaxAmount is not null && request.Amount > limit.MaxAmount);
        }

        var now = DateTime.UtcNow;

        // ── Persona humana decision rules (DDL 072) ───────────────────────────────
        // Only an APPROVAL of a persona analysis — a pre-approval, a one-step approval or a quorum vote.
        // The rules come from the tenant's current pf_ policy and the frozen suggestion, the same
        // resolution GET /{id} exposes as personEvaluation.decisionRules. A balance analysis never enters.
        string? personChecksJson = null;
        if (isApproved && analysis.AnalysisKind == AnalysisKinds.PersonaHumana)
        {
            var (rules, _, _) = await ResolvePersonDecisionRulesAsync(analysis.AnalysisId, tenantId, cancellationToken)
                .ConfigureAwait(false);

            // A coverage of the fiadores below the minimum does not block the approval, but makes the reason
            // mandatory (RequiresReason already carries it) and names why.
            if (rules.RequiresReason && string.IsNullOrWhiteSpace(request.ReviewNotes))
            {
                if (!rules.CounterGuaranteeBelowMin)
                {
                    return CodedDecisionProblem(400, "Bad Request", "reason_required",
                        "Aprobar el análisis de una persona humana exige un motivo (reviewNotes).");
                }

                var belowMin = CodedDecisionProblem(400, "Bad Request", "reason_required",
                    CounterGuaranteeBelowMinDetail(rules.CounterGuaranteeCoverage!));
                ((ProblemDetails)belowMin.Value!).Extensions["counterGuaranteeBelowMin"] = true;
                return belowMin;
            }

            if (request.CounterGuaranteesConfirmed != true || request.FdrLimitConfirmed != true)
                return CodedDecisionProblem(400, "Bad Request", "confirmations_required",
                    "Aprobar el análisis de una persona humana exige confirmar las contragarantías " +
                    "(counterGuaranteesConfirmed) y el límite del Fondo de Riesgo (fdrLimitConfirmed).");

            // Confirming the counter-guarantees while an operation goes out a sola firma is a contradiction
            // whenever the tenant requires a coverage. Checked before every write, the quorum vote included, so a
            // refused pre-approval, approval or confirmation leaves the analysis exactly as it was.
            if (!rules.SolaFirmaAllowed && request.Operations?.Any(o => o is not null && o.ASolaFirma) == true)
                return CodedDecisionProblem(400, "Bad Request", "person_sola_firma_not_allowed",
                    $"En persona humana la contragarantía tiene que cubrir al menos el {rules.MinCounterGuaranteeCoveragePct} % " +
                    "del aval: ninguna operación puede ser a sola firma.");

            if (rules.RequiresPreApproval)
            {
                // A suggested rejection is approved only with two signatures, whatever the amount and
                // whoever signs first: the first approval becomes a pre-approval even for a confirmer,
                // and that same person cannot then be the one who confirms it.
                if (!wasPreApproved)
                    mustPreApprove = true;
                else if (analysis.PreApprovedBy == currentUser.UserId)
                    return CodedDecisionProblem(403, "Forbidden", "self_confirmation_not_allowed",
                        "El análisis tiene rechazo sugerido: quien lo pre-aprobó no puede confirmarlo.");
            }

            personChecksJson = PersonEvaluationJson.Serialize(new AnalysisDecisionChecks
            {
                CounterGuaranteesConfirmed = true,
                FdrLimitConfirmed          = true,
                Reason                     = string.IsNullOrWhiteSpace(request.ReviewNotes) ? null : request.ReviewNotes.Trim(),
                By                         = currentUser.UserId,
                At                         = now
            });
        }

        // ── The quorum engine ─────────────────────────────────────────────────────
        //
        // A confirmer APPROVING a pre-approved analysis no longer settles it on their signature
        // alone: they cast a VOTE, and the analysis is committed only once the analyst's team has
        // collected the number of signatures it asked for. Everything else on this endpoint is
        // untouched — a rejection is still an immediate veto, a return is still a return, and an
        // analyst's own approval or pre-approval never reaches this branch at all.
        //
        // When the analyst's team has no eligible manager the effective quorum is 1 and the first
        // confirmer confirms, which is byte-for-byte the behaviour this endpoint had before.
        var isQuorumVote = wasPreApproved && isConfirmer && isApproved;

        // The tally that settled the analysis, when a quorum was in force. Null on every other path,
        // which is what keeps the tally OFF the response of a decision no quorum governed.
        ApprovalTally? confirmedTally = null;

        if (isQuorumVote)
        {
            var (outcome, tally) = await _approvalQuorumService
                .VoteAndTryConfirmAsync(
                    tenantId:                tenantId,
                    analysis:                analysis,
                    voter:                   currentUser,
                    decisionAmount:          request.Amount,
                    decisionCurrency:        currency,
                    confirmedOperationsJson: operationsJson,
                    reviewNotes:             request.ReviewNotes,
                    nowUtc:                  now,
                    ct:                      cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not ApprovalVoteOutcome.Confirmed)
            {
                // Either the vote landed below quorum — the analysis row is deliberately NOT written,
                // so it stays 'PreApproved' with the votes on the side — or another manager crossed
                // the quorum first and the service refreshed the entity from their row. Either way
                // THIS request confirmed nothing, so it emits no 'Analysis.Confirmed' audit row and
                // no confirmation notification: both belong to whoever actually settled it.
                return await BuildApprovalVoteResultAsync(
                    tenantId, result, analysis, currentUser, tally, outcome, personChecksJson, reliabilityReview, cancellationToken)
                    .ConfigureAwait(false);
            }

            confirmedTally = tally;

            // The guarded UPDATE above settles the persona-agnostic final record; the confirmations of the
            // signature that crossed the quorum are written beside it, on persona analyses only.
            if (personChecksJson is not null)
            {
                await _personAnalysisRepository
                    .SetConfirmationChecksAsync(analysis.AnalysisId, tenantId, personChecksJson, CancellationToken.None)
                    .ConfigureAwait(false);
                analysis.ConfirmationChecksJson = personChecksJson;
            }

            // Crossed. The service has already written the final record with a guarded UPDATE and
            // mirrored it onto `analysis`, so the flow below continues exactly as a one-signature
            // confirmation always did — audit, notification and response all describe a committed
            // decision. It must NOT fall through to UpdateAsync, which would re-save the same row
            // unguarded and undo the concurrency guarantee the guarded write exists for.
        }
        else if (mustPreApprove)
        {
            // Step one. Only the pre-approval record is written; the final record stays untouched
            // so a re-decided analysis does not appear to carry a confirmation it never received.
            analysis.Status              = PreApprovedStatus;
            analysis.PreApprovedBy       = currentUser.UserId;
            analysis.PreApprovedAt       = now;
            analysis.PreApprovedAmount   = request.Amount;
            analysis.PreApprovedCurrency = currency;

            // What the analyst PROPOSED. ConfirmedOperationsJson is deliberately left alone, exactly
            // as the final record above it is: this step commits nothing.
            analysis.ProposedOperationsJson = operationsJson;

            // Persona humana (DDL 072): the confirmations this first signature carried.
            if (personChecksJson is not null)
                analysis.PreApprovalChecksJson = personChecksJson;
        }
        else
        {
            // The final record, whether this is a one-step approval, a confirmation, a rejection or
            // a return. The pre-approval record is deliberately NOT cleared or overwritten: on a
            // confirmation it is the first of the two signatures and the reason the second matters.
            analysis.Status     = decision!;
            analysis.ReviewedBy = currentUser.UserId;
            analysis.ReviewedAt = now;

            // Only an approval commits a figure. A rejection or a return commits nothing, and
            // writing the requested amount there would misread as money that was granted.
            analysis.DecisionAmount   = isApproved ? request.Amount : null;
            analysis.DecisionCurrency = isApproved ? currency       : null;

            // The operations actually settled on, on the same terms as the amount above: only an
            // approval commits any. ProposedOperationsJson is never touched here — on a confirmation
            // it is what the analyst asked for, and the pair is the whole point.
            analysis.ConfirmedOperationsJson = isApproved ? operationsJson : null;

            // Persona humana (DDL 072): the confirmations of the signature that committed it.
            if (isApproved && personChecksJson is not null)
                analysis.ConfirmationChecksJson = personChecksJson;
        }

        if (!isQuorumVote)
        {
            if (request.ReviewNotes is not null)
                analysis.ReviewNotes = request.ReviewNotes;

            // Discarding the votes BEFORE the write, not after, and deliberately not fault-isolated.
            // A pre-approval that begins with somebody else's signatures already counted is the one
            // outcome worse than a failed request: if the wipe cannot be done, the transition must
            // not happen either.
            //
            // Returning for rework is the rule that names this, but it is not the only way back into
            // 'PreApproved' — a rejected or approved analysis can be re-decided — so the wipe is
            // keyed on ENTERING the state as well, which covers every route in.
            if (mustPreApprove || isReturned)
            {
                await _approvalQuorumService
                    .DiscardVotesAsync(tenantId, analysis.AnalysisId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _analysisRepository
                .UpdateAsync(analysis, CancellationToken.None)
                .ConfigureAwait(false);

            // A veto is immediate and answers to no quorum — the rejection above already stands. The
            // row is only the trail: it records WHO vetoed, beside the approvals that were standing
            // when they did. Those approvals are deliberately left in place; a later re-pre-approval
            // wipes them on the way in, so they can never pre-fill a fresh quorum.
            //
            // Fault-isolated, unlike the wipe above, and for the opposite reason: the decision is
            // already persisted and a missing trail row must never cost the manager their rejection.
            if (isRejected && wasPreApproved)
            {
                try
                {
                    await _approvalQuorumService
                        .RecordVetoAsync(tenantId, analysis, currentUser, now, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception vetoEx)
                {
                    _logger.LogWarning(
                        vetoEx,
                        "Failed to record the veto trail for analysis {AnalysisId} tenant {TenantId}. The rejection was recorded and the response is unaffected.",
                        analysis.AnalysisId, tenantId);
                }
            }
        }

        // Each act gets its own name so the trail shows BOTH steps of a two-step approval rather
        // than two indistinguishable "Analysis.Approved" rows.
        var auditAction = mustPreApprove              ? "Analysis.PreApproved"
                        : isReturned                  ? "Analysis.ReturnedToReview"
                        : isRejected                  ? "Analysis.Rejected"
                        : wasPreApproved              ? "Analysis.Confirmed"
                        :                               "Analysis.Approved";

        // On a confirmation the before/after amounts are the fact an auditor comes looking for: it
        // is where a RiskManager changing the analyst's number is visible. Null elsewhere, matching
        // how the other single-step audit calls in this controller behave.
        string? oldValueJson = null;
        string? newValueJson = null;
        if (wasPreApproved && isApproved)
        {
            oldValueJson = JsonSerializer.Serialize(new
            {
                Amount     = analysis.PreApprovedAmount,
                Currency   = analysis.PreApprovedCurrency,
                Operations = DeserializeOperationsOrNull(analysis.ProposedOperationsJson),
            });
            newValueJson = reliabilityReview is null
                ? JsonSerializer.Serialize(new
                {
                    Amount     = analysis.DecisionAmount,
                    Currency   = analysis.DecisionCurrency,
                    Operations = DeserializeOperationsOrNull(analysis.ConfirmedOperationsJson),
                    Checks     = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(personChecksJson),
                })
                : JsonSerializer.Serialize(new
                {
                    Amount            = analysis.DecisionAmount,
                    Currency          = analysis.DecisionCurrency,
                    Operations        = DeserializeOperationsOrNull(analysis.ConfirmedOperationsJson),
                    Checks            = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(personChecksJson),
                    ReliabilityReview = reliabilityReview,
                });
        }
        else if (personChecksJson is not null)
        {
            // A persona pre-approval or one-step approval: the confirmations and the reason are the
            // record an auditor needs, so they are audited even though no amounts change hands here.
            newValueJson = JsonSerializer.Serialize(new
            {
                Checks = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(personChecksJson),
            });
        }
        else if (reliabilityReview is not null)
        {
            // A balance pre-approval, one-step approval or rejection (score credibility 1b-5): the reliability the
            // decider was shown, what they acknowledged and their figures-review note.
            newValueJson = JsonSerializer.Serialize(new { ReliabilityReview = reliabilityReview });
        }

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       auditAction,
            severity:     "Info",
            entityType:   "Analysis",
            entityId:     analysis.AnalysisId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: oldValueJson,
            newValueJson: newValueJson,
            ct:           CancellationToken.None);

        // The amount is deliberately absent from this line: it is a credit figure, and the audit row
        // above is where it belongs.
        _logger.LogInformation(
            "Analysis decision recorded: {AnalysisId} tenant {TenantId} status={Status} action={AuditAction} by {ReviewerId} figuresReviewNoteLength={FiguresReviewNoteLength}",
            analysis.AnalysisId, tenantId, analysis.Status, auditAction, currentUser.UserId, figuresReviewNote?.Length ?? 0);

        // Build the same response shape as GET /{id} so the frontend can refresh from it.
        var entity = await _entityRepository
            .GetByIdAsync(analysis.EntityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Tell whoever is on the other side of this decision. Placed after the entity lookup so the
        // copy can name the company, and after the persist and the audit above so a notification can
        // never be emitted for a decision that did not stick.
        //
        // AWAITED, not fire-and-forget like the audit call above. IAuditService manages its own
        // scope; the notifier writes through the request-scoped FinanciaDbContext, so discarding the
        // task would race that context's disposal. Fault isolation comes from the try/catch, which
        // is what makes this safe to await: the notifier already swallows its own failures, and this
        // outer guard covers the rest — a notification must never cost the analyst their decision.
        try
        {
            await _decisionNotifier.NotifyAsync(
                tenantId:       tenantId,
                analysis:       analysis,
                currentUser:    currentUser,
                auditAction:    auditAction,
                wasPreApproved: wasPreApproved,
                entityName:     entity?.BusinessName,
                amount:         request.Amount,
                currency:       currency,
                ct:             CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(
                notifyEx,
                "Failed to emit decision notifications for analysis {AnalysisId} tenant {TenantId}. The decision was recorded and the response is unaffected.",
                analysis.AnalysisId, tenantId);
        }

        // One batched lookup for all three actors. Note reviewedByName is NOT currentUser.DisplayName
        // any more: after a pre-approval the caller is the PRE-approver, and ReviewedBy still holds
        // whoever last settled the analysis (or nothing at all), so naming the caller there would
        // attribute a confirmation to someone who did not make one.
        var actorNames = await ResolveActorNamesAsync(
            [analysis.RequestedBy, analysis.ReviewedBy, analysis.PreApprovedBy],
            cancellationToken).ConfigureAwait(false);

        var decisionResponse = MapToResponse(
            result,
            analysis.DocumentId ?? Guid.Empty,
            entity?.BusinessName,
            entity?.CUIT,
            analysis.RequestedAmount,
            analysis.RequestedBy.ToString(),
            ActorName(analysis.RequestedBy, actorNames),
            analysis.ReviewedBy?.ToString(),
            ActorName(analysis.ReviewedBy, actorNames),
            fiscalYear: analysis.FiscalYear,
            status: analysis.Status,
            reviewNotes: analysis.ReviewNotes,
            finalRiskScore: analysis.FinalRiskScore,
            finalRiskLevel: analysis.FinalRiskLevel,
            entityId: analysis.EntityId,
            preApprovedBy: analysis.PreApprovedBy?.ToString(),
            preApprovedByName: ActorName(analysis.PreApprovedBy, actorNames),
            preApprovedAt: analysis.PreApprovedAt,
            preApprovedAmount: analysis.PreApprovedAmount,
            preApprovedCurrency: analysis.PreApprovedCurrency,
            decisionAmount: analysis.DecisionAmount,
            decisionCurrency: analysis.DecisionCurrency,
            proposedOperationsJson: analysis.ProposedOperationsJson,
            confirmedOperationsJson: analysis.ConfirmedOperationsJson);
        ApplyDetailChannels(decisionResponse, result.CatalogueAlerts, result.ScoreBreakdownJson, analysis.AnalysisKind, result.RescoredAt);
        await ApplyPersonFieldsAsync(decisionResponse, analysis, tenantId, cancellationToken).ConfigureAwait(false);
        // On a confirmation that crossed a real quorum this is the provenance of the decision: how
        // many signatures it took and whose. Null whenever no quorum was in force, which is every
        // decision on a team without managers.
        ApplyApprovalTally(decisionResponse, confirmedTally);
        return Ok(decisionResponse);
    }

    // ── DELETE /api/analyses/{id}/decision/vote ───────────────────────────────────

    /// <summary>
    /// Retracts the caller's own approval from a pre-approved analysis, dropping the tally by one.
    /// </summary>
    /// <remarks>
    /// A dedicated endpoint rather than a fourth <c>Decision</c> value on PATCH /{id}/decision, for
    /// three reasons. That field is documented — on the model and in the action — as case-sensitive
    /// to match CK_Analyses_Status, and every value it admits IS a status the action writes;
    /// "WithdrawApproval" is not one, and adding it would break that correspondence in the one field
    /// where it is load-bearing. A withdrawal also has no payload at all: the route and the
    /// authenticated identity determine it completely. And routing it through PATCH would thread it
    /// past the amount and currency validation, the operations builder, the cupo guard and the
    /// approval-limit lookup — five constructs it has nothing to do with.
    ///
    /// <para>
    /// ONLY THE CALLER'S OWN VOTE, and only while the analysis is still pre-approved. Ownership is
    /// not checked so much as made inexpressible: the repository scopes the delete by the caller's
    /// user id, so there is no argument through which somebody else's signature could be named.
    /// </para>
    ///
    /// <para>
    /// Idempotent: withdrawing when nothing is on record is a 200 with the unchanged tally, not a
    /// 404. The caller asked for their approval not to be standing, and it is not.
    /// </para>
    /// </remarks>
    /// <param name="id">The analysis result identifier (ResultId), as for the decision endpoint.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the updated <see cref="AnalysisResultResponse"/>, or 403/404/409.</returns>
    [HttpDelete("{id:guid}/decision/vote")]
    [Authorize(Roles = Roles.Deciders)]
    [ProducesResponseType(typeof(AnalysisResultResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> WithdrawApprovalVoteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var result = await _analysisResultRepository
            .GetByIdAsync(id, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el resultado del análisis.");

        var analysis = await _analysisRepository
            .GetByIdAsync(result.AnalysisId, cancellationToken)
            .ConfigureAwait(false);
        if (analysis is null || !string.Equals(analysis.TenantId, tenantId, StringComparison.Ordinal))
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró el análisis.");

        // The role comes off the DATABASE row, exactly as DecideAsync takes it, so a role changed
        // through the settings screen applies before the caller's token has caught up.
        if (!Roles.IsConfirmer(currentUser.Role))
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "Sólo un RiskManager o un Admin puede retirar una aprobación.");

        // Votes are reversible only while they still decide something. Once the analysis is settled
        // the signatures are the record of how it was settled, and withdrawing one would rewrite the
        // justification of a credit decision that has already been committed.
        if (!string.Equals(analysis.Status, PreApprovedStatus, StringComparison.Ordinal))
            return Problem(statusCode: 409, title: "Conflict",
                detail: "Sólo se puede retirar una aprobación mientras el análisis está pre-aprobado.");

        var (withdrawn, tally) = await _approvalQuorumService
            .WithdrawAsync(tenantId, analysis, currentUser, cancellationToken)
            .ConfigureAwait(false);

        // No audit row when nothing moved: an idempotent retry must not fill the trail with acts
        // that did not happen.
        if (withdrawn)
        {
            _ = _auditService.LogAsync(
                tenantId:     tenantId,
                userId:       currentUser.UserId,
                action:       ApprovalWithdrawnAction,
                severity:     "Info",
                entityType:   "Analysis",
                entityId:     analysis.AnalysisId.ToString(),
                ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
                requestId:    HttpContext.TraceIdentifier,
                newValueJson: JsonSerializer.Serialize(new { tally.Approved, Required = tally.Required }),
                ct:           CancellationToken.None);

            _logger.LogInformation(
                "Approval withdrawn: {AnalysisId} tenant {TenantId} by {ReviewerId} tally={Approved}/{Required}",
                analysis.AnalysisId, tenantId, currentUser.UserId, tally.Approved, tally.Required);
        }

        var response = await BuildAnalysisDecisionResponseAsync(
            tenantId, result, analysis, tally, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>
    /// Attaches the approval tally to a response, or leaves it absent when no quorum is in force.
    /// </summary>
    /// <remarks>
    /// Absent — not zeroed — is the meaningful default: a tenant whose teams name no managers must
    /// see no tally at all rather than a misleading "0 de 1". <see cref="ApprovalTally.InForce"/> is
    /// what tells the two apart, since a degenerate quorum of 1 is numerically indistinguishable
    /// from a real one-manager team.
    /// </remarks>
    private static void ApplyApprovalTally(AnalysisResultResponse response, ApprovalTally? tally)
    {
        if (tally is null || !tally.InForce) return;

        response.ApprovalQuorum    = tally.Required;
        response.ApprovalCount     = tally.Approved;
        response.ApprovedByUserIds = tally.ApproverUserIds
            .Select(userId => userId.ToString())
            .ToArray();
    }

    /// <summary>
    /// Builds the 200 for a vote that did NOT confirm the analysis — either because it landed below
    /// the quorum or because another manager crossed it first.
    /// </summary>
    /// <remarks>
    /// Deliberately a 200 and not a 409 in both cases. The caller asked to approve and their
    /// approval is on record; below quorum the analysis is simply not finished, and when someone
    /// else finished it first the outcome is the one they asked for. Neither is an error, and the
    /// payload carries the tally so the Portal can say which of the two happened.
    ///
    /// <para>
    /// It emits its own audit row and NO notification: 'Analysis.Confirmed' and the inbox copy that
    /// rides with it belong to the request that actually settled the analysis. Emitting them here
    /// would tell the analyst their pre-approval was confirmed while it is still waiting.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> BuildApprovalVoteResultAsync(
        string              tenantId,
        AnalysisResult      result,
        Analysis            analysis,
        User                voter,
        ApprovalTally       tally,
        ApprovalVoteOutcome outcome,
        string?             personChecksJson,
        object?             reliabilityReview,
        CancellationToken   cancellationToken)
    {
        // The tally is the fact an auditor reconstructing a multi-signature approval needs, and it
        // is the only place it is recorded — the analysis row itself is untouched by a partial vote.
        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       voter.UserId,
            action:       ApprovalVotedAction,
            severity:     "Info",
            entityType:   "Analysis",
            entityId:     analysis.AnalysisId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: personChecksJson is not null
                ? JsonSerializer.Serialize(new
                {
                    tally.Approved,
                    Required = tally.Required,
                    Checks   = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(personChecksJson)
                })
                : reliabilityReview is not null
                    // Score credibility 1b-5: the vote is a decision too, so it records the review it passed.
                    ? JsonSerializer.Serialize(new { tally.Approved, Required = tally.Required, ReliabilityReview = reliabilityReview })
                    : JsonSerializer.Serialize(new { tally.Approved, Required = tally.Required }),
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Approval vote recorded: {AnalysisId} tenant {TenantId} outcome={Outcome} tally={Approved}/{Required}",
            analysis.AnalysisId, tenantId, outcome, tally.Approved, tally.Required);

        var response = await BuildAnalysisDecisionResponseAsync(
            tenantId, result, analysis, tally, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    /// <summary>
    /// Builds the standard decision payload — the same shape GET /{id} returns, so the Portal can
    /// refresh straight from it — with the approval tally attached.
    /// </summary>
    private async Task<AnalysisResultResponse> BuildAnalysisDecisionResponseAsync(
        string            tenantId,
        AnalysisResult    result,
        Analysis          analysis,
        ApprovalTally?    tally,
        CancellationToken cancellationToken)
    {
        var entity = await _entityRepository
            .GetByIdAsync(analysis.EntityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var actorNames = await ResolveActorNamesAsync(
            [analysis.RequestedBy, analysis.ReviewedBy, analysis.PreApprovedBy],
            cancellationToken).ConfigureAwait(false);

        var response = MapToResponse(
            result,
            analysis.DocumentId ?? Guid.Empty,
            entity?.BusinessName,
            entity?.CUIT,
            analysis.RequestedAmount,
            analysis.RequestedBy.ToString(),
            ActorName(analysis.RequestedBy, actorNames),
            analysis.ReviewedBy?.ToString(),
            ActorName(analysis.ReviewedBy, actorNames),
            fiscalYear: analysis.FiscalYear,
            status: analysis.Status,
            reviewNotes: analysis.ReviewNotes,
            finalRiskScore: analysis.FinalRiskScore,
            finalRiskLevel: analysis.FinalRiskLevel,
            entityId: analysis.EntityId,
            preApprovedBy: analysis.PreApprovedBy?.ToString(),
            preApprovedByName: ActorName(analysis.PreApprovedBy, actorNames),
            preApprovedAt: analysis.PreApprovedAt,
            preApprovedAmount: analysis.PreApprovedAmount,
            preApprovedCurrency: analysis.PreApprovedCurrency,
            decisionAmount: analysis.DecisionAmount,
            decisionCurrency: analysis.DecisionCurrency,
            proposedOperationsJson: analysis.ProposedOperationsJson,
            confirmedOperationsJson: analysis.ConfirmedOperationsJson);

        ApplyDetailChannels(response, result.CatalogueAlerts, result.ScoreBreakdownJson, analysis.AnalysisKind, result.RescoredAt);
        ApplyApprovalTally(response, tally);
        await ApplyPersonFieldsAsync(response, analysis, tenantId, cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// Resolves display names for the given actor ids in ONE batched query, keyed by UserId.
    /// Nulls and duplicates are dropped; ids that no longer resolve are simply absent, and
    /// <see cref="ActorName"/> substitutes "Usuario" for those.
    /// Mirrors <c>CreditLinesController.ResolveCreatorNamesAsync</c> and the chat conversation list.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ResolveActorNamesAsync(
        IEnumerable<Guid?> actorIds, CancellationToken ct)
    {
        var ids = actorIds
            .Where(actorId => actorId.HasValue)
            .Select(actorId => actorId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        var users = await _userRepository
            .GetByIdsAsync(ids, ct)
            .ConfigureAwait(false);

        return users.ToDictionary(u => u.UserId, u => u.DisplayName);
    }

    /// <summary>
    /// Display name for an actor id, falling back to "Usuario" when the id is unknown or the stored
    /// name is blank — the same fallback the chat conversation list and the credit-line payloads
    /// use. A null id returns null: nobody performed that act, which is not the same as an
    /// unresolvable someone.
    /// </summary>
    private static string? ActorName(Guid? actorId, IReadOnlyDictionary<Guid, string> actorNames)
    {
        if (actorId is null)
            return null;

        return actorNames.TryGetValue(actorId.Value, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "Usuario";
    }

    // ── GET /api/analyses ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a paginated list of analysis results for the current tenant.
    /// Optionally filtered by <paramref name="entityId"/>.
    /// <para>
    /// Rows are the narrow <see cref="AnalysisListItemResponse"/>, NOT the full
    /// <see cref="AnalysisResultResponse"/>: the detail blobs the table never renders are not
    /// shipped here. Fetch GET /api/analyses/{resultId} for those.
    /// </para>
    /// </summary>
    /// <param name="page">1-based page number. Default: 1.</param>
    /// <param name="pageSize">Items per page. Default: 20.</param>
    /// <param name="entityId">Optional entity filter.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="AnalysisListResponse"/>.</returns>
    [HttpGet]
    [Authorize(Roles = Roles.All)]
    [ProducesResponseType(typeof(AnalysisListResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] int   page              = 1,
        [FromQuery] int   pageSize          = 20,
        [FromQuery] Guid? entityId          = null,
        CancellationToken cancellationToken  = default)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        // Ordering and paging happen in SQL. The repository projects to AnalysisListProjection, so
        // the 14 heavy nvarchar(max) columns are never read off disk — only ScoreBreakdownJson is,
        // and only for the ≤pageSize rows of this page. Total is a COUNT(*), not a List.Count.
        var skip = (page - 1) * pageSize;
        var (page_items, total) = entityId.HasValue
            ? await _analysisResultRepository
                .GetListPageByEntityAsync(entityId.Value, tenantId, skip, pageSize, cancellationToken)
                .ConfigureAwait(false)
            : await _analysisResultRepository
                .GetListPageByTenantAsync(tenantId, skip, pageSize, cancellationToken)
                .ConfigureAwait(false);

        // Look up EntityIds for this page only (not the entire result set). DocumentId used to be
        // looked up here too; the list row no longer carries it, and it lives on the detail DTO.
        var analysisIds = page_items.Select(r => r.AnalysisId).ToList();
        var analyses    = await _analysisRepository
            .GetByIdsAsync(analysisIds, cancellationToken)
            .ConfigureAwait(false);
        var entityIdLookup = analyses.ToDictionary(a => a.AnalysisId, a => a.EntityId);

        // By id, not by tenant. This used to call GetAllByTenantAsync and filter in memory, which
        // read every entity of the tenant with all five of its source snapshots — a second
        // full-table blob scan, inside the endpoint whose whole point is not to do those.
        var neededEntityIds = entityIdLookup.Values.Distinct().ToList();
        var entityLookup    = await _entityRepository
            .GetNameLookupByIdsAsync(neededEntityIds, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Build user lookups for RequestedBy / ReviewedBy
        var neededUserIds = analyses
            .SelectMany(a => new[] { (Guid?)a.RequestedBy, a.ReviewedBy })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var users = await _userRepository
            .GetByIdsAsync(neededUserIds, cancellationToken)
            .ConfigureAwait(false);
        var userNameLookup = users.ToDictionary(u => u.UserId, u => u.DisplayName);

        var analysisLookup = analyses.ToDictionary(a => a.AnalysisId);
        var unreadableResultIds = new List<Guid>();

        var items = page_items
            .Select(r =>
            {
                var entityId = entityIdLookup.GetValueOrDefault(r.AnalysisId);
                entityLookup.TryGetValue(entityId, out var entity);
                analysisLookup.TryGetValue(r.AnalysisId, out var analysis);

                string? requestedByName = null;
                string? reviewedByName = null;
                if (analysis is not null)
                {
                    userNameLookup.TryGetValue(analysis.RequestedBy, out requestedByName);
                    if (analysis.ReviewedBy.HasValue)
                        userNameLookup.TryGetValue(analysis.ReviewedBy.Value, out reviewedByName);
                }

                var listItem = MapToListItem(
                    r,
                    out var breakdownUnreadable,
                    entity?.BusinessName,
                    entity?.CUIT,
                    analysis?.RequestedAmount,
                    analysis?.RequestedBy.ToString(),
                    requestedByName,
                    analysis?.ReviewedBy?.ToString(),
                    reviewedByName,
                    fiscalYear: analysis?.FiscalYear,
                    status: analysis?.Status,
                    finalRiskScore: analysis?.FinalRiskScore,
                    finalRiskLevel: analysis?.FinalRiskLevel,
                    entityId: entityId,
                    decisionAmount: analysis?.DecisionAmount,
                    decisionCurrency: analysis?.DecisionCurrency,
                    analysisKind: analysis?.AnalysisKind ?? AnalysisKinds.Balance);

                // Set here, not through MapToListItem's parameter list: a persona humana row has no
                // score, and its kind is what tells the Portal that the null is by design.
                listItem.AnalysisKind = analysis?.AnalysisKind ?? AnalysisKinds.Balance;

                if (breakdownUnreadable)
                    unreadableResultIds.Add(r.ResultId);

                return listItem;
            })
            .ToList();

        // Never per row: one line per page, and only when a breakdown on it could not be read.
        if (unreadableResultIds.Count > 0)
        {
            _logger.LogWarning(
                "{Count} score breakdown(s) on this analyses page could not be read for tenant {TenantId}; their score reliability is reported as facts_unavailable. ResultIds: {ResultIds}",
                unreadableResultIds.Count, tenantId, string.Join(", ", unreadableResultIds.Take(10)));
        }

        _logger.LogInformation(
            "Listed {Count}/{Total} analyses for tenant {TenantId} (page {Page}/{PageSize}, entityId={EntityId})",
            items.Count, total, tenantId, page, pageSize,
            entityId?.ToString() ?? "all");

        return Ok(new AnalysisListResponse
        {
            Items    = items,
            Page     = page,
            PageSize = pageSize,
            Total    = total
        });
    }

    // ── GET /api/analyses/{resultId}/chat ───────────────────────────────────────────

    [HttpGet("{resultId:guid}/chat")]
    [ProducesResponseType(typeof(ChatConversationListResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<IActionResult> GetChatHistoryAsync(
        Guid resultId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");

        // Conversations are shared across all analysts of the tenant for this result (read-only).
        var conversations = await _chatRepository
            .GetConversationsByResultForTenantAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Resolve author display names for the distinct creators
        var authorIds = conversations
            .Select(c => c.CreatedByUserId)
            .Distinct()
            .ToList();
        var authors = await _userRepository
            .GetByIdsAsync(authorIds, cancellationToken)
            .ConfigureAwait(false);
        var authorNameLookup = authors.ToDictionary(u => u.UserId, u => u.DisplayName);

        var response = new ChatConversationListResponse
        {
            Conversations = conversations.Select(c => new ChatConversationSummary
            {
                ConversationId  = c.ConversationId,
                Title           = c.Title,
                MessageCount    = c.MessageCount,
                LastMessageAt   = c.LastMessageAt,
                CreatedAt       = c.CreatedAt,
                CreatedByUserId = c.CreatedByUserId,
                CreatedByName   = authorNameLookup.TryGetValue(c.CreatedByUserId, out var name)
                                  && !string.IsNullOrWhiteSpace(name)
                                      ? name
                                      : "Usuario"
            }).ToList()
        };

        _logger.LogInformation("Returned {Count} conversations for result {ResultId} in tenant {TenantId}",
            conversations.Count, resultId, tenantId);

        return Ok(response);
    }

    // ── GET /api/analyses/{resultId}/chat/{conversationId} ────────────────────────

    [HttpGet("{resultId:guid}/chat/{conversationId:guid}")]
    [ProducesResponseType(typeof(ChatHistoryResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<IActionResult> GetConversationMessagesAsync(
        Guid resultId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");

        // Any analyst of the tenant may read the conversation, regardless of author.
        var conversation = await _chatRepository
            .GetConversationByIdForTenantAsync(conversationId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null || conversation.ResultId != resultId)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró la conversación o no pertenece a este análisis.");

        var messages = await _chatRepository
            .GetMessagesAsync(conversationId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        var response = new ChatHistoryResponse
        {
            ConversationId = conversation.ConversationId,
            Messages = messages.Select(m => new ChatHistoryMessage
            {
                // The handle for the attachment endpoint below (script 056). A BIGINT identity, not
                // a GUID. Safe to expose: that endpoint re-verifies tenant → result → conversation →
                // message on every call, so an id guessed from another tenant resolves to nothing.
                MessageId    = m.ChatMessageId,
                Role         = m.Role,
                Content      = m.Content,
                CreatedAt    = m.CreatedAt,
                InputTokens  = m.InputTokens,
                OutputTokens = m.OutputTokens,
                // The marker, never the Markdown (script 055). Free — GetMessagesAsync already
                // materialises the full entity — and it keeps this payload bounded: one stored
                // attachment can be 200.000 characters and this endpoint returns every turn.
                HasAttachment = m.AttachmentMarkdown is not null,
                // The NAME rides this payload (script 056) where the Markdown deliberately does not:
                // it is tens of bytes, and without it the marker cannot tell one version of a cash
                // flow from the next. Null on turns taken between the 055 and 056 deploys.
                AttachmentFileName = m.AttachmentFileName
            }).ToList()
        };

        _logger.LogInformation("Returned {Count} messages for conversation {ConversationId} in tenant {TenantId}",
            messages.Count, conversationId, tenantId);

        return Ok(response);
    }

    // ── GET /api/analyses/{resultId}/chat/{conversationId}/messages/{messageId}/attachment ──

    /// <summary>
    /// Returns the spreadsheet stored on one chat turn (scripts 055 and 056), so the Portal can show
    /// the analyst what they attached and offer to re-use it, without the history list carrying it.
    /// </summary>
    /// <remarks>
    /// A READ PATH, not a prompt path. Nothing here is sent to Claude, and re-using the spreadsheet
    /// means the caller posting this Markdown back as an ordinary <c>attachmentMarkdown</c> on a
    /// later turn — the same route any upload takes, so the model is never handed anything by a path
    /// the analyst did not drive.
    /// <para>
    /// <c>messageId</c> is a <see langword="long"/> because ChatMessages.ChatMessageId is a BIGINT
    /// identity. The ownership chain is the history endpoint's, verbatim and in the same order, with
    /// the message itself read through a predicate scoped by conversation AND tenant — so a
    /// sequential id guessed from another tenant resolves to nothing rather than to a 403 that would
    /// confirm it exists.
    /// </para>
    /// </remarks>
    [HttpGet("{resultId:guid}/chat/{conversationId:guid}/messages/{messageId:long}/attachment")]
    [ProducesResponseType(typeof(ChatAttachmentContentResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> GetMessageAttachmentAsync(
        Guid resultId,
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");

        // Any analyst of the tenant may read the conversation, regardless of author — the same rule
        // the history endpoint applies, and the attachment must not be more restricted than the turn
        // it hangs off.
        var conversation = await _chatRepository
            .GetConversationByIdForTenantAsync(conversationId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null || conversation.ResultId != resultId)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró la conversación o no pertenece a este análisis.");

        var message = await _chatRepository
            .GetMessageByIdAsync(messageId, conversationId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // ONE shape for "no such message" and "that message carries nothing", deliberately. Splitting
        // them would turn this route into a probe for which ids exist in a conversation the caller
        // can already read — no real leak, but no reason to answer the question either.
        if (message is null || message.AttachmentMarkdown is null)
        {
            _logger.LogInformation(
                "No stored chat attachment for message {ChatMessageId} in conversation {ConversationId} tenant {TenantId}",
                messageId, conversationId, tenantId);

            return Problem(statusCode: 404, title: "Not Found",
                detail: "No se encontró el adjunto de este mensaje.");
        }

        // Length only, never a character of the content — the same rule the /chat log line follows,
        // and the reason the PII carve-out for this attachment survives it being readable.
        _logger.LogInformation(
            "Returned stored chat attachment for message {ChatMessageId} in conversation {ConversationId} " +
            "tenant {TenantId}: chars={AttachmentChars}",
            messageId, conversationId, tenantId, message.AttachmentMarkdown.Length);

        return Ok(new ChatAttachmentContentResponse
        {
            MessageId = message.ChatMessageId,
            // Null for a turn taken between the 055 and 056 deploys: the Markdown was stored before
            // anything recorded its name. The viewer still works, it just has no label.
            FileName  = message.AttachmentFileName,
            Markdown  = message.AttachmentMarkdown
        });
    }

    // ── PATCH /api/analyses/{resultId}/chat/{conversationId} ──────────────────────

    [HttpPatch("{resultId:guid}/chat/{conversationId:guid}")]
    [Authorize(Roles = Roles.Operators)]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<IActionResult> RenameConversationAsync(
        Guid resultId,
        Guid conversationId,
        [FromBody] RenameConversationRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");

        var renamed = await _chatRepository
            .RenameConversationAsync(conversationId, tenantId, currentUser.UserId, request.Title, cancellationToken)
            .ConfigureAwait(false);

        if (!renamed)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró la conversación o no pertenece a este usuario.");

        return NoContent();
    }

    // ── POST /api/analyses/{resultId}/chat ────────────────────────────────────────

    /// <summary>
    /// Sends a conversational message to Claude grounded in the specified analysis result.
    /// The full analysis context (ratios, BCRA, scoring, executive summary) is included
    /// in the system prompt so Claude can answer questions without external lookups.
    /// </summary>
    /// <param name="resultId">The analysis result to use as context.</param>
    /// <param name="request">
    /// Request body with the analyst's message, the optional conversation to continue, the optional
    /// Claude model to answer with, and the optional Markdown of an .xlsx attached to this turn. The
    /// message and the capped attachment are persisted on the user turn; the model choice applies to
    /// this turn alone.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with the assistant's reply, or 400/403/404/502 on error.</returns>
    /// <remarks>
    /// An attachment is a scoped, deliberate exception to the project rule against putting PII in a
    /// prompt: a cash-flow sheet often carries the company name or CUIT in a title row, and this is
    /// the analyst's own file for their own analysis. It reaches the LIVE prompt for this turn only
    /// — never replayed as history, never re-injected into a later prompt, never logged as content.
    /// The capped Markdown IS stored on the user turn (ChatMessages.AttachmentMarkdown, script 055)
    /// so the analyst can read back what they attached; nothing reads that column into a prompt. See
    /// the carve-out in CLAUDE.md.
    ///
    /// <para>
    /// The model is likewise per-turn. <c>request.Model</c> is honoured when the tenant's
    /// <c>ChatEnabledModels</c> allow-list contains it, and otherwise replaced by the first model on
    /// that list — never rejected. Which model answered is recorded on the assistant's
    /// <c>ChatMessages</c> row and on the ApiCallLogs entry, so the cost figure and the transcript
    /// agree about what was actually called.
    /// </para>
    /// </remarks>
    [HttpPost("{resultId:guid}/chat")]
    [Authorize(Roles = Roles.Operators)]
    [BillingQuota(BillingQuotaKind.ChatTokens)]
    [ProducesResponseType(typeof(ChatResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    [ProducesResponseType(typeof(ProblemDetails), 502)]
    [RequestSizeLimit(ChatRequestMaxBytes)]
    public async Task<IActionResult> ChatAsync(
        Guid resultId,
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        // The monthly chat-token quota was already checked by [BillingQuota] above. It only
        // blocks when Billing:EnforceChatTokens is on; otherwise it measures and logs. Either
        // way the check MUST stay ahead of the user-turn persist below — a rejected turn that
        // has already been written leaves a question in the transcript that never got an answer
        // — and ahead of the Claude call, which is what costs the tokens being metered. A reply
        // already in flight is never interrupted: the gate runs before any of this.
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(request.Message))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "El mensaje no puede estar vacío.");

        // ── Resolve requesting user ───────────────────────────────────────────────

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        // ── Load AnalysisResult — 403 if not found to avoid info leakage ─────────

        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");

        // A persona humana analysis (DDL 072) has no chat in M10 Phase 4a: nothing of a natural person's
        // evaluation goes to the model. Refused before any history, persistence or Claude call. The same
        // query hands back the analysis's fiscal year, which the reliability verdict below needs.
        var (personRefusal, analysisKindAndYear) = await RefuseChatOnPersonAnalysisAsync(
                result.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (personRefusal is not null) return personRefusal;

        // ── The company's identifiers (K5) ────────────────────────────────────────
        //
        // Nothing that identifies the company reaches Claude: not the razón social the extraction wrote
        // into the executive summary, not an earlier answer that repeated it, not the analyst's own
        // question. The identifiers are loaded and the patterns compiled HERE, before the conversation is
        // loaded or created and before anything is persisted, because this is where the realistic
        // failures happen (a missing row, a failed read) — and a turn that cannot be redacted is NOT sent:
        // it answers the same 502 an unavailable model does, and leaves no trace.
        var redactor = await BuildChatRedactorOrNullAsync(
                result, analysisKindAndYear, resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (redactor is null) return ChatServiceUnavailable();

        // ── Load or create conversation ───────────────────────────────────────────

        ChatConversation conversation;
        if (request.ConversationId.HasValue)
        {
            var existing = await _chatRepository
                .GetConversationByIdAsync(request.ConversationId.Value, tenantId, currentUser.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null || existing.ResultId != resultId)
                return Problem(statusCode: 403, title: "Forbidden",
                    detail: "No se encontró la conversación o no pertenece a este usuario.");

            conversation = existing;
        }
        else
        {
            conversation = await _chatRepository.CreateConversationAsync(new ChatConversation
            {
                ResultId        = resultId,
                AnalysisId      = result.AnalysisId,
                TenantId        = tenantId,
                Title           = Truncate(request.Message, 60),
                CreatedByUserId = currentUser.UserId
            }, cancellationToken).ConfigureAwait(false);
        }

        // ── Load history from DB ──────────────────────────────────────────────────

        var dbMessages = await _chatRepository
            .GetMessagesAsync(conversation.ConversationId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        // no longer used server-side; history is rebuilt from ChatMessages
        var history = dbMessages
            .Select(m => new ConversationMessage { Role = m.Role, Content = m.Content })
            .ToList();

        // ── Per-conversation context budget ────────────────────────────────────────
        //
        // A SECOND gate, and a different KIND from the [BillingQuota] filter at the top of this
        // method. That one is a monthly pool the whole tenant spends and it resets on the 1st; this
        // is a ceiling on how large ONE conversation's prompt may get, it never resets, and the only
        // thing that clears it is the analyst opening a new conversation. Both answer 429 and they
        // carry different errorCodes for exactly that reason: "esperá al mes que viene" and "abrí
        // una conversación nueva" are not the same instruction. Neither carries a Retry-After, and
        // this one especially must not — no amount of waiting shortens a transcript.
        //
        // WHY IT EXISTS: GetMessagesAsync above is uncapped and unpaginated, so every turn replays
        // the whole conversation. Left alone the prompt grows until the model window rejects it,
        // which surfaces as an opaque 502 at an unpredictable moment. This turns that into an
        // answerable message sent before the tokens are spent.
        //
        // THE MEASUREMENT IS REACTIVE, and free: the input_tokens Anthropic already billed on the
        // last assistant turn of THIS conversation, persisted in ChatMessages. No tokenizer, no
        // estimate, no extra Claude call — the gate cannot be wrong about a number it did not
        // compute. What that costs is one turn of lag: the turn that crosses the budget is answered
        // and the NEXT one is refused. That is the trade, not an oversight — refusing the crossing
        // turn would mean pricing the prompt before sending it, the estimate this avoids.
        //
        // WHICH TURNS COUNT: the clean ones. A turn that carried an ephemeral attachment was billed
        // for a prompt including ~67k tokens of spreadsheet that never entered ChatMessages and can
        // never be replayed, so its figure describes a request no later turn rebuilds.
        // LastAssistantInputTokens skips those rows (ChatMessage.HadEphemeralAttachment, script 053)
        // and measures by the most recent clean turn instead. Still reactive, still free, still no
        // estimate — one bit recorded at write time, read here. It under-reports when the last clean
        // turn is old, which is the direction this gate is already built to be wrong in.
        //
        // WHY IT SITS HERE, after the history read and before the user turn is written: a refused
        // turn must leave NO trace. Persisting the question first would strand a message in the
        // transcript that never got an answer — the same reason the [BillingQuota] filter runs ahead
        // of that write — and the Claude call below is what actually costs money.
        //
        // WHY A BRAND-NEW CONVERSATION IS NEVER REFUSED, and why that is structural rather than a
        // courtesy: when request.ConversationId was null a ChatConversation row was already INSERTED
        // above. This gate reads only persisted assistant turns, and a conversation created four
        // statements ago has none — LastAssistantInputTokens returns null and the helper returns on
        // the spot. That row is therefore ungateable, and the create-then-refuse orphan (an empty
        // conversation stranded in the analyst's list, holding a question it never accepted) cannot
        // happen. TRIPWIRE: this holds only while the measurement is reactive. If it ever becomes
        // predictive — estimating THIS turn rather than reading the last one's — then
        // CreateConversationAsync must move below this gate before it does.
        //
        // ONE READ, TWO CONSUMERS. The plan is resolved here rather than inside the gate because this
        // turn needs it twice over: for the context budget below, and for the tenant's chat-model
        // allow-list further down. The gate used to read it lazily, on the argument that a
        // conversation with no billed turn could not be gated and the read would be asking a question
        // whose answer was already irrelevant. That stopped being true when the model became
        // per-tenant: the read now happens on every turn regardless, so a lazy second one would only
        // add a call that returns the same memoised answer.
        //
        // Null means the read FAILED — both consumers fail open on it, each in its own way: the gate
        // lets the turn through unchecked, and the model falls back to the catalogue default.
        var billingPlan = await ResolveChatBillingPlanOrNullAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        var contextGate = BuildChatContextGateOrNull(
            tenantId, conversation.ConversationId, dbMessages, billingPlan);

        if (contextGate is not null) return contextGate;

        // ── Attachment ────────────────────────────────────────────────────────────
        // Capped HERE and not inside ClaudeService: this is the trust boundary, and the only place
        // holding the tenant/result context the truncation has to be logged against.
        //
        // It sits ahead of the user-turn write because that write now STORES the capped value
        // (script 055) — the analyst has to be able to reopen the conversation and see what they
        // attached. It stays behind the context gate above, so a refused turn still leaves no trace.
        //
        // Ephemeral in the sense that matters and always did: the Markdown is not `Content`, so it
        // never enters `history` and no later turn replays it. `history` was read from ChatMessages
        // before this turn was even written, and nothing below re-injects the stored column into a
        // prompt. What rides the Claude call is this local, once.
        var (attachmentMarkdown, attachmentTruncated) = CapAttachmentMarkdown(request.AttachmentMarkdown);

        // Script 056. Derived from the CAPPED Markdown above, not from request.AttachmentMarkdown and
        // not independently: name and content are stored together or not at all. A request carrying a
        // file name and a blank attachment stores NULL for both, so the analyst can never be shown a
        // label for a spreadsheet that was never kept. The DDL's verification block checks exactly
        // this invariant in production. The name is a LABEL: it goes nowhere near the prompt.
        var attachmentFileName = attachmentMarkdown is null
            ? null
            : NormalizeAttachmentFileName(request.AttachmentFileName);

        // ── Build system prompt ───────────────────────────────────────────────────

        // The deterministic cash-flow reading is computed HERE rather than inside the builder so the
        // builder stays static and dependency-free, and therefore unit-testable with no DI at all.
        // BuildCashFlowNarrativeOrEmpty never throws: a reading failure costs the block, never the turn.
        // Built ahead of the user-turn write so the redaction below can refuse the turn before anything
        // is persisted.
        var cashFlowNarrative = BuildCashFlowNarrativeOrEmpty(result.BalanceJson, resultId, tenantId);

        // The SAME predicate ClaudeService.ChatAsync uses to decide whether it emits the attachment
        // block at all. Tying the two together is the point: the prompt must never claim a spreadsheet
        // arrived on a turn whose payload does not carry one, nor deny one that does.
        // Score credibility 1b-3: the SAME verdict the detail screen shows for this row — the one adapter, the
        // row's own scalars, the analysis's kind and fiscal year (read with the person-refusal query above).
        // Never throws; an unreadable breakdown is logged once, as the detail does.
        var fiscalYear  = analysisKindAndYear?.FiscalYear;
        var reliability = ScoreReliabilityAdapter.Evaluate(
            new ScoreReliabilityRow(
                AiSuggestedScore: result.AiSuggestedScore,
                EnrichmentStatus: result.EnrichmentStatus,
                AnalysisKind:     analysisKindAndYear?.Kind,
                FiscalYear:       fiscalYear,
                GeneratedAt:      result.GeneratedAt,
                RescoredAt:       result.RescoredAt),
            result.ScoreBreakdownJson);

        if (reliability.BreakdownUnreadable)
        {
            _logger.LogWarning(
                "Score breakdown of result {ResultId} tenant {TenantId} could not be read; the chat reports its score reliability as facts_unavailable",
                resultId, tenantId);
        }

        var systemPrompt = BuildChatSystemPrompt(
            result, cashFlowNarrative, hasAttachment: !string.IsNullOrWhiteSpace(attachmentMarkdown),
            reliability: reliability.Reliability, fiscalYear: fiscalYear);

        // ── Redact the request (K5) ───────────────────────────────────────────────
        //
        // At the boundary, on everything that rides the Claude call: the FINAL system prompt (the
        // executive summary inside it is model-written from the PDF and usually opens with the razón
        // social), every replayed turn of both roles, and the analyst's current message. Copies only —
        // ChatMessages, the summary and the screen keep the real name, which the analyst is allowed to
        // see. The attachment is NOT redacted here: it is the documented carve-out, and the Portal scrubs
        // it before sending. A failure refuses the turn with the chat's 502, before the user turn is
        // written.
        var redacted = RedactChatRequestOrNull(redactor, systemPrompt, history, request.Message, resultId, tenantId);
        if (redacted is null) return ChatServiceUnavailable();

        // ── Persist user turn BEFORE Claude call ──────────────────────────────────

        await _chatRepository.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.ConversationId,
            TenantId       = tenantId,
            Role           = "user",
            Content        = request.Message,
            SenderUserId   = currentUser.UserId,
            // Script 055. The CAPPED value, the same string the Claude call below is handed — storing
            // the raw request field instead would let a caller persist an attachment larger than the
            // one the model was ever shown. CapAttachmentMarkdown answers null for null, empty and
            // whitespace alike, so a Portal that always sends the field stores NULL when nothing was
            // uploaded. Only the USER turn carries it: this is the turn the upload arrived on.
            AttachmentMarkdown = attachmentMarkdown,
            // Script 056. The label for the column above, on the same turn and NULL under the same
            // condition. It is never added to Content, never sent to Claude and never reaches the
            // system prompt — the model is told a spreadsheet arrived, not what it was called.
            AttachmentFileName = attachmentFileName
        }, cancellationToken).ConfigureAwait(false);

        // ── Resolve the effective model ───────────────────────────────────────────
        //
        // The analyst's pick is a REQUEST, not an instruction: what they may actually reach is the
        // tenant's allow-list, which a SuperAdmin controls. This is the only place the two meet, and
        // it is deliberately in the controller rather than in ClaudeService — the tenant context and
        // the billing resolution live here, and a service that resolved entitlements itself would be
        // the hidden coupling this project's rules exist to prevent.
        //
        // A request naming a model outside the list is NOT rejected. It falls back, silently to the
        // caller and at Debug here, for two reasons: the Portal can hold a stale list after a
        // SuperAdmin narrows one, and refusing an otherwise valid question over a dropdown value is a
        // worse outcome than answering it with a model the tenant is entitled to. Same for a
        // misspelled id and for no id at all — one rule, three inputs.
        //
        // No second read: billingPlan was resolved once above and is shared with the context gate.
        //
        // A NULL PLAN MEANS THE READ FAILED, and the fallback is the catalogue default — what the
        // chat called when the model was hardcoded. The analyst's pick is deliberately NOT honoured
        // in that case: there is no entitlement left to check it against, and a failed read must not
        // become a way to reach a model the tenant was never sold.
        //
        // The Count check is belt-and-braces: SanitizeOrDefault guarantees a non-empty list, so
        // indexing [0] is safe by construction. It is written out anyway because the cost of being
        // wrong is an unhandled IndexOutOfRangeException on a turn whose question is already
        // persisted, and the cost of the check is one comparison.
        var requestedModel = request.Model?.Trim();
        var enabledModels  = billingPlan?.Plan.ChatEnabledModels;

        var model = !string.IsNullOrWhiteSpace(requestedModel)
                    && enabledModels is not null
                    && enabledModels.Contains(requestedModel, StringComparer.Ordinal)
            ? requestedModel
            : enabledModels is { Count: > 0 } ? enabledModels[0] : ChatModelCatalog.Default.Id;

        if (!string.IsNullOrWhiteSpace(requestedModel) && !string.Equals(model, requestedModel, StringComparison.Ordinal))
        {
            // Debug, not Warning: a Portal holding a list a SuperAdmin has since narrowed is an
            // expected race, not an anomaly, and it is answered rather than refused.
            _logger.LogDebug(
                "Chat model {RequestedModel} is not enabled for tenant {TenantId}; falling back to {Model}.",
                requestedModel, tenantId, model);
        }

        // ── Call Claude (throws ClaudeApiException on failure) ────────────────────

        var sw = Stopwatch.StartNew();

        ClaudeChatResult chatResult;
        try
        {
            chatResult = await _claudeService
                .ChatAsync(model, redacted.SystemPrompt, redacted.History, redacted.Message, cancellationToken,
                           attachmentMarkdown: attachmentMarkdown)
                .ConfigureAwait(false);
        }
        catch (ClaudeApiException ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Claude chat failed for result {ResultId} tenant {TenantId}",
                resultId, tenantId);

            _ = _apiCallLogService.LogAsync(
                tenantId:         tenantId,
                service:          "ClaudeChat",
                endpoint:         "/v1/messages",
                responseStatus:   ex.ApiStatusCode,
                inputTokens:      null,
                outputTokens:     null,
                estimatedCostUsd: null,
                isSuccess:        false,
                // The internal reason (for an HTTP error: status, error type, clamped message, request
                // id), never the client text — that one says nothing and is what the response carries.
                errorMessage:     ex.DiagnosticMessage,
                durationMs:       (int)sw.ElapsedMilliseconds,
                operation:        ApiCallLogOperations.Chat,
                model:            model,
                userId:           currentUser.UserId,
                analysisId:       result.AnalysisId,
                ct:               CancellationToken.None);

            return ChatServiceUnavailable();
        }

        sw.Stop();

        // ── Persist assistant turn AFTER Claude call ──────────────────────────────

        var assistantMsg = await _chatRepository.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.ConversationId,
            TenantId       = tenantId,
            Role           = "assistant",
            Content        = chatResult.Text,
            InputTokens    = chatResult.InputTokens,
            OutputTokens   = chatResult.OutputTokens,
            // The model that ACTUALLY answered, not the one the chat used to be hardcoded to. This
            // column is per-turn precisely because a conversation may mix models, and it is what the
            // history endpoint reports back.
            Model          = model,
            // Script 053. NOT the attachment, and not its size — one bit saying InputTokens above
            // counts something that will never be replayed, so the context gate must skip this row
            // rather than measure the conversation by it. `attachmentMarkdown` is the CAPPED value:
            // CapAttachmentMarkdown answers null for null, empty and whitespace alike, so this is
            // true exactly when a real block rode the request, matching the IsNullOrWhiteSpace test
            // ClaudeService applies before emitting it. Only the ASSISTANT turn carries the flag —
            // the user turn holds nothing about the attachment and is not what the gate reads.
            HadEphemeralAttachment = attachmentMarkdown is not null
        }, cancellationToken).ConfigureAwait(false);

        // ── Log success (fire-and-forget) ─────────────────────────────────────────

        // Rates come from the catalogue entry for the model that answered, not from a literal. They
        // used to be Sonnet 4.6's $3/$15 written inline, which was correct only while the chat could
        // call nothing else — it would over-report a Haiku turn threefold and over-report a Sonnet 5
        // turn by half. Find cannot return null here: ChatAsync would already have thrown for a model
        // outside the catalogue, and `model` came from an allow-list filtered against it.
        var pricing = ChatModelCatalog.Find(model) ?? ChatModelCatalog.Default;

        var estimatedCost = (chatResult.InputTokens  * pricing.InputCostPerMTokUsd  +
                             chatResult.OutputTokens * pricing.OutputCostPerMTokUsd) / 1_000_000m;

        _ = _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "ClaudeChat",
            endpoint:         "/v1/messages",
            responseStatus:   200,
            inputTokens:      chatResult.InputTokens,
            outputTokens:     chatResult.OutputTokens,
            estimatedCostUsd: estimatedCost,
            isSuccess:        true,
            errorMessage:     null,
            durationMs:       (int)sw.ElapsedMilliseconds,
            operation:        ApiCallLogOperations.Chat,
            model:            model,
            userId:           currentUser.UserId,
            analysisId:       result.AnalysisId,
            chatMessageId:    assistantMsg.ChatMessageId,
            ct:               CancellationToken.None);

        // Attachment SIZE and truncation, never a character of its content. A cash-flow sheet
        // usually carries the company name or CUIT in a title row; letting that reach the LIVE
        // prompt is a scoped decision, and it does not relax the rule against PII in a log line.
        _logger.LogInformation(
            "Chat completed for result {ResultId} tenant {TenantId}: model={Model} " +
            "input_tokens={InputTokens} output_tokens={OutputTokens} duration_ms={DurationMs} " +
            "attachment_chars={AttachmentChars} attachment_truncated={AttachmentTruncated}",
            resultId, tenantId, model,
            chatResult.InputTokens, chatResult.OutputTokens, sw.ElapsedMilliseconds,
            attachmentMarkdown?.Length ?? 0, attachmentTruncated);

        return Ok(new ChatResponse
        {
            ConversationId = conversation.ConversationId,
            Message        = chatResult.Text,
            InputTokens    = chatResult.InputTokens,
            OutputTokens   = chatResult.OutputTokens
        });
    }

    // ── POST /api/analyses/{resultId}/chat/attachment ─────────────────────────────

    /// <summary>
    /// Parses an .xlsx the analyst wants to ask the chat about and returns it as Markdown tables.
    /// The Anthropic API does not accept .xlsx in a document block, so a spreadsheet has to become
    /// text before a model can read it; this endpoint does that structuring and hands the text back
    /// to the caller, which includes it in an ordinary chat turn. It never calls the model itself.
    /// </summary>
    /// <remarks>
    /// The upload is ephemeral: the bytes live only for this request and are never written to
    /// storage or the database. The audit trail records what was attached — name, hash, size,
    /// counts — and never a single cell of what it said.
    /// <para>
    /// Note that a cash-flow workbook often carries the company name or CUIT in a title row, so the
    /// Markdown returned here can contain identifying data. Nothing is sent to Claude from this
    /// endpoint, so the project rule against putting PII in a prompt is untouched by it; the
    /// obligation lands on whoever forwards this text into a chat turn.
    /// </para>
    /// </remarks>
    /// <param name="resultId">The analysis result the chat is grounded in.</param>
    /// <param name="file">The .xlsx file (required, max 5 MB).</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="ChatAttachmentResponse"/>, or 400/401/403/500 on error.</returns>
    [HttpPost("{resultId:guid}/chat/attachment")]
    [Authorize(Roles = Roles.Operators)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ChatAttachmentResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    [RequestSizeLimit(ExcelCashFlowParser.MaxFileSizeBytes)]
    public async Task<IActionResult> ChatAttachmentAsync(
        Guid              resultId,
        IFormFile         file,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        // ── Validate file ────────────────────────────────────────────────────────

        if (file is null || file.Length == 0)
            return ValidationProblem("file", "Se requiere un archivo no vacío.");

        if (file.Length > ExcelCashFlowParser.MaxFileSizeBytes)
            return ValidationProblem("file", "El archivo supera el tamaño máximo permitido de 5 MB.");

        if (!string.Equals(file.ContentType, XlsxMimeType, StringComparison.OrdinalIgnoreCase))
            return ValidationProblem("file", "Solo se aceptan archivos .xlsx.");

        // The Content-Type header above is client-supplied and trivially spoofed, so it is only a
        // cheap early reject. The byte check below is as far as leading bytes can take us: an .xlsx
        // is a ZIP, so its signature is shared with .docx, .pptx, .jar and any plain .zip. Proving
        // the file is really a spreadsheet takes actually opening it as one, which the parser does.
        await using (var headerStream = file.OpenReadStream())
        {
            if (!await FileSignatureValidator
                    .MatchesAsync(headerStream, FileSignatureType.Xlsx, cancellationToken)
                    .ConfigureAwait(false))
                return ValidationProblem("file", "El archivo no es un .xlsx válido.");
        }

        // ── Load AnalysisResult — 403 if not found to avoid info leakage ─────────

        var result = await _analysisResultRepository
            .GetByIdAsync(resultId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");

        // A persona humana analysis (DDL 072) has no chat in M10 Phase 4a: nothing of a natural person's
        // evaluation goes to the model. Refused before any history, persistence or Claude call.
        var (personRefusal, _) = await RefuseChatOnPersonAnalysisAsync(result.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (personRefusal is not null) return personRefusal;

        // ── Resolve requesting user ───────────────────────────────────────────────
        // Resolved fully BEFORE any parsing work, so an unresolved caller costs nothing.

        var currentUser = await ResolveCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        // ── Read bytes, hash for the audit trail, parse ───────────────────────────

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        memoryStream.Position = 0;

        var hashBytes = await SHA256.HashDataAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var hashHex   = Convert.ToHexString(hashBytes).ToLowerInvariant();
        memoryStream.Position = 0;

        var parsed = _excelCashFlowParser.Parse(memoryStream);

        if (parsed.Status != ExcelParseStatus.Ok)
        {
            // A parse failure is the caller's problem and nobody else's: nothing was written, and
            // the chat conversation this attachment was headed for is untouched.
            _logger.LogInformation(
                "Chat attachment rejected ({Status}) for result {ResultId} tenant {TenantId}",
                parsed.Status, resultId, tenantId);

            return ValidationProblem("file", parsed.Status switch
            {
                ExcelParseStatus.MacroEnabledOrTemplate =>
                    "No se aceptan libros con macros ni plantillas. Guardá el archivo como .xlsx.",
                ExcelParseStatus.NoWorksheets =>
                    "El archivo no contiene ninguna hoja con datos.",
                ExcelParseStatus.DecompressionBombSuspected =>
                    "El archivo declara un contenido descomprimido desproporcionado y fue rechazado.",
                _ =>
                    "El archivo no es un libro de Excel (.xlsx) válido."
            });
        }

        // ── Audit ────────────────────────────────────────────────────────────────
        // What was attached, never what it said. The counts and hash ride newValueJson, an existing
        // free-text column, so an ephemeral upload needs no schema of its own.

        _ = _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       currentUser.UserId,
            action:       "Analysis.ChatAttachment.Parsed",
            severity:     "Info",
            entityType:   "AnalysisResult",
            entityId:     resultId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                fileName   = Path.GetFileName(file.FileName),
                sha256     = hashHex,
                sizeBytes  = file.Length,
                sheetCount = parsed.SheetCount,
                rowCount   = parsed.RowCount,
                cellCount  = parsed.CellCount,
                truncated  = parsed.Truncated
            }),
            ct:           CancellationToken.None);

        _logger.LogInformation(
            "Chat attachment parsed for result {ResultId} tenant {TenantId}: " +
            "sheets={SheetCount} rows={RowCount} cells={CellCount} truncated={Truncated}",
            resultId, tenantId, parsed.SheetCount, parsed.RowCount, parsed.CellCount, parsed.Truncated);

        return Ok(new ChatAttachmentResponse
        {
            Markdown   = parsed.Markdown,
            SheetCount = parsed.SheetCount,
            RowCount   = parsed.RowCount,
            CellCount  = parsed.CellCount,
            Truncated  = parsed.Truncated,
            Note       = parsed.Note
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in AnalysisController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    private async Task<User?> ResolveCurrentUserAsync(CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return null;

        return await _userRepository.GetByFirebaseUidAsync(firebaseUid, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The persona humana fields of a detail or decision response (DDL 072): the kind, the confirmations
    /// each approval record carried and the frozen evaluation with the decision rules that apply to it.
    /// A balance analysis gets its kind only and costs no query.
    /// </summary>
    private async Task ApplyPersonFieldsAsync(
        AnalysisResultResponse response, Analysis? analysis, string tenantId, CancellationToken cancellationToken)
    {
        response.AnalysisKind = analysis?.AnalysisKind ?? AnalysisKinds.Balance;
        if (analysis is null || response.AnalysisKind != AnalysisKinds.PersonaHumana)
            return;

        response.ValidationStatus = PersonAnalysesController.ValidationStatusNotApplicable;

        response.PreApprovalChecks  = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(analysis.PreApprovalChecksJson);
        response.ConfirmationChecks = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(analysis.ConfirmationChecksJson);

        var (rules, solicitante, fiadores) = await ResolvePersonDecisionRulesAsync(analysis.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        response.SolaFirmaAllowed               = rules.SolaFirmaAllowed;
        response.MinCounterGuaranteeCoveragePct = rules.MinCounterGuaranteeCoveragePct;
        response.PersonEvaluation = PersonEvaluationJson.ToDetailResponse(rules, solicitante, fiadores);
    }

    /// <summary>
    /// What approving a persona humana analysis requires: the tenant's CURRENT pf_ decision rules applied to
    /// the solicitante's frozen suggestion, with the aggregate counter-guarantee coverage of the frozen fiador
    /// rows. One resolution for the GET and the PATCH, so what the Portal is told is exactly what the decision
    /// enforces.
    /// </summary>
    private Task<(PersonDecisionRules Rules, AnalysisParticipant? Solicitante, IReadOnlyList<PersonFiadorEvaluationResponse> Fiadores)>
        ResolvePersonDecisionRulesAsync(
        Guid analysisId, string tenantId, CancellationToken cancellationToken)
        => PersonEvaluationJson.ResolveSolicitanteAsync(_personAnalysisRepository, analysisId, tenantId, cancellationToken);

    /// <summary>Why an approval below the fiadores' minimum coverage needs a reason, in es-AR.</summary>
    private static string CounterGuaranteeBelowMinDetail(CounterGuaranteeCoverage coverage)
    {
        var minimum = coverage.MinPct.ToString(EsArFormat.Culture);
        return coverage.Ratio is { } ratio
            ? $"La contragarantía de los fiadores cubre el {(ratio * 100m).ToString("0.#", EsArFormat.Culture)} % " +
              $"del monto solicitado, por debajo del mínimo de {minimum} %: aprobar exige un motivo (reviewNotes)."
            : $"La cobertura de la contragarantía de los fiadores no se puede calcular (la operación no está en pesos) y el " +
              $"mínimo es {minimum} %: aprobar exige un motivo (reviewNotes).";
    }

    /// <summary>A decision refusal with a stable <c>errorCode</c> the Portal branches on.</summary>
    private ObjectResult CodedDecisionProblem(int status, string title, string errorCode, string detail)
    {
        var problem = Problem(statusCode: status, title: title, detail: detail);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = errorCode;
        return problem;
    }

    /// <summary>
    /// 409 <c>chat_unavailable_for_person_analysis</c> when the analysis is a persona humana one (DDL 072),
    /// otherwise a null refusal. M10 Phase 4a sends nothing of a person's evaluation to the model, so its chat
    /// and its attachment are refused outright rather than answered on a PyME prompt. Also returns the kind
    /// and fiscal year it read (null when the analysis is not found), so the chat's reliability verdict costs
    /// no second query.
    /// </summary>
    private async Task<(IActionResult? Refusal, AnalysisKindAndFiscalYear? Analysis)> RefuseChatOnPersonAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken cancellationToken)
    {
        var analysis = await _personAnalysisRepository
            .GetKindAndFiscalYearAsync(analysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (analysis?.Kind != AnalysisKinds.PersonaHumana)
            return (null, analysis);

        var refused = Problem(statusCode: 409, title: "Conflict",
            detail: "El chat no está disponible para el análisis de una persona humana.");
        ((ProblemDetails)refused.Value!).Extensions["errorCode"] = "chat_unavailable_for_person_analysis";
        return (refused, analysis);
    }

    /// <summary>
    /// The chat's one "not available" answer: what a failed Claude call returns, and what a turn that could
    /// not be redacted returns (K5). One method so the two can never drift into telling the analyst
    /// different things about the same outcome — the turn was not answered.
    /// </summary>
    private IActionResult ChatServiceUnavailable()
        => Problem(statusCode: 502, title: "Bad Gateway",
            detail: "El servicio de chat de IA no está disponible temporalmente.");

    /// <summary>
    /// Loads what identifies the evaluated company and compiles the chat's redactor for this turn (K5):
    /// <c>Entities.BusinessName</c> (the ARCA razón social), <c>TradeName</c> and <c>CUIT</c>, plus the
    /// LUFE registry name and the authorities/partners from BOTH LUFE snapshots — the one frozen on the
    /// result (what the executive summary was written beside) and the entity's current one (who the
    /// partners are now). One read: <see cref="IEntityRepository.GetDetailByIdAsync"/> carries the three
    /// columns and the current snapshot together, and the narrow name projection is pinned to four columns
    /// for the list paths that share it.
    /// </summary>
    /// <returns>
    /// The redactor, or null — FAIL CLOSED — when the analysis or the entity row is missing, the read fails
    /// or the patterns cannot be built. Each null logs one Error naming the reason and never the exception
    /// object or any identifier. An UNREADABLE LUFE blob is not a null: it costs its own names (Warning),
    /// while the entity's name, trade name and CUIT are still enforced — otherwise one malformed vendor blob
    /// would close the chat on that analysis for good.
    /// </returns>
    private async Task<EntityIdentifierRedactor?> BuildChatRedactorOrNullAsync(
        AnalysisResult            result,
        AnalysisKindAndFiscalYear? analysis,
        Guid                      resultId,
        string                    tenantId,
        CancellationToken         cancellationToken)
    {
        if (analysis is null)
        {
            LogChatTurnNotRedactable(resultId, tenantId, "analysis_not_found");
            return null;
        }

        EntityDetailProjection? entity;

        try
        {
            entity = await _entityRepository
                .GetDetailByIdAsync(analysis.EntityId, tenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type only: a database exception's message can quote the query's parameters.
            LogChatTurnNotRedactable(resultId, tenantId, $"lookup_failed:{ex.GetType().Name}");
            return null;
        }

        if (entity is null)
        {
            LogChatTurnNotRedactable(resultId, tenantId, "entity_not_found");
            return null;
        }

        var identifiers = BuildChatEntityIdentifiers(
            entity.BusinessName, entity.TradeName, entity.CUIT,
            [result.LufeDataJson, entity.LufeDataJson],
            out var lufeUnreadable);

        if (lufeUnreadable)
        {
            _logger.LogWarning(
                "LUFE snapshot of result {ResultId} tenant {TenantId} could not be read; the chat strips the entity's name, trade name and CUIT but not its LUFE names",
                resultId, tenantId);
        }

        if (!EntityIdentifierRedactor.TryCreate(identifiers, out var redactor) || redactor is null)
        {
            LogChatTurnNotRedactable(resultId, tenantId, "redaction_failed");
            return null;
        }

        return redactor;
    }

    /// <summary>
    /// The identifiers of one entity, as <see cref="EntityIdentifierRedactor"/> looks for them: its name,
    /// trade name and CUIT, and from each LUFE snapshot (<c>$.Entidad.nombre</c>/<c>cuit</c> and
    /// <c>$.Autoridades[*].denominacion</c>/<c>cuit</c>) the registry name and every authority or partner —
    /// a legal person (CUIT prefix 30/33/34) under the company-name rules, anyone else under the person rules.
    /// Static and dependency-free so it is testable without a controller. Duplicates are left to the redactor.
    /// </summary>
    /// <param name="businessName"><c>Entities.BusinessName</c>.</param>
    /// <param name="tradeName"><c>Entities.TradeName</c>, or null.</param>
    /// <param name="cuit"><c>Entities.CUIT</c>.</param>
    /// <param name="lufeSnapshots">LUFE snapshots (<c>LufeData</c>, PascalCase root); null entries are skipped.</param>
    /// <param name="lufeUnreadable">True when a snapshot was present but is not readable JSON.</param>
    internal static EntityIdentifiers BuildChatEntityIdentifiers(
        string                 businessName,
        string?                tradeName,
        string                 cuit,
        IReadOnlyList<string?> lufeSnapshots,
        out bool               lufeUnreadable)
    {
        ArgumentNullException.ThrowIfNull(lufeSnapshots);

        lufeUnreadable = false;

        var companies = new List<string> { businessName };
        var related   = new List<string>();
        var persons   = new List<string>();
        var cuits     = new List<string> { cuit };

        if (!string.IsNullOrWhiteSpace(tradeName))
            companies.Add(tradeName);

        foreach (var lufeJson in lufeSnapshots.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(lufeJson)) continue;

            if (!TryReadLufeIdentifiers(lufeJson, companies, related, persons, cuits))
                lufeUnreadable = true;
        }

        return new EntityIdentifiers(companies, related, persons, cuits);
    }

    /// <summary>Adds one LUFE snapshot's names and CUITs to the lists. False when it is not readable JSON.</summary>
    private static bool TryReadLufeIdentifiers(
        string lufeJson, List<string> companies, List<string> related, List<string> persons, List<string> cuits)
    {
        try
        {
            using var document = JsonDocument.Parse(lufeJson);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "Entidad", out var entidad) && entidad.ValueKind == JsonValueKind.Object)
                {
                    if (ReadLufeString(entidad, "nombre") is { } nombre) companies.Add(nombre);
                    if (ReadLufeCuit(entidad, "cuit") is { } cuit) cuits.Add(cuit);
                }

                if (TryGetPropertyIgnoreCase(root, "Autoridades", out var autoridades)
                    && autoridades.ValueKind == JsonValueKind.Array)
                {
                    foreach (var autoridad in autoridades.EnumerateArray())
                    {
                        if (autoridad.ValueKind != JsonValueKind.Object) continue;

                        var cuit = ReadLufeCuit(autoridad, "cuit");
                        if (cuit is not null) cuits.Add(cuit);

                        if (ReadLufeString(autoridad, "denominacion") is not { } denominacion) continue;

                        if (CuitHelper.PersonTypeFromPrefix(cuit) == PersonTypes.Juridica)
                            related.Add(denominacion);
                        else
                            persons.Add(denominacion);
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Redacts every string the chat sends (K5) and logs how many occurrences were replaced — counts only,
    /// never text or identifiers. Null, after one Error, when any of them could not be redacted.
    /// </summary>
    private RedactedChatRequest? RedactChatRequestOrNull(
        EntityIdentifierRedactor           redactor,
        string                             systemPrompt,
        IReadOnlyList<ConversationMessage> history,
        string                             message,
        Guid                               resultId,
        string                             tenantId)
    {
        var redacted = RedactChatRequest(redactor, systemPrompt, history, message);

        if (redacted is null)
        {
            LogChatTurnNotRedactable(resultId, tenantId, "redaction_failed");
            return null;
        }

        if (redacted.Counts.Total > 0)
        {
            _logger.LogInformation(
                "Chat redaction for result {ResultId} tenant {TenantId} replaced {Names} name, {Persons} person and {Cuits} CUIT occurrence(s)",
                resultId, tenantId, redacted.Counts.Names, redacted.Counts.Persons, redacted.Counts.Cuits);
        }

        return redacted;
    }

    /// <summary>
    /// The pure half of <see cref="RedactChatRequestOrNull"/>: the system prompt, every history turn (both
    /// roles, as new copies) and the current message through <paramref name="redactor"/>. Null when any
    /// string could not be redacted.
    /// </summary>
    internal static RedactedChatRequest? RedactChatRequest(
        EntityIdentifierRedactor           redactor,
        string                             systemPrompt,
        IReadOnlyList<ConversationMessage> history,
        string                             message)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(history);

        if (!redactor.TryRedact(systemPrompt, out var redactedPrompt, out var counts))
            return null;

        var redactedHistory = new List<ConversationMessage>(history.Count);
        foreach (var turn in history)
        {
            if (!redactor.TryRedact(turn.Content, out var content, out var turnCounts))
                return null;

            redactedHistory.Add(new ConversationMessage { Role = turn.Role, Content = content });
            counts = counts.Add(turnCounts);
        }

        if (!redactor.TryRedact(message, out var redactedMessage, out var messageCounts))
            return null;

        return new RedactedChatRequest(redactedPrompt, redactedHistory, redactedMessage, counts.Add(messageCounts));
    }

    private void LogChatTurnNotRedactable(Guid resultId, string tenantId, string reason)
        => _logger.LogError(
            "Chat turn for result {ResultId} tenant {TenantId} not sent: entity identifiers could not be applied ({Reason})",
            resultId, tenantId, reason);

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadLufeString(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A LUFE CUIT is a JSON number (<c>long</c>); a string is accepted too.</summary>
    private static string? ReadLufeCuit(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0
                => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString())
                => value.GetString(),
            _ => null
        };
    }

    /// <summary>
    /// Field-level 400, shaped exactly like the one DocumentsController returns so the Portal reads
    /// errorCode / errors the same way on every upload path.
    /// </summary>
    private IActionResult ValidationProblem(string field, string message)
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

    private static string Truncate(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");
    }

    /// <summary>
    /// Reduces a caller-supplied attachment file name to what may be stored: the leaf name, trimmed,
    /// bounded by <see cref="AttachmentFileNameMaxChars"/>, or <c>null</c> when there is nothing
    /// worth keeping.
    /// </summary>
    /// <remarks>
    /// Any directory a caller puts in the field is stripped — the browser sends a leaf name, but this
    /// field is ordinary JSON and nothing stops a client posting a path, which would then be rendered
    /// straight back by the Portal.
    /// <para>
    /// NOT <see cref="Path.GetFileName(string)"/>, deliberately. That follows the RUNNING platform:
    /// on Linux it does not treat <c>\</c> as a separator at all, so a Windows path would be stored
    /// whole there and split on the Windows App Service — the same input producing two different rows
    /// depending on the host, and a test suite on ubuntu-latest that cannot see the production
    /// behaviour. This value describes the CALLER's filesystem, not ours, so both separators are cut
    /// unconditionally.
    /// </para>
    /// <para>
    /// Blank in, <c>null</c> out, matching the blank-is-absent rule
    /// <see cref="CapAttachmentMarkdown"/> already applies to the Markdown itself. It TRUNCATES
    /// rather than refusing, for the same reason the Markdown does.
    /// </para>
    /// </remarks>
    /// <param name="fileName">The caller-supplied file name, or <c>null</c>.</param>
    /// <returns>The name to store, or <c>null</c> when none was usable.</returns>
    private static string? NormalizeAttachmentFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var trimmed = fileName.Trim();
        var cut     = trimmed.LastIndexOfAny(AttachmentPathSeparators);
        var leaf    = cut < 0 ? trimmed : trimmed[(cut + 1)..];

        // A value that is all directory ("planillas/") has an empty leaf, which is why the blank test
        // is repeated here rather than assumed away by the one above.
        return string.IsNullOrWhiteSpace(leaf)
            ? null
            : Truncate(leaf, AttachmentFileNameMaxChars);
    }

    /// <summary>
    /// Applies <see cref="AttachmentMarkdownMaxChars"/> to an attachment, appending a note in the
    /// same es-AR voice <see cref="ExcelCashFlowParser"/> already uses for its own truncation, so
    /// the model is TOLD the data is incomplete instead of silently reasoning over a table that
    /// stops mid-row. Truncating rather than refusing is deliberate: the parser applies no per-cell
    /// length cap, so /chat/attachment can legitimately return Markdown above this ceiling, and a
    /// 400 here would dead-end an analyst whose file the server had already accepted.
    /// </summary>
    /// <param name="markdown">The caller-supplied attachment Markdown, or <c>null</c>.</param>
    /// <returns>The Markdown to send (null when there is none), and whether it had to be cut.</returns>
    internal static (string? Markdown, bool Truncated) CapAttachmentMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return (null, false);

        if (markdown.Length <= AttachmentMarkdownMaxChars)
            return (markdown, false);

        var cutLength = AttachmentMarkdownMaxChars;

        // Never cut between the halves of a surrogate pair. An emoji or a CJK glyph in a cell would
        // otherwise leave a lone surrogate at the seam, and that lands in a JSON string on the very
        // next hop — a serialisation fault THERE happens outside the ClaudeApiException catch, so it
        // would surface as a 500 with the analyst's turn already persisted and no assistant reply,
        // the exact stranded-message failure this endpoint is built to avoid.
        if (char.IsHighSurrogate(markdown[cutLength - 1]))
            cutLength--;

        // One string, not five Concat arguments: the span overloads top out at four, and a mixed
        // span/string call silently binds to Concat(params object[]), which cannot box a ref struct.
        var note = "\n\n" +
                   "> **Nota:** el adjunto se truncó por su extensión y termina de forma abrupta. " +
                   "No infieras totales ni conclusiones sobre las filas que faltan.";

        return (string.Concat(markdown.AsSpan(0, cutLength), note), true);
    }

    /// <summary>
    /// Returns the 429 to send when this conversation has already outgrown its plan's
    /// <c>ChatContextBudgetTokens</c>, or <c>null</c> when the turn should proceed. See the call site
    /// in <see cref="ChatAsync"/> for why the gate sits where it does and why a brand-new
    /// conversation can never reach the comparison.
    /// </summary>
    /// <remarks>
    /// FAILS OPEN on anything short of a fact. It does NOT read the plan itself — the caller resolves
    /// it once, for this gate and for the chat-model allow-list — and it treats both of that read's
    /// non-answers the same way: <c>null</c> (the read threw) and <c>IsDegraded</c> (the read failed
    /// and fell back) each let the turn through unchecked. The distinction that matters is the third
    /// case: a tenant genuinely ON the minimum plan carries the same 40.000 and IS gated on it.
    /// Without that separation one blipped SELECT would cut a 200-plan tenant's conversation off at
    /// the 50-plan budget.
    /// <para>
    /// Synchronous: everything it needs is already in hand by the time it is called.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Resolves the tenant's effective billing plan for a chat turn, or <c>null</c> when it could not
    /// be read.
    ///
    /// <para>
    /// The ONE read a chat turn makes. Both consumers — the context-budget gate and the chat-model
    /// allow-list — take their answer from it, so the turn cannot ask twice and cannot get two
    /// different answers.
    /// </para>
    ///
    /// <para>
    /// Null means FAILED, and every caller must fail open on it. <c>GetEffectiveAsync</c> is
    /// documented never to throw and answers a failed read with the MINIMUM plan, but a caller
    /// trusting its numbers alone would turn one blipped SELECT into the strictest possible outcome —
    /// a 200-plan tenant's conversation cut off at the 50-plan budget. The call is wrapped anyway:
    /// "never throws" is a contract, and a turn whose question is already about to be persisted is
    /// not the place to take a contract on faith. Cancellation is excluded deliberately — a cancelled
    /// request is not a degraded plan read.
    /// </para>
    /// </summary>
    private async Task<BillingPlanResolution?> ResolveChatBillingPlanOrNullAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _tenantBillingConfigService
                .GetEffectiveAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same fail-open shape as the extraction budget in TriggerAsync: log it, let the turn
            // through unchecked rather than pushing the failure down to the Claude call with the
            // analyst's turn already written.
            _logger.LogWarning(ex,
                "Billing configuration could not be resolved for tenant {TenantId}; the chat turn " +
                "proceeds without the context check and on the default model.",
                tenantId);

            return null;
        }
    }

    private IActionResult? BuildChatContextGateOrNull(
        string tenantId,
        Guid conversationId,
        IReadOnlyList<ChatMessage> dbMessages,
        BillingPlanResolution? resolution)
    {
        // Measured BEFORE the plan is consulted. A conversation with no billed assistant turn cannot
        // be gated whatever the budget says, so nothing below it is worth evaluating.
        //
        // This early return used to be observable as "the config service was never called at all",
        // and it no longer is: the plan is read once by the caller for the chat-model allow-list,
        // whether this gate needs it or not. What it still guarantees is unchanged and is what
        // actually matters — a brand-new conversation cannot be REFUSED, because there is no billed
        // turn to measure it against. See the TRIPWIRE at the call site.
        var lastInputTokens = LastAssistantInputTokens(dbMessages);
        if (lastInputTokens is null)
            return null;

        // Null is a FAILED read; degraded is a read that fell back. Neither is a fact about this
        // tenant's entitlement, and "on the minimum plan" is — all three carry 40.000, only one of
        // them means it.
        if (resolution is null || resolution.IsDegraded)
        {
            _logger.LogWarning(
                "Chat context budget for tenant {TenantId} is degraded (billing configuration could not be read); " +
                "allowing conversation {ConversationId} through without the context check.",
                tenantId, conversationId);

            return null;
        }

        var budget = resolution.Plan.ChatContextBudgetTokens;

        // Non-positive disables the gate for this tenant. PUT /api/billing/config rejects <= 0, so
        // the only way to land here is a DBA-seeded row — and a DBA who wrote 0 meant "off", not
        // "refuse every turn after the first".
        if (budget <= 0)
            return null;

        // >=, matching BillingQuotaFilter: at the budget is over it.
        if (lastInputTokens.Value < budget)
            return null;

        // Null-safe like the extraction budget above: ten test call sites construct this controller
        // with a null billingOptions, and the honest reading of "no options bound" is "not enforcing".
        if (!(_billingOptions?.Value?.EnforceChatContextBudget ?? false))
        {
            // Measured, not enforced. This log is the evidence product needs to agree the real
            // per-plan context budgets before Billing:EnforceChatContextBudget is switched on — see
            // BillingOptions.EnforceChatContextBudget for why it ships off, and for the attachment
            // caveat these logs are the only way to size.
            _logger.LogWarning(
                "Chat context budget reached for tenant {TenantId} on plan {PlanName}: conversation " +
                "{ConversationId} last billed {ContextTokens}/{Budget} input tokens. " +
                "NOT blocking — Billing:EnforceChatContextBudget is off.",
                tenantId, resolution.Plan.PlanName, conversationId, lastInputTokens.Value, budget);

            return null;
        }

        _logger.LogWarning(
            "Chat context budget reached for tenant {TenantId} on plan {PlanName}: conversation " +
            "{ConversationId} last billed {ContextTokens}/{Budget} input tokens. " +
            "Turn rejected with 429; the analyst must start a new conversation.",
            tenantId, resolution.Plan.PlanName, conversationId, lastInputTokens.Value, budget);

        var problem = new ProblemDetails
        {
            Status   = StatusCodes.Status429TooManyRequests,
            Title    = "Too Many Requests",
            Detail   = "Esta conversación alcanzó su límite de contexto. Iniciá una nueva conversación para seguir.",
            Type     = ErrorTypeBase + "chat_context_budget_exceeded",
            Instance = HttpContext.Request.Path
        };

        // Deliberately NOT the used/limit/periodStart/periodEnd shape BillingQuotaFilter writes,
        // despite the note there about the Portal parsing one quota shape. errorCode and planName are
        // shared because they mean the same thing in both; the numbers are not. Emitting used/limit
        // WITHOUT the period keys would be a partial impersonation of the monthly shape — worse than
        // a distinct one, because a renderer that assumes "quota shape ⇒ period fields" would caption
        // a per-conversation ceiling "este mes" next to a date that does not exist here. With
        // distinct keys an un-updated Portal falls back to Detail alone, which already states both
        // the problem and the remedy, and is correct.
        problem.Extensions["errorCode"]      = "CHAT_CONTEXT_BUDGET_EXCEEDED";
        problem.Extensions["used"]           = lastInputTokens.Value;
        problem.Extensions["budget"]         = budget;
        problem.Extensions["planName"]       = resolution.Plan.PlanName;
        problem.Extensions["conversationId"] = conversationId.ToString();

        return new ObjectResult(problem)
        {
            StatusCode   = StatusCodes.Status429TooManyRequests,
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>
    /// The input_tokens Anthropic billed on the most recent MEASURABLE assistant turn of a
    /// conversation, or <c>null</c> when there is none — which is the whole of the "a new
    /// conversation is never gated" rule. That figure is the size of the context as of that answer:
    /// system prompt plus the full replayed history plus that turn's message.
    /// </summary>
    /// <remarks>
    /// "Measurable" excludes a turn that carried an ephemeral attachment
    /// (<see cref="ChatMessage.HadEphemeralAttachment"/>, script 053). Its input_tokens covers the
    /// whole request INCLUDING the attachment block, which is never replayed (it is stored on the user
    /// turn since script 055, but not in <c>Content</c>, which is what history is rebuilt from), so
    /// that number describes a prompt the next turn will not rebuild — measuring by it pins a
    /// conversation whose real transcript is tiny. Skipping it can only under-report, which is the
    /// same safe direction as every other skip below.
    /// <para>
    /// Internal rather than private so it can be pinned with a plain list — no controller, no DI, no
    /// HttpContext, no repository stub — matching BuildChatSystemPrompt and BuildBcraChatSummary.
    /// </para>
    /// </remarks>
    internal static int? LastAssistantInputTokens(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is null || messages.Count == 0)
            return null;

        // Backwards: GetMessagesAsync orders by ChatMessageId ASC, and the turn that describes how
        // large this conversation currently IS is the last one Claude billed for a prompt the next
        // turn will actually rebuild.
        //
        // All three halves of the predicate are required, and scanning PAST a non-matching row is the
        // lenient direction on purpose. A user turn carries no InputTokens at all, and an assistant
        // row written before the column was populated carries none either.
        //
        // THE THIRD SKIP IS THE ATTACHMENT ONE, and it is why "older turns are strictly smaller than
        // newer ones" needs qualifying: it holds for the transcript, which only grows, but NOT for
        // the billed figure. An attachment turn's InputTokens includes up to ~67k tokens of
        // spreadsheet (AttachmentMarkdownMaxChars) that was never written to ChatMessages.Content and
        // is therefore absent from every later prompt — a number the next turn cannot reproduce and
        // that would have the gate pin the conversation on a prompt that no longer exists. Skipping
        // it falls back to the last CLEAN turn, which really is smaller, so the guarantee that
        // matters survives intact: this can only UNDER-report — it can let a turn through that a
        // complete measurement would have refused, never the reverse. A conversation whose only
        // assistant turn carried an attachment measures as null and is simply not gated, exactly like
        // a brand-new one, and fails open for the same reason.
        //
        // Role is compared case-insensitively although this controller only ever writes it lowercase:
        // rows can also arrive from a DDL script. Rows predating script 053 read false — "no
        // attachment recorded" is treated as "no attachment", which preserves the previous behaviour
        // for them exactly.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];

            if (message?.InputTokens is not { } tokens)
                continue;

            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;

            if (message.HadEphemeralAttachment)
                continue;

            return tokens;
        }

        return null;
    }

    /// <summary>
    /// Produces the deterministic es-AR cash-flow reading ("LECTURA DEL FLUJO") for the CHAT system
    /// prompt, or an EMPTY list when there is nothing to read. Returns empty, never null.
    ///
    /// <para>
    /// Read-time only — nothing is persisted, exactly like the reading <see cref="GetById"/> serves
    /// and the one the PDF export renders. Same extract → reconcile → read pipeline, so the model
    /// reasons over the identical audited text the analyst has on screen instead of the raw figures
    /// alone. The reconciliation is RECOMPUTED here (not read from the blob) so the gate works even
    /// for analyses whose persisted BalanceJson has no reconciliation node.
    /// </para>
    ///
    /// <para>
    /// FAULT ISOLATION is the contract, and it is stricter here than on the detail path: by the time
    /// chat reaches this the analyst's turn is ALREADY persisted, so a throw would strand a user
    /// message with no assistant reply and surface as a 500. Any failure is logged and degrades to
    /// an empty reading — the prompt then prints the header plus "No disponible" and the turn
    /// completes normally. A reading failure costs the block, never the turn.
    /// </para>
    ///
    /// <para>
    /// Deliberately NOT reused by <see cref="GetById"/>: that endpoint distinguishes "no statement
    /// at all" (the response field stays null and serialises as null) from "a statement with an
    /// empty reading" (<c>[]</c>), a distinction the Portal reads off the wire. Collapsing the two
    /// here would be an API-visible change to a different endpoint.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> BuildCashFlowNarrativeOrEmpty(
        string? balanceJson, Guid resultId, string tenantId)
    {
        try
        {
            var cashFlow = TryExtractCashFlow(balanceJson);

            // Load-bearing, not defensive noise: Read dereferences cashFlow.Present with no null
            // guard of its own. A present:false statement needs no guard — Read handles it.
            if (cashFlow is null)
                return Array.Empty<string>();

            var reconciliation = _cashFlowReconciliationService.Reconcile(cashFlow);
            return _cashFlowNarrativeService.Read(cashFlow, reconciliation);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cash-flow narrative failed for result {ResultId}, tenant {TenantId}; " +
                "the chat prompt will omit the reading.",
                resultId, tenantId);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Explains the one thing the chat BCRA context deliberately withholds. Without it the model
    /// reads the absent key as absent DATA and answers "no tengo información de cheques" to a
    /// question the aggregates it DOES have would answer — the regression this change would
    /// otherwise introduce. Names only the fields that actually survive; promises nothing else.
    /// </summary>
    private const string ChequeDetalleElisionNote =
        "(Del bloque de cheques rechazados se omitió cheques.detalle —el listado cheque por " +
        "cheque— por su extensión. Sí tenés los totales de rechazados e impagos, los montos " +
        "totales rechazado e impago, las fechas de primer y último rechazo, el desglose por " +
        "causal y los indicadores de proceso judicial y revisión: respondé con esos datos y NO " +
        "afirmes que no tenés información de cheques. Si te piden un cheque puntual (número, " +
        "fecha o entidad), indicá que ese detalle lo tiene el analista a la vista en la pantalla " +
        "de análisis.)";

    /// <summary>
    /// Renders the BCRA blob for the CHAT system prompt: the persisted <c>BcraDataJson</c> with
    /// <c>cheques.detalle</c> removed, plus the two identifying fields on each block.
    ///
    /// <para>
    /// <c>cheques.detalle</c> is one object per rejected cheque — ~295 bytes serialised, and a
    /// cheque-heavy CUIT carries hundreds to thousands of them, dwarfing every other block. The
    /// prompt is rebuilt from scratch on EVERY chat turn, so the whole array was being re-sent to
    /// Claude for a list the analyst almost never asks about. Every aggregate beside it —totals,
    /// montos, first/last rejection dates, the per-causal breakdown, the judicial/revision flags—
    /// is computed independently of the array, so dropping it costs the model nothing it can
    /// actually use. <c>deuda</c> stays whole: it is the credit core and its
    /// <c>entidadesDetalle</c> is ~40 reporting entities at most.
    /// </para>
    ///
    /// <para>
    /// <c>identificacion</c> and <c>denominacion</c> come off both blocks because CUIT and company
    /// name are PII and must never reach a Claude prompt. The sibling
    /// <see cref="Servicios.ClaudeService.BuildNarrativePrompt"/> already anonymises for the same
    /// reason ("Empresa: la empresa evaluada"); this path was the outlier only because it shipped
    /// the blob verbatim.
    /// </para>
    ///
    /// <para>
    /// Edited as a <see cref="JsonNode"/> tree rather than round-tripped through
    /// <see cref="BcraData"/>, for two reasons. First, every key this method does not name passes
    /// through untouched — a property added to <see cref="BcraChequesData"/> tomorrow reaches the
    /// prompt with no edit here, and a key written by a newer build than the one reading it is not
    /// silently dropped, which a deserialise/re-serialise would do. The flip side is that a key
    /// worth withholding has to be named: <c>deuda.historialMontosPorSituacion</c> is, because it
    /// is a chart affordance whose signal the model already gets from the two series beside it.
    /// Second, nothing is mutated:
    /// <see cref="TryDeserializeBcraData"/> is the same helper GET /api/analyses/{id}/context uses
    /// to ship this blob — cheque rows included — to the Portal, so nulling fields on an instance
    /// it returned would couple this prompt to that endpoint's response through a shared object
    /// the moment anyone memoised it.
    /// </para>
    ///
    /// <para>
    /// The key is REMOVED, not nulled or emptied. <see cref="BcraChequesData.Detalle"/> documents
    /// <c>null</c> as "legacy stored data" and <c>[]</c> as "no cheques reported"; either would
    /// contradict a non-zero <c>totalRechazados</c> sitting three keys above it.
    /// </para>
    ///
    /// <para>
    /// Never throws: a blank, malformed or non-object blob degrades to "No disponible", matching
    /// <see cref="TryExtractCashFlow"/> and every other blob read on this controller.
    /// </para>
    /// </summary>
    internal static string BuildBcraChatSummary(string? bcraDataJson)
    {
        const string NoDisponible = "No disponible";

        if (string.IsNullOrWhiteSpace(bcraDataJson)) return NoDisponible;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(bcraDataJson);
        }
        catch (JsonException)
        {
            // Malformed BcraDataJson — degrade, exactly as the cash-flow block does. Passing it
            // through raw would re-admit the whole payload in precisely the pathological case.
            return NoDisponible;
        }

        // Pattern-matched rather than cast: the literal `null`, a bare scalar and a top-level
        // array all parse cleanly, and indexing a non-object node throws InvalidOperationException
        // — which is NOT a JsonException and would escape the catch above.
        if (parsed is not JsonObject bcra) return NoDisponible;

        var elided = false;

        if (bcra["cheques"] is JsonObject cheques)
        {
            elided = cheques["detalle"] is JsonArray { Count: > 0 };

            cheques.Remove("detalle");
            cheques.Remove("identificacion");
            cheques.Remove("denominacion");
        }

        if (bcra["deuda"] is JsonObject deuda)
        {
            deuda.Remove("identificacion");
            deuda.Remove("denominacion");

            // Chart-only, and not free: this blob is rebuilt into the system prompt on EVERY turn
            // of every conversation. historialMontos already carries each período's total and
            // historialSituaciones its severity, so the per-situación split of the same 24 months
            // costs tokens on every turn and tells the model nothing the two series beside it do
            // not. Named here precisely because the passthrough rule above would otherwise ship it.
            deuda.Remove("historialMontosPorSituacion");
        }

        var json = bcra.ToJsonString();

        // The note is emitted only when rows were actually withheld. A blob with no detalle key,
        // or an empty one, hides nothing — telling the model something was elided AND that the
        // analyst can see it would be false on both halves.
        return elided ? json + "\n" + ChequeDetalleElisionNote : json;
    }

    /// <summary>
    /// Builds the CHAT system prompt, rebuilt from scratch and re-sent on every turn of every
    /// conversation. Static and dependency-free on purpose: everything it needs arrives as an
    /// argument, so the whole prompt shape is unit-testable with no controller, no DI, no stubs.
    /// </summary>
    /// <param name="result">The persisted analysis the conversation is grounded in.</param>
    /// <param name="cashFlowNarrative">
    /// The deterministic es-AR reading from <see cref="ICashFlowNarrativeService"/>, computed at the
    /// call site by <see cref="BuildCashFlowNarrativeOrEmpty"/>; empty when there is no statement or
    /// the reading failed. REQUIRED, never defaulted: a caller that forgot to wire it would
    /// otherwise compile clean and ship a prompt asserting the reading is "No disponible" on an
    /// analysis that has one — a false statement to the model, worse than emitting no block at all.
    /// The compiler is the enforcement.
    /// </param>
    /// <param name="hasAttachment">
    /// Whether THIS turn carries an analyst-uploaded spreadsheet. The attachment reaches the model
    /// once by design — it rides the final user message and never enters ChatMessages.Content, so it
    /// is absent from <c>history</c> on every later turn — and the model cannot observe that on its
    /// own. Without this flag it answers a follow-up question about the spreadsheet by RECONSTRUCTING
    /// one, presenting invented monthly figures as if read from the file. REQUIRED, never defaulted,
    /// for the same reason as <paramref name="cashFlowNarrative"/>: a defaulted-to-false caller on a
    /// turn that DOES carry a spreadsheet would tell the model to refuse the very data sitting in
    /// its own message.
    /// </param>
    /// <param name="reliability">
    /// The score-reliability verdict of this row (score credibility 1b-3), from the one adapter. Rendered as
    /// fixed phrases right after DATOS DEL ANÁLISIS — never its messages, which can quote the entity's sector.
    /// Null, or <c>complete</c> with no reason, emits nothing. REQUIRED, never defaulted, for the same reason
    /// as the two parameters above: a caller that forgot it would let the model present doubtful figures as
    /// certain.
    /// </param>
    /// <param name="fiscalYear">
    /// <c>Analyses.FiscalYear</c>, handed to <see cref="ExtractionIntegrityReader.Read"/> when the verdict
    /// reports a failed integrity check. Required for the same reason.
    /// </param>
    internal static string BuildChatSystemPrompt(
        AnalysisResult            result,
        IReadOnlyList<string>     cashFlowNarrative,
        bool                      hasAttachment,
        ScoreReliabilityResponse? reliability,
        int?                      fiscalYear)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "Sos un analista financiero senior especializado en PyMEs argentinas " +
            "trabajando para una SGR (Sociedad de Garantía Recíproca).");
        sb.AppendLine(
            "Tenés acceso al análisis financiero completo de la empresa evaluada.");
        sb.AppendLine();

        sb.AppendLine("DATOS DEL ANÁLISIS:");
        sb.AppendLine(result.AiSuggestedScore.HasValue
            ? $"- Score de riesgo: {ScoreScale.ToNosis(result.AiSuggestedScore.Value)}/1000 (escala NOSIS) — Nivel: {result.AiSuggestedLevel ?? "N/D"}"
            : "- Score de riesgo: No disponible");
        sb.AppendLine($"- RESUMEN EJECUTIVO (estados financieros): {result.ExecutiveSummary ?? "No disponible"}");
        sb.AppendLine($"- JUSTIFICACIÓN DEL SCORE: {result.ScoringNarrative ?? "No disponible"}");
        sb.AppendLine();

        var qualityBlock = BuildChatDataQualityBlock(result.BalanceJson, reliability, fiscalYear);
        if (qualityBlock is not null)
        {
            sb.AppendLine(qualityBlock);
            sb.AppendLine();
        }

        var breakdownBlock = FormatScoreBreakdownBlock(result.ScoreBreakdownJson, result.AiSuggestedScore);
        if (breakdownBlock is not null)
        {
            sb.AppendLine(breakdownBlock);
            sb.AppendLine();
        }

        sb.AppendLine("RATIOS FINANCIEROS:");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.RatiosJson)
            ? "No disponible"
            : result.RatiosJson);
        if (NoCurrentLiabilitiesRatioNote(result.ScoreBreakdownJson) is { } noCurrentLiabilitiesNote)
            sb.AppendLine(noCurrentLiabilitiesNote);
        sb.AppendLine();

        // The extracted figures behind the ratios above: scalars, rubros, prior year, then fixed rules
        // (no cost of sales, no indirect derivation, financial debt ≠ total liabilities). Header always
        // emitted; "No disponible" when the blob holds no figure. See BuildChatBalanceFiguresBlock.
        sb.AppendLine(BuildChatBalanceFiguresBlock(result.BalanceJson));
        sb.AppendLine();

        sb.AppendLine("FLUJO DE EFECTIVO:");
        var cashFlow = TryExtractCashFlow(result.BalanceJson);
        sb.AppendLine(cashFlow is { Present: true }
            ? JsonSerializer.Serialize(cashFlow)
            : "No disponible");
        sb.AppendLine(
            "(El flujo de efectivo es contexto para el analista y NO se utilizó en el cálculo del " +
            "score de riesgo; no afirmes que el score refleja estas cifras.)");
        sb.AppendLine();

        // The same deterministic reading the analyst sees on the detail screen and in the exported
        // PDF, rendered VERBATIM — one finished sentence per line, in order, never reworded,
        // re-ordered, summarised or bullet-decorated, and with no lead sentence of our own. Byte
        // identity with ICashFlowNarrativeService's output is the point: the model must reason over
        // the audited text, not a paraphrase of it. ADDITIVE — the raw block above is untouched and
        // still carries its own disclaimer.
        //
        // The header is emitted UNCONDITIONALLY. An analysis with no statement (or whose reading
        // failed) prints "No disponible" beneath it, so the prompt shape stays stable across every
        // analysis and an absent block is never read by the model as an absent CONCEPT — the same
        // failure ChequeDetalleElisionNote exists to prevent for cheques.
        sb.AppendLine("LECTURA DEL FLUJO (determinística):");

        var wroteReading = false;
        if (cashFlowNarrative is { Count: > 0 })
        {
            foreach (var sentence in cashFlowNarrative)
            {
                // A blank entry would emit an empty line, and empty lines are this prompt's ONLY
                // block delimiter — everything after it would read as an unlabelled new block.
                if (string.IsNullOrWhiteSpace(sentence)) continue;

                sb.AppendLine(sentence);
                wroteReading = true;
            }
        }

        if (!wroteReading)
            sb.AppendLine("No disponible");

        // "determinística" in the header would otherwise invite the model to read this as a computed
        // scoring signal. The rounding clause is not filler either: Magnitude/Signed round to whole
        // pesos while the raw JSON one block above carries the exact double, and without the clause
        // the model reports a reconciliation discrepancy between the two blocks that does not exist.
        sb.AppendLine(
            "(Esta lectura la produce un motor de reglas determinístico sobre las mismas cifras del " +
            "bloque anterior, redondeadas a pesos enteros — no la generó un modelo y no hay " +
            "discrepancia entre ambos bloques. Es contexto para el analista y TAMPOCO se utilizó en " +
            "el cálculo del score de riesgo.)");
        sb.AppendLine();

        sb.AppendLine("CONTEXTO BCRA:");
        sb.AppendLine(BuildBcraChatSummary(result.BcraDataJson));
        sb.AppendLine();

        sb.AppendLine("VARIABLES MACRO:");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.MacroDataJson)
            ? "No disponible"
            : result.MacroDataJson);
        sb.AppendLine();

        sb.AppendLine(
            "ESCALA DE SCORE — MUY IMPORTANTE: " +
            "El score está expresado en escala NOSIS 0-1000, donde 1000 es la máxima solvencia y 0 es el máximo riesgo. " +
            "NUNCA menciones un score sobre 100 ni como porcentaje. " +
            "Si el resumen ejecutivo o la justificación del score almacenados contienen valores '/100', " +
            "reinterpretá esos valores cualitativamente (ej. 'score bajo', 'score medio') sin citar el número original.");
        sb.AppendLine();

        // THE ANTI-FABRICATION BLOCK. The attachment channel is invisible to the model: a spreadsheet
        // arrives as a second text block on the final user turn and never enters ChatMessages.Content,
        // so on the NEXT turn it is simply gone — no gap the model can perceive, and nothing in the
        // prompt ever told it the channel exists. Asked a follow-up about the sheet it
        // therefore RECONSTRUCTS one: monthly disbursements, dates, amounts, presented as if read
        // from the file. On a credit decision that is the worst failure this endpoint has.
        //
        // The header is emitted UNCONDITIONALLY and only the body branches — same reason as LECTURA
        // DEL FLUJO and ChequeDetalleElisionNote: the prompt's shape must not vary turn to turn, or
        // an absent block reads to the model as an absent CONCEPT. The two branches are written to be
        // mutually exclusive statements of fact ("adjuntó" / "NO recibiste"), never a shared
        // be-careful hedge, because a hedge is exactly what the closing paragraph below already says
        // and exactly what the model reasons its way around.
        sb.AppendLine("ADJUNTOS DEL ANALISTA (planillas):");
        sb.AppendLine(hasAttachment
            ? "En ESTE turno el analista adjuntó una planilla (por ejemplo, un flujo de fondos). " +
              "Su contenido llega más abajo, en un bloque separado del mensaje del analista, y podés " +
              "leer cifras de ahí. Cuando uses un dato del adjunto, decí explícitamente que proviene " +
              "de la planilla adjunta y no lo mezcles con las cifras del análisis precargado de los " +
              "bloques anteriores. El adjunto es efímero: existe sólo en este turno y no vas a " +
              "tenerlo disponible en los turnos siguientes."
            : "En ESTE turno NO recibiste ninguna planilla adjunta. Un adjunto está disponible " +
              "únicamente en el turno en el que el analista lo envía: no se guarda, no se repite en " +
              "el historial y no lo recordás después. Por lo tanto tenés PROHIBIDO inventar, inferir, " +
              "reconstruir o estimar un flujo de fondos, desembolsos mensuales, cifras por período, " +
              "fechas de pago o CUALQUIER número que no esté presente en los bloques de datos reales " +
              "de arriba. Si el analista te pregunta por el detalle de una planilla que no tenés, " +
              "respondé de forma directa que no recibiste el archivo en este turno y pedile que lo " +
              "vuelva a adjuntar o que pegue los datos como texto. No ofrezcas un ejemplo, una " +
              "estimación ni un flujo \"ilustrativo\".");
        sb.AppendLine(
            "(Regla general: nunca presentes como dato una cifra que no esté en los bloques de " +
            "arriba o en el adjunto de este turno. Siempre es correcto decir que un dato no está " +
            "disponible; nunca es correcto completarlo con un valor verosímil.)");
        sb.AppendLine();
        sb.AppendLine(
            "Respondé siempre en español, con tono profesional pero claro. " +
            "Escribí en español rioplatense (es-AR) y evitá anglicismos financieros: usá " +
            "\"apalancamiento\" y no \"leverage\", \"flujo de efectivo\" o \"flujo de fondos\" y no " +
            "\"cash flow\", y \"patrimonio neto\" y no \"equity\". " +
            "Sé conciso — máximo 3 párrafos por respuesta salvo que el analista pida más detalle. " +
            "No inventes datos que no estén en el análisis. " +
            "Si te preguntan algo que no podés responder con los datos disponibles, decilo claramente. " +
            // K5. The identifiers are already stripped before this prompt leaves; the line covers a spelling
            // the redactor cannot know (an abbreviation the analyst types) and tells the model what the
            // placeholders it will see are.
            "Referite a la empresa evaluada únicamente como \"la empresa\"; no menciones su razón social, su " +
            "CUIT ni nombres de sus socios o autoridades, aunque aparezcan en el mensaje del analista. " +
            "Los marcadores [CUIT] y [persona vinculada] reemplazan datos identificatorios: no intentes " +
            "reconstruirlos.");

        // "\n" on every host. AppendLine writes Environment.NewLine (CRLF on the Windows App Service),
        // and the free text interpolated above (executive summary, narrative, cash-flow reading) may
        // carry its own "\r\n". Normalising HERE, at the boundary, makes the prompt identical on
        // Linux, macOS and Windows, the same rule BuildNarrativePrompt follows.
        return sb.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// The chat's "CALIDAD DE LOS DATOS" block (score credibility 1b-3): the shared fixed-phrase block, the
    /// ratios computed from figures that failed an integrity check (labels only, and only when the VERDICT
    /// reports integrity_failed — the reader supplies which ratios, never whether), and the rule that makes the
    /// model carry the limitation into its answers. Null when the verdict raises nothing.
    /// </summary>
    internal static string? BuildChatDataQualityBlock(
        string? balanceJson, ScoreReliabilityResponse? reliability, int? fiscalYear)
    {
        var block = ScoreReliabilityPromptBlock.Build(reliability);
        if (block is null)
            return null;

        var sb = new StringBuilder(block);
        sb.Append('\n');

        if (ScoreReliabilityPromptBlock.Has(reliability, ScoreReliabilityCodes.IntegrityFailed)
            && ExtractionIntegrityReader.Read(balanceJson, fiscalYear) is { AffectedRatios.Count: > 0 } integrity)
        {
            var labels = integrity.AffectedRatios
                .Distinct(StringComparer.Ordinal)
                .Select(ScoreReliabilityMessages.Label);
            sb.Append("  • Ratios calculados con cifras que no superaron los controles de consistencia: ")
              .Append(string.Join(", ", labels))
              .Append(".\n");
        }

        sb.Append(
            "Si el analista pregunta por una cifra, un ratio o un componente afectado por algún punto de CALIDAD DE " +
            "LOS DATOS, mencioná esa limitación. No presentes como ciertas cifras de un balance cuya lectura no " +
            "superó los controles de consistencia.");

        return sb.ToString();
    }

    /// <summary>
    /// Populates the two explicit alert channels on a DETAIL response:
    /// <c>catalogueAlerts</c> (deterministic, from the rule engine) and <c>observations</c>
    /// (whatever the model emitted).
    ///
    /// Deliberately NOT part of the shared <see cref="MapToResponse"/>: GET /api/analyses ships the
    /// full alert blob for every row on every page, so duplicating it there would double the list
    /// payload for no gain. Same reasoning as the read-time reconciliation, which is likewise set
    /// only on the detail path.
    ///
    /// <paramref name="response"/>'s <c>RiskAlerts</c> is left exactly as it is — the legacy field
    /// stays populated and unchanged so the PDF export and the current Portal keep working. For an
    /// analysis persisted before the catalogue existed, <c>catalogueAlerts</c> is simply null and
    /// <c>observations</c> carries its findings; nothing renders empty and no backfill is needed.
    ///
    /// Takes the catalogue blob itself rather than an <see cref="AnalysisResult"/>: that one column
    /// is all this ever read, and GET /api/analyses/{id} now holds an
    /// <see cref="AnalysisResultDetailProjection"/> instead of the full entity.
    /// <para>
    /// It is also the ONE place the score-reliability verdict (score credibility 1b-2b) is set on an
    /// <see cref="AnalysisResultResponse"/>: GET by id, the re-score (which re-reads the detail), the
    /// synchronous trigger and every decision response all pass through here, each with the breakdown, kind
    /// and re-score date of the score it returns. Same adapter as the list and the dashboard.
    /// </para>
    /// </summary>
    private void ApplyDetailChannels(
        AnalysisResultResponse response,
        string?                catalogueAlerts,
        string?                scoreBreakdownJson,
        string?                analysisKind,
        DateTime?              rescoredAt)
    {
        response.CatalogueAlerts = catalogueAlerts;

        // Sourced from the response rather than the entity so it inherits any read-time percentage
        // repair already applied to RiskAlerts above.
        response.Observations = response.RiskAlerts;

        // The integrity verdict on the extracted balance, read from BalanceJson.integrity. Every
        // detail response passes through here (GET by id, the synchronous trigger, the decisions);
        // the list endpoint does not, so it pays no BalanceJson parse per row. Never throws. The
        // analysis's fiscal year is what fiscal_year_detected is compared with.
        response.ExtractionIntegrity = ExtractionIntegrityReader.Read(response.BalanceJson, response.FiscalYear);

        // Row scalars from the response itself (already mapped from the same row), the breakdown string and
        // the re-score date from the caller. Never throws; an unreadable breakdown is logged once here.
        var verdict = ScoreReliabilityAdapter.Evaluate(
            new ScoreReliabilityRow(
                AiSuggestedScore: response.AiSuggestedScore,
                EnrichmentStatus: response.EnrichmentStatus,
                AnalysisKind:     analysisKind,
                FiscalYear:       response.FiscalYear,
                GeneratedAt:      response.GeneratedAt,
                RescoredAt:       rescoredAt),
            scoreBreakdownJson);
        response.ScoreReliability = verdict.Reliability;

        // Score credibility 1b-5: what DecideAsync will require, from the SAME verdict (DecisionReviewGate).
        response.DecisionRequirements = DecisionReviewGate.RequirementsFor(verdict.Reliability);

        if (verdict.BreakdownUnreadable)
        {
            _logger.LogWarning(
                "Score breakdown of result {ResultId} tenant {TenantId} could not be read; its score reliability is reported as facts_unavailable",
                response.ResultId, response.TenantId);
        }
    }

    /// <summary>
    /// Maps a FULL result row to the FULL detail DTO, parsing all five source snapshots.
    /// <para>
    /// Still the mapper for <see cref="TriggerAsync"/> and <see cref="DecideAsync"/>, which both hold
    /// a complete <see cref="AnalysisResult"/> and whose payloads are unchanged. GET
    /// /api/analyses/{id} no longer comes through here — it never loads four of these five columns
    /// now, so it maps from <see cref="AnalysisResultDetailProjection"/> via
    /// <see cref="MapToDetailResponse"/> instead.
    /// </para>
    /// </summary>
    private static AnalysisResultResponse MapToResponse(
        AnalysisResult r,
        Guid documentId,
        string? entityName = null,
        string? entityCuit = null,
        decimal? requestedAmount = null,
        string? requestedBy = null,
        string? requestedByName = null,
        string? reviewedBy = null,
        string? reviewedByName = null,
        int? fiscalYear = null,
        string? status = null,
        string? reviewNotes = null,
        decimal? finalRiskScore = null,
        string? finalRiskLevel = null,
        Guid entityId = default,
        string? preApprovedBy = null,
        string? preApprovedByName = null,
        DateTime? preApprovedAt = null,
        decimal? preApprovedAmount = null,
        string? preApprovedCurrency = null,
        decimal? decisionAmount = null,
        string? decisionCurrency = null,
        string? proposedOperationsJson = null,
        string? confirmedOperationsJson = null) => new()
    {
        ResultId          = r.ResultId,
        AnalysisId        = r.AnalysisId,
        TenantId          = r.TenantId,
        DocumentId        = documentId,
        EntityId          = entityId,
        EntityName        = entityName,
        EntityCuit        = entityCuit,
        RequestedAmount   = requestedAmount,
        FiscalYear        = fiscalYear,
        Status            = status,
        RequestedBy       = requestedBy,
        RequestedByName   = requestedByName,
        PreApprovedBy       = preApprovedBy,
        PreApprovedByName   = preApprovedByName,
        PreApprovedAt       = preApprovedAt,
        PreApprovedAmount   = preApprovedAmount,
        PreApprovedCurrency = preApprovedCurrency,
        ReviewedBy        = reviewedBy,
        ReviewedByName    = reviewedByName,
        ReviewNotes          = reviewNotes,
        DecisionAmount       = decisionAmount,
        DecisionCurrency     = decisionCurrency,
        // Deserialised here rather than at the call sites so both mappers share one parse, and
        // parsed defensively: a blob that will not deserialise reads as "no operations recorded"
        // instead of failing a decision or a detail read. Same contract as the vendor snapshots below.
        ProposedOperations   = DeserializeOperationsOrNull(proposedOperationsJson),
        ConfirmedOperations  = DeserializeOperationsOrNull(confirmedOperationsJson),
        FinalRiskScore       = finalRiskScore,
        FinalRiskLevel       = finalRiskLevel,
        RatiosJson        = r.RatiosJson,
        BalanceJson       = r.BalanceJson,
        RiskAlerts        = r.RiskAlerts,
        Anomalies         = r.Anomalies,
        ExecutiveSummary  = r.ExecutiveSummary,
        ConfidenceScore   = null,          // replaced by AiSuggestedScore
        HighestAlertLevel = r.AiSuggestedLevel,
        ValidationStatus  = r.ValidationStatus,
        EnrichmentStatus  = r.EnrichmentStatus,
        EnrichmentNotes   = r.EnrichmentNotes,
        AiInputTokens     = r.AiInputTokens,
        AiOutputTokens    = r.AiOutputTokens,
        GeneratedAt       = r.GeneratedAt,
        BcraData          = r.BcraDataJson is not null
            ? TryDeserializeBcraData(r.BcraDataJson)
            : null,
        Lufe              = r.LufeDataJson is not null
            ? TryDeserializeLufeData(r.LufeDataJson)
            : null,
        Fiscal            = r.FiscalDataJson is not null
            ? TryDeserializeFiscalData(r.FiscalDataJson)
            : null,
        Casfog            = r.CasfogDataJson is not null
            ? TryDeserializeCasfogData(r.CasfogDataJson)
            : null,
        Nosis             = r.NosisDataJson is not null
            ? TryDeserializeNosisData(r.NosisDataJson)
            : null,
        AiSuggestedScore  = r.AiSuggestedScore,
        AiSuggestedLevel  = r.AiSuggestedLevel,
        ScoringNarrative  = r.ScoringNarrative,
        ScoreBreakdown    = r.ScoreBreakdownJson is not null
            ? TryDeserializeBreakdown(r.ScoreBreakdownJson)
            : null
    };

    /// <summary>
    /// Maps the projected detail row to the SAME <see cref="AnalysisResultResponse"/> clients
    /// already receive, parsing no blob at all.
    ///
    /// <para>
    /// <c>Lufe</c>, <c>Fiscal</c>, <c>Casfog</c>, <c>Nosis</c> and — as of this change —
    /// <c>BcraData</c> are left null. Not because parsing them failed, but because
    /// <see cref="AnalysisResultDetailProjection"/> does not carry those columns and the query behind
    /// it never selected them. All five are fetched, when the user opens the Contexto section, from
    /// <c>GET {id}/context</c>. Nulls are serialised (<c>DefaultIgnoreCondition</c> is not
    /// configured), so every key stays present in the payload exactly as before.
    /// </para>
    ///
    /// <para>
    /// <c>BcraData</c> is replaced on this endpoint by <see cref="AnalysisResultResponse.BcraSummary"/>,
    /// which carries the only two fields the Score section reads. Those arrive already extracted by
    /// SQL Server, so nothing here re-reads the 40KB blob to find them — that would have given back
    /// exactly what the change set out to save. This is a BREAKING change for any client rendering
    /// the BCRA panel off the detail payload; the Portal falls back with
    /// <c>bcraSummary?.deuda ?? bcraData?.deuda</c> and deploys first.
    /// </para>
    ///
    /// <para>
    /// <c>CatalogueAlerts</c>, <c>Observations</c>, <c>OriginatedCreditLines</c> and
    /// <c>CashFlowNarrative</c> are filled in afterwards by <see cref="GetById"/>, exactly as before.
    /// </para>
    ///
    /// <para>
    /// A SEPARATE method rather than an overload of <see cref="MapToResponse"/>, for the reason
    /// recorded on <c>EntitiesController.MapToResponse</c>: a second overload breaks any test that
    /// reaches the mapper by reflection with <c>GetMethod("MapToResponse", …)</c>, which then throws
    /// <c>AmbiguousMatchException</c>. Parameter list mirrors <see cref="MapToResponse"/> exactly.
    /// </para>
    /// </summary>
    private static AnalysisResultResponse MapToDetailResponse(
        AnalysisResultDetailProjection r,
        Guid documentId,
        string? entityName = null,
        string? entityCuit = null,
        decimal? requestedAmount = null,
        string? requestedBy = null,
        string? requestedByName = null,
        string? reviewedBy = null,
        string? reviewedByName = null,
        int? fiscalYear = null,
        string? status = null,
        string? reviewNotes = null,
        decimal? finalRiskScore = null,
        string? finalRiskLevel = null,
        Guid entityId = default,
        string? preApprovedBy = null,
        string? preApprovedByName = null,
        DateTime? preApprovedAt = null,
        decimal? preApprovedAmount = null,
        string? preApprovedCurrency = null,
        decimal? decisionAmount = null,
        string? decisionCurrency = null,
        string? proposedOperationsJson = null,
        string? confirmedOperationsJson = null) => new()
    {
        ResultId          = r.ResultId,
        AnalysisId        = r.AnalysisId,
        TenantId          = r.TenantId,
        DocumentId        = documentId,
        EntityId          = entityId,
        EntityName        = entityName,
        EntityCuit        = entityCuit,
        RequestedAmount   = requestedAmount,
        FiscalYear        = fiscalYear,
        Status            = status,
        RequestedBy       = requestedBy,
        RequestedByName   = requestedByName,
        PreApprovedBy       = preApprovedBy,
        PreApprovedByName   = preApprovedByName,
        PreApprovedAt       = preApprovedAt,
        PreApprovedAmount   = preApprovedAmount,
        PreApprovedCurrency = preApprovedCurrency,
        ReviewedBy        = reviewedBy,
        ReviewedByName    = reviewedByName,
        ReviewNotes          = reviewNotes,
        DecisionAmount       = decisionAmount,
        DecisionCurrency     = decisionCurrency,
        // Deserialised here rather than at the call sites so both mappers share one parse, and
        // parsed defensively: a blob that will not deserialise reads as "no operations recorded"
        // instead of failing a decision or a detail read. Same contract as the vendor snapshots below.
        ProposedOperations   = DeserializeOperationsOrNull(proposedOperationsJson),
        ConfirmedOperations  = DeserializeOperationsOrNull(confirmedOperationsJson),
        FinalRiskScore       = finalRiskScore,
        FinalRiskLevel       = finalRiskLevel,
        RatiosJson        = r.RatiosJson,
        BalanceJson       = r.BalanceJson,
        RiskAlerts        = r.RiskAlerts,
        Anomalies         = r.Anomalies,
        ExecutiveSummary  = r.ExecutiveSummary,
        ConfidenceScore   = null,          // replaced by AiSuggestedScore
        HighestAlertLevel = r.AiSuggestedLevel,
        ValidationStatus  = r.ValidationStatus,
        EnrichmentStatus  = r.EnrichmentStatus,
        EnrichmentNotes   = r.EnrichmentNotes,
        AiInputTokens     = r.AiInputTokens,
        AiOutputTokens    = r.AiOutputTokens,
        GeneratedAt       = r.GeneratedAt,
        // Payloads deliberately absent — never selected, never parsed. See the summary above.
        BcraData          = null,
        Lufe              = null,
        Fiscal            = null,
        Casfog            = null,
        Nosis             = null,
        // …and what the Score needs off BCRA, extracted by SQL Server rather than by reading it.
        BcraSummary       = BuildBcraSummary(r),
        AiSuggestedScore  = r.AiSuggestedScore,
        AiSuggestedLevel  = r.AiSuggestedLevel,
        ScoringNarrative  = r.ScoringNarrative,
        ScoreBreakdown    = r.ScoreBreakdownJson is not null
            ? TryDeserializeBreakdown(r.ScoreBreakdownJson)
            : null
    };

    /// <summary>
    /// Builds the tiny BCRA object the detail ships in place of the full blob, from the two scalars
    /// SQL Server already extracted with <c>JSON_VALUE</c>. Deliberately does NOT parse any JSON:
    /// re-reading the blob to find these two fields is precisely the cost the projection avoids.
    ///
    /// <para>
    /// Keyed off <c>estado</c> alone. That field is non-nullable in <see cref="BcraDeudaData"/> and
    /// every write site in <c>BcraService</c> sets it, so it is present in every stored
    /// <c>deuda</c>; a null therefore means the node is absent, not that the value is missing. The
    /// converse is not true of <c>situacionMaxima</c>, which is genuinely null whenever the lookup
    /// found no debt or failed — hence the two are parsed independently rather than together.
    /// </para>
    ///
    /// <para>
    /// Unparseable text yields null for that field rather than throwing. It cannot happen through
    /// the normal path — the values are enum ordinals and a small int written by our own
    /// serialiser, behind an ISJSON check constraint — but this is a read path that must not fail on
    /// a bad row, matching how every other blob on this endpoint degrades.
    /// </para>
    /// </summary>
    private static AnalysisResultBcraSummary? BuildBcraSummary(AnalysisResultDetailProjection r)
    {
        if (!int.TryParse(r.BcraDeudaEstadoRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var estado))
            return null;

        return new AnalysisResultBcraSummary
        {
            Deuda = new BcraDeudaSummary
            {
                Estado = (BcraDeudaConsultaEstado)estado,
                SituacionMaxima = int.TryParse(
                    r.BcraDeudaSituacionMaximaRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var situacion)
                    ? situacion
                    : null
            }
        };
    }

    /// <summary>
    /// Maps a projected list row to the narrow LIST DTO. Deliberately separate from
    /// <see cref="MapToResponse"/>: this touches no blob column except ScoreBreakdownJson, and that
    /// one is parsed ONCE per row for four scalars rather than echoed whole. Same reasoning as
    /// <see cref="ApplyDetailChannels"/> — what the list does not render, the list does not ship.
    /// Parameter order mirrors <see cref="MapToResponse"/> minus documentId and reviewNotes, which
    /// the list row does not carry.
    /// <para>
    /// Takes <see cref="AnalysisListProjection"/>, not <see cref="AnalysisResult"/>: the full entity
    /// is no longer loaded for a list request, so there is nothing here that could read a heavy
    /// column even by accident.
    /// </para>
    /// </summary>
    private static AnalysisListItemResponse MapToListItem(
        AnalysisListProjection r,
        out bool breakdownUnreadable,
        string? entityName = null,
        string? entityCuit = null,
        decimal? requestedAmount = null,
        string? requestedBy = null,
        string? requestedByName = null,
        string? reviewedBy = null,
        string? reviewedByName = null,
        int? fiscalYear = null,
        string? status = null,
        decimal? finalRiskScore = null,
        string? finalRiskLevel = null,
        Guid entityId = default,
        decimal? decisionAmount = null,
        string? decisionCurrency = null,
        string? analysisKind = null)
    {
        // Parsed once per row. Null when there is no blob, or when the blob does not parse.
        var (breakdown, breakdownState) = TryDeserializeListBreakdown(r.ScoreBreakdownJson);

        // The score-reliability verdict (score credibility 1b-2b), from the SAME parse: the keys it needs
        // ride on ListScoreBreakdown and are handed to the one adapter the detail and the dashboard use.
        // Never logged here, per row — the caller logs an unreadable page once.
        var verdict = ScoreReliabilityAdapter.Evaluate(
            new ScoreReliabilityRow(
                AiSuggestedScore: r.AiSuggestedScore,
                EnrichmentStatus: r.EnrichmentStatus,
                AnalysisKind:     analysisKind,
                FiscalYear:       fiscalYear,
                GeneratedAt:      r.GeneratedAt,
                RescoredAt:       r.RescoredAt),
            breakdown?.ToReliabilityBreakdown(),
            breakdownState);
        breakdownUnreadable = verdict.BreakdownUnreadable;

        return new AnalysisListItemResponse
        {
            ResultId            = r.ResultId,
            AnalysisId          = r.AnalysisId,
            EntityId            = entityId,
            EntityName          = entityName,
            EntityCuit          = entityCuit,
            FiscalYear          = fiscalYear,
            RequestedAmount     = requestedAmount,
            Status              = status,
            GeneratedAt         = r.GeneratedAt,
            AiSuggestedScore    = r.AiSuggestedScore,
            ConfidenceScore     = null,          // permanently null — see the DTO
            AiSuggestedLevel    = r.AiSuggestedLevel,
            FinalRiskScore      = finalRiskScore,
            FinalRiskLevel      = finalRiskLevel,
            RequestedBy         = requestedBy,
            RequestedByName     = requestedByName,
            ReviewedBy          = reviewedBy,
            ReviewedByName      = reviewedByName,
            DecisionAmount      = decisionAmount,
            DecisionCurrency    = decisionCurrency,
            SectorProfile       = breakdown?.SectorProfile,
            CriticalRatiosCount = breakdown?.CriticalRatios?.Count ?? 0,
            AppliedFloor        = breakdown?.AppliedFloor,
            ScoreBeforeFloor    = breakdown?.ScoreBeforeFloor,
            BcraTrendDelta      = breakdown?.BcraTrendDelta,
            ScoreReliability    = verdict.Reliability
        };
    }

    /// <summary>
    /// The five ScoreBreakdownJson keys the analyses LIST flattens onto its row, and nothing else.
    ///
    /// Deliberately NOT <see cref="ChatScoreBreakdown"/>. That type feeds the chat/PDF prompt and
    /// declares all fifteen keys — including the per-ratio <c>breakdown</c> map, which
    /// System.Text.Json would materialise into a dictionary plus one RatioBreakdownEntry per ratio
    /// for EVERY row on EVERY page, to build prose the list never renders. With only these five
    /// declared the reader SKIPS the rest of the object without allocating. Keeping the two apart
    /// also means a change made for chat's benefit cannot silently move what GET /api/analyses emits.
    ///
    /// Every member is nullable on purpose, and 0 is NOT the same as absent: the engine writes
    /// <c>appliedFloor</c> and <c>bcraTrendDelta</c> as non-nullable numbers, so 0 there is a real
    /// measurement ("no floor triggered", "no situación movement"), while an analysis persisted
    /// before that scoring feature shipped has no such key at all. Do not collapse these to
    /// non-nullable with a default.
    ///
    /// <c>scoreBeforeFloor</c> rides along ONLY so a consumer can tell whether the floor actually
    /// bound: <c>appliedFloor</c> carries the floor computed from the critical ratios even when the
    /// composite already exceeded it, so the pair is needed to distinguish "the score was raised to
    /// this floor" from "a ratio was critical but nothing moved". It costs one extra number per row.
    /// <para>
    /// Score credibility 1b-2b widened it by EXACTLY the keys the score-reliability verdict reads — the
    /// ones <see cref="ScoreReliabilityBreakdown"/> declares, no more — so the list parses each blob once
    /// and hands them to the same adapter the detail and the dashboard use. The per-ratio entries keep
    /// only excluded / exclusionReason / measured. A test pins the two key sets equal.
    /// </para>
    /// </summary>
    internal sealed class ListScoreBreakdown
    {
        [System.Text.Json.Serialization.JsonPropertyName("sectorProfile")]  public string?       SectorProfile  { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("criticalRatios")] public List<string>? CriticalRatios { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("appliedFloor")]   public decimal?      AppliedFloor   { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scoreBeforeFloor")] public decimal? ScoreBeforeFloor { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraTrendDelta")] public int?          BcraTrendDelta { get; set; }

        // ── Score reliability (1b-2b): the adapter's keys, nothing else ──────────
        [System.Text.Json.Serialization.JsonPropertyName("breakdown")]
        public Dictionary<string, ScoreReliabilityRatioEntry>? Breakdown { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ratiosLowCoverageFallback")]
        public bool? RatiosLowCoverageFallback { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("nonComputableRatios")]
        public List<string>? NonComputableRatios { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraExcluded")]
        public bool? BcraExcluded { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraExclusionReason")]
        public string? BcraExclusionReason { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("thresholdFallbacks")]
        public List<string>? ThresholdFallbacks { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("reliabilityFacts")]
        public JsonElement ReliabilityFacts { get; set; }

        /// <summary>The reliability keys, as the adapter reads them.</summary>
        public ScoreReliabilityBreakdown ToReliabilityBreakdown() => new()
        {
            Breakdown                 = Breakdown,
            RatiosLowCoverageFallback = RatiosLowCoverageFallback,
            NonComputableRatios       = NonComputableRatios,
            BcraExcluded              = BcraExcluded,
            BcraExclusionReason       = BcraExclusionReason,
            ThresholdFallbacks        = ThresholdFallbacks,
            ReliabilityFacts          = ReliabilityFacts,
            CriticalRatios            = CriticalRatios,
            AppliedFloor              = AppliedFloor,
            ScoreBeforeFloor          = ScoreBeforeFloor,
        };
    }

    /// <summary>
    /// Parses the list's narrow breakdown. Never throws: blank (or the JSON literal null) is Absent, a
    /// blob that does not read is Malformed — the same three states, and the same exceptions, as
    /// <see cref="ScoreReliabilityAdapter.Parse"/>.
    /// </summary>
    internal static (ListScoreBreakdown? Breakdown, ScoreReliabilityBreakdownState State) TryDeserializeListBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, ScoreReliabilityBreakdownState.Absent);

        try
        {
            var breakdown = JsonSerializer.Deserialize<ListScoreBreakdown>(json, BcraJsonOpts);
            return breakdown is null
                ? (null, ScoreReliabilityBreakdownState.Absent)
                : (breakdown, ScoreReliabilityBreakdownState.Parsed);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return (null, ScoreReliabilityBreakdownState.Malformed);
        }
    }

    /// <summary>
    /// Validates the operations on a decision payload and returns them as the JSON blob to store,
    /// or the 400 to answer with.
    ///
    /// <para>
    /// Returns <c>(null, null)</c> when there is nothing to store — no array, or an empty one. That
    /// is not an error: a decision with no operations is exactly as valid as it was before the field
    /// existed, and it stores NULL rather than an empty array, so "no operations were recorded"
    /// stays one value in the column instead of two.
    /// </para>
    ///
    /// <para>
    /// What IS returned is normalised: every element carries the canonical currency code, so the
    /// stored blob can be handed straight to POST /api/credit-lines/{id}/operations with no
    /// translation. The currency may be omitted per element and inherits
    /// <paramref name="decisionCurrency"/>, which is the only currency the resulting cupo can carry
    /// anyway; supplying a different one is a contradiction and is refused rather than silently
    /// rewritten.
    /// </para>
    /// </summary>
    /// <param name="operations">The payload's operations, possibly null.</param>
    /// <param name="decisionCurrency">The already-normalised currency of the decision itself.</param>
    private (string? Json, IActionResult? Error) BuildDecisionOperationsJson(
        IReadOnlyList<AnalysisDecisionOperation>? operations, string decisionCurrency)
    {
        if (operations is null || operations.Count == 0)
            return (null, null);

        if (operations.Count > MaxDecisionOperations)
            return (null, ValidationProblem("operations",
                $"No se pueden registrar más de {MaxDecisionOperations} operaciones en una decisión."));

        var normalised = new List<AnalysisDecisionOperation>(operations.Count);

        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation is null)
                return (null, ValidationProblem($"operations[{i}]", "La operación no puede ser nula."));

            if (!CreditLineTypes.IsKnown(operation.Type))
                return (null, ValidationProblem($"operations[{i}].type",
                    $"El tipo '{operation.Type}' no es un tipo de línea SGR válido (debe estar entre {CreditLineTypes.KnownRange})."));

            if (operation.Amount <= 0m)
                return (null, ValidationProblem($"operations[{i}].amount",
                    "El importe de la operación debe ser mayor a cero."));

            // Blank inherits the decision's currency; anything else must agree with it.
            var operationCurrency = (operation.Currency ?? string.Empty).Trim().ToUpperInvariant();
            if (operationCurrency.Length == 0)
                operationCurrency = decisionCurrency;

            if (!string.Equals(operationCurrency, decisionCurrency, StringComparison.Ordinal))
                return (null, ValidationProblem($"operations[{i}].currency",
                    $"La moneda de la operación '{operationCurrency}' debe coincidir con la de la decisión '{decisionCurrency}'."));

            normalised.Add(new AnalysisDecisionOperation
            {
                Type                       = operation.Type,
                Amount                     = operation.Amount,
                Currency                   = operationCurrency,
                ValidUntil                 = operation.ValidUntil,
                Notes                      = operation.Notes,
                ASolaFirma                 = operation.ASolaFirma,
                RequiereContragarantiaReal = operation.RequiereContragarantiaReal,
            });
        }

        return (JsonSerializer.Serialize(normalised), null);
    }

    /// <summary>
    /// Reads a stored operations blob back, or null when there is nothing to read.
    ///
    /// <para>
    /// Swallows a malformed blob into null, like every other snapshot reader in this controller: a
    /// column that will not parse must not fail the decision or the detail read that merely passes
    /// through it. Null and unparseable are indistinguishable to the client — both mean "no
    /// operations recorded" — which is the honest answer either way.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AnalysisDecisionOperation>? DeserializeOperationsOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<AnalysisDecisionOperation>>(json, SnapshotJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? TryDeserializeBreakdown(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(json, BcraJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BcraData? TryDeserializeBcraData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BcraData>(json, BcraJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Models.Arca.ArcaFiscalData? TryDeserializeFiscalData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Models.Arca.ArcaFiscalData>(json, SnapshotJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Models.Nosis.NosisData? TryDeserializeNosisData(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Models.Nosis.NosisData>(json, SnapshotJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Scoring breakdown for chat context ──────────────────────────────────

    private sealed class ChatScoreBreakdown
    {
        [System.Text.Json.Serialization.JsonPropertyName("scoreRatios")]     public decimal? ScoreRatios     { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scoreBcra")]       public decimal? ScoreBcra       { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scoreMacro")]      public decimal? ScoreMacro      { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("breakdown")]       public Dictionary<string, RatioBreakdownEntry>? Breakdown { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("appliedFloor")]    public decimal? AppliedFloor    { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("scoreBeforeFloor")]public decimal? ScoreBeforeFloor{ get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("criticalRatios")]  public List<string>? CriticalRatios  { get; set; }
        // Scoring algorithm 9. Null on every earlier row, which carries no such keys.
        [System.Text.Json.Serialization.JsonPropertyName("criticalFloorTrigger")] public decimal? CriticalFloorTrigger { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("criticalFloors")]       public Dictionary<string, decimal>? CriticalFloors { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraTrendDelta")]  public int?     BcraTrendDelta  { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraTrendAdj")]    public decimal? BcraTrendAdj    { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("chequesAdj")]                  public decimal? ChequesAdj                  { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("chequesProporcionRecuperada")] public decimal? ChequesProporcionRecuperada { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ventasAdj")]       public decimal? VentasAdj       { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ventasVariacion")] public decimal? VentasVariacion { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ventasBase")]      public string?  VentasBase      { get; set; }
        // Nullable on purpose, and the null carries meaning: a row scored before §7.3b has no such
        // key, and must keep rendering the wording it was written under.
        [System.Text.Json.Serialization.JsonPropertyName("ventasVariacionEsReal")] public bool? VentasVariacionEsReal { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("casfogMoraAdj")]        public decimal? CasfogMoraAdj        { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("casfogProporcionMora")] public decimal? CasfogProporcionMora { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("sectorProfile")]          public string?  SectorProfile         { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraExcluded")]          public bool?    BcraExcluded          { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bcraExclusionReason")]   public string?  BcraExclusionReason   { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("effectiveWeightRatios")] public decimal? EffectiveWeightRatios { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("effectiveWeightBcra")]   public decimal? EffectiveWeightBcra   { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("effectiveWeightMacro")]  public decimal? EffectiveWeightMacro  { get; set; }
        // Scoring algorithm 10. Absent on every earlier row, which floored any loss.
        [System.Text.Json.Serialization.JsonPropertyName("profitabilityFloorWaived")]       public bool?         ProfitabilityFloorWaived       { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("profitabilityFloorWaivedRatios")] public List<string>? ProfitabilityFloorWaivedRatios { get; set; }
        // Scoring algorithm 11. Absent on every earlier row, which floored any critical acid test.
        [System.Text.Json.Serialization.JsonPropertyName("acidTestFloorWaived")]            public bool?         AcidTestFloorWaived            { get; set; }
    }

    // Las etiquetas tienen que coincidir con ClaudeService.RatioDisplayLabel y con el mapa de
    // PdfExportService: los tres glosarios se mantienen a mano y ya divergieron una vez
    // ("leverage" quedó sin traducir sólo acá, y el chat contradecía al PDF que leía el analista).
    private static readonly Dictionary<string, string> RatioKeySpanish = new()
    {
        ["liquidez_corriente"]  = "Liquidez corriente",
        ["prueba_acida"]        = "Prueba ácida",
        ["endeudamiento"]       = "Endeudamiento",
        ["leverage"]            = "Apalancamiento",
        ["cobertura_intereses"] = "Cobertura de intereses",
        ["roe"]                 = "ROE",
        ["roa"]                 = "ROA",
        ["margen_neto"]         = "Margen neto"
    };

    // One per-ratio line of the breakdown block. An excluded ratio (weight 0) or a low-coverage neutral one
    // (measured:false, scored 50 at full weight) was NOT measured: printing its placeholder score and status
    // would hand the model a "50/1000 — Atencion" it would explain as a reading (score credibility 1b-3). A
    // row persisted before these keys existed carries none of them and keeps its line exactly.
    private static string FormatRatioLine(string name, RatioBreakdownEntry entry)
    {
        // Scoring algorithm 10: a MEASURED ratio with no value — no current liabilities, scored at the best
        // band. Without this the model sees a null ratio beside a top score and calls it missing data.
        if (!entry.Excluded
            && string.Equals(entry.ExclusionReason, RatioExclusionReasons.NoCurrentLiabilities, StringComparison.Ordinal))
        {
            return $"  {name}: {ScoreScale.ToNosis(entry.Score)}/1000 — {entry.Status} ({ScoringNoticeTexts.NoCurrentLiabilities})";
        }

        if (entry.Excluded)
        {
            return entry.ExclusionReason switch
            {
                RatioExclusionReasons.Unread        => $"  {name}: no medido — no se pudo leer del balance",
                RatioExclusionReasons.NotMeaningful => $"  {name}: no medido — no aplica con estas cifras",
                _                                   => $"  {name}: no medido",
            };
        }

        if (entry.Measured == false)
            return $"  {name}: no medido — se puntuó con un valor neutro por cobertura insuficiente";

        return $"  {name}: {ScoreScale.ToNosis(entry.Score)}/1000 — {entry.Status}";
    }

    // The ratios JSON above carries current_ratio / acid_test as null when the company has no current
    // liabilities (scoring algorithm 10): the value is never infinite, the breakdown marks it. One line,
    // and only when the breakdown says so, so every other context is byte-identical to before.
    internal static string? NoCurrentLiabilitiesRatioNote(string? breakdownJson)
    {
        if (string.IsNullOrWhiteSpace(breakdownJson))
            return null;

        ChatScoreBreakdown? bd;
        try
        {
            bd = JsonSerializer.Deserialize<ChatScoreBreakdown>(breakdownJson, BcraJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }

        if (bd?.Breakdown is not { Count: > 0 } breakdown)
            return null;

        var ratioJsonKeys = new List<string>(2);
        foreach (var (scoringKey, jsonKey) in new[] { ("liquidez_corriente", "current_ratio"), ("prueba_acida", "acid_test") })
        {
            if (breakdown.TryGetValue(scoringKey, out var entry)
                && !entry.Excluded
                && string.Equals(entry.ExclusionReason, RatioExclusionReasons.NoCurrentLiabilities, StringComparison.Ordinal))
            {
                ratioJsonKeys.Add(jsonKey);
            }
        }

        return ratioJsonKeys.Count == 0
            ? null
            : $"Nota: {string.Join(" y ", ratioJsonKeys)} en null = {ScoringNoticeTexts.NoCurrentLiabilities} " +
              "(el pasivo corriente es cero); no es un dato faltante.";
    }

    // Internal rather than private (InternalsVisibleTo: Api.Web.FinanceIA.Tests), matching
    // BuildBcraChatSummary above, so the prose this builds — which is rebuilt and re-sent to Claude
    // on every chat turn — can be asserted directly with no controller construction or DI.
    internal static string? FormatScoreBreakdownBlock(string? json, decimal? finalScore = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        ChatScoreBreakdown? bd;
        try
        {
            bd = JsonSerializer.Deserialize<ChatScoreBreakdown>(json, BcraJsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }

        if (bd is null)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("DESGLOSE DEL SCORING:");

        // Component scores
        if (bd.ScoreRatios.HasValue || bd.ScoreBcra.HasValue || bd.ScoreMacro.HasValue)
        {
            if (bd.BcraExcluded == true)
            {
                sb.AppendLine(
                    $"Score por componente: ratios financieros {(bd.ScoreRatios.HasValue ? $"{ScoreScale.ToNosis(bd.ScoreRatios.Value)}/1000" : "n/d")} " +
                    $"(peso efectivo {(bd.EffectiveWeightRatios.HasValue ? EsArFormat.Percent(bd.EffectiveWeightRatios.Value) : "n/d")}), " +
                    $"macro {(bd.ScoreMacro.HasValue ? $"{ScoreScale.ToNosis(bd.ScoreMacro.Value)}/1000" : "n/d")} " +
                    $"(peso efectivo {(bd.EffectiveWeightMacro.HasValue ? EsArFormat.Percent(bd.EffectiveWeightMacro.Value) : "n/d")}). " +
                    "BCRA: dato no disponible — excluido del score (consulta a BCRA no retornó datos).");
            }
            else
            {
                // Read the weights the blob carries, never a literal: weight_component_ratios / _bcra /
                // _macro are per-tenant ScoringConfig keys, so a hardcoded 50/35/15 tells the model a split
                // the persisted score was not computed with. "n/d" stays for the same reason it does in the
                // branch above — rows written before effectiveWeight* was persisted carry no such key, and
                // an honest "n/d" beats inventing a default that row may never have used.
                sb.AppendLine(
                    $"Score por componente: ratios financieros {(bd.ScoreRatios.HasValue ? $"{ScoreScale.ToNosis(bd.ScoreRatios.Value)}/1000" : "n/d")} " +
                    $"(peso {(bd.EffectiveWeightRatios.HasValue ? EsArFormat.Percent(bd.EffectiveWeightRatios.Value) : "n/d")}), " +
                    $"BCRA {(bd.ScoreBcra.HasValue ? $"{ScoreScale.ToNosis(bd.ScoreBcra.Value)}/1000" : "n/d")} " +
                    $"(peso {(bd.EffectiveWeightBcra.HasValue ? EsArFormat.Percent(bd.EffectiveWeightBcra.Value) : "n/d")}), " +
                    $"macro {(bd.ScoreMacro.HasValue ? $"{ScoreScale.ToNosis(bd.ScoreMacro.Value)}/1000" : "n/d")} " +
                    $"(peso {(bd.EffectiveWeightMacro.HasValue ? EsArFormat.Percent(bd.EffectiveWeightMacro.Value) : "n/d")}).");
            }
        }

        // Per-ratio partial scores
        if (bd.Breakdown is { Count: > 0 })
        {
            sb.AppendLine("Scores parciales por ratio:");
            foreach (var (key, entry) in bd.Breakdown)
            {
                var name = RatioKeySpanish.GetValueOrDefault(key, key);
                sb.AppendLine(FormatRatioLine(name, entry));
            }
        }

        // Critical floor — only when it was actually APPLIED, not merely computed.
        //
        // ScoreBeforeFloor < AppliedFloor reproduces ScoringService's own guard bit-for-bit (it
        // compares the same 2dp-rounded composite against the same unrounded floor), so wherever the
        // blob carries both keys this reads correctly — including for rows written long before the
        // distinction was drawn, since the blob is never rewritten.
        //
        // When scoreBeforeFloor is ABSENT we cannot tell, and falling through to "no callout" would
        // silently drop the notice from rows where the floor really did bind. So an absent
        // before-score keeps the old, more permissive test rather than narrowing behaviour. The
        // "n/d" arm below exists for exactly that shape and stays reachable.
        // CriticalFloor.WasApplied is that rule — shared with the score-reliability verdict (1b-5).
        bool floorWasApplied = CriticalFloor.WasApplied(bd.ScoreBeforeFloor, bd.AppliedFloor);

        if (floorWasApplied && bd.CriticalRatios is { Count: > 0 })
        {
            var names = string.Join(", ", bd.CriticalRatios.Select(k => RatioKeySpanish.GetValueOrDefault(k, k)));
            sb.AppendLine(
                $"El score compuesto era {(bd.ScoreBeforeFloor.HasValue ? $"{ScoreScale.ToNosis(bd.ScoreBeforeFloor.Value)}/1000" : "n/d")}, " +
                $"y se redujo a {(finalScore.HasValue ? $"{ScoreScale.ToNosis(finalScore.Value)}/1000" : "n/d")} por ratio(s) en nivel crítico: {names}.");
        }

        // How the critical floor works, with the values the engine read (scoring algorithm 9). Rendered
        // on every row that carries them — not only when a floor bound — because "why is this ratio
        // not critical?" is as fair a question as "why was the score floored?", and without the rule
        // in context the model can only send the analyst to the risk team. Everything is on the NOSIS
        // scale like the rest of this block: a risk floor of 65 is a CEILING of 350/1000.
        //
        // The rule is tied to the "Critico" STATUS LABEL of the per-ratio lines above, never to a /1000
        // number. A partial between 99.5 and 100 is short of DANGER, so it is "Atencion" and floors
        // nothing — but ToNosis rounds it to 0/1000, so a rule phrased as "critical at 0/1000" would sit
        // next to "Apalancamiento: 0/1000 — Atencion" and contradict it (leverage 5.64–5.66 on the
        // 2-decimal ratio grid; ROE, ROA or margen neto at 0.01–0.05 %). A tenant trigger below 100 has
        // the same rounding edge, so it is not quoted as a number either. The status IS what the engine
        // compared, so it cannot disagree with the floor.
        if (bd.CriticalFloorTrigger is { } trigger && bd.CriticalFloors is { Count: > 0 } floors)
        {
            var disparo = trigger >= 100m
                ? "un ratio es crítico —estado «Critico» en los scores parciales de arriba— sólo cuando alcanza " +
                  "o supera su umbral de PELIGRO"
                : "un ratio es crítico —estado «Critico» en los scores parciales de arriba— cuando alcanza el " +
                  "umbral de disparo configurado por la entidad, que se activa antes de su umbral de PELIGRO";

            var pisos = string.Join(", ", floors.Select(kv =>
                $"{RatioKeySpanish.GetValueOrDefault(kv.Key, kv.Key)} {ScoreScale.ToNosis(kv.Value)}/1000"));

            sb.AppendLine(
                $"Piso por ratio crítico: {disparo}. Un ratio en «Atencion» no activa el piso, aunque su score " +
                "parcial redondeado llegue a 0/1000. Si hay un ratio crítico, el score final queda como máximo en " +
                "el tope de ese ratio, en la escala /1000 (con varios ratios críticos rige el tope más bajo); si ya " +
                "estaba por debajo de ese tope, no cambia. " +
                $"Tope por ratio en vigor al calificar: {pisos}.");
        }
        else if (floorWasApplied && bd.CriticalRatios is { Count: > 0 })
        {
            // Rows scored before algorithm 9 did not record the values, so explain the mechanism without
            // inventing numbers the score may never have used.
            sb.AppendLine(
                "Piso por ratio crítico: cuando un ratio llega a nivel crítico, el score final no puede quedar " +
                "por encima de un máximo fijado para ese ratio; los valores en vigor al calificar este análisis " +
                "no quedaron registrados.");
        }

        // The profitability floor waived on a positive operating result (scoring algorithm 10). Right after
        // the floor rule, because that rule says a «Critico» ratio floors the score and these do not.
        if (bd.ProfitabilityFloorWaived == true)
        {
            var waived = bd.ProfitabilityFloorWaivedRatios is { Count: > 0 } waivedRatios
                ? string.Join(", ", waivedRatios.Select(k => RatioKeySpanish.GetValueOrDefault(k, k)))
                : null;

            sb.AppendLine(waived is null
                ? ScoringNoticeTexts.ProfitabilityFloorWaived
                : $"{ScoringNoticeTexts.ProfitabilityFloorWaived} Ratios alcanzados: {waived} — siguen en «Critico» " +
                  "y puntúan en el score compuesto, pero no activan el piso.");
        }

        // The acid-test floor waived on a current ratio at or above its warn threshold (scoring algorithm 11).
        // Same place and reason as the profitability waiver above: a «Critico» ratio that floors nothing.
        if (bd.AcidTestFloorWaived == true)
        {
            sb.AppendLine(
                $"{ScoringNoticeTexts.AcidTestFloorWaived} Prueba ácida sigue en «Critico» y puntúa en el score " +
                "compuesto, pero no activa el piso.");
        }

        // BCRA trend
        if (bd.BcraTrendDelta is not null and not 0)
        {
            var direction = bd.BcraTrendDelta > 0 ? "empeorando" : "mejorando";
            sb.AppendLine(
                $"Tendencia BCRA: situación {direction} (delta {bd.BcraTrendDelta:+#;-#;0}), " +
                $"ajuste aplicado: {bd.BcraTrendAdj?.ToString("F1", CultureInfo.InvariantCulture) ?? "0"} puntos.");
        }

        // Cheques rechazados (scoring algorithm 5). Rendered only when the penalty actually moved the
        // BCRA component: chequesAdj is the REALISED movement, so > 0 means the score carries it. The
        // block closes by telling the model to explain these figures and call anything absent
        // unavailable, so a penalty folded into scoreBcra with no line here would leave the model
        // explaining a BCRA score whose arithmetic it cannot see. Rows written before this signal
        // existed carry no key at all and stay silent, exactly like the trend line above.
        if (bd.ChequesAdj is > 0m)
        {
            // Both figures are formatted with InvariantCulture so this prompt does not change shape
            // with the server's culture — it is rebuilt and re-sent on every turn, and the model is
            // separately instructed to render numbers es-AR in what it writes back.
            var recuperado = bd.ChequesProporcionRecuperada is { } proporcion
                ? $"se recuperó el {(proporcion * 100m).ToString("F1", CultureInfo.InvariantCulture)} % del importe rechazado"
                : "el importe recuperado no está disponible";

            sb.AppendLine(
                $"Cheques rechazados: {recuperado}; " +
                $"penalidad aplicada al score BCRA: +{bd.ChequesAdj.Value.ToString("F1", CultureInfo.InvariantCulture)} puntos.");
        }

        // Caída de ventas LUFE (scoring algorithm 6, §7.3a; real terms since algorithm 7, §7.3b).
        // Rendered only when the penalty actually moved the score: ventasAdj is the REALISED
        // movement, so > 0 means the final score carries it. Unlike the cheques line this one is NOT
        // folded into a component — it is an adjustment on the composite, so the component figures
        // above will not add up to the final score without it, and leaving it out would hand the
        // model an arithmetic it cannot close. Rows written before this signal existed carry no key
        // at all and stay silent, like the lines above.
        if (bd.VentasAdj is > 0m)
        {
            // Invariant culture for the same reason the cheques line uses it: this prompt is rebuilt
            // and re-sent every turn and must not change shape with the server's culture. The model
            // is separately instructed to render numbers es-AR in what it writes back.
            var baseTexto = bd.VentasBase switch
            {
                "interanual_3m" => "interanual, últimos 3 meses contra los mismos 3 meses del año anterior",
                "interanual"  => "interanual, contra el mismo mes del año anterior",
                "trimestral"  => "trimestral, último trimestre contra el anterior",
                _             => "base no informada"
            };

            var caida = bd.VentasVariacion is { } variacion
                ? $"caída de {(Math.Abs(variacion) * 100m).ToString("F1", CultureInfo.InvariantCulture)} % ({baseTexto})"
                : $"caída registrada ({baseTexto})";

            // THREE states, not two, and the null arm is load-bearing. A row scored before §7.3b
            // carries no such key: it was nominal BY DESIGN, not for want of inflation data, so it
            // keeps the exact sentence it was written under and old analyses render unchanged.
            // Saying "sin datos de inflación" about it would assert something untrue.
            var terminos = bd.VentasVariacionEsReal switch
            {
                true  => "medida en términos reales (deflactado por IPC)",
                false => "medida en términos nominales (sin datos de inflación)",
                null  => "medida en términos nominales"
            };

            sb.AppendLine(
                $"Ventas declaradas (LUFE): {caida}, {terminos}; " +
                $"penalidad aplicada al score final: +{bd.VentasAdj.Value.ToString("F1", CultureInfo.InvariantCulture)} puntos.");
        }

        // Garantías SGR en mora (scoring algorithm 8, §7.4). Rendered only when the penalty actually
        // moved the score: casfogMoraAdj is the REALISED movement, so > 0 means the final score carries
        // it. Like the ventas line and unlike the cheques one this is NOT folded into a component — it
        // is a second adjustment on the composite, so the component figures above will not add up to
        // the final score without it either. Rows written before this signal existed carry no key at
        // all and stay silent, like the lines above.
        if (bd.CasfogMoraAdj is > 0m)
        {
            // Invariant culture for the same reason the two lines above use it: this prompt is rebuilt
            // and re-sent every turn and must not change shape with the server's culture. The model is
            // separately instructed to render numbers es-AR in what it writes back.
            var mora = bd.CasfogProporcionMora is { } proporcionMora
                ? $"el {(proporcionMora * 100m).ToString("F1", CultureInfo.InvariantCulture)} % del saldo vigente informado está en mora"
                : "hay mora informada sobre el saldo vigente";

            sb.AppendLine(
                $"Garantías SGR (CASFOG): {mora}, medido por saldo y no por cantidad de garantías; " +
                $"penalidad aplicada al score final: +{bd.CasfogMoraAdj.Value.ToString("F1", CultureInfo.InvariantCulture)} puntos.");
        }

        // Sector
        if (!string.IsNullOrWhiteSpace(bd.SectorProfile))
            sb.AppendLine($"Perfil sectorial: {bd.SectorProfile}.");

        sb.AppendLine(
            "Estos valores fueron calculados determinísticamente por el motor de scoring. " +
            "Explicalos al analista, pero nunca los recalcules ni los cuestiones. " +
            "Si te preguntan un dato que no figura aquí, indicá que no está disponible.");

        // "\n" on every host — see BuildChatSystemPrompt. The sector profile is free text from the blob.
        return sb.ToString().ReplaceLineEndings("\n");
    }
}
