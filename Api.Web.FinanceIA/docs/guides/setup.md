# Local Development Setup Guide

## Prerequisites

List required software with versions:
- .NET 8 SDK
- SQL Server 2019+ (local or Azure)
- Visual Studio 2022 / VS Code / Rider
- Git
- Postman (for testing)
- Firebase project with Service Account
- Google reCAPTCHA V3 site

## Step 1 — Clone Repository

```bash
git clone [repository-url]
cd Api.Web.FinanceIA
git checkout develop
```

## Step 2 — Database Setup

### 2.1 Create Database

```sql
CREATE DATABASE ServiciosFinancieros;
GO
```

### 2.2 Execute DDL Scripts

Execute in order:
1. `docs/database/TenantFirebaseConfig.sql`
2. `docs/database/ErrorLog_AlterTraceId.sql`

Note: Credenciales table should already exist (legacy table).
If not, create it manually.

### 2.3 Verify Tables

```sql
USE ServiciosFinancieros;
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE';
```

Should show: Credenciales, TenantFirebaseConfig, ErrorLogs

## Step 3 — Configure Secrets

### 3.1 Initialize User Secrets

```bash
cd Api.Web.FinanceIA
dotnet user-secrets init
```

### 3.2 Set Required Secrets

```bash
# Database connection
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Database=ServiciosFinancieros;User Id=sa;Password=YourPassword;TrustServerCertificate=True;"

# JWT signing key (min 32 characters)
dotnet user-secrets set "Jwt:SigningKey" "your-super-secret-256-bit-key-here-minimum-32-chars"

# Encryption master key (min 32 characters)
dotnet user-secrets set "ENCRYPTION_MASTER_KEY" "your-encryption-master-key-32-chars-minimum"
```

### 3.3 Verify Secrets

```bash
dotnet user-secrets list
```

## Step 4 — Encrypt Tenant Credentials

Use EncryptionHelper to encrypt sensitive data before inserting into DB.

### 4.1 Encrypt Dynamics Client Secret

```bash
cd EncryptionHelper
dotnet run "your-ENCRYPTION_MASTER_KEY" "dynamics-client-secret"
```

Copy the Base64 output.

### 4.2 Encrypt Firebase Service Account JSON

First, convert JSON to single line:
```powershell
$json = Get-Content "path\to\firebase-service-account.json" -Raw
$json = $json -replace "`r`n", "" -replace "`n", ""
cd ..\EncryptionHelper
dotnet run "your-ENCRYPTION_MASTER_KEY" $json
```

Copy the Base64 output.

### 4.3 Encrypt reCAPTCHA Secret Key

```bash
cd EncryptionHelper
dotnet run "your-ENCRYPTION_MASTER_KEY" "recaptcha-secret-key"
```

Copy the Base64 output.

## Step 5 — Insert Test Tenant

### 5.1 Insert into Credenciales

```sql
INSERT INTO Credenciales (url, clientid, clientsecret, tenantid, cliente)
VALUES (
    'https://yourorg.crm.dynamics.com',
    'your-dynamics-client-id',
    'ENCRYPTED_CLIENT_SECRET_BASE64_HERE',
    'your-azure-ad-tenant-guid',
    'tenant-prueba'
);
```

### 5.2 Insert into TenantFirebaseConfig

```sql
INSERT INTO TenantFirebaseConfig
    (Cliente, ProjectId, CredentialJson, RecaptchaSecretKey, IsActive)
VALUES (
    'tenant-prueba',
    'your-firebase-project-id',
    'ENCRYPTED_FIREBASE_JSON_BASE64_HERE',
    'ENCRYPTED_RECAPTCHA_KEY_BASE64_HERE',
    1
);
```

### 5.3 Verify Insert

```sql
SELECT * FROM Credenciales WHERE cliente = 'tenant-prueba';
SELECT * FROM TenantFirebaseConfig WHERE Cliente = 'tenant-prueba';
```

## Step 6 — Configure Firestore (for Search)

### 6.1 Create Test User Document

In Firebase Console → Firestore:

Collection: `usuarios`
Document ID: auto-generated
Fields:
```json
{
  "uid": "firebase-uid-of-test-user",
  "contactid": "guid-of-contact-in-dynamics",
  "accountid": "guid-of-account-in-dynamics",
  "email": "testuser@example.com"
}
```

Note: `contactid` and `accountid` must be valid GUIDs from Dynamics 365.

## Step 7 — Build and Run

### 7.1 Restore and Build

```bash
cd Api.Web.FinanceIA
dotnet restore
dotnet build
```

Should see: `Build succeeded. 0 Warning(s). 0 Error(s).`

### 7.2 Run Application

```bash
dotnet run
```

Application should start on `https://localhost:7xxx`

### 7.3 Open Swagger

Navigate to: `https://localhost:7xxx/swagger`

## Step 8 — Test Endpoints

### 8.1 Test Tenant Resolution
POST https://localhost:7xxx/api/bff/auth/onboarding-token
Headers:
Content-Type: application/json
X-Tenant-ID: tenant-inexistente
Body:
{
"recaptchaToken": "test"
}
Expected: `404 Not Found` with `"errorCode": "tenant_not_found"`

### 8.2 Test Firebase Authentication

Requires generating a real Firebase ID token from frontend.

See [Authentication Testing Guide](./authentication-testing.md) for details.

## Common Issues

### Issue: "ENCRYPTION_MASTER_KEY environment variable is not configured"

**Solution:** The key is in user-secrets, but EncryptionService reads from environment.
Fix applied: EncryptionService now reads from IConfiguration.

### Issue: "Tenant not found" with valid X-Tenant-ID

**Solution:** Check that Credenciales.cliente matches X-Tenant-ID exactly (case-sensitive).

### Issue: reCAPTCHA score is 0

**Solution:**
1. Verify `localhost` is in allowed domains in reCAPTCHA admin
2. Wait 5-10 minutes for Google to propagate changes
3. Temporarily set threshold to 0.0 in BffController for testing

### Issue: Firebase token validation fails

**Solution:**
1. Verify TenantFirebaseConfig.ProjectId matches Firebase project
2. Verify CredentialJson is correctly encrypted
3. Check logs for specific Firebase error

## Next Steps

- [Tenant Management Guide](./tenant-management.md) - Add more tenants
- [Adding Endpoints Guide](./adding-endpoints.md) - Extend the template
- [Deployment Guide](./deployment-azure.md) - Deploy to Azure

## Additional Resources

- Architecture Overview: [docs/architecture/README.md](../architecture/README.md)
- API Documentation: Swagger UI at `/swagger`
- Troubleshooting: [docs/guides/troubleshooting.md](./troubleshooting.md)

## Step 9 — Test FetchXML Endpoints

### 9.1 Test Automatic Pagination (All Records)
POST https://localhost:7xxx/api/consultafetch/ejecutar
Headers:
Content-Type: application/json
X-Tenant-ID: tenant-prueba
Authorization: Bearer {your-firebase-id-token}
Body:
{
"entityName": "contacts",
"fetchXml": "<fetch count='100'><entity name='contact'><attribute name='fullname'/><attribute name='emailaddress1'/></entity></fetch>"
}
**Response:** Array with ALL matching contacts (automatically paginated).

**Use case:** Reports, exports, batch operations.

### 9.2 Test Manual Pagination (Page by Page)

**First page:**
POST https://localhost:7xxx/api/consultafetch/ejecutar-paginado
Headers:
Content-Type: application/json
X-Tenant-ID: tenant-prueba
Authorization: Bearer {your-firebase-id-token}
Body:
{
"entityName": "contacts",
"fetchXml": "<fetch count='50'><entity name='contact'><attribute name='fullname'/></entity></fetch>",
"pageNumber": 1,
"pagingCookie": null
}

**Response:**
```json
{
  "data": [...],
  "moreRecords": true,
  "pagingCookie": "%3ccookie%20page%3d%222%22%3e...",
  "currentPage": 1,
  "recordCount": 50
}
```

**Next page (if moreRecords = true):**
POST https://localhost:7xxx/api/consultafetch/ejecutar-paginado
Body:
{
"entityName": "contacts",
"fetchXml": "<fetch count='50'>...</fetch>",
"pageNumber": 2,
"pagingCookie": "%3ccookie%20page%3d%222%22%3e..."  ← from previous response
}

**Use case:** Interactive tables, lookups with many options, large datasets.

### 9.3 Test Secure Search (Recommended)
POST https://localhost:7xxx/api/search
Headers:
Content-Type: application/json
X-Tenant-ID: tenant-prueba
Authorization: Bearer {your-firebase-id-token}
Body:
{
"entity": "contact",
"select": ["fullname", "emailaddress1", "telephone1"],
"filters": [
{
"field": "address1_city",
"op": "Eq",
"value": "Buenos Aires"
}
],
"sort": [
{
"field": "fullname",
"dir": "asc"
}
],
"page": 1,
"pageSize": 50
}

**Response:**
```json
{
  "data": [...],
  "moreRecords": true,
  "pagingCookie": "...",
  "currentPage": 1,
  "totalInPage": 50
}
```

**Security:** Server automatically injects `ownerid` filter matching user's contactid.
User CANNOT bypass this filter.

**Use case:** All new development (recommended over raw FetchXML).

### 9.4 Test Search with Joins (Link-Entities)
POST https://localhost:7xxx/api/search
Body:
{
"entity": "contact",
"select": ["fullname", "emailaddress1"],
"linkEntities": [
{
"entityName": "account",
"fromAttribute": "parentcustomerid",
"toAttribute": "accountid",
"alias": "parentaccount",
"select": ["name", "telephone1"],
"type": "Inner"
}
],
"page": 1,
"pageSize": 50
}

**Result:** Contacts with joined account data.

## FetchXML vs Search Comparison

| Feature | `/consultafetch/ejecutar` | `/search` |
|---------|---------------------------|-----------|
| **Security** | ⚠️ Client controls FetchXML (IDOR risk) | ✅ Server injects user filters (RLS) |
| **Flexibility** | ✅ Full FetchXML power | ⚠️ Limited to whitelisted fields |
| **Pagination** | ✅ Automatic | ✅ Manual via paging cookie |
| **Use case** | Legacy portals, admin tools | New development, user-facing features |
| **Recommendation** | Migrate to `/search` | ✅ Use for all new endpoints |

**IMPORTANT:** For new development, use `/api/search` instead of raw FetchXML.
The search endpoint prevents IDOR attacks by enforcing row-level security.
