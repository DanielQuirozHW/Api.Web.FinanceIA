# 067 — Where Phase 2 will record the signatures

## What changed and why

064 lets a team have several managers and 065 says how many must agree. This table is where the
individual agreements are recorded, so Phase 2 can count them.

`[dbo].[AnalysisApprovalVotes]`, one row per manager per analysis:

| Column | Meaning |
|---|---|
| `VoteId` | surrogate primary key, clustered |
| `AnalysisId` | the analysis being voted on; FK → `[dbo].[Analyses].[AnalysisId]` |
| `UserId` | the manager who voted; FK → `[dbo].[Users].[UserId]` |
| `TenantId` | tenant slug |
| `Vote` | `'Approved'` / `'Rejected'`; `NVARCHAR(20)` |
| `VotedAt` | UTC timestamp |

### ⚠⚠ Nothing in this release reads or writes it

There is **no EF entity, no `DbSet`, no repository, no controller** for this table. It will stay
**empty** until Phase 2 deploys, and an empty table here is **correct** — not a bug, not a failed
migration. Do not go looking for the code that fills it.

**Why ship it now:** the DDL procedure treats every tenant as its own database, so each script is a
manual visit to N databases by a human. Phase 1 already requires three of those visits. Adding the
fourth to the same trip costs the DBA nothing and makes Phase 2 a code-only deploy — no schema
change, no `ddl_aplicada` attestation, no window where the code is live and the table is not. The
asymmetry is the whole argument: creating an unused table is free; deploying code against a missing
one breaks production.

### Not covered by `--verify-schema`, by design

The deploy gate compares **this build's EF model** against `sys.objects` / `sys.columns`. No entity
maps this table, so the model does not mention it and the gate neither expects nor checks it. Extra
tables in the database are invisible to it — which is exactly what makes provisioning ahead safe.

`SchemaExpectationTests` therefore deliberately does **not** list `AnalysisApprovalVotes`, and there
is a comment there saying so. A gate that expects a table no build maps would fail against a model
that is correct. Phase 2 adds the entity and the `InlineData` together.

### ⚠ The key is `AnalysisId`, not `ResultId`

This is the trap in this script, because the route does the opposite.
`PATCH /api/analyses/{id}/decision` takes a **ResultId**: `DecideAsync` resolves
`[dbo].[AnalysisResults]` by it and only then hops to the analysis via `result.AnalysisId`.

But the **decision state machine** lives entirely on `[dbo].[Analyses]` — `Status`, `PreApprovedBy`,
`PreApprovedAt`, `ReviewedBy` are all columns there (058, 060, 061). A vote is a signature on that
state machine, so it keys on the row that carries it.

`ResultId` is the deep-link id and nothing more — PR #219 exists precisely to make
`Notifications.SourceId` carry it for that reason. Keying votes on it would mean a re-scored analysis
with a second result row could collect two independent quorums.

### Surrogate PK here, composite in 064

Deliberate, not an inconsistency. A `[dbo].[TeamManagers]` row is a **membership fact**: it has no
life of its own, nothing addresses one, and `(TeamId, UserId)` is its whole identity. A vote is an
**event**: it happened at a time, by a person, and Phase 2 will want to address one individually — to
amend it, to show it in an audit trail, to reference it from a future decision-log row. Events get
surrogate keys in this schema (`Notes`, `Alerts`, `Notifications` all do); membership facts do not.

### The unique constraint is the business rule

`UQ_AnalysisApprovalVotes_Analysis_User UNIQUE (AnalysisId, UserId)` — one manager, one vote per
analysis. A constraint rather than an application check, because Phase 2's failure mode is a **race**:
two browser tabs, or a retried request, confirming the same analysis at the same moment. An
application-side "have they voted already?" read cannot close that window; a unique constraint can,
and the named constraint is what Phase 2 catches to turn the second one into a clean 409 rather than
a 500.

It leads with `AnalysisId`, so it doubles as the index for the only read Phase 2 needs — every vote
on this analysis, to tally them. Hence **no separate index**, the same reasoning 064 gives.

An index on `UserId` ("everything this manager has voted on") is deliberately **not** added: no
screen asks for it, and 063's argument applies. Add it in Phase 2 if a "my pending approvals" view
actually needs it.

### No `CHECK` on `[Vote]`

Beyond the 036/046/052/059/062 convention, this one has hard evidence: `[dbo].[Analyses]` *has* such
a constraint, `CK_Analyses_Status`, and **script 060 exists for no other purpose** than to `ALTER` it
on every tenant database so it would admit one single new value, `'PreApproved'`. That is the cost of
freezing a vocabulary into the schema. Phase 2 has not settled this one — whether an abstention or a
"returned" vote belongs here is open — so the API will validate the value and the schema stays out of
it.

## DDL

Script location: `docs/database/067_analysis_approval_votes_table.sql`

| Object | Type |
|---|---|
| `[dbo].[AnalysisApprovalVotes]` | table, 6 columns |
| `PK_AnalysisApprovalVotes` | primary key, clustered, `(VoteId)` |
| `DF_AnalysisApprovalVotes_VoteId` | default constraint, `DEFAULT (NEWSEQUENTIALID())` |
| `DF_AnalysisApprovalVotes_VotedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `UQ_AnalysisApprovalVotes_Analysis_User` | unique constraint, `(AnalysisId, UserId)` |
| `FK_AnalysisApprovalVotes_Analysis` | foreign key → `[dbo].[Analyses]([AnalysisId])`, no cascade |
| `FK_AnalysisApprovalVotes_User` | foreign key → `[dbo].[Users]([UserId])`, no cascade |

Three guarded blocks on `sys.objects` / `sys.foreign_keys`, `PRINT` in both branches, `GO` per batch,
no transaction. The `NEWSEQUENTIALID()` default is for rows written **by hand**; Phase 2's
application code will supply the Guid itself, as every other entity here does — see
`NotificationConfiguration` on why telling EF the database generates the key breaks the INSERT
silently.

Both foreign keys are declared (049's subject test: a vote pointing at a non-existent analysis or
user can never be tallied) and neither cascades. The no-cascade matters more here than usual:
accounts are deactivated via `IsActive`, never deleted, and a cascade would destroy the signatures
that justified a credit decision because the signatory left the company. Those are exactly the rows
an auditor asks for.

## How the application uses it

**Nothing yet, by design.** See the top of this document. Phase 2 will add the entity, its Fluent
configuration, a repository, the write in `DecideAsync`, and the matching `SchemaExpectationTests`
entry.

## Shipping alongside

- **064**, **065**, **066** — Phase 1. This script is independent of all three; it only needs
  `[dbo].[Analyses]` and `[dbo].[Users]`, which long predate them.

## Pre-conditions

- Run on **every tenant database**, in the same window as 064–066.
- **No App Service restart is needed for this script on its own** — no deployed code maps this table,
  so EF's model cache is unaffected. 064 and 065 do need one.
- Creating the table changes **no** behaviour whatsoever in this release. It cannot: nothing reads it.
- No Portal deploy is required.

**Deploy order: DDL 064 + 065 + 066 + 067 on every tenant DB → publish this API → restart the App Service.**

## Rollback

In **this** release the rollback is free and needs no application change first: no deployed code maps
the table, so dropping it is invisible to the running API. Nothing references it either — both
foreign keys point *out* of it.

```sql
DROP TABLE [dbo].[AnalysisApprovalVotes];
GO
```

Nothing is lost, because in this release the table is empty by construction.

⚠ **From Phase 2 on this is no longer true.** Once the entity exists, roll the application back first
— dropping the table under a running Phase 2 deployment breaks the decision endpoint with
`Invalid object name` — and the rows become the audit record of who signed off which credit decision.
Export before dropping:

```sql
SELECT [VoteId], [AnalysisId], [UserId], [TenantId], [Vote], [VotedAt]
FROM   [dbo].[AnalysisApprovalVotes];
```

What is lost then is the per-manager signature trail. The decisions themselves are **not** affected —
`Analyses.Status`, `PreApprovedBy` and `ReviewedBy` carry the outcome on their own table — but the
record of *who made up the quorum* is gone, and every in-flight pre-approval reverts to needing a
single confirmation.

## Verification

The script carries eight commented checks: the six columns and their types; the clustered PK on
`VoteId`; that the unique constraint exists, is unique, and leads with `AnalysisId`; both foreign keys
enabled and trusted; and — the one that matters in this release — that **the table is empty and must
stay empty until Phase 2 deploys**. A non-zero count before then means something is writing it that
should not be, and the application in this release has no code path that can.

Three further queries are written for Phase 2 and cannot fire yet: the cross-tenant integrity check,
the vote tally against the team's quorum, and the duplicate-vote check that proves the unique
constraint is doing its job.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |

---

## Phase 2 update — the table is now mapped (no new DDL)

Everything above described this table while **nothing mapped it**. The quorum engine changes that.
The script itself is unchanged and there is no 068: Phase 2 is a code-only deploy, which was the whole
argument for provisioning the table in the Phase 1 batch.

What is now true and was not:

- **An entity maps it.** `Models/Financia/AnalysisApprovalVote.cs` +
  `Data/Configurations/Financia/AnalysisApprovalVoteConfiguration.cs`, with the unique index declared
  under its DDL name so the repository can tell a duplicate vote from any other write failure.
- **`--verify-schema` now covers it.** The gate reads the EF model, and the model now names the table,
  so a tenant database that never ran 067 **fails the deploy** instead of failing at runtime. This is
  the reversal the "not covered by --verify-schema, and that is by design" note above anticipated;
  `SchemaExpectationTests` gained the matching `InlineData` in the same change.
- **Verification query 5 is now void.** "The table is empty and must stay empty" was correct only
  while nothing wrote it. From this release a non-zero count is the normal state. Queries 6, 7 and 8 —
  cross-tenant integrity, the tally against `Teams.ApprovalQuorum`, and the duplicate-vote check —
  become live and are worth keeping in the run sheet.
- **The rollback is no longer free.** Roll the APPLICATION back first; dropping the table under a
  running Phase 2 deployment breaks the decision endpoint with `Invalid object name`.

### Deploy order

DDL first, as always — the fact that the table was created in Phase 1's batch does not exempt it:

1. Confirm **067 has run on every tenant database** (the checklist below is the record). It ran with
   064/065/066 in the Phase 1 window for tenants that took that window; any tenant added since needs it.
2. Deploy this API. `--verify-schema` refuses the deploy if any configured database is missing the table.
3. Restart the App Service, so EF's model cache picks up the new mapping.

### Rollout note

The engine ships **inert**. `Teams.ApprovalQuorum` defaults to 1 on every team and the API still
exposes it read-only, so until a DBA raises it for a specific team every analysis confirms on one
signature exactly as before. Raising a quorum is what turns the feature on, one team at a time.

### What a `Rejected` vote row is, and is not

The script left it open whether a rejection belongs in this table. It does, but only as a trail: a
veto is written to `Analyses` and settles the analysis immediately, and the vote row records **who**
vetoed beside the approvals that were standing when they did. It is never counted — `GetApproverUserIds`
filters on `'Approved'` — and no quorum applies to a rejection.
