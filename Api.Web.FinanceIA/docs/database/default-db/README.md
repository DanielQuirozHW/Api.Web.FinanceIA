# Default-DB Scripts — Api.Web.FinanceIA

## What lives here
DDL for the **shared "Default" database** — the one `AppDbContext` binds
(`ConnectionStrings:Default`). It holds authentication and tenant-configuration
tables only: `Credenciales`, `TenantFirebaseConfig`, `ErrorLogs`.

**`002` is the only script in this folder, and it is pending.** Run it before the
matching application deploy — see Execution History below for why that ordering
is not optional.

`001_mfa_backup_codes.sql` **has been withdrawn and deleted.** It created
`[dbo].[MfaBackupCodes]` here; that table is FinancIA business data and now
belongs to the per-tenant business database, created by
`docs/database/054_mfa_backup_codes.sql`. It was never executed anywhere, so
nothing has to be undone — but do not run a stray copy of it. See
`docs/db-changes/default-db-001_mfa_backup_codes.md`.

**This is not the business database.** The numbered `NNN_*.sql` scripts one
level up (`docs/database/001…054`) target a **tenant business database**
(`ConnectionStrings:FinanciaDefault`) and are executed **once per tenant DB**.
The scripts in this folder are executed **once, on the single shared DB**.

Running a script from the wrong folder against the wrong database is the
failure mode this split exists to prevent. Check which folder a script is in
before executing it.

> **El gate de deploy ya NO verifica esta base.** Es compartida por todos los
> portales de la empresa y los releases de este repo no la cambian, así que
> `.github/workflows/deploy.yml` corre `--verify-schema` con
> `FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT=true` y sólo comprueba la base Financia
> de cada cliente. Consecuencia: **cualquier script futuro de esta carpeta se
> corre a mano Y se comprueba a mano** — ningún deploy lo va a detectar si
> falta. Para comprobarlo, `--verify-schema` con
> `FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION` apuntando a esta base (ver
> `docs/guides/schema-verification.md`, "Correrlo a mano").
>
> Ojo con `002`: el código ya mapea `TenantFirebaseConfig.WebApiKey`
> (`Data/Configurations/TenantFirebaseConfigConfiguration.cs`), y el historial
> de abajo lo da como NO ejecutado. Si la columna falta, cae toda la API en la
> resolución de tenant. Comprobalo a mano una vez y registrá la ejecución abajo.

## Why the folder exists
`docs/database/` grew two unnumbered Default-DB scripts
(`ErrorLog_AlterTraceId.sql`, `TenantFirebaseConfig.sql`) whose only signal of
being different was the absence of a number — easy to miss beside 53 numbered
per-tenant scripts. New Default-DB work goes here instead, with its own
sequence starting at `001`.

The two legacy scripts stay where they are: they have already been executed,
and moving an executed script is a bigger risk than the inconsistency.

## Naming
`NNN_snake_case_description.sql` — its own sequence, independent of the
per-tenant one. This folder's `002` and the parent folder's `002` are
unrelated scripts against different databases. Numbers are never reused, not
even a withdrawn one: the next script here is `003`.

## Conventions
Same as the parent folder:
- Idempotent — every block guards with
  `IF NOT EXISTS (SELECT 1 FROM sys.objects / sys.columns / sys.indexes / …)`
- Every guarded block ends with a `PRINT` saying what it did or that it
  skipped, and is followed by `GO`
- `NVARCHAR` never `VARCHAR`; `DATETIME2` never `DATETIME`
- No transaction and no `BEGIN TRY` wrapper: each block is independently
  idempotent, so a partial run is re-runnable rather than needing rollback
- Sensitive columns marked as encrypted/hashed in their column descriptions

## Execution Order
| # | Script | Purpose |
|---|--------|---------|
| 2 | `002_tenantfirebaseconfig_add_webapikey.sql` | Adds `TenantFirebaseConfig.WebApiKey` |

`001` is absent on purpose: withdrawn, never executed, superseded by the
per-tenant `054_mfa_backup_codes.sql`. Its number is not reused.

## Execution History
| Script | Environment | Date | Executed by |
|--------|-------------|------|-------------|
| `002_tenantfirebaseconfig_add_webapikey.sql` | | | |

> `002_tenantfirebaseconfig_add_webapikey.sql` has **NOT** been executed yet.
> It is the **higher-risk half** of the MFA backup-code delivery, and the
> difference matters: the per-tenant `054` only creates a new table, so getting
> that order wrong breaks the new feature alone, whereas 002 adds a column to
> `TenantFirebaseConfig` — a table read during **tenant resolution on every
> request**. Deploying the code before this script runs makes every read of that
> table fail with
> `Invalid column name 'WebApiKey'`, which takes down the whole API, not just
> the new endpoint. Run it first, then restart the App Service.
>
> After running it, populate `WebApiKey` per tenant with the same Firebase Web
> API key the Portal already uses (Firebase console → Project settings →
> General → Web API Key). Until a tenant has one, `POST
> /api/mfa/backup-codes/redeem` answers 503 for that tenant and nothing else is
> affected. Rationale:
> `docs/db-changes/default-db-002_tenantfirebaseconfig_add_webapikey.md`.

> `001_mfa_backup_codes.sql` was **withdrawn before it was ever executed** and
> the file has been deleted from this folder. It created `[dbo].[MfaBackupCodes]`
> in this shared database; the table is FinancIA business data belonging to one
> `[dbo].[Users]` row, so it now lives in the per-tenant business database and is
> created by `docs/database/054_mfa_backup_codes.sql` — where it can carry a real
> `FOREIGN KEY` to `Users(UserId)`. Nothing was deployed and no code was ever
> issued, so there is no data to migrate and nothing to roll back here. Rationale:
> `docs/db-changes/default-db-001_mfa_backup_codes.md`.

## Before Executing on Production
1. Execute on development first
2. Review with the DBA
3. Take a backup
4. Never execute directly on production without DBA approval
5. Record the run in the table above
