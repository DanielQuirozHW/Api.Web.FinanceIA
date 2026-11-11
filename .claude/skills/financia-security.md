# financia-security.md — Security rules for FinanceIA BFF

## Token storage
- JWT is issued by the BFF as an httpOnly cookie — never in a response body
- Frontend NEVER stores tokens in localStorage or sessionStorage
- Cookie must have: HttpOnly=true, Secure=true, SameSite=Strict, Path=/

## TenantId — source of truth
- TenantId ALWAYS comes from:
  1. X-Tenant-ID request header (resolved by TenantResolutionMiddleware)
  2. JWT claim (fallback via TenantFromJwtMiddleware)
- NEVER trust TenantId from request body or query string
- Every DB query against FinanciaDbContext MUST include a TenantId filter
- TenantId is an NVARCHAR(100) Firebase tenant SLUG in BOTH contexts
  (e.g. "SgrOneClickSandboxFB2") — it is NOT a GUID anywhere.
  DDL 005_fix_business_tenantid_to_nvarchar.sql migrated every business table
  off UNIQUEIDENTIFIER so they accept slugs.
- There is no EF global query filter: the TenantId predicate is yours to write
  on every FinanciaDbContext query.

## Firebase — named instances only
- NEVER use FirebaseAuth.DefaultInstance
- NEVER use FirebaseApp.DefaultInstance
- Always resolve via TenantContext.FirebaseProjectId:
  FirebaseApp.GetInstance(tenantContext.FirebaseProjectId)
- Firebase credentials (CredentialJson) are decrypted at request time from AppDbContext
- Never log or persist FirebaseCredentialJson or DynamicsClientSecret

## ITenantContextAccessor — injection rules
- NEVER inject ITenantContextAccessor in services or repositories
- TenantContext is resolved ONCE in middleware and passed as a parameter
- Controllers receive it via ITenantContextAccessor, then pass it down explicitly
- This avoids hidden coupling and makes tenant scope explicit

## Audit tables — INSERT only
These tables in FinanciaDbContext are append-only. NEVER UPDATE or DELETE:
  AuditLogs, AnalysisEdits, SessionLogs, ApiCallLogs,
  DocumentAccessLogs, ExportLogs, SgrSyncLogs
- IAuditService handles all audit writes — always fire-and-forget (never breaks main flow)
- AuditService catches and logs its own exceptions internally

## Encryption
- AES-256-CBC for sensitive data at rest
- Master key from environment variable ENCRYPTION_MASTER_KEY — never in appsettings
- Use IEncryptionService — never implement encryption inline

## Frontend security rules
- NEVER call Firebase Admin SDK, Claude API, BCRA, or Dynamics from frontend
- Everything external goes through the BFF
- NEVER expose secrets in NEXT_PUBLIC_ variables
- Sanitize user-generated content before rendering

## Error responses
- Always use ProblemDetails (RFC 7807)
- Error shape: { type, title, status, detail, errorCode, traceId }
- Never log tokens, secrets, connection strings or PII

## Rate limiting
- Per user: 150 req/min
- Per tenant: dynamic (active_users × 150), max 1000/min
- Anonymous (onboarding): 5 req/min
