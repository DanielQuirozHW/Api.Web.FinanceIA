# financia-architecture.md — Architecture patterns for FinanceIA BFF

## Two database contexts — critical distinction

### AppDbContext (master DB — shared, single connection)
- Connection: ConnectionStrings:Default (user-secrets in dev)
- Tables: Credenciales, TenantFirebaseConfig, ErrorLogs
- Purpose: tenant resolution, Firebase config, error logging
- NEVER query business data here

### FinanciaDbContext (business DB)
- Connection: ConnectionStrings:FinanciaDefault — ONE static connection string.
  There is no per-tenant connection resolution in code. The DDL procedure does
  treat each tenant as its own database, so schema changes are applied per
  tenant DB; both facts are true and neither replaces the other.
- Tables: the 20 DbSets on FinanciaDbContext — Users, UserInvitations, Entities,
  Documents, Analyses, AnalysisResults, ScoringConfig, AlertRuleCatalogue,
  Alerts, CreditLines, CreditLineOperations, Fiadores, ChatConversations,
  ChatMessages, Notes, TenantSettings + the audit/log tables.
  Read Data/Configurations/Financia/ for the current set rather than this list.
- EVERY query must include a TenantId filter — no exceptions, and no EF global
  query filter exists to catch a miss. TenantId is an NVARCHAR(100) slug.
- Database First — NEVER run EF migrations against this context

## Tenant resolution pipeline (already implemented — do not modify)
Request → TenantResolutionMiddleware → Authentication → TenantFromJwtMiddleware
       → RateLimitingMiddleware → Authorization → Controller

## TenantContext shape
- TenantId: string slug from X-Tenant-ID header (e.g. "acme") — NOT a GUID
- Contains: DynamicsUrl, DynamicsClientId, DynamicsClientSecret (decrypted),
  AzureAdTenantId, FirebaseProjectId, FirebaseCredentialJson (decrypted),
  RecaptchaSecretKey (decrypted)
- Immutable record — resolved once per request

## Repository pattern
- Interface: Data/Repositories/I{Name}Repository.cs
- Implementation: Data/Repositories/{Name}Repository.cs
- Register as Scoped in Program.cs
- Always CancellationToken in every async method
- Always AsNoTracking() for read-only queries
- NEVER inject ITenantContextAccessor — receive TenantContext as method parameter
- Always scope every FinanciaDbContext query to tenantId

## Folder structure
Api.Web.FinanceIA/
├── Clases/Authentication/    # Firebase + JWT handlers
├── Clases/Middleware/        # TenantResolution, TenantFromJwt, RateLimiting
├── Clases/Exceptions/        # Custom exceptions
├── Clases/Services/          # Core services
├── Controllers/              # Thin controllers — delegate to services
├── Data/Configurations/      # EF Fluent API
├── Data/Repositories/        # Interfaces + implementations
├── Models/                   # DTOs, EF entities, config models
├── Servicios/                # Business services, external integrations
└── docs/database/            # DDL scripts for DBA execution

## Authentication
- SmartJwt: auto-detects Firebase vs Internal JWT by token issuer
- Firebase: issuer contains "securetoken.google.com"
- Internal JWT: 15 min expiry, httpOnly cookie, HMAC-SHA256

## IUserContextResolver — FinanceIA adaptation (TBD)
- Template uses FirestoreUserContextResolver — NOT used in FinanceIA
- FinanceIA resolves user context from FinanciaDbContext.Users
- Do not modify until first business endpoint is built

## C# conventions
- sealed classes unless inheritance is required
- CancellationToken in every async method
- Structured logging with TenantId parameter — never string interpolation
- System.Text.Json only — no Newtonsoft.Json
- Always explicit types — no var for non-obvious returns
- ProblemDetails for all error responses
