# NOSIS Manager report import

An analyst uploads a NOSIS Manager **"Informe Individual"** PDF on a PyME's NOSIS tab. The API reads it
**in memory** with a deterministic parser (PdfPig, no model call), stores **only company-level data** as
JSON on the entity (`Entities.NosisReportJson`, DDL 087), and **discards the file**. Display only: no
score, alert or prompt reads it.

Code: `Controllers/EntitiesController.NosisReport.cs`, `Servicios/NosisReport/`,
`Models/Nosis/NosisManagerReport.cs`, `Data/Repositories/NosisReportRepository.cs`.

## Endpoints

| Verb | Route | Roles | Notes |
|---|---|---|---|
| GET | `/api/entities/{id}/nosis` | any authenticated | gains `report` (null until imported); nothing else changes |
| POST | `/api/entities/{id}/nosis/report` | Operators | multipart field `file` |
| DELETE | `/api/entities/{id}/nosis/report` | Operators | idempotent; 200 with `report: null` |

POST, in order:

| Condition | Answer |
|---|---|
| empty file, > 25 MB (`PdfSizeLimits`), not `application/pdf`, no `%PDF` signature | 400 `validation_error` |
| entity not in this tenant | 404 |
| persona humana entity | 422 `nosis_report_person_type_unsupported` |
| tenant profile disables NOSIS | 400 "NOSIS no está habilitado para este tenant." |
| same SHA-256 as the stored report | 200, current response, nothing written or audited |
| more than 1.000 pages | 400 `validation_error` |
| not a NOSIS Manager "Informe Individual" (no title/footer/date, footers naming two CUITs, unopenable) | 422 `nosis_report_not_recognized` |
| the report's CUIT ≠ the entity's | 422 `nosis_report_cuit_mismatch` (neither CUIT echoed or logged) |
| otherwise | 200 `EntityNosisResponse`, report stored |

The gate is the tenant's NOSIS **entitlement** only. The NOSIS web-service credentials that
`refresh-nosis` needs (`Nosis:Enabled`, `Nosis:ApiKey`, `Nosis:VR`) play no part: the report is a file
the analyst brings. DELETE is not gated by the entitlement, so stored data can always be removed. A CUIT
correction on the entity clears the report with the other vendor snapshots.

## What is kept, what is never kept

Kept (each section parsed on its own; status `ok` | `unreadable` | `absent` in `report.sections`, all
17 keys always present; parser `nm-1.2`):
`summary` (the header row's SCORE and SITUACIÓN BCRA, then PERFIL / INFORMACIÓN GENERAL first — total
debt, monthly commitments, rejected cheques, consultas per window, estimated turnover "Desde/Mayor a/Menor
a", age and staff range printed under their label — then ARCA, MI PYME, RESUMEN CONFIGURABLE; whitelisted
labels only), `evolucion24m`, `deudaPorEntidad`, `atrasos`,
`aportesPatronales`, `chequesRechazadosPorMes` (month totals only), `criteriosAceptacion`, `juicios`
(count, and per "Fecha Inicio:" block its date, the "Objeto:" type and the court line cut before
"Secr."/"Expte."/"N°"), `sisa` (status, categories), `srt`, `referenciasComerciales` (count,
source names), `balancesReducidos` (up to the 3 newest closings; not the ratios nor the PyME size
table), `mav` (amount per instrument and per currency), `antecedentesFiscales` (one entry per printed rate —
"Alícuota percepción: 3,00 %  Alícuota retención: 1,50 %" gives two, tipo `Percepción` / `Retención` — with
the organism and the block's "Vigencia desde/hasta"; a block with no rate is one entry typed by its
description), `comunicadosBcra` (count and dates of the "Fecha:" lines only), `comercioExterior` (importer /
exporter flags, the years of the "Pos | Descripción | Año …" header; a missing EXPORTACIONES heading
leaves `exportador` null), `marcasCantidad` ("Registra N marcas", count only).

**Empty sections.** A known heading with no body is `ok` with an empty list (`[]`), or `0` for
`marcasCantidad`: the company has nothing there. `unreadable` means a body the parser could not read.

**Real table shapes** (nm-1.2): the bureau's aggregate rows (Situación, Comp. mensuales, Endeudamiento) are
skipped and an entity name alone on a line joins the value line under it; a row the month columns cannot
place is skipped, the section is unreadable only when no entity reads. Atrasos' header spans three lines
and its rows start "MM.YYYY"; the "Situación 5" and "Total" columns are located only to drop them. In
cheques rechazados, "Total = N amount" closes the month above it, a month printed twice across a page
break is summed, and a Total matching neither that month nor all months is a misread (unreadable).
Balances read "31 Dic 2025" headers, keep the value of each "value %" cell, ignore the variation columns
and "#3" footnote marks, and stop at the "Tamaño / Sector" table. MAV joins a wrapped label to the amount
on its own line and stops at the monthly "Instrumento | Cant." cross-table.

Never kept — the splitter drops these lines as it reads them, before any parser: NÓMINA DE EMPLEADOS,
RELACIONES / ÁRBOL, FIRMANTES DE CHEQUES, INFORMACIÓN DE CONTACTO, CHEQUES POSITIVOS, RATIOS
ECONOMICOFINANCIEROS, SOCIEDADES, HOMÓNIMOS, TRANSFERENCIAS FONDOS DE COMERCIO, MIS SEGUIMIENTOS,
CONSULTAS (Y SEGUIMIENTOS), FICHA PROFESIONAL, SCORES, RIESGO SECTORIAL, VALIDACIÓN DE IDENTIDAD,
REFERENCIAS BANCARIAS, ÚLTIMO INFORME, LÍNEAS DE CRÉDITO, COMPROMISOS MENSUALES, **any heading the parser
does not know**, the page title (business name) and footer (CUIT). Inside kept sections, the per-cheque
detail, the lawsuits' actor and case number, the referencias' contact persons and phones, the SISA CBU,
the comunicados' text, the fiscal regimes' CUIT column, the foreign-trade chapter descriptions and the
profile's addresses and phones are never read. `NosisReportPrivacyGuard` then drops any kept string
shaped like a CUIT/CUIL, DNI, phone, CBU or e-mail.

**Headings are told by their printed style.** Each line carries its point size, weight and colour. Once
at least three known headings are found and they are printed unlike the body, a caps line in a heading
style is a heading even when its text is unknown — it closes the previous section and its own lines
are dropped — and a known text printed like the body (a "CONSULTAS" label) is not a heading. A row with
column-wide gaps (a table's column header) is never taken for one. Small or flat documents fall back to
the known texts alone.

**Numbers are read by their own shape**, because the format changes by section within one report: US
(`7,258,316,409`, `31,472.6`), Argentine (`1.527.384.960`, `5,00 %`) or ambiguous (`1,234`, `1.234`).
An ambiguous one follows its section's unambiguous numbers; in a section with none, or with both (MAV),
it reads null rather than a guess. Amounts are normalised to **pesos**: a section, or its INFORMACIÓN
BANCARIA group, that says "Importes expresados en miles de $" is × 1000. Labels cut short with "…" match
by prefix. Month tables are read by x-position against the header (names, `09/2026`, or numbers under a
year line), so a blank cell stays null. Periods are `YYYY-MM`, ascending. `reportDate` is the footer's
Argentina wall-clock time (no offset); `importedAt` is UTC.

**Persona humana** reports are refused (422) in this version. For a follow-up: they carry five scores,
an NSE, aportes as "Empleado" and no cheques rows.

**Re-import.** The same file is a no-op only when its SHA-256 and the parser version both match the
stored report; after a parser upgrade, re-uploading the same PDF reads it again.

## Audit and logs

`AuditLogs`: `Entity.NosisReportImported`, `Entity.NosisReportReplaced`, `Entity.NosisReportDeleted`
(`EntityType = Entity`) with the SHA-256, report date, parser version and section statuses — no value.
Logs: TenantId, EntityId, pages, elapsed ms, section statuses, dropped-string count. Never text, names,
CUITs or amounts.

## The live check on real reports (local only)

Run it on real reports on your machine. The folder and the expected file must be **outside the
repository** (the test refuses otherwise) and no real report is ever committed.

```bash
FINANCIA_NOSIS_REPORTS_DIR=/ruta/fuera/del/repo FINANCIA_NOSIS_REPORTS_EXPECTED=/ruta/fuera/del/repo/expected.json dotnet test Api.Web.ServiciosFinancieros.slnx -c Release --filter "FullyQualifiedName~NosisManagerReportHarnessTests" --logger "console;verbosity=detailed"
```

PowerShell:

```powershell
$env:FINANCIA_NOSIS_REPORTS_DIR = 'C:\ruta\fuera\del\repo'; $env:FINANCIA_NOSIS_REPORTS_EXPECTED = 'C:\ruta\fuera\del\repo\expected.json'; dotnet test Api.Web.ServiciosFinancieros.slnx -c Release --filter "FullyQualifiedName~NosisManagerReportHarnessTests" --logger "console;verbosity=detailed"
```

Per file (named `#1`, `#2`… by sorted order) it prints the page count, the encryption (`V2/R3` is the
NOSIS RC4-128 owner-password file), the elapsed time, the outcome, the subject's person type (from the
CUIT prefix; the CUIT is never printed), how many headings were found (known + unknown), whether the
heading style calibrated, and each section's status and row counts.

**Ground truth.** `FINANCIA_NOSIS_REPORTS_EXPECTED` (optional) is a JSON you write by hand. Entries are
matched to files by SHA-256; for each field present in an entry the harness prints only `OK`, `DIFF`
or `MISSING` — never either value — and names entries by their index, never their alias. Fields absent
from an entry are not compared; amounts compare within 0.01. `outcome` is the parse result: `"ok"`
(parsed), `"not_recognized"` or `"too_many_pages"`; when a file did not parse, every other field reads
`MISSING`. Synthetic example:

```json
{ "files": [ { "alias": "x", "sha256": "…", "reportDate": "2026-11-03T14:36:00",
  "summary": { "score": 1, "situacionBcra": 3, "endeudamientoTotal": 1, "compromisosMensuales": 1,
               "tieneChequesRechazados": true, "consultas3m": 1, "consultas12m": 1 },
  "criteriosAceptacion": { "resultado": "Observado", "enPrueba": true },
  "evolucion24m": { "count": 24, "first": "2024-12", "last": "2026-11", "lastScore": 1 },
  "chequesRechazadosPorMes": { "latest": { "periodo": "2026-10", "cantidad": 5, "monto": 1.0 } },
  "juicios": { "comoDemandadoCantidad": 1 } } ] }
```

`evolucion24m` is compared as `{count, first, last, lastScore}` and `chequesRechazadosPorMes` as
`{count, latest}`; every other field by its contract name (`sisa.estado`, `mav.porMoneda[0].monto`, …).

**Shapes.** `FINANCIA_NOSIS_REPORTS_SHAPES=1` prints, for each `unreadable` section, its lines with every
letter masked to `a`/`A` and every digit to `9`, each line with its printed style (`[9.0 B #1f4e79]`)
and each word with its left edge in points (`Aaaaaaa@40 9,999@197`), plus the headings found (unknown
ones masked). `=all` does it for every section and the header block — use it when a field reads `DIFF`.
That is enough to fix a rule without the report.
