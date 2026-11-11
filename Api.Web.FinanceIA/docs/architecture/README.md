# Api.Web.FinanceIA — Architecture Overview

## Project Purpose
Multi-tenant BFF (Backend For Frontend) API template for financial 
services. Base template for all future APIs in the organization.
Built with ASP.NET Core 8, targeting Azure hosting.

---

## Tech Stack
- Framework  : ASP.NET Core 8 (.NET 8.0), C#
- Database   : Azure SQL Server — single shared DB (Database First)
- Auth       : Firebase Admin SDK + JWT Bearer
- MSAL       : Microsoft.Identity.Client (Dynamics 365 OAuth)
- Caching    : IMemoryCache
- Telemetry  : Azure Application Insights
- Docs       : Swagger via Swashbuckle
- CI/CD      : GitHub Actions

---

## Multi-Tenant Design
- Every request belongs to exactly one tenant
- Tenant identified via X-Tenant-ID request header
- Value matches Credenciales.cliente in SQL Server
- TenantContext resolved once per request in middleware
- TenantContext injected via ITenantContextAccessor
- Never resolve TenantContext inside controllers or services

## Authentication Flow
1. User logs in via Firebase (portal)
2. Portal sends Firebase ID Token to API in Authorization header
3. API validates token against tenant-specific FirebaseApp instance
4. API issues Internal JWT with tenantId + user claims
5. Portal stores Internal JWT in httpOnly cookie
6. Subsequent requests use Internal JWT

## Unauthenticated Endpoints Flow
1. Portal sends X-Tenant-ID + reCAPTCHA V3 token
2. TenantResolutionMiddleware resolves TenantContext
3. Controller validates reCAPTCHA using TenantContext.RecaptchaSecretKey
4. On success: issue short-lived Onboarding JWT (10 min, scope: onboarding_anon)

---

## Database
- Approach: Database First exclusively
- Never run EF Core migrations
- Schema changes via DDL scripts in /docs/database/
- Single shared Azure SQL Server database

### Existing Tables
Credenciales
  Purpose : Master tenant registry + Dynamics 365 OAuth credentials
  Key cols: id, url, clientid, clientsecret (encrypted),
            tenantid (Azure AD GUID), cliente (tenant identifier)
  Note    : conexion column is obsolete — ignored in EF mapping

ErrorLogs  
  Purpose : Persistent API error log for operational review
  Key cols: ErrorId, Timestamp, Level, Message, ExceptionDetails,
            Source (TenantId), Url, UserId, IPAddress, 
            StackTrace, TraceId
  Note    : TraceId added via ErrorLog_AlterTraceId.sql

### New Tables
TenantFirebaseConfig
  Purpose : Firebase project config per tenant
  Key cols: Id, Cliente, ProjectId, CredentialJson (encrypted),
            RecaptchaSecretKey (encrypted), IsActive,
            CreatedAt, UpdatedAt
  Script  : docs/database/TenantFirebaseConfig.sql

---

## TenantContext
Immutable record resolved once per request by TenantResolutionMiddleware.
Contains all tenant-specific config needed by any component.

Fields:
- TenantId             → X-Tenant-ID header value
- DynamicsUrl          → Credenciales.url
- DynamicsClientId     → Credenciales.clientid
- DynamicsClientSecret → Credenciales.clientsecret (AES decrypted)
- AzureAdTenantId      → Credenciales.tenantid
- FirebaseProjectId    → TenantFirebaseConfig.ProjectId
- FirebaseCredentialJson → TenantFirebaseConfig.CredentialJson (AES decrypted)
- RecaptchaSecretKey   → TenantFirebaseConfig.RecaptchaSecretKey (AES decrypted)

---

## Project Structure
Api.Web.FinanceIA/
├── Clases/
│   ├── Exceptions/              ← custom exception hierarchy
│   ├── EncryptionService.cs     ← AES-256 encrypt/decrypt
│   ├── ITenantContextAccessor.cs← access TenantContext in controllers
│   ├── ITenantResolver.cs       ← resolves TenantContext from DB
│   ├── TenantResolutionMiddleware.cs ← resolves tenant per request
│   ├── TenantFirebaseRegistry.cs← manages FirebaseApp instances
│   ├── FirebaseTokenValidator.cs← validates Firebase ID tokens
│   ├── FirebaseAuthenticationHandler.cs
│   ├── ITokenService.cs         ← issues Internal JWT
│   ├── OnboardingTokenIssuer.cs ← issues Onboarding JWT
│   ├── RecaptchaVerifier.cs     ← validates reCAPTCHA V3
│   ├── IErrorLogService.cs      ← persists errors to ErrorLogs
│   └── GlobalExceptionHandler.cs← catches all unhandled exceptions
├── Controllers/
│   └── BffController.cs         ← token exchange endpoints
├── Data/
│   ├── AppDbContext.cs
│   ├── Configurations/          ← EF Fluent API configs
│   └── Repositories/            ← repository interfaces + implementations
├── Models/
│   ├── TenantContext.cs         ✅ done
│   ├── TenantCredencial.cs      ✅ done
│   ├── TenantFirebaseConfig.cs  ✅ done
│   └── ErrorLog.cs              ✅ done
├── Servicios/
│   ├── IDynamicsTokenService.cs ← MSAL token acquisition
│   └── IDynamicsClient.cs       ← Dynamics 365 HTTP operations
├── docs/
│   ├── architecture/
│   ├── database/                ✅ done
│   ├── guides/
│   └── decisions/
├── CLAUDE.md
├── MISTAKES.md
└── Program.cs

---

## Exception Hierarchy
AppException (base)
├── TenantResolutionException  → 400 tenant_resolution_error
├── TenantNotFoundException    → 404 tenant_not_found
├── TenantConfigException      → 500 tenant_config_error
├── FirebaseAuthException      → 401 firebase_auth_error
├── TokenGenerationException   → 500 token_generation_error
├── DatabaseException          → 500 database_error
├── EncryptionException        → 500 encryption_error
├── BusinessException          → 422 business_error
└── ValidationException        → 400 validation_error

ExternalServiceException (base)
├── DynamicsAuthException      → 502 dynamics_auth_error
├── DynamicsNotFoundException  → 404 dynamics_not_found
├── DynamicsBusinessException  → 422 dynamics_business_error
├── DynamicsUnavailableException→ 503 dynamics_unavailable
├── DynamicsTimeoutException   → 504 dynamics_timeout
├── FirebaseServiceException   → 502 firebase_service_error
└── RecaptchaException         → 502 recaptcha_error

---

## Encryption
- Algorithm : AES-256-CBC
- Key source : environment variable ENCRYPTION_MASTER_KEY
- Never store key in appsettings.json or database
- Fields encrypted in DB:
  - Credenciales.clientsecret
  - TenantFirebaseConfig.CredentialJson
  - TenantFirebaseConfig.RecaptchaSecretKey
- Always decrypt in-memory at runtime
- Never cache decrypted values

---

## JWT Tokens

### Internal JWT (session)
- Algorithm : HMAC-SHA256
- Lifetime  : 15 minutes (configurable)
- Mandatory claims: tenantId, sub, azp, iat, scope, roles
- Signing key: global, from appsettings / environment

### Onboarding JWT
- Algorithm : HMAC-SHA256
- Lifetime  : 10 minutes
- Scope     : onboarding_anon only
- Claims    : tenantId, scope, typ=onboarding_session

---

## Dynamics 365 Integration
- Auth      : MSAL client credentials flow per request
- Credentials: always from TenantContext
- HTTP      : IHttpClientFactory named client "DynamicsClient"
- Never use : new HttpClient() directly
- Never use : ADAL (Microsoft.IdentityModel.Clients.ActiveDirectory)
- Timeout   : from DynamicsOptions (appsettings)
- API ver   : v9.2
- JSON lib  : System.Text.Json only (never Newtonsoft.Json)
- Pagination: automatic via paging cookie

---

## Key Rules Summary
- Never use FirebaseAuth.DefaultInstance
- Never use FirebaseApp.DefaultInstance
- Never hardcode tenant names, client IDs or secrets
- Never inject ITenantContextAccessor in services
  (pass TenantContext as method parameter)
- Never use new HttpClient() (use IHttpClientFactory)
- Never use ADAL (use MSAL)
- Never run EF Core migrations
- Never return exception details in API responses
- Never log tokens, secrets, keys, PII or connection strings
- Always use sealed classes unless inheritance required
- Always use AsNoTracking() for read-only EF queries
- Always include CancellationToken in every async method
- Always use structured logging with TenantId parameter
- Always use ProblemDetails for error responses
- Always use await (never .Result or .GetAwaiter().GetResult())

---

## Files Completed
Models/TenantContext.cs          ✅
Models/TenantCredencial.cs       ✅
Models/TenantFirebaseConfig.cs   ✅
Models/ErrorLog.cs               ✅
Clases/Exceptions/               ✅ (6 files)
docs/database/                   ✅ (3 files)

## Files Pending
Clases/EncryptionService.cs
Data/AppDbContext.cs
Data/Configurations/TenantCredencialConfiguration.cs
Data/Configurations/TenantFirebaseConfigConfiguration.cs
Data/Configurations/ErrorLogConfiguration.cs
Data/Repositories/ICredencialRepository.cs + implementation
Data/Repositories/ITenantFirebaseConfigRepository.cs + implementation
Data/Repositories/IErrorLogRepository.cs + implementation
Clases/IErrorLogService.cs + implementation
Clases/ITenantContextAccessor.cs
Clases/ITenantResolver.cs + implementation
Clases/TenantResolutionMiddleware.cs
Clases/TenantFirebaseRegistry.cs
Clases/FirebaseTokenValidator.cs
Clases/FirebaseAuthenticationHandler.cs
Clases/ITokenService.cs + implementation
Clases/OnboardingTokenIssuer.cs
Clases/RecaptchaVerifier.cs
Clases/GlobalExceptionHandler.cs
Servicios/IDynamicsTokenService.cs + implementation
Servicios/IDynamicsClient.cs + implementation
Controllers/BffController.cs
Program.cs