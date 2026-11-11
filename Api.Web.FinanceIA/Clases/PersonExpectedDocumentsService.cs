// Project : Api.Web.FinanceIA
// Created : 2026-10-31

using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IPersonExpectedDocumentsService"/>, over
/// <see cref="ITenantSettingsRepository"/>.
///
/// <para>
/// No cache, as <see cref="SgrSettingsService"/>: TenantSettings reads are per request, so the GET after a PUT —
/// and the next evaluation — read the stored row, and there is nothing to invalidate.
/// </para>
/// </summary>
public sealed class PersonExpectedDocumentsService : IPersonExpectedDocumentsService
{
    /// <summary>The audit action of an update and of a reset.</summary>
    public const string AuditAction = "Settings.UpdatePersonExpectedDocuments";

    /// <summary>Error kind of a read that threw.</summary>
    public const string ErrorReadFailed = "read_failed";

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ITenantSettingsRepository                _settingsRepository;
    private readonly IPersonAnalysisRepository                _personAnalysisRepository;
    private readonly IAuditService                            _auditService;
    private readonly ILogger<PersonExpectedDocumentsService>  _logger;

    /// <summary>Initializes a new instance of <see cref="PersonExpectedDocumentsService"/>.</summary>
    public PersonExpectedDocumentsService(
        ITenantSettingsRepository               settingsRepository,
        IPersonAnalysisRepository               personAnalysisRepository,
        IAuditService                           auditService,
        ILogger<PersonExpectedDocumentsService> logger)
    {
        _settingsRepository       = settingsRepository;
        _personAnalysisRepository = personAnalysisRepository;
        _auditService             = auditService;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task<PersonExpectedDocumentsResolved> GetResolvedAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        TenantSettingEntry? entry;
        try
        {
            entry = await _settingsRepository
                .GetEntryAsync(tenantId, TenantSettingKeys.PersonExpectedDocuments, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUnusable(ex, tenantId, ErrorReadFailed);
            return PersonExpectedDocumentsResolved.Defaults;
        }

        if (entry is null)
            return PersonExpectedDocumentsResolved.Defaults;

        if (!PersonExpectedDocumentsMatrix.TryParseStored(entry.Value, out var regimes, out var errorKind))
        {
            LogUnusable(null, tenantId, errorKind ?? PersonExpectedDocumentsMatrix.ErrorInvalid);
            return PersonExpectedDocumentsResolved.Defaults;
        }

        return new PersonExpectedDocumentsResolved
        {
            UpdatedAt = entry.UpdatedAt,
            Regimes   = ExpectedDocumentRegimes.All
                .Select(regime => regimes.TryGetValue(regime, out var lines)
                    ? new PersonExpectedDocumentsForRegime
                    {
                        Regime    = regime,
                        Source    = ExpectedDocumentsSources.Tenant,
                        UpdatedAt = entry.UpdatedAt,
                        Lines     = lines
                    }
                    : new PersonExpectedDocumentsForRegime
                    {
                        Regime = regime,
                        Source = ExpectedDocumentsSources.Default,
                        Lines  = LegajoExpectedDocuments.Defaults[regime]
                    })
                .ToList()
        };
    }

    /// <inheritdoc />
    public async Task<PersonExpectedDocumentsForRegime> ForAsync(string tenantId, string? fiscalRegime, CancellationToken ct)
    {
        var resolved = await GetResolvedAsync(tenantId, ct).ConfigureAwait(false);
        return resolved.For(fiscalRegime);
    }

    /// <inheritdoc />
    public async Task<PersonExpectedDocumentsSettingsDto> GetViewAsync(string tenantId, CancellationToken ct)
    {
        var resolved = await GetResolvedAsync(tenantId, ct).ConfigureAwait(false);
        return await BuildViewAsync(tenantId, resolved, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersonExpectedDocumentsSettingsDto> UpdateAsync(
        string tenantId,
        IReadOnlyDictionary<string, List<PersonExpectedDocumentLineDto?>?>? regimes,
        Guid userId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var errors = PersonExpectedDocumentsMatrix.Validate(regimes);
        if (errors.Count > 0)
            throw new ValidationException(errors);

        // Read BEFORE the write, for the audit trail — after it the old matrix is gone.
        var before = await GetResolvedAsync(tenantId, ct).ConfigureAwait(false);

        var value = PersonExpectedDocumentsMatrix.Serialize(regimes!);
        await _settingsRepository
            .SetValuesAsync(tenantId, new Dictionary<string, string?> { [TenantSettingKeys.PersonExpectedDocuments] = value }, ct)
            .ConfigureAwait(false);

        var after = await GetResolvedAsync(tenantId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Person expected documents updated for TenantId: {TenantId} ({RegimeCount} regimes, {LineCount} lines)",
            tenantId, regimes!.Count, regimes.Values.Sum(l => l?.Count ?? 0));

        await AuditAsync(tenantId, userId, ipAddress, requestId, before, after, isReset: false, ct).ConfigureAwait(false);

        return await BuildViewAsync(tenantId, after, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PersonExpectedDocumentsSettingsDto> ResetAsync(
        string tenantId,
        Guid userId,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var before = await GetResolvedAsync(tenantId, ct).ConfigureAwait(false);

        // A null value deletes the row (a no-op when there is none).
        await _settingsRepository
            .SetValuesAsync(tenantId, new Dictionary<string, string?> { [TenantSettingKeys.PersonExpectedDocuments] = null }, ct)
            .ConfigureAwait(false);

        var after = PersonExpectedDocumentsResolved.Defaults;

        _logger.LogInformation("Person expected documents reset to the code defaults for TenantId: {TenantId}", tenantId);

        await AuditAsync(tenantId, userId, ipAddress, requestId, before, after, isReset: true, ct).ConfigureAwait(false);

        return await BuildViewAsync(tenantId, after, ct).ConfigureAwait(false);
    }

    private async Task<PersonExpectedDocumentsSettingsDto> BuildViewAsync(
        string tenantId, PersonExpectedDocumentsResolved resolved, CancellationToken ct)
    {
        var configRows = await _personAnalysisRepository.GetEvaluationConfigAsync(tenantId, ct).ConfigureAwait(false);
        var parameters = PersonEvaluationParameters.From(configRows);

        return new PersonExpectedDocumentsSettingsDto
        {
            Regimes       = resolved.Regimes.Select(r => RegimeView(r, parameters)).ToList(),
            Defaults      = PersonExpectedDocumentsResolved.Defaults.Regimes.Select(r => RegimeView(r, parameters)).ToList(),
            DocumentTypes = LegajoExpectedDocuments.ConfigurableTypes,
            UpdatedAt     = resolved.UpdatedAt
        };
    }

    private static PersonExpectedDocumentsRegimeDto RegimeView(
        PersonExpectedDocumentsForRegime regime, PersonEvaluationParameters parameters) => new()
    {
        Regime = regime.Regime,
        Source = regime.Source,
        Lines  = regime.Lines
            .Select(line => new PersonExpectedDocumentLineViewDto
            {
                DocumentType          = line.DocumentType,
                Requirement           = line.Required
                    ? PersonExpectedDocumentsMatrix.RequirementRequired
                    : PersonExpectedDocumentsMatrix.RequirementOptional,
                Alternatives          = line.Alternatives,
                MaxAgeMonths          = line.MaxAgeMonths,
                EffectiveMaxAgeMonths = LegajoDocumentAgeClasses.EffectiveMaxAge(line, parameters),
                AgeClass              = LegajoDocumentAgeClasses.Of(line.DocumentType)
            })
            .ToList()
    };

    private async Task AuditAsync(
        string tenantId, Guid userId, string? ipAddress, string? requestId,
        PersonExpectedDocumentsResolved before, PersonExpectedDocumentsResolved after, bool isReset, CancellationToken ct)
    {
        // AuditService owns its try/catch and never propagates, so awaiting it cannot fail the write that
        // already committed.
        await _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       AuditAction,
            entityType:   "TenantSettings",
            entityId:     TenantSettingKeys.PersonExpectedDocuments,
            ipAddress:    ipAddress,
            requestId:    requestId,
            oldValueJson: AuditJson(before, reset: null),
            newValueJson: AuditJson(after, reset: isReset ? true : null),
            ct:           ct).ConfigureAwait(false);
    }

    private static string AuditJson(PersonExpectedDocumentsResolved matrix, bool? reset)
        => JsonSerializer.Serialize(new
        {
            reset,
            updatedAt = matrix.UpdatedAt,
            regimes   = matrix.Regimes.Select(r => new
            {
                regime = r.Regime,
                source = r.Source,
                lines  = r.Lines.Select(l => new
                {
                    documentType = l.DocumentType,
                    requirement  = l.Required
                        ? PersonExpectedDocumentsMatrix.RequirementRequired
                        : PersonExpectedDocumentsMatrix.RequirementOptional,
                    alternatives = l.Alternatives,
                    maxAgeMonths = l.MaxAgeMonths
                })
            })
        }, AuditJsonOptions);

    private void LogUnusable(Exception? ex, string tenantId, string errorKind)
    {
        // The value is operator input of unknown shape: the key and the kind are logged, never the value.
        _logger.LogWarning(
            ex,
            "Tenant setting {SettingKey} of tenant {TenantId} is not usable ({ErrorKind}); the code defaults apply to every regime.",
            TenantSettingKeys.PersonExpectedDocuments, tenantId, errorKind);
    }
}
