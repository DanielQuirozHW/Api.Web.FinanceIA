# Multi-Tenant BFF Patterns — Api.Web.ServiciosFinancieros

## Core Concepts
- Every request belongs to exactly one tenant
- Tenant identity is established once per request in middleware, before authentication
- TenantContext is the single source of truth for all tenant-specific configuration
- No service, repository or controller should resolve tenant config independently
- The word "cliente" in legacy code maps to "tenantId" in this template

## Tenant Resolution Flow
1. Request arrives with header X-Tenant-ID: "{tenantId}"
2. TenantResolutionMiddleware validates header format
3. Middleware checks IMemoryCache for existing TenantContext
4. Cache miss: load Credenciales + TenantFirebaseConfig from SQL Server
5. Decrypt sensitive fields in-memory using EncryptionService
6. Build TenantContext and store in cache with sliding expiration
7. Store TenantContext in HttpContext.Items["TenantContext"]
8. Request continues to authentication middleware
9. Controllers and services retrieve TenantContext from HttpContext.Items

## TenantContext Structure
TenantContext is an immutable record resolved once per request:
- TenantId: string            → value from X-Tenant-ID header (= Credenciales.cliente)
- DynamicsUrl: string         → Credenciales.url
- DynamicsClientId: string    → Credenciales.clientid
- DynamicsClientSecret: string→ Credenciales.clientsecret (decrypted at runtime)
- AzureAdTenantId: string     → Credenciales.tenantid (Azure AD GUID)
- FirebaseProjectId: string   → TenantFirebaseConfig.ProjectId
- FirebaseCredentialJson: string → TenantFirebaseConfig.CredentialJson (decrypted)
- RecaptchaSecretKey: string  → TenantFirebaseConfig.RecaptchaSecretKey (decrypted)

## X-Tenant-ID Header Rules
- Header name: X-Tenant-ID (case-insensitive in resolution, canonical form in docs)
- Value format: alphanumeric and hyphens only, max 100 characters
- Missing header: return 400 Bad Request with ProblemDetails
- Empty header: return 400 Bad Request with ProblemDetails  
- Unresolvable tenant: return 404 Not Found with ProblemDetails
- Never return tenant config details in error responses

## Firebase Multi-Tenant Pattern
- Each tenant has its own Firebase project with its own FirebaseApp instance
- FirebaseApp instances are named with tenantId: FirebaseApp.Create(options, tenantId)
- FirebaseAuth is retrieved per-tenant: FirebaseAuth.GetAuth(firebaseApp)
- FirebaseApp instances are created lazily and cached in TenantFirebaseRegistry
- TenantFirebaseRegistry is registered as Singleton
- Never use FirebaseAuth.DefaultInstance or FirebaseApp.DefaultInstance

## Authentication Flow (Authenticated Endpoints)
1. Client sends Firebase ID Token in Authorization: Bearer {firebaseToken}
2. SmartJwt PolicyScheme inspects token issuer
3. Token issuer matches Firebase → routes to Firebase scheme
4. FirebaseTokenValidator retrieves FirebaseApp for current tenant from registry
5. Validates token against tenant-specific FirebaseApp instance
6. On success: BffController exchanges for Internal JWT
7. Internal JWT includes tenantId claim matching TenantContext.TenantId
8. Client stores Internal JWT in httpOnly cookie
9. Subsequent requests use Internal JWT in Authorization header
10. SmartJwt routes to Internal scheme for validation

## Authentication Flow (Unauthenticated Endpoints)
1. Client sends X-Tenant-ID header + reCAPTCHA V3 token
2. TenantResolutionMiddleware resolves TenantContext
3. Controller retrieves RecaptchaSecretKey from TenantContext
4. RecaptchaVerifier validates token against Google API
5. Score below threshold: return 403 Forbidden
6. Score acceptable: issue short-lived Onboarding JWT
7. Onboarding JWT scope: onboarding_anon only, 10 minutes lifetime

## Internal JWT Claims
Every Internal JWT must contain these claims:
- tenantId: TenantContext.TenantId
- sub: Firebase UID or clientId for machine clients
- azp: authorized party (client identifier)
- iat: issued at (Unix timestamp)
- scope: space-separated list of granted scopes
- roles: granted roles array
- end_user_uid: Firebase UID (when on-behalf-of flow)
- end_user_email: user email (when on-behalf-of flow)

## TenantContext Access Patterns

### In Controllers (preferred)
Inject ITenantContextAccessor and access TenantContext directly:
public sealed class ExampleController : ControllerBase
{
    private readonly ITenantContextAccessor _tenantContextAccessor;
    
    public ExampleController(ITenantContextAccessor tenantContextAccessor)
    {
        _tenantContextAccessor = tenantContextAccessor;
    }
    
    public IActionResult Get()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
    }
}

### In Services
Receive TenantContext as constructor parameter via DI or 
as method parameter when the service is tenant-agnostic.
Never read HttpContext.Items directly from services.

## Caching Strategy
- Cache key: $"tenant:{tenantId}" 
- Cache type: IMemoryCache (in-process, no distributed cache needed for config)
- Sliding expiration: 30 minutes (configurable)
- Absolute expiration: 4 hours (configurable)
- Cache stores encrypted values — decryption happens on every cache hit
- Cache invalidation: explicit removal by tenantId when config is updated
- Never cache decrypted credentials

## OAuthClients Configuration
- OAuthClients defined in appsettings.json per environment
- Each client has: ClientId, AllowOnBehalfOf, Scopes, Roles
- Client lookup uses tenantId + clientId composite key in multi-tenant context
- Never hardcode client IDs or scope names in controller logic

## Error Response Standards
All error responses use ProblemDetails (RFC 7807):
- 400: invalid X-Tenant-ID format or missing required fields
- 401: invalid or expired token
- 403: reCAPTCHA score below threshold or insufficient scope
- 404: tenant not found or resource not found
- 429: rate limit exceeded
- 500: internal error (never expose details)
- 502: downstream service error (Dynamics, Firebase)

## BFF Controller Responsibilities
- Token exchange (Firebase → Internal JWT): POST /api/bff/obo-token
- Onboarding token issuance: POST /api/bff/onboarding-token
- Health check: GET /api/bff/health
- Never implement business logic in BffController
- Never call Dynamics 365 directly from BffController

## Dynamics 365 Integration Pattern
- DynamicsTokenService acquires OAuth token using MSAL client credentials flow
- Credentials sourced exclusively from TenantContext
- Token acquired per-request — never cached across requests
- All Dynamics calls go through a typed HttpClient registered per tenant
- Dynamics errors mapped to appropriate HTTP status codes before returning to client