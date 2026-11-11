# Error Handling — Api.Web.ServiciosFinancieros

## Philosophy
Every error in the system has a known origin and a specific type.
Errors must be:
- Caught at the right layer (middleware for unhandled, service boundaries for known)
- Logged with full context internally (tenantId, operation, original exception)
- Returned to the client as sanitized ProblemDetails (RFC 7807)
- Never expose internal details, stack traces or infrastructure info to clients

## Exception Hierarchy

### Base Exceptions
All custom exceptions inherit from one of these two bases:

public abstract class AppException : Exception
{
    public string ErrorCode { get; }
    public int HttpStatusCode { get; }

    protected AppException(
        string errorCode,
        int httpStatusCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        HttpStatusCode = httpStatusCode;
    }
}

public abstract class ExternalServiceException : AppException
{
    public string ServiceName { get; }

    protected ExternalServiceException(
        string serviceName,
        string errorCode,
        int httpStatusCode,
        string message,
        Exception? innerException = null)
        : base(errorCode, httpStatusCode, message, innerException)
    {
        ServiceName = serviceName;
    }
}

## Exception Types & Mappings

### Tenant Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

TenantNotFoundException
  ErrorCode   : tenant_not_found
  HttpStatus  : 404
  When        : X-Tenant-ID value does not exist in Credenciales table
  Log level   : Warning
  Client msg  : "Tenant not found."

TenantConfigException
  ErrorCode   : tenant_config_error
  HttpStatus  : 500
  When        : Tenant exists but config is incomplete, 
                decryption fails or Firebase credentials are invalid
  Log level   : Error
  Client msg  : "Tenant configuration error."

TenantResolutionException
  ErrorCode   : tenant_resolution_error
  HttpStatus  : 400
  When        : X-Tenant-ID header is missing, empty or invalid format
  Log level   : Warning
  Client msg  : "Invalid or missing tenant identifier."

### Authentication Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

FirebaseAuthException
  ErrorCode   : firebase_auth_error
  HttpStatus  : 401
  When        : Firebase ID token is invalid, expired or from wrong project
  Log level   : Warning
  Client msg  : "Authentication failed."

TokenGenerationException
  ErrorCode   : token_generation_error
  HttpStatus  : 500
  When        : Internal JWT could not be generated
  Log level   : Error
  Client msg  : "Authentication error."

### Dynamics 365 Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

DynamicsAuthException
  ErrorCode   : dynamics_auth_error
  HttpStatus  : 502
  When        : MSAL fails to acquire token for Dynamics 365
                (wrong credentials, expired secret, Azure AD issue)
  Log level   : Error
  Client msg  : "Error connecting to financial services."
  ServiceName : "Dynamics365"

DynamicsNotFoundException
  ErrorCode   : dynamics_not_found
  HttpStatus  : 404
  When        : Requested entity does not exist in Dynamics 365
  Log level   : Warning
  Client msg  : "Resource not found."
  ServiceName : "Dynamics365"

DynamicsBusinessException
  ErrorCode   : dynamics_business_error
  HttpStatus  : 422
  When        : Dynamics 365 returns a business rule violation
                (e.g. account locked, insufficient balance, 
                duplicate record, workflow restriction)
  Log level   : Warning
  Client msg  : pass through sanitized Dynamics message when safe,
                otherwise "Business rule violation."
  ServiceName : "Dynamics365"

DynamicsUnavailableException
  ErrorCode   : dynamics_unavailable
  HttpStatus  : 503
  When        : Dynamics 365 API is unreachable or returns 5xx
  Log level   : Error
  Client msg  : "Financial services temporarily unavailable."
  ServiceName : "Dynamics365"

DynamicsTimeoutException
  ErrorCode   : dynamics_timeout
  HttpStatus  : 504
  When        : Dynamics 365 API call exceeds configured timeout
  Log level   : Error
  Client msg  : "Request to financial services timed out."
  ServiceName : "Dynamics365"

### Firebase Service Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

FirebaseServiceException
  ErrorCode   : firebase_service_error
  HttpStatus  : 502
  When        : Firebase Admin SDK fails (not auth-related)
                e.g. Firestore write fails, user creation fails
  Log level   : Error
  Client msg  : "Error connecting to identity services."
  ServiceName : "Firebase"

### Business Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

BusinessException
  ErrorCode   : business_error
  HttpStatus  : 422
  When        : Business rule violation within the API itself
                (not from Dynamics — API's own rules)
  Log level   : Warning
  Client msg  : pass through the exception message (safe, internal rule)

### Validation Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

ValidationException
  ErrorCode   : validation_error
  HttpStatus  : 400
  When        : Input validation fails (invalid format, missing fields,
                constraint violations)
  Log level   : Warning
  Client msg  : pass through validation details (safe, user input)
  Extra field : IReadOnlyCollection<ValidationError> Errors

ValidationError record:
  Field   : string  → name of the invalid field
  Message : string  → description of the violation

### Infrastructure Exceptions
Namespace: Api.Web.ServiciosFinancieros.Clases.Exceptions

DatabaseException
  ErrorCode   : database_error
  HttpStatus  : 500
  When        : SQL Server query fails, connection fails,
                or EF Core throws DbUpdateException
  Log level   : Error
  Client msg  : "Database error."

EncryptionException
  ErrorCode   : encryption_error
  HttpStatus  : 500
  When        : AES encryption or decryption fails
                (wrong key, corrupted data)
  Log level   : Error
  Client msg  : "Configuration error."

RecaptchaException
  ErrorCode   : recaptcha_error
  HttpStatus  : 502
  When        : Google reCAPTCHA API is unreachable or returns error
  Log level   : Error
  Client msg  : "Verification service unavailable."

## Exception to HTTP Status Mapping Table

| Exception                  | HTTP | ErrorCode                 | Log Level |
|----------------------------|------|---------------------------|-----------|
| TenantResolutionException  | 400  | tenant_resolution_error   | Warning   |
| TenantNotFoundException    | 404  | tenant_not_found          | Warning   |
| TenantConfigException      | 500  | tenant_config_error       | Error     |
| FirebaseAuthException      | 401  | firebase_auth_error       | Warning   |
| TokenGenerationException   | 500  | token_generation_error    | Error     |
| DynamicsAuthException      | 502  | dynamics_auth_error       | Error     |
| DynamicsNotFoundException  | 404  | dynamics_not_found        | Warning   |
| DynamicsBusinessException  | 422  | dynamics_business_error   | Warning   |
| DynamicsUnavailableException| 503 | dynamics_unavailable      | Error     |
| DynamicsTimeoutException   | 504  | dynamics_timeout          | Error     |
| FirebaseServiceException   | 502  | firebase_service_error    | Error     |
| BusinessException          | 422  | business_error            | Warning   |
| ValidationException        | 400  | validation_error          | Warning   |
| DatabaseException          | 500  | database_error            | Error     |
| EncryptionException        | 500  | encryption_error          | Error     |
| RecaptchaException         | 502  | recaptcha_error           | Error     |
| Unhandled Exception        | 500  | internal_error            | Critical  |

## ProblemDetails Response Format
All error responses use RFC 7807 ProblemDetails format:

Standard error response:
{
  "type": "https://serviciosfinancieros.api/errors/{errorCode}",
  "title": "{human readable title}",
  "status": {httpStatusCode},
  "detail": "{sanitized client message}",
  "instance": "{request path}",
  "errorCode": "{errorCode}",
  "traceId": "{correlation id}"
}

Validation error response (additional field):
{
  "type": "https://serviciosfinancieros.api/errors/validation_error",
  "title": "Validation Error",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "instance": "{request path}",
  "errorCode": "validation_error",
  "traceId": "{correlation id}",
  "errors": [
    {
      "field": "Email",
      "message": "Email is required."
    }
  ]
}

Dynamics business error response (additional field):
{
  "type": "https://serviciosfinancieros.api/errors/dynamics_business_error",
  "title": "Business Rule Violation",
  "status": 422,
  "detail": "{sanitized dynamics message}",
  "instance": "{request path}",
  "errorCode": "dynamics_business_error",
  "traceId": "{correlation id}",
  "serviceName": "Dynamics365"
}

## GlobalExceptionHandler
Location: Clases/GlobalExceptionHandler.cs
Registered as: IExceptionHandler (ASP.NET Core 8 built-in)
Registration: builder.Services.AddExceptionHandler<GlobalExceptionHandler>()
              app.UseExceptionHandler()

Responsibilities:
- Catch all unhandled exceptions from the pipeline
- Log with full context: tenantId, path, method, traceId, exception
- Map AppException subtypes to their defined HTTP status + errorCode
- Map unhandled Exception to 500 + internal_error
- Always return ProblemDetails — never raw exception details
- Always include traceId from Activity.Current?.Id or HttpContext.TraceIdentifier
- Never log the full token or credential values in exception context

Handler logic order:
1. Extract tenantId from HttpContext.Items["TenantContext"] if available
2. Extract traceId from Activity.Current?.Id ?? HttpContext.TraceIdentifier
3. Check if exception is AppException subtype:
   a. Cast to AppException
   b. Use exception.HttpStatusCode and exception.ErrorCode
   c. Log at exception-defined log level with tenantId + traceId
   d. Build ProblemDetails with sanitized client message
4. If not AppException (unhandled):
   a. Log at Critical level with full exception details
   b. Return 500 with generic "An unexpected error occurred." message
   c. Never include exception.Message in response
5. Set response Content-Type: application/problem+json
6. Write ProblemDetails as JSON response

## Where to Throw Exceptions

### Middleware (TenantResolutionMiddleware)
- Missing/invalid X-Tenant-ID header → throw TenantResolutionException
- Tenant not found in DB → throw TenantNotFoundException
- Decryption failure → throw TenantConfigException

### Repositories (Data/Repositories/)
- DbUpdateException caught → throw DatabaseException
- Connection failure caught → throw DatabaseException
- Null result when required → return null (let caller decide)
  Never throw from repository for not-found — return null

### Services (Servicios/)
- MSAL token acquisition fails → throw DynamicsAuthException
- Dynamics returns 404 → throw DynamicsNotFoundException
- Dynamics returns 4xx business error → throw DynamicsBusinessException
- Dynamics returns 5xx → throw DynamicsUnavailableException
- Dynamics timeout → throw DynamicsTimeoutException
- Firebase Admin SDK fails → throw FirebaseServiceException

### Clases/ (business services)
- Business rule violated → throw BusinessException
- Input validation fails → throw ValidationException with errors list
- AES operation fails → throw EncryptionException

### Controllers
- Never throw exceptions from controllers
- Never catch AppException in controllers
  (GlobalExceptionHandler handles it)
- Only catch specific exceptions when you need to add context:
  try { ... }
  catch (DynamicsBusinessException ex)
  {
      // enrich with additional context if needed
      throw;
  }

## Logging Context for Exceptions
Every exception log must include:
- TenantId (from TenantContext when available)
- TraceId (correlation ID)
- Operation name (calling method/service)
- Exception type
- Error code (for AppException subtypes)

Example:
_logger.LogError(ex,
    "Dynamics auth failed. " +
    "TenantId: {TenantId} | " +
    "Operation: {Operation} | " +
    "ErrorCode: {ErrorCode} | " +
    "TraceId: {TraceId}",
    tenantContext.TenantId,
    nameof(AcquireTokenAsync),
    DynamicsAuthException.DefaultErrorCode,
    traceId);

Never include in logs:
- Full token strings
- ClientSecret values
- Connection strings
- CredentialJson content
- Full inner exception message if it contains credential details

## IErrorLogService — Persistent Error Logging

### Purpose
Persist errors to SQL Server ErrorLogs table for operational
review by the team without requiring access to Azure Portal.
Works in combination with ILogger<T> → Application Insights.

### Interface Definition
public interface IErrorLogService
{
    Task LogAsync(
        ErrorLogEntry entry,
        CancellationToken cancellationToken);
}

### ErrorLogEntry Record
public sealed record ErrorLogEntry(
    string Level,
    string Message,
    string? ExceptionDetails,
    string? Source,
    string? Url,
    string? UserId,
    string? IpAddress,
    string? StackTrace,
    string? TraceId);

### Log Levels
Always use these exact string values for Level field:
- "Information" → significant business events
- "Warning"     → unexpected but recoverable (tenant not found,
                   recaptcha failed, firebase auth failed)
- "Error"       → failures needing attention (dynamics error,
                   db error, token generation failed)
- "Critical"    → system-level failures (unhandled exceptions,
                   encryption failures, config missing)

### ErrorLogs Table Mapping
SQL Table  : [dbo].[ErrorLogs]
EF Entity  : ErrorLog (Models/ErrorLog.cs)

Column mapping:
ErrorId          → int, PK, identity
Timestamp        → DateTime, always UtcNow, required
Level            → string(50), required
Message          → string, required
ExceptionDetails → string, nullable (exception type + error code)
Source           → string(255), nullable (TenantId)
Url              → string(2048), nullable (request path)
UserId           → string(255), nullable (end_user_uid from JWT)
IPAddress        → string(45), nullable (client IP)
StackTrace       → string, nullable (full stack trace)
TraceId          → string(100), nullable (correlation ID)

### What to Log in Each Field
Message          → operation description, never raw exception message
                   Example: "Firebase token validation failed"
                   Never : "Object reference not set to instance"
ExceptionDetails → "{ExceptionType} | ErrorCode: {errorCode}"
                   Example: "FirebaseAuthException | ErrorCode: firebase_auth_error"
Source           → TenantContext.TenantId when available, 
                   "unknown" when tenant resolution failed
Url              → HttpContext.Request.Path + QueryString
UserId           → end_user_uid claim from JWT when available
IPAddress        → HttpContext.Connection.RemoteIpAddress?.ToString()
StackTrace       → ex.ToString() — full stack trace, never just ex.Message
TraceId          → Activity.Current?.Id ?? HttpContext.TraceIdentifier

### GlobalExceptionHandler Integration
GlobalExceptionHandler calls IErrorLogService after ILogger:

1. Extract context from HttpContext:
   - tenantId from HttpContext.Items["TenantContext"]
   - traceId from Activity.Current?.Id ?? HttpContext.TraceIdentifier
   - userId from HttpContext.User claims (end_user_uid or sub)
   - ipAddress from HttpContext.Connection.RemoteIpAddress
   - path from HttpContext.Request.Path

2. Log to ILogger (Application Insights):
   _logger.Log(logLevel, ex, 
       "Unhandled {ExceptionType} for tenant {TenantId}. " +
       "TraceId: {TraceId}",
       ex.GetType().Name, tenantId, traceId);

3. Persist to ErrorLogs table via IErrorLogService:
   await _errorLogService.LogAsync(new ErrorLogEntry(
       Level: logLevel,
       Message: GetSafeMessage(ex),
       ExceptionDetails: $"{ex.GetType().Name} | ErrorCode: {errorCode}",
       Source: tenantId,
       Url: httpContext.Request.Path,
       UserId: userId,
       IpAddress: ipAddress,
       StackTrace: ex.ToString(),
       TraceId: traceId),
   cancellationToken);

4. Return sanitized ProblemDetails to client

### Never Log in ErrorLogs Table
- Full token strings
- ClientSecret or credential values  
- CredentialJson content
- Connection strings
- AES encryption key values
- User passwords

### IErrorLogService Registration
builder.Services.AddScoped<IErrorLogService, ErrorLogService>();

### Error Handling within IErrorLogService
If the DB write itself fails:
- Log to ILogger at Critical level only
- Never throw — logging failure must not affect the API response
- Swallow the exception silently after logging to ILogger:
  try { await _dbContext.ErrorLogs.AddAsync(entity, ct); }
  catch (Exception ex)
  {
      _logger.LogCritical(ex,
          "Failed to persist error log. TraceId: {TraceId}",
          entry.TraceId);
  }

## Review Checklist for Error Handling
When reviewing any file, verify:
- [ ] No raw Exception caught and swallowed silently
- [ ] No exception.Message returned directly in API response
- [ ] No stack trace in API response
- [ ] All AppException subtypes thrown at correct layer
- [ ] GlobalExceptionHandler registered in Program.cs
- [ ] Every catch block logs before rethrowing or handling
- [ ] throw used (not throw ex) to preserve stack trace
- [ ] ValidationException includes field-level error details
- [ ] DynamicsBusinessException message sanitized before returning
- [ ] traceId included in every error ProblemDetails response