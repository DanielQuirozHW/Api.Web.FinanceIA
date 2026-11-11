# Post-Generate Hook — Api.Web.ServiciosFinancieros

## Purpose
Automatically verify and complete generated code after Claude 
creates or modifies any .cs file. Ensures every generated file 
is consistent, complete and ready to compile without manual fixes.

## Trigger
Runs automatically after Claude generates or modifies any .cs file.
Can be invoked manually with: claude hook post-generate {filename}

## Checks to Execute (in order)

### 1. Namespace Verification
Verify the namespace matches the folder structure:

- File in Clases/ → namespace Api.Web.ServiciosFinancieros.Clases
- File in Controllers/ → namespace Api.Web.ServiciosFinancieros.Controllers
- File in Data/ → namespace Api.Web.ServiciosFinancieros.Data
- File in Data/Configurations/ → namespace Api.Web.ServiciosFinancieros.Data.Configurations
- File in Data/Repositories/ → namespace Api.Web.ServiciosFinancieros.Data.Repositories
- File in Models/ → namespace Api.Web.ServiciosFinancieros.Models
- File in Servicios/ → namespace Api.Web.ServiciosFinancieros.Servicios

If namespace does not match folder: correct it automatically.

### 2. File Header Verification
Verify the file contains the standard header at the top:
// Project : Api.Web.ServiciosFinancieros
// Created : {today's date in yyyy-MM-dd}
// Modified: {today's date in yyyy-MM-dd}

If header is missing: add it automatically at the top of the file.

### 3. Using Statements Cleanup
Verify using statements:
- Remove unused using statements
- Sort using statements alphabetically
- Group in this order:
  1. System namespaces
  2. Microsoft namespaces
  3. Third-party namespaces (FirebaseAdmin, Google, etc.)
  4. Internal namespaces (Api.Web.ServiciosFinancieros.*)
- Add blank line between each group

Apply automatically.

### 4. DI Registration Check
When a new service, repository or middleware class is generated,
verify it is registered in Program.cs:

- New class implementing an interface in Clases/:
  Check that builder.Services.Add{Lifetime}<IInterface, Implementation>()
  exists in Program.cs
  
- New repository in Data/Repositories/:
  Check that builder.Services.AddScoped<IRepository, Repository>()
  exists in Program.cs
  
- New middleware in Clases/ ending with "Middleware":
  Check that app.UseMiddleware<MiddlewareName>() exists in Program.cs
  and is placed in correct pipeline order

If registration is missing:
- Do NOT modify Program.cs automatically
- Report exactly what needs to be added and where:
  "Add to Program.cs in DI section:
   builder.Services.AddScoped<ITenantConfigRepository, TenantConfigRepository>();"

### 5. Interface Consistency Check
When a class implements an interface, verify:

- All interface methods are implemented in the class
- Method signatures match exactly (name, parameters, return type)
- CancellationToken is present in every async method signature
  in both interface and implementation

If inconsistency found: report and suggest the fix.

### 6. Nullable Reference Types Check
Verify the generated file is compatible with nullable reference types:

- All reference type parameters that can be null are marked with ?
- Null checks use ArgumentNullException.ThrowIfNull or 
  IsNullOrWhiteSpace guard clauses
- Return types that can be null are marked with ?

Report violations with suggested fixes.
Do not apply automatically — require developer confirmation.

### 7. Async Consistency Check
Verify async patterns in generated code:

- Every method returning Task or Task<T> has Async suffix
- Every async method accepts CancellationToken as last parameter
- No .Result or .Wait() usage
- No async void methods (except event handlers)
- CancellationToken is passed through to every awaited call

Fix Async suffix automatically.
Report CancellationToken issues without auto-fixing.

### 8. Sealed Class Check
Verify that classes are sealed unless:
- The class is a base class (has virtual members)
- The class is abstract
- The class explicitly requires inheritance

If a class is not sealed and none of the above apply:
Add sealed modifier automatically.

### 9. Record Immutability Check
When a record is generated, verify:
- All properties use init accessor, not set
- Constructor parameters match property names
- Record is sealed if it is a DTO or response model

Apply init accessor automatically if set is used on a record property.

### 10. Summary Report
After all checks complete, output:

=== POST-GENERATE REPORT: {FileName} ===

[AUTO-FIXED] Namespace corrected to Api.Web.ServiciosFinancieros.Clases
[AUTO-FIXED] File header added
[AUTO-FIXED] Using statements sorted and grouped
[AUTO-FIXED] sealed modifier added to TenantResolver
[MANUAL REQUIRED] Register in Program.cs:
  builder.Services.AddScoped<ITenantResolver, TenantResolver>();
[MANUAL REQUIRED] CancellationToken missing in ResolveAsync parameter
[INFO] Interface consistency: OK
[INFO] Async patterns: OK

=== FILES MODIFIED ===
- Clases/TenantResolver.cs (namespace, header, sealed, usings)

=== ACTION REQUIRED ===
1. Add DI registration to Program.cs (see above)
2. Add CancellationToken to ResolveAsync signature

## Important Rules
- Never modify Program.cs automatically
- Never modify interface files automatically when fixing implementations
- Always report what was auto-fixed vs what requires manual action
- Never remove code — only add or correct
- When in doubt about a fix: report and ask, never assume