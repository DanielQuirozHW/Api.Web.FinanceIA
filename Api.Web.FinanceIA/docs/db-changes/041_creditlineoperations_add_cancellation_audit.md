# 041 — A cancellation now says who, when, and what it was before

## What changed and why

Script 040 gave the `Aprobada` → `Vigente` transition two audit columns and a response field. The end
of an operation's life got none of that, so it was recorded worse than its beginning: the row said
`Cancelada` and nothing more, and the only trace of who cancelled it and when lived in an `AuditLogs`
entry that named **neither the old nor the new status**.

That last omission is the load-bearing one, because `Cancelada` is reached from two different
business events:

```
Aprobada ──cancel──▶ Cancelada     the deal FELL THROUGH ("se cayó") — granted, never became real
Vigente  ──cancel──▶ Cancelada     the line was CLOSED — it was in force and its life ended
```

Both leave an identical row. An SGR cares a great deal about the difference — one is a lost
origination, the other is a completed one — and until now nothing in the system could tell them
apart after the fact. `CreditLineOperation.Cancelled` recorded the user, the IP and the request id,
and left the transition itself unstated.

This release closes both halves:

- **On the row**: `CanceladaEl` / `CanceladaPor`, the exact mirror of `VigenteConfirmadaEl` /
  `VigenteConfirmadaPor`.
- **In the trail**: the audit entry now carries `oldValueJson` = `{"Status":"Aprobada"}` or
  `{"Status":"Vigente"}` and `newValueJson` = `{"Status":"Cancelada"}`.

The split is deliberate. The row answers *"was this cancelled, by whom, when?"* — questions about the
operation's current state, which belong next to it. The trail answers *"what was it before?"* — a
question about a transition, which belongs where transitions live. `OperationResponse` therefore
gains `canceladaEl` and **not** the cancelling user, following the grain 040 set and #131 before it
("the editor of a rule comes back by name, not by GUID"): a raw user GUID on a read model is not an
answer anyone can use.

### Keeping the audit honest under concurrency

`CancelAsync` guards its conditional `UPDATE` on the **exact** status the caller read
(`Status = @expectedStatus`), not on the weaker `Status <> 'Cancelada'`.

The weaker guard is enough to make the write idempotent, and it is wrong anyway. Consider a
`confirmar-vigencia` that commits between the cancel request's read and its write:

1. The controller reads the operation: `Aprobada`.
2. Another analyst confirms it. The row is now `Vigente`.
3. Under `Status <> 'Cancelada'`, the cancel still matches. The row becomes `Cancelada`.
4. The audit entry — built from the stale read — records `Aprobada` → `Cancelada`.

The trail now says the deal fell through when it was in fact closed. That is precisely the
distinction this release exists to create, failing at the only moment it is interesting. With the
exact-status guard, step 3 matches **zero rows**; the controller re-reads, discovers the row is now
`Vigente`, and retries once against that status — so the entry it writes is the one that actually
happened.

The retry is bounded to a single attempt. A second loss means another writer is already on the row,
and the endpoint returns `204` without inventing an audit entry for a write it did not make. The
`204` / `404` contract is unchanged: no `409` was added to the `DELETE`. Cancellation is idempotent
in a way confirmation is not — "someone else already cancelled it" is success, whereas "someone else
already confirmed it" is a conflict, because re-confirming would overwrite a real timestamp.

`ExecuteUpdateAsync` is used deliberately here, with the confirmation `ef-core-patterns.md` requires:
status and both audit columns must land in one statement, or a row is briefly `Cancelada` with no
record of who cancelled it. The same reasoning applied to `ConfirmVigenciaAsync` in 040, where it went
unrecorded. Note the consequence: `ExecuteUpdateAsync` bypasses the change tracker and does not
refresh tracked entities. Every read in this repository is `AsNoTracking`, so nothing goes stale
in-process.

## DDL

Script location: `docs/database/041_creditlineoperations_add_cancellation_audit.sql`

| Object | Type |
|---|---|
| `[dbo].[CreditLineOperations].[CanceladaEl]` | column, `DATETIME2 NULL` |
| `[dbo].[CreditLineOperations].[CanceladaPor]` | column, `UNIQUEIDENTIFIER NULL` |

`CanceladaPor` is a `UNIQUEIDENTIFIER`, matching `[CreatedByUserId]` and `[VigenteConfirmadaPor]` on
this same table — user ids on the credit-line tables are GUIDs referencing `[dbo].[Users]`, not
strings. No FK is declared, for the same reason those two have none: a deleted user must not make an
operation's history unreadable.

**Both columns are NULL, with no default and no backfill.** Operations cancelled before this script
ran keep their `Cancelada` status and get NULL audit values on purpose — we do not know who cancelled
them, or when, and NULL is the honest answer rather than a plausible-looking guess. **Treat these
columns as meaningful only for operations cancelled after this script ran.**

**No CHECK constraint and no change to the `Status` default.** 040 already moved that default to
`'Aprobada'` and explained why the vocabulary carries no CHECK (it is validated in the API — see
029/036). `Cancelada` is not a new value and needs neither. **No index either**: both columns are read
as attributes of an operation already located by its primary key or by the existing
`TenantId`/`CreditLineId` index — nothing searches *by* cancellation date.

### What this supersedes

`docs/db-changes/040_creditlineoperations_add_vigencia_audit.md` stated that cancellation gets **no**
equivalent row-level columns, on the grounds that the close path already writes a full `IAuditService`
entry and that row columns for one transition and not the others would duplicate what `AuditLogs`
holds. That reading held only as long as nobody needed to tell the two cancellation paths apart, or
to sort and filter by cancellation date without joining to the audit table. Both turned out to be
needed, and the duplication argument cuts the other way once you notice that the audit entry was
recording *less* than the confirm path, not the same.

040's paragraph is left as written, with a pointer to this document appended — the same treatment 040
gave script 030's `CREATE TABLE` comment, for the same reason: superseding a claim is not the same as
pretending it was never made.

## Pre-conditions

- **Must be executed BEFORE deploying the matching commit**, on **every** tenant database. Both
  columns are mapped in `CreditLineOperationConfiguration`, so EF Core emits them in every `SELECT`
  and `INSERT` against `CreditLineOperations`. Against a database without them, operation creation
  **and** retrieval fail with "Invalid column name", and no application-level try/catch can protect
  that. Both columns being nullable makes this less severe than 039 — nothing needs backfilling — but
  it does not make it optional.
- **The Portal does NOT need to be deployed first.** This differs from 040, which introduced a new
  `Status` value the Portal's renderer had no branch for. 041 introduces no new status value: the
  Portal gains one optional read field (`canceladaEl`, null unless cancelled) and can ignore it
  entirely. DDL → this API, in either order relative to the Portal.
- **Restart the App Service after deploying**, so EF's model cache is rebuilt.
- The script is idempotent — both columns are guarded, so it is safe to run multiple times.
  Re-running never resets a value already written.

## Rollback

Reverse order. Both columns are new and read only by this feature, so neither drop loses data outside
it.

```sql
ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [CanceladaPor];
GO
ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [CanceladaEl];
GO
```

Roll the application back **first**: the deployed code maps both columns, so dropping them under a
running deployment breaks operation reads and writes.

Unlike 040, rolling this schema back loses nothing that the previous code cannot express. No new
status value was introduced, so there are no rows the rolled-back code would misread — a `Cancelada`
operation reads as `Cancelada` either way. The only loss is the who/when of cancellations performed
while the release was live, and those remain recoverable from `AuditLogs` under
`CreditLineOperation.Cancelled`, which is where they were recorded before 041 and still are.

## Verification

After applying the script, all three queries must return what is shown.

```sql
-- 1. Both columns exist, with the expected types → 2 rows
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'CreditLineOperations'
  AND  COLUMN_NAME IN ('CanceladaEl', 'CanceladaPor')
ORDER  BY COLUMN_NAME;
-- expected: CanceladaEl  datetime2        YES
--           CanceladaPor uniqueidentifier YES

-- 2. No existing row was rewritten — every operation cancelled before this
--    script has NULL audit values, and nothing else moved
SELECT [Status], COUNT(*) AS Rows,
       SUM(CASE WHEN [CanceladaEl] IS NULL THEN 1 ELSE 0 END) AS NullCanceladaEl
FROM   [dbo].[CreditLineOperations]
GROUP  BY [Status];
-- expected immediately after the script: NullCanceladaEl equal to Rows for
--           every status, including 'Cancelada'

-- 3. After cancelling one operation through the API, the stamp is populated,
--    and the audit entry says which transition it was → 1 row each
SELECT TOP (1) [OperationId], [Status], [CanceladaEl], [CanceladaPor]
FROM   [dbo].[CreditLineOperations]
WHERE  [CanceladaEl] IS NOT NULL
ORDER  BY [CanceladaEl] DESC;
-- expected: Status = 'Cancelada', both audit columns non-NULL

SELECT TOP (1) [Action], [EntityId], [OldValueJson], [NewValueJson]
FROM   [dbo].[AuditLogs]
WHERE  [Action] = N'CreditLineOperation.Cancelled'
ORDER  BY [CreatedAt] DESC;
-- expected: OldValueJson = {"Status":"Aprobada"}  ← se cayó
--                       or {"Status":"Vigente"}   ← close
--           NewValueJson = {"Status":"Cancelada"}
```

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
