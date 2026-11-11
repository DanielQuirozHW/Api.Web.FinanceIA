# Pre-Commit Hook — Api.Web.ServiciosFinancieros

## Purpose
Validate code quality, security and standards compliance 
before every commit. Catch issues early, never let them reach the repository.

## Trigger
Runs automatically before every git commit.
Can be invoked manually with: claude hook pre-commit

## Checks to Execute (in order)

### 1. Secrets & Credentials Scan
Scan every staged file for hardcoded sensitive data.
Reject commit if any of the following patterns are found:

- Hardcoded connection strings:
  "Server=", "Data Source=", "Initial Catalog=" outside of 
  appsettings.template.json and appsettings.Development.json
  
- Hardcoded API keys or secrets:
  any string matching patterns like:
  "secret": "{value longer than 10 chars that is not a placeholder}",
  "key": "{value longer than 10 chars that is not a placeholder}",
  "password": "{value longer than 10 chars that is not a placeholder}"
  
- Firebase credential JSON content:
  "private_key", "private_key_id", "client_email" outside of 
  test fixtures or template files
  
- Azure AD credentials:
  Tenant GUIDs in format xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
  hardcoded in .cs files outside of test fixtures
  
- reCAPTCHA secret keys:
  strings starting with "6L" with length > 30 in .cs files

If any pattern is found:
- Block the commit
- Show the file name and line number
- Show the pattern that was matched
- Suggest using environment variables or User Secrets instead

### 2. Naming Conventions Check
Scan every staged .cs file and verify:

- Private fields use _camelCase prefix (underscore required)
  Flag any private field not starting with underscore
  
- Async methods end with Async suffix
  Flag any method returning Task or Task<T> without Async suffix
  
- Interfaces start with I prefix
  Flag any interface not starting with I
  
- Boolean properties/variables start with is, has or can
  Flag obvious violations (e.g. "bool active", "bool enabled", "bool valid")

Report violations as warnings (do not block commit, only warn)

### 3. Security Patterns Check
Scan every staged .cs file and verify:

- No usage of FirebaseAuth.DefaultInstance
  Flag: use TenantFirebaseRegistry to get per-tenant FirebaseAuth instance
  
- No usage of FirebaseApp.DefaultInstance  
  Flag: use named FirebaseApp instances via TenantFirebaseRegistry

- No usage of Microsoft.IdentityModel.Clients.ActiveDirectory (ADAL)
  Flag: migrate to Microsoft.Identity.Client (MSAL)
  
- No usage of .Result or .Wait() on Task
  Flag: use await instead
  
- No usage of async void methods (except event handlers)
  Flag: use async Task instead
  
- No string concatenation for building SQL queries
  Flag: use EF Core parameterized queries

Block commit if any security pattern violation is found.

### 4. Sensitive Data Logging Check
Scan every staged .cs file for dangerous log patterns:

- LogInformation/LogWarning/LogError/LogDebug calls that include:
  "token", "secret", "password", "credential", "key", "connectionString"
  as variable names in string interpolation or structured log parameters

Report as warnings with suggestion to sanitize log parameters.

### 5. Copyright & File Header Check
Verify every new .cs file contains the standard file header:
// Project : Api.Web.ServiciosFinancieros
// Created : {date}
// Modified: {date}

Add automatically if missing (do not block commit).

### 6. TODO / FIXME Check
Scan staged files for:
- TODO comments
- FIXME comments  
- HACK comments
- hardcoded strings like "sandbox", "test123", "temp", "temporal"

Report as warnings with file name and line number.
Do not block commit, only inform.

## Output Format
Always output results in this format:

=== PRE-COMMIT VALIDATION ===

[PASS] Secrets scan
[PASS] Security patterns  
[WARN] Naming conventions
  → BffController.cs:45 — private field "logger" should be "_logger"
[WARN] TODO comments found
  → TenantResolver.cs:78 — TODO: add retry logic
[BLOCK] Hardcoded credentials
  → appsettings.json:12 — possible secret detected in "clientSecret" field

=== RESULT: BLOCKED ===
Fix the issues marked as [BLOCK] before committing.

## On Block
When a commit is blocked:
1. Show exactly which files and lines caused the block
2. Explain why it was blocked
3. Provide the specific fix required
4. Never modify files automatically on block — only report