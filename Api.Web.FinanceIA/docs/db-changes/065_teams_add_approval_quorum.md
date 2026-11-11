# 065 — How many signatures a team needs

## What changed and why

064 let a team have several managers. This column is where a team says how many of them have to agree
before a pre-approval is confirmed.

`[dbo].[Teams]`, one new column:

| Column | Meaning |
|---|---|
| `ApprovalQuorum` | manager signatures required to confirm; `INT NOT NULL DEFAULT (1)` |

### ⚠ It is stored and displayed, and enforced by nothing

`DecideAsync` is deliberately untouched in this release. A single confirmation still confirms an
analysis, exactly as it does today. **Phase 2** is what reads this value and counts votes (067)
against it.

It ships now for one reason: the DDL procedure treats every tenant as its own database, so each
script is a manual visit to N databases by a human. Adding this column to the same trip costs the DBA
nothing and makes Phase 2 a code-only deploy — no schema change, no `ddl_aplicada` attestation, no
window where the code is live and the column is not.

Because nothing enforces it, **the API exposes it read-only**: `TeamDto` returns it so the Portal can
display it, and `CreateTeamRequest` / `UpdateTeamRequest` do not carry it at all. An Admin cannot set
a quorum of 3 and be quietly overruled by a single signature. A control that visibly does nothing is
worse than an absent one.

### `DEFAULT (1)` is the whole safety argument

`NOT NULL` with a default of 1 backfills every existing row the moment it runs, and "one signature"
is precisely today's behaviour. So there is no such thing as a team that behaves differently because
of this script — not on the day it runs, and not on the day Phase 2 starts reading the column, since
a team nobody has reconfigured still says 1. The migration is a no-op **by construction** rather than
by luck.

The entity carries the same default as a C# initializer (`public int ApprovalQuorum { get; set; } = 1;`)
and that is load-bearing, not decoration: `TeamsController` builds `new Team { ... }` without naming
the property, and EF sends every mapped property on an INSERT — so without it EF would write an
explicit `0` and silently override the column default. The two must stay in step.

### `INT`, and no `CHECK`

`INT` rather than `TINYINT`: the storage saving is meaningless at this row count, and `INT` is what
every other count-shaped column here uses (`Documents.ExtractionAttempts`, 050). Matching the
neighbours removes a surprise.

No `CHECK` bounding the value, per 036, 046, 052, 059 and 062 — value rules live in the API, where
changing one is a deploy rather than a migration on every tenant database. The sharper reason is that
Phase 2 has not settled what the bound should be: whether a quorum may exceed the team's manager
count, and what happens when a manager is removed below it, are open questions. **060 is the worked
example of that cost** — it exists for no other purpose than to `ALTER` `CK_Analyses_Status` so it
would admit one new value.

## DDL

Script location: `docs/database/065_teams_add_approval_quorum.sql`

| Object | Type |
|---|---|
| `[dbo].[Teams].[ApprovalQuorum]` | column, `INT NOT NULL` |
| `DF_Teams_ApprovalQuorum` | default constraint, `DEFAULT (1)` |

One guarded block on `sys.columns`, `PRINT` in both branches, `GO`, no transaction. Re-running never
resets a quorum an Admin has changed — the `ALTER` simply does not execute a second time.

## How the application uses it

- **`Team.ApprovalQuorum`** is mapped by `TeamConfiguration` with `.HasDefaultValue(1).IsRequired()`,
  mirroring how `UserConfiguration` maps `IsActive`.
- **`TeamDto.ApprovalQuorum`** is returned by `GET /api/teams`. Nothing writes it from a request.
- **Nothing else reads it.** `AnalysisDecisionNotifier` does not, and `AnalysisController.DecideAsync`
  does not. That is the point of Phase 1.

## Shipping alongside

- **064** — `[dbo].[TeamManagers]`, the managers this quorum will count.
- **066** — the data migration into 064.
- **067** — `[dbo].[AnalysisApprovalVotes]`, what Phase 2 tallies against this number.

## Pre-conditions

- Apply **062 first** — this alters the table 062 creates.
- Apply **064 then 065 then 066 then 067**, on **every tenant database**, **before** publishing.
- ⚠ **This is the most dangerous of the four scripts.** `TeamConfiguration` maps `[ApprovalQuorum]`
  unconditionally, so publishing the code first makes **every** `SELECT` against `[dbo].[Teams]` fail
  with `Invalid column name 'ApprovalQuorum'`. That is not just the new field — it is the whole of
  `/api/teams` (list, create, update, delete), and also the team lookup on the notification path,
  which is fault-isolated and so fails *silently* into the RiskManager fallback rather than loudly.
  Run the DDL first.
- **Restart the App Service** after deploying so EF's model cache is rebuilt.
- Adding the column changes **no** existing behaviour: every row is backfilled to 1, which is what
  the system already does, and nothing reads it until Phase 2.
- No Portal deploy is required by this script. The Portal may display `approvalQuorum` when it is
  ready; the field is additive and omitting it breaks nothing.

**Deploy order: DDL 064 + 065 + 066 + 067 on every tenant DB → publish this API → restart the App Service.**

## Rollback

Roll the **application** back first: `TeamConfiguration` maps this column, so dropping it under a
running deployment breaks every request against `[dbo].[Teams]`, not just the new field. The default
constraint must go before the column.

```sql
ALTER TABLE [dbo].[Teams] DROP CONSTRAINT [DF_Teams_ApprovalQuorum];
GO
ALTER TABLE [dbo].[Teams] DROP COLUMN [ApprovalQuorum];
GO
```

What is lost is nothing that any deployed behaviour depends on — in **this** release the column is
read by no decision path, so removing it is invisible outside the `/api/teams` payload. Export first
only if Phase 2 has shipped and real quorums have been configured:

```sql
SELECT [TeamId], [TenantId], [Name], [ApprovalQuorum] FROM [dbo].[Teams];
```

## Verification

The script carries five commented checks: the column exists as a non-nullable `int`; the default
constraint reads `((1))`; **every existing team was backfilled to 1** — the check that proves the
script changed no behaviour; the distribution, which should be a single row in this release; and a
forward-looking query for a quorum larger than the team's manager count, which is the configuration
that could never be satisfied once Phase 2 is live. Nothing in the schema forbids it, by choice, so
that query is how it gets found.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
