# Command: Review Code — Api.Web.ServiciosFinancieros

## Purpose
Perform a comprehensive code quality review on a specific file
or the entire project. Validates adherence to coding standards,
architecture patterns, naming conventions and best practices
defined in the project skills.

## Usage
/review-code {filepath}
/review-code Controllers/BffController.cs
/review-code Clases/TenantResolver.cs
/review-code --all

## Review Checklist

### 1. Naming Conventions
Verify all naming follows project standards:

Classes & Interfaces:
- Classes use PascalCase
- Interfaces use PascalCase with I prefix
- Abstract classes use PascalCase (no prefix/suffix)
- Generic type parameters prefixed with T

Fields & Properties:
- Private fields use _camelCase (underscore required)
- Public properties use PascalCase
- Boolean properties/fields start with is, has or can
- Constants use PascalCase

Methods:
- All methods use PascalCase
- Async methods end with Async suffix
- Boolean-returning methods start with Is, Has, Can or Try

Local Variables & Parameters:
- All use camelCase
- No single-letter variables except loop counters (i, j, k)
- No abbreviations unless universally known (id, url, dto, http)
- CancellationToken parameter always named cancellationToken

Severity for violations: MEDIUM
Report: file name, line number, current name, suggested name

### 2. Code Formatting & Structure
Verify formatting follows Allman style:

Braces:
- Opening brace always on new line
- Closing brace on its own line
- No single-line if statements without braces

Member Ordering:
- Private fields first
- Constructor(s) second
- Public properties third
- Public methods fourth
- Private methods last
- Blank line between each member

File Structure:
- One class per file
- File name matches class name exactly
- Standard file header present
- Using statements grouped and sorted

Severity for violations: LOW
Report: file name, line number, description of violation

### 3. Architecture & Pattern Compliance
Verify the file follows multi-tenant BFF architecture:

Controller Rules:
- No business logic in controllers
- No direct DbContext injection
- No direct HttpClient injection (use IHttpClientFactory)
- No TenantContext resolution (must come from ITenantContextAccessor)
- No hardcoded tenant names, client IDs or scope names
- Every public endpoint has explicit [Authorize] or [AllowAnonymous]
- Every public endpoint has [EnableRateLimiting]
- Every action method returns IActionResult or Task<IActionResult>
- Every action method has CancellationToken parameter

Service Rules:
- No HttpContext access in services
- No ITenantContextAccessor injection in services
  (receive TenantContext as method parameter)
- No direct IConfiguration injection
  (use IOptions<T> or receive config via constructor from DI)
- Interface and implementation follow same method signatures
- No static methods with side effects

Repository Rules:
- Always use AsNoTracking() for read queries
- Never return EF entities directly
  (always project to records or DTOs)
- Always pass CancellationToken to EF async methods
- Never use synchronous EF methods

Middleware Rules:
- Always call next(context) unless intentionally short-circuiting
- Always resolve TenantContext before calling next
- Never throw unhandled exceptions (use try/catch + proper response)
- Short-circuit with ProblemDetails on validation failures

Severity for violations: HIGH
Report: file name, line number, violation description, suggested fix

### 4. Async & Threading Patterns
Verify async code follows project standards:

- Every method returning Task or Task<T> has Async suffix
  Severity: MEDIUM

- Every async method has CancellationToken as last parameter
  Severity: HIGH

- CancellationToken passed to every awaited call
  Severity: HIGH

- No .Result or .Wait() usage
  Severity: HIGH (can cause deadlocks)

- No async void (except event handlers)
  Severity: HIGH

- No unnecessary await (return Task directly when no post-await work)
  Severity: LOW

- ConfigureAwait(false) used in service/library code
  Severity: LOW

### 5. Null Safety & Guard Clauses
Verify null handling follows project standards:

- ArgumentNullException.ThrowIfNull used for object parameters
  at public method entry points
  Severity: MEDIUM

- ArgumentException.ThrowIfNullOrWhiteSpace used for string parameters
  at public method entry points
  Severity: MEDIUM

- Nullable reference types enabled and respected
  Severity: MEDIUM

- No null-forgiving operator (!) without comment explaining why
  Severity: LOW

- Guard clauses used over nested if blocks
  Severity: LOW

- Null checks at the top of methods, not in the middle
  Severity: LOW

### 6. Dependency Injection & Lifetime
Verify DI usage:

- Services registered with correct lifetime:
  Singleton: only for truly stateless, thread-safe services
  Scoped: for tenant-aware or request-scoped services
  Transient: for lightweight stateless utilities
  Severity: HIGH (wrong lifetime = bugs in multi-tenant scenarios)

- No IServiceProvider.GetService inside classes (service locator)
  Severity: HIGH

- No new keyword for services that should be injected
  Severity: HIGH

- Constructor parameters match registered services
  Severity: HIGH

### 7. Error Handling
Verify error handling patterns:

- Try/catch only at boundaries (controllers, middleware)
  Severity: MEDIUM

- No empty catch blocks
  Severity: HIGH

- No throw ex (use throw to preserve stack trace)
  Severity: MEDIUM

- All errors logged before handling or rethrowing
  Severity: MEDIUM

- ProblemDetails used for all error API responses
  Severity: MEDIUM

- No internal exception details in API responses
  Severity: HIGH

### 8. Logging Quality
Verify logging follows structured logging standards:

- ILogger<T> used (not Console.Write or Debug.Write)
  Severity: HIGH

- Structured parameters used (not string interpolation in message):
  WRONG:  _logger.LogInformation($"Tenant {tenantId} resolved")
  CORRECT: _logger.LogInformation("Tenant {TenantId} resolved", tenantId)
  Severity: MEDIUM

- TenantId included in every log statement inside tenant-aware code
  Severity: MEDIUM

- Correct log level used:
  Trace/Debug: development diagnostic info
  Information: significant business events
  Warning: unexpected but recoverable
  Error: failures needing attention
  Critical: system failures
  Severity: LOW

- No sensitive data in log parameters
  Severity: HIGH (covered in detail by review-security command)

### 9. SOLID Principles
Verify SOLID compliance:

Single Responsibility:
- Each class has one clear responsibility
- Classes are not doing too many things
  Severity: MEDIUM

Open/Closed:
- New behavior added via new classes, not modifying existing ones
  Severity: LOW

Liskov Substitution:
- Interface implementations honor the interface contract
  Severity: HIGH

Interface Segregation:
- Interfaces are focused, not bloated with unrelated methods
  Severity: MEDIUM

Dependency Inversion:
- Dependencies on abstractions (interfaces), not concretions
  Severity: HIGH

### 10. Code Smells
Flag common code smells:

- Magic numbers or strings (use named constants)
  Severity: LOW

- Long methods (over 40 lines — suggest extraction)
  Severity: MEDIUM

- Long parameter lists (over 4 parameters — suggest object)
  Severity: LOW

- Duplicate code blocks (suggest extraction to shared method)
  Severity: MEDIUM

- Deep nesting (over 3 levels — suggest guard clauses or extraction)
  Severity: MEDIUM

- TODO comments without issue reference
  Severity: LOW

- Commented-out code
  Severity: LOW (remove it — use git history instead)

- Inconsistent naming within same file
  Severity: MEDIUM

## Output Format

=== CODE REVIEW: {filename} ===
Reviewed : {date}
Reviewer : Claude Code Reviewer
Standards: dotnet-coding-standards + multi-tenant-bff + ef-core-patterns

HIGH (address before merge):
  [HIGH-001] Line 34 — No CancellationToken in ResolveAsync
  Current : public async Task<TenantContext> ResolveAsync(string tenantId)
  Expected: public async Task<TenantContext> ResolveAsync(
                string tenantId,
                CancellationToken cancellationToken)

  [HIGH-002] Line 67 — TenantContext resolved inside service
  Current : var ctx = _httpContextAccessor.HttpContext?.Items["TenantContext"]
  Expected: Receive TenantContext as method parameter from controller

MEDIUM (fix in current iteration):
  [MED-001] Line 12 — Private field missing underscore prefix
  Current : private readonly ILogger<TenantResolver> logger;
  Expected: private readonly ILogger<TenantResolver> _logger;

  [MED-002] Line 89 — String interpolation in log message
  Current : _logger.LogInformation($"Resolved tenant {tenantId}");
  Expected: _logger.LogInformation("Resolved tenant {TenantId}", tenantId);

LOW (fix when convenient):
  [LOW-001] Line 23 — Magic string "tenant:" should be a constant
  Current : var cacheKey = "tenant:" + tenantId;
  Expected: private const string TenantCacheKeyPrefix = "tenant:";
            var cacheKey = TenantCacheKeyPrefix + tenantId;

=== SUMMARY ===
High   : 2 — must fix before merge
Medium : 2 — fix in current iteration  
Low    : 1 — fix when convenient
Total  : 5

Overall Quality: NEEDS WORK
Blocking issues: 2