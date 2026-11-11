# 054 — MFA backup codes move to the business database, beside Users

**Target database:** every **tenant business** database (`FinanciaDefault`,
`FinanciaDbContext`) — run once per tenant DB, like every other numbered script
in `docs/database/`.
**Script:** `Api.Web.FinanceIA/docs/database/054_mfa_backup_codes.sql`
**Supersedes:** `docs/database/default-db/001_mfa_backup_codes.sql`, now deleted
and never executed.
**Deploy blocker:** yes, for the new feature only.

## What it does

Creates `[dbo].[MfaBackupCodes]`, one foreign key and two indexes. Adds nothing
to, and alters nothing in, any existing table.

```
BackupCodeId  INT              IDENTITY, PK
TenantId      NVARCHAR(100)    NOT NULL   -- Firebase tenant slug (script 005), from the Users row
UserId        UNIQUEIDENTIFIER NOT NULL   -- FK_MfaBackupCodes_Users -> [dbo].[Users].[UserId]
CodeHash      NVARCHAR(255)    NOT NULL
CreatedAt     DATETIME2(7)     NOT NULL DEFAULT GETUTCDATE()
UsedAt        DATETIME2(7)     NULL
UsedIp        NVARCHAR(45)     NULL
```

- `FK_MfaBackupCodes_Users` — the constraint the whole move exists for.
- `IX_MfaBackupCodes_TenantId_UserId` — non-unique; a user holds a whole set.
- `IX_MfaBackupCodes_TenantId_UserId_Unused` — same key, `WHERE [UsedAt] IS NULL`.
  Validation and the remaining-count read only ever touch unspent rows, and spent
  rows are never deleted, so the filter is what stops this index growing with the
  audit trail.

## Why the business database and not "Default"

PRs #185 and #186 put this table in the shared "Default" database, on the
reasoning that a backup code is an attribute of global user identity. That was
reversed before anything was deployed: **a backup code is FinancIA business data
belonging to one FinancIA user.** Putting it beside `[dbo].[Users]` makes the
foreign key possible, and the foreign key is what the uid-keyed design was
working around — 001 explicitly documented "no FK to Users: that table lives in
a different database entirely".

Nothing had been deployed when the decision changed, so this is a **design
migration, not a data migration**: no environment has the table and no backup
code has ever been issued. There is nothing to move.

## Why TenantId comes from the Users row, not from X-Tenant-ID

Every `FinanciaDbContext` query must be tenant-scoped, and there is no EF global
query filter to catch a miss, so the table carries `TenantId` and every query
filters on `(TenantId, UserId)`.

The value is written from the **resolved `Users` row**, never from the request's
`X-Tenant-ID` header. Reading it from the header would make the two disagree
whenever a user authenticates under a tenant other than the one their `Users`
row belongs to — a cross-tenant SuperAdmin, say — and the consequence would be
the worst one available on a recovery path: codes generated under one tenant and
unredeemable under another, on the exact endpoint a locked-out user reaches for.
Taking it from the user row makes that state unrepresentable.

## Why there is no FirebaseUid column

`[dbo].[Users]` already carries a unique one (`UQ_Users_FirebaseUid`), so a
Firebase uid resolves to a `UserId` in a single indexed lookup, which the API
does once per request and reuses. A second copy here could only drift out of
agreement with the first.

## Hashing

`CodeHash` stores a salted **PBKDF2-HMAC-SHA256** hash, 100 000 iterations,
16-byte per-row salt, 32-byte output, serialised as
`pbkdf2-sha256${iterations}${base64 salt}${base64 hash}` (~85 chars). Unchanged
from #185 — the move is about which database the rows live in, not about how
they are protected.

The plaintext exists exactly once — in the body of the generation response — and
is never stored, never logged and not recoverable. A user who loses the list
generates a new set.

The iteration count is stored **inside each hash**, so it can be raised later
without invalidating existing rows: verification reads each row's own count.
100 000 rather than OWASP's 210 000 is deliberate — verification runs once per
*candidate* row (10 by default), so the per-request cost is ten derivations, and
the tight rate limits on the validate and redeem endpoints carry the rest.

## Deploy ordering

Run this script on **every tenant business database** *before* publishing the
matching application build, then **restart the App Service**.

EF Core maps `MfaBackupCodes` unconditionally through the `DbSet` on
`FinanciaDbContext`, so code-first-then-DDL means every `/api/mfa/backup-codes`
request fails with `Invalid object name 'dbo.MfaBackupCodes'`. The blast radius
is strictly the new feature: no existing table is touched, no existing column
changes type or nullability, and nothing that works today starts failing.

`default-db/002_tenantfirebaseconfig_add_webapikey.sql` is a separate, still
pending script on the **shared** database, needed by the redeem endpoint alone.
Neither script depends on the other; both must run before the deploy. No Portal
deploy is needed — the Portal does not call these endpoints yet.

## Rollback

```sql
ALTER TABLE [dbo].[MfaBackupCodes] DROP CONSTRAINT [FK_MfaBackupCodes_Users];
DROP TABLE [dbo].[MfaBackupCodes];
```

Dropping the table destroys every unspent code, so any user who had generated a
set must generate a new one after it is recreated. There is no data to preserve:
the rows are hashes of secrets that cannot be re-derived. The two indexes go with
the table.

## Idempotency

Table, foreign key, both indexes and both extended properties are guarded
separately, so a run that failed halfway re-runs cleanly. Re-running never drops
a row and never rewrites a hash already stored.
