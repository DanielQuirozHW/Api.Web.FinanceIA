# Template Usage Guide - How to Fork and Adapt

This guide explains how to use `Api.Web.ServiciosFinancieros` as a template for new projects.

---

## 🎯 Overview

This template provides a **complete multi-tenant API foundation** that you can fork and customize for specific projects.

**What you get:**
- 6-8 weeks of development already done
- Production-ready architecture
- Security patterns implemented
- Scalable infrastructure

**What you customize:**
- Namespaces and project names
- Business logic and entities
- Specific integrations
- UI/UX specific features

---

## 📋 Fork Checklist

### Phase 1: Copy and Rename (30 minutes)

#### Step 1: Copy the Template

```bash
# Option A: Clone from repository
git clone <template-repo-url> Api.Web.YourProjectName
cd Api.Web.YourProjectName
rm -rf .git  # Remove template git history
git init     # Start fresh git repo

# Option B: Copy locally
cp -r Api.Web.ServiciosFinancieros Api.Web.YourProjectName
cd Api.Web.YourProjectName
```

#### Step 2: Rename Project Files

```bash
# Rename .csproj file
mv Api.Web.ServiciosFinancieros.csproj Api.Web.YourProjectName.csproj

# Update .sln file (if you have one)
# Find and replace: Api.Web.ServiciosFinancieros → Api.Web.YourProjectName
```

#### Step 3: Rename Namespaces (IDE)

**In Visual Studio:**
1. Open `Api.Web.YourProjectName.csproj`
2. Right-click on project → Rename
3. Enter `Api.Web.YourProjectName`
4. Check "Update references"

**In VS Code / Rider:**
1. Find and Replace across entire solution
2. Find: `Api.Web.ServiciosFinancieros`
3. Replace: `Api.Web.YourProjectName`
4. Replace All

**Files to verify:**
- All `.cs` files (namespace declarations)
- `Program.cs`
- `.csproj` file
- Any `.sln` file
- `launchSettings.json`

#### Step 4: Update Configuration Files

**appsettings.json:**
```json
{
  "Jwt": {
    "Issuer": "https://api.yourcompany.com",  // ← Update
    "Audience": "yourproject-api"             // ← Update
  }
}
```

**launchSettings.json:**
```json
{
  "profiles": {
    "YourProjectName": {  // ← Update profile name
      "applicationUrl": "https://localhost:7001;http://localhost:5000"
    }
  }
}
```

---

### Phase 2: Configure Your Environment (1 hour)

#### Step 1: Set Up Database

**Create database in SQL Server:**
```sql
CREATE DATABASE YourProjectDatabase;
```

**Run schema scripts:**
```sql
USE YourProjectDatabase;

-- Tables
CREATE TABLE Credenciales (...);
CREATE TABLE TenantFirebaseConfig (...);
CREATE TABLE ErrorLogs (...);
```

See `/docs/guides/setup.md` for complete SQL scripts.

#### Step 2: Configure User Secrets

```bash
dotnet user-secrets init

# SQL Connection
dotnet user-secrets set "ConnectionStrings:Default" \
  "Server=YOUR_SERVER;Database=YourProjectDatabase;User Id=YOUR_USER;Password=YOUR_PASS;TrustServerCertificate=true"

# JWT Signing Key (generate a secure random string, min 32 chars)
dotnet user-secrets set "Jwt:SigningKey" \
  "your-secure-random-32-char-minimum-key-here"

# Encryption Master Key (generate a secure random string, min 32 chars)
dotnet user-secrets set "ENCRYPTION_MASTER_KEY" \
  "your-secure-random-32-char-minimum-key-here"
```

**Generate secure keys:**
```bash
# On Linux/Mac
openssl rand -base64 32

# On Windows (PowerShell)
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
```

#### Step 3: Set Up Firebase Project

1. Go to [Firebase Console](https://console.firebase.google.com)
2. Create new project: `YourProject`
3. Enable Authentication → Email/Password
4. Go to Project Settings → Service Accounts
5. Generate new private key → Download JSON
6. Save as `firebase-admin-sdk.json` (DO NOT commit to git)

#### Step 4: Set Up Dynamics 365

**Register App in Azure AD:**
1. Go to [Azure Portal](https://portal.azure.com)
2. Azure Active Directory → App registrations → New
3. Name: `YourProject API`
4. API Permissions → Add → Dynamics CRM → user_impersonation
5. Certificates & secrets → New client secret
6. Copy: Application (client) ID, Directory (tenant) ID, Client secret

**Configure Dynamics:**
1. System Settings → Security → Application Users
2. Create application user linked to Azure AD app
3. Assign security role (e.g., System Administrator)

#### Step 5: Set Up Firestore

1. Go to [Google Cloud Console](https://console.cloud.google.com)
2. Select your Firebase project
3. Firestore Database → Create database
4. Start in production mode
5. Choose location (same as Firebase)

**Create collection structure:**
```
usuarios/
  {uid}/
    uid: "firebase-uid"
    email: "user@example.com"
    contactid: "guid-from-dynamics"      // Optional
    accountid: "guid-from-dynamics"      // Optional
    new_empleadoid: "guid-from-dynamics" // Optional
```

---

### Phase 3: Configure Tenant (30 minutes)

#### Step 1: Encrypt Sensitive Data

```bash
cd EncryptionHelper
dotnet run

# Encrypt Dynamics client secret
encrypt your-dynamics-client-secret-here
# Output: encrypted-value-here

# Encrypt Firebase credential JSON
encrypt '{"type":"service_account","project_id":"...",...}'
# Output: encrypted-json-here

# Encrypt reCAPTCHA secret key
encrypt your-recaptcha-secret-key
# Output: encrypted-secret-here
```

#### Step 2: Insert Tenant Configuration

**Credenciales table:**
```sql
INSERT INTO Credenciales (
    TenantId, 
    DynamicsUrl, 
    DynamicsClientId, 
    DynamicsClientSecret,  -- ENCRYPTED
    AzureAdTenantId
) VALUES (
    'your-tenant-id',
    'https://yourorg.crm.dynamics.com',
    'your-azure-app-client-id',
    'ENCRYPTED_CLIENT_SECRET_HERE',
    'your-azure-directory-tenant-id'
);
```

**TenantFirebaseConfig table:**
```sql
INSERT INTO TenantFirebaseConfig (
    TenantId,
    FirebaseProjectId,
    CredentialJson,        -- ENCRYPTED
    RecaptchaSecretKey,    -- ENCRYPTED
    IsActive
) VALUES (
    'your-tenant-id',
    'your-firebase-project-id',
    'ENCRYPTED_FIREBASE_JSON_HERE',
    'ENCRYPTED_RECAPTCHA_SECRET_HERE',
    1
);
```

---

### Phase 4: Customize Business Logic (varies)

#### Step 1: Extend MetaRegistry

Add your Dynamics entities to `Servicios/MetaRegistry.cs`:

```csharp
public static readonly Dictionary<string, EntityMeta> Entities = new()
{
    // Existing entities...
    
    // Your custom entities
    ["opportunity"] = new EntityMeta
    {
        EntityLogicalName = "opportunity",
        EntitySetName = "opportunities",
        Selectable = new[] { "name", "estimatedvalue", "closeprobability", "statuscode" },
        Filterable = new[] { "name", "estimatedvalue", "statecode", "statuscode" },
        Sortable = new[] { "name", "estimatedvalue", "createdon" }
    },
    
    ["quote"] = new EntityMeta
    {
        EntityLogicalName = "quote",
        EntitySetName = "quotes",
        Selectable = new[] { "name", "totalamount", "quotenumber" },
        Filterable = new[] { "name", "statecode", "statuscode" },
        Sortable = new[] { "name", "createdon" }
    }
};
```

#### Step 2: Add Custom Security Rules

Extend `Servicios/SecurityRegistry.cs`:

```csharp
public static readonly Dictionary<string, SecurityRule> Rules = new()
{
    // Existing rules...
    
    // Custom rules
    ["opportunity"] = new SecurityRule
    {
        EntityName = "opportunity",
        UserFilterField = "ownerid",
        RequiresOwnerFilter = true
    },
    
    ["quote"] = new SecurityRule
    {
        EntityName = "quote",
        UserFilterField = "customerid",
        RequiresOwnerFilter = true
    }
};
```

#### Step 3: Add Custom Controllers (Optional)

Create project-specific controllers:

```csharp
// Controllers/OpportunityController.cs
namespace Api.Web.YourProjectName.Controllers;

[ApiController]
[Route("api/opportunities")]
[Authorize]
public sealed class OpportunityController : ControllerBase
{
    private readonly IDynamicsClient _dynamicsClient;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    
    // Custom business logic here...
}
```

#### Step 4: Add Custom Services (Optional)

Create domain-specific services:

```csharp
// Servicios/QuoteCalculationService.cs
namespace Api.Web.YourProjectName.Servicios;

public interface IQuoteCalculationService
{
    Task<decimal> CalculateTotalAsync(string quoteId, CancellationToken cancellationToken);
}

public sealed class QuoteCalculationService : IQuoteCalculationService
{
    // Implementation...
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddScoped<IQuoteCalculationService, QuoteCalculationService>();
```

---

### Phase 5: Test and Verify (1 hour)

#### Step 1: Build and Run

```bash
dotnet build
# Should compile with 0 errors, 0 warnings

dotnet run
# Should start successfully
```

#### Step 2: Test Health Checks

```bash
curl http://localhost:5079/health
# Expected: "Healthy"

curl http://localhost:5079/ready
# Expected: JSON with all checks "Healthy"
```

#### Step 3: Test Authentication Flow

**Get Firebase token** (use Firebase SDK in your frontend):
```javascript
// Example with Firebase JS SDK
const userCredential = await signInWithEmailAndPassword(auth, email, password);
const firebaseToken = await userCredential.user.getIdToken();
```

**Exchange for Internal JWT:**
```bash
curl -X POST http://localhost:5079/api/bff/auth/token \
  -H "X-Tenant-ID: your-tenant-id" \
  -H "Authorization: Bearer {firebase-token}" \
  -H "Content-Type: application/json"

# Expected: { "token": "eyJ..." }
```

#### Step 4: Test Search

```bash
curl -X POST http://localhost:5079/api/search \
  -H "X-Tenant-ID: your-tenant-id" \
  -H "Authorization: Bearer {internal-jwt}" \
  -H "Content-Type: application/json" \
  -d '{
    "entity": "contact",
    "select": ["fullname", "emailaddress1"],
    "page": 1,
    "pageSize": 10
  }'

# Expected: Search results with RLS applied
```

#### Step 5: Test Rate Limiting

Make 151 requests rapidly:
```bash
for i in {1..151}; do
  curl -X POST http://localhost:5079/api/search \
    -H "X-Tenant-ID: your-tenant-id" \
    -H "Authorization: Bearer {internal-jwt}" \
    -H "Content-Type: application/json" \
    -d '{"entity":"contact","select":["fullname"],"page":1,"pageSize":1}'
  echo "Request $i"
done

# Expected: First 150 succeed, 151st returns 429 Too Many Requests
```

---

## 🎨 Customization Examples

### Example 1: Add Custom Endpoint

```csharp
// Controllers/ReportsController.cs
[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController : ControllerBase
{
    [HttpGet("sales-summary")]
    public async Task<IActionResult> GetSalesSummary(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken cancellationToken)
    {
        // Custom report logic using DynamicsClient
        // Returns formatted data specific to your business
    }
}
```

### Example 2: Add Custom Middleware

```csharp
// Clases/Middleware/RequestLoggingMiddleware.cs
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantId = context.Items["TenantContext"] as TenantContext;
        
        _logger.LogInformation(
            "Request: {Method} {Path} | Tenant: {TenantId}",
            context.Request.Method,
            context.Request.Path,
            tenantId?.TenantId);

        await _next(context);
    }
}

// In Program.cs
app.UseMiddleware<RequestLoggingMiddleware>();
```

### Example 3: Override Rate Limits for Specific Tenant

```csharp
// Option 1: In configuration (for all)
"RateLimiting": {
  "PerUser": {
    "Search": 300  // Increase from 150
  }
}

// Option 2: In code (per tenant - future enhancement)
// Add column to Credenciales table: RateLimitOverride
// Read in RateLimitService and apply tenant-specific limits
```

---

## 🚀 Deployment

### Development
```bash
dotnet run --environment Development
```

### Staging
```bash
dotnet publish -c Release -o ./publish
# Deploy to staging environment
# Configure Azure App Service with staging connection strings
```

### Production
```bash
dotnet publish -c Release -o ./publish
# Deploy to production environment
# Use Azure Key Vault for secrets
# Enable Application Insights
# Configure CDN/Load Balancer
```

See `/docs/guides/deployment-azure.md` for complete deployment guide (coming soon).

---

## 📚 What to Keep vs. Modify

### ✅ Keep (Core Template Features)

- **Middleware pipeline order**
- **Authentication flow** (Firebase → Internal JWT)
- **Rate limiting logic**
- **Health check infrastructure**
- **Exception handling**
- **Security patterns** (RLS, encryption, validation)
- **Database schema** (Credenciales, TenantFirebaseConfig, ErrorLogs)

### 🎨 Customize (Project-Specific)

- **Namespaces** (rename to your project)
- **MetaRegistry** (add your Dynamics entities)
- **SecurityRegistry** (add your RLS rules)
- **Custom controllers** (your business logic)
- **Custom services** (domain-specific)
- **Rate limit values** (adjust to your needs)
- **JWT expiration times** (based on security requirements)

### ❌ Don't Modify (Unless You Know What You're Doing)

- **TenantResolutionMiddleware** core logic
- **FirebaseAuthenticationHandler** validation
- **SmartJwt policy scheme** selector
- **SearchSecurityEnforcer** RLS injection
- **FetchXmlSafeBuilder** security validations
- **Encryption/Decryption** algorithms

---

## 🆘 Troubleshooting

### "Tenant not found" Error

**Check:**
1. Tenant exists in `Credenciales` table
2. `X-Tenant-ID` header matches `TenantId` in DB
3. Tenant data is properly encrypted

### "User has no valid identity IDs" Error

**Check:**
1. User document exists in Firestore `usuarios/{uid}`
2. At least one ID field exists: `contactid`, `accountid`, or `new_empleadoid`
3. UID in JWT matches Firestore document UID

### Rate Limit Always Returns 429

**Check:**
1. Rate limit configuration in `appsettings.json`
2. Memory cache is working (restart app)
3. System clock is correct (rate limits use timestamps)

### Health Checks Show "Unhealthy"

**Check:**
1. Database connection string is correct
2. Dynamics credentials are valid and not expired
3. Firebase credentials JSON is valid
4. Firestore is accessible from your network

---

## 📈 Next Steps After Fork

1. ✅ Customize for your project (namespaces, config)
2. ✅ Add your business logic (controllers, services)
3. ✅ Extend MetaRegistry with your entities
4. ✅ Write project-specific tests
5. ✅ Deploy to staging environment
6. ✅ Load test and optimize
7. ✅ Deploy to production

---

## 🤝 Contributing Back to Template

If you create improvements that are **generic and reusable**:

1. Extract the generic code
2. Test it in the template
3. Submit a PR to the template repository
4. Help other projects benefit from your work

**Examples of good contributions:**
- Better error handling patterns
- Performance optimizations
- Security enhancements
- Additional health checks
- Improved logging

**Examples of project-specific (don't contribute back):**
- Business logic specific to your domain
- Custom Dynamics entities for your industry
- UI-specific endpoints
- Client-specific integrations

---

## 📄 License

[Your License Here]

---

**Happy building! 🚀**
