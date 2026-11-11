# 072 — The persona humana analysis and its participants

## What changed and why

M10 Phase 4a "v0" lets an analyst open a credit analysis on a persona humana **applicant**
(solicitante). The analysis runs on the person's legajo instead of a balance file, and it produces three
things:

- pre-filters: the MiPyME certificate (LUFE), BCRA situación, serious BCRA events, unpaid cheques,
  ARCA sucesión and CUIT status, activity age, the monotributo cap (or, for a responsable inscripto, the
  declared consumption against the tenant's reference basket), and sales declared to the creditor vs to
  the fisco;
- indicators from the #253 read model: sales level and real trend, activity cash flow less
  consumption, cuota/ingreso, debt-service coverage, solvency, indebtedness, operating assets and sales
  coherence. Every filter and indicator says whether its evidence is `Verificado` (DDJJ, BCRA, ARCA,
  LUFE) or `Declarado` (manifestación, certificación, tenant parameters);
- a documentary coverage index.

There is **no score, no risk level and no model call**. The only conclusion is a flag: "Rechazo
sugerido" when a filter fires a rejection, otherwise "Para decisión del analista".

Once created, the analysis follows the normal flow: decision, approval limits, pre-approval and quorum,
then the credit line. Every threshold is a `pf_` key of `[dbo].[ScoringConfig]` with a PROVISIONAL code
default (`PersonEvaluationDefaults`). Those keys need no DDL: the code fallback covers a tenant with no
row, as with scripts 017 and 068.

The design of record is the Claude Doc "M10 · Diseño del módulo Persona física", sections "Modelo de
datos", "Filtros e indicadores por rol" and "Índice de cobertura documental".

| Change | Where |
|---|---|
| The kind of an analysis | `[dbo].[Analyses].[AnalysisKind]` NVARCHAR(20) NOT NULL DEFAULT `Balance`, `CK_Analyses_AnalysisKind` |
| A persona analysis has no single document | `[dbo].[Analyses].[DocumentId]` becomes NULLABLE |
| NULL only on a persona analysis | `CK_Analyses_DocumentId_Kind` |
| The confirmations of a persona approval, per signer | `[dbo].[Analyses].[PreApprovalChecksJson]`, `[ConfirmationChecksJson]` NVARCHAR(MAX) NULL, `CK_Analyses_PreApprovalChecksJson`, `CK_Analyses_ConfirmationChecksJson` (`ISJSON`) |
| The role of each person, with the evaluation frozen | new `[dbo].[AnalysisParticipants]` |

### Why the Analyses row stays the decision record

The exploration behind this phase found that the decision path never reads the document or the balance.
That path covers:

- `PATCH /api/analyses/{resultId}/decision`;
- `AnalysisApprovalQuorumService`;
- `UserApprovalLimits`;
- the decision notifier;
- the credit-line create, raise and operation gates.

All of them are keyed by the AnalysisResults `ResultId` and read only these Analyses columns: tenant,
entity, status, requester, pre-approval and decision amount and currency, and created-at.

A persona analysis is therefore one `[Analyses]` row and one `[AnalysisResults]` row, the same pair a
balance analysis writes, plus its `[AnalysisParticipants]` row:

- **`[Analyses]`**: `AnalysisKind = PersonaHumana`, `DocumentId = NULL`, `Status = PendingReview`.
  `AiModel = 'none'` and `PromptVersion = 'pf-0.1'`, because no model answered it.
  `AiTemperature = 0.00`, which `CK_Analyses_AiTemperature` requires.
- **`[AnalysisResults]`**: every balance-only column is NULL, namely balance, ratios, score, level,
  narrative, alerts, anomalies and breakdown. The required columns are filled honestly:
  - `AiInputHash` and `AiOutputHash` are the SHA-256 of the frozen inputs and outputs;
  - `ValidationStatus` is `Passed`, only because the column is NOT NULL and
    `CK_AnalysisResults_ValidationStatus` admits nothing else that fits. It is a balance-sheet check
    that does not apply to a person, so every API response for a persona analysis returns it **empty**
    (the Portal then shows neither "Estado de validación" nor an "Estado actualizado" event);
  - `EnrichmentStatus` is `Complete`, or `Degraded` when the legajo's BCRA snapshot is missing or
    failed.

  The legajo's BCRA and ARCA snapshots and the CPI series used are frozen into `BcraDataJson`,
  `FiscalDataJson` and `MacroDataJson`. That is the same write-once rule the balance pipeline follows,
  and it lets `/bcra/cheques` and `/context` work unchanged.

## Deploy ordering

**RUN BEFORE DEPLOYING**, once on Financia-db, then restart the App Service. Run it on any other
business database the DDL procedure lists, as with 069–071.

EF maps `[Analyses].[AnalysisKind]`, the two checks columns and the new table. Publishing first therefore breaks **every**
SELECT and INSERT against `[dbo].[Analyses]` with `Invalid column name`. That takes down the analysis
list, the detail, the decision, the pipeline and the credit lines. The deploy job's `--verify-schema`
step refuses that publish.

Running the DDL first is safe for the code still running:

- `AnalysisKind` is NOT NULL with a default, so the old INSERT (which does not name it) writes a valid
  `Balance` row;
- the two checks columns are NULL and the old code never names them;
- a nullable `DocumentId` accepts every value the old code writes;
- every existing row has a document, so `CK_Analyses_DocumentId_Kind` holds;
- the old code never reads `[AnalysisParticipants]`.

`--verify-schema` checks only that tables and columns exist. It does **not** check that `DocumentId`
became nullable. If the ALTER did not run, the first persona analysis fails its INSERT, which is a 500
on that endpoint only. Post-check 1 is the evidence for nullability.

**Portal.** The API change is additive: a new endpoint, `analysisKind` on the list and the detail,
`personEvaluation` (with `decisionRules`), `preApprovalChecks` and `confirmationChecks` on the detail,
two optional booleans on the decision request, new 400/403 `errorCode`s on the decision of a persona
analysis only, and new 409 `errorCode`s on chat, chat attachment and PDF export. No
enum value of an existing field changes, so no Portal-first step is needed. The Portal's 4b work
consumes the new fields after this deploy.

### Execution history

- Pending.

## `[dbo].[Analyses]`

### `AnalysisKind`

| Value | Meaning |
|---|---|
| `Balance` | The PyME analysis of one balance file. The default, and every row before 072. |
| `PersonaHumana` | The persona humana evaluation on the legajo, created by `POST /api/person-analyses`. |

The code's list is `AnalysisKinds`; `PersonAnalysisMappingTests` pins it to the script.

### `DocumentId` → NULL

There is no index on the column. 001 creates only `IX_Analyses_TenantId_EntityId` and
`IX_Analyses_TenantId_Status`, so nothing has to be dropped. The script **does not guess**: if an index
on the column exists anyway (pre-check 2), it RAISERRORs naming it, alters nothing, and sets
`NOEXEC ON` so that no later batch runs. RAISERROR alone would end only its own batch, and the CHECK
and the table would then be created over a `DocumentId` that is still NOT NULL. Re-run the whole script
after deciding about the index. `FK_Analyses_DocumentId` stays as it is, because NULL bypasses an FK, the same as in 048.

### `CK_Analyses_DocumentId_Kind`

```sql
CHECK ([DocumentId] IS NOT NULL OR [AnalysisKind] = N'PersonaHumana')
```

A balance analysis without its balance file is a bug, and the database refuses it.

### `PreApprovalChecksJson` and `ConfirmationChecksJson`

Approving a persona analysis needs two explicit confirmations (`counterGuaranteesConfirmed`,
`fdrLimitConfirmed`) and, under `pf_aprobacion_requiere_motivo`, a reason. Each signer's are kept:

```json
{ "counterGuaranteesConfirmed": true, "fdrLimitConfirmed": true, "reason": "…", "by": "<userId>", "at": "…" }
```

- The pre-approval writes `PreApprovalChecksJson`; the final approval (direct, or the vote that reaches
  quorum) writes `ConfirmationChecksJson`. Neither rewrites the other, the same pairing as
  `PreApprovedOperationsJson` (058) and `ConfirmedOperationsJson` (061).
- A quorum vote that does not confirm is recorded in the audit log only.
- Both are NULL on every balance analysis and on every row before 072. The `ISJSON` CHECK rejects a
  non-JSON write; NULL passes.
- `reason` is the analyst's own text (the `reviewNotes` of the request). It is free text: treat it as
  possibly personal data, like `ReviewNotes`.

## `[dbo].[AnalysisParticipants]`

One row per person and role in an analysis (`UX_AnalysisParticipants_Analysis_Legajo_Role`). Phase 4a
writes only the `Solicitante`; `Fiador` is reserved for Phase 5 (073). Phase 5 also adds the fiador
columns of the design: `GuaranteeType`, `SpouseConsent` and `HousingInopposable`.

| Column | What it holds |
|---|---|
| `AmountAtCharge`, `Currency` | The requested amount, for the solicitante. |
| `InstallmentAmount` | The cuota of the requested operation, when the analyst stated it (it feeds cuota/ingreso). |
| `DocumentVersionsJson` | Everything the evaluation read; see below. |
| `FiltersJson`, `IndicatorsJson`, `CoverageJson` | The evaluation output. |
| `ScoreJson` | **RESERVED.** Always NULL in Phase 4a. |
| `EvaluationVersion` | `PersonEvaluationService.Version` (`0.1`). |

`DocumentVersionsJson` records:

- every live legajo document, with its current extraction run (id, schema, prompt, model, output hash)
  and the ids of the corrections applied, in order. A correction changes the effective values without
  a new run, so the run id alone would not identify what was read;
- the BCRA, ARCA and inventory stamps;
- where the CPI series came from;
- every threshold used.

The row is written once and never updated. A later correction, upload or rebuild of the legajo does
not change a past analysis.

## FKs

| Name | References | Action |
|---|---|---|
| `FK_AnalysisParticipants_Analyses` | `[Analyses].[AnalysisId]` | NO ACTION |
| `FK_AnalysisParticipants_PersonLegajos` | `[PersonLegajos].[PersonLegajoId]` | NO ACTION |

EF models neither, as in 070 and 071. `PersonAnalysisRepository.CreateAsync` inserts the Analyses row
in its own SaveChanges before the result and the participant, inside one transaction, so the three rows
exist together or not at all.

## ⚠ PII

The participant row carries figures and document ids, **never** the person's name or CUIT. The figures
are still a natural person's income and patrimony.

The persona analysis's `[AnalysisResults]` row **does** hold identity. It freezes the legajo's BCRA
snapshot (`identificacion`, `denominacion`) and ARCA snapshot (domicilio fiscal), exactly as a balance
analysis freezes the company's. That is storage in the tenant's database beside the analysis, not a
prompt and not a log. Treat every row of both tables, and every export in the rollback, as personal
data.

Nothing of the evaluation reaches the model:

- the engine is deterministic C#;
- chat and chat attachment answer 409 `chat_unavailable_for_person_analysis`;
- the PDF export answers 409 `person_analysis_export_unavailable`.

Logs carry ids only.

## Verification

Run the commented post-check at the end of the script:

1. `AnalysisKind` is NOT NULL, `DocumentId` is nullable, `PreApprovalChecksJson` and
   `ConfirmationChecksJson` exist as nullable, and every existing row reads `Balance`.
2. The 12 CHECKs are enabled and trusted.
3. The 3 FKs are NO ACTION.
4. The 2 indexes exist.

Then, after the deploy:

1. `POST /api/person-analyses` on a persona humana entity whose legajo has consent and at least one
   Ready document answers 201 with `analysisKind = PersonaHumana`.
2. The new analysis appears in `GET /api/analyses?entityId=…` with no score.

## Rollback

See the commented rollback at the end of the script. Roll the application back first.

Persona analyses have `DocumentId` NULL, so they must be exported and deleted before `DocumentId` can
return to NOT NULL. Delete their dependants with them: results, votes, cupos and participants. The
export includes their `[AnalysisResults]` rows, which carry the person's identity (see PII).

Step (c) drops the two `ISJSON` CHECKs, then the two checks columns. Export them first if the persona
approvals' confirmations must survive.
