// Project : Api.Web.FinanceIA
// Created : 2026-05-09

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Api.Web.FinanceIA.Servicios.Pdf;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Generates and streams report exports (PDF) for a given analysis result.
/// Route: GET /api/bff/analyses/{analysisId}/export/pdf
/// All endpoints require an internal JWT (<c>Internal</c> scheme).
/// Tenant isolation is enforced on every lookup.
/// </summary>
[ApiController]
[Route("api/bff/analyses/{analysisId:guid}/export")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class ExportController : ControllerBase
{
    // Internal rather than private (InternalsVisibleTo: Api.Web.FinanceIA.Tests) so a test can pin
    // this whitelist against PdfExportService.AllSections. They are two lists that can drift, and
    // until now that drift was only visible as a runtime Warning logged by the service.
    // Two reports share it: the balance keys and the persona_* keys. Which half a request may use is
    // decided by the analysis kind (see IsKeyForKind), after the analysis is loaded.
    internal static readonly IReadOnlySet<string> ValidSectionKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "balance", "ratios", "ratio_breakdown", "executive_summary", "risk", "bcra", "lufe", "casfog", "anomalies", "cash_flow", "footer",
            "persona_resumen", "persona_elegibilidad", "persona_filtros", "persona_capacidad", "persona_solvencia",
            "persona_fiadores", "persona_coherencia", "persona_cobertura", "persona_fuentes", "persona_decision"
        };

    /// <summary>The template a persona humana analysis exports with when no sections are asked for.</summary>
    internal const string PersonaTemplate = "persona";

    private const string PersonaKeyPrefix = "persona_";

    internal static readonly Dictionary<string, IReadOnlyList<string>> Templates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ejecutivo"] = new[] { "executive_summary", "risk", "footer" },
            ["completo"]  = new[] { "balance", "ratios", "executive_summary", "risk", "cash_flow", "footer" },
            // "casfog" belongs to the FULL AUDIT template only, beside bcra and anomalies. It is safe
            // here for every tenant: an entity with no CASFOG snapshot renders the section's
            // "Datos no disponibles." placeholder and nothing else. "lufe" rides with them, right after
            // bcra, and is safer still: with no LUFE series the service renders nothing for it at all.
            ["tecnico"]   = new[] { "balance", "ratios", "ratio_breakdown", "executive_summary", "risk", "bcra", "lufe", "casfog", "anomalies", "cash_flow", "footer" },
            // The persona humana report (M10 Phase 4b): every persona section, in the Portal's order.
            [PersonaTemplate] = new[]
            {
                "persona_resumen", "persona_elegibilidad", "persona_filtros", "persona_capacidad", "persona_solvencia",
                "persona_fiadores", "persona_coherencia", "persona_cobertura", "persona_fuentes", "persona_decision"
            }
        };

    /// <summary>The analysis kind a template's sections belong to.</summary>
    internal static string TemplateKind(string templateKey)
        => string.Equals(templateKey, PersonaTemplate, StringComparison.OrdinalIgnoreCase)
            ? AnalysisKinds.PersonaHumana
            : AnalysisKinds.Balance;

    /// <summary>A persona humana analysis takes only persona_* keys; a balance analysis takes none of them.</summary>
    internal static bool IsKeyForKind(string key, string analysisKind)
        => key.StartsWith(PersonaKeyPrefix, StringComparison.OrdinalIgnoreCase)
           == (analysisKind == AnalysisKinds.PersonaHumana);

    private readonly ITenantContextAccessor       _tenantContextAccessor;
    private readonly IAnalysisResultRepository    _analysisResultRepository;
    private readonly IAnalysisRepository          _analysisRepository;
    private readonly IUserRepository              _userRepository;
    private readonly IPdfExportService            _pdfExportService;
    private readonly IExportLogRepository         _exportLogRepository;
    private readonly IPersonAnalysisRepository    _personAnalysisRepository;
    private readonly IEntityRepository            _entityRepository;
    private readonly ICreditLineRepository        _creditLineRepository;
    private readonly ITenantSettingsService       _tenantSettingsService;
    private readonly IAnalysisComparisonService   _analysisComparisonService;
    private readonly ITenantPdfBrandingProvider   _brandingProvider;
    private readonly IAlertRuleCatalogueRepository _alertRuleCatalogueRepository;
    private readonly ILogger<ExportController>    _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ExportController"/>.
    /// </summary>
    public ExportController(
        ITenantContextAccessor    tenantContextAccessor,
        IAnalysisResultRepository analysisResultRepository,
        IAnalysisRepository       analysisRepository,
        IUserRepository           userRepository,
        IPdfExportService         pdfExportService,
        IExportLogRepository      exportLogRepository,
        IPersonAnalysisRepository personAnalysisRepository,
        IEntityRepository         entityRepository,
        ICreditLineRepository     creditLineRepository,
        ITenantSettingsService    tenantSettingsService,
        IAnalysisComparisonService analysisComparisonService,
        ITenantPdfBrandingProvider brandingProvider,
        IAlertRuleCatalogueRepository alertRuleCatalogueRepository,
        ILogger<ExportController> logger)
    {
        _tenantContextAccessor    = tenantContextAccessor;
        _analysisResultRepository = analysisResultRepository;
        _analysisRepository       = analysisRepository;
        _userRepository           = userRepository;
        _pdfExportService         = pdfExportService;
        _exportLogRepository      = exportLogRepository;
        _personAnalysisRepository = personAnalysisRepository;
        _entityRepository         = entityRepository;
        _creditLineRepository     = creditLineRepository;
        _tenantSettingsService    = tenantSettingsService;
        _analysisComparisonService = analysisComparisonService;
        _brandingProvider         = brandingProvider;
        _alertRuleCatalogueRepository = alertRuleCatalogueRepository;
        _logger                   = logger;
    }

    // ── GET /api/bff/analyses/{analysisId}/export/pdf ─────────────────────────

    /// <summary>
    /// Generates a PDF report for the specified analysis result and streams it
    /// as a file download. Tenant ownership is validated before generation.
    /// Logs the export to ExportLogs on success.
    /// </summary>
    /// <param name="analysisId">The AnalysisResult primary key (ResultId).</param>
    /// <param name="sections">
    /// Comma-separated list of section keys to include.
    /// Balance analysis: balance, ratios, ratio_breakdown, executive_summary, risk, bcra, lufe, casfog,
    /// anomalies, cash_flow, footer. Persona humana analysis: persona_resumen, persona_elegibilidad,
    /// persona_filtros, persona_capacidad, persona_solvencia, persona_fiadores, persona_coherencia,
    /// persona_cobertura, persona_fuentes, persona_decision. A key of the other kind is 400.
    /// When omitted, all sections of the analysis's kind are included (a persona analysis uses the
    /// "persona" template).
    /// </param>
    /// <param name="template">Optional template key that supplies the sections when none are given.</param>
    /// <param name="theme">
    /// Optional palette, case-insensitive: <c>light</c> (default — the paper document) or <c>dark</c> (navy paper,
    /// for reading on screen). Both reports. Any other value is 400. The file name does not change with it.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// 200 with application/pdf binary on success;
    /// 403 when the result does not belong to this tenant;
    /// 400 when section keys, the template or the theme are invalid, or the keys belong to the other analysis kind;
    /// 500 on unexpected failure.
    /// </returns>
    [HttpGet("pdf")]
    [Authorize(Roles = Roles.All)]
    [ProducesResponseType(typeof(FileResult), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> ExportPdfAsync(
        [FromRoute] Guid   analysisId,
        [FromQuery] string sections          = "",
        [FromQuery] string template          = "",
        [FromQuery] string theme             = "",
        CancellationToken  cancellationToken = default)
    {
        // ── Resolve tenant ────────────────────────────────────────────────────

        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in ExportController");
            return Problem(statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved.");
        }

        string tenantId = tenantContext.TenantId;

        // ── Resolve template ────────────────────────────────────────────────────

        string? resolvedTemplate = null;

        if (!string.IsNullOrWhiteSpace(template))
        {
            if (!Templates.TryGetValue(template, out IReadOnlyList<string>? templateSections))
            {
                return Problem(statusCode: 400, title: "Bad Request",
                    detail: $"La plantilla '{template}' no existe. Plantillas válidas: {string.Join(", ", Templates.Keys)}.");
            }

            resolvedTemplate = template.ToLowerInvariant();

            // Template supplies sections only when explicit sections param is absent
            if (string.IsNullOrWhiteSpace(sections))
                sections = string.Join(",", templateSections);
        }

        // ── Resolve theme ───────────────────────────────────────────────────────

        if (!TryParseTheme(theme, out PdfTheme pdfTheme))
        {
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"El tema '{theme}' no existe. Temas válidos: {string.Join(", ", ValidThemes)}.");
        }

        // ── Validate and parse section keys ───────────────────────────────────

        IReadOnlyList<string>? sectionKeys = ParseSections(sections);
        if (sectionKeys is null)
        {
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Una o más claves de sección no son válidas. " +
                        "Valores válidos: " + string.Join(", ", ValidSectionKeys.Order(StringComparer.Ordinal)) + ".");
        }

        // ── Load AnalysisResult — 403 on not-found to avoid info leakage ──────

        AnalysisResult? result = await _analysisResultRepository
            .GetByIdAsync(analysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            _logger.LogWarning(
                "PDF export denied for result {AnalysisId} tenant {TenantId}: not found",
                analysisId, tenantId);
            return Problem(statusCode: 403, title: "Forbidden",
                detail: "No se encontró el resultado del análisis o no pertenece a este tenant.");
        }

        // ── Load Analysis (for fiscal year) ───────────────────────────────────

        Analysis? analysis = await _analysisRepository
            .GetByIdAsync(result.AnalysisId, cancellationToken)
            .ConfigureAwait(false);

        if (analysis is null)
        {
            _logger.LogWarning(
                "Analysis record missing for AnalysisId {AnalysisId} tenant {TenantId}",
                result.AnalysisId, tenantId);
            return Problem(statusCode: 500, title: "Internal Server Error",
                detail: "Analysis metadata not found.");
        }

        // ── Match the sections to the analysis kind ──────────────────────────
        // A persona humana analysis (DDL 072) has no balance, ratios or score, and a balance analysis has
        // no persona evaluation: each report takes only its own keys. No sections on a persona analysis
        // means the persona template.

        bool isPersona = analysis.AnalysisKind == AnalysisKinds.PersonaHumana;

        if (isPersona && sectionKeys.Count == 0)
            sectionKeys = Templates[PersonaTemplate];

        // ExportLogs.Template names the report a persona export produced, whichever sections it carried.
        if (isPersona)
            resolvedTemplate ??= PersonaTemplate;

        if (sectionKeys.Any(key => !IsKeyForKind(key, analysis.AnalysisKind)))
        {
            _logger.LogWarning(
                "PDF export rejected for result {AnalysisId} tenant {TenantId}: section keys of the other analysis kind ({Kind})",
                analysisId, tenantId, analysis.AnalysisKind);

            var mismatch = Problem(statusCode: 400, title: "Bad Request",
                detail: isPersona
                    ? "El análisis de una persona humana sólo admite las secciones persona_* (plantilla \"persona\")."
                    : "Las secciones persona_* sólo aplican al análisis de una persona humana.");
            ((ProblemDetails)mismatch.Value!).Extensions["errorCode"] = "section_not_valid_for_analysis_kind";
            return mismatch;
        }

        // ── Resolve current user ───────────────────────────────────────────────

        string? firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return Unauthorized();

        User? currentUser = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, cancellationToken)
            .ConfigureAwait(false);

        if (currentUser is null)
            return Unauthorized();

        // ── Generate PDF ──────────────────────────────────────────────────────

        var sw = Stopwatch.StartNew();
        byte[] pdfBytes;
        string? reportName;

        // The client identity for page 1's header, resolved once per export. Never fails it: no usable logo, or
        // any problem, is TenantPdfBranding.None — the Financia header.
        TenantPdfBranding branding = await TryBrandingAsync(tenantId, pdfTheme, result.ResultId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (isPersona)
            {
                PersonPdfReport personReport =
                    await BuildPersonReportAsync(result, analysis, tenantId, cancellationToken).ConfigureAwait(false);
                personReport = personReport with { Branding = branding };
                reportName = personReport.EntityName;
                pdfBytes = await _pdfExportService
                    .GeneratePersonAsync(personReport, sectionKeys, cancellationToken, pdfTheme)
                    .ConfigureAwait(false);
            }
            else
            {
                CompanyPdfReport companyReport =
                    await BuildCompanyReportAsync(result, analysis, tenantId, cancellationToken).ConfigureAwait(false);
                companyReport = companyReport with { Branding = branding };
                reportName = companyReport.EntityName;
                pdfBytes = await _pdfExportService
                    .GenerateAsync(result, analysis, tenantId, sectionKeys, cancellationToken, companyReport, pdfTheme)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "PDF generation failed for result {AnalysisId} tenant {TenantId}",
                analysisId, tenantId);

            return Problem(statusCode: 500, title: "Internal Server Error",
                detail: "No se pudo generar el PDF.");
        }

        sw.Stop();

        // ── Compute file hash ─────────────────────────────────────────────────

        string fileHash = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();

        // ── Log export (fire-and-forget) ──────────────────────────────────────

        string sectionsJson = JsonSerializer.Serialize(
            sectionKeys.Count > 0 ? sectionKeys : PdfExportService.BalanceSections);

        string? ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        ExportLog exportLog = new()
        {
            AnalysisId       = analysisId,
            TenantId         = tenantId,
            ExportedBy       = currentUser.UserId,
            Format           = "PDF",
            SectionsIncluded = sectionsJson,
            Template         = resolvedTemplate,
            FileHashSha256   = fileHash,
            IpAddress        = ipAddress,
            ExportedAt       = DateTime.UtcNow
        };

        _ = _exportLogRepository
            .CreateAsync(exportLog, CancellationToken.None)
            .ContinueWith(
                t => _logger.LogWarning(
                    t.Exception?.GetBaseException(),
                    "ExportLog write failed for result {AnalysisId} tenant {TenantId}",
                    analysisId, tenantId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        _logger.LogInformation(
            "PDF exported for result {AnalysisId} tenant {TenantId}: " +
            "sections={Sections} theme={Theme} brand={Brand} bytes={Bytes} duration_ms={DurationMs}",
            analysisId, tenantId,
            string.Join(",", sectionKeys),
            ThemeKey(pdfTheme),
            branding.IsCoBranded ? "client" : "financia",
            pdfBytes.Length,
            sw.ElapsedMilliseconds);

        // ── Return PDF stream ─────────────────────────────────────────────────

        // "Informe-crediticio_<Razon-social>_Ejercicio-<año>_<aaaa-mm-dd>.pdf" / "Evaluacion-crediticia_<Nombre>_
        // <aaaa-mm-dd>.pdf": an ASCII name for every client, and the UTF-8 one as RFC 5987 filename*. The
        // header is exposed to the Portal by the CORS policy (Program.cs).
        DateOnly exportDate = DateOnly.FromDateTime(ArgentineTime.ToLocal(DateTime.UtcNow));
        ExportFileName.Names names = isPersona
            ? ExportFileName.ForPerson(reportName, exportDate)
            : ExportFileName.ForCompany(reportName, analysis.FiscalYear, exportDate);

        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(names.Ascii);
        disposition.FileNameStar = names.Utf8;
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();

        return File(pdfBytes, "application/pdf");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// What the company report's first page prints beyond the analysis and its result: the razón social and
    /// CUIT, this tenant's users the analysis names, the credit lines it originated, the previous decided
    /// analysis (IAnalysisComparisonService, the same reader GET .../previous-comparison uses), the tenant's
    /// display name and its AlertRuleCatalogue display names (the alert titles). EACH read is isolated: a failure
    /// leaves that member empty, logs a Warning with ids only, and the first page omits the item — the export
    /// never fails for it. No new external call.
    /// </summary>
    private async Task<CompanyPdfReport> BuildCompanyReportAsync(
        AnalysisResult result, Analysis analysis, string tenantId, CancellationToken cancellationToken)
    {
        async Task<T?> TryReadAsync<T>(string item, Func<Task<T?>> read) where T : class
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Company PDF export for result {ResultId} analysis {AnalysisId} tenant {TenantId}: {Item} unavailable; the first page omits it",
                    result.ResultId, analysis.AnalysisId, tenantId, item);
                return null;
            }
        }

        EntityNameProjection? entity = await TryReadAsync("entity",
            () => _entityRepository.GetNameByIdAsync(analysis.EntityId, tenantId, cancellationToken)).ConfigureAwait(false);

        if (entity is null)
        {
            _logger.LogWarning(
                "Company PDF export for result {ResultId} tenant {TenantId}: entity {EntityId} not found",
                result.ResultId, tenantId, analysis.EntityId);
        }

        Guid[] userIds = new Guid?[] { analysis.RequestedBy, analysis.ReviewedBy, analysis.PreApprovedBy }
            .OfType<Guid>()
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        IReadOnlyList<User>? users = userIds.Length == 0
            ? []
            : await TryReadAsync("users", () => _userRepository.GetByIdsAsync(userIds, cancellationToken)!).ConfigureAwait(false);

        IReadOnlyList<CreditLine>? creditLines = await TryReadAsync("credit_lines",
            () => _creditLineRepository.GetByOriginAnalysisAsync(analysis.AnalysisId, tenantId, cancellationToken)!).ConfigureAwait(false);

        AnalysisComparisonResponse? comparison = await TryReadAsync("previous_comparison",
            () => _analysisComparisonService.CompareWithPreviousAsync(result.ResultId, tenantId, cancellationToken)).ConfigureAwait(false);

        string? tenantName = await TryReadAsync("tenant_display_name",
            () => _tenantSettingsService.GetTenantDisplayNameAsync(tenantId, cancellationToken)).ConfigureAwait(false);

        // Every rule, enabled or not: a rule disabled since this analysis was scored still names its code.
        IReadOnlyList<AlertRuleDefinition>? catalogue = await TryReadAsync("alert_catalogue",
            () => _alertRuleCatalogueRepository.GetAllAsync(tenantId, cancellationToken)!).ConfigureAwait(false);

        var alertDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (AlertRuleDefinition rule in catalogue ?? [])
        {
            if (rule.TenantId == tenantId
                && !string.IsNullOrWhiteSpace(rule.Code)
                && !string.IsNullOrWhiteSpace(rule.DisplayNameEs))
            {
                alertDisplayNames.TryAdd(rule.Code.Trim(), rule.DisplayNameEs.Trim());
            }
        }

        return new CompanyPdfReport
        {
            EntityName        = entity?.BusinessName,
            EntityCuit        = entity?.CUIT,
            // GetByIdsAsync is not tenant-scoped: only this tenant's users are named.
            UserNames         = (users ?? [])
                .Where(u => u.TenantId == tenantId)
                .GroupBy(u => u.UserId)
                .ToDictionary(g => g.Key, g => g.First().DisplayName),
            CreditLines       = creditLines ?? [],
            Previous          = comparison is { Available: true } ? comparison.Previous : null,
            Current           = comparison is { Available: true } ? comparison.Current : null,
            TenantDisplayName = tenantName,
            AlertDisplayNames = alertDisplayNames,
        };
    }

    /// <summary>
    /// Everything the persona report prints, from the readers the detail uses: the solicitante's frozen
    /// evaluation with its decision rules (<see cref="PersonEvaluationJson.ResolveSolicitanteAsync"/>, the
    /// same resolution as GET /api/analyses/{id}), the approval records, the identity from Entity only, the
    /// approvers' names (this tenant's users only) and the credit line the approval originated.
    /// </summary>
    private async Task<PersonPdfReport> BuildPersonReportAsync(
        AnalysisResult result, Analysis analysis, string tenantId, CancellationToken cancellationToken)
    {
        var (rules, solicitante, fiadores) = await PersonEvaluationJson
            .ResolveSolicitanteAsync(_personAnalysisRepository, analysis.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        EntityNameProjection? entity = await _entityRepository
            .GetNameByIdAsync(analysis.EntityId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        AnalysisDecisionChecks? preApproval  = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(analysis.PreApprovalChecksJson);
        AnalysisDecisionChecks? confirmation = PersonEvaluationJson.TryDeserialize<AnalysisDecisionChecks>(analysis.ConfirmationChecksJson);

        Guid[] userIds = new[] { analysis.PreApprovedBy, analysis.ReviewedBy, preApproval?.By, confirmation?.By }
            .OfType<Guid>()
            .Distinct()
            .ToArray();

        IReadOnlyList<User> users = userIds.Length == 0
            ? []
            : await _userRepository.GetByIdsAsync(userIds, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CreditLine> creditLines = await _creditLineRepository
            .GetByOriginAnalysisAsync(analysis.AnalysisId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            _logger.LogWarning(
                "Persona PDF export for result {AnalysisId} tenant {TenantId}: entity {EntityId} not found",
                result.ResultId, tenantId, analysis.EntityId);
        }

        return new PersonPdfReport
        {
            ResultId           = result.ResultId,
            Analysis           = analysis,
            Evaluation         = PersonEvaluationJson.ToDetailResponse(rules, solicitante, fiadores),
            EntityName         = entity?.BusinessName ?? string.Empty,
            EntityCuit         = entity?.CUIT ?? string.Empty,
            PreApprovalChecks  = preApproval,
            ConfirmationChecks = confirmation,
            // GetByIdsAsync is not tenant-scoped: only this tenant's users are named.
            UserNames          = users
                .Where(u => u.TenantId == tenantId)
                .GroupBy(u => u.UserId)
                .ToDictionary(g => g.Key, g => g.First().DisplayName),
            CreditLines        = creditLines,
            RescoredAt         = result.RescoredAt,
            RescoreCount       = result.RescoreCount,
            TenantDisplayName  = await TryTenantDisplayNameAsync(tenantId, result.ResultId, cancellationToken).ConfigureAwait(false)
        };
    }

    // The document's Author. Never fails the export: GetTenantDisplayNameAsync already swallows its own read
    // failures, and anything else lands here.
    private async Task<string?> TryTenantDisplayNameAsync(string tenantId, Guid resultId, CancellationToken cancellationToken)
    {
        try
        {
            return await _tenantSettingsService.GetTenantDisplayNameAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "PDF export for result {ResultId} tenant {TenantId}: tenant display name unavailable; Author falls back to the product name",
                resultId, tenantId);
            return null;
        }
    }

    // The co-branded header's inputs. The provider already never throws (it logs and returns None); this guard
    // keeps the export alive even if a future provider did.
    private async Task<TenantPdfBranding> TryBrandingAsync(
        string tenantId, PdfTheme theme, Guid resultId, CancellationToken cancellationToken)
    {
        try
        {
            return await _brandingProvider.ResolveAsync(tenantId, theme, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "PDF export for result {ResultId} tenant {TenantId}: branding unavailable; the header stays Financia",
                resultId, tenantId);
            return TenantPdfBranding.None;
        }
    }

    /// <summary>The <c>theme</c> values the export accepts, in the order the 400 lists them.</summary>
    internal static readonly IReadOnlyList<string> ValidThemes = ["light", "dark"];

    /// <summary>
    /// Parses the <c>theme</c> query parameter: absent or blank is <see cref="PdfTheme.Light"/>; <c>light</c> and
    /// <c>dark</c> are accepted in any case, surrounding spaces ignored. Anything else — a number included, which
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would accept — is false.
    /// </summary>
    internal static bool TryParseTheme(string? raw, out PdfTheme theme)
    {
        theme = PdfTheme.Light;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string value = raw.Trim();
        if (value.Equals("light", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Equals("dark", StringComparison.OrdinalIgnoreCase))
        {
            theme = PdfTheme.Dark;
            return true;
        }

        return false;
    }

    private static string ThemeKey(PdfTheme theme) => theme == PdfTheme.Dark ? "dark" : "light";

    /// <summary>
    /// Parses and validates the sections query parameter.
    /// Returns null when any key is invalid.
    /// Returns an empty list when the parameter is absent/empty (meaning all sections).
    /// </summary>
    private static IReadOnlyList<string>? ParseSections(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        string[] keys = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string key in keys)
        {
            if (!ValidSectionKeys.Contains(key))
                return null;
        }

        return keys;
    }
}
