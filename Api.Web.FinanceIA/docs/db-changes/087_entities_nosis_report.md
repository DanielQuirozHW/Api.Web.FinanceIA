# 087 — The NOSIS Manager report on the entity

## What changed and why
An analyst can now upload a NOSIS Manager "Informe Individual" PDF on a PyME's NOSIS tab
(`POST /api/entities/{id}/nosis/report`). The API reads it **in memory** with a deterministic parser
(`Servicios/NosisReport/`, PdfPig, no model call), keeps only company-level data as JSON, and discards
the file. Nothing is stored in blob storage and no `Documents` row is written.

| Column | Type | Meaning |
|---|---|---|
| `Entities.NosisReportJson` | `nvarchar(max)` NULL | The parsed report — the `report` object of `GET /api/entities/{id}/nosis`, verbatim. |
| `Entities.NosisReportDate` | `datetime2` NULL | The report's own date and time, as its footer prints it (Argentina wall-clock time, **not** UTC). |
| `Entities.NosisReportImportedAt` | `datetime2` NULL | When it was imported, UTC. |

All three are NULL together (no report) or set together. A re-upload of the same file (same SHA-256)
writes nothing; a different file replaces the three; `DELETE /api/entities/{id}/nosis/report` clears
them; a CUIT correction on the entity clears them with the other vendor snapshots.

### Why separate from `NosisDataJson`
`NosisDataJson` is the WS.01 variables snapshot, copied write-once into `AnalysisResults` and read by
the scoring engine. The Manager report feeds neither the score nor the alerts in this release, and it
must never reach a prompt. A separate column keeps it out of every path that reads `NosisDataJson`.

### Why two date columns beside the JSON
So a list or a support query can show "report of <date>, imported <date>" without reading the blob.

## What ships with it (no further DDL)
- `AuditLogs` rows `Entity.NosisReportImported`, `Entity.NosisReportReplaced` and
  `Entity.NosisReportDeleted` (`EntityType = Entity`). No CHECK constrains `AuditLogs.Action`.
- No `ApiCallLogs` row: no external call is made.

## Audit and PII
The parser never persists a person. It skips, without reading them into the model: NÓMINA DE EMPLEADOS,
RELACIONES / ÁRBOL, FIRMANTES DE CHEQUES, INFORMACIÓN DE CONTACTO, the per-cheque detail of CHEQUES
RECHAZADOS (only the month totals are kept), the parties and case numbers of the lawsuits, the contact
persons of the referencias, the SISA CBU and the CONSULTAS detail. A last pass drops any stored string
shaped like a CUIT/CUIL, DNI, e-mail, phone or CBU. The subject's own CUIT and business name are compared
and discarded, not stored. The audit rows carry the file's SHA-256, the report date, the parser version
and each section's status — no value. Logs carry counts and statuses only.

## Deploy order
1. The DBA runs `087_entities_nosis_report.sql` in **every** Financia database, then its post-check.
2. The deploy workflow from DEVELOP with `ddl_aplicada` ticked and `client = all`. `--verify-schema`
   refuses a database missing any of the three columns.
3. The App Service restarts with the publish; `/health` is the post-gate.
4. The Portal follows (the `report` field is additive; an older Portal ignores it).

## Rollback
Roll the application back first. Keeping the columns with the old code is safe: it never reads them.
To drop them, see the script's rollback block. Nothing is lost that the analyst cannot re-upload.
