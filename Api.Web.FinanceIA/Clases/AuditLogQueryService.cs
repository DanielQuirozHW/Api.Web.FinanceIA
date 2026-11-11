// Project : Api.Web.FinanceIA
// Created : 2026-06-20

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Implements <see cref="IAuditLogQueryService"/> by orchestrating the repository,
/// user resolution, and entity name resolution.
/// </summary>
public sealed class AuditLogQueryService : IAuditLogQueryService
{
    private static readonly IReadOnlyList<string> SeverityOrder =
        ["Info", "Warning", "Error", "Critical"];

    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IUserRepository _userRepository;
    private readonly IEntityRepository _entityRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly ILogger<AuditLogQueryService> _logger;

    public AuditLogQueryService(
        IAuditLogRepository auditLogRepository,
        IUserRepository userRepository,
        IEntityRepository entityRepository,
        IDocumentRepository documentRepository,
        ILogger<AuditLogQueryService> logger)
    {
        _auditLogRepository = auditLogRepository;
        _userRepository = userRepository;
        _entityRepository = entityRepository;
        _documentRepository = documentRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AuditLogListResponse> GetLogsAsync(
        string tenantId, AuditLogFilter filter, CancellationToken ct)
    {
        var (rawItems, total) = await _auditLogRepository
            .GetPagedAsync(tenantId, filter, ct)
            .ConfigureAwait(false);

        // ── Batch-resolve users ────────────────────────────────────────────────
        var userIds = rawItems
            .Where(x => x.UserId.HasValue)
            .Select(x => x.UserId!.Value)
            .Distinct();

        var userMap = await _userRepository
            .GetByIdsAsync(userIds, ct)
            .ConfigureAwait(false);

        var userById = userMap.ToDictionary(u => u.UserId);

        // ── Best-effort entity name resolution ─────────────────────────────────
        // Branched by EntityType: "Entity" → direct PyME lookup; "Document" → resolve
        // the owning PyME via the document; other types → entityName stays null.
        //
        // Two passes: collect the entity ids this page names, then resolve them all in ONE
        // set-based query. It used to be one IEntityRepository.GetByIdAsync per DISTINCT entity —
        // dictionary-cached, so not a plain N+1, but GetByIdAsync is a full-row load: every
        // distinct entity dragged all five of its nvarchar(max) source snapshots off disk purely
        // to read BusinessName. Same fix as #142.
        var entityCache      = new Dictionary<Guid, string?>();  // entityGuid → BusinessName
        var docEntityIdCache = new Dictionary<Guid, Guid>();     // documentGuid → owning entityGuid
        var neededEntityIds  = new HashSet<Guid>();

        // ── Pass 1: collect the entity ids to resolve ──────────────────────────
        foreach (var item in rawItems)
        {
            if (string.IsNullOrEmpty(item.EntityId)) continue;
            if (!Guid.TryParse(item.EntityId, out var itemGuid)) continue;

            if (item.EntityType == "Entity")
            {
                neededEntityIds.Add(itemGuid);
            }
            else if (item.EntityType == "Document")
            {
                // Resolve document → owning EntityId (cached). Deliberately still per-id and
                // unprojected: Document has no nvarchar(max) column, so a full-row read here is
                // cheap. The expensive read was the entity one, and that is the one now batched.
                if (!docEntityIdCache.TryGetValue(itemGuid, out var ownerEntityGuid))
                {
                    try
                    {
                        var doc = await _documentRepository
                            .GetByIdAsync(itemGuid, tenantId, ct)
                            .ConfigureAwait(false);
                        if (doc is null) continue;
                        ownerEntityGuid = doc.EntityId;
                        docEntityIdCache[itemGuid] = ownerEntityGuid;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not resolve document for DocumentId {DocumentId}, TenantId {TenantId}",
                            itemGuid, tenantId);
                        continue;
                    }
                }

                neededEntityIds.Add(ownerEntityGuid);
            }
            // Other EntityTypes (Login, System, Config …): skip → entityName stays null
        }

        // ── Pass 2: one lookup for every entity id the page names ──────────────
        // Projects three display columns, so no source snapshot is read. Ids with no entity in
        // this tenant are absent from the map and never enter entityCache, so the mapping pass
        // below misses on them and leaves entityName null — what entity?.BusinessName did before.
        if (neededEntityIds.Count > 0)
        {
            try
            {
                var entityLookup = await _entityRepository
                    .GetNameLookupByIdsAsync(neededEntityIds, tenantId, ct)
                    .ConfigureAwait(false);

                foreach (var (entityGuid, projection) in entityLookup)
                    entityCache[entityGuid] = projection.BusinessName;
            }
            catch (Exception ex)
            {
                // Best-effort, as the per-item resolution was: a name that cannot be spelled must
                // not cost the auditor the page. entityCache stays empty → every entityName falls
                // back to null and the log still renders.
                _logger.LogWarning(ex,
                    "Could not batch-resolve entity names for {EntityIdCount} ids, TenantId {TenantId}",
                    neededEntityIds.Count, tenantId);
            }
        }

        // ── Map to response DTOs ───────────────────────────────────────────────
        var items = rawItems.Select(x =>
        {
            string? displayName = null;
            string? email = null;

            if (x.UserId.HasValue && userById.TryGetValue(x.UserId.Value, out var user))
            {
                displayName = user.DisplayName;
                email = user.Email;
            }

            string? entityName = null;

            if (!string.IsNullOrEmpty(x.EntityId) && Guid.TryParse(x.EntityId, out var itemGuid))
            {
                if (x.EntityType == "Entity" && entityCache.TryGetValue(itemGuid, out var n))
                    entityName = n;
                else if (x.EntityType == "Document" &&
                         docEntityIdCache.TryGetValue(itemGuid, out var ownerGuid) &&
                         entityCache.TryGetValue(ownerGuid, out var n2))
                    entityName = n2;
            }

            string? entityTypeLabel = x.EntityType switch
            {
                "Entity"   => "Empresa",
                "Document" => "Documento",
                "Analysis" => "Análisis",
                null       => null,
                var other  => other,
            };

            return new AuditLogItem
            {
                AuditLogId = x.AuditLogId,
                Action = x.Action,
                EntityType = x.EntityType,
                EntityId = x.EntityId,
                EntityName = entityName,
                EntityTypeLabel = entityTypeLabel,
                UserDisplayName = displayName,
                UserEmail = email,
                IpAddress = x.IpAddress,
                RequestId = x.RequestId,
                Source = x.Source,
                Severity = x.Severity,
                Timestamp = x.Timestamp,
            };
        }).ToList();

        return new AuditLogListResponse
        {
            Items = items,
            Page = filter.Page,
            PageSize = filter.PageSize,
            Total = total,
        };
    }

    /// <inheritdoc />
    public async Task<AuditLogFiltersResponse> GetFiltersAsync(
        string tenantId, CancellationToken ct)
    {
        var actions = await _auditLogRepository
            .GetDistinctActionsAsync(tenantId, ct)
            .ConfigureAwait(false);

        return new AuditLogFiltersResponse
        {
            Actions = actions,
            Severities = SeverityOrder,
        };
    }
}
