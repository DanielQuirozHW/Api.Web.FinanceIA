# default-db/001_mfa_backup_codes.sql — WITHDRAWN

**Status:** withdrawn. The script has been **deleted** from
`Api.Web.FinanceIA/docs/database/default-db/`. It was never executed, in any
environment. **Do not run it if you have a copy** — it creates
`[dbo].[MfaBackupCodes]` in the wrong database.

**Superseded by:** `docs/database/054_mfa_backup_codes.sql` — the per-tenant
script, executed **once per tenant business database** like every other numbered
script in that folder. Rationale: `docs/db-changes/054_mfa_backup_codes.md`.

## Why

001 created the table in the shared **"Default"** database (`AppDbContext`), on
the reasoning that a backup code is global identity data keyed by the Firebase
uid. The decision was reversed before either script ran: a backup code is
**FinancIA business data** belonging to exactly one `[dbo].[Users]` row, so it
belongs in the business database next to that row — where it can finally carry a
real `FOREIGN KEY` to `Users(UserId)`, which is the constraint the uid-keyed
design existed to work around.

Nothing was deployed, so **there is no data to migrate**: the table exists in no
environment and no backup code has ever been issued.

## What did NOT move

`default-db/002_tenantfirebaseconfig_add_webapikey.sql` stays exactly where it
is and is unaffected. `WebApiKey` hangs off `TenantFirebaseConfig`, which is
genuinely tenant **configuration** and correctly lives in the "Default"
database. After this change, `002` is the only pending script in `default-db/`.
