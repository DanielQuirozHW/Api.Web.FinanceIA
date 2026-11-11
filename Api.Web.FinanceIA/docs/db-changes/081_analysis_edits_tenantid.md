# 081 — AnalysisEdits for the manual correction of balance figures (Phase 10-F3a)

## What changed and why

An analyst who sees "Cifras inconsistentes" on an analysis had no way to fix a misread figure. Take a
scanned balance where the reading put the bank-debt figure into "Créditos por ventas", a rubro the document
prints as "–". Re-analysing sent the same bytes to the same model, and the re-score (DDL 073) reused the same
extraction.

`POST /api/analyses/{resultId}/balance-corrections` lets the analyst correct figures of the persisted
`AnalysisResults.BalanceJson` and re-scores the analysis over the corrected balance. It uses the same engine,
the same gates and the re-score pipeline of 073, and makes no new extraction. Every corrected figure is
recorded in `[dbo].[AnalysisEdits]`, which is append-only.

That table has existed since `001_initial_schema`. Nothing ever mapped it, and `005` (the TenantId → slug
migration) skipped it, so its `[TenantId]` is still `UNIQUEIDENTIFIER` and cannot hold a Firebase tenant
slug. 081 makes the table usable and adds the index the history read needs:

| Change | Where |
|---|---|
| The table, when it is missing (a database not built from 001) | `CREATE TABLE [dbo].[AnalysisEdits]`: 001's columns, `TenantId NVARCHAR(100)` |
| TenantId as a slug | `ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL`. Only while it is uniqueidentifier, only on an empty table, inside a transaction |
| The 001 index, superseded | `DROP INDEX [IX_AnalysisEdits_TenantId_AnalysisId]` |
| The history read | `IX_AnalysisEdits_TenantId_AnalysisId_EditedAt` (TenantId, AnalysisId, EditedAt DESC) |

The script is idempotent. Every block is guarded on its `sys.*` view and PRINTs in both branches.

### Why the retype refuses a non-empty table

Nothing in this API has ever written `[AnalysisEdits]`, so every client database should report 0 rows (run
pre-check 2). Rows there would come from outside the API, and their GUID tenant does not map to a slug on its
own. In that case the block RAISEs and changes nothing, and the index blocks skip, so the table stays exactly
as it was found. The DBA decides what to do and re-runs the script.

### Why the deploy gate checks a type here

Before 081 every mapped table and column of `[AnalysisEdits]` already exists, so the name-only
`--verify-schema` would pass, and the first correction would then fail on the uniqueidentifier column.
`SchemaExpectation.RequiredColumnTypes` lists exactly this column (`AnalysisEdits.TenantId` → `nvarchar`), and
the gate refuses the publish with `WRONG TYPE` until 081 has run. The list is short on purpose: the check
changed the verdict of no other table.

## The row

| Column | Holds |
|---|---|
| `AnalysisEditId` | BIGINT IDENTITY |
| `AnalysisId` | `Analyses.AnalysisId` of the corrected result |
| `TenantId` | the Firebase tenant slug |
| `EditedBy` | `Users.UserId` of the analyst |
| `FieldPath` | the BalanceJson path, e.g. `current_assets` or `current_assets_items[1].amount` |
| `OldValue` / `NewValue` | the value right before this correction and the corrected one, as invariant-culture numbers (`R` format), or NULL |
| `EditReason` | the analyst's reason (10..1000 characters) |
| `EditedAt` | UTC; the same instant as the re-score's `RescoredAt` |

## What one correction writes, in ONE transaction

The write runs in the REPEATABLE READ transaction of 073 (`AnalysisRescoreRepository.TryApplyAsync`):

1. The decision gate is re-checked: PendingReview, no vote, no decision record.
2. `[AnalysisResults]` is updated, guarded by `RescoreCount`, with the re-score's columns AND `BalanceJson`.
   The corrected blob carries:
   - the corrected figures;
   - a top-level `manual_corrections` array, one `{path, original, corrected, by, at, reason}` entry per
     correction, where `original` is the FIRST original of that path;
   - an `integrity` node re-evaluated by `BalanceIntegrityService` over the corrected figures. It keeps
     `extraction` (the reading's provenance), the unit and the reported confidence, and adds
     `manuallyCorrected: true`.
3. One `[AnalysisRescores]` row. Its `PreviousValuesJson` also carries `"trigger": "balance_correction"` and
   the previous `"balanceJson"`, both omitted on a plain re-score. A correction is therefore reversible by
   hand from that row.
4. One `[AnalysisEdits]` row per corrected figure.

All of it commits, or none of it does. A refusal before the write (400, 409, 429, 503) writes nothing.
`Anomalies` is not rewritten, because it is the original reading; the detail says so in
`balanceCorrections.note`.

## Deploy order

1. The DBA runs `081_analysis_edits_tenantid.sql` in **every** client's Financia DB (`sgr-portales`,
   `movilsgr`, `neuquenpymessgr`), after 080. Pre-check 2 should report 0 rows. Then run the post-checks.
2. Run the deploy workflow from DEVELOP with `ddl_aplicada` ticked and `client = all`. `--verify-schema` fails
   a client whose `AnalysisEdits.TenantId` is still uniqueidentifier.
3. Release the Portal (later). Every API change is additive: the new endpoints, `balanceCorrections` on the
   detail, `trigger` on the re-score history, and the `manual_correction` reliability code under policy
   version 2.

## Rollback

Roll the application back first. Then follow the commented rollback at the end of the script: export the
table, delete its rows (a slug does not fit a uniqueidentifier), retype the column and recreate the 001 index.
The corrected figures stay in `BalanceJson`. To undo a correction, copy the `balanceJson` member of its
`AnalysisRescores.PreviousValuesJson` row (`trigger = balance_correction`) back. To restore the score, use the
other members of the same row.

## PII

`EditReason` is the analyst's free text. It is stored in `[AnalysisEdits]` and in
`BalanceJson.manual_corrections`, and returned only to the tenant's own users (GET .../balance-corrections, the
PDF line). It is never logged, never written to `AuditLogs` (which carries paths and counts only), and never
sent to a prompt. The `manual_correction` reliability phrase carries a count and a date.
