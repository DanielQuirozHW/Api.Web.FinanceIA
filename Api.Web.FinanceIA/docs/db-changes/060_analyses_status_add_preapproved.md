# 060 — Admitting the state an analysis sits in while it waits for a second signature

## What changed and why

`[dbo].[Analyses].[Status]` **is** constrained. `CK_Analyses_Status`, created in
`001_initial_schema.sql`, admits exactly six values:

```
Queued, Processing, PendingReview, Reviewed, Approved, Rejected
```

The two-step decision flow introduces a seventh: `PreApproved`, the state an analysis enters when an
analyst commits to an amount above their own approval limit and it is waiting on a RiskManager. This
script widens the constraint to admit it.

**This script is required, not optional.** Without it, 058's columns exist but every pre-approval
fails on the CHECK — the feature cannot write a single row. It was worth verifying rather than
assuming: the project generally prefers validating evolving vocabularies in the API (029, 036, 046,
052 all say so explicitly), and 024's header notes it did not need a constraint change because
`Approved` and `Rejected` were already whitelisted. `Status` is the older, stricter case, and no
script has ever altered this particular constraint before.

### Why widen rather than drop

`Status` is not a vendor catalogue that grows unpredictably; it is a state machine whose members are
known in advance, the constraint already exists, and removing it altogether would be a far larger
change than this feature warrants. So the list gains a value and loses none.

### No new state for "returned to the analyst"

A RiskManager who will not confirm but does not want to reject can send the analysis back for rework.
That destino is the **existing** `PendingReview` — already legal, already the status a freshly
created analysis carries, and already exactly what it means. Inventing a second name for a state that
exists would have cost a value here and a branch in every consumer, for nothing.

So the API accepts three decision values (`Approved`, `Rejected`, `PendingReview`) but this script
adds only one.

### `WITH CHECK` is deliberate

Following 043. Every existing row already satisfies the **wider** list — this only adds a value — so
validation cannot fail on legitimate data. If it does fail, some unexpected value is already present
and failing loudly is correct. The verification section checks `is_not_trusted = 0` for exactly this
reason: a constraint added without validation would hide such a row.

## DDL

Script location: `docs/database/060_analyses_status_add_preapproved.sql`

| Object | Type |
|---|---|
| `CK_Analyses_Status` | check constraint, dropped and recreated with `N'PreApproved'` added |

The drop is guarded on `sys.check_constraints` (by `name` + `parent_object_id`, as 043 does) and
prints in both branches; the re-add is unconditional and immediately follows, so a re-run leaves
exactly one constraint carrying the full list. There is no `ALTER` for a CHECK constraint's
expression in T-SQL — drop and recreate is the only route, as 009, 012 and 043 all did for
`CK_ApiCallLogs_Service`.

The value list is written in the newer `IN (N'...')` form used by 039 and 043 rather than 001's
SSMS-generated chained-`OR` form. The two are equivalent; the newer form is legible.

## How the application uses it

`AnalysisController` writes `PreApproved` from a named constant (`PreApprovedStatus`) rather than an
inline literal, alongside `PendingReviewStatus` — the two the routing branches on. The value is
surfaced unchanged on `AnalysisResultResponse.status`.

## Shipping alongside

- **058** — the pre-approval and decision-amount columns this state records into.
- **059** — `[dbo].[UserApprovalLimits]`, which decides when a decision becomes a pre-approval.

Any one of the three alone leaves the feature broken:

- 058 + 059 without 060 → every pre-approval fails on the CHECK.
- 060 without 058 → the state is legal but has nowhere to record who pre-approved, or at what amount.

## Pre-conditions

- Apply **058, 059 and 060 together**, on **every tenant database**, **before** publishing the API.
  Order among the three does not matter — they touch different objects and none depends on another's
  data.
- **The Portal must be deployed BEFORE this API.** This is the same situation as 040, which
  introduced `'Aprobada'` on `[CreditLineOperations]`: a Portal that does not know `'PreApproved'`
  has no branch for it in its status renderer. (058 adds a second, independent reason: the decision
  payload now requires an amount and currency on an approval.)
- **Restart the App Service** after deploying.

**Deploy order: Portal → DDL 058 + 059 + 060 on every tenant DB → publish this API → restart the App Service.**

## Rollback

Narrowing this constraint **fails while any row still holds `'PreApproved'`**, and that is the
correct behaviour: those analyses are mid-decision, and silently rewriting them would fabricate a
decision nobody made.

Resolve them first — through the API, by confirming or rejecting each one, which is the honest route.
To see what is outstanding:

```sql
SELECT [AnalysisId], [PreApprovedBy], [PreApprovedAt],
       [PreApprovedAmount], [PreApprovedCurrency]
FROM   [dbo].[Analyses]
WHERE  [Status] = N'PreApproved';
```

Once that returns no rows:

```sql
ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [CK_Analyses_Status];
GO
ALTER TABLE [dbo].[Analyses] WITH CHECK
    ADD CONSTRAINT [CK_Analyses_Status]
    CHECK ([Status] IN (N'Queued', N'Processing', N'PendingReview',
                        N'Reviewed', N'Approved', N'Rejected'));
GO
```

If those analyses genuinely cannot be decided through the API, returning them to the analyst is the
least destructive alternative — it discards no final decision, because a pre-approved analysis has
none yet:

```sql
UPDATE [dbo].[Analyses] SET [Status] = N'PendingReview'
WHERE  [Status] = N'PreApproved';
```

Roll the application back **before** narrowing either way, or the running code will keep writing
`'PreApproved'` and start failing on the constraint.

## Verification

The script carries five commented checks: that the constraint exists, is enabled and is **trusted**;
that exactly one check constraint governs `Status` (a failed re-run could conceivably leave a
duplicate); that no row holds a value outside the new list; that the new value is genuinely accepted
(inside an explicit transaction that is rolled back, so it leaves no fabricated pre-approval); and a
distribution query — a `PreApproved` count that only grows means nobody is confirming, which is a
process problem, but this is where it surfaces first.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
