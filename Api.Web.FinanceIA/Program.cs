using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Concurrency;
using Api.Web.FinanceIA.Clases.Json;
using Api.Web.FinanceIA.Clases.SchemaVerification;
using Api.Web.FinanceIA.Clases.Telemetry;
using Api.Web.FinanceIA.Clases.Middleware;
using Api.Web.FinanceIA.Clases.EmailVerification;
using Api.Web.FinanceIA.Clases.PasswordReset;
using Api.Web.FinanceIA.Servicios.RateLimiting;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.NosisReport;
using Api.Web.FinanceIA.Servicios.AnalysisComparison;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Servicios.HealthChecks;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.Pdf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

// ── Schema verification mode (deploy gate) ────────────────────────────────────
// `Api.Web.FinanceIA --verify-schema` checks that every table and column this build's EF
// models map already exists in each target database, then exits WITHOUT starting the API.
// It is the deploy gate: this project is Database-First, the numbered DDL scripts in
// docs/database/ are run by hand by the DBA, and publishing before they run breaks
// production with "Invalid column name". Read-only — it executes no DDL.
// See Api.Web.FinanceIA/docs/guides/schema-verification.md.
//
// This runs BEFORE WebApplication.CreateBuilder on purpose, for two reasons: the check has no
// use for the web host or any registered dependency (Firebase, Claude, SQL for the app itself),
// and CreateBuilder feeds args to the command-line configuration provider, which expects
// --key=value pairs — a bare switch has no business going through it. Configuration is
// therefore built here with just what the check reads: appsettings plus environment variables.
if (SchemaVerificationCommand.IsRequested(args))
{
    var verificationConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        // So a developer can run the check locally against the connection strings they already
        // have in user-secrets. Optional: there are none on the build agent or in App Service.
        .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
        .AddEnvironmentVariables()
        .Build();

    using var verificationLoggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());

    return await SchemaVerificationCommand.RunAsync(
        verificationConfig,
        verificationLoggerFactory.CreateLogger("SchemaVerification"),
        CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ── Logging ─────────────────────────────────────────────────────────────────────
// Route ILogger output to Azure App Service diagnostics so it surfaces in the Log
// stream (filesystem/blob). The default providers alone only reach stdout, which is
// invisible in the Log stream unless App Service filesystem logging is enabled — that
// is why unhandled exceptions (already logged by GlobalExceptionHandler) left "no
// trace". No-op when not hosted in App Service, so it is safe for local runs and tests.
builder.Logging.AddAzureWebAppDiagnostics();

// ── Telemetry (Application Insights 3.x, OpenTelemetry-based) ──────────────────────
// Requests, dependencies (Anthropic, BCRA, LUFE, NOSIS, CASFOG, ARCA, SQL), exceptions and ILogger logs
// to Application Insights — ON ONLY when APPLICATIONINSIGHTS_CONNECTION_STRING is set (the 3.x SDK throws
// without one, so with none nothing is registered). No sampling; no query strings, CUITs in paths, SQL
// parameters, headers or bodies. Log levels: Logging:OpenTelemetry:LogLevel. See TelemetryRegistration
// and docs/guides/telemetry.md.
var telemetryEnabled = builder.AddFinanciaTelemetry();

// ── MVC / Swagger ─────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        o.JsonSerializerOptions.Converters.Add(new NullableUtcDateTimeConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Singleton services ────────────────────────────────────────────────────────
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton<ITenantFirebaseRegistry, TenantFirebaseRegistry>();

// ── EF Core ───────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(cfg.GetConnectionString("Default"),
        sqlOptions => sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null))
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

// FinanciaDbContext — per-tenant business database
// Required user secret: ConnectionStrings:FinanciaDefault
// dotnet user-secrets set "ConnectionStrings:FinanciaDefault"
//   "Server=(localdb)\MSSQLLocalDB;Database=financia-dev;Trusted_Connection=True;TrustServerCertificate=True;"
builder.Services.AddDbContext<FinanciaDbContext>(options =>
    options.UseSqlServer(
        cfg.GetConnectionString("FinanciaDefault"),
        sql => sql.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null)));

// ── Scoped services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<ICredencialRepository, CredencialRepository>();
builder.Services.AddScoped<ITenantFirebaseConfigRepository, TenantFirebaseConfigRepository>();
builder.Services.AddScoped<IErrorLogRepository, ErrorLogRepository>();
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();
builder.Services.AddScoped<IMfaBackupCodeService, MfaBackupCodeService>();
builder.Services.AddScoped<IFirebaseCustomTokenIssuer, FirebaseCustomTokenIssuer>();
builder.Services.AddScoped<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddScoped<ITenantResolver, TenantResolver>();
builder.Services.AddScoped<IFirebaseTokenValidator, FirebaseTokenValidator>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IOnboardingTokenIssuer, OnboardingTokenIssuer>();
builder.Services.AddScoped<IRecaptchaVerifier, RecaptchaVerifier>();
builder.Services.AddScoped<IDynamicsTokenService, DynamicsTokenService>();
builder.Services.AddScoped<IDynamicsClient, DynamicsClient>();
builder.Services.AddScoped<IUserContextResolver, FirestoreUserContextResolver>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserApprovalLimitRepository, UserApprovalLimitRepository>();
builder.Services.AddScoped<IUserInvitationRepository, UserInvitationRepository>();
builder.Services.AddScoped<IEntityRepository, EntityRepository>();
builder.Services.AddScoped<INosisReportRepository, NosisReportRepository>();
builder.Services.AddSingleton<INosisManagerReportParser, NosisManagerReportParser>();
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IAnalysisRunStateRepository, AnalysisRunStateRepository>();
builder.Services.AddScoped<IDocumentStorageService, LocalDocumentStorageService>();
builder.Services.AddScoped<IAnalysisResultRepository, AnalysisResultRepository>();
builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
builder.Services.AddScoped<IAnalysisComparisonRepository, AnalysisComparisonRepository>();
builder.Services.AddScoped<IClaudeService, ClaudeService>();
// The extraction second pass asks it how to turn the pages of a scan (no text layer). Stateless, one
// small claude-haiku-4-5 call on "ClaudeClient"; never fails an analysis.
builder.Services.AddSingleton<IScanOrientationDetector, ClaudeScanOrientationDetector>();
builder.Services.AddScoped<IBcraService, BcraService>();
// Process-wide: every scope shares the same Central de Deudores permits (Bcra:CentralDeudoresMaxConcurrency).
builder.Services.AddSingleton<IBcraCentralDeudoresGate, BcraCentralDeudoresGate>();
builder.Services.AddScoped<ILufeService, LufeService>();
builder.Services.AddScoped<ICasfogService, CasfogService>();
builder.Services.AddScoped<INosisService, NosisService>();
builder.Services.AddScoped<IScoringService, ScoringService>();
builder.Services.AddSingleton<IRatioCalculationService, RatioCalculationService>();
builder.Services.AddSingleton<ICashFlowReconciliationService, CashFlowReconciliationService>();
builder.Services.AddSingleton<ICashFlowNarrativeService, CashFlowNarrativeService>();
builder.Services.AddSingleton<IAlertRatioReconciliationService, AlertRatioReconciliationService>();
// Deterministic and stateless: the balance integrity checks run inside ClaudeService on every extraction pass.
builder.Services.AddSingleton<IBalanceIntegrityService, BalanceIntegrityService>();
// Serializes a company upload per (tenant, entity, file hash) so a double submit stores one document
// (DocumentsController.DuplicateUploadWindow). One process only — see KeyedAsyncLock.
builder.Services.AddSingleton<KeyedAsyncLock>();
// The same kind of deterministic checks over a legajo extraction (M10 Phase 2b): the pipeline and the
// review endpoints both run it.
builder.Services.AddSingleton<ILegajoIntegrityService, LegajoIntegrityService>();
// Deterministic and stateless like its neighbours above; the catalogue it evaluates arrives as a
// method argument, read separately by the Scoped repository below.
builder.Services.AddSingleton<IAlertRuleEngine, AlertRuleEngine>();
// Deterministic and stateless like its neighbours above: it holds no tenant state and reads
// nothing but the stream the controller hands it, so a single instance is safe.
builder.Services.AddSingleton<IExcelCashFlowParser, ExcelCashFlowParser>();
builder.Services.AddScoped<IAlertRuleCatalogueRepository, AlertRuleCatalogueRepository>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IApiCallLogService, ApiCallLogService>();
builder.Services.AddScoped<IExportLogRepository, ExportLogRepository>();
builder.Services.AddScoped<IPdfExportService, PdfExportService>();
builder.Services.AddScoped<IAlertRepository, AlertRepository>();
// Per-user notifications. Scoped like every other repository — it writes through FinanciaDbContext.
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IChatRepository, ChatRepository>();
builder.Services.AddScoped<INoteRepository, NoteRepository>();
builder.Services.AddScoped<ICreditLineRepository, CreditLineRepository>();
builder.Services.AddScoped<ICreditLineOperationRepository, CreditLineOperationRepository>();
builder.Services.AddScoped<IFiadorRepository, FiadorRepository>();
// Persona humana legajos (DDL 069) — one per person per tenant, written with their entity.
builder.Services.AddScoped<IPersonLegajoRepository, PersonLegajoRepository>();
builder.Services.AddScoped<IUsageRepository, UsageRepository>();

// Persona humana legajo documents (DDL 070, M10 Phase 2a): upload into a legajo, consent, and the
// background classification pipeline. The queue is a singleton wake-up signal between the request
// scopes and the worker; the worker opens a fresh scope (and DbContext) per document, in which the
// processor, the repository and the classification service live. The page renderer is stateless.
builder.Services.AddScoped<ILegajoDocumentRepository, LegajoDocumentRepository>();
builder.Services.AddScoped<ILegajoReconciliationRepository, LegajoReconciliationRepository>();
builder.Services.AddSingleton<ILegajoPipelineQueue, LegajoPipelineQueue>();
builder.Services.AddSingleton<ILegajoPageRenderer, PdfToImagePageRenderer>();
// The shared Messages plumbing of both passes (retries, consent hook, ApiCallLogs row). Scoped: it
// writes through the scoped IApiCallLogService.
builder.Services.AddScoped<LegajoMessagesClient>();
builder.Services.AddScoped<ILegajoClassificationService, LegajoClassificationService>();
// M10 Phase 2b/3a: extraction of the types with a schema (LegajoExtractionPrompts.SupportedTypes).
builder.Services.AddScoped<ILegajoExtractionService, LegajoExtractionService>();
// M10 Phase 3b: the reconciled inventory and incomes, rebuilt from the Ready documents (no model call).
builder.Services.AddScoped<ILegajoReconciliationService, LegajoReconciliationService>();
builder.Services.AddScoped<ILegajoDocumentProcessor, LegajoDocumentProcessor>();
// M10 Phase 5: BCRA/ARCA refreshes keyed by the legajo (a fiador's legajo has no entity route).
builder.Services.AddScoped<IPersonLegajoSnapshotRefreshService, PersonLegajoSnapshotRefreshService>();
builder.Services.AddHostedService<LegajoPipelineWorker>();
// M10 Phase 4a (DDL 072): the persona humana analysis. PersonEvaluationService is pure and static; only
// its persistence is registered.
builder.Services.AddScoped<IPersonAnalysisRepository, PersonAnalysisRepository>();

// POST /api/analyses with Prefer: respond-async (202). The pipeline is the long part of the request,
// shared by the synchronous path; the registry and queue are per-instance state, hence singletons.
builder.Services.AddScoped<IAnalysisPipeline, AnalysisPipeline>();
builder.Services.AddSingleton<AnalysisRunRegistry>();
builder.Services.AddSingleton<IAnalysisJobQueue, AnalysisJobQueue>();
builder.Services.AddHostedService<AnalysisJobWorker>();
// GET /api/analyses/runs: running, failed and recently completed balance runs of the tenant.
builder.Services.AddScoped<IAnalysisRunListRepository, AnalysisRunListRepository>();
builder.Services.AddScoped<IAnalysisRunListService, AnalysisRunListService>();
// POST /api/analyses/{id}/rescore (DDL 073): re-scores a balance analysis in place, sharing the pipeline's
// integration stages (AnalysisScoringStage). No extraction, no quota.
// POST /api/analyses/{id}/balance-corrections (DDL 081): the append-only AnalysisEdits rows, written inside
// the re-score's own transaction.
builder.Services.AddScoped<IAnalysisEditRepository, AnalysisEditRepository>();
builder.Services.AddScoped<IAnalysisRescoreRepository, AnalysisRescoreRepository>();
builder.Services.AddScoped<IPersonAnalysisRescoreRepository, AnalysisRescoreRepository>();
builder.Services.AddScoped<IAnalysisRescoreService, AnalysisRescoreService>();
builder.Services.AddScoped<IAnalysisComparisonService, AnalysisComparisonService>();
builder.Services.AddScoped<IPersonAnalysisRescoreService, PersonAnalysisRescoreService>();
// "Sugerir corrección" (10-F4a): one model question about the failed figures, verified by the integrity checks.
builder.Services.AddScoped<IBalanceCorrectionSuggestionService, BalanceCorrectionSuggestionService>();
// Single-flight per suggestion key: concurrent openings over the same figures share one model call (one process).
builder.Services.AddSingleton<BalanceSuggestionInFlight>();
// Teams — the analyst grouping that gives a pre-approval a specific manager to go to (DDL 062/063).
builder.Services.AddScoped<ITeamRepository, TeamRepository>();

// Per-manager approval signatures (DDL 067). Mapped and read from this release on — a tenant whose
// 067 was not run now fails --verify-schema rather than the decision endpoint.
builder.Services.AddScoped<IAnalysisApprovalVoteRepository, AnalysisApprovalVoteRepository>();
builder.Services.AddScoped<IServiceStatusService, ServiceStatusService>();

// Audit log read
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();

// Settings MVP2
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IUserInvitationService, UserInvitationService>();
builder.Services.AddScoped<IEmailService, SendGridEmailService>();

// Password reset (POST /api/bff/auth/password-reset). The controller answers 202 after reCAPTCHA
// and the per-email budget and queues the rest, so a known and an unknown address answer alike;
// the worker does the user lookup, the tenant's Firebase link and the SendGrid email in its own
// scope. Throttle and queue are per-instance state, hence singletons.
builder.Services.AddSingleton<IPasswordResetEmailThrottle, PasswordResetEmailThrottle>();
builder.Services.AddSingleton<IPasswordResetQueue, PasswordResetQueue>();
builder.Services.AddScoped<IPasswordResetLinkGenerator, FirebasePasswordResetLinkGenerator>();
builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();
builder.Services.AddHostedService<PasswordResetWorker>();

// Email verification (POST /api/bff/auth/email-verification, and register-invited marking invited
// users verified). Same shape as password reset: per-user budget and queue are per-instance state,
// hence singletons; the worker sends the tenant's Firebase link through SendGrid in its own scope.
builder.Services.AddScoped<ITenantFirebaseUserAdmin, FirebaseTenantUserAdmin>();
builder.Services.AddSingleton<IEmailVerificationThrottle, EmailVerificationThrottle>();
builder.Services.AddSingleton<IEmailVerificationQueue, EmailVerificationQueue>();
builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
builder.Services.AddHostedService<EmailVerificationWorker>();

// Per-tenant product-behaviour settings (e.g. creditline.mode). Seeded by the DBA; the repository
// also carries the narrow write path the tenant-profile endpoint uses. ITenantSettingsService
// itself stays read-only. Scoped so the service caches reads for the lifetime of the request.
builder.Services.AddScoped<ITenantSettingsRepository, TenantSettingsRepository>();
builder.Services.AddScoped<ITenantSettingsService, TenantSettingsService>();
// The SGR Fondo de Riesgo settings (TenantSettings sgr.*): GET/PUT /api/bff/settings/sgr and the
// portfolio dashboard's sgr section. Scoped, like the repository it reads and writes through.
builder.Services.AddScoped<ISgrSettingsService, SgrSettingsService>();
// The persona humana expected-documents matrix (TenantSettings person.expected_documents): GET/PUT
// /api/bff/settings/person-expected-documents, the legajo checklist and the coverage index (engine 0.10).
// Its reads never throw — a settings fault falls back to the code defaults.
builder.Services.AddScoped<IPersonExpectedDocumentsService, PersonExpectedDocumentsService>();

// Tenant profile (§7.0): entity type + which optional integrations (LUFE, CASFOG, NOSIS) the
// tenant may use, resolved from TenantSettings rows overlaid on the in-code presets in
// TenantProfilePresets. Read fails open (a tenant with no rows resolves to the SGR preset); the
// write (PUT /api/bff/settings/tenant-profile) throws. Scoped so the service caches its one read
// for the lifetime of the request and can drop that entry after a write.
builder.Services.AddScoped<ITenantProfileService, TenantProfileService>();

// Both halves of every optional-integration gate — the profile ENTITLEMENT (fails open) and the
// installation's AVAILABILITY (LUFE key, CASFOG credentials, NOSIS switch + key + VR) — in one
// place, read by EntitiesController, FiadoresController and the operations dashboard alike so
// they cannot disagree. Scoped because it reads through the scoped ITenantProfileService.
builder.Services.AddScoped<IIntegrationAccessService, IntegrationAccessService>();

// Per-tenant billing/quota limits. Read by enforcement, written by the SuperAdmin endpoint
// PUT /api/billing/config (rows may still be seeded by the DBA). Scoped so the config service
// caches its read for the lifetime of the request — its write drops that entry — while
// BillingUsageService deliberately re-counts usage on every call: a quota gate must not serve a
// cached count.
builder.Services.AddScoped<ITenantBillingConfigRepository, TenantBillingConfigRepository>();
builder.Services.AddScoped<ITenantBillingConfigService, TenantBillingConfigService>();
builder.Services.AddScoped<IBillingUsageService, BillingUsageService>();

// Emits the 80% / 100% plan-consumption alerts, driven from GET /api/billing/usage. Scoped
// because it writes through the request-scoped IAlertRepository.
builder.Services.AddScoped<IBillingAlertService, BillingAlertService>();

// Reconciles the cupo event alerts (CUPO_OPERATIONS_EXPIRED / CUPO_UTILIZATION_HIGH), driven from
// GET /api/alerts before the listing is read. Scoped because it reads and writes through the
// request-scoped repositories. It never throws (only the request's own cancellation propagates), and
// unlike the billing alerts it fans out no notifications.
builder.Services.AddScoped<ICupoAlertService, CupoAlertService>();

// Emits the per-user notifications for each analysis decision transition, driven from
// PATCH /api/analyses/{id}/decision. Scoped because it writes through the request-scoped
// INotificationRepository. Strictly secondary to the decision itself — it owns its own try/catch
// and never throws, so a notification failure cannot cost an analyst their decision.
builder.Services.AddScoped<IAnalysisDecisionNotifier, AnalysisDecisionNotifier>();

// Emits the per-user "analysis ready" / "analysis failed" notification at the end of
// POST /api/analyses, so the analyst can leave the processing screen and come back from the bell.
// Scoped for the same reason as above, and just as secondary: it never throws, so a notification
// failure cannot change the analysis outcome or the response.
builder.Services.AddScoped<IAnalysisCompletionNotifier, AnalysisCompletionNotifier>();

// The team-quorum engine: how many manager signatures a pre-approval needs, and when it has them.
builder.Services.AddScoped<IAnalysisApprovalQuorumService, AnalysisApprovalQuorumService>();

// The role dashboards (GET /api/dashboard/my-work and /approvals): read-only summaries whose every
// figure comes from the rules the detail and the decision endpoint already use.
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<IDashboardService, DashboardService>();

// The persona humana calibration board (GET /api/dashboard/person-calibration): read-only, its own
// repository and service so the role dashboards above are untouched.
builder.Services.AddScoped<IPersonCalibrationRepository, PersonCalibrationRepository>();
builder.Services.AddScoped<IPersonCalibrationService, PersonCalibrationService>();

// ARCA Padron A13
builder.Services.AddScoped<IArcaPadronService, ArcaPadronA13Service>();

// ── Options ───────────────────────────────────────────────────────────────────
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection("Billing"));
builder.Services.Configure<BcraOptions>(builder.Configuration.GetSection("Bcra"));
builder.Services.Configure<LegajoOptions>(builder.Configuration.GetSection("Legajo"));
builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection("Analysis"));
builder.Services.Configure<RescoreOptions>(builder.Configuration.GetSection("Rescore"));
builder.Services.Configure<OnboardingOptions>(builder.Configuration.GetSection("Onboarding"));
// Setup:BootstrapKey (App Setting Setup__BootstrapKey) turns on POST /api/bff/setup/first-admin.
builder.Services.Configure<SetupOptions>(builder.Configuration.GetSection("Setup"));
// Read once here so a bad ForwardedHeaders:TrustedHops stops startup, not every request.
var trustedHops = ClientIpForwarding.ReadTrustedHops(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(options => ClientIpForwarding.Configure(options, trustedHops));
// The two values from other sections are copied in here so nothing below Program.cs reads IConfiguration.
builder.Services.Configure<PasswordResetOptions>(options =>
{
    builder.Configuration.GetSection("PasswordReset").Bind(options);
    options.DefaultPortalUrl     = builder.Configuration["AppSettings:FrontendUrl"] ?? string.Empty;
    options.AllowedPortalOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
});
builder.Services.Configure<EmailVerificationOptions>(options =>
{
    builder.Configuration.GetSection("EmailVerification").Bind(options);
    options.DefaultPortalUrl     = builder.Configuration["AppSettings:FrontendUrl"] ?? string.Empty;
    options.AllowedPortalOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    options.DefaultTemplateId    = builder.Configuration["SendGrid:EmailVerificationTemplateId"];
});
builder.Services.Configure<LegajoClassificationOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<LegajoExtractionOptions>(builder.Configuration.GetSection("Anthropic"));
// Balance extraction model: Anthropic:ExtractionModel / ExtractionEffort / ExtractionTimeoutSeconds.
// All optional; unset sends exactly today's request (see ExtractionModelCatalog).
builder.Services.Configure<ExtractionModelOptions>(builder.Configuration.GetSection("Anthropic"));

// ── Named HTTP clients ────────────────────────────────────────────────────────
builder.Services.AddHttpClient("RecaptchaClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("DynamicsClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("ClaudeClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});
// Balance extraction: no client-wide timeout. Each attempt carries its own, from the extraction
// model's spec (120 s for claude-sonnet-4-6, as before; 300 s for the thinking Sonnet 5.x), or
// Anthropic:ExtractionTimeoutSeconds. "ClaudeClient" above stays 120 s for the chat, the narrative,
// the legajo classification and the scan-orientation call (which bounds itself to 30 s).
builder.Services.AddHttpClient(ClaudeService.ExtractionHttpClientName, client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
// Legajo extraction (M10 Phase 2b): up to 16k output tokens outlast the 120 s above.
// Legajo:ExtractionTimeoutSeconds, default 600; a timeout is not retried (see LegajoOptions).
builder.Services.AddHttpClient(LegajoExtractionService.HttpClientName, client =>
{
    var seconds = builder.Configuration.GetValue("Legajo:ExtractionTimeoutSeconds", new LegajoOptions().ExtractionTimeoutSeconds);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, seconds));
});
// BCRA: two named clients — "BcraClient" (estadísticas, unchanged pipeline) and
// "BcraCentralDeudoresClient" (Central de Deudores, one gentle retry). See BcraHttpClientRegistration.
builder.Services.AddBcraHttpClients();
builder.Services.AddHttpClient("LufeClient", client =>
{
    client.BaseAddress = new Uri("https://legajounicoapi.produccion.gob.ar/lufe/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "FinancIA/1.0 (+https://hwapplications.com)");
});
// NosisClient — plain header-auth GET (X-API-KEY), so a named HttpClient is enough; no login POST
// and therefore none of the HttpWebRequest machinery CASFOG needs below. BaseAddress is deliberately
// NOT set: the absolute endpoint comes from Nosis:BaseUrl so it stays configurable per environment.
// No resilience pipeline — only BCRA has one, and NosisService degrades to LookupFailed on failure.
builder.Services.AddHttpClient("NosisClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "FinancIA/1.0 (+https://hwapplications.com)");
});
// CASFOG transport — ported from HttpClient to HttpWebRequest to match the proven, in-production
// Api.Web.Dynamics365 client, which does NOT hang the login POST the way HttpClient does here.
// The mechanism (Expect:100-continue disabled, per-request cert-accept scoped to the CASFOG host,
// body via GetRequestStream, response via GetResponse) lives in HttpWebRequestCasfogTransport.
// Stateless → singleton. No resilience pipeline: the proven client has no retry, and CasfogService
// still re-logs in once on a 401. Base URL / credentials are read from configuration in the service.
builder.Services.AddSingleton<ICasfogTransport, HttpWebRequestCasfogTransport>();
// Identity Platform password sign-in — the BFF's own first-factor check for the MFA
// backup-code login. No resilience pipeline on purpose: this call carries a password attempt,
// and a retry would double the failed-login count Google tracks against the account.
builder.Services.AddHttpClient<IIdentityPlatformSignInService, IdentityPlatformSignInService>(client =>
{
    client.BaseAddress = new Uri(IdentityPlatformSignInService.IdentityToolkitBaseAddress);
    client.Timeout     = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
// Tenant PDF branding (co-branded header): the SSRF-guarded "BrandingLogoClient" (5 s, https only, no
// redirects, no proxy, public addresses only, 1 MB), the cached logo fetcher and the per-export provider.
// See Servicios/Pdf/PdfBrandingServiceRegistration.cs and docs/guides/pdf-branding.md.
builder.Services.AddTenantPdfBranding();
builder.Services.AddHttpClient("AnthropicStatusClient", client =>
{
    client.BaseAddress = new Uri("https://status.claude.com");
    client.Timeout     = TimeSpan.FromSeconds(3);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// ARCA WSAA (authentication service)
builder.Services.AddHttpClient<IArcaWsaaService, ArcaWsaaService>(client =>
{
    var wsaaUrl = cfg["Arca:WsaaUrl"]
        ?? (builder.Environment.IsDevelopment()
            ? "https://wsaahomo.afip.gov.ar/ws/services/LoginCms"
            : "https://wsaa.afip.gov.ar/ws/services/LoginCms");
    client.BaseAddress = new Uri(wsaaUrl);
    client.Timeout     = TimeSpan.FromSeconds(30);
});

// ARCA Padron A13 (entity lookup)
builder.Services.AddHttpClient<IArcaPadronService, ArcaPadronA13Service>(client =>
{
    var padronUrl = cfg["Arca:ConstanciaUrl"]
        ?? (builder.Environment.IsDevelopment()
            ? "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5"
            : "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5");
    client.BaseAddress = new Uri(padronUrl);
    client.Timeout     = TimeSpan.FromSeconds(15);
});

// ARCA Constancia — persona lookup via getPersona_v2
builder.Services.AddHttpClient<IArcaConstanciaService, ArcaConstanciaService>(client =>
{
    var url = cfg["Arca:Services:ws_sr_constancia_inscripcion:Endpoint"]
        ?? "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5";
    client.BaseAddress = new Uri(url);
    client.Timeout     = TimeSpan.FromSeconds(15);
});

// ── Rate limiting ─────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRateLimitService, RateLimitService>();

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<DynamicsHealthCheck>("dynamics")
    .AddCheck<FirebaseHealthCheck>("firebase")
    .AddCheck<FirestoreHealthCheck>("firestore");

// ── Exception handling ────────────────────────────────────────────────────────
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ── Authentication ────────────────────────────────────────────────────────────
var jwtConfig = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "Internal";
        options.DefaultChallengeScheme    = "Internal";
    })
    .AddScheme<AuthenticationSchemeOptions, FirebaseAuthenticationHandler>(
        FirebaseAuthenticationHandler.SchemeName, _ => { })
    .AddJwtBearer("Internal", o =>
    {
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization) &&
                    ctx.Request.Cookies.TryGetValue("bff_session", out var cookieToken))
                {
                    ctx.Token = cookieToken;
                }
                return Task.CompletedTask;
            }
        };
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer            = true,
            ValidIssuer               = jwtConfig["Issuer"],
            ValidateAudience          = true,
            ValidAudience             = jwtConfig["Audience"],
            ValidateLifetime          = true,
            ClockSkew                 = TimeSpan.FromMinutes(2),
            ValidateIssuerSigningKey  = true,
            IssuerSigningKey          = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtConfig["SigningKey"]!)),
            ValidAlgorithms           = new[] { SecurityAlgorithms.HmacSha256 },
            RoleClaimType             = System.Security.Claims.ClaimTypes.Role
        };
    })
    .AddPolicyScheme("SmartJwt", "Smart JWT Selector", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authHeader) ||
                !authHeader.StartsWith("Bearer "))
            {
                return FirebaseAuthenticationHandler.SchemeName;
            }

            var token = authHeader.Substring("Bearer ".Length).Trim();
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (!handler.CanReadToken(token))
                {
                    return FirebaseAuthenticationHandler.SchemeName;
                }

                var jwtToken = handler.ReadJwtToken(token);
                var issuer = jwtToken.Issuer;

                if (issuer.Contains("firebase", StringComparison.OrdinalIgnoreCase) ||
                    issuer.Contains("securetoken.google.com"))
                {
                    return FirebaseAuthenticationHandler.SchemeName;
                }

                return "Internal";
            }
            catch
            {
                return FirebaseAuthenticationHandler.SchemeName;
            }
        };
    });

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                builder.Configuration
                    .GetSection("Cors:AllowedOrigins")
                    .Get<string[]>() ?? Array.Empty<string>())
            .AllowAnyMethod()
            .AllowAnyHeader()
            // The export's download name (ExportController) travels in Content-Disposition, which a
            // cross-origin script cannot read unless it is exposed.
            .WithExposedHeaders("Content-Disposition")
            .AllowCredentials(); // required for httpOnly cookies
    });
});

// ── Cookie Policy (cross-site dev/staging) ──────────────────────────────────
var sameSiteSetting = cfg["CookieSettings:SameSite"];
if (Enum.TryParse<SameSiteMode>(sameSiteSetting, ignoreCase: true, out var parsedSameSite))
{
    builder.Services.Configure<CookiePolicyOptions>(options =>
    {
        options.OnAppendCookie = ctx =>
        {
            if (string.Equals(ctx.CookieName, "bff_session", StringComparison.Ordinal))
            {
                ctx.CookieOptions.SameSite = parsedSameSite;
                ctx.CookieOptions.Secure   = true;
            }
        };
        options.OnDeleteCookie = ctx =>
        {
            if (string.Equals(ctx.CookieName, "bff_session", StringComparison.Ordinal))
            {
                ctx.CookieOptions.SameSite = parsedSameSite;
                ctx.CookieOptions.Secure   = true;
            }
        };
    });
}

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// One line saying whether Application Insights is on (never the connection string).
TelemetryRegistration.LogStatus(app.Logger, telemetryEnabled);

// An allowed origin with a path or a trailing slash never matches a browser Origin header, so CORS
// fails for it with no error anywhere. Say so once at startup. Only the count and the indexes are
// logged, never the values; the policy above is unchanged. See CorsOriginCheck.
var corsOffending = CorsOriginCheck.FindNonOriginIndexes(
    cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []);
if (corsOffending.Count > 0)
{
    app.Logger.LogWarning(
        "Cors:AllowedOrigins has {Count} entries that are not a bare origin (path or trailing slash) at index(es) {Indexes}; they never match a browser Origin",
        corsOffending.Count, string.Join(",", corsOffending));
}

// Log the resolved document-storage base directory once at startup so the deployed
// value is verifiable from the log stream without a debugger. Mirrors the resolution
// the scoped LocalDocumentStorageService performs per request.
app.Logger.LogInformation(
    "Document storage base directory resolved to {BaseDirectory}",
    LocalDocumentStorageService.ResolveBaseDirectory(
        cfg[LocalDocumentStorageService.StoragePathConfigKey], cfg["HOME"]));

// First, so every later reader of Connection.RemoteIpAddress (rate limiting, audit, ErrorLogs)
// sees the one resolved client IP. Options and rationale: ClientIpForwarding.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors();
app.UseCookiePolicy();
app.UseHttpsRedirection();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantFromJwtMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseAuthorization();
app.UseExceptionHandler(options => { });

app.MapControllers();

app.MapGet("/health", () => Results.Ok("Healthy"));

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});

app.Run();

// Explicit success code for the normal path. Required rather than cosmetic: the
// --verify-schema branch above returns a value, and once any path in top-level statements
// returns one, every path must (CS0161).
return 0;
