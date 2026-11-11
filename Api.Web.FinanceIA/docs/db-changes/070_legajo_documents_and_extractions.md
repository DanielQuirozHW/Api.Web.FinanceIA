# 070 — Legajo documents, consent, and one row per model run

## What changed and why

M10 Phase 2a lets a persona humana's **legajo** (DDL 069) receive typed PDFs. Each upload is stored
like any other document and measured page by page for text quality, locally, with no model call.
When the legajo has a recorded **data-processing authorization**, the document is queued for a
cheap classification pass in a background worker. That pass reads the document type, the period,
the holder CUIT, the engagement type, and whether the PDF looks like several documents. C# then
compares the holder CUIT with the legajo's and **blocks** a mismatch.

Phase 2b (extraction, arithmetic checks, analyst review) needs more schema. This script creates all
of it now, so 2b ships without DDL.

| Change | Where |
|---|---|
| Ten legajo document types | `CK_Documents_DocumentType` widened |
| `'Mixed'` (some pages have text, some do not) | `CK_Documents_PdfType` widened |
| The document's legajo, reading and pipeline state | `[dbo].[Documents]`: `PersonLegajoId`, `PeriodEnd`, `EngagementType`, `SupersedesDocumentId`, `HolderCuit`, `LegajoStatus`, `LegajoStatusUpdatedAt`, `PageQualityJson` |
| The person's authorization | `[dbo].[PersonLegajos]`: `ConsentRecordedAt`, `ConsentDocumentId`, `ConsentRecordedBy` |
| One immutable row per model run | new `[dbo].[LegajoExtractions]` |
| Analyst corrections (2b) | new `[dbo].[LegajoCorrections]` |
| Cost rows tied to a document | `[dbo].[ApiCallLogs].[DocumentId]` |
| The legajo operations admitted by the out-of-band CHECK | `CK_ApiCallLogs_Operation` widened, where one exists |

## Deploy ordering

**RUN BEFORE DEPLOYING, once on Financia-db**, then restart the App Service. EF maps every new
column and both tables. Publishing first breaks every read and write of `[dbo].[Documents]`,
`[dbo].[PersonLegajos]` and `[dbo].[ApiCallLogs]` with `Invalid column name`. The deploy job's
`--verify-schema` step refuses the publish until the columns exist.

Running the DDL first is safe for the code still running:

- every new column is nullable;
- the widened CHECKs are supersets of the old ones;
- `CK_Documents_LegajoStatus_Owner` holds for every row the old code writes, because it writes
  `LegajoStatus = NULL`.

No Portal-first step is needed: the Portal gains new fields and endpoints, and every existing
response only grows.

### Execution history

- **Financia-db, 2026-09-29.** Sections 1–6 applied. Section 7, then a `RAISERROR` guard, stopped
  the run: the database carries an out-of-band `CK_ApiCallLogs_Operation` that did not admit the
  legajo operations (see below). The DBA recreated it by hand with exactly the list the current
  section 7 writes, with the same drop loop, `WITH CHECK`, in one transaction. **No further DDL is
  needed on Financia-db.** Re-running the current 070 there only rewrites that CHECK with the same
  list.
- Any other business database gets the widening from 070 itself.

## The CHECK widenings

T-SQL has no way to alter a CHECK's expression in place, so each one is dropped and recreated, as
060 did. That applies to section 1 (`[Documents]`) and section 7 (`[ApiCallLogs].[Operation]`).
This script does **not** rely on the constraint's name to find it. It drops:

- every check constraint **bound to the column** (`sys.check_constraints.parent_column_id` = the
  `DocumentType` / `PdfType` / `Operation` column), whatever its name or definition;
- any constraint holding the canonical name.

It then recreates the rule under the canonical name. **Table-level** checks
(`parent_column_id = 0`) are never touched, so the two-column `CK_Documents_LegajoStatus_Owner`
added later in the same script survives a re-run.

Drop and recreate share one transaction under `XACT_ABORT`. If `WITH CHECK` finds a row outside the
new list, the drop rolls back and the old rule stays in force. Pre-check 1 finds such a row before
you start.

## New columns on `[dbo].[Documents]`

Every column is NULL for a company document: every row that predates 070, and every row the legacy
upload and the LUFE import write.

| Column | Meaning |
|---|---|
| `PersonLegajoId` | the legajo the document was uploaded into. `FK_Documents_PersonLegajos`, NO ACTION; filtered `IX_Documents_TenantId_PersonLegajoId`; filtered unique `UX_Documents_Legajo_Hash` (see below) |
| `PeriodEnd` | `DATE`, the period close or measurement date the classification read |
| `EngagementType` | `Certificacion` / `Compilacion` / `Ninguno`, with a CHECK |
| `SupersedesDocumentId` | rectificativas. Self FK `FK_Documents_SupersedesDocument`; **nothing sets it in 2a** |
| `HolderCuit` | `NVARCHAR(13)`, `XX-XXXXXXXX-X`, what the classification read. ⚠ PII, and a **third party's** when the document is `Blocked`. Never returned by the API, which exposes only `holderCuitMatches` |
| `LegajoStatus` | `Uploaded`, `Queued`, `Classifying`, `Blocked`, `ReadyForExtraction`, `Extracting`, `NeedsReview`, `Ready`, `Failed`. `CK_Documents_LegajoStatus_Owner`: only a legajo document has one |
| `LegajoStatusUpdatedAt` | when the status last moved. The worker's stale requeue reads it |
| `PageQualityJson` | `[{page, chars, digits, suspiciousRatio, verdict}]`. Figures only, never page text |

`ProcessingStatus` is untouched. It belongs to the balance pipeline, and a legajo document keeps its
default `'Pending'` there. `ExtractionAttempts`, `LastExtractionError` and `LastExtractionFailedAt`
(DDL 050) are reused for legajo failures, and `Billing:MaxExtractionAttemptsPerDocument` caps them
per file, as it does for balances.

`POST /api/analyses` refuses a legajo document (400), so a person's paperwork can never reach the
balance extraction path, which knows nothing about consent. It also refuses (409 `consent_required`)
any document whose ENTITY is linked to a legajo without active consent — a persona humana's paperwork
uploaded through the legacy path, e.g. as `Otro`.

### `UX_Documents_Legajo_Hash`

A filtered unique index on `(PersonLegajoId, FileHashSHA256) WHERE PersonLegajoId IS NOT NULL AND
IsDeleted = 0`. The same file is stored at most once per legajo among live rows. The upload checks
first and answers 409 `duplicate_document`. The index is the database side of that rule, so two
requests racing (a double-click) cannot both store the file.

- The API recognises the violation by the index NAME (#237's pattern) and returns the same 409, with
  the existing document's id. Do not rename it.
- Company documents and soft-deleted rows are outside the filter, so a deleted file can be uploaded
  again.
- Pre-check 4 must return 0 rows. On a first run the column does not exist yet and there are no rows
  to check.

## Consent, and the FK cycle

`POST /api/person-legajos/{id}/consent { documentId }` sets the three consent columns against an
`AutorizacionDatos` document of the same legajo. `DELETE` clears them.

**Active consent** is one rule, read everywhere: `ConsentRecordedAt` is set **and** `ConsentDocumentId`
points at an `AutorizacionDatos` document of the same legajo and tenant that exists and is not deleted.

- The background worker re-checks it immediately before every model call, and again before moving a
  classified document. A document without active consent returns to `Uploaded` and no request is
  sent.
- `DELETE /api/documents/{id}` refuses (409 `consent_document_in_use`) to delete the document a
  recorded consent stands on. Revoking first is explicit and audited.

`FK_Documents_PersonLegajos` and `FK_PersonLegajos_ConsentDocument` reference each other. Both
columns are nullable and both FKs are NO ACTION, so either row can exist first. The API never writes
both in one SaveChanges: the document is stored first, and consent is recorded against it in a later
request.

The EF model maps **neither** FK. EF orders one SaveChanges' INSERTs by modelled FKs only (the #237
lesson on `EntityConfiguration`), and every write here inserts rows whose parents were saved
earlier. The configurations carry the warning: a future write that inserts a parent and its child in
the same SaveChanges must model that FK first.

## `[dbo].[LegajoExtractions]`

There is one **immutable** row per model run. Only two things ever change on a row: `IsCurrent`, and
the 2b review stamp.

- **`Kind`**: `Classification` or `Extraction`.
- **`ReadMode`**: `Document`, `Images`, or `Mixed` (2b).
- **`Status`**: `Succeeded` or `Failed`. A failed run is stored too, because it records what was
  spent.
- **`ResultJson`**: the run's output. Each field carries its own provenance, and the classification
  also records its review reasons. ⚠ PII.
- **`Confidence`**: 0..1, enforced by a CHECK.
- **Tokens and cost**: `InputTokens`, `OutputTokens` and `CostUsd`.
- **Hashes**: `InputHash` and `OutputHash`, SHA-256 of the request and response bodies.
- **`CreatedBy`**: NULL means the background worker ran it.
- **`UX_LegajoExtractions_Current`**: filtered on `IsCurrent = 1`, it admits one current run per
  `(DocumentId, Kind)`. `IsCurrent` defaults to 0 so a hand-written row never takes the slot.

A new run and the flip of the previous run to `IsCurrent = 0` happen in **one SaveChanges**. The
unique index is modelled in EF and pinned by a test. The ordering itself was checked against EF Core
9's `CommandBatchPreparer`:

- EF adds a unique-index dependency only when a **key** column of the index changes. The flip
  changes the **filter** column, so no edge is created.
- `ModificationCommandComparer` therefore decides the order. Within the same table it sorts by
  `EntityState`, and Modified (3) comes before Added (4), so the UPDATE that releases the slot always
  precedes the INSERT that takes it.
- SQL Server checks the filtered index per statement.

## `[dbo].[LegajoCorrections]` (Phase 2b)

- Append-only: one row per corrected field, never updated or deleted.
- `CK_LegajoCorrections_Reason` enforces a reason of at least five non-blank characters.
- FK to `LegajoExtractions`.
- Nothing reads or writes it in 2a.

## `[dbo].[ApiCallLogs].[DocumentId]`

There is no FK, as with `AnalysisId`, so a log row never blocks a document operation. The legajo
passes log `Service = 'Claude'`, which `CK_ApiCallLogs_Service` has admitted since 001.

## `CK_ApiCallLogs_Operation` (section 7, the last batch)

No numbered script ever created a CHECK on `Operation`: that column and its siblings predate this
folder. Financia-db nevertheless carries one, created **out of band**:

```sql
([Operation] IS NULL OR ([Operation]='DynamicsSync' OR [Operation]='BcraLookup' OR [Operation]='Chat'
 OR [Operation]='Narrative' OR [Operation]='Extraction'))
```

It rejected both legajo operations. `ApiCallLogService` swallows a failed write, so those cost rows
would have vanished silently. The first version of this script only **refused** to go on: its last
batch raised an error. It also tested only `LegajoClassification`, although
`LegajoExtractionService` (#239) logs `LegajoExtraction` too.

Section 7 now **widens** the CHECK, with section 1's drop loop. The new list is a **superset**:

| Value | Why it is in the list |
|---|---|
| NULL | ARCA (`EntitiesController`), LUFE (`LufeService`) and NOSIS (`NosisService`) log no operation |
| `DynamicsSync`, `BcraLookup` | Legacy: admitted by the out-of-band CHECK, written by no code in this repo. Another application may write them, so they stay. |
| `Extraction` | `ApiCallLogOperations.Extraction` — balance extraction, `AnalysisController` |
| `Narrative` | `ApiCallLogOperations.Narrative` — scoring narrative, `AnalysisController` |
| `Chat` | `ApiCallLogOperations.Chat` — analyst chat, `AnalysisController` (success and failure rows) |
| `LegajoClassification` | `ApiCallLogOperations.LegajoClassification` = `LegajoMessagesClient.ClassificationOperation`, via `LegajoClassificationService` |
| `LegajoExtraction` | `ApiCallLogOperations.LegajoExtraction` = `LegajoMessagesClient.ExtractionOperation`, via `LegajoExtractionService` |

- **Where no CHECK exists, nothing is created.** A database that never had a CHECK on `Operation`
  stays without one, and section 7 PRINTs so.
- **The code is pinned as a subset.** `ApiCallLogOperations.Known` is every value the API writes.
  `LegajoDocumentsMappingTests` checks that it is a **subset** of this list, not equal to it, and that
  the legacy values remain. The same class fails on any string literal passed as an operation, so a
  new operation cannot ship without joining `Known`, and therefore without widening this CHECK.
- **Pre-check 3** lists the CHECK that will be replaced. It also lists live values outside the new
  list, which must be none: `WITH CHECK` would roll section 7 back.

## Verification

Post-checks 1–5 in the script cover:

- the widened CHECKs: exactly one per column, trusted;
- the eight new CHECKs, with the owner rule table-level, plus `CK_ApiCallLogs_Operation` where
  `[Operation]` had a CHECK before the script (9 rows on Financia-db, 8 on a database without one);
- the six FKs;
- the four indexes;
- that no existing row changed.

## Rollback

See the script's rollback section. Roll the application back first and **export first**: holder
CUITs, model readings and consent records are personal data.

Narrowing the two `[Documents]` CHECKs back is optional and blocked by any legajo document row. The
old code runs fine on the wider lists.

The rollback **keeps** the widened `CK_ApiCallLogs_Operation`. Its pre-070 definition was made out of
band and may differ between databases, so the script has nothing reliable to restore. The widened
list is a superset of the one on Financia-db, and the old code writes fine against it.
