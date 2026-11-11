# 069 — An entity can be a person: legajos, `PersonType`, and CUIT unique per tenant

## What changed and why

FinancIA has assumed every entity is a company (persona jurídica). M10 Phase 1 lets an entity be a
**persona humana**. On create, the API now records whether the CUIT belongs to a company or to a
person, the ARCA fiscal regime and, for a person, the monotributo category. It also links a persona
humana's entity to that person's **legajo**: the person-level record that later phases hang documents
and fiador links from. Documents, extraction and scoring are **not** part of this phase, and nothing
here changes a score.

The schema changes in four places:

| Change | Where |
|---|---|
| A new table: one legajo per person per tenant | `[dbo].[PersonLegajos]` |
| Company or person, the fiscal regime, and the link to the legajo | `[dbo].[Entities]`: `PersonType`, `FiscalRegime`, `PersonLegajoId` |
| Room for a fiador to point at a person (nothing writes it yet) | `[dbo].[Fiadores].[PersonLegajoId]` |
| CUIT unique **per tenant** instead of across the database | `UQ_Entities_CUIT` → `UQ_Entities_TenantId_CUIT` |

### The legajo

`[dbo].[PersonLegajos]`, one row per `(TenantId, CUIT)`:

| Column | Meaning |
|---|---|
| `PersonLegajoId` | surrogate primary key, clustered. The `NEWSEQUENTIALID()` default serves the backfill and hand-written rows; the API supplies its own Guid (the `NotificationConfiguration` precedent) |
| `TenantId` | tenant slug, `NVARCHAR(100)` |
| `CUIT` | the person's CUIT/CUIL, hyphenated `XX-XXXXXXXX-X` exactly as `Entities.CUIT` |
| `FullName` | `"APELLIDO, NOMBRE"`; `NVARCHAR(300)`, the width of `Entities.BusinessName` |
| `FiscalRegime` | `Monotributo` / `ResponsableInscripto` / `Exento` / `Desconocido`; `NOT NULL DEFAULT 'Desconocido'`, with a CHECK |
| `MonotributoCategory` | letter A–K, validated in the API; NULL unless the person is a monotributista |
| `ActivityStartDate` | `DATE` |
| `MaritalStatus` | `NVARCHAR(30)` |
| `PatrimonialRegime` | reserved; nothing writes it this phase |
| `ArcaSnapshotJson` / `ArcaDataUpdatedAt` | the person's ARCA constancia, `ArcaFiscalData` shape |
| `BcraDataJson` / `BcraDataUpdatedAt` | the person's BCRA snapshot, queried by the CUIL |
| `NosisDataJson` / `NosisDataUpdatedAt` | NULL, and they stay NULL this phase |
| `CreatedBy` | `UNIQUEIDENTIFIER NULL`, no FK, the shape of `Entities.CreatedBy` (032). NULL means a script wrote the row (066's rule) |
| `CreatedAt` / `UpdatedAt` | UTC |

The person lives in its own row rather than on the entity because the same person can later be a
fiador: `Fiadores.PersonLegajoId` points at the same row. That column is provisioned here and
**nothing writes it in this phase** (Phase 5 will). Shipping it now keeps that phase a code-only
deploy, which is 067's argument.

### ⚠ PII

A legajo is a **natural person's** record: the CUIL, the full name, the ARCA constancia and the BCRA
Central de Deudores snapshot of that person. It must **never** be serialized wholesale into a Claude
prompt, the rule `Fiador.cs` carries for the same reason, and neither its CUIT nor its name goes to a
log. Nothing builds a prompt from it in this phase. Treat any export of the table, including the one
the rollback asks for, as personal data.

### Why CHECK constraints this time

This script adds four CHECKs, the exception to the folder's usual preference: 036, 046, 052 and 067
deliberately validate an **evolving** vocabulary in the API. These follow `CK_Analyses_Status`
(mirrored by `AnalysisStatuses` in code) and, on this very table, 001's `CK_Entities_MipymeCategory`.

- **`PersonType` is binary and structural.** `CK_Entities_PersonLegajo_Humana` (only a Humana may
  point at a legajo) depends on it, and that rule means nothing if the column can hold a third value.
- **`FiscalRegime` is a closed set** the API derives from ARCA's answer: `Monotributo`,
  `ResponsableInscripto`, `Exento`, or `Desconocido` when the answer does not settle it. The code's
  list is `FiscalRegimes.Known`, and a test pins it to these CHECKs.

The cost is known and accepted: widening either list takes a DDL on every database, exactly as 060
did for `CK_Analyses_Status`. All four are added `WITH CHECK`, following 043 and 060. Every existing
row satisfies them at the moment they are added (the new columns hold `'Juridica'` / NULL / NULL), so
validation cannot fail on legitimate data. `MonotributoCategory` gets **no** CHECK: the category
scale is ARCA's, and the API validates the letter.

### The backfill

Existing entities are classified by CUIT prefix. **20 / 23 / 24 / 27** are natural persons and become
`'Humana'`; every other prefix keeps `'Juridica'`. The script has no ARCA answer to settle an unusual
prefix, and the API also lets an unambiguous prefix win over everything else
(`CuitHelper.ResolvePersonType`). Then, for each Humana:

1. `Entities.FiscalRegime` becomes `'Desconocido'` where it is NULL, which is what the API writes for
   a Humana whose regime is not established.
2. A legajo is created for its `(TenantId, CUIT)` if there is none. `FullName` comes from
   `BusinessName`, and the entity's BCRA snapshot is copied: it was queried by this same CUIT, so it
   is the person's. `CreatedBy` is NULL, per 066's rule (no application user created the row).
3. The entity is linked to it.

The legajo's regime is the **literal** `'Desconocido'` and never `Entities.FiscalRegime`. The API
writes a known regime to a legajo only together with the ARCA answer it came from
(`ArcaSnapshotJson`). The backfill copies no ARCA snapshot, because an old `FiscalDataJson` predates
the regime fields and could not carry one. The next `refresh-arca` fills both. `Entities.UpdatedAt` is
not touched: nobody edited these entities.

### CUIT uniqueness moves to per tenant, and a live 500 goes away

The business database is **shared**, and 001 made `UQ_Entities_CUIT` **global**. So today, when a CUIT
has already been registered by **another** tenant, `POST /api/entities` fails with a **500**. The
create's duplicate pre-check is tenant-scoped, as it should be because it must not reveal another
tenant's portfolio. It finds nothing, and the INSERT then hits the constraint. A same-tenant race (two
creates of the same CUIT at once) ends in the same 500.

After the swap, the first case succeeds. The second becomes a **409**, because the matching code
recognises the index **name** in the provider's error. That makes the names
`UQ_Entities_TenantId_CUIT`, `UX_Entities_PersonLegajoId` and `UX_PersonLegajos_TenantId_CUIT`
load-bearing: do not rename them.

`UQ_Entities_TenantId_CUIT` is a unique **index**, the form and name the EF mapping declares.
Creating it cannot fail while the old key is still in place, because a CUIT that is unique across the
database is unique within each tenant. The old key is dropped as whatever it turns out to be: a
constraint (001 created it as one, which is the real case) or a plain unique index.
`IX_Entities_TenantId` becomes a redundant prefix again, now of this index, just as 045 said it already
was of `IX_Entities_TenantId_CreatedAt`. Dropping it is a separate DBA decision.

### Two transactions, and why the swap is last

The folder's convention is no transactions, because every guarded block is one atomic statement. Two
blocks here are not, so they follow 011, 013 and 068:

- **The backfill is all or nothing.** No reader ever sees a Humana without its legajo halfway through.
- **The swap must never leave a window without uniqueness.** Creating the new index and dropping the
  old key commit together or not at all.

Both run with `SET XACT_ABORT ON`.

The swap first checks that no foreign key references `UQ_Entities_CUIT`. 001 declares none, and 027,
030 and 046 all reference `EntityId`. If one does, the swap rolls back, raises an error and
`RETURN`s. `RAISERROR` does not abort anything under `XACT_ABORT`, hence the explicit `ROLLBACK`. And
`RETURN` ends only the **current** batch, because SSMS and `sqlcmd` carry on with the next one after a
`GO`. Placing the swap last is what makes "stop" mean the end of the script. Nothing before it depends
on it: while the global key stands, CUIT uniqueness is stricter than anything the backfill needs.

**If the swap aborts, the application still works** (sections 1–4 are committed), but a CUIT that
exists in another tenant keeps returning 500 on create until the swap runs.

### ⚠ SET options: filtered indexes

The script's first batch is `SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;`. `001_initial_schema` set
both database defaults OFF. Two of the new indexes are **filtered**: `UX_Entities_PersonLegajoId` (the
first on `Entities`) and `IX_Fiadores_PersonLegajoId`. SQL Server refuses to create a filtered index,
and refuses any INSERT, UPDATE or DELETE on a table that has one, unless both options are ON
(**error 1934**). The backfill writes `Entities` after its filtered index exists.

SSMS and Azure Data Studio connect with both options ON. `sqlcmd` connects with `QUOTED_IDENTIFIER`
OFF unless it is given `-I`. The application is unaffected, because SqlClient connects with both
ON. Hand-written DML is a different matter. From now on, an `UPDATE` on `Entities` from a session
with either option OFF fails with 1934, just as one on `Fiadores` already does.

### Re-run it after the deploy

Re-running is safe, since every block is guarded and the backfill converges. It is also **useful**.
Between this script and the publish, the **old** code keeps creating entities, and it knows nothing of
`PersonType`. Its rows get the default `'Juridica'` even with a 20/23/24/27 prefix, and they get no
legajo. Re-run the script right after the publish and it backfills them.

Do it promptly. Suppose the new code has already refreshed one of those stragglers from ARCA. That
entity then carries a known `FiscalRegime` that its new legajo (`'Desconocido'`) will not match until
the next `refresh-arca`.

One straggler a re-run cannot repair on its own: an old-code `PUT` that changed the CUIT of an entity
the script had already linked leaves that entity pointing at the old CUIT's legajo. The backfill
skips any already-linked row and never links a legajo another entity holds, so such a case does not
fail the transaction. It shows up in post-checks 2 and 3 instead, which say how to fix it by hand.

## DDL

Script location: `docs/database/069_person_legajos_and_entity_person_type.sql`

| Object | Type |
|---|---|
| `[dbo].[PersonLegajos]` | table, 18 columns |
| `PK_PersonLegajos` | primary key, clustered, `(PersonLegajoId)` |
| `DF_PersonLegajos_PersonLegajoId` | default constraint, `DEFAULT (NEWSEQUENTIALID())` |
| `DF_PersonLegajos_FiscalRegime` | default constraint, `DEFAULT ('Desconocido')` |
| `DF_PersonLegajos_CreatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `DF_PersonLegajos_UpdatedAt` | default constraint, `DEFAULT (GETUTCDATE())` |
| `CK_PersonLegajos_FiscalRegime` | check constraint, `FiscalRegime IN (Monotributo, ResponsableInscripto, Exento, Desconocido)` |
| `UX_PersonLegajos_TenantId_CUIT` | unique nonclustered index, `(TenantId, CUIT)` |
| `[dbo].[Entities].[PersonType]` | column, `NVARCHAR(10) NOT NULL` |
| `DF_Entities_PersonType` | default constraint, `DEFAULT ('Juridica')` |
| `CK_Entities_PersonType` | check constraint, `WITH CHECK`, `PersonType IN (Juridica, Humana)` |
| `[dbo].[Entities].[FiscalRegime]` | column, `NVARCHAR(30) NULL` |
| `CK_Entities_FiscalRegime` | check constraint, `WITH CHECK`, NULL or one of the four regimes |
| `[dbo].[Entities].[PersonLegajoId]` | column, `UNIQUEIDENTIFIER NULL` |
| `FK_Entities_PersonLegajos` | foreign key → `[dbo].[PersonLegajos]([PersonLegajoId])`, no cascade |
| `UX_Entities_PersonLegajoId` | unique nonclustered index, `(PersonLegajoId)`, filtered `WHERE PersonLegajoId IS NOT NULL` |
| `CK_Entities_PersonLegajo_Humana` | check constraint, `WITH CHECK`, `PersonLegajoId IS NULL OR PersonType = 'Humana'` |
| `[dbo].[Fiadores].[PersonLegajoId]` | column, `UNIQUEIDENTIFIER NULL` |
| `FK_Fiadores_PersonLegajos` | foreign key → `[dbo].[PersonLegajos]([PersonLegajoId])`, no cascade |
| `IX_Fiadores_PersonLegajoId` | nonclustered index (**not** unique), `(PersonLegajoId)`, filtered `WHERE PersonLegajoId IS NOT NULL` |
| `UQ_Entities_TenantId_CUIT` | unique nonclustered index, `(TenantId, CUIT)`; **replaces** `UQ_Entities_CUIT` |
| `UQ_Entities_CUIT` | unique constraint, `(CUIT)`, from 001; **dropped** |
| (data) | backfill of `Entities.PersonType` / `FiscalRegime` / `PersonLegajoId` and of the matching `PersonLegajos` rows |

The script's structure:

- a first batch with the two `SET`s;
- one guarded block per object on its `sys.*` view, `PRINT` in both branches and `GO` per batch;
- a CHECK, FK or index never shares a batch with the column it refers to;
- the backfill in one transaction;
- the swap, in its own transaction, as the last batch.

It also carries two commented blocks, both read-only: a pre-check at the top to run before, and a
post-check at the end to run after. The index builds are offline (no `WITH (ONLINE = ON)`, unlike
045), so each takes a brief schema lock.

Neither foreign key cascades, per 027, 049 and 067. A legajo is a live subject, and deleting one must
never silently take a credit file's links with it. `IX_Fiadores_PersonLegajoId` is not unique because
one person may guarantee several PyMEs, with one `Fiadores` row for each.

## How the application uses it

- **Mapping.** There is a new `PersonLegajo` entity with `PersonLegajoConfiguration`. `Entity` gains
  `PersonType`, `FiscalRegime` and `PersonLegajoId`, and `Fiador` gains `PersonLegajoId`.
  `EntityConfiguration` models `FK_Entities_PersonLegajos` **without a navigation property**, so that
  EF inserts a new legajo before the entity that points at it in the same `SaveChanges`. Without
  that edge EF orders the INSERTs by table name, and `Entities` sorts before `PersonLegajos`. The
  Fiador FK is deliberately not modelled yet; the phase that first writes that column must add it
  for the same reason.
- **Create, `POST /api/entities`.**
  - The request accepts an optional `personType` (`Juridica` / `Humana`, case-insensitive; anything
    else is a 400).
  - The stored type comes from `CuitHelper.ResolvePersonType`: an unambiguous prefix wins, then the
    request's `personType`, then `Juridica`. Create makes no ARCA call; lookup-arca is where ARCA's
    `tipoPersona` takes part.
  - `FiscalRegime` is the one in `fiscalData` when known, and `Desconocido` otherwise for a Humana. A
    Juridica created without fiscal data keeps NULL.
  - For a Humana, the API finds or creates the legajo for `(tenant, CUIT)`. It merges in only
    informative values, so a known regime is never downgraded to `Desconocido`. It then links the
    legajo and persists **entity and legajo in one `SaveChanges`**
    (`IPersonLegajoRepository.SaveEntityWithLegajoAsync`).
  - The single BCRA call the create already makes is copied onto the legajo in that same
    fault-isolated step. No second BCRA call is made, and there is still no ARCA or NOSIS call on
    create.
- **`POST /api/entities/lookup-arca`** returns `personType`, and `fiscal.fiscalRegime` /
  `fiscal.monotributoCategoria`.
- **`POST /api/entities/{id}/refresh-arca`**
  - It writes the entity's regime and, when the entity is linked, the legajo's regime, category, ARCA
    snapshot and timestamps in the same save.
  - It never changes `PersonType`.
  - An incomplete ARCA answer never overwrites a known regime or category.
- **`POST /api/entities/{id}/refresh-bcra`**: when the entity is linked, it also writes the legajo's
  BCRA pair in the same save.
- **`PUT /api/entities/{id}`** resyncs only when the CUIT changes. It re-derives `PersonType` from an
  unambiguous prefix.
  - A Humana gets the legajo for the new CUIT (created if missing, with nothing copied from the old
    CUIT's snapshots) and is linked to it. The old legajo is kept.
  - A Juridica is unlinked.
  - An unchanged CUIT does no legajo work.
- **Reads.** `GET /api/entities/{id}` returns `personType`, `fiscalRegime`, `monotributoCategory` and
  `personLegajoId`. The category comes from a tenant-scoped, one-column correlated subquery, never
  from the legajo's JSON columns. The list returns `personType` and `fiscalRegime` with no join.
  Every legajo read filters on `TenantId`, since there is no EF global query filter.
- **Per-tenant uniqueness and the 409 race mapping.** `UniqueViolation.Is` matches the constraint
  name in the provider's message, following `AnalysisApprovalVoteRepository.IsDuplicateVote`:
  - `UX_PersonLegajos_TenantId_CUIT`: another request created the legajo first. The create re-reads
    it, merges, links and retries **once**.
  - `UQ_Entities_TenantId_CUIT` **or** `UX_Entities_PersonLegajoId`: SQL Server reports whichever
    index it checks first. The result is a **409** with the pre-check's text. Juridica creates and
    `PUT` races get the same 409 through `EntityRepository`.

  The resulting `DuplicateCuitException` carries **no** inner exception and none of the provider's
  text: SQL Server's "duplicate key value is (…)" would otherwise carry the CUIL into
  `ErrorLogs.StackTrace`.

Nothing writes `Fiadores.PersonLegajoId`, `PatrimonialRegime` or the legajo's NOSIS pair, and no
prompt builder reads `PersonLegajo`.

## Shipping alongside

- **No other DDL.** This script is self-contained. It needs `[dbo].[Entities]` as of 005/023/025
  (`TenantId NVARCHAR`, `BcraDataJson`, `BcraDataUpdatedAt`) and `[dbo].[Fiadores]` from 046, all long
  deployed.
- **Code-only changes in the same release**, with nothing for the DBA to run. The ARCA constancia
  parser becomes tolerant of partial answers:
  - `lookup-arca` answers 200 instead of 404 when ARCA returns data next to an error section.
  - `refresh-arca` answers 200, or 502 "sin datos fiscales" with the snapshot kept, instead of 404.
  - `esSucesion` accepts `SI` / `S`.

  These apply to companies too. The PR lists them.
- **No Portal deploy is required first.** The API gains optional fields, and one new optional request
  field on create. No existing field changes shape and no existing enum gains a value. The Portal can
  send `personType` and render the new fields at its own pace.

## Pre-conditions

- Run the script's **pre-check** first (read-only):
  - the prefix × tenant counts, which show what the backfill will classify;
  - the foreign-key probe on `UQ_Entities_CUIT`, which must return nothing.
- Run it in **SSMS / Azure Data Studio**, or `sqlcmd -I`, on **every tenant/business database**
  **before** publishing the API. Use a quiet window: the index builds are offline.
- **Nothing here is scoped to one tenant.** Unlike 068 there is no `@TenantId`: every statement acts on
  every tenant's rows in the database it runs on, which is correct for a shared database.
- **DDL first is safe for the code still running.** Every new column is nullable or defaulted, so the
  old code's INSERTs satisfy all four CHECKs. The old code keeps working from the moment the script
  commits until the publish.
- **The code first is not.** EF maps every new column and the table, and the list, detail and create
  projections select them, so publishing first fails every read and write of an entity with
  `Invalid column name`.
- **Restart the App Service** after publishing so EF's model cache is rebuilt.

**Deploy order: pre-check → DDL 069 on every tenant/business DB → `--verify-schema` → publish this
API → restart the App Service → re-run 069 (the old code's stragglers) → post-check.**

## Rollback

Roll the **application** back first. The matching code maps every column below and the
`PersonLegajos` table, so dropping them under it fails every read and write of an entity. Run the
rollback with the same `SET` options as the script (`ANSI_NULLS` and `QUOTED_IDENTIFIER` ON). Work in
reverse order, dropping every constraint, index or default **before** the column it sits on.

**(a) CUIT uniqueness back to global: optional, and often impossible.** The old code runs fine on
per-tenant uniqueness, because its pre-check is already tenant-scoped, so the swap can simply stay. The
global key can only come back while no CUIT exists in two tenants, so this must return **nothing**:

```sql
SELECT CUIT, COUNT(*) AS Copies
FROM   [dbo].[Entities]
GROUP  BY CUIT
HAVING COUNT(*) > 1;
```

If it returns rows, each tenant registered that CUIT legitimately. Do not delete one to make room;
leave the swap in place. Otherwise, run the two statements in this order, so uniqueness never lapses:

```sql
ALTER TABLE [dbo].[Entities]
    ADD CONSTRAINT [UQ_Entities_CUIT] UNIQUE NONCLUSTERED ([CUIT] ASC);
DROP INDEX [UQ_Entities_TenantId_CUIT] ON [dbo].[Entities];
GO
```

**(b) Fiadores, then (c) Entities:**

```sql
DROP INDEX [IX_Fiadores_PersonLegajoId] ON [dbo].[Fiadores];
ALTER TABLE [dbo].[Fiadores] DROP CONSTRAINT [FK_Fiadores_PersonLegajos];
ALTER TABLE [dbo].[Fiadores] DROP COLUMN [PersonLegajoId];
GO

ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [CK_Entities_PersonLegajo_Humana];
DROP INDEX [UX_Entities_PersonLegajoId] ON [dbo].[Entities];
ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [FK_Entities_PersonLegajos];
ALTER TABLE [dbo].[Entities] DROP COLUMN [PersonLegajoId];
ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [CK_Entities_FiscalRegime];
ALTER TABLE [dbo].[Entities] DROP COLUMN [FiscalRegime];
ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [CK_Entities_PersonType];
ALTER TABLE [dbo].[Entities] DROP CONSTRAINT [DF_Entities_PersonType];
ALTER TABLE [dbo].[Entities] DROP COLUMN [PersonType];
GO
```

**(d) The legajos, last.** Both foreign keys into the table are gone by this point. **Export first.**
The legajos hold data that exists nowhere else: the ARCA snapshot, regime and category the API wrote
for each person, and the BCRA snapshots refreshed onto them since. ⚠ The export is **personal data**
(a natural person's CUIL, name and credit record), so handle it accordingly.

```sql
SELECT [PersonLegajoId], [TenantId], [CUIT], [FullName], [FiscalRegime],
       [MonotributoCategory], [ActivityStartDate], [MaritalStatus],
       [PatrimonialRegime], [ArcaSnapshotJson], [ArcaDataUpdatedAt],
       [BcraDataJson], [BcraDataUpdatedAt], [NosisDataJson],
       [NosisDataUpdatedAt], [CreatedBy], [CreatedAt], [UpdatedAt]
FROM   [dbo].[PersonLegajos];

DROP TABLE [dbo].[PersonLegajos];
GO
```

**What survives:** every entity, with its own `FiscalDataJson`, `BcraDataJson` and the rest of its
snapshots, none of which this script wrote. **What is lost:** the Humana/Juridica classification, the
entity's regime, the category, and the legajos themselves.

## Verification

`--verify-schema` checks only that the mapped **tables and columns** exist. For 069 that means
`PersonLegajos` with its 18 columns, the three new `Entities` columns and `Fiadores.PersonLegajoId`.
It does not see the swap, the CHECKs, the foreign keys, the filtered indexes or the backfill.

```bash
export FINANCIA_SCHEMA_VERIFY_CONNECTIONS='<business-db conn, one per line>'
export FINANCIA_SCHEMA_VERIFY_DEFAULT_CONNECTION='<Default DB conn>'
dotnet publish Api.Web.FinanceIA/Api.Web.FinanceIA.csproj -c Release -o publish
dotnet publish/Api.Web.FinanceIA.dll --verify-schema; echo "exit=$?"   # 0 ok · 1 missing · 2 inconclusive
# from source instead: dotnet run --project Api.Web.FinanceIA/Api.Web.FinanceIA.csproj -- --verify-schema
```

The script's commented **post-check** is the evidence for everything else:

1. Classification per tenant and prefix. Every 20/23/24/27 row should be `Humana` and every other
   row `Juridica`.
2. Humana rows with no legajo. Expected: **0**.
3. Entities whose legajo disagrees on tenant or CUIT. Expected: **0 rows**. A row here is an entity
   whose CUIT the **old** `PUT` changed after the script had linked it; the old code does not know
   the legajo. Fix it by hand:
   - set its `PersonLegajoId` to NULL, together with `PersonType = 'Juridica'` if the new CUIT starts
     30/33/34;
   - re-run the script.
4. The indexes: `UQ_Entities_TenantId_CUIT`, `UX_Entities_PersonLegajoId` (filtered),
   `IX_Fiadores_PersonLegajoId` (filtered) and `UX_PersonLegajos_TenantId_CUIT` should be present.
   `UQ_Entities_CUIT` should be absent, both as an index and as a key constraint.
5. The four CHECKs and both foreign keys should be enabled and trusted, with `NO_ACTION` on delete.
6. Fiadores with a `PersonLegajoId`. Expected in **this** release: **0**.

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
