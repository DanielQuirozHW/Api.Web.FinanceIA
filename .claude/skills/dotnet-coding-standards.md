# .NET Coding Standards — Api.Web.ServiciosFinancieros

## Naming Conventions
- Classes, interfaces, methods, properties, constants: PascalCase
- Private fields: _camelCase (always underscore prefix)
- Local variables and parameters: camelCase
- Async methods: always suffix with Async (e.g. ValidateTokenAsync)
- Interfaces: always prefix with I (e.g. IFirebaseTokenValidator)
- Boolean variables/properties: prefix with is, has or can 
  (e.g. isValid, hasExpired, canRetry)
- Generic type parameters: prefix with T (e.g. TEntity, TResult)

## Code Formatting
- Allman style braces: opening brace always on new line
- One class per file, file name must match class name exactly
- Blank line between methods
- Blank line between logical blocks inside a method
- Member ordering inside a class:
  1. Private fields
  2. Constructor(s)
  3. Public properties
  4. Public methods
  5. Private methods
- Always use explicit access modifiers (public, private, internal, protected)
- Expression-bodied members only for single-line getters or simple delegates
- Always use var when the type is obvious from the right side
- Never use var when the type is not immediately obvious

## Language Features (.NET 8)
- Use sealed classes unless inheritance is explicitly required
- Use primary constructors for simple DI injection
- Use records for DTOs, responses and immutable config models
- Use required modifier for mandatory properties in records
- Use IReadOnlyCollection<T> for collections that should not be modified
- Use IEnumerable<T> only for deferred execution scenarios
- Use pattern matching over type casting
- Use switch expressions over switch statements when returning a value
- Prefer collection expressions [] over new List<T>() where applicable
- Always enable nullable reference types (<Nullable>enable</Nullable>)
- Always treat warnings as errors in production builds

## Async Patterns
- Always use CancellationToken in every async method signature
- Always propagate CancellationToken through the entire call chain
- Never use .Result or .Wait() — always await
- Never use async void except for event handlers
- Use ConfigureAwait(false) in library/service code
- Always suffix async methods with Async

## Logging
- Always use ILogger<T> — never Console.Write or Debug.Write
- Never log sensitive data: tokens, passwords, keys, credentials, PII
- Use structured logging with named parameters:
  _logger.LogInformation("Tenant {TenantId} resolved in {ElapsedMs}ms", tenantId, elapsed)
- Log levels:
  - Trace: detailed internal flow (development only)
  - Debug: diagnostic information
  - Information: significant business events
  - Warning: unexpected but recoverable situations
  - Error: failures that need attention
  - Critical: system-level failures

## Error Handling
- Use guard clauses at method entry points — fail fast
- Throw InvalidOperationException for missing required configuration
- Throw ArgumentNullException for null arguments (use ArgumentNullException.ThrowIfNull)
- Throw ArgumentException for invalid arguments
- Never swallow exceptions silently — always log before rethrowing
- Never expose internal exception details in API responses
- Never use throw ex — always use throw to preserve stack trace
- Use try/catch only at boundaries (controllers, middleware, background services)

## Dependency Injection
- Register services in Program.cs using extension methods 
  when the registration block exceeds 5 lines
- Lifetimes:
  - Singleton: registries, caches, encryption services (stateless, thread-safe)
  - Scoped: tenant-aware services, repositories, DbContext
  - Transient: stateless validators, lightweight utilities
- Never inject IConfiguration directly into classes below Program.cs
  — use strongly typed options (IOptions<T>) instead
- Never use service locator pattern (IServiceProvider.GetService inside classes)

## Validation
- Use string.IsNullOrWhiteSpace over string.IsNullOrEmpty always
- Use ArgumentNullException.ThrowIfNull for null checks at entry points
- Validate all inputs at controller level before passing to services
- Never trust data coming from headers, tokens or external sources 
  without explicit validation