# 064 — A team may have more than one manager

## What changed and why

062 gave an escalation an addressee. It gave it exactly one, and one manager is a single point of
failure: they go on holiday and every pre-approval from that team sits in `PreApproved` until someone
notices. The RiskManager fallback does not help, because it only fires when the manager is **absent
or deactivated**, never when they are merely unavailable.

`[dbo].[TeamManagers]`, one row per manager of a team:

| Column | Meaning |
|---|---|
| `TeamId` | the team; FK → `[dbo].[Teams].[TeamId]`, part of the composite PK |
| `UserId` | the manager; FK → `[dbo].[Users].[UserId]`, part of the composite PK |
| `TenantId` | tenant slug, copied from the **team** (the row records "this team has this manager") |
| `CreatedAt` / `CreatedBy` | audit trail; `CreatedBy` is NULL for rows 066 migrated |

This is **Phase 1** of the team-quorum feature: it changes **who is notified**, and nothing else.
`DecideAsync` is untouched — a single confirmation still confirms. 065 adds the quorum column, 067
provisions the vote table, and Phase 2 is what makes them mean anything.

### Why the primary key is composite, and why that differs from 067

`PK_TeamManagers` is `CLUSTERED (TeamId, UserId)`. There is no surrogate `TeamManagerId`, and the
absence is the design:

- **The pair is the identity.** Nothing addresses one row of this table — no route takes an id, no
  DTO carries one, nothing points at it. Contrast `[dbo].[Teams]` and `[dbo].[Notifications]`, which
  carry surrogate GUIDs precisely *because* they are addressed by id.
- **It makes the uniqueness rule structural.** A surrogate key would still need
  `UNIQUE (TeamId, UserId)` to stop the same person being named twice — the same index, plus a
  redundant key column, plus a second object to keep in step.
- **It is the index.** The only read is "every manager of this team", and a clustered
  `(TeamId, UserId)` serves it as a covering seek.

067 makes the opposite choice for `[dbo].[AnalysisApprovalVotes]`, deliberately. A vote is an
**event** with its own timestamp that Phase 2 will address individually; a team-manager row is a
**membership fact** with no life of its own. Events get surrogate keys in this schema
(`Notes`, `Alerts`, `Notifications` all do); membership facts do not.

### No index, on purpose

The clustered PK already covers the only read path. Nothing looks up teams **by** manager, and
Phase 2's "is this user a manager of that team?" is a point lookup on the full composite key — the
same seek again. An index written on every membership change and read by nothing is a cost, not a
win: the argument 063 makes for `Users.TeamId`, applied here.

### Constraints

Both foreign keys **are** declared, following 027, 049, 054, 059 and 062 — the 049 test: each column
is a live *subject*, not a record of a past action. A row pointing at a team or user that does not
exist can never notify anyone.

`CreatedBy` carries **no** foreign key, the other half of the same rule (030, 040, 041, 046, 059,
062), and is nullable because 066 writes NULL for every row it migrates.

No `CASCADE` on either, per 027, 049, 059 and 062. **This has a direct consequence on the delete
path:** `DELETE /api/bff/teams/{id}` must now clear this table before removing the team row, exactly
as `FK_Users_Team` already forces it to unassign members first. A team delete satisfies **both**
constraints or it fails at the database. `TeamsController` does this; see below.

## DDL

Script location: `docs/database/064_team_managers_table.sql`

| Object | Type |
|---|---|
| `[dbo].[TeamManagers]` | table, 5 columns |
| `PK_TeamManagers` | primary key, clustered, **composite** `(TeamId, UserId)` |
| `DF_TeamManagers_CreatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `FK_TeamManagers_Team` | foreign key → `[dbo].[Teams]([TeamId])`, no cascade |
| `FK_TeamManagers_User` | foreign key → `[dbo].[Users]([UserId])`, no cascade |

Three guarded blocks on `sys.objects` / `sys.foreign_keys`, `PRINT` in both branches, `GO` per batch,
no transaction — the conventions in `docs/database/README.md`. The foreign keys sit in their own
batches after the table's `GO`, per 063's precedent: the table must be committed before a constraint
can reference it in the same run.

## How the application uses it

`TeamManager` is a mapped entity on `FinanciaDbContext` with a composite key, reached through
`ITeamRepository`, registered scoped. Every query is scoped by `TenantId` by hand — no global query
filter on this context.

- **Read, on the notification path**: `GetManagerUserIdsAsync(teamId, tenantId, ct)`, projecting to
  `Guid`. `AnalysisDecisionNotifier` calls it once per decision and notifies **every** id it returns.
- **Write, mirrored from the Admin screen**: `ReplaceManagersAsync(teamId, tenantId, userIds,
  createdBy, ct)` — replace-the-set, following `IUserApprovalLimitRepository.ReplaceForUserAsync`.
  `POST` and `PUT /api/teams` call it with the single manager they set; `DELETE` calls it with an
  empty set before removing the team.

### The union read, and why `Teams.ManagerUserId` survives

`Teams.ManagerUserId` is **retained but deprecated**. The notifier reads the **union** of this table
and that column, de-duplicated, because:

- 066 is a **data** script, and the `--verify-schema` deploy gate compares *columns*, not *rows*. A
  tenant whose 066 was skipped would pass every check and still silently lose its named manager to
  the fan-out. The union closes that window.
- The existing single-manager Admin UI is not being replaced in this release, so the column is still
  written. Keeping both in step and reading both means either source alone is enough.
- It is the rollback path: with the column intact, dropping this table returns the system to its
  pre-064 behaviour rather than to teams with no manager at all.

The fallback ladder is **unchanged** — active RiskManagers, then active Admins only if there are
none. What changed is when it fires: previously on three gaps, now on four, the fourth being *every*
named manager deactivated. One active manager out of three keeps the ladder out of it.

## Shipping alongside

- **065** — `Teams.ApprovalQuorum`, how many of these managers must agree (Phase 2).
- **066** — copies the deprecated `Teams.ManagerUserId` into this table. **Must run after this one.**
- **067** — `[dbo].[AnalysisApprovalVotes]`, provisioned for Phase 2.

## Pre-conditions

- Apply **062 and 063 first** — this script's foreign keys target both tables.
- Apply **064 then 065 then 066 then 067**, on **every tenant database**, **before** publishing.
- ⚠ `TeamManager` is a mapped entity, so EF Core maps `[dbo].[TeamManagers]` unconditionally.
  Against a database without the table, every `/api/teams` request fails with `Invalid object name`.
  The notification path degrades more gently — the notifier is fault-isolated and only logs a
  Warning — but the endpoints do not.
- **Restart the App Service** after deploying so EF's model cache is rebuilt.
- Creating the table changes **no** existing behaviour on its own: until the new code is live nothing
  reads it, and once live, an empty table for a team falls back to that team's deprecated
  `ManagerUserId` and, failing that, to the ladder.
- No Portal deploy is required (no enum value added; `TeamDto` only gains a field).

**Deploy order: DDL 064 + 065 + 066 + 067 on every tenant DB → publish this API → restart the App Service.**

## Rollback

Roll the **application** back first — `TeamManager` is mapped, so dropping the table under a running
deployment breaks `/api/teams` with `Invalid object name`.

Nothing references this table; both foreign keys point *out* of it and go with it.

```sql
DROP TABLE [dbo].[TeamManagers];
GO
```

Export first if the extra managers matter:

```sql
SELECT [TeamId], [UserId], [TenantId], [CreatedAt], [CreatedBy] FROM [dbo].[TeamManagers];
```

What is lost is the multi-manager structure **only**. No team, user, analysis or notification row is
touched, and `Teams.ManagerUserId` is deliberately still populated — this is exactly what the
retained column buys — so after a rollback every team falls back to its single named manager. That
is the pre-064 behaviour and a working state, not a broken one.

If **062** is rolled back too, run this rollback **first**: `FK_TeamManagers_Team` blocks
`DROP TABLE [dbo].[Teams]` while it exists.

## Verification

The script carries seven commented checks: the five columns and their types; that the primary key is
clustered **and composite with `TeamId` first**; both foreign keys enabled and trusted; that there is
**exactly one** index (a second means somebody added one this table does not need); that the table
starts empty (066 fills it); a cross-tenant integrity query that must return nothing; and a
managers-per-team listing that, after 066, shows at least one manager wherever `ManagerUserId` is
non-null.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
