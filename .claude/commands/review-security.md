# Command: Review Security — Api.Web.ServiciosFinancieros

## Purpose
Perform a comprehensive security audit on a specific file or 
the entire project. Identifies vulnerabilities, bad practices 
and deviations from the security guidelines defined in 
.claude/skills/dotnet-security.md

## Usage
/review-security {filepath}
/review-security Controllers/BffController.cs
/review-security Clases/FirebaseTokenValidator.cs
/review-security --all

## Audit Checklist

### 1. Hardcoded Secrets & Credentials
Scan for:
- Hardcoded connection strings in .cs files
- Hardcoded API keys, secrets or passwords
- Hardcoded Azure AD tenant GUIDs or client IDs
- Hardcoded Firebase project IDs or credential JSON content
- Hardcoded reCAPTCHA secret keys
- Any string longer than 20 chars that looks like a key or secret

Severity: CRITICAL
Action: Flag exact line, explain risk, provide fix using 
IOptions<T> or environment variables

### 2. Firebase Multi-Tenant Violations
Scan for:
- FirebaseAuth.DefaultInstance usage
  Severity: CRITICAL
  Fix: Use TenantFirebaseRegistry.GetAuthAsync(tenantId, ct)

- FirebaseApp.DefaultInstance usage
  Severity: CRITICAL  
  Fix: Use named FirebaseApp via TenantFirebaseRegistry

- FirebaseTokenValidator resolving projectId from IConfiguration directly
  Severity: HIGH
  Fix: Resolve from TenantContext only

- Single FirebaseApp initialized at startup for all tenants
  Severity: CRITICAL
  Fix: Lazy per-tenant initialization in TenantFirebaseRegistry

### 3. Token Security
Scan for:
- JWT signed with HS256 using a key shorter than 32 characters
  Severity: HIGH
  Fix: Use minimum 32-character key, prefer 64-character

- JWT without tenantId claim
  Severity: HIGH
  Fix: Always include tenantId in CreateAccessToken

- Missing Cache-Control: no-store on token responses
  Severity: MEDIUM
  Fix: Add Response.Headers.CacheControl = "no-store"

- Token values logged in full
  Severity: HIGH
  Fix: Log only first 10 characters for traceability

- Missing token expiration validation
  Severity: CRITICAL
  Fix: Always set ValidateLifetime = true

### 4. Tenant Isolation Violations
Scan for:
- TenantContext resolved inside a controller or service
  Severity: HIGH
  Fix: TenantContext must only be resolved in 
  TenantResolutionMiddleware

- Missing X-Tenant-ID validation before processing
  Severity: HIGH
  Fix: Validate in TenantResolutionMiddleware before 
  authentication runs

- Cross-tenant data access (tenantId not used in DB queries)
  Severity: CRITICAL
  Fix: Always filter queries by tenantId or cliente field

- TenantContext stored in a Singleton service
  Severity: CRITICAL
  Fix: TenantContext is request-scoped, never store in Singleton

### 5. Authentication & Authorization
Scan for:
- Controllers missing [Authorize] attribute without explicit 
  [AllowAnonymous]
  Severity: HIGH
  Fix: Add [Authorize] or explicit [AllowAnonymous] with 
  reCAPTCHA validation

- Missing [AllowAnonymous] on unauthenticated endpoints
  Severity: MEDIUM
  Fix: Add [AllowAnonymous] attribute

- reCAPTCHA not validated on unauthenticated endpoints
  Severity: HIGH
  Fix: Always call RecaptchaVerifier.VerifyAsync before 
  business logic on unauthenticated endpoints

- reCAPTCHA score threshold below 0.5
  Severity: MEDIUM
  Fix: Use minimum score of 0.5, configure per tenant

- Missing rate limiting on endpoints
  Severity: MEDIUM
  Fix: Add [EnableRateLimiting("PerUserOrIp")] attribute

- FallbackPolicy not requiring authenticated user
  Severity: HIGH
  Fix: Set FallbackPolicy to RequireAuthenticatedUser

### 6. Sensitive Data Logging
Scan for:
- Logging full token strings
  Severity: HIGH
  Pattern: LogX(... token ...) where token is a full JWT

- Logging connection strings
  Severity: CRITICAL
  Pattern: LogX(... connectionString ...)

- Logging ClientSecret or credential values
  Severity: CRITICAL
  Pattern: LogX(... clientSecret/password/secret/key ...)

- Logging PII (email in debug/trace level)
  Severity: MEDIUM
  Pattern: LogDebug/LogTrace(... email ...)

### 7. HTTP Security Headers
Scan for:
- Missing removal of Server header
  Severity: LOW
  Fix: Add app.UseSecurityHeaders() or manual header removal

- CORS configured with AllowAnyOrigin
  Severity: HIGH
  Fix: Use explicit WithOrigins from configuration

- Missing Secure flag on cookies
  Severity: HIGH
  Fix: Set cookieOptions.Secure = true in production

- Missing SameSite on cookies
  Severity: MEDIUM
  Fix: Set cookieOptions.SameSite = SameSiteMode.Strict

### 8. Input Validation
Scan for:
- Missing X-Tenant-ID format validation
  Severity: HIGH
  Fix: Validate alphanumeric + hyphens, max 100 chars

- Missing null/empty checks on method parameters
  Severity: MEDIUM
  Fix: Use ArgumentNullException.ThrowIfNull and 
  ArgumentException.ThrowIfNullOrWhiteSpace

- Raw user input passed to external services without sanitization
  Severity: HIGH
  Fix: Sanitize and validate all external inputs

### 9. Dependency & Library Security
Scan for:
- Usage of Microsoft.IdentityModel.Clients.ActiveDirectory (ADAL)
  Severity: HIGH
  Fix: Migrate to Microsoft.Identity.Client (MSAL)

- Usage of deprecated or vulnerable NuGet packages
  Severity: HIGH
  Fix: Update to latest stable version

- Synchronous .Result or .Wait() on Tasks
  Severity: MEDIUM
  Fix: Use await — .Result can cause deadlocks in ASP.NET Core

### 10. Exception Handling
Scan for:
- Stack traces returned in API responses
  Severity: HIGH
  Fix: Use ProblemDetails with generic error message only

- Internal exception messages exposed to client
  Severity: HIGH
  Fix: Log internally, return generic ProblemDetails

- Empty catch blocks silently swallowing exceptions
  Severity: HIGH
  Fix: Always log before rethrowing or handling

- throw ex usage (loses stack trace)
  Severity: MEDIUM
  Fix: Use throw without ex to preserve stack trace

## Output Format

=== SECURITY REVIEW: {filename} ===
Reviewed: {date}
Reviewer: Claude Security Analyzer

CRITICAL (must fix before merge):
  [CRIT-001] FirebaseAuth.DefaultInstance used at line 45
  → Risk: All tenants share the same Firebase instance,
    cross-tenant token validation is possible
  → Fix: Replace with TenantFirebaseRegistry.GetAuthAsync(
      tenantContext.TenantId, cancellationToken)

HIGH (should fix before merge):
  [HIGH-001] Missing [Authorize] attribute on PerfilController
  → Risk: Endpoint accessible without authentication
  → Fix: Add [Authorize] or [AllowAnonymous] with reCAPTCHA

MEDIUM (fix in next iteration):
  [MED-001] reCAPTCHA score threshold hardcoded at 0.3
  → Risk: Low-quality traffic may pass validation
  → Fix: Move threshold to appsettings.json per tenant

LOW (nice to have):
  [LOW-001] Server header not removed
  → Risk: Minor information disclosure
  → Fix: Add app.Use to remove Server header

=== SUMMARY ===
Critical : 1
High     : 1  
Medium   : 1
Low      : 1
Total    : 4

Overall Risk: HIGH — address CRITICAL issues before merging