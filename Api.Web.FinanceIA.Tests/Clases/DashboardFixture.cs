// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// One in-memory tenant database with REAL repositories over it, shared by the dashboard suites and
/// the quorum batch-equivalence suite. The rules under test are about which rows exist and which of
/// them count, so the stores are real; nothing here is a stub.
/// </summary>
internal sealed class DashboardFixture : IAsyncDisposable
{
    public const string Tenant = "tenant-1";
    public const string Other  = "tenant-2";

    /// <summary>The instant every test describes, so ages and windows are deterministic.</summary>
    public static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private DashboardFixture(FinanciaDbContext context)
    {
        Context = context;
    }

    public FinanciaDbContext Context { get; }

    public static DashboardFixture Create(string name)
        => new(new FinanciaDbContext(
            new DbContextOptionsBuilder<FinanciaDbContext>().UseInMemoryDatabase($"dashboard-{name}-{Guid.NewGuid()}").Options));

    public ValueTask DisposeAsync() => Context.DisposeAsync();

    // ── Seeding ───────────────────────────────────────────────────────────────

    public User AddUser(string role, Guid? teamId = null, bool isActive = true, string tenant = Tenant, string? name = null)
    {
        var id   = Guid.NewGuid();
        var user = new User
        {
            UserId      = id,
            TenantId    = tenant,
            FirebaseUid = "uid-" + id.ToString("N"),
            Email       = id.ToString("N") + "@example.test",
            DisplayName = name ?? role + "-" + id.ToString("N")[..6],
            Role        = role,
            IsActive    = isActive,
            TeamId      = teamId,
            CreatedAt   = Now,
            UpdatedAt   = Now
        };
        Context.Users.Add(user);
        return user;
    }

    public Team AddTeam(int quorum, IEnumerable<Guid> managerIds, Guid? legacyManagerId = null, string tenant = Tenant, Guid? teamId = null)
    {
        var team = new Team
        {
            TeamId         = teamId ?? Guid.NewGuid(),
            TenantId       = tenant,
            Name           = "Equipo",
            ApprovalQuorum = quorum,
            ManagerUserId  = legacyManagerId,
            CreatedAt      = Now,
            UpdatedAt      = Now
        };
        Context.Teams.Add(team);

        foreach (var managerId in managerIds)
        {
            Context.TeamManagers.Add(new TeamManager
            {
                TeamId    = team.TeamId,
                UserId    = managerId,
                TenantId  = tenant,
                CreatedAt = Now
            });
        }

        return team;
    }

    public Entity AddEntity(string tenant = Tenant, string? name = null)
    {
        var entity = new Entity
        {
            EntityId     = Guid.NewGuid(),
            TenantId     = tenant,
            CUIT         = "30" + Random.Shared.Next(10_000_000, 99_999_999) + "9",
            BusinessName = name ?? "PyME " + Guid.NewGuid().ToString("N")[..6],
            CreatedAt    = Now,
            UpdatedAt    = Now
        };
        Context.Entities.Add(entity);
        return entity;
    }

    public Document AddDocument(Guid entityId, string processingStatus = "Completed", string tenant = Tenant)
    {
        var document = new Document
        {
            DocumentId       = Guid.NewGuid(),
            TenantId         = tenant,
            EntityId         = entityId,
            OriginalFileName = "balance.pdf",
            StoredFileName   = "balance.pdf",
            StoragePath      = "x/balance.pdf",
            MimeType         = "application/pdf",
            FileHashSHA256   = "hash",
            DocumentType     = "Balance",
            ProcessingStatus = processingStatus
        };
        Context.Documents.Add(document);
        return document;
    }

    /// <summary>
    /// An analysis and its result. Defaults: a balance analysis, PendingReview, requested by
    /// <paramref name="requestedBy"/>, generated a day before <see cref="Now"/>, with a balance blob,
    /// enrichment Complete. Tweak either row with the callbacks.
    /// </summary>
    public (Analysis Analysis, AnalysisResult Result) AddAnalysis(
        Guid requestedBy,
        Guid? entityId = null,
        Action<Analysis>? analysis = null,
        Action<AnalysisResult>? result = null,
        string tenant = Tenant)
    {
        var a = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = tenant,
            EntityId      = entityId ?? Guid.NewGuid(),
            AnalysisKind  = AnalysisKinds.Balance,
            RequestedBy   = requestedBy,
            Status        = AnalysisStatuses.PendingReview,
            FiscalYear    = 2025,
            AiModel       = "model",
            PromptVersion = "v1",
            CreatedAt     = Now.AddDays(-1)
        };
        analysis?.Invoke(a);

        var r = new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = a.AnalysisId,
            TenantId         = tenant,
            GeneratedAt      = a.CreatedAt,
            BalanceJson      = """{"activo":1}""",
            AiSuggestedScore = 50m,
            AiSuggestedLevel = "Medium",
            AiInputHash      = "in",
            AiOutputHash     = "out",
            ValidationStatus = "Valid",
            EnrichmentStatus = nameof(BcraEnrichmentOutcome.Complete)
        };
        result?.Invoke(r);

        Context.Analyses.Add(a);
        Context.AnalysisResults.Add(r);
        return (a, r);
    }

    /// <summary>A PreApproved analysis pre-approved by <paramref name="preApprovedBy"/>.</summary>
    public (Analysis Analysis, AnalysisResult Result) AddPreApproved(
        Guid preApprovedBy,
        DateTime? preApprovedAt = null,
        Guid? entityId = null,
        string kind = AnalysisKinds.Balance,
        string tenant = Tenant)
        => AddAnalysis(preApprovedBy, entityId, a =>
        {
            a.AnalysisKind        = kind;
            a.Status              = AnalysisStatuses.PreApproved;
            a.PreApprovedBy       = preApprovedBy;
            a.PreApprovedAt       = preApprovedAt ?? Now.AddDays(-2);
            a.PreApprovedAmount   = 1_000_000m;
            a.PreApprovedCurrency = "ARS";
        }, tenant: tenant);

    public void AddVote(Guid analysisId, Guid userId, string vote = AnalysisVoteValues.Approved, DateTime? votedAt = null, string tenant = Tenant)
        => Context.AnalysisApprovalVotes.Add(new AnalysisApprovalVote
        {
            VoteId     = Guid.NewGuid(),
            AnalysisId = analysisId,
            UserId     = userId,
            TenantId   = tenant,
            Vote       = vote,
            VotedAt    = votedAt ?? Now.AddHours(-1)
        });

    /// <summary>A solicitante whose frozen filters carry one filter with <paramref name="outcome"/>.</summary>
    public void AddSolicitante(Guid analysisId, string outcome, string tenant = Tenant, DateTime? createdAt = null)
        => Context.AnalysisParticipants.Add(new AnalysisParticipant
        {
            ParticipantId     = Guid.NewGuid(),
            TenantId          = tenant,
            AnalysisId        = analysisId,
            PersonLegajoId    = Guid.NewGuid(),
            Role              = ParticipantRoles.Solicitante,
            AmountAtCharge    = 100m,
            Currency          = "ARS",
            FiltersJson       = $$"""[{"id":"F1","outcome":"{{outcome}}","evidence":"e","source":"s"}]""",
            EvaluationVersion = "1",
            CreatedAt         = createdAt ?? Now.AddDays(-3)
        });

    /// <summary>One TenantSettings row, e.g. creditline.mode or an sgr.* key.</summary>
    public void AddSetting(string key, string value, string tenant = Tenant)
        => Context.TenantSettings.Add(new TenantSettings
        {
            SettingId    = Guid.NewGuid(),
            TenantId     = tenant,
            SettingKey   = key,
            SettingValue = value,
            UpdatedAt    = Now
        });

    public void AddConfig(string key, decimal value, string tenant = Tenant)
        => Context.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId    = Guid.NewGuid(),
            TenantId    = tenant,
            ConfigKey   = key,
            ConfigValue = value,
            UpdatedAt   = Now
        });

    /// <summary>A cupo. Defaults: Active, ARS, 1,000,000, no analysis on record.</summary>
    public CreditLine AddCreditLine(
        Guid entityId,
        string currency = "ARS",
        decimal amount = 1_000_000m,
        string status = "Active",
        Guid? originAnalysisId = null,
        Guid? lastRaiseAnalysisId = null,
        string tenant = Tenant)
    {
        var line = new CreditLine
        {
            CreditLineId        = Guid.NewGuid(),
            TenantId            = tenant,
            EntityId            = entityId,
            Amount              = amount,
            Currency            = currency,
            Status              = status,
            CreatedByUserId     = Guid.NewGuid(),
            CreatedAt           = Now.AddDays(-30),
            OriginAnalysisId    = originAnalysisId,
            LastRaiseAnalysisId = lastRaiseAnalysisId
        };
        Context.CreditLines.Add(line);
        return line;
    }

    /// <summary>An operation under <paramref name="line"/>, in its currency unless told otherwise. Defaults: Vigente, open-ended.</summary>
    public CreditLineOperation AddOperation(
        CreditLine line,
        decimal amount,
        string status = "Vigente",
        DateTime? validUntil = null,
        string? tenant = null)
    {
        var operation = new CreditLineOperation
        {
            OperationId     = Guid.NewGuid(),
            TenantId        = tenant ?? line.TenantId,
            CreditLineId    = line.CreditLineId,
            AnalysisId      = Guid.NewGuid(),
            Type            = 1,
            Amount          = amount,
            Currency        = line.Currency,
            ValidUntil      = validUntil,
            Status          = status,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt       = Now.AddDays(-10)
        };
        Context.CreditLineOperations.Add(operation);
        return operation;
    }

    /// <summary>One AnalysisRescores row for <paramref name="result"/>.</summary>
    public AnalysisRescore AddRescore(AnalysisResult result, Guid rescoredBy, DateTime rescoredAt, string tenant = Tenant)
    {
        var rescore = new AnalysisRescore
        {
            RescoreId                = Guid.NewGuid(),
            TenantId                 = tenant,
            AnalysisId               = result.AnalysisId,
            ResultId                 = result.ResultId,
            RescoredAt               = rescoredAt,
            RescoredBy               = rescoredBy,
            PreviousScore            = 40m,
            NewScore                 = 60m,
            PreviousLevel            = "Medium",
            NewLevel                 = "High",
            PreviousEnrichmentStatus = nameof(BcraEnrichmentOutcome.Degraded),
            NewEnrichmentStatus      = nameof(BcraEnrichmentOutcome.Complete),
            PreviousValuesJson       = "{}",
            SourcesJson              = "[]"
        };
        Context.AnalysisRescores.Add(rescore);
        return rescore;
    }

    /// <summary>One ApiCallLogs row.</summary>
    public void AddApiCall(
        string service, bool isSuccess, DateTime calledAt, int? durationMs = 100, string tenant = Tenant,
        int? responseStatus = null, string? errorMessage = "boom")
        => Context.ApiCallLogs.Add(new ApiCallLog
        {
            TenantId       = tenant,
            Service        = service,
            Endpoint       = "/endpoint",
            ResponseStatus = responseStatus,
            IsSuccess      = isSuccess,
            ErrorMessage   = isSuccess ? null : errorMessage,
            DurationMs     = durationMs,
            CalledAt       = calledAt
        });

    /// <summary>A BcraData blob as the engine stores it, for Entities.BcraDataJson or AnalysisResults.BcraDataJson.</summary>
    public static string BcraJson(BcraDeudaConsultaEstado estado, int? situacionMaxima)
        => System.Text.Json.JsonSerializer.Serialize(new BcraData
        {
            Deuda = new BcraDeudaData { Estado = estado, SituacionMaxima = situacionMaxima }
        });

    public async Task SaveAsync()
    {
        Context.ChangeTracker.DetectChanges();
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
    }

    // ── Real collaborators ────────────────────────────────────────────────────

    public DashboardRepository Repository() => new(Context);

    public AnalysisApprovalQuorumService Quorum() => new(
        new AnalysisApprovalVoteRepository(Context, NullLogger<AnalysisApprovalVoteRepository>.Instance),
        new AnalysisRepository(Context, NullLogger<AnalysisRepository>.Instance),
        new TeamRepository(Context, NullLogger<TeamRepository>.Instance),
        new UserRepository(Context, NullLogger<UserRepository>.Instance),
        NullLogger<AnalysisApprovalQuorumService>.Instance);

    /// <param name="repository">Replaces the real repository — e.g. with a failing one.</param>
    /// <param name="integrationAccess">Default: <see cref="IntegrationAccess"/> with its defaults — an SGR
    /// tenant on a fully configured installation, so every integration is listed.</param>
    public DashboardService Service(
        IDashboardRepository? repository = null, IIntegrationAccessService? integrationAccess = null) => new(
        repository ?? Repository(),
        Quorum(),
        new EntityRepository(Context, NullLogger<EntityRepository>.Instance),
        new UserRepository(Context, NullLogger<UserRepository>.Instance),
        new TeamRepository(Context, NullLogger<TeamRepository>.Instance),
        new PersonAnalysisRepository(Context, NullLogger<PersonAnalysisRepository>.Instance),
        integrationAccess ?? IntegrationAccess(),
        new TenantSettingsService(TenantSettingsRepository(), NullLogger<TenantSettingsService>.Instance),
        SgrSettings(),
        NullLogger<DashboardService>.Instance);

    public TenantSettingsRepository TenantSettingsRepository()
        => new(Context, NullLogger<TenantSettingsRepository>.Instance);

    /// <summary>The REAL SGR settings service over this database, with an audit sink the test can read.</summary>
    public SgrSettingsService SgrSettings(IAuditService? audit = null)
        => new(TenantSettingsRepository(), audit ?? new RecordingAuditService(), NullLogger<SgrSettingsService>.Instance);

    // ── Integration access ────────────────────────────────────────────────────

    /// <summary>
    /// Every availability gate open for <see cref="Tenant"/>: its LUFE key, the CASFOG credentials, NOSIS
    /// switched on with a key and a VR. Copy and remove an entry to close one gate.
    /// </summary>
    public static Dictionary<string, string?> FullyConfigured() => new()
    {
        [$"Tenants:{Tenant}:LufeApiKey"] = "lufe-key",
        ["Casfog:Email"]                 = "casfog@example.com",
        ["Casfog:Password"]              = "casfog-password",
        ["Nosis:Enabled"]                = "true",
        ["Nosis:ApiKey"]                 = "nosis-key",
        ["Nosis:VR"]                     = "42"
    };

    /// <summary>
    /// The REAL <see cref="IntegrationAccessService"/> — the code the enrichment endpoints gate on — over a
    /// profile service that answers <paramref name="resolution"/> (default: SGR, configured) or throws
    /// <paramref name="throwOnRead"/>, and over <paramref name="settings"/> (default:
    /// <see cref="FullyConfigured"/>).
    /// </summary>
    public static IntegrationAccessService IntegrationAccess(
        TenantProfileResolution? resolution = null,
        Exception? throwOnRead = null,
        IDictionary<string, string?>? settings = null,
        ILogger<IntegrationAccessService>? logger = null)
        => new(
            new FixedTenantProfileService(
                resolution ?? new TenantProfileResolution(TenantProfilePresets.Sgr, TenantProfileSource.Configured),
                throwOnRead),
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? FullyConfigured()).Build(),
            logger ?? NullLogger<IntegrationAccessService>.Instance);

    /// <summary>Answers one fixed resolution, or throws. The dashboard only ever reads the profile.</summary>
    private sealed class FixedTenantProfileService(TenantProfileResolution resolution, Exception? throwOnRead)
        : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => throwOnRead is null ? Task.FromResult(resolution) : throw throwOnRead;

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("The dashboard must not write the tenant profile.");
    }
}
