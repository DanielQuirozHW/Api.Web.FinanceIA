# 079 — Declared installments on AnalysisParticipants, and the Efectivo item kind

## What changed and why

Persona humana engine 0.8 lets the analyst state the cuotas vigentes a person already pays a month. Until 0.7
the engine only estimated them as `pf_cuota_estimada_pct_saldo` (5 %) of the Central de Deudores balance,
and that estimate feeds `cuota_ingreso`, `cobertura_servicio_deuda` (monthly and agro annual) and the fiador's
`cuota_ingreso_estres`. When the analyst has the real figure — a bank statement, the lender's schedule — it
replaces the estimate in all of them. The figure is a term of the analysis, stated per person on
`POST /api/person-analyses` (`cuotasVigentesDeclaradas` for the solicitante and per fiador), and a
re-evaluation ("Reanalizar") must reuse it, so it is stored on each participant's row:

| Column | Type | Meaning |
|---|---|---|
| `ExistingInstallmentsAmount` | DECIMAL(18,2) NULL | the monthly cuotas vigentes the analyst declared, in pesos; solicitante and fiador rows |

`CK_AnalysisParticipants_ExistingInstallmentsAmount`: `[ExistingInstallmentsAmount] IS NULL OR
[ExistingInstallmentsAmount] >= 0`, created WITH CHECK (every existing row is NULL). The request validates the
same rule (400 `validation_error`).

The same script widens `CK_LegajoItems_Kind` with `Efectivo`. The reconciliation used to map the Bienes
Personales rubro `DineroEfectivo` to `Otro`, together with `BienesMueblesRegistrables`, `OtrosBienes` and
`Otro`, and `[LegajoItems]` does not store the rubro, so the fiador coverage could not tell cash apart: cash
was left out only because `pf_haircut_otro` defaults to 100 %, with the generic `bien_no_admitido` reason, and
a tenant lowering that haircut would have counted it. Decision 7 is that cash at hand without documentary
support never counts, so cash now reconciles into its own kind and the coverage always excludes it with
`efectivo_sin_respaldo` ("Efectivo declarado sin respaldo documental: no se computa."). Bank deposits are
unchanged: `Deposito`, `pf_haircut_deposito`.

### What NULL means

- **`ExistingInstallmentsAmount` NULL** means "not declared": the evaluation estimates, exactly as 0.7 did.
  Every row written before this release is NULL, and re-evaluating it keeps estimating.
- **Zero is a declaration**: the analyst states the person pays no installments. It is not NULL.

### Efectivo on legajos reconciled before this release

The items are rebuilt from the extractions, never migrated: a legajo reconciled under 0.7 keeps its cash as
`Otro` (and `bien_no_admitido`) until its reconciliation runs again — `POST
/api/person-legajos/{id}/reconciliation/rebuild`, or any document change. Analyses already evaluated are
frozen and do not change.

## What ships with it (no further DDL)

- Engine 0.8. The evaluation inputs gain `cuotasVigentesOrigen` (`declarada` | `estimada`) and, when
  declared, `cuotasVigentesDeclaradas`; `cuotasVigentesEstimadas` keeps the estimate for comparison.
  Evaluations frozen before 0.8 have none and read as they were frozen.
- The manifestación extraction moves to `mb-1.2`: it reads the declared amount of "avales y/o coobligaciones
  otorgados" (`/contingencies/guaranteesGiven`), stored in the extraction's result like every other
  manifestación figure and exposed on `GET /api/person-legajos/{id}/inventory` as
  `garantiasOtorgadasDeclaradas` with `garantiasOtorgadasDocumentId`. It is NOT applied to the evaluation:
  the analyst still sends `guaranteesGivenAmount`. Documents extracted before are not re-run; the figure is
  null until a document is reprocessed.
- The dashboard's recent re-scores carry `previousSuggestion` / `newSuggestion` for persona items, read with
  `JSON_VALUE` from `[AnalysisRescores].[PreviousValuesJson]` (DDL 073, no change).

## Audit and PII

No personal data: an amount keyed by participant row, and a list of kinds. The declared amount is not logged;
the create and re-evaluation audit entries are unchanged.

## Deploy order

1. The DBA runs `079_person_installments_and_cash_kind.sql` in **every** Financia database (one per client,
   `.github/clients.json`). It is not for the Default database. Running it first is safe for the code already
   deployed: the column is nullable with no default, the old INSERT does not name it, NULL satisfies its CHECK,
   and the widened kind CHECK admits everything the old code writes. Section 0 stops the script, altering
   nothing, if `[AnalysisParticipants]`, `[LegajoItems]` or 078's `[CuotasPorAnio]` is missing. Run the
   post-check: `--verify-schema` sees the column but not the CHECKs.
2. Deploy the Portal labels for the new item kind `Efectivo` and the reason `efectivo_sin_respaldo` (an added
   enum value goes Portal first; an older Portal shows the raw value).
3. Run the deploy workflow from DEVELOP with `ddl_aplicada` ticked and `client = all`.
   `--verify-schema` refuses a client whose database lacks the column.
4. The App Service restart is part of the publish (`restart: true`).

Publishing the API before the script breaks every read and write of `[AnalysisParticipants]`, i.e. the
persona analysis, with `Invalid column name`; and, on a database without the widened CHECK, the
reconciliation of any legajo whose Bienes Personales declares cash fails on `CK_LegajoItems_Kind`.

## Rollback

Roll the API back to a release that does not map the column, then run the commented rollback at the end of
the script: export the declared amounts (no personal data), set `Efectivo` items back to `Otro`, drop the
CHECK and the column, and restore the 071 kind list. Analyses evaluated under 0.8 stay readable by the old
code, which ignores the new input keys; a re-evaluation under the old code estimates again.
