# 066 — Moving the existing managers into the new table

## What changed and why

064 created `[dbo].[TeamManagers]` and the application now reads it. Every team that already has a
manager has it in `[dbo].[Teams].[ManagerUserId]`, and nothing put it in the new table. This script
copies them across.

```sql
INSERT INTO [dbo].[TeamManagers] ([TeamId], [UserId], [TenantId], [CreatedAt], [CreatedBy])
SELECT t.[TeamId], t.[ManagerUserId], t.[TenantId], GETUTCDATE(), NULL
FROM   [dbo].[Teams] t
WHERE  t.[ManagerUserId] IS NOT NULL
  AND  NOT EXISTS (SELECT 1 FROM [dbo].[TeamManagers] m
                   WHERE m.[TeamId] = t.[TeamId] AND m.[UserId] = t.[ManagerUserId]);
```

### ⚠ This is a DATA script, not a schema script

It is the first of its kind in this folder: every other numbered script creates or alters an object,
and this one moves rows between two that already exist. It is numbered into the same sequence anyway,
because the DBA has to run it at a specific point in the same visit, and **a file outside the
sequence is a file that gets missed**.

It is also why it is not, and must not become, a startup migration in the application. Nothing in
this codebase writes schema or bulk-migrates data at boot. Database First means the DBA runs the
scripts and the code reads what is there.

### What happens if it is skipped

The application does **not** break. `AnalysisDecisionNotifier` reads the **union** of
`[dbo].[TeamManagers]` and the deprecated `Teams.ManagerUserId`, precisely so a tenant whose data
migration was missed still routes a pre-approval to its named manager instead of silently degrading
to the RiskManager fan-out.

That union is a safety net, not a licence to skip this script. It exists because the
`--verify-schema` deploy gate compares **columns, not rows**: a tenant missing this migration passes
every automated check. And the union is itself the thing a later cleanup wants to remove — which it
cannot, until every tenant has run this. **Skipping defers the work; it does not avoid it.**

### Idempotent per row, not per statement

The guard sits inside the `INSERT`'s `WHERE` clause rather than wrapping the whole statement. A
statement-level "if the table is empty" guard would be wrong twice over: it would refuse to run on a
tenant that had already created one team through the new UI, and it would silently do nothing on a
re-run that ought to pick up a team added between the two runs.

Per row, the script **converges**: run it once, five times, or again next month after new teams
appear — the result is the same and nothing is ever duplicated. (`PK_TeamManagers` would reject a
duplicate anyway; the `NOT EXISTS` is what makes the script *no-op* instead of *error*.)

The script also guards on `sys.objects` for the target table and `RAISERROR`s at severity 16 if 064
has not run, so a wrong-order run stops loudly rather than half-migrating.

### The values it writes

- **`CreatedBy` is NULL**, deliberately. The column records which application user named a manager,
  and nobody did — the DBA did, via this script. 062 made it nullable for exactly this case, and
  inventing a user id here would put a fiction in an audit trail. It also doubles as the marker that
  identifies these rows in the rollback below.
- **`TenantId` is copied from the team**, not looked up from the user. They agree today and 064's
  verification query 6 checks they keep agreeing, but the team is the right source: the row records
  "this team has this manager", so it belongs to the team's tenant.
- **Teams with a NULL `ManagerUserId` are skipped**, which is correct and not a gap. NULL means "no
  manager named yet" — a real state, per 062 — and the application already reads it as "fall back to
  the ladder". There is nothing to migrate.

### ⚠ `ManagerUserId` is NOT dropped, and that is intentional

The column is **retained but deprecated**:

- the application still **writes** it, because the existing Admin UI sets a single manager through
  `PUT /api/teams/{id}` and that endpoint is not replaced in this release — `TeamsController` keeps
  the column and the new table in step;
- the application still **reads** it, unioned as described above;
- and it is the **rollback path** — with the column intact, dropping `[dbo].[TeamManagers]` returns
  the system to pre-064 behaviour rather than to teams with no manager at all.

A later cleanup script drops it, once Phase 2's management API has replaced the single-manager setter
and the union read is gone. Do not tidy it away here.

## DDL

Script location: `docs/database/066_migrate_manager_to_team_managers.sql`

| Object | Type |
|---|---|
| — | none. This script creates and alters nothing. |

One guarded block on `sys.objects` (the target table's existence), then the guarded `INSERT`, then a
`PRINT` of `@@ROWCOUNT`. `GO`, no transaction — per-row idempotency makes a partial run re-runnable
rather than needing rollback, which is the folder's stated rationale for having no transactions.

## How the application uses it

Nothing calls this script; it is DBA-run. What depends on it is the *absence* of the union read: once
every tenant has run it, `Teams.ManagerUserId` can be dropped and `AnalysisDecisionNotifier` can read
`[dbo].[TeamManagers]` alone. Until then the notifier reads both, and this script is what makes the
two agree.

## Shipping alongside

- **064** — creates the target table. **Must run before this script.**
- **065**, **067** — independent of this one; order within the visit is 064 → 065 → 066 → 067.

## Pre-conditions

- ⚠ **Run 064 first.** This script `INSERT`s into `[dbo].[TeamManagers]`.
- Run on **every tenant database**, in the same window as 064/065.
- **No App Service restart is needed for this script on its own** — it changes no schema, so EF's
  model cache is unaffected. 064 and 065 do need one.
- It may be run **before or after** the code is published: the union read means either order works.
  Running it with the rest of the DDL, before publishing, is simply tidier.
- No Portal deploy is required.

**Deploy order: DDL 064 + 065 + 066 + 067 on every tenant DB → publish this API → restart the App Service.**

## Rollback

There is nothing to roll back that 064's rollback does not already cover: this script only inserts
rows into `[dbo].[TeamManagers]`, and dropping that table removes them.

To undo **only** this script's effect while keeping the table — removing the migrated rows but
preserving any manager added through the application — delete exactly the rows it wrote:

```sql
DELETE m
FROM   [dbo].[TeamManagers] m
JOIN   [dbo].[Teams] t ON t.[TeamId] = m.[TeamId]
WHERE  m.[CreatedBy] IS NULL
  AND  m.[UserId] = t.[ManagerUserId];
GO
```

The `CreatedBy IS NULL` predicate is load-bearing: without it this would also delete a manager an
Admin named through the application who happens to be the same person.

Nothing is lost that cannot be regenerated — `Teams.ManagerUserId` is deliberately still populated,
so re-running this script rebuilds exactly what the delete removed.

## Verification

The script carries five commented checks. The one that matters is the first: **every team with a
named manager now has the matching row**, which must return nothing. A row there is a manager the
migration missed, and that team's pre-approvals depend on the deprecated column's union read until it
is fixed.

The rest: that the two counts line up (equal immediately after the run; the table may legitimately
grow larger later); that nothing was duplicated (structurally impossible under the composite PK, so a
result means the PK is not what 064 declared); the cross-tenant integrity query, worth re-running
here because this script puts the first rows in; and a readable listing that distinguishes
"migrated by 066" from "named in the app" via `CreatedBy`.

## Tenant checklist

| Tenant | Run date | Run by | Rows migrated | Verified |
|--------|----------|--------|---------------|----------|
|        |          |        |               |          |
|        |          |        |               |          |
|        |          |        |               |          |
|        |          |        |               |          |
