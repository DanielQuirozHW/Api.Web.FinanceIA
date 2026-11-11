# 076 — Users: CK_Users_Role admits SuperAdmin

## What changed and why

`Roles.SuperAdmin` is the read-only support role: it reads and exports everything the Auditor does,
and keeps the two operator writes, `PUT /api/billing/config` and
`PUT /api/bff/settings/tenant-profile`. The role claim in the internal JWT is copied from
`[Users].[Role]` at token exchange, so the role only works if a user row can hold it. It could not:
`001_initial_schema` created `CK_Users_Role` with the four client roles, and no later script widened
it. Inserting or updating a user to `'SuperAdmin'` failed with a CHECK violation.

| Change | Where |
|---|---|
| `'SuperAdmin'` becomes a legal role | `[dbo].[Users]` CHECK `[CK_Users_Role]`: Analyst, RiskManager, Admin, Auditor, **SuperAdmin** |

## What does not change

- `[dbo].[UserInvitations]` CHECK `[CK_UserInvitations_Role]` keeps the four client roles. A
  SuperAdmin is granted by the DBA, never invited from the portal, and
  `SettingsController.ValidRoles` excludes it too. Keeping the invitation constraint narrow means the
  database refuses a SuperAdmin invitation even if that list is ever widened by mistake.
- No column, type or index changes. Existing rows are untouched.
- EF maps no check constraint for `[Users]`. The model does not change, so the code neither needs
  nor detects this script.

## Deploy order

1. Run `076` in **every** Financia database. Check the definition with the query at the end of the
   script.
2. Only then create or promote a SuperAdmin user row.

The API release that makes SuperAdmin read-only can be published before or after step 1. Nothing EF
selects or inserts changes. `--verify-schema` cannot detect the miss, because it checks table and
column names, not constraints.

No App Service restart is needed.

## Rollback

Only possible while no row has `Role = 'SuperAdmin'`. `WITH CHECK` validates existing rows, so it
fails by design otherwise:

```sql
BEGIN TRANSACTION;
ALTER TABLE [dbo].[Users] DROP CONSTRAINT [CK_Users_Role];
ALTER TABLE [dbo].[Users] WITH CHECK ADD CONSTRAINT [CK_Users_Role]
    CHECK ([Role] IN (N'Analyst', N'RiskManager', N'Admin', N'Auditor'));
COMMIT TRANSACTION;
```
