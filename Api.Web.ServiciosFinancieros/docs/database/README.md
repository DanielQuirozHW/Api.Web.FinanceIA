# Database Scripts — Api.Web.ServiciosFinancieros

## Approach
Database First exclusively. EF Core maps existing tables only.
Never run dotnet ef migrations add or dotnet ef database update.
All schema changes are managed via DDL scripts in this folder.

## Execution Order
Run scripts in this exact order on a fresh environment:

1. ErrorLog_AlterTraceId.sql
   → Adds TraceId column to existing ErrorLogs table
   → Safe to run multiple times (checks column existence first)

2. TenantFirebaseConfig.sql
   → Creates TenantFirebaseConfig table
   → Safe to run multiple times (checks table existence first)

## Existing Tables (no script needed)
These tables already exist and are managed externally:

Credenciales
  → Master tenant registry
  → Contains Dynamics 365 OAuth credentials per tenant
  → Fields: id, conexion (obsolete), url, clientid,
            clientsecret, tenantid, cliente

ErrorLogs
  → API error log for operational review
  → Fields: ErrorId, Timestamp, Level, Message,
            ExceptionDetails, Source, Url, UserId,
            IPAddress, StackTrace, TraceId (added by script 1)

## Script Conventions
- Every script is wrapped in BEGIN TRY / BEGIN CATCH
- Every script checks existence before creating/altering
- Every script is safe to run multiple times (idempotent)
- Every script uses GETUTCDATE() never GETDATE()
- Every script uses NVARCHAR never VARCHAR
- Every script uses DATETIME2 never DATETIME
- Sensitive columns (credentials, keys) always marked
  as AES-256 encrypted in column descriptions

## Naming Convention for New Scripts
{TableName}_{Action}_{YYYYMMDD}.sql

Examples:
  TenantFirebaseConfig_Create_20250101.sql
  ErrorLogs_AlterTraceId_20250101.sql
  Credenciales_AddIndex_20250101.sql

## Before Executing on Production
1. Always execute on development environment first
2. Review script with DBA before production execution
3. Take database backup before executing on production
4. Never execute directly on production without DBA approval
5. Document execution in this README under Execution History

## Execution History
| Script                        | Environment | Date       | Executed by |
|-------------------------------|-------------|------------|-------------|
| ErrorLog_AlterTraceId.sql     | Development |            |             |
| TenantFirebaseConfig.sql      | Development |            |             |