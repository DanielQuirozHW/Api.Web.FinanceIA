# Api.Web.ServiciosFinancieros

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet) ![ASP.NET Core](https://img.shields.io/badge/ASP.NET_Core-8.0-512BD4?logo=dotnet) ![Azure](https://img.shields.io/badge/Azure-App_Service-0078D4?logo=microsoftazure) ![SQL Server](https://img.shields.io/badge/Azure_SQL-Database_First-CC2927?logo=microsoftsqlserver)

## Overview

Multi-tenant Backend-for-Frontend (BFF) API template for financial services applications.
Built with ASP.NET Core 8, designed for Azure deployment with comprehensive security,
multi-tenant isolation, and enterprise integrations.

## Key Features

- Multi-tenant architecture with per-request tenant isolation
- Firebase Authentication integration (multi-project support)
- Microsoft Dynamics 365 integration via MSAL
- Internal JWT session management
- reCAPTCHA V3 bot protection
- AES-256-CBC encryption for credentials at rest
- Database First EF Core with SQL Server
- RFC 7807 ProblemDetails error responses
- Structured logging with correlation IDs
- Azure Application Insights ready

## Tech Stack

| Layer | Technology |
|---|---|
| **Framework** | ASP.NET Core 8 (.NET 8.0) |
| **Database** | Azure SQL Server (Database First, no migrations) |
| **Authentication** | Firebase Admin SDK + Internal JWT (HMAC-SHA256) |
| **External APIs** | Dynamics 365 OData v9.2, Google reCAPTCHA V3 |
| **ORM** | Entity Framework Core 8 |
| **DI Container** | Built-in ASP.NET Core DI |
| **Logging** | Microsoft.Extensions.Logging + Application Insights |

## Project Structure

```
Api.Web.ServiciosFinancieros/
├── Clases/
│   ├── Exceptions/              # Typed exception hierarchy
│   ├── EncryptionService.cs     # AES-256 encryption
│   ├── TenantResolver.cs        # Tenant configuration resolution
│   ├── TenantResolutionMiddleware.cs
│   ├── FirebaseTokenValidator.cs
│   ├── FirebaseAuthenticationHandler.cs
│   ├── TokenService.cs          # Internal JWT issuance
│   ├── RecaptchaVerifier.cs
│   └── GlobalExceptionHandler.cs
├── Controllers/
│   └── BffController.cs         # Auth token endpoints
├── Data/
│   ├── AppDbContext.cs
│   ├── Configurations/          # EF Fluent API configs
│   └── Repositories/
├── Models/                      # Entities and DTOs
├── Servicios/
│   ├── DynamicsTokenService.cs  # MSAL OAuth for Dynamics
│   └── DynamicsClient.cs        # Dynamics 365 HTTP client
├── docs/
│   ├── architecture/
│   ├── database/                # DDL scripts
│   └── guides/
├── EncryptionHelper/            # CLI tool for encrypting secrets
└── .claude/                     # AI coding assistant config
```

## Quick Start

### Prerequisites

- .NET 8 SDK
- SQL Server (local or Azure)
- Visual Studio 2022 or VS Code
- Firebase project with Service Account
- Google reCAPTCHA V3 site

### Setup

See [docs/guides/setup.md](Api.Web.ServiciosFinancieros/docs/guides/setup.md) for detailed instructions.

```bash
# Clone repository
git clone [repository-url]
cd Api.Web.ServiciosFinancieros

# Configure secrets
cd Api.Web.ServiciosFinancieros
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=ServiciosFinancieros;..."
dotnet user-secrets set "Jwt:SigningKey" "your-256-bit-secret-key"
dotnet user-secrets set "ENCRYPTION_MASTER_KEY" "your-encryption-master-key"

# Run database scripts
# Execute scripts in docs/database/ against your SQL Server

# Restore and build
dotnet restore
dotnet build

# Run
dotnet run --launch-profile https
```

Open `https://localhost:7xxx/swagger`

### Adding Your First Tenant

See [docs/guides/tenant-management.md](Api.Web.ServiciosFinancieros/docs/guides/tenant-management.md)

## API Endpoints

### Authentication

| Method | Endpoint | Description | Auth |
|--------|----------|-------------|------|
| `POST` | `/api/bff/auth/token` | Exchange Firebase token for Internal JWT | Firebase Bearer |
| `POST` | `/api/bff/auth/onboarding-token` | Issue anonymous onboarding JWT | reCAPTCHA V3 |

### Health *(TODO)*

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health` | Health check endpoint |
| `GET` | `/health/ready` | Readiness probe |

## Architecture Decisions

See [docs/architecture/README.md](Api.Web.ServiciosFinancieros/docs/architecture/README.md) for complete architecture overview.

### Multi-Tenant Flow

```
Request
  │
  ├─ X-Tenant-ID header
  │
  ▼
TenantResolutionMiddleware
  ├─ Validate header format
  ├─ Load from cache (IMemoryCache)
  │   └─ Cache miss: query SQL Server → decrypt secrets → cache
  └─ Store TenantContext in HttpContext.Items
  │
  ▼
Authentication Middleware
  ├─ Firebase token → validate against tenant FirebaseApp instance
  └─ Internal JWT  → validate tenantId claim matches TenantContext
  │
  ▼
Controller / Service
```

## Deployment

See [docs/guides/deployment-azure.md](Api.Web.ServiciosFinancieros/docs/guides/deployment-azure.md) for Azure deployment instructions.

### CI/CD

There is no automatic deployment, and there is no Staging environment. Production is published
by hand, and the reason is the Database-First rule: the numbered DDL scripts in
`Api.Web.FinanceIA/docs/database/` are run by the DBA **before** the matching deploy, because
EF Core maps new columns unconditionally and publishing first breaks production with
`Invalid column name`.

| Branch | Trigger | Action |
|--------|---------|--------|
| `DEVELOP` | Pull Request | Build + test (`ci.yml`), plus the `no-ef-migrations` gate |
| `DEVELOP` | Push | Build, test and upload the artifact — **no deploy** |
| `DEVELOP` | Run workflow (manual) | Deploy to Production, gated on `ddl_aplicada` + schema verification |

The manual run is the only path to production: `deploy.yml`'s deploy job requires
`workflow_dispatch`, the `ddl_aplicada` confirmation and `refs/heads/DEVELOP`. Before publishing
it verifies against the database that every table and column this build maps already exists —
see [docs/guides/schema-verification.md](Api.Web.FinanceIA/docs/guides/schema-verification.md).

## Security

- Credentials encrypted at rest with AES-256-CBC
- Firebase multi-tenant isolation (named `FirebaseApp` instances per tenant)
- JWT signed with HMAC-SHA256; mandatory `tenantId` claim
- Per-request tenant context validation before authentication
- reCAPTCHA V3 for all anonymous endpoints (min score 0.5)
- CORS configured with explicit origins (never `AllowAnyOrigin` in production)
- No sensitive data in logs or error responses
- RFC 7807 `ProblemDetails` for all error responses

## Contributing

This is a template repository. Fork and customize for your organization's needs.

## License

[Your License Here]

## Support

For issues specific to this template, see [MISTAKES.md](Api.Web.ServiciosFinancieros/MISTAKES.md) for known issues and solutions.
