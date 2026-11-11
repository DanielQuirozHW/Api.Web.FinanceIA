// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using System.Diagnostics;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// EF-backed implementation of <see cref="IPersonLegajoSnapshotRefreshService"/>. Mirrors
/// EntitiesController.RefreshBcraAsync / RefreshArcaAsync step for step — same services, same
/// <see cref="PersonSnapshotWriter"/>, same ARCA ApiCallLogs row — with the legajo's CUIT as the key.
/// BCRA goes through <see cref="IBcraService"/>, so the Central de Deudores gate and the failure cache
/// (#247) apply here exactly as on the entity route.
/// </summary>
public sealed class PersonLegajoSnapshotRefreshService : IPersonLegajoSnapshotRefreshService
{
    private readonly ILegajoDocumentRepository _legajoDocumentRepository;
    private readonly IPersonLegajoRepository   _personLegajoRepository;
    private readonly IEntityRepository         _entityRepository;
    private readonly IFiadorRepository         _fiadorRepository;
    private readonly IBcraService              _bcraService;
    private readonly IArcaConstanciaService    _arcaConstanciaService;
    private readonly IApiCallLogService        _apiCallLogService;
    private readonly ILogger<PersonLegajoSnapshotRefreshService> _logger;

    /// <summary>Initializes a new instance of <see cref="PersonLegajoSnapshotRefreshService"/>.</summary>
    public PersonLegajoSnapshotRefreshService(
        ILegajoDocumentRepository legajoDocumentRepository,
        IPersonLegajoRepository   personLegajoRepository,
        IEntityRepository         entityRepository,
        IFiadorRepository         fiadorRepository,
        IBcraService              bcraService,
        IArcaConstanciaService    arcaConstanciaService,
        IApiCallLogService        apiCallLogService,
        ILogger<PersonLegajoSnapshotRefreshService> logger)
    {
        _legajoDocumentRepository = legajoDocumentRepository;
        _personLegajoRepository   = personLegajoRepository;
        _entityRepository         = entityRepository;
        _fiadorRepository         = fiadorRepository;
        _bcraService              = bcraService;
        _arcaConstanciaService    = arcaConstanciaService;
        _apiCallLogService        = apiCallLogService;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task<LegajoSnapshotRefreshOutcome> RefreshBcraAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var (legajo, entity) = await ResolveAsync(personLegajoId, tenantId, ct).ConfigureAwait(false);
        if (legajo is null)
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.LegajoNotFound);

        var bcraData = await _bcraService
            .GetBcraDataForAnalysisAsync(legajo.CUIT, ct)
            .ConfigureAwait(false);

        if (bcraData is null)
        {
            _logger.LogWarning(
                "BCRA returned no data for legajo {PersonLegajoId} tenant {TenantId}; snapshot left unchanged",
                personLegajoId, tenantId);
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.BcraUnavailable);
        }

        // A failed Central de Deudores is never stored over what the legajo holds (the same rule
        // RefreshUnusableBcraAsync applies): the outcome and its HTTP mapping are the all-failed ones.
        if (!PersonSnapshotWriter.IsDeudaAnswered(bcraData))
        {
            _logger.LogWarning(
                "BCRA Central de Deudores unavailable for legajo {PersonLegajoId} tenant {TenantId}; snapshot left unchanged",
                personLegajoId, tenantId);
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.BcraUnavailable);
        }

        var keptStoredCheques = PersonSnapshotWriter.KeepStoredChequesOnFailure(bcraData, legajo.BcraDataJson);

        var now = DateTime.UtcNow;
        PersonSnapshotWriter.ApplyBcra(entity, legajo, bcraData, now);
        await SaveAsync(entity, legajo, now).ConfigureAwait(false);

        _logger.LogInformation(
            "BCRA data refreshed for legajo {PersonLegajoId} (entity {EntityId}) tenant {TenantId}; stored cheques kept after a failed cheques lookup: {KeptStoredCheques}",
            personLegajoId, entity?.EntityId, tenantId, keptStoredCheques);

        return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.Refreshed, legajo, entity?.EntityId);
    }

    /// <inheritdoc />
    public async Task<LegajoSnapshotRefreshOutcome> RefreshUnusableBcraAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var (legajo, entity) = await ResolveAsync(personLegajoId, tenantId, ct).ConfigureAwait(false);
        if (legajo is null)
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.LegajoNotFound);

        if (PersonSnapshotWriter.IsBcraSnapshotUsable(PersonSnapshotWriter.ReadBcra(legajo.BcraDataJson))
            || string.IsNullOrWhiteSpace(legajo.CUIT))
        {
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.NotNeeded, legajo, entity?.EntityId);
        }

        BcraData? fresh;
        try
        {
            fresh = await _bcraService.GetBcraDataForAnalysisAsync(legajo.CUIT, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "BCRA re-query failed for the evaluation of legajo {PersonLegajoId}, tenant {TenantId}; the stored snapshot is kept.",
                personLegajoId, tenantId);
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.BcraUnavailable, legajo, entity?.EntityId);
        }

        if (fresh?.Deuda is not { Estado: not BcraDeudaConsultaEstado.LookupFailed })
        {
            _logger.LogWarning(
                "BCRA re-query for the evaluation of legajo {PersonLegajoId}, tenant {TenantId}: the Central de Deudores " +
                "is still unavailable; the stored snapshot is kept.",
                personLegajoId, tenantId);
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.BcraUnavailable, legajo, entity?.EntityId);
        }

        var now = DateTime.UtcNow;
        PersonSnapshotWriter.ApplyBcra(entity, legajo, fresh, now);
        await SaveAsync(entity, legajo, now).ConfigureAwait(false);

        _logger.LogInformation(
            "BCRA snapshot of legajo {PersonLegajoId} (entity {EntityId}) refreshed for an evaluation, tenant {TenantId}: " +
            "deuda={DeudaEstado}, cheques={ChequesEstado}",
            personLegajoId, entity?.EntityId, tenantId, fresh.Deuda.Estado, fresh.Cheques?.Estado);

        return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.Refreshed, legajo, entity?.EntityId);
    }

    /// <inheritdoc />
    public async Task<LegajoSnapshotRefreshOutcome> RefreshArcaAsync(
        Guid personLegajoId, string tenantId, string? cuitRepresentada, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var (legajo, entity) = await ResolveAsync(personLegajoId, tenantId, ct).ConfigureAwait(false);
        if (legajo is null)
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.LegajoNotFound);

        if (string.IsNullOrWhiteSpace(cuitRepresentada))
        {
            _logger.LogWarning("ARCA refresh blocked for tenant {TenantId} — no CUIT configured", tenantId);
            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.ArcaNotConfigured);
        }

        // ARCA expects the CUIT without hyphens.
        var cuitNormalized = legajo.CUIT.Replace("-", "");

        var sw = Stopwatch.StartNew();
        try
        {
            var arcaData = await _arcaConstanciaService
                .GetPersonaByCuitAsync(cuitNormalized, cuitRepresentada, ct)
                .ConfigureAwait(false);

            sw.Stop();

            if (arcaData is null)
            {
                _ = LogArcaCallAsync(tenantId, false, (int)sw.ElapsedMilliseconds, null);
                return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.ArcaPersonNotFound);
            }

            // ARCA answered, so the call succeeded — but an answer without a fiscal block leaves the
            // stored snapshot as it is rather than overwriting a good one with null.
            _ = LogArcaCallAsync(tenantId, true, (int)sw.ElapsedMilliseconds, null);

            var now = DateTime.UtcNow;

            if (arcaData.FiscalData is null)
            {
                if (string.IsNullOrWhiteSpace(arcaData.EstadoClave))
                {
                    _logger.LogWarning(
                        "ARCA returned no fiscal data for legajo {PersonLegajoId} tenant {TenantId} — snapshot left unchanged",
                        personLegajoId, tenantId);
                    return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.ArcaNoFiscalData);
                }

                PersonSnapshotWriter.ApplyKeyStatusOnly(entity, legajo, arcaData.EstadoClave, now);
                await SaveAsync(entity, legajo, now).ConfigureAwait(false);

                // No CUIT, no name and no status text: the ids are enough to find the row.
                _logger.LogWarning(
                    "ARCA returned the key status but no fiscal section for legajo {PersonLegajoId} (entity {EntityId}) tenant {TenantId}; only the key status was stored.",
                    personLegajoId, entity?.EntityId, tenantId);

                return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.KeyStatusOnly, legajo, entity?.EntityId);
            }

            // As on the entity route: the person type is never changed by a refresh, and ARCA
            // disagreeing with an entity's is logged for a human to look at.
            var arcaPersonType = CuitHelper.PersonTypeFromArca(arcaData.TipoPersona);
            if (entity is not null && arcaPersonType is not null && arcaPersonType != entity.PersonType)
            {
                _logger.LogWarning(
                    "ARCA reports person type {ArcaPersonType} for entity {EntityId} tenant {TenantId}, stored as {PersonType}; refresh-arca never changes the person type.",
                    arcaPersonType, entity.EntityId, tenantId, entity.PersonType);
            }

            PersonSnapshotWriter.ApplyArca(entity, legajo, arcaData, now);
            await SaveAsync(entity, legajo, now).ConfigureAwait(false);

            _logger.LogInformation(
                "ARCA data refreshed for legajo {PersonLegajoId} (entity {EntityId}) tenant {TenantId}",
                personLegajoId, entity?.EntityId, tenantId);

            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.Refreshed, legajo, entity?.EntityId);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _ = LogArcaCallAsync(tenantId, false, (int)sw.ElapsedMilliseconds, ex.Message);

            _logger.LogError(ex, "ARCA API request failed for legajo {PersonLegajoId} tenant {TenantId}", personLegajoId, tenantId);

            return new LegajoSnapshotRefreshOutcome(LegajoSnapshotRefreshStatus.ArcaUnavailable);
        }
    }

    /// <summary>
    /// The legajo (full row) and, when it is an entity's, that entity — or (null, null) when the legajo
    /// is not reachable here: missing, another tenant's, or neither an entity's nor an ACTIVE fiador's.
    /// Resolved BEFORE any vendor call, so an unreachable legajo never costs a query.
    /// </summary>
    private async Task<(PersonLegajo? Legajo, Entity? Entity)> ResolveAsync(
        Guid personLegajoId, string tenantId, CancellationToken ct)
    {
        var summary = await _legajoDocumentRepository
            .GetLegajoSummaryAsync(personLegajoId, tenantId, ct)
            .ConfigureAwait(false);

        if (summary is null)
            return (null, null);

        if (summary.EntityId is null
            && !await _fiadorRepository.AnyActiveWithLegajoAsync(personLegajoId, tenantId, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Legajo {PersonLegajoId} tenant {TenantId} is neither an entity's nor an active fiador's; refresh refused.",
                personLegajoId, tenantId);
            return (null, null);
        }

        var legajo = await _personLegajoRepository
            .GetByIdAsync(personLegajoId, tenantId, ct)
            .ConfigureAwait(false);

        if (legajo is null)
            return (null, null);

        Entity? entity = null;
        if (summary.EntityId is Guid entityId)
        {
            entity = await _entityRepository.GetByIdAsync(entityId, tenantId, ct).ConfigureAwait(false);

            // Read a moment ago through the same link; gone now means a concurrent delete. The legajo
            // is still the person's, so it is refreshed alone.
            if (entity is null)
            {
                _logger.LogWarning(
                    "Legajo {PersonLegajoId} tenant {TenantId} links entity {EntityId}, which was not found; refreshing the legajo only.",
                    personLegajoId, tenantId, entityId);
            }
        }

        return (legajo, entity);
    }

    /// <summary>
    /// An entity's legajo is saved WITH the entity in one SaveChanges, as the entity route does; a
    /// fiador's legajo writes its snapshot columns only. Not cancellable: the vendor was already paid.
    /// </summary>
    private async Task SaveAsync(Entity? entity, PersonLegajo legajo, DateTime nowUtc)
    {
        legajo.UpdatedAt = nowUtc;

        if (entity is not null)
        {
            await _personLegajoRepository
                .SaveEntityWithLegajoAsync(entity, entityIsNew: false, legajo, legajoIsNew: false, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        await _personLegajoRepository
            .SaveLegajoSnapshotsAsync(legajo, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>The ApiCallLogs row of a getPersona_v2 call, the same shape EntitiesController writes.</summary>
    private Task LogArcaCallAsync(string tenantId, bool isSuccess, int durationMs, string? errorMessage)
    {
        return _apiCallLogService.LogAsync(
            tenantId:         tenantId,
            service:          "ARCA",
            endpoint:         "/ws_sr_constancia_inscripcion/getPersona_v2",
            responseStatus:   isSuccess ? 200 : 502,
            inputTokens:      null,
            outputTokens:     null,
            estimatedCostUsd: null,
            isSuccess:        isSuccess,
            errorMessage:     errorMessage,
            durationMs:       durationMs,
            ct:               CancellationToken.None);
    }
}
