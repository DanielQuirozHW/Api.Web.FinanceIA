# 062 — Who a pre-approval is supposed to reach

## What changed and why

The two-step decision flow (058–061) can escalate an analysis, but it cannot tell anyone. When an
analyst pre-approves, the analysis sits in `PreApproved` until somebody happens to look at the
screen. This script adds the structure that gives an escalation an **addressee**: a team, and the
manager who runs it.

`[dbo].[Teams]`, one row per team:

| Column | Meaning |
|---|---|
| `TeamId` | primary key, clustered |
| `TenantId` | tenant slug (carried and filtered on in every query) |
| `Name` | display name, shown in the Admin screen |
| `ManagerUserId` | the person a pre-approval from this team goes to; **nullable** |
| `CreatedAt` / `CreatedBy` / `UpdatedAt` / `UpdatedBy` | audit trail |

### A nullable manager is a real state, not a loose constraint

`ManagerUserId` is nullable because a team can exist before anyone has been named to run it, and an
Admin should be able to create the team first. The application reads `NULL` as "no manager to
notify" and falls back to notifying **every active RiskManager of the tenant**.

That fallback is the whole reason the column may be null without the feature breaking. The failure
mode worth designing against is a pre-approval that reaches *nobody*; notifying too many people is a
nuisance, notifying no one loses the work. So every gap in the data — no team, no manager, or a
manager whose account has been deactivated — resolves to the fan-out rather than to silence.

### Constraints: one FK, no CHECKs

`FK_Teams_ManagerUser` **is** declared, following 027, 049, 054 and 059. 049 states the test: a
foreign key earns its place when the column is a live *subject* rather than a record of a past
action. The manager is exactly that — a team pointing at a user row that does not exist can never
notify anyone. No `CASCADE`, per 027, 049 and 059: accounts are deactivated via `IsActive`, never
deleted, and a cascade would silently destroy a team because its manager left.

`CreatedBy` / `UpdatedBy` carry **no** foreign key — the other half of the same rule, as 030, 040,
041, 046 and 059 argue: they record who performed an action and may outlive the account. Both are
nullable, because the DBA may seed a team with no application user behind it.

No `CHECK` on `Name` beyond `NOT NULL`. The API trims it and rejects blanks, following 036, 046, 052
and 059 — a value rule in the schema turns every change into a migration on every tenant database.

### Why the index is `(TenantId, Name)`

The only read this table serves is "the teams of this tenant", and the Admin screen shows them in
name order. `Name` is the second key column so the `ORDER BY` is satisfied by the index itself rather
than a sort. There is no index on `ManagerUserId`: nothing looks a team up by its manager.

## DDL

Script location: `docs/database/062_teams_table.sql`

| Object | Type |
|---|---|
| `[dbo].[Teams]` | table, 8 columns |
| `PK_Teams` | primary key, clustered, `(TeamId)` |
| `DF_Teams_TeamId` | default constraint, `DEFAULT (NEWSEQUENTIALID())` |
| `DF_Teams_CreatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `DF_Teams_UpdatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `FK_Teams_ManagerUser` | foreign key → `[dbo].[Users]([UserId])`, no cascade |
| `IX_Teams_TenantId_Name` | nonclustered index |

Three guarded blocks on `sys.objects` / `sys.foreign_keys` / `sys.indexes`, `PRINT` in both branches,
`GO` per batch, no transaction — the conventions in `docs/database/README.md`.

Note the `NEWSEQUENTIALID()` default is for rows written **by hand**. The application supplies the
Guid itself, and `TeamConfiguration` maps `TeamId` as `ValueGeneratedNever()` for exactly the reason
`NotificationConfiguration` spells out: telling EF the database generates the key makes it omit the
explicit Guid and the INSERT breaks silently.

## How the application uses it

`Team` is a mapped entity on `FinanciaDbContext`, reached through `ITeamRepository`, registered
scoped. Every query is scoped by `TenantId` by hand — there is no global query filter on this
context.

- **Read, on the notification path**: `GetByIdAsync(teamId, tenantId, ct)` — one point lookup per
  pre-approval or direct approval, served by the clustered PK, to find `ManagerUserId`.
- **Read, on the Admin screen**: `GetByTenantAsync(tenantId, ct)`, served by
  `IX_Teams_TenantId_Name`. Member counts and the manager's display name are derived from the single
  active-user roster the controller already loads, grouped in memory — no per-team query.
- **Write, from the Admin screen**: `GET` / `POST` / `PUT` / `DELETE` on `/api/bff/teams`, all
  `[Authorize(Roles = Roles.Admin)]`. `DELETE` unassigns every member first (sets `Users.TeamId` back
  to `NULL`) and then removes the row, so the delete never trips `FK_Users_Team`.
- **Assignment**: `PUT /api/bff/settings/users/{userId}/team`, also Admin-only, folded into the
  existing per-user admin surface beside `approval-limits` rather than given a new route.

Without the write path this table could only be populated by the DBA, which would ship the feature
with every tenant permanently on the RiskManager fan-out. That is why the Admin endpoints are part of
this change rather than a follow-up.

## Shipping alongside

- **063** — `[dbo].[Users].[TeamId]`, the membership column that points back here.

The two are one change split across two tables. **062 must run first**: 063 declares a foreign key
against `[dbo].[Teams].[TeamId]`.

## Pre-conditions

- Apply **062 then 063**, in that order, on **every tenant database**, **before** publishing the API.
- `Team` is a mapped entity, so EF Core maps `[dbo].[Teams]` unconditionally. Against a database
  without the table, every `/api/teams` request fails with `Invalid object name`.
- **Restart the App Service** after deploying so EF's model cache is rebuilt.
- Creating the table changes **no** existing behaviour on its own: until the new code is live nothing
  reads it, and once live, an empty table simply means every analyst falls back to the RiskManager
  fan-out. Populating it is a business decision per tenant — use the Admin screen. No seed is
  provided, deliberately: a guessed team structure is worse than none.
- No Portal deploy is required by this script (no enum value is added and no existing DTO changes).

**Deploy order: DDL 062 + 063 on every tenant DB → publish this API → restart the App Service.**

## Rollback

Roll the **application** back first, and drop **063 before this script** — `FK_Users_Team` references
this table and blocks the `DROP` while it exists.

```sql
ALTER TABLE [dbo].[Users] DROP CONSTRAINT [FK_Users_Team];
ALTER TABLE [dbo].[Users] DROP COLUMN [TeamId];
GO
DROP TABLE [dbo].[Teams];
GO
```

Export first if the structure matters:

```sql
SELECT [TeamId], [TenantId], [Name], [ManagerUserId] FROM [dbo].[Teams];
SELECT [UserId], [TenantId], [TeamId] FROM [dbo].[Users] WHERE [TeamId] IS NOT NULL;
```

What is lost is the team structure and every membership. **No analysis, user or notification is
affected** — they live on their own tables and are untouched — but after a rollback every
pre-approval notification falls back to the RiskManager fan-out until the teams are rebuilt. That is
a degraded-but-safe state, not a broken one, which is the point of choosing that fallback.

## Verification

The script carries five commented checks: the eight columns and their types, that the primary key is
clustered on `TeamId`, the foreign key and index, that the table starts **empty** (which is correct),
and a cross-tenant integrity query — a team pointed at a manager from another tenant — that must
return nothing.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
