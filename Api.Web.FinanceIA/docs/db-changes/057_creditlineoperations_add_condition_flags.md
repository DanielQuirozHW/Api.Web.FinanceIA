# 057 — Two conditions an operation carries, recorded as fields instead of prose

## What changed and why

An SGR operation can be granted **a sola firma**, and it can require a **contragarantía real**. Both
are underwriting conditions of the granted line, both are decided when the operation is created, and
until now neither had anywhere to live. The analyst either dropped them into the free-text `[Notes]`
alongside everything else, or did not record them at all — which means today there is no way to ask
"which of our live operations are a sola firma?" without reading a thousand notes by hand.

This script adds the two columns. That is the whole change:

| Column | Meaning |
|---|---|
| `ASolaFirma` | the line was granted a sola firma |
| `RequiereContragarantiaReal` | the line requires a contragarantía real |

They are **independent**. An operation may carry both, either or neither, and neither implies
anything about the other — a sola firma is a statement about who signs, a contragarantía real is a
statement about what backs it. There is no constraint tying them together because there is no rule
tying them together.

### Per OPERATION, not per cupo and not per analysis

This is the load-bearing modelling decision, and it is the one that would be expensive to get wrong.
Two operations under the **same cupo**, drawn from the **same approved analysis**, routinely differ
on both conditions: the cupo is a tope, the analysis is the justification for that tope, and neither
is the thing that was granted. The operation is. Putting either flag on `[CreditLines]` would force
every operation under a cupo to share one answer, and putting it on `[Analyses]` would freeze the
answer at scoring time, before the operations that need it exist.

### The flags are metadata — this is a promise, not an omission

They participate in **no rule**:

- **Cupo consumption is untouched.** `OccupiesCupo` is still "stored status is not `'Cancelada'`",
  so a flagged operation reserves saldo exactly like an unflagged one. `Utilizado`, `Disponible` and
  `OperationsCount` do not look at them, and neither does the create-time sum rule or the raise
  underwater check.
- **The lifecycle is untouched.** `'Aprobada'` → `'Vigente'` → `'Cancelada'` is unchanged, no status
  is derived from either flag, and `EffectiveStatus` never reads them.
- **Nothing validates them.** All four combinations are legal. A `BIT` is already a two-value domain,
  so there is nothing left for a `CHECK` constraint to say.

That promise is what makes it safe to backfill every existing row to 0 without knowing anything about
those rows. `CreditLinesControllerOperationsTests.Flags_DoNotAffectCupoConsumptionOrTheStatusLifecycle`
pins it: a flagged operation still consumes its amount against the sum rule, and a second operation
that would exceed the tope is still refused. **If a later release wants a flag to affect the maths,
that is a new script with its own argument — do not quietly wire it in here.**

### Why NOT NULL DEFAULT 0, when 040 and 041 chose nullable

The two audit pairs added next door on this same table (`VigenteConfirmadaEl/Por`,
`CanceladaEl/Por`) are deliberately nullable with no backfill, and their rationale notes are emphatic
that NULL is "the honest answer rather than a plausible-looking guess". This script does the
opposite, and the difference is real rather than a drift in taste:

- Those columns record an **event**. An operation either was confirmed in force at some instant by
  some person, or it was not — and for rows written before 040, the event happened and we genuinely
  do not know when. NULL is the only truthful value.
- These record a **condition** of the granted line, and every operation has an answer. An operation
  that was not granted a sola firma simply **was not**. That is `false`, not unknown.

So `0` is a true statement about every pre-existing row, the `DEFAULT` backfills them inside the
`ALTER`, and there is no data migration and no forward-only gap. (SQL Server also requires a
`DEFAULT` to add a `NOT NULL` column to a populated table at all, so the constraint is not optional
once NOT NULL is chosen — but it is the NOT NULL that was chosen first, for the reason above.)

The default constraints are **named** (`DF_CreditLineOperations_ASolaFirma`,
`DF_CreditLineOperations_RequiereContragarantiaReal`), like every other default on this table, so a
later script can address them rather than hunting a generated name.

### Why the names are Spanish

`[dbo].[CreditLineOperations]` already mixes English structural names (`[Status]`, `[Amount]`,
`[ValidUntil]`, `[CreatedByUserId]`) with Spanish business names (`[VigenteConfirmadaEl]`,
`[VigenteConfirmadaPor]`, `[CanceladaEl]`, `[CanceladaPor]`, from 040 and 041). "A sola firma" and
"contragarantía real" are Argentine SGR terms of art with no faithful English equivalent — "sole
signature" and "real collateral" are both lossy — so they stay in the vocabulary the analysts
actually use, following the precedent this table already set.

Unaccented, as **every** identifier in this schema and codebase is: `RequiereContragarantiaReal`, not
`RequiereContragarantíaReal`.

## DDL

Script location: `docs/database/057_creditlineoperations_add_condition_flags.sql`

| Object | Type |
|---|---|
| `[dbo].[CreditLineOperations].[ASolaFirma]` | column, `BIT NOT NULL` |
| `DF_CreditLineOperations_ASolaFirma` | default constraint, `DEFAULT (0)` |
| `[dbo].[CreditLineOperations].[RequiereContragarantiaReal]` | column, `BIT NOT NULL` |
| `DF_CreditLineOperations_RequiereContragarantiaReal` | default constraint, `DEFAULT (0)` |

Two guarded blocks on `sys.columns`, `PRINT` in both branches, `GO` per batch, no transaction — the
conventions in `docs/database/README.md`.

**No index.** Neither column is a predicate anywhere: both are read as attributes of an operation
already located by its primary key or by `IX_CreditLineOperations_TenantId_CreditLineId`. A `BIT` is
also close to useless as a leading index key. If a "list every a-sola-firma operation" screen ever
arrives, it will want a filtered index designed for that query, not a speculative one added now.

## How the application uses it

- `CreditLineOperation.ASolaFirma` / `.RequiereContragarantiaReal` (`bool`) are mapped in
  `CreditLineOperationConfiguration` as `.HasColumnName(...).IsRequired()`. The DB default is
  deliberately **not** declared in the Fluent config, exactly as `[Status]`'s is not: EF then always
  writes the value the application decided, and the column default only ever serves a hand-written
  `INSERT`.
- `CreateOperationRequest.ASolaFirma` / `.RequiereContragarantiaReal` are optional plain `bool`s —
  no `[Required]`, matching `Type` next to them, and `[Required]` on a `bool` would be meaningless
  anyway. An omitted field is `false`.
- `CreditLinesController.CreateOperationAsync` persists both verbatim; `MapOperation` echoes both on
  `OperationResponse`. No other endpoint writes them — there is no "edit operation" path in this
  release, so a flag is set at creation or not at all.

## Shipping alongside: ownership on credit-line mutations (§5.2)

The same PR carries a **code-only** change with no schema component, noted here so the DBA is not
surprised by a release that touches this table for two reasons at once.

Credit-line and operation mutations are now gated by ownership: `RaiseCreditLineAsync`,
`SuspendCreditLineAsync`, `ReactivateCreditLineAsync`, `ConfirmOperationVigenciaAsync` and
`CancelOperationAsync` require the caller to be the row's creator **or** an Admin, and answer **403**
otherwise. `[CreatedByUserId]` was already stored on both tables since script 030 and was simply
never read back; nothing about it changes now, and **no column is added, altered or backfilled for
this half**.

Two consequences worth knowing:

- The check lives in the controller, on the row each mutator already loads, **not** in the
  conditional `UPDATE` the way the status guard does. `[CreatedByUserId]` is written once at creation
  and never updated, so unlike `[Status]` it cannot change between the read and the write — there is
  no race for an atomic predicate to close. Folding it in would also have made the repository's
  `false` mean "wrong status **or** not the owner", which would have degraded a non-owner's cancel
  into a `204`: success, reported for a write that never happened.
- `OperationResponse` and `CreditLineResponse` now carry `createdByUserId` and `createdByName`, so
  the Portal can hide an affordance instead of discovering the 403. Names are resolved in one batched
  `IUserRepository.GetByIdsAsync` per response — the structure read resolves every cupo and every
  operation author in a single query — falling back to `"Usuario"` for an id that no longer resolves.

## Pre-conditions

- **Must be executed BEFORE deploying the matching commit**, on **every** tenant database. Both
  columns are mapped in `CreditLineOperationConfiguration`, and `CreditLineOperationRepository` reads
  and inserts the **full entity** with no projection (`GetByCreditLineAsync` and `GetByIdAsync` are
  plain entity reads, `CreateAsync` adds the whole entity). EF Core therefore names both columns in
  every `SELECT` and every `INSERT` against `[dbo].[CreditLineOperations]`.

  Against a database without them, **operation creation, confirmar-vigencia, cancellation and the
  entire `GET /api/credit-lines` structure read** fail with `Invalid column name`, and no
  application-level `try`/`catch` can protect against it. This is the same deploy-order constraint as
  040 and 041, on the same table, for the same reason. The columns being NOT NULL does not make it
  worse than those: the `DEFAULT` handles existing rows in the `ALTER`, so there is still nothing to
  backfill by hand.
- **Restart the App Service after deploying**, so EF's model cache is rebuilt.
- **No Portal deploy is required first**, unlike 040. That script introduced a new `[Status]` value
  the Portal's renderer had no branch for; this one introduces no enum value and changes no existing
  field's shape. The operation payload gains two additive booleans plus the two authorship fields
  from §5.2, and a Portal that does not know them ignores them.

  The Portal **will** want a follow-up, though, and it is worth telling that team directly: the five
  gated endpoints can now answer **403** where they previously answered 200. That is the reason
  `createdByUserId` is on the payload — the front should compare it against the logged-in user and
  not offer the button, rather than letting the analyst click it and read an error.
- The script is idempotent — both columns are guarded — so it is safe to run more than once.
  Re-running never rewrites a flag already recorded.

Deploy order: **DDL 057 on every tenant DB → publish this API → restart the App Service**.

## Rollback

Reverse order. Roll the **application** back first: the deployed code maps both columns, so dropping
them under a running deployment produces exactly the failure described above, on every operation
read and write.

```sql
ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [RequiereContragarantiaReal];
GO
ALTER TABLE [dbo].[CreditLineOperations] DROP COLUMN [ASolaFirma];
GO
```

No separate constraint drop: `DROP COLUMN` removes the column's own default constraint with it.

What is lost is **the two conditions on any operation created while the release was live**. They
exist in no other column and in no audit row — the creation audit entry records that the operation
was created, not with what conditions — so export them first if that still matters:

```sql
SELECT [OperationId], [ASolaFirma], [RequiereContragarantiaReal]
FROM   [dbo].[CreditLineOperations]
WHERE  [ASolaFirma] = 1 OR [RequiereContragarantiaReal] = 1;
```

Unlike 040, **rolling back loses no correctness**. Because the flags feed no rule, dropping them
changes no cupo figure and no operation status: there is no equivalent of 040's `Aprobada` rows
silently reading as in force under rolled-back code. The §5.2 half rolls back with the application
alone, since it has no schema component.

## Verification

The script carries the full block; the four checks are, in order: both columns exist as `bit`,
`NOT NULL`, each with its `((0))` default; every pre-existing row reads `0 / 0` **immediately after
the ALTER and before the deploy**; one operation created through the API with both conditions set
round-trips as `1 / 1`; and `Utilizado` per cupo is still the sum of its non-`'Cancelada'`
operations regardless of the flags — the query that proves the metadata promise held in production.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
