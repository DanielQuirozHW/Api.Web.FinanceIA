// Project : Api.Web.FinanceIA
// Created : 2026-05-11

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Time;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// EF Core implementation of <see cref="ISettingsService"/>.
/// Read queries use AsNoTracking. Write operations call
/// ChangeTracker.DetectChanges() manually because AutoDetectChangesEnabled
/// is disabled on <see cref="FinanciaDbContext"/>.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    // ── Dependencies ────────────────────────────────────────────────────────────

    private static readonly NumberFormatInfo EsArNumberFormat = new() { NumberDecimalSeparator = "," };

    private readonly FinanciaDbContext _dbContext;
    private readonly IAuditService _auditService;
    private readonly ITenantProfileService _tenantProfileService;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(
        FinanciaDbContext dbContext,
        IAuditService auditService,
        ITenantProfileService tenantProfileService,
        ILogger<SettingsService> logger)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _tenantProfileService = tenantProfileService;
        _logger = logger;
    }

    // ── Tenant profile ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<TenantProfileDto> GetTenantProfileAsync(string tenantId, CancellationToken ct)
    {
        var resolution = await _tenantProfileService
            .GetEffectiveAsync(tenantId, ct)
            .ConfigureAwait(false);

        return ToDto(resolution);
    }

    /// <inheritdoc />
    public async Task<TenantProfileDto> UpdateTenantProfileAsync(
        Guid userId,
        string tenantId,
        UpdateTenantProfileRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read BEFORE the write, for the audit trail: "what did it change from" is the question an
        // auditor asks first, and after the upsert the old value is gone.
        var before = await _tenantProfileService
            .GetEffectiveAsync(tenantId, ct)
            .ConfigureAwait(false);

        // Throws ArgumentException on an unknown entity type, and propagates a failed write. The
        // controller maps the former to a 400; anything else is a 500, which is correct — an
        // operator must not be told the profile was saved when it was not.
        var after = await _tenantProfileService
            .SetEntityTypeAsync(tenantId, request.EntityType, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Tenant profile updated from {OldEntityType} to {NewEntityType} for TenantId: {TenantId}",
            before.Profile.EntityType,
            after.Profile.EntityType,
            tenantId);

        // AuditService owns its try/catch and never propagates, so awaiting it cannot fail the
        // write that already committed above.
        await _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Settings.UpdateTenantProfile",
            entityType:   "TenantSettings",
            entityId:     TenantSettingKeys.EntityType,
            ipAddress:    ipAddress,
            requestId:    requestId,
            oldValueJson: SerializeProfile(before),
            newValueJson: SerializeProfile(after),
            ct:           ct).ConfigureAwait(false);

        return ToDto(after);
    }

    private static TenantProfileDto ToDto(TenantProfileResolution resolution) => new()
    {
        EntityType    = resolution.Profile.EntityType,
        Source        = resolution.Source.ToString(),
        LufeEnabled   = resolution.Profile.LufeEnabled,
        CasfogEnabled = resolution.Profile.CasfogEnabled,
        NosisEnabled  = resolution.Profile.NosisEnabled
    };

    private static string SerializeProfile(TenantProfileResolution resolution) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            entityType    = resolution.Profile.EntityType,
            source        = resolution.Source.ToString(),
            lufeEnabled   = resolution.Profile.LufeEnabled,
            casfogEnabled = resolution.Profile.CasfogEnabled,
            nosisEnabled  = resolution.Profile.NosisEnabled
        });

    /// <inheritdoc />
    public async Task<ProfileDto?> GetProfileAsync(
        Guid userId,
        string tenantId,
        CancellationToken ct)
    {
        return await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.UserId == userId && u.TenantId == tenantId)
            .Select(u => new ProfileDto
            {
                UserId = u.UserId,
                DisplayName = u.DisplayName,
                Email = u.Email,
                Role = u.Role,
                LastLoginAt = u.LastLoginAt
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProfileDto?> UpdateProfileAsync(
        Guid userId,
        string tenantId,
        UpdateProfileRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.UserId == userId && u.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        if (user is null)
            return null;

        user.DisplayName = request.DisplayName.Trim();
        user.UpdatedAt = DateTime.UtcNow;

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Profile updated for UserId: {UserId}, TenantId: {TenantId}",
            user.UserId,
            tenantId);

        // Fire-and-forget audit (IAuditService catches all exceptions internally)
        await _auditService.LogAsync(
            tenantId: tenantId,
            userId: user.UserId,
            action: "User.UpdateProfile",
            entityType: "User",
            entityId: user.UserId.ToString(),
            ipAddress: ipAddress,
            requestId: requestId,
            ct: ct).ConfigureAwait(false);

        return new ProfileDto
        {
            UserId = user.UserId,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Role = user.Role,
            LastLoginAt = user.LastLoginAt
        };
    }

    /// <inheritdoc />
    public async Task<List<UserDto>> GetUsersAsync(
        string tenantId,
        CancellationToken ct)
    {
        // The tenant's teams once, up front. Users.TeamId is a bare Guid? with no EF navigation —
        // FK_Users_Team lives in the database only — so the name is resolved from this dictionary
        // in memory rather than with a join per user. Two queries in total, whatever the roster size.
        var teamNames = await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new { t.TeamId, t.Name })
            .ToDictionaryAsync(t => t.TeamId, t => t.Name, ct)
            .ConfigureAwait(false);

        var users = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new
            {
                u.UserId,
                u.DisplayName,
                u.Email,
                u.Role,
                u.IsActive,
                u.LastLoginAt,
                u.TeamId
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return users
            .Select(u => new UserDto
            {
                UserId = u.UserId,
                DisplayName = u.DisplayName,
                Email = u.Email,
                Role = u.Role,
                IsActive = u.IsActive,
                LastLoginAt = u.LastLoginAt,
                // A row in Users has completed registration by definition.
                InvitationStatus = InvitationStatusResolver.Accepted,
                TeamId = u.TeamId,
                // Null when unassigned, and also when the id names no team of this tenant — a team
                // deleted between the two queries must lose the name, never fail the listing.
                TeamName = u.TeamId is Guid teamId && teamNames.TryGetValue(teamId, out var name)
                    ? name
                    : null
            })
            .ToList();
    }

    // ── Scoring config ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ScoringConfigDto> GetScoringConfigAsync(
        string tenantId,
        CancellationToken ct)
    {
        var rows = await _dbContext.ScoringConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var merged = MergeWithDefaults(rows);
        var storedKeys = rows.Select(r => r.ConfigKey).ToHashSet(StringComparer.Ordinal);
        return BuildDto(merged, storedKeys);
    }

    /// <inheritdoc />
    public async Task<ScoringConfigDto?> UpdateScoringConfigAsync(
        Guid userId,
        string tenantId,
        UpdateScoringConfigRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        // Reject unknown keys. The pf_ keys of the persona humana evaluation (M10 Phase 4a) live in the same
        // table and are written through this same PUT; BuildDto surfaces them in their own personaHumana array.
        foreach (var item in request.Items)
        {
            if (!ScoringDefaults.Values.ContainsKey(item.Key) && !PersonEvaluationDefaults.IsKey(item.Key))
                return null;
        }

        ValidateUpdateItems(request.Items);

        // Build post-merge state: defaults → DB → submitted values. A reset puts the code default back.
        var existingRows = await _dbContext.ScoringConfigs
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var merged = MergeWithDefaults(existingRows);
        var personKeys = request.Items
            .Select(i => i.Key)
            .Where(PersonEvaluationDefaults.IsKey)
            .ToList();
        var personBefore = personKeys.ToDictionary(k => k, k => merged[k].Value, StringComparer.Ordinal);

        foreach (var item in request.Items)
        {
            var value = IsReset(item) ? PersonEvaluationDefaults.Get(item.Key) : item.Value!.Value;
            merged[item.Key] = (value, merged[item.Key].Description);
        }

        // A request made only of pf_ keys validates only the persona humana config (merged with the
        // tenant's stored values), so a balance problem stored before this PUT — which the request does
        // not touch — cannot block a persona-only save. Any balance key in the request brings back the
        // full validation below, unchanged.
        bool isPersonOnly = personKeys.Count == request.Items.Count;
        if (isPersonOnly)
        {
            ValidatePersonEvaluationConfig(merged);
        }
        else
        {
            // Validate the full post-merge config
            ValidateScoringConfig(merged);

            // Threshold ordering, the same rule the sector PUT has always enforced — base saves used to skip
            // it, so a partial PUT (a lone danger row under the default warn) could store a triplet the
            // engine then had to refuse. Checked on the base triplets AND on every sector profile layered
            // over them: a base change can invert a sector whose override row sits on the other side of it.
            ValidateThresholdCoherence(merged);
            foreach (var sector in SectorProfiles.KnownSectors)
            {
                if (sector == "General") continue;

                var suffix = SectorProfiles.SectorSuffix(sector);
                if (existingRows.Any(r => r.ConfigKey.EndsWith(suffix, StringComparison.Ordinal)))
                    ValidateThresholdCoherence(OverlaySectorRows(merged, existingRows, sector), sector);
            }
        }

        // Persist changes: update existing or insert new rows; a reset deletes the row, if any.
        var existingByKey = existingRows.ToDictionary(r => r.ConfigKey);
        var now = DateTime.UtcNow;

        foreach (var item in request.Items)
        {
            if (IsReset(item))
            {
                if (existingByKey.Remove(item.Key, out var resetRow))
                    _dbContext.ScoringConfigs.Remove(resetRow);

                continue;
            }

            if (existingByKey.TryGetValue(item.Key, out var row))
            {
                row.ConfigValue = item.Value!.Value;
                row.UpdatedAt = now;
            }
            else
            {
                _dbContext.ScoringConfigs.Add(new ScoringConfig
                {
                    ConfigId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ConfigKey = item.Key,
                    ConfigValue = item.Value!.Value,
                    Description = ScoringDefaults.Descriptions.TryGetValue(item.Key, out var description)
                        ? description
                        : PersonEvaluationDefaults.Descriptions[item.Key],
                    UpdatedAt = now
                });
            }
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Scoring config updated ({Count} keys, {ResetCount} reset) for TenantId: {TenantId}",
            request.Items.Count,
            request.Items.Count(IsReset),
            tenantId);

        // Audit. The pf_ keys carry their effective value before and after (a reset shows the default it
        // went back to) — keys and numbers only. A request with no pf_ key keeps the bare entry it always had.
        var personAfter = personKeys.ToDictionary(k => k, k => merged[k].Value, StringComparer.Ordinal);

        await _auditService.LogAsync(
            tenantId: tenantId,
            userId: userId,
            action: "Settings.UpdateScoringConfig",
            entityType: "ScoringConfig",
            entityId: tenantId,
            ipAddress: ipAddress,
            requestId: requestId,
            oldValueJson: personKeys.Count == 0 ? null : JsonSerializer.Serialize(personBefore),
            newValueJson: personKeys.Count == 0 ? null : JsonSerializer.Serialize(personAfter),
            ct: ct).ConfigureAwait(false);

        // Return fresh read
        return await GetScoringConfigAsync(tenantId, ct).ConfigureAwait(false);
    }

    // ── Sector profiles ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<SectorListItemDto>> GetSectorsAsync(
        string tenantId, CancellationToken ct)
    {
        var rows = await _dbContext.ScoringConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ConfigKey.Contains("__sector__"))
            .Select(c => c.ConfigKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sectorsWithOverrides = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in rows)
        {
            foreach (var sector in SectorProfiles.KnownSectors)
            {
                if (sector == "General") continue;
                if (key.EndsWith(SectorProfiles.SectorSuffix(sector), StringComparison.Ordinal))
                {
                    sectorsWithOverrides.Add(sector);
                    break;
                }
            }
        }

        return SectorProfiles.KnownSectors
            .Select(s => new SectorListItemDto
            {
                Sector = s,
                HasOverrides = sectorsWithOverrides.Contains(s)
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SectorProfileDto> GetSectorProfileAsync(
        string tenantId, string sector, CancellationToken ct)
    {
        var canonicalSector = SectorProfiles.Canonicalize(sector);

        var rows = await _dbContext.ScoringConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Base: non-sector rows merged with defaults
        var baseRows = rows.Where(c => !c.ConfigKey.Contains("__sector__")).ToList();
        var merged = MergeWithDefaults(baseRows);

        // Overlay sector-specific rows
        if (canonicalSector != "General")
        {
            var suffix = SectorProfiles.SectorSuffix(canonicalSector);
            foreach (var row in rows)
            {
                if (row.ConfigKey.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var baseKey = row.ConfigKey[..^suffix.Length];
                    if (merged.ContainsKey(baseKey))
                        merged[baseKey] = (row.ConfigValue, merged[baseKey].Description);
                }
            }
        }

        var thresholds = ScoringDefaults.RatioThresholdKeys
            .Select(k => new ScoringConfigItemDto
            {
                Key = k,
                Value = merged[k].Value,
                Description = merged[k].Description
            })
            .ToList();

        return new SectorProfileDto
        {
            Sector = canonicalSector,
            Thresholds = thresholds
        };
    }

    /// <inheritdoc />
    public async Task<SectorProfileDto?> UpdateSectorProfileAsync(
        Guid userId,
        string tenantId,
        string sector,
        UpdateSectorProfileRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct)
    {
        var canonicalSector = SectorProfiles.Canonicalize(sector);
        if (canonicalSector == "General")
            return null;

        var thresholdKeySet = new HashSet<string>(ScoringDefaults.RatioThresholdKeys);

        // Reject unrecognized keys
        foreach (var item in request.Thresholds)
        {
            if (!thresholdKeySet.Contains(item.Key))
                return null;
        }

        // Build effective state for coherence validation
        var existingRows = await _dbContext.ScoringConfigs
            .Where(c => c.TenantId == tenantId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var baseRows = existingRows.Where(c => !c.ConfigKey.Contains("__sector__")).ToList();
        var merged = OverlaySectorRows(MergeWithDefaults(baseRows), existingRows, canonicalSector);
        var suffix = SectorProfiles.SectorSuffix(canonicalSector);

        // Apply submitted values
        foreach (var item in request.Thresholds)
            merged[item.Key] = (item.Value, merged[item.Key].Description);

        // Coherence validation: threshold ordering per ratio
        ValidateThresholdCoherence(merged, canonicalSector);

        // Upsert sector-suffixed rows
        var existingByKey = existingRows.ToDictionary(r => r.ConfigKey);
        var now = DateTime.UtcNow;

        foreach (var item in request.Thresholds)
        {
            var suffixedKey = item.Key + suffix;
            var baseDesc = ScoringDefaults.Descriptions.TryGetValue(item.Key, out var desc) ? desc : item.Key;

            if (existingByKey.TryGetValue(suffixedKey, out var row))
            {
                row.ConfigValue = item.Value;
                row.Description = $"{baseDesc} — sector {canonicalSector}";
                row.UpdatedAt = now;
            }
            else
            {
                _dbContext.ScoringConfigs.Add(new ScoringConfig
                {
                    ConfigId = Guid.NewGuid(),
                    TenantId = tenantId,
                    ConfigKey = suffixedKey,
                    ConfigValue = item.Value,
                    Description = $"{baseDesc} — sector {canonicalSector}",
                    UpdatedAt = now
                });
            }
        }

        _dbContext.ChangeTracker.DetectChanges();
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Sector profile updated ({Count} keys, sector {Sector}) for TenantId: {TenantId}",
            request.Thresholds.Count, canonicalSector, tenantId);

        // Audit
        await _auditService.LogAsync(
            tenantId: tenantId,
            userId: userId,
            action: "Settings.UpdateSectorProfile",
            entityType: "ScoringConfig",
            entityId: $"{tenantId}:{canonicalSector}",
            ipAddress: ipAddress,
            requestId: requestId,
            ct: ct).ConfigureAwait(false);

        return await GetSectorProfileAsync(tenantId, canonicalSector, ct).ConfigureAwait(false);
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private static Dictionary<string, (decimal Value, string? Description)> MergeWithDefaults(
        List<ScoringConfig> dbRows)
    {
        var merged = new Dictionary<string, (decimal Value, string? Description)>(
            ScoringDefaults.Values.Count + PersonEvaluationDefaults.Values.Count);

        foreach (var (key, value) in ScoringDefaults.Values)
            merged[key] = (value, ScoringDefaults.Descriptions[key]);

        // The persona humana pf_ keys join the merged map so a PUT validates them against the stored state.
        // BuildDto names its groups explicitly: they surface only in its personaHumana array.
        foreach (var (key, value) in PersonEvaluationDefaults.Values)
            merged[key] = (value, PersonEvaluationDefaults.Descriptions[key]);

        foreach (var row in dbRows)
        {
            if (merged.ContainsKey(row.ConfigKey))
                merged[row.ConfigKey] = (row.ConfigValue, row.Description ?? merged[row.ConfigKey].Description);
        }

        return merged;
    }

    private static ScoringConfigDto BuildDto(
        Dictionary<string, (decimal Value, string? Description)> merged,
        IReadOnlySet<string> storedKeys)
    {
        var today = TodayInArgentina();

        ScoringConfigItemDto ToItem(string key) => new()
        {
            Key = key,
            Value = merged[key].Value,
            Description = merged[key].Description
        };

        return new ScoringConfigDto
        {
            RatioWeights = ScoringDefaults.RatioWeightKeys.Select(ToItem).ToList(),
            RatioThresholds = ScoringDefaults.RatioThresholdKeys.Select(ToItem).ToList(),
            ComponentWeights = ScoringDefaults.ComponentWeightKeys.Select(ToItem).ToList(),
            ScoreThresholds = ScoringDefaults.ScoreThresholdKeys.Select(ToItem).ToList(),
            Macro = ScoringDefaults.MacroKeys.Select(ToItem).ToList(),
            FloorRules = ScoringDefaults.ProfitabilityFloorKeys
                .Concat(ScoringDefaults.AcidTestFloorKeys)
                .Select(ToItem)
                .ToList(),
            PersonaHumana = PersonEvaluationDefaults.Layout.Select(entry =>
            {
                var (kind, min, max) = PersonEvaluationDefaults.BoundsOf(entry.Key, today);
                return new PersonEvaluationConfigItemDto
                {
                    Key = entry.Key,
                    Value = merged[entry.Key].Value,
                    DefaultValue = PersonEvaluationDefaults.Get(entry.Key),
                    IsOverride = storedKeys.Contains(entry.Key),
                    Kind = kind,
                    Min = min,
                    Max = max,
                    Unit = entry.Unit,
                    Group = entry.Group,
                    Description = merged[entry.Key].Description
                };
            }).ToList(),
        };
    }

    private static bool IsReset(ScoringConfigUpdateItem item) => item.Reset == true;

    /// <summary>
    /// The shape of the PUT items, checked before anything is read: a reset only on a pf_ key, a value on
    /// every other item, and no pf_ key twice — two inserts of one key would break the (TenantId,
    /// ConfigKey) unique index, and a set and a reset of the same key contradict each other.
    /// </summary>
    private static void ValidateUpdateItems(IReadOnlyList<ScoringConfigUpdateItem> items)
    {
        var seenPersonKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            bool isPersonKey = PersonEvaluationDefaults.IsKey(item.Key);

            if (IsReset(item) && !isPersonKey)
                throw new InvalidOperationException(
                    $"reset solo se admite para las claves pf_ (recibido: {item.Key}).");

            if (!IsReset(item) && item.Value is null)
                throw new InvalidOperationException($"Falta el valor de {item.Key}.");

            if (isPersonKey && !seenPersonKeys.Add(item.Key))
                throw new InvalidOperationException($"La clave {item.Key} aparece más de una vez en la solicitud.");
        }
    }

    /// <summary>
    /// Validates that for each of the 8 ratios, threshold ordering is coherent:
    /// higher-is-better (ok &gt; danger): ok &gt; warn &gt; danger;
    /// lower-is-better (ok &lt; danger): ok &lt; warn &lt; danger.
    /// The rule itself is <see cref="ScoringDefaults.IsThresholdTripletOrdered"/>, which the scoring
    /// engine applies too — one rule, so what this refuses to store is exactly what the engine refuses
    /// to score on. <paramref name="sector"/> only names the profile in the message.
    /// </summary>
    private static void ValidateThresholdCoherence(
        Dictionary<string, (decimal Value, string? Description)> values,
        string? sector = null)
    {
        foreach (var ratio in ScoringDefaults.ThresholdRatios)
        {
            var okKey     = $"threshold_{ratio}_ok";
            var warnKey   = $"threshold_{ratio}_warn";
            var dangerKey = $"threshold_{ratio}_danger";

            if (!values.ContainsKey(okKey)) continue;

            var ok     = values[okKey].Value;
            var warn   = values[warnKey].Value;
            var danger = values[dangerKey].Value;

            if (ScoringDefaults.IsThresholdTripletOrdered(ratio, ok, warn, danger))
                continue;

            var scope = sector is null ? string.Empty : $" in sector profile '{sector}'";

            throw new InvalidOperationException(
                ScoringDefaults.Get(okKey) > ScoringDefaults.Get(dangerKey)
                    ? $"Threshold ordering for '{ratio}'{scope} (higher-is-better) requires ok ({ok}) > warn ({warn}) > danger ({danger})."
                    : $"Threshold ordering for '{ratio}'{scope} (lower-is-better) requires ok ({ok}) < warn ({warn}) < danger ({danger}).");
        }
    }

    /// <summary>
    /// A copy of <paramref name="baseMerged"/> with <paramref name="canonicalSector"/>'s
    /// <c>__sector__</c> rows laid over it — the same overlay <c>ScoringService.LoadConfigAsync</c>
    /// applies when it scores an entity of that sector.
    /// </summary>
    private static Dictionary<string, (decimal Value, string? Description)> OverlaySectorRows(
        Dictionary<string, (decimal Value, string? Description)> baseMerged,
        List<ScoringConfig> rows,
        string canonicalSector)
    {
        var overlaid = new Dictionary<string, (decimal Value, string? Description)>(baseMerged);
        var suffix   = SectorProfiles.SectorSuffix(canonicalSector);

        foreach (var row in rows)
        {
            if (!row.ConfigKey.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var baseKey = row.ConfigKey[..^suffix.Length];
            if (overlaid.ContainsKey(baseKey))
                overlaid[baseKey] = (row.ConfigValue, overlaid[baseKey].Description);
        }

        return overlaid;
    }

    private static void ValidateScoringConfig(
        Dictionary<string, (decimal Value, string? Description)> values)
    {
        // All values must be >= 0, EXCEPT the handful that are signed by construction. The
        // sales-variation thresholds (§7.3a) measure a DROP, so their whole meaningful range is
        // negative and the shipped default for lufe_ventas_umbral_plena is -0.35. This loop walks the
        // MERGED map — defaults overlaid with the tenant's rows — so without the exemption those
        // defaults would fail validation on every PUT, for every tenant, on every key. The exempt
        // keys are not left unchecked: the block below bounds them into [-1,0], a stricter rule than
        // the one they skip here.
        foreach (var (key, entry) in values)
        {
            if (ScoringDefaults.SignedScoringKeys.Contains(key))
                continue;

            if (entry.Value < 0)
                throw new InvalidOperationException(
                    $"Configuration value for '{key}' must be greater than or equal to zero.");
        }

        // Ratio weights must sum to 1.0
        decimal ratioWeightSum = ScoringDefaults.RatioWeightKeys.Sum(k => values[k].Value);
        if (Math.Abs(ratioWeightSum - 1.0m) > 0.001m)
            throw new InvalidOperationException(
                $"Ratio weights must sum to 1.0 (current sum: {ratioWeightSum}).");

        // Component weights must sum to 1.0
        decimal componentWeightSum = ScoringDefaults.ComponentWeightKeys.Sum(k => values[k].Value);
        if (Math.Abs(componentWeightSum - 1.0m) > 0.001m)
            throw new InvalidOperationException(
                $"Component weights must sum to 1.0 (current sum: {componentWeightSum}).");

        // Component weights must also be COMPUTABLE, not merely normalised. Summing to 1.0 does
        // not make a trio usable: 0 / 1.00 / 0 sums to 1 and passes the check above, but when BCRA
        // is excluded the engine renormalises over ratios+macro only — and both are zero, so there
        // is nothing left to weight and every company scores 0.00. Requiring two non-zero weights
        // is exactly the condition that cannot happen: with the sum pinned at 1.0, "ratios == 0
        // and macro == 0" is the same statement as "bcra is the only non-zero weight".
        decimal wRatios = values["weight_component_ratios"].Value;
        decimal wBcra   = values["weight_component_bcra"].Value;
        decimal wMacro  = values["weight_component_macro"].Value;

        int nonZeroComponents = ScoringDefaults.ComponentWeightKeys.Count(k => values[k].Value > 0m);
        if (nonZeroComponents < 2)
            throw new InvalidOperationException(
                "At least two of the three component weights must be greater than zero " +
                $"(current: ratios {wRatios}, bcra {wBcra}, macro {wMacro}).");

        // Every critical-floor key is a point on the 0-100 score scale: the eight per-ratio keys
        // floor the final score, and critical_floor_trigger_threshold is compared against a partial
        // score, which PartialScore bounds to [0,100] by construction. A value outside that range
        // is not a floor — 150 would carry the final score clean off the scale.
        foreach (var floorKey in ScoringDefaults.CriticalFloorKeys)
        {
            decimal floorValue = values[floorKey].Value;
            if (floorValue < 0m || floorValue > 100m)
                throw new InvalidOperationException(
                    $"Configuration value for '{floorKey}' must be between 0 and 100 " +
                    $"(current: {floorValue}).");
        }

        // The profitability-floor rule (algorithm version 10) is a BOOLEAN with nowhere boolean to live,
        // exactly like bcra_cheques_enabled: the engine reads "> 0" as on, so 0.5 or 7 would silently
        // enable it. Rejecting them keeps the stored config honest about what the key is.
        foreach (var profitabilityKey in ScoringDefaults.ProfitabilityFloorKeys)
        {
            decimal profitabilityValue = values[profitabilityKey].Value;
            if (profitabilityValue != 0m && profitabilityValue != 1m)
                throw new InvalidOperationException(
                    $"Configuration value for '{profitabilityKey}' must be 0 (off) or 1 (on) " +
                    $"(current: {profitabilityValue}).");
        }

        // The acid-test-floor rule (algorithm version 11): the same kind of BOOLEAN, the same reason.
        foreach (var acidTestKey in ScoringDefaults.AcidTestFloorKeys)
        {
            decimal acidTestValue = values[acidTestKey].Value;
            if (acidTestValue != 0m && acidTestValue != 1m)
                throw new InvalidOperationException(
                    $"Configuration value for '{acidTestKey}' must be 0 (off) or 1 (on) " +
                    $"(current: {acidTestValue}).");
        }

        // The rejected-cheques penalty (§7.1). Two rules, because the two keys are different kinds of
        // number sharing one decimal(10,4) column:
        //
        //  · bcra_cheques_penalizacion_plena is a number of POINTS on the 0-100 scoreBcra scale, so the
        //    same bound the critical floors carry applies — 150 is not a penalty, it is a bug. The
        //    engine clamps it again, exactly as it does a floor.
        //  · bcra_cheques_enabled is a BOOLEAN with nowhere boolean to live: ConfigValue is decimal, so
        //    the flag is stored as 0 or 1. The engine reads "> 0" as on, which would silently accept
        //    0.5 or 7 as "enabled" — values whose author plainly meant something else. Rejecting them
        //    here keeps the stored config honest about what the key is.
        foreach (var chequesKey in ScoringDefaults.BcraChequesKeys)
        {
            decimal chequesValue = values[chequesKey].Value;

            if (chequesKey == "bcra_cheques_enabled")
            {
                if (chequesValue != 0m && chequesValue != 1m)
                    throw new InvalidOperationException(
                        $"Configuration value for '{chequesKey}' must be 0 (off) or 1 (on) " +
                        $"(current: {chequesValue}).");
            }
            else if (chequesValue < 0m || chequesValue > 100m)
            {
                throw new InvalidOperationException(
                    $"Configuration value for '{chequesKey}' must be between 0 and 100 " +
                    $"(current: {chequesValue}).");
            }
        }

        // The LUFE sales-drop penalty (§7.3a). Three kinds of number share the one decimal(10,4)
        // column, so each gets its own rule:
        //
        //  · lufe_ventas_penalizacion_plena is a number of POINTS on the 0-100 final-score scale —
        //    the same bound the critical floors and the cheques plena carry.
        //  · lufe_ventas_umbral_inicio and lufe_ventas_umbral_plena are signed VARIATION FRACTIONS.
        //    A drop is negative, and -1 is a total collapse to zero sales, so [-1,0] is the whole
        //    meaningful range; a positive threshold would make growth penalise, inverting the
        //    signal's entire premise. These are the keys exempted from the blanket >= 0 rule above.
        //  · lufe_ventas_enabled is a BOOLEAN with nowhere boolean to live, exactly like
        //    bcra_cheques_enabled: the engine reads "> 0" as on, so 0.5 or 7 would silently enable it.
        foreach (var lufeKey in ScoringDefaults.LufeVentasKeys)
        {
            decimal lufeValue = values[lufeKey].Value;

            if (lufeKey == "lufe_ventas_enabled")
            {
                if (lufeValue != 0m && lufeValue != 1m)
                    throw new InvalidOperationException(
                        $"Configuration value for '{lufeKey}' must be 0 (off) or 1 (on) " +
                        $"(current: {lufeValue}).");
            }
            else if (ScoringDefaults.SignedScoringKeys.Contains(lufeKey))
            {
                if (lufeValue < -1m || lufeValue > 0m)
                    throw new InvalidOperationException(
                        $"Configuration value for '{lufeKey}' must be between -1 and 0 " +
                        $"(current: {lufeValue}).");
            }
            else if (lufeValue < 0m || lufeValue > 100m)
            {
                throw new InvalidOperationException(
                    $"Configuration value for '{lufeKey}' must be between 0 and 100 " +
                    $"(current: {lufeValue}).");
            }
        }

        // The penalty ramps from umbral_inicio down to umbral_plena, so the full-penalty threshold
        // must sit at or below the one where it starts. Inverted, the ramp has a negative or infinite
        // slope and the config no longer describes a penalty at all. Equal is allowed and means a step
        // at that single point — the engine handles that arm explicitly rather than dividing by zero.
        decimal umbralInicio = values["lufe_ventas_umbral_inicio"].Value;
        decimal umbralPlena  = values["lufe_ventas_umbral_plena"].Value;

        if (umbralPlena > umbralInicio)
            throw new InvalidOperationException(
                $"'lufe_ventas_umbral_plena' ({umbralPlena}) must be less than or equal to " +
                $"'lufe_ventas_umbral_inicio' ({umbralInicio}): the penalty ramps downward from the " +
                "start threshold to the full-penalty threshold.");

        // The CASFOG guarantee-arrears penalty (§7.4). Three kinds of number share the one
        // decimal(10,4) column, so each gets its own rule:
        //
        //  · casfog_mora_penalizacion_plena is a number of POINTS on the 0-100 final-score scale —
        //    the same bound the critical floors and the other two plenas carry.
        //  · casfog_mora_umbral_inicio and casfog_mora_umbral_plena are PROPORTIONS of the guaranteed
        //    balance in arrears, so [0,1] is the whole meaningful range: 0 is nothing in arrears and 1
        //    is the entire balance. Unlike their lufe_ventas_* counterparts they are POSITIVE by
        //    construction — a share, not a signed drop — so they are NOT in SignedScoringKeys and the
        //    blanket ">= 0" rule above already applies to them; this bound only adds the upper half.
        //  · casfog_mora_enabled is a BOOLEAN with nowhere boolean to live, exactly like
        //    bcra_cheques_enabled: the engine reads "> 0" as on, so 0.5 or 7 would silently enable it.
        foreach (var casfogKey in ScoringDefaults.CasfogMoraKeys)
        {
            decimal casfogValue = values[casfogKey].Value;

            if (casfogKey == "casfog_mora_enabled")
            {
                if (casfogValue != 0m && casfogValue != 1m)
                    throw new InvalidOperationException(
                        $"Configuration value for '{casfogKey}' must be 0 (off) or 1 (on) " +
                        $"(current: {casfogValue}).");
            }
            else if (casfogKey == "casfog_mora_penalizacion_plena")
            {
                if (casfogValue < 0m || casfogValue > 100m)
                    throw new InvalidOperationException(
                        $"Configuration value for '{casfogKey}' must be between 0 and 100 " +
                        $"(current: {casfogValue}).");
            }
            else if (casfogValue < 0m || casfogValue > 1m)
            {
                throw new InvalidOperationException(
                    $"Configuration value for '{casfogKey}' must be between 0 and 1 " +
                    $"(current: {casfogValue}).");
            }
        }

        // This ramp runs UPWARD — a larger share in arrears is worse — so the ordering rule is the
        // mirror of the sales one above: the full-penalty threshold must sit at or ABOVE the one where
        // the penalty starts. Inverted, the ramp has a negative slope and the config no longer
        // describes a penalty. Equal is allowed and means a step at that single point, which the
        // engine handles explicitly rather than dividing by zero.
        decimal moraInicio = values["casfog_mora_umbral_inicio"].Value;
        decimal moraPlena  = values["casfog_mora_umbral_plena"].Value;

        if (moraPlena < moraInicio)
            throw new InvalidOperationException(
                $"'casfog_mora_umbral_plena' ({moraPlena}) must be greater than or equal to " +
                $"'casfog_mora_umbral_inicio' ({moraInicio}): the penalty ramps upward from the " +
                "start threshold to the full-penalty threshold.");

        // Every score threshold is a point on the 0-100 score scale: Classify compares the final score,
        // which the engine clamps to [0,100], against them. A cutoff above 100 can never be reached, and
        // the Portal refuses such a triplet and falls back to the defaults — so the level it would show
        // contradicts the one stored here. The blanket ">= 0" rule above already refuses a negative
        // cutoff; the lower half is repeated so this rule reads whole on its own.
        foreach (var scoreKey in ScoringDefaults.ScoreThresholdKeys)
        {
            decimal scoreValue = values[scoreKey].Value;
            if (scoreValue < 0m || scoreValue > 100m)
                throw new InvalidOperationException(
                    $"Configuration value for '{scoreKey}' must be between 0 and 100 " +
                    $"(current: {scoreValue}).");
        }

        // Score thresholds must also be ordered: low < medium < high
        decimal low = values["score_threshold_low"].Value;
        decimal medium = values["score_threshold_medium"].Value;
        decimal high = values["score_threshold_high"].Value;

        if (low >= medium || medium >= high)
            throw new InvalidOperationException(
                $"Score thresholds must be ordered: low ({low}) < medium ({medium}) < high ({high}).");

        ValidatePersonEvaluationConfig(values);
    }

    /// <summary>
    /// Bounds the persona humana pf_ keys (M10 Phase 4a): whole numbers inside their range for the counts,
    /// windows and the consumo floor (in thousands, so it fits DECIMAL(10,4)), 0/1 flags, fractions in
    /// [0,1], the cuota/ingreso cuts strictly ascending, the debt-service bands with verde above rojo, the
    /// certificate renewal window not inverted, the agro cuotas a year one of 1, 2, 4 or 12, the rejection age below the alert age, the cuota estimate inside
    /// its narrower band and the consumo base month a real month between 202001 and the current one. The values are
    /// PROVISIONAL; the bounds only keep a tenant from storing one the evaluation cannot read. The messages
    /// are es-AR, because the PUT returns them verbatim as the 400 detail the Settings UI shows.
    /// </summary>
    private static void ValidatePersonEvaluationConfig(
        Dictionary<string, (decimal Value, string? Description)> values)
    {
        foreach (var (key, (min, max)) in PersonEvaluationDefaults.IntegerKeys)
        {
            decimal value = values[key].Value;
            if (value != decimal.Truncate(value) || value < min || value > max)
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que ser un número entero entre {min} y {max} (recibido: {EsAr(value)}).");
        }

        foreach (var (key, allowed) in PersonEvaluationDefaults.AllowedValueKeys)
        {
            decimal value = values[key].Value;
            if (!allowed.Contains((int)value))
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que ser {PersonEvaluationDefaults.EsArList(allowed)} (cuotas por año) " +
                    $"(recibido: {EsAr(value)}).");
        }

        foreach (var key in PersonEvaluationDefaults.FlagKeys)
        {
            decimal value = values[key].Value;
            if (value != 0m && value != 1m)
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que ser 0 (desactivado) o 1 (activado) (recibido: {EsAr(value)}).");
        }

        foreach (var key in PersonEvaluationDefaults.FractionKeys)
        {
            decimal value = values[key].Value;
            if (value < 0m || value > 1m)
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que estar entre 0 y 1 (recibido: {EsAr(value)}).");
        }

        foreach (var (key, (min, max)) in PersonEvaluationDefaults.BoundedFractionKeys)
        {
            decimal value = values[key].Value;
            if (value < min || value > max)
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que estar entre {EsAr(min)} y {EsAr(max)} (recibido: {EsAr(value)}).");
        }

        var currentMonth = PersonEvaluationDefaults.ToYearMonth(TodayInArgentina());
        foreach (var key in PersonEvaluationDefaults.YearMonthKeys)
        {
            decimal value = values[key].Value;
            if (value != decimal.Truncate(value)
                || value < PersonEvaluationDefaults.YearMonthMin
                || value > currentMonth
                || value % 100m is < 1m or > 12m)
            {
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que ser un mes AAAAMM entre {PersonEvaluationDefaults.YearMonthMin} y " +
                    $"{currentMonth} (recibido: {EsAr(value)}).");
            }
        }

        var cuts = PersonEvaluationDefaults.CuotaIngresoCutKeys;
        for (var i = 1; i < cuts.Length; i++)
        {
            decimal lower = values[cuts[i - 1]].Value;
            decimal upper = values[cuts[i]].Value;
            if (upper <= lower)
                throw new InvalidOperationException(
                    $"El valor de {cuts[i]} ({EsAr(upper)}) tiene que ser mayor que {cuts[i - 1]} ({EsAr(lower)}): " +
                    "las bandas de cuota/ingreso suben de verde a roja.");
        }

        foreach (var key in PersonEvaluationDefaults.DscrKeys)
        {
            decimal value = values[key].Value;
            if (value < PersonEvaluationDefaults.DscrMin || value > PersonEvaluationDefaults.DscrMax)
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que estar entre {EsAr(PersonEvaluationDefaults.DscrMin)} y " +
                    $"{EsAr(PersonEvaluationDefaults.DscrMax)} (recibido: {EsAr(value)}).");
        }

        decimal dscrRojo  = values["pf_dscr_rojo"].Value;
        decimal dscrVerde = values["pf_dscr_verde"].Value;
        if (dscrVerde <= dscrRojo)
            throw new InvalidOperationException(
                $"El valor de pf_dscr_verde ({EsAr(dscrVerde)}) tiene que ser mayor que pf_dscr_rojo ({EsAr(dscrRojo)}).");

        foreach (var key in PersonEvaluationDefaults.CoberturaKeys)
        {
            decimal value = values[key].Value;
            if (value < PersonEvaluationDefaults.CoberturaMin || value > PersonEvaluationDefaults.CoberturaMax)
                throw new InvalidOperationException(
                    $"El valor de {key} tiene que estar entre {EsAr(PersonEvaluationDefaults.CoberturaMin)} y " +
                    $"{EsAr(PersonEvaluationDefaults.CoberturaMax)} (recibido: {EsAr(value)}).");
        }

        decimal coberturaRojo  = values["pf_cobertura_rojo"].Value;
        decimal coberturaVerde = values["pf_cobertura_verde"].Value;
        if (coberturaVerde <= coberturaRojo)
            throw new InvalidOperationException(
                $"El valor de pf_cobertura_verde ({EsAr(coberturaVerde)}) tiene que ser mayor que pf_cobertura_rojo " +
                $"({EsAr(coberturaRojo)}): las bandas de cobertura del fiador suben de roja a verde.");

        decimal certificadoDesde = values["pf_certificado_alerta_desde_mes"].Value;
        decimal certificadoHasta = values["pf_certificado_alerta_hasta_mes"].Value;
        if (certificadoHasta < certificadoDesde)
            throw new InvalidOperationException(
                $"El valor de pf_certificado_alerta_hasta_mes ({EsAr(certificadoHasta)}) tiene que ser mayor o igual que " +
                $"pf_certificado_alerta_desde_mes ({EsAr(certificadoDesde)}): juntos acotan la ventana de renovación " +
                "de un certificado vencido el 30/04.");

        decimal antiguedadRechazo = values["pf_antiguedad_rechazo_meses"].Value;
        decimal antiguedadMinima  = values["pf_antiguedad_min_meses"].Value;
        if (antiguedadRechazo > antiguedadMinima)
            throw new InvalidOperationException(
                $"El valor de pf_antiguedad_rechazo_meses ({EsAr(antiguedadRechazo)}) no puede superar " +
                $"pf_antiguedad_min_meses ({EsAr(antiguedadMinima)}): por debajo del primero se sugiere rechazo, " +
                "por debajo del segundo es alerta.");
    }

    /// <summary>
    /// A number as the es-AR messages print it: comma decimal, no trailing zeros (1.3000 → "1,3"). A fixed
    /// format rather than the es-AR culture, so the text never depends on the host's ICU data.
    /// </summary>
    private static string EsAr(decimal value) => value.ToString("0.####", EsArNumberFormat);

    /// <summary>Today in Buenos Aires: the upper bound of a month (AAAAMM) key.</summary>
    private static DateOnly TodayInArgentina() => DateOnly.FromDateTime(ArgentineTime.ToLocal(DateTime.UtcNow));
}
