# 063 — Which team a user belongs to

## What changed and why

062 created the teams. This script adds the column that puts people in them, and it is the half the
notification routing actually reads: to find the manager for a pre-approval, the emitter starts from
the acting user's `TeamId`.

`[dbo].[Users]`, one new column:

| Column | Meaning |
|---|---|
| `TeamId` | the team this user belongs to; **nullable**, FK → `[dbo].[Teams].[TeamId]` |

### NULL is the default, and it is a designed state

Every existing user gets `NULL` the moment this runs, and nothing is backfilled. `NULL` means "not
assigned to a team", and the emitter reads it as "no manager to notify" and falls back to notifying
**every active RiskManager of the tenant**.

This is what makes the change safe to deploy into a live tenant that has no teams yet: the fallback
is the pre-existing behaviour in everything but name, so a tenant that never creates a single team
keeps working exactly as it does today. It is also why the column is not `NOT NULL` — there is no
sensible default team to point people at, and inventing one would be worse than the fallback.

The same fallback covers the other two gaps, which is deliberate: a user in a team with no manager,
and a user in a team whose manager has been deactivated. All three resolve to the fan-out rather than
to silence, because a pre-approval that reaches nobody is the failure worth designing against.

### Constraints

`FK_Users_Team` **is** declared, following 027, 049, 054 and 059 — the 049 test again: the team is a
live *subject*, not a record of a past action. A user pointing at a team row that does not exist
would resolve to no manager and silently fall back forever.

No `CASCADE`, per 027, 049 and 059: deleting a team must not delete its members. The application
unassigns every member first — setting this column back to `NULL` — and only then removes the team
row, so the delete never trips the constraint. Without `NO CASCADE`, a mis-typed `DELETE` against
`[dbo].[Teams]` would take the analysts with it.

### No index, on purpose

Nothing queries users **by** team. The Admin list reads the whole active roster for the tenant in one
go and groups it in memory; the notification path travels the other direction (user → team → manager)
and lands on the clustered PK of `[dbo].[Teams]`. An index here would be written on every user update
and read by nothing.

## DDL

Script location: `docs/database/063_users_add_teamid.sql`

| Object | Type |
|---|---|
| `[dbo].[Users].[TeamId]` | column, `UNIQUEIDENTIFIER NULL` |
| `FK_Users_Team` | foreign key → `[dbo].[Teams]([TeamId])`, no cascade |

Two guarded blocks on `sys.columns` / `sys.foreign_keys`, `PRINT` in both branches, `GO` per batch,
no transaction. The `GO` between them is load-bearing rather than cosmetic: the column has to be
committed before a constraint in the same script run can reference it.

## How the application uses it

`User.TeamId` is a nullable `Guid?` mapped by `UserConfiguration`. It is read and written in three
places:

- **The notification path** — `AnalysisDecisionNotifier` reads `currentUser.TeamId` to resolve the
  manager for `ANALYSIS_PREAPPROVAL_PENDING` (case 1) and `ANALYSIS_APPROVED_FYI` (case 5), falling
  back to the RiskManager fan-out on any gap.
- **Assignment** — `PUT /api/bff/settings/users/{userId}/team`, `[Authorize(Roles = "Admin")]`, body
  `{ "teamId": <guid|null> }` where `null` unassigns. Folded into the existing per-user admin surface
  beside `approval-limits`, and reusing the same `EnsureUserInTenantAsync` guard, so an Admin cannot
  read or rewrite a user in another tenant by guessing a GUID.
- **Team deletion** — `DELETE /api/bff/teams/{id}` nulls this column for every member of the team
  before removing the team row.

The Admin team screen's member counts come from the active-user roster the controller already loads,
grouped by `TeamId` in memory — this column is never queried on its own.

## Shipping alongside

- **062** — `[dbo].[Teams]`, the table this column references.

The two are one change split across two tables. **062 must run first**: this script's foreign key
targets `[dbo].[Teams].[TeamId]` and cannot be applied before that table exists.

## Pre-conditions

- Apply **062 then 063**, in that order, on **every tenant database**, **before** publishing the API.
- ⚠ **This is the more dangerous of the two scripts.** `UserConfiguration` maps `[TeamId]`
  unconditionally, so publishing the code first makes every `SELECT` against `[dbo].[Users]` fail
  with `Invalid column name 'TeamId'` — and that is not just the new endpoints. Resolving the calling
  user reads that table, so it is **every authenticated request**. Run the DDL first.
- **Restart the App Service** after deploying so EF's model cache is rebuilt.
- Adding the column changes **no** existing behaviour on its own: it is nullable with no default, so
  every existing row simply gets `NULL` and no existing query is affected until the new code reads
  it.
- No Portal deploy is required by this script.

**Deploy order: DDL 062 + 063 on every tenant DB → publish this API → restart the App Service.**

## Rollback

Roll the **application** back first: `UserConfiguration` maps this column, so dropping it under a
running deployment breaks **every authenticated request**, not just the team endpoints. The
constraint must go before the column.

```sql
ALTER TABLE [dbo].[Users] DROP CONSTRAINT [FK_Users_Team];
GO
ALTER TABLE [dbo].[Users] DROP COLUMN [TeamId];
GO
```

Export first if the memberships matter:

```sql
SELECT [UserId], [Email], [TenantId], [TeamId]
FROM   [dbo].[Users] WHERE [TeamId] IS NOT NULL;
```

What is lost is every membership. No user account is otherwise affected and no analysis, notification
or team row is touched — but after a rollback every pre-approval notification falls back to the
RiskManager fan-out until the assignments are rebuilt.

If **062** is being rolled back too, run **this script's rollback first** — `FK_Users_Team` blocks
`DROP TABLE [dbo].[Teams]` while it exists.

## Verification

The script carries five commented checks: that the column exists as a nullable `uniqueidentifier`,
that the foreign key is enabled and trusted, that every existing user starts unassigned (which is
correct), a cross-tenant integrity query — a user assigned to another tenant's team — that must
return nothing, and a readable membership listing.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
