# 061 — The operations behind a decision, on both sides of the hand-off

## What changed and why

`058` gave a credit decision two amounts — one proposed, one committed — and that turned out to be
only half of the record. When an analyst decides an amount above their limit they also enter the
**operations** the cupo is made up of: type, importe, vigencia, *a sola firma*, *contragarantía
real*. The API had nowhere to put them. `DecideAsync` wrote `PreApprovedAmount` and
`PreApprovedCurrency` and dropped the rest on the floor, so the RiskManager who confirms received a
bare figure and had to reconstruct the proposal by hand, or guess at it.

This script adds the two columns those operations go in.

| Column | Meaning |
|---|---|
| `ProposedOperationsJson` | the operations the **analyst proposed** at pre-approval |
| `ConfirmedOperationsJson` | the operations the **final decision settled on** |

Both `NVARCHAR(MAX) NULL`, each holding a JSON array. They are the operation counterpart of 058's
amount pair and are read the same way.

### Why two columns and not one

The same reason 058 keeps two amounts. A confirmer may change the operations and not just the
total — drop one, split another, shorten a vigencia — and the pair is the only place that
adjustment is visible. A single column would record the last writer and destroy the proposal it
overwrote, which is exactly the loss this script exists to stop. A confirmation therefore **never**
rewrites `ProposedOperationsJson`.

### Why a JSON column and not a child table

These are the **terms of a decision**, frozen on the analysis — not live credit. The live rows keep
living in `[dbo].[CreditLineOperations]`, created by `CreditLinesController` once the analysis is
`Approved`, with their own lifecycle, their own foreign keys and their own audit columns (040, 041,
057). A child table here would be a second, mutable home for the same shape and would invite the
question of which of the two is the real operation.

As a plain JSON blob this follows `[dbo].[Entities].[LufeDataJson]` and
`[dbo].[AnalysisResults].[BcraDataJson]`: serialised manually at the controller boundary, read back
as one value, never joined — the rule `CLAUDE.md` states for payloads of this kind (plain `string`
properties, no value converter, no owned entity). Note this is the **first** JSON column on
`[dbo].[Analyses]`; `[ReviewNotes]` is `NVARCHAR(MAX)` but free text.

### The element shape is the operation shape

Each element carries `type`, `amount`, `currency`, `validUntil`, `notes`, `aSolaFirma` and
`requiereContragarantiaReal` — matching `CreateOperationRequest` field for field, **minus
`AnalysisId`**. On that request the id ties a loose operation to the analysis that justifies it;
here the operation is stored *inside* that analysis, so carrying the id again would create a second,
contradictable copy of the same fact. A confirmed element can therefore be handed straight to
`POST /api/credit-lines/{creditLineId}/operations` with no translation layer. The API normalises the
currency before storing, so the blob is canonical.

### Why both are nullable, and why the two are asymmetric

Following 058 next door rather than 057: like the pre-approval columns, these record an **event that
either happened or did not**, so `NULL` is the honest "no operations were recorded on this side" and
not a guess. No backfill, no data migration — every analysis already decided stays valid untouched.

An empty array is **never** stored. The API writes `NULL` when a decision carries no operations, so
"nothing was recorded" stays one value in the column instead of two.

The two columns are independent:

- `ProposedOperationsJson` non-`NULL` → an analyst escalated a proposal above their limit.
- `ConfirmedOperationsJson` non-`NULL` → a figure was actually committed, in one step or two.

So a **one-step approval** (within the analyst's limit) fills confirmed and leaves proposed `NULL`,
exactly as it leaves `PreApprovedAmount` `NULL` today; a rejection or a return leaves confirmed
`NULL`, exactly as it leaves `DecisionAmount` `NULL`. Reading proposed as "the operations of this
decision" is therefore wrong for the common case — read **confirmed** for what was granted, and the
pair only when both are present.

### Why no CHECK, no ISJSON, no FK, no index

The element contract is validated in the API (`Models/Financia/AnalysisDecisionOperation.cs`, capped
at 50 operations, types against `Clases/CreditLineTypes.cs`, currency against
`Clases/Currencies.cs`), following the reasoning recorded in 036, 046, 052 and 058: a value list or a
payload shape in the schema turns adding a line type into a migration on every tenant database. The
read path is defensive — a blob that will not parse reads as "no operations recorded" rather than
failing the endpoint — so a hand-edited row cannot break the analysis detail either.

No foreign key, because there is nothing to point at: the elements are frozen terms, not rows in
`[dbo].[CreditLineOperations]`. An FK would tie a historical decision to a live operation that can
later be cancelled, and rewrite history when it was.

No index: neither column is a predicate anywhere — both are read as attributes of an analysis
already located by its primary key, or through the existing result → analysis path. An index on an
`NVARCHAR(MAX)` column is not possible in any case.

## DDL

Script location: `docs/database/061_analyses_add_proposed_confirmed_operations.sql`

| Object | Type |
|---|---|
| `[dbo].[Analyses].[ProposedOperationsJson]` | column, `NVARCHAR(MAX) NULL` |
| `[dbo].[Analyses].[ConfirmedOperationsJson]` | column, `NVARCHAR(MAX) NULL` |

Two guarded blocks on `sys.columns`, `PRINT` in both branches, `GO` per batch, no transaction — the
conventions in `docs/database/README.md`.

## How the application uses it

`PATCH /api/analyses/{id}/decision` accepts an **optional** `operations` array on
`AnalysisDecisionRequest`, validated only when supplied **and only on an approval** (count ≤ 50;
every `type` in the SGR catalogue; every `amount` > 0; every `currency` either omitted — inheriting
the decision's — or equal to it). A malformed array answers `400` naming the offending element, e.g.
`operations[1].amount`: silently discarding a proposal an analyst typed is the defect this field
exists to fix. An absent or empty array is not an error and stores `NULL`. A rejection or a return
ignores the array outright, exactly as it already ignores the amount and currency beside it —
neither commits anything, and neither has a decision currency to validate an operation against.

`DecideAsync` then writes it on exactly the terms of the amount beside it:

- the **pre-approve** branch sets `ProposedOperationsJson` and leaves the confirmed column alone;
- the **final-record** branch sets `ConfirmedOperationsJson` on an approval (`NULL` on a rejection
  or a return, mirroring `DecisionAmount`) and never touches the proposed column.

Both are exposed as **typed arrays** — `proposedOperations` / `confirmedOperations` on
`AnalysisResultResponse` — by the decision response *and* by `GET /api/analyses/{id}`, because the
detail view is where a RiskManager reads the proposal before confirming. The confirmation's audit
row (`Analysis.Confirmed`) carries both sides in `OldValue`/`NewValue` alongside the amounts, so an
override is legible from `AuditLogs`.

Nothing in this API creates a cupo from the confirmed array: `[dbo].[CreditLineOperations]` rows are
still created through the credit-line routes once the analysis is `Approved`. The Portal reads
`confirmedOperations` and calls them — a separate change in `PortalesHWA/Portal-FinanceIA`.

## Shipping alongside

Nothing else in this release has a schema component. The same code change extracts the SGR
credit-line type catalogue out of `CreditLinesController` into `Clases/CreditLineTypes.cs` so the
decision endpoint and the operations endpoint validate a `type` against one list instead of two
copies — **code-only**, same eighteen codes, code `0` still accepted, and no DDL, exactly as the
`Currencies` extraction needed none.

## Pre-conditions

**Run this script BEFORE deploying the matching code, on every tenant database.** Both columns are
mapped in `AnalysisConfiguration`, and `AnalysisRepository.GetByIdAsync` selects the full entity with
no projection while `UpdateAsync` does a full-row `.Update()`, so publishing first fails **the
decision endpoint, the analysis detail AND the analyses list** with `Invalid column name`. No
application-level `try`/`catch` can protect against it. **Restart the App Service** after deploying
so EF's model cache is rebuilt.

**Apply 058 + 059 + 060 in the same sitting.** None of the four has been executed yet and they are
one feature: 058 adds the two amounts these columns qualify, 059 the limits that decide whether a
decision escalates at all, 060 the `PreApproved` status the escalation writes. 061 alone is harmless
but pointless — with no pre-approval step there is no proposal to keep.

**Portal first**, for the reason 058 already gives; 061 adds nothing new to it. This script's own
API change only *accepts* an optional array and *returns* two new nullable ones, so a Portal
deployed after this API keeps working (it sends none and renders none). It is 058's now-required
`amount`/`currency` and 060's new status value that force the order.

**Deploy order: Portal → DDL 058 + 059 + 060 + 061 on every tenant DB → publish this API → restart
the App Service.** All four are idempotent.

## Rollback

Roll the **application** back first — the deployed code maps both columns, so dropping them under a
running deployment breaks every read and write of an analysis. Export before dropping: the
operations behind a decision exist in no other column, and the *proposal* in particular exists
nowhere else at all.

```sql
SELECT [AnalysisId], [Status],
       [PreApprovedAmount], [PreApprovedCurrency],
       [DecisionAmount],    [DecisionCurrency],
       [ProposedOperationsJson], [ConfirmedOperationsJson]
FROM   [dbo].[Analyses]
WHERE  [ProposedOperationsJson]  IS NOT NULL
   OR  [ConfirmedOperationsJson] IS NOT NULL;
GO

ALTER TABLE [dbo].[Analyses] DROP COLUMN [ConfirmedOperationsJson];
ALTER TABLE [dbo].[Analyses] DROP COLUMN [ProposedOperationsJson];
GO
```

Every decision itself survives — `[Status]`, `[ReviewedBy]`, `[ReviewedAt]`, `[ReviewNotes]` and
both amount pairs are untouched here — so no approval is lost, only the record of which operations
it was made up of. The operations actually **granted** also survive independently as rows in
`[dbo].[CreditLineOperations]`; it is the proposal, and the difference between the two sides, that a
rollback destroys.

## Verification

The script carries six commented checks: both columns and their types, that **nothing was
backfilled** (run before the deploy), that a real two-step decision round-trips with both sides
present and differing, that each element `OPENJSON`s into the seven fields the credit-line routes
expect, that no row ever holds an empty array `'[]'`, and that single-step approvals predating this
release still read correctly and are not to be backfilled.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
