# Tool: Add Tenant Feature — Api.Web.ServiciosFinancieros

## Purpose
Generate a complete vertical slice for a new tenant-aware feature.
Creates all required files: models, repository or service, 
controller and registers everything in Program.cs guidance.
Ensures the feature is fully integrated with the multi-tenant 
architecture from day one.

## Usage
claude tool add-tenant-feature {FeatureName} {type} {scope}

Examples:
  claude tool add-tenant-feature Factura dynamics authenticated
  claude tool add-tenant-feature Cuenta dynamics authenticated
  claude tool add-tenant-feature Onboarding unauthenticated unauthenticated
  claude tool add-tenant-feature TenantConfig repository admin

## Parameters
- FeatureName: PascalCase name of the business entity or feature
- type:
  - dynamics: data comes from Dynamics 365 API
  - repository: data comes from SQL Server via EF Core
  - external: data comes from any other external HTTP API
  - internal: pure business logic, no external data source
- scope:
  - authenticated: requires Internal JWT
  - unauthenticated: reCAPTCHA protected
  - admin: requires AdminClient policy

## Files Generated

For claude tool add-tenant-feature Factura dynamics authenticated,
the following files are generated in order:

1. Models/{FeatureName}Models.cs
   Contains all request, response and result records for the feature:
   
   public sealed record {FeatureName}Response(
       string Id,
       // TODO: add properties matching Dynamics entity
   );
   
   public sealed record {FeatureName}Request(
       // TODO: add request properties
   );
   
   public sealed record {FeatureName}Result(
       bool IsSuccess,
       {FeatureName}Response? Data,
       string? ErrorMessage
   );

2. Servicios/{FeatureName}Service.cs (for dynamics/external types)
   OR
   Data/Repositories/{FeatureName}Repository.cs (for repository type)
   OR
   Clases/{FeatureName}Service.cs (for internal type)
   
   Generated using scaffold-service tool rules.
   Interface and implementation in same file.

3. Controllers/{FeatureName}Controller.cs
   Generated using scaffold-controller tool rules.
   Scope matches the scope parameter.
   Injects the generated service/repository.

## Execution Steps

### Step 1 — Confirm feature details
Before generating, ask developer to confirm:
- What Dynamics 365 entity or SQL table does this feature map to?
- What HTTP methods are needed? (GET, POST, PUT, DELETE)
- Are there any specific authorization requirements beyond the scope?
- Are there any specific reCAPTCHA requirements for unauthenticated scope?

### Step 2 — Generate Models
Generate {FeatureName}Models.cs in Models/ folder.
Apply post-generate hook automatically.

### Step 3 — Generate Service or Repository
Generate service or repository based on type parameter.
Apply post-generate hook automatically.

### Step 4 — Generate Controller
Generate controller based on scope parameter.
Inject the generated service/repository.
Apply post-generate hook automatically.

### Step 5 — Generate DI Registration Guidance
Output the exact lines to add to Program.cs:

=== PROGRAM.CS UPDATES REQUIRED ===

// Add in DI registration section:
builder.Services.AddScoped<I{FeatureName}Service, {FeatureName}Service>();

// For dynamics/external type, add HttpClient if not already registered:
builder.Services.AddHttpClient("{FeatureName}Service", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

=== END PROGRAM.CS UPDATES ===

### Step 6 — Generate Feature Summary
Output a complete summary of what was generated:

=== FEATURE SUMMARY: {FeatureName} ===

FILES CREATED:
  Models/{FeatureName}Models.cs
    - {FeatureName}Request (record)
    - {FeatureName}Response (record)  
    - {FeatureName}Result (record)

  Servicios/{FeatureName}Service.cs
    - I{FeatureName}Service (interface)
    - {FeatureName}Service (implementation)
    Methods:
      - GetAsync(id, tenantContext, cancellationToken)

  Controllers/{FeatureName}Controller.cs
    - {FeatureName}Controller (sealed)
    Endpoints:
      - GET api/{featurename}
      - POST api/{featurename} (if applicable)

MANUAL STEPS REQUIRED:
  1. Add DI registrations to Program.cs (see above)
  2. Implement TODO sections in service methods
  3. Map Dynamics/SQL response to {FeatureName}Response record
  4. Add any feature-specific validation logic

TENANT INTEGRATION:
  ✅ TenantContext injected via ITenantContextAccessor
  ✅ TenantId included in all log statements
  ✅ reCAPTCHA validated (if unauthenticated scope)
  ✅ Rate limiting applied
  ✅ ProblemDetails error responses
  ✅ CancellationToken propagated

=== END FEATURE SUMMARY ===

## Quality Rules Applied
- Every generated file passes pre-commit hook checks
- Every generated file passes post-generate hook checks
- No hardcoded tenant names, client IDs or secrets
- No business logic in controllers
- No direct DbContext injection in controllers
- No ADAL references
- No FirebaseAuth.DefaultInstance references
- All async methods have CancellationToken
- All entry points have guard clauses
- All log statements include TenantId
- All error responses use ProblemDetails

## Limitations
This tool does not:
- Modify existing files (only creates new ones)
- Register services in Program.cs automatically
- Create database tables or EF migrations
- Create unit test files (use scaffold-tests tool for that)
- Map Dynamics 365 entity fields automatically
  (developer must implement TODO sections)