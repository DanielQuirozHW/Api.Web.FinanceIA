# Security Guidelines — Api.Web.ServiciosFinancieros

## Authentication Architecture
- Two-scheme authentication: Firebase (external) + Internal JWT (session)
- SmartJwt PolicyScheme routes tokens dynamically based on issuer claim
- Firebase tokens validated per-tenant using named FirebaseApp instances
- Internal JWT signed with HMAC-SHA256, includes mandatory tenantId claim
- Never validate Firebase tokens against a global/default FirebaseApp instance
- Every FirebaseApp instance must be named with the tenantId

## Multi-Tenant Security
- Every request must resolve a valid TenantContext before authentication
- Requests with missing, empty or unresolvable X-Tenant-ID must be rejected with 400
- TenantContext must be resolved in middleware, never inside controllers or services
- Never allow a token from tenant A to authenticate against tenant B
- Always verify that the tenantId claim in the Internal JWT matches 
  the resolved TenantContext.TenantId
- Tenant config must be cached with sliding expiration — never fetched on every request

## Credential & Key Management
- Sensitive fields stored in SQL Server must be encrypted with AES-256-CBC
- Fields that must always be encrypted:
  - Credenciales.clientsecret
  - TenantFirebaseConfig.CredentialJson
  - TenantFirebaseConfig.RecaptchaSecretKey
- AES encryption key must come from environment variable or Azure App Configuration
- AES key must never be stored in appsettings.json, code or database
- Never log decrypted values of any sensitive field
- Always decrypt credentials in-memory at runtime, never cache decrypted values
  in persistent storage

## Token Security
- Firebase ID token: validated per-tenant against named FirebaseApp instance
- Internal JWT mandatory claims: tenantId, sub, azp, iat, scope, roles
- Internal JWT lifetime: 15 minutes default, configurable per environment
- Onboarding JWT: 10 minutes max, scope strictly limited to onboarding_anon
- Always set these headers on token responses:
  Cache-Control: no-store
  Pragma: no-cache
- Never return token details in error responses
- Never log full token strings — log only first 10 chars for traceability

## Endpoint Security
- Unauthenticated endpoints (register, onboarding) must validate reCAPTCHA V3
  before any business logic executes
- reCAPTCHA secret key must be resolved from TenantContext, never from config files
- Minimum acceptable reCAPTCHA score: 0.5 (configurable per tenant)
- Rate limiting must be applied to all endpoints:
  - Authenticated: partition by sub claim
  - Unauthenticated: partition by IP address
- CORS origins must be explicitly configured per environment
- Never use AllowAnyOrigin() in production

## CSRF Protection
- httpOnly cookies require CSRF protection on all state-changing endpoints
- Use Double Submit Cookie pattern
- SameSite=Strict preferred, SameSite=Lax only when cross-site flows are required
- Always set Secure flag on cookies in production

## Input Validation & Injection Prevention
- Always validate X-Tenant-ID header: not null, not empty, max 100 chars, 
  alphanumeric and hyphens only
- Never use string concatenation for SQL queries — always use EF Core 
  parameterized queries
- Never pass raw user input to any external service call without sanitization
- Validate all JWT claims explicitly after token validation:
  issuer, audience, expiry, tenantId match

## Headers & Information Disclosure
- Never return stack traces in API responses
- Never return internal exception messages in API responses  
- Never expose infrastructure details (server, framework version) in response headers
- Always remove Server and X-Powered-By headers
- Use ProblemDetails (RFC 7807) for all error responses

## Dynamics 365 OAuth Security
- Never store Dynamics 365 access tokens in DB or persistent cache
- Access tokens must be acquired per-request using MSAL with client credentials flow
- Always use the AzureAdTenantId from TenantContext, never a hardcoded value
- ClientSecret must be decrypted from TenantContext at runtime
- Never log ClientSecret or acquired access tokens

## Logging & Audit
- Log all authentication failures with tenantId and reason (never with token content)
- Log all reCAPTCHA failures with tenantId and score
- Log all tenant resolution failures with the attempted tenantId value
- Log all 4xx and 5xx responses with correlation ID
- Never log: passwords, secrets, keys, full tokens, PII, connection strings