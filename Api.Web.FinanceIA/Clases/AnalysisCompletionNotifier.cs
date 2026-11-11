// Project : Api.Web.FinanceIA
// Created : 2026-10-02

using System.Globalization;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IAnalysisCompletionNotifier"/>.
///
/// <para>
/// Registered Scoped, like the repositories it writes through. It is driven from
/// <c>AnalysisPipeline.RunAsync</c> (the long part of <c>AnalysisController.TriggerAsync</c>, inline or
/// in the background) and from the analysis worker's stale-Processing sweep, at the run's terminal points — after the document has been
/// set to "Completed", or to "Failed" once the server owned the work — so by the time this runs the
/// outcome is already a fact, and nothing that happens here can change it.
/// </para>
///
/// <para>
/// SOURCEID IS THE RESULT ID on the completed row, for the same reason as in
/// <see cref="AnalysisDecisionNotifier"/>: the Portal deep-links a notification with SourceType
/// "Analysis" to /dashboard/analyses/{sourceId}, which is keyed by <c>AnalysisResults.ResultId</c>.
/// </para>
///
/// <para>
/// THE FAILED ROW POINTS AT THE RUN: SourceType <see cref="AnalysisRunSourceType"/>, SourceId = the
/// DOCUMENT id — there is no result to open, but the document is what GET /api/analyses/runs lists and
/// what POST /api/analyses re-triggers. Rows written before this carry no source and are left as they
/// are. A Portal build that does not know the type routes it nowhere (marks it read, no navigation).
/// </para>
/// </summary>
public sealed class AnalysisCompletionNotifier : IAnalysisCompletionNotifier
{
    /// <summary>The analysis finished and its result is ready to open.</summary>
    public const string CompletedType = "ANALYSIS_COMPLETED";

    /// <summary>The analysis could not be completed after the server took the work on.</summary>
    public const string FailedType = "ANALYSIS_FAILED";

    /// <summary>Value stored in <see cref="Notification.SourceType"/> by the completed row.</summary>
    public const string AnalysisSourceType = "Analysis";

    /// <summary>
    /// Value stored in <see cref="Notification.SourceType"/> by the failed row; its SourceId is the
    /// document id of the run (GET /api/analyses/runs lists it). Wire contract the Portal routes on.
    /// </summary>
    public const string AnalysisRunSourceType = "AnalysisRun";

    /// <summary>Width of [dbo].[Notifications].[Title] — nvarchar(300), DDL 049.</summary>
    private const int MaxTitleLength = 300;

    private readonly INotificationRepository              _notificationRepository;
    private readonly IEntityRepository                    _entityRepository;
    private readonly ILogger<AnalysisCompletionNotifier>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisCompletionNotifier"/>.
    /// </summary>
    public AnalysisCompletionNotifier(
        INotificationRepository             notificationRepository,
        IEntityRepository                   entityRepository,
        ILogger<AnalysisCompletionNotifier> logger)
    {
        _notificationRepository = notificationRepository;
        _entityRepository       = entityRepository;
        _logger                 = logger;
    }

    /// <inheritdoc />
    public async Task NotifyCompletedAsync(
        string            tenantId,
        Guid              userId,
        Guid              analysisId,
        Guid              resultId,
        string?           entityName,
        int               fiscalYear,
        decimal?          score,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;

        try
        {
            // Keyed on (user, type, SOURCE), and on the value actually STORED in SourceId below, so
            // no path can put two ANALYSIS_COMPLETED rows for the same result in one inbox.
            var alreadyNotified = await _notificationRepository
                .ExistsByTypeForUserAndSourceAsync(tenantId, userId, CompletedType, resultId, ct)
                .ConfigureAwait(false);

            if (alreadyNotified) return;

            var notification = new Notification
            {
                // NotificationConfiguration maps NotificationId as ValueGeneratedNever — the app
                // supplies it, exactly as AnalysisDecisionNotifier does.
                NotificationId = Guid.NewGuid(),
                TenantId       = tenantId,
                UserId         = userId,
                Type           = CompletedType,
                Title          = BuildCompletedTitle(entityName),
                Body           = BuildCompletedBody(fiscalYear, score),
                SourceType     = AnalysisSourceType,
                // The ResultId, not the AnalysisId — see the class remarks.
                SourceId       = resultId,
                IsRead         = false,
                CreatedAt      = DateTime.UtcNow
            };

            await _notificationRepository.CreateManyAsync([notification], ct).ConfigureAwait(false);

            // Ids only. The title and body name the company — neither belongs in a log line.
            _logger.LogInformation(
                "Analysis completion notification emitted: {AnalysisId} result {ResultId} tenant {TenantId}",
                analysisId, resultId, tenantId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Analysis completion notification cancelled for analysis {AnalysisId} tenant {TenantId}.",
                analysisId, tenantId);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, and Warning rather than Error: the analysis is already
            // persisted and its document already "Completed". What is lost is only the inbox copy.
            _logger.LogWarning(
                ex,
                "Failed to emit the completion notification for analysis {AnalysisId} tenant {TenantId}. The analysis was persisted and is unaffected.",
                analysisId, tenantId);
        }
    }

    /// <inheritdoc />
    public async Task NotifyFailedAsync(
        string            tenantId,
        Guid              userId,
        Guid              documentId,
        Guid              entityId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;

        try
        {
            var entityName = await ResolveEntityNameAsync(tenantId, documentId, entityId, ct)
                .ConfigureAwait(false);

            // No dedupe read: each failed run is its own event, and one document can fail many times,
            // so (user, type, source) is not unique here. The pipeline guarantees at most one call per run.
            var notification = new Notification
            {
                NotificationId = Guid.NewGuid(),
                TenantId       = tenantId,
                UserId         = userId,
                Type           = FailedType,
                Title          = BuildFailedTitle(entityName),
                Body           = "Volvé a intentarlo desde Nuevo análisis. El documento quedó cargado.",
                // No analysis row exists to link to: the source is the run, keyed by its document.
                SourceType     = AnalysisRunSourceType,
                SourceId       = documentId,
                IsRead         = false,
                CreatedAt      = DateTime.UtcNow
            };

            await _notificationRepository.CreateManyAsync([notification], ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Analysis failure notification emitted: document {DocumentId} tenant {TenantId}",
                documentId, tenantId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Analysis failure notification cancelled for document {DocumentId} tenant {TenantId}.",
                documentId, tenantId);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed: the document is already "Failed" and re-analysable. What is
            // lost is only the inbox copy.
            _logger.LogWarning(
                ex,
                "Failed to emit the failure notification for document {DocumentId} tenant {TenantId}. The document state is unaffected.",
                documentId, tenantId);
        }
    }

    /// <summary>
    /// The entity's business name for the failed copy, or null when it cannot be had.
    /// </summary>
    /// <remarks>
    /// A failed lookup degrades the title to its generic form rather than costing the analyst the
    /// notification. The narrow name projection, not the full-row <c>GetByIdAsync</c>: the copy
    /// needs one string, not five nvarchar(max) snapshots.
    /// </remarks>
    private async Task<string?> ResolveEntityNameAsync(
        string tenantId, Guid documentId, Guid entityId, CancellationToken ct)
    {
        try
        {
            var projection = await _entityRepository
                .GetNameByIdAsync(entityId, tenantId, ct)
                .ConfigureAwait(false);

            return projection?.BusinessName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Entity name lookup failed for the failure notification of document {DocumentId} tenant {TenantId}; using the generic title.",
                documentId, tenantId);
            return null;
        }
    }

    /// <summary>"Análisis listo: {company}", or the bare headline when there is no name.</summary>
    private static string BuildCompletedTitle(string? entityName)
        => string.IsNullOrWhiteSpace(entityName)
            ? "Análisis listo"
            : ClampTitle($"Análisis listo: {entityName.Trim()}");

    /// <summary>"No pudimos completar el análisis de {company}", or without the name.</summary>
    private static string BuildFailedTitle(string? entityName)
        => string.IsNullOrWhiteSpace(entityName)
            ? "No pudimos completar el análisis"
            : ClampTitle($"No pudimos completar el análisis de {entityName.Trim()}");

    /// <summary>
    /// The completed body: the fiscal year when there is one, and the score when scoring produced one.
    /// </summary>
    /// <remarks>
    /// The score is whatever the pipeline already holds — scoring is fault-isolated there, so a null
    /// simply drops the sentence rather than printing a zero that never existed.
    ///
    /// <para>
    /// It is the INTERNAL motor score (0–100, 0 = healthy), so it is restated on the NOSIS scale
    /// (0–1000, 1000 = healthy) through the canonical <see cref="ScoreScale.ToNosis"/> — the figure the
    /// Portal's gauge and the exported PDF show. Printing the raw value would tell the analyst the
    /// inverse of what they see when they open the analysis.
    /// </para>
    /// </remarks>
    private static string BuildCompletedBody(int fiscalYear, decimal? score)
    {
        var body = fiscalYear > 0
            ? $"El análisis del balance {fiscalYear.ToString(CultureInfo.InvariantCulture)} terminó."
            : "El análisis del balance terminó.";

        return score is decimal value
            ? $"{body} Score: {ScoreScale.ToNosis(value).ToString(CultureInfo.InvariantCulture)} sobre 1000."
            : body;
    }

    /// <summary>
    /// Keeps a title inside [Title] nvarchar(300). Titles here interpolate the business name, so a
    /// long one must be cut rather than fail the insert.
    /// </summary>
    private static string ClampTitle(string title)
        => title.Length <= MaxTitleLength
            ? title
            : string.Concat(title.AsSpan(0, MaxTitleLength - 1), "…");
}
