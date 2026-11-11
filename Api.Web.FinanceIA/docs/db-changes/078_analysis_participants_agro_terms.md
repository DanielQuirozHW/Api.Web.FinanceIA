# 078 — AnalysisParticipants: the solicitante's agro annual mode terms

## What changed and why

Persona humana engine 0.7 evaluates a solicitante whose main activity is agro in ANNUAL mode: the sales
window is aligned to the campaign, a monthly DDJJ gap that falls in the off-season is not stale, and the debt
service is compared with the flujo on the same annual base, reading the cuota by its periodicity. Agro lines
pay at maturity (180–300 days) or in monthly, quarterly, semiannual or annual cuotas, so "the cuota" alone no
longer says what a year of debt service is.

The two terms that decide it are the analyst's, stated on `POST /api/person-analyses`, and a re-evaluation
(#287, "Reanalizar") must reuse them, so they are stored on the solicitante's row beside `InstallmentAmount`:

| Column | Type | Meaning |
|---|---|---|
| `CuotasPorAnio` | TINYINT NULL | how many cuotas a year the cuota is: 1 (annual or at maturity), 2, 4 or 12 — the value RESOLVED at creation |
| `AgroModeOverride` | BIT NULL | the analyst's override: 1 forces agro mode on, 0 forces it off; NULL detects it from ARCA |

Two CHECKs, both created WITH CHECK (trusted, every existing row is NULL):

- `CK_AnalysisParticipants_CuotasPorAnio`: `[CuotasPorAnio] IS NULL OR [CuotasPorAnio] IN (1, 2, 4, 12)` — the
  same closed set the request validates (`PersonEvaluationDefaults.AllowedCuotasPorAnio`).
- `CK_AnalysisParticipants_AgroTerms_Role`: `[Role] <> N'Fiador' OR ([CuotasPorAnio] IS NULL AND [AgroModeOverride] IS NULL)`.
  A fiador keeps his own evaluation and is never in agro mode.

### What NULL means

- **`CuotasPorAnio` NULL** appears only on rows written before this release. Their cuota was stated as
  monthly, so a re-evaluation reads them as **12**, never as the tenant's `pf_agro_cuotas_por_anio`.
- **`AgroModeOverride` NULL** means "detect": agro when the main ARCA activity code starts with 01, 02 or 03
  and the tenant's `pf_agro_modo_anual` is on.

### Why the RESOLVED periodicity is stored

On a new analysis without `cuotasPorAnio`, the evaluation resolves it (the tenant's `pf_agro_cuotas_por_anio`
in agro mode, else 12) and the row stores that value. The periodicity is a term of the operation, not a tenant
threshold: a later change to `pf_agro_cuotas_por_anio` must not re-read an existing operation's cuota.

## What ships with it (no further DDL)

- Three tenant keys in `[ScoringConfig]` (group `capacidad`, defaults in code, no seed row needed):
  `pf_agro_modo_anual` (flag, 1), `pf_agro_cuotas_por_anio` (1, 2, 4 or 12; 2) and
  `pf_agro_mes_inicio_campania` (1-12; 7, the July–June campaign).
- The engine version moves to 0.7. Its resolved mode is frozen in `DocumentVersionsJson` (`sources.agro`);
  evaluations frozen before 0.7 have none and read exactly as they were frozen.

## Audit and PII

No personal data: a periodicity and a flag. Neither is logged. The create and re-evaluation audit entries
are unchanged.

## Deploy order

1. The DBA runs `078_analysis_participants_agro_terms.sql` in **every** Financia database (one per client,
   `.github/clients.json`). It is not for the Default database. Running it first is safe for the code already
   deployed: both columns are nullable with no default, the old INSERT names neither, and NULL satisfies both
   CHECKs. Section 0 stops the script, altering nothing, if `[AnalysisParticipants]` or 077's `[FiadorId]` is
   missing.
2. Run the deploy workflow from DEVELOP with `ddl_aplicada` ticked and `client = all`.
   `--verify-schema` refuses a client whose database lacks a column.
3. The App Service restart is part of the publish (`restart: true`).
4. Deploy the Portal. Every API change is additive: two optional request fields (`cuotasPorAnio`,
   `agroMode`) and two response fields (`personEvaluation.cuotasPorAnio`, `personEvaluation.agroMode`).

Publishing the API before the script breaks every read and write of `[AnalysisParticipants]`, i.e. the
persona analysis, with `Invalid column name`.

## Rollback

Roll the API back to a release that does not map the columns, then run the commented rollback at the end of
the script: export the rows that use the columns (no personal data), then drop the two CHECKs and the two
columns. Analyses created under 0.7 stay readable by the old code, which ignores `sources.agro` and the new
input keys; a re-evaluation under the old code reads their cuota as monthly again.
