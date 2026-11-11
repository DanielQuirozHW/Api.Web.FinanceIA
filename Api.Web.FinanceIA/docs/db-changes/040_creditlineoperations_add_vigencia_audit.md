# 040 — An operation is no longer born already in force

## What changed and why

Operations were created with `Status = 'Vigente'`, which collapsed two distinct business states into
one. The real lifecycle has an earlier step: an approved, granted line is **`Aprobada`** — committed
on paper — and becomes **`Vigente`** only when an analyst confirms it is actually in force. In
between, it can fall through.

```
(create) ─▶ Aprobada ──confirmar-vigencia──▶ Vigente ──(ValidUntil<hoy, derived)──▶ Vencida
                │                                │
                └────cancel (se cayó)──▶ Cancelada ◀────cancel (close)
```

The vocabulary is now:

- **`Aprobada`** — granted and committed, not yet confirmed in force. **Reserves cupo from creation.**
- **`Vigente`** — an analyst confirmed it is in force. The two columns added here record who and when.
- **`Cancelada`** — closed, or it fell through.
- **`Vencida`** — derived at read time from a **`Vigente`** operation past `ValidUntil`. Never stored.
  An `Aprobada` operation never derives to it: *`Vencida` is a statement about a line that was once in
  force*, so an approval nobody ever confirmed cannot expire into it. It reads as a stale `Aprobada`.

This supersedes the `-- Vigente | Cancelada ONLY` comment in script 030's `CREATE TABLE`. That
comment is deliberately left as written — numbered scripts are an executed historical record — so
script 040's header is the current vocabulary.

### The load-bearing correctness change

**An `Aprobada` operation reserves the cupo.** `Utilizado`, `Disponible` and `OperationsCount` now
count every operation whose stored status is not `Cancelada`, instead of only those stored `Vigente`.

This is not cosmetic. The predicate was stated three separate times in `CreditLinesController` (the
raise underwater check, the create margin check, and the response mapper) and is now a single
`OccupiesCupo` helper, because those three had to agree and nothing forced them to.

More importantly, it makes the cupo agree with the **analysis-rejection guard**, which asks the same
question — "does this analysis still justify live exposure?" — but answered it with an *exclusion*
predicate (`HasNonCancelledByAnalysisAsync`, stored status `!= 'Cancelada'`). Consumption used an
*inclusion* predicate (`== 'Vigente'`). Introducing `Aprobada` under the old inclusion predicate
would have produced an operation that **blocks an analysis rejection while reserving no cupo** — live
enough to freeze the analysis, invisible enough to let a second operation push the cupo over its
tope. Both sides are now the same predicate.

Consequence worth stating plainly: **confirming vigencia does not move `Utilizado`.** The amount was
already counted the moment the operation was created. The confirmation records a fact about the
world; it does not grant anything further.

### Audit columns

`VigenteConfirmadaEl` and `VigenteConfirmadaPor` record who declared the operation in force and when.
The confirming user is deliberately **not** surfaced on `OperationResponse` — only the timestamp is.
Returning a raw user GUID runs against the grain set in #131 ("the editor of a rule comes back by
name, not by GUID"), and the confirmer is already recorded in the `AuditLogs` trail under
`CreditLineOperation.VigenciaConfirmed`.

Cancellation gets **no** equivalent row-level columns. It is not missing an audit trail — the close
path already writes a full `IAuditService` entry with user, IP and request id. Adding row columns for
one transition and not the others would duplicate what `AuditLogs` already holds.

**Superseded by 041** — see `docs/db-changes/041_creditlineoperations_add_cancellation_audit.md`. The
duplication argument overlooked that the cancellation entry recorded *less* than the confirm path: it
named neither the old nor the new status, so an `Aprobada` → `Cancelada` ("se cayó") could not be told
from a `Vigente` → `Cancelada` (a close). 041 adds `CanceladaEl` / `CanceladaPor` and puts both ends
of the transition in the audit entry. This paragraph is left as written.

## DDL

Script location: `docs/database/040_creditlineoperations_add_vigencia_audit.sql`

| Object | Type |
|---|---|
| `[dbo].[CreditLineOperations].[VigenteConfirmadaEl]` | column, `DATETIME2 NULL` |
| `[dbo].[CreditLineOperations].[VigenteConfirmadaPor]` | column, `UNIQUEIDENTIFIER NULL` |
| `DF_CreditLineOperations_Status` | default constraint — dropped and recreated as `N'Aprobada'` |

`VigenteConfirmadaPor` is a `UNIQUEIDENTIFIER`, matching `[CreatedByUserId]` on this table and on
`[dbo].[CreditLines]` — user ids on the credit-line tables are GUIDs referencing `[dbo].[Users]`,
not strings. No FK is declared, for the same reason `CreatedByUserId` has none: a deleted user must
not make an operation's history unreadable.

**Both columns are NULL, with no default and no backfill.** Existing operations keep their `Vigente`
status and get NULL audit values on purpose — we do not know who made them vigente, or when, and
NULL is the honest answer rather than a plausible-looking guess. **Treat these columns as meaningful
only for operations confirmed after this script ran.**

**No CHECK constraint** is added on `Status`. Script 030 deliberately creates none (the vocabulary is
validated in the API, as with `CreditLines.Status` — see 029/036), so `Aprobada` needs no constraint
change, and `NVARCHAR(20)` already accommodates it. This differs from 039, where the constraint was
added because that column decides whether a score can be trusted.

### Why the default constraint is flipped

The flip is **inert for the application**: EF Core always writes `Status` explicitly, so the default
never fires from the API. It is not inert as documentation. Leaving `DEFAULT 'Vigente'` means a
manual or DBA `INSERT` silently creates an operation that is already in force and that no analyst
ever confirmed — precisely the failure this release exists to prevent. Existing rows are untouched: a
`DEFAULT` applies to future `INSERT`s that omit the column, never retroactively, so no `Vigente`
operation becomes `Aprobada`.

## Pre-conditions

- **Must be executed BEFORE deploying the matching commit**, on **every** tenant database. Both
  columns are mapped in `CreditLineOperationConfiguration`, so EF Core emits them in every `SELECT`
  and `INSERT` against `CreditLineOperations`. Against a database without them, operation creation
  **and** retrieval fail with "Invalid column name", and no application-level try/catch can protect
  that. Both columns being nullable makes this less severe than 039 — nothing needs backfilling — but
  it does not make it optional.
- **The Portal must be deployed first.** Its operation-status renderer is a `switch` with no default
  on the pre-release build, so the first `Aprobada` this API emits crashes the credit-line card.
  Portal → this DDL → this API.
- **Restart the App Service after deploying**, so EF's model cache is rebuilt.
- The script is idempotent — both columns and both halves of the default-constraint swap are guarded,
  so it is safe to run multiple times. Re-running never resets a value already written.

## Rollback

Reverse order. Both columns are new and read only by this feature, so neither drop loses data outside
it. The default constraint returns to its pre-040 value.

```sql
ALTER TABLE [dbo].[CreditLineOperations] DROP CONSTRAINT [DF_CreditLineOperations_Status];
GO
ALTER TABLE [dbo].[CreditLineOperations]
    ADD CONSTRAINT [DF_CreditLineOperations_Status] DEFAULT ('Vigente') FOR [Status];
GO
ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [VigenteConfirmadaPor];
GO
ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [VigenteConfirmadaEl];
GO
```

Roll the application back **first**: the deployed code maps both columns, so dropping them under a
running deployment breaks operation reads and writes.

⚠ Rolling the schema back does **not** undo the data. Any operation created while the release was
live is stored `Aprobada`, and the rolled-back code has no branch for that value: `EffectiveStatus`
falls through to `Vigente`, so those operations would silently read as in force. If a rollback is
needed after operations have been created, decide deliberately what to do with the `Aprobada` rows —
promoting them to `Vigente` is the only status the old code understands.

```sql
-- Only if rolling back with Aprobada rows in flight. Read the paragraph above first.
-- UPDATE [dbo].[CreditLineOperations] SET [Status] = N'Vigente' WHERE [Status] = N'Aprobada';
```

## Verification

After applying the script, all four queries must return what is shown.

```sql
-- 1. Both columns exist, with the expected types → 2 rows
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'CreditLineOperations'
  AND  COLUMN_NAME IN ('VigenteConfirmadaEl', 'VigenteConfirmadaPor')
ORDER  BY COLUMN_NAME;
-- expected: VigenteConfirmadaEl  datetime2        YES
--           VigenteConfirmadaPor uniqueidentifier YES

-- 2. The Status default now reads 'Aprobada' → 1 row
SELECT dc.name, dc.definition
FROM   sys.default_constraints dc
WHERE  dc.parent_object_id = OBJECT_ID(N'[dbo].[CreditLineOperations]')
  AND  dc.name             = N'DF_CreditLineOperations_Status';
-- expected: DF_CreditLineOperations_Status   (N'Aprobada')

-- 3. No existing row was rewritten — history keeps its own statuses, and every
--    pre-040 row has NULL audit values
SELECT [Status], COUNT(*) AS Rows,
       SUM(CASE WHEN [VigenteConfirmadaEl] IS NULL THEN 1 ELSE 0 END) AS NullConfirmadaEl
FROM   [dbo].[CreditLineOperations]
GROUP  BY [Status];
-- expected immediately after the script: no 'Aprobada' rows yet, and
--           NullConfirmadaEl equal to Rows for every status

-- 4. After confirming one operation through the API, the stamp is populated → 1 row
SELECT TOP (1) [OperationId], [Status], [VigenteConfirmadaEl], [VigenteConfirmadaPor]
FROM   [dbo].[CreditLineOperations]
WHERE  [VigenteConfirmadaEl] IS NOT NULL
ORDER  BY [VigenteConfirmadaEl] DESC;
-- expected: Status = 'Vigente', both audit columns non-NULL
```

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
