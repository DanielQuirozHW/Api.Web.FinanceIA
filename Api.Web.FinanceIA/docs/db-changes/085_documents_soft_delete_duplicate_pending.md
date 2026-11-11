# 085 — Soft-delete the duplicate "Pending" documents of a double submit (data fix)

## Why

QA Q32: a real company upload created two `Documents` rows for the same file 250 ms apart. The analyst
analysed one; the other stayed `Pending` forever and kept being offered by the new-analysis wizard.
`POST /api/documents/upload` never looked for an existing document with the same hash, so a double submit
(two clicks, or a retried request) stored the file twice.

The API now answers a repeat company upload of the same file (same entity, document type and SHA-256)
within 120 s (`DocumentsController.DuplicateUploadWindow`) with the first document — 200 OK, nothing
written — under an in-process lock per (tenant, entity, hash). 085 cleans the duplicates created before.

## What it does

- **DATA FIX (DML), no schema change.** Optional; **not a deploy blocker**; safe before or after the deploy;
  no restart. `--verify-schema` neither needs nor sees it.
- Run once in **each Financia DB**; covers every TenantId in it.
- A document is soft-deleted (`IsDeleted = 1`, `DeletedAt = SYSUTCDATETIME()`, never a physical DELETE)
  when it is a company document (`PersonLegajoId IS NULL`), `Pending`, not deleted, has no `Analyses` row,
  and has a non-deleted sibling — same TenantId, EntityId, DocumentType and non-blank FileHashSHA256,
  uploaded within 120 s — that outranks it.
- Ranking (kept first): has an analysis → not Pending → uploaded first → smaller DocumentId. The top row of
  every group is always kept; a row with an analysis, or not Pending, is never touched.
- `@Apply = 0` (default) **previews**: result set 1 lists each row it would soft-delete with the sibling kept.
  `@Apply = 1` writes it in **one transaction** (XACT_ABORT ON); the UPDATE re-checks Pending / not deleted /
  no analysis, so a document analysed meanwhile is skipped. Idempotent.

## What it does not touch

- Persona humana legajo documents (their `UX_Documents_Legajo_Hash` unique index already rejects duplicates).
- The blob in storage (as with every soft delete), `ExtractionAttempts` and every other column.
- Duplicates further apart than 120 s: a deliberate re-upload stays a separate document, as the API keeps it
  (`ReanalysisPolicy` is per document — see the XML comment on `DuplicateUploadWindow`).

⚠ It bypasses the application audit trail (no `Document.Deleted` AuditLogs row). Record the run in
`docs/database/README.md`.
