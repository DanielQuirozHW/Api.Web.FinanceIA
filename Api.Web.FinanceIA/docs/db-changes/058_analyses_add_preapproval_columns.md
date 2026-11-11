# 058 — A credit decision that can carry two signatures instead of one

## What changed and why

Until now a decision on an analysis was one act by one person. `PATCH /api/analyses/{id}/decision`
wrote `Status`, `ReviewedBy`, `ReviewedAt` and `ReviewNotes`, and any analyst could approve any
amount. There was no way to express "this analyst may commit up to X, and beyond that somebody more
senior has to agree" — the schema had exactly one slot for a decider, so a second signature had
nowhere to go.

This script adds the second record, plus the figures that were never stored at all.

| Column | Meaning |
|---|---|
| `PreApprovedBy` | the analyst who pre-approved |
| `PreApprovedAt` | when they did |
| `PreApprovedAmount` | the amount they **proposed** |
| `PreApprovedCurrency` | its ISO 4217 code |
| `DecisionAmount` | the amount actually **committed** |
| `DecisionCurrency` | its ISO 4217 code |

### ReviewedBy/At/Notes are reused, not duplicated

There are no new columns for the final decider. `ReviewedBy` / `ReviewedAt` / `ReviewNotes` already
meant "who settled this and when", which is precisely what a confirmation is. A parallel
`ConfirmedBy` / `ConfirmedAt` pair would have left two candidates for "who approved this analysis"
and no rule for choosing between them — and every existing consumer reading `reviewedBy` would have
silently kept reading the wrong one.

So the two records are:

- **pre-approval** — `PreApprovedBy/At/Amount/Currency`, written only when an analyst commits above
  their own limit;
- **final decision** — `ReviewedBy/At/Notes` + `DecisionAmount/Currency`, written when the decision
  is actually settled, whether in one step or two.

### Why both amounts are kept

They are not redundant. A RiskManager confirming a pre-approval may **lower** the figure, and the
pair `(PreApprovedAmount, DecisionAmount)` is the only place that adjustment exists. Where the two
differ, someone overrode someone; where the pre-approval group is entirely NULL, no second signature
was ever needed. Overwriting `PreApprovedAmount` at confirmation time would have destroyed exactly
the fact the two-step flow was built to record, so the confirmation deliberately leaves it alone.

`Analyses` carried no currency column at all before this — `RequestedAmount` was a bare number.
Currency now matters because it selects *which* approval limit applies (DDL 059), so it is stored on
both halves rather than assumed.

### Why all six are nullable, when 057 next door chose NOT NULL

057 added `BIT NOT NULL DEFAULT 0` flags and argued that 0 was a *true statement* about every
existing row. That reasoning does not transfer. These columns record an **event that either happened
or did not**: an analysis approved directly, within limit, never had a pre-approver, and neither did
any analysis decided before this release. NULL is the honest "no pre-approval was made" — a default
would be a guess dressed as data.

This follows 040 and 041, whose audit columns are nullable for the same reason.

The practical consequence: **no backfill, no data migration**. Every already-`Approved` row stays
valid untouched and simply carries no pre-approval record, which is true of it.

### Why the names avoid `ApprovedAmount`

`[ApprovedAmount]` and `[ApprovedCurrency]` existed on this table once — added by 024, dropped by
031 — as a first-pass per-analysis credit line that never reached production. Reusing those names
would mean a re-run of 031 against a stale database silently dropping columns this feature depends
on. `DecisionAmount` / `DecisionCurrency` cannot collide with anything.

### Why `FK_Analyses_PreApprovedBy` exists when 030/046 omit theirs

049 records the rule: a foreign key to `Users` earns its place when the column is a live *subject*,
not a record of who performed a past action, because an actor may outlive their account. By that
rule alone `PreApprovedBy` would take no FK.

The local precedent wins here. On **this table**, `RequestedBy`, `ReviewedBy` and `SentToSGRBy` each
carry an FK from `001_initial_schema`, and `PreApprovedBy` is the exact peer of `ReviewedBy` — the
two are read together as the two halves of one decision. A table whose four actor columns disagree
about referential integrity is worse than either rule applied consistently. No `CASCADE`, per 027 and
049: accounts are deactivated via `IsActive`, never deleted.

## DDL

Script location: `docs/database/058_analyses_add_preapproval_columns.sql`

| Object | Type |
|---|---|
| `[dbo].[Analyses].[PreApprovedBy]` | column, `UNIQUEIDENTIFIER NULL` |
| `[dbo].[Analyses].[PreApprovedAt]` | column, `DATETIME2(7) NULL` |
| `[dbo].[Analyses].[PreApprovedAmount]` | column, `DECIMAL(18,2) NULL` |
| `[dbo].[Analyses].[PreApprovedCurrency]` | column, `NVARCHAR(3) NULL` |
| `[dbo].[Analyses].[DecisionAmount]` | column, `DECIMAL(18,2) NULL` |
| `[dbo].[Analyses].[DecisionCurrency]` | column, `NVARCHAR(3) NULL` |
| `FK_Analyses_PreApprovedBy` | foreign key → `[dbo].[Users]([UserId])`, no cascade |

Seven guarded blocks on `sys.columns` / `sys.foreign_keys`, `PRINT` in both branches, `GO` per
batch, no transaction — the conventions in `docs/database/README.md`.

`DECIMAL(18,2)` matches `RequestedAmount` on this table and `Amount` on `[dbo].[CreditLines]`, so a
confirmed decision amount becomes a cupo without a precision change. `NVARCHAR(3)` matches
`[dbo].[CreditLines].[Currency]`.

No `CHECK` on either currency: the accepted codes live in `Clases/Currencies.cs` and are validated in
the API, following 036, 046 and 052. No index: none of the six is a predicate anywhere.

## How the application uses it

All six are mapped in `AnalysisConfiguration` and surfaced on `AnalysisResultResponse` as
`preApprovedBy` / `preApprovedByName` / `preApprovedAt` / `preApprovedAmount` /
`preApprovedCurrency` / `decisionAmount` / `decisionCurrency`.

`AnalysisController.DecideAsync` routes by the caller's role — read from the **database row**, not
the JWT claim — and, for an analyst, by their limit for the currency:

- analyst within limit → `Approved`, final record only;
- analyst above limit → `PreApproved`, pre-approval record only;
- RiskManager/Admin on a pre-approved analysis → confirm (final record written, pre-approval record
  **preserved**), reject, or return for rework.

The pre-approver's name is resolved alongside the requester's and the reviewer's in **one** batched
`IUserRepository.GetByIdsAsync`, with the `"Usuario"` fallback. `GetById` previously issued a
separate single-id read per actor; a third actor would have made that three round trips, so it was
converted to the batched form at the same time.

## Shipping alongside

- **059** — `[dbo].[UserApprovalLimits]`, the per-(user, currency) ceiling that decides when a
  decision becomes a pre-approval.
- **060** — widens `CK_Analyses_Status` to admit `'PreApproved'`. **Without 060 this feature cannot
  write a single pre-approval**; the constraint refuses the value.

## Pre-conditions

- Apply **058, 059 and 060 together**, on **every tenant database**, **before** publishing the API.
  Order among the three does not matter — they touch different objects and none depends on
  another's data — but all three must land before the deploy.
- All six columns are EF-mapped, and `AnalysisRepository.GetByIdAsync` selects the **full entity**
  with no projection while `UpdateAsync` calls `.Update()` (a full-row write). EF therefore emits
  them in every `SELECT` and every `UPDATE` against `[dbo].[Analyses]`. Against a database without
  them, the decision endpoint, the analysis detail **and** the analyses list all fail with
  `Invalid column name`, and no application-level `try/catch` can protect against it. Same
  deploy-order constraint as 040, 041 and 057.
- **The Portal must be deployed BEFORE this API**, like 040 and unlike 057. Two reasons compound:
  `'PreApproved'` is a new `Status` value the Portal's renderer has no branch for, and the decision
  payload now **requires** `amount` and `currency` on an approval — the current Portal's approve call
  becomes a `400` the moment this API is live.
- **Restart the App Service** after deploying so EF's model cache is rebuilt.

**Deploy order: Portal → DDL 058 + 059 + 060 on every tenant DB → publish this API → restart the App Service.**

## Rollback

Roll the **application** back first — the deployed code maps all six columns, so dropping them under
a running deployment breaks every read and write of an analysis.

```sql
ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [FK_Analyses_PreApprovedBy];
GO
ALTER TABLE [dbo].[Analyses] DROP COLUMN [DecisionCurrency];
ALTER TABLE [dbo].[Analyses] DROP COLUMN [DecisionAmount];
ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedCurrency];
ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedAmount];
ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedAt];
ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovedBy];
GO
```

Export first — the pre-approval half of every two-signature decision exists in no other column and
in no audit row beyond the `Analysis.PreApproved` / `Analysis.Confirmed` entries:

```sql
SELECT [AnalysisId], [Status], [PreApprovedBy], [PreApprovedAt],
       [PreApprovedAmount], [PreApprovedCurrency],
       [DecisionAmount], [DecisionCurrency]
FROM   [dbo].[Analyses]
WHERE  [PreApprovedBy] IS NOT NULL OR [Status] = N'PreApproved';
```

What **survives** a rollback is the final decision on every confirmed analysis: `Status`,
`ReviewedBy`, `ReviewedAt` and `ReviewNotes` are untouched by this script, so no approval is lost —
only the record of who proposed it and at what figure. Any analysis still sitting in `'PreApproved'`
has no final decision at all and must be re-decided; 060's rollback note covers that case.

## Verification

The script carries five commented checks: the six columns and their types, the foreign key's
existence and trust, that **nothing was backfilled** (run before the deploy), that a real two-step
decision round-trips with both signatures present, and that single-step approvals predating this
release still read correctly and are not to be backfilled.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
