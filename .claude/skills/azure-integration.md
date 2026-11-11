# Azure Integration Patterns — Api.Web.ServiciosFinancieros

## Azure Services Used
- Azure App Service: API hosting
- Azure SQL Server: the system of record for ALL business data. Two contexts —
  AppDbContext (auth/config: Credenciales, TenantFirebaseConfig, ErrorLogs) and
  FinanciaDbContext (the 20 business tables). Tenancy is row-level via a
  TenantId nvarchar(100) slug column.
- Dynamics 365 API: template legacy. Still DI-registered and health-checked, but
  reachable only from ConsultaFetchController and SearchController. It is NOT
  where business data lives.
- Azure App Configuration: AES encryption key + environment config
- Azure Application Insights: telemetry, logging, performance monitoring
- GitHub Actions: CI/CD pipeline (not Azure DevOps)

## Azure AD / MSAL — Dynamics 365 Authentication
- Authentication library: Microsoft.Identity.Client (MSAL) — never ADAL
- Flow: Client Credentials (machine-to-machine, no user interaction)
- Credentials sourced exclusively from TenantContext at runtime:
  - TenantContext.AzureAdTenantId → Azure AD tenant GUID
  - TenantContext.DynamicsClientId → App Registration Client ID
  - TenantContext.DynamicsClientSecret → decrypted at runtime
  - TenantContext.DynamicsUrl → Dynamics 365 instance URL
- Never cache MSAL tokens manually — use MSAL's built-in token cache
- Never store acquired access tokens in DB or persistent storage
- Always use WithAuthority combining base URL + AzureAdTenantId:
  $"https://login.microsoftonline.com/{tenantContext.AzureAdTenantId}"
- Always use WithClientSecret with the decrypted secret from TenantContext
- Scope format for Dynamics 365:
  $"{tenantContext.DynamicsUrl}/.default"
- MSAL errors must be caught, logged (without credentials) 
  and mapped to 502 Bad Gateway

## MSAL Client Pattern
- Create IConfidentialClientApplication per request using TenantContext
- Never register IConfidentialClientApplication as Singleton or Scoped in DI
  — it must be built with per-tenant credentials at runtime
- Wrap MSAL acquisition in a dedicated DynamicsTokenService:
  public interface IDynamicsTokenService
  {
      Task<string> AcquireTokenAsync(
          TenantContext tenantContext, 
          CancellationToken ct);
  }
- Log token acquisition success with tenantId and expiry (never log the token)
- On MsalException: log warning with tenantId + error code, throw as 
  DynamicsAuthException for controller to map to 502

## Azure App Configuration
- Used for: AES encryption master key, environment-specific settings
- Never store secrets in appsettings.json
- AES key environment variable name: ENCRYPTION_MASTER_KEY
- In local development: use .NET User Secrets (dotnet user-secrets)
- In Azure App Service: use Application Settings (environment variables)
- Never use Azure App Configuration SDK for per-request config reads
  — load at startup only, cache for app lifetime

## Application Insights
- SDK 3.x (`Microsoft.ApplicationInsights.AspNetCore` 3.1.0), which is OpenTelemetry underneath.
  Registered ONLY through `TelemetryRegistration.AddFinanciaTelemetry()` (Clases/Telemetry/) — never call
  AddApplicationInsightsTelemetry() directly: 3.x throws without a connection string, and that method
  is what keeps it off when `APPLICATIONINSIGHTS_CONNECTION_STRING` is absent.
- TenantId is a per-REQUEST activity tag (`customDimensions.TenantId`), set from
  HttpContext.Items["TenantContext"] by the ASP.NET Core instrumentation's response enricher.
  NEVER use `TelemetryClient.Context.GlobalProperties["TenantId"]`: it is process-wide, so concurrent
  requests of different tenants overwrite each other.
- No sampling: SamplingRatio 1 and EnableTraceBasedLogsSampler false (the 3.x defaults drop the logs of
  a sampled-out request, Warnings and Errors included). Levels: Logging:OpenTelemetry:LogLevel.
- Never send sensitive data: query strings are dropped, path segments with ≥7 digits (CUIT/DNI)
  become {id}, SQL parameter values are off, headers and bodies are never captured.
- Use ILogger<T> for all logging — the OpenTelemetry logging provider exports it; a log with an
  exception becomes an `exceptions` row. TelemetryClient custom events are not used.
- Guide: Api.Web.FinanceIA/docs/guides/telemetry.md.

## Azure SQL Server
- Two connection strings: "Default" (AppDbContext) and "FinanciaDefault"
  (FinanciaDbContext). Code binds one static FinanciaDefault connection — there
  is no per-tenant connection resolution — but the DDL procedure treats each
  tenant as its own database, so schema changes are applied per tenant DB.
- AppDbContext holds authentication and tenant configuration only:
  - Credenciales: tenant registry with Dynamics 365 OAuth credentials
  - TenantFirebaseConfig: Firebase project config per tenant
  - ErrorLogs
- FinanciaDbContext holds all business data — analyses, entities, credit lines,
  fiadores, alerts, chat, notes and the audit/log tables. Vendor payloads are
  JSON in nvarchar(max) columns.
- Always use Azure SQL connection strings with Encrypt=True
- Connection string format:
  Server={server}.database.windows.net;
  Database={database};
  User Id={user};
  Password={password};
  Encrypt=True;
  TrustServerCertificate=False;
  Connection Timeout=30;
- Never use TrustServerCertificate=True in production
- Always set Connection Timeout=30 explicitly
- Use connection resiliency with EnableRetryOnFailure:
  options.UseSqlServer(connectionString, sqlOptions =>
      sqlOptions.EnableRetryOnFailure(
          maxRetryCount: 3,
          maxRetryDelay: TimeSpan.FromSeconds(5),
          errorNumbersToAdd: null))

## GitHub Actions CI/CD
- Secrets stored in GitHub Repository Secrets, never in workflow YAML files
- Required secrets:
  - AZURE_WEBAPP_PUBLISH_PROFILE
  - ENCRYPTION_MASTER_KEY
  - SQL_CONNECTION_STRING
- Workflow triggers:
  - Push to main: deploy to production
  - Push to develop: deploy to staging
  - Pull request: build + test only, no deploy
- Always run dotnet test before deploying
- Never deploy if tests fail
- Use environment-specific appsettings:
  appsettings.Production.json
  appsettings.Staging.json
  appsettings.Development.json

## Environment Configuration Strategy
- appsettings.json: non-sensitive defaults only
- appsettings.{Environment}.json: environment-specific non-sensitive config
- Environment variables / Azure App Settings: all secrets and keys
- User Secrets (local dev only): local overrides for secrets
- Never commit appsettings.Production.json with real values
- Always provide appsettings.template.json with placeholder values 
  and comments explaining each setting

## Health Checks
- Register health checks for all external dependencies:
  - SQL Server connectivity
  - Firebase Admin SDK initialization
  - Azure AD token acquisition (smoke test)
- Endpoint: GET /health
- Endpoint: GET /health/ready (readiness probe for Azure App Service)
- Never expose sensitive config details in health check responses
- Health check response format:
  {
    "status": "Healthy",
    "checks": {
      "sqlServer": "Healthy",
      "firebase": "Healthy",
      "azureAd": "Healthy"
    }
  }