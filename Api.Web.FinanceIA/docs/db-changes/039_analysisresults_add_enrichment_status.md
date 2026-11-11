# 039 — Enrichment status: a failed BCRA lookup stops looking like a clean one

## What changed and why

When the BCRA Central de Deudores is unreachable, `BcraService` still returns a `BcraData` — it
returns `null` only if deuda **and** macro **and** cambiario all fail. The deuda block inside it
carries `Estado = LookupFailed`, but `AnalysisController` only null-checked the payload, so that
object was serialised into `BcraDataJson` and logged as *"BCRA data enriched"*.

Downstream, `ScoringService` does notice: it excludes the BCRA component and renormalises the
weights onto ratios and macro. Because a clean BCRA scores `0` risk at weight `0.35`, removing it
redistributes that weight to the two non-zero components and the composite gets **strictly worse** —
on the order of 100 NOSIS points, varying with the ratios score.

The resulting row was indistinguishable from a fully-enriched one. The only trace was a
`bcraExcluded` boolean inside the free-form `ScoreBreakdownJson` blob, and that boolean **cannot
tell a BCRA outage apart from an entity that has no CUIT** — it is true in both cases.

This change adds:

- **`[dbo].[AnalysisResults].[EnrichmentStatus]`** — a first-class, queryable, constrained value:
  `Complete` (BCRA answered), `NotApplicable` (the entity resolved and has no CUIT, so there was
  never anything to query), `Degraded` (we tried and failed, and a retry could succeed — the entity
  could not be resolved, or BCRA did not answer).
- **`[dbo].[AnalysisResults].[EnrichmentNotes]`** — a short es-AR sentence for the `Degraded` case
  only, written for an analyst rather than a log reader.

`NotApplicable` and `Degraded` carry the **same numeric penalty** — both are scored without the BCRA
component and both trigger the identical renormalisation. What separates them is **permanence, not
magnitude**: a `NotApplicable` analysis can never score differently, because with no CUIT no BCRA
data can ever exist for it, whereas a `Degraded` one would on a retry that succeeded. Only the second
is provisional.

`ValidationStatus` is deliberately **not** reused: it is the balance-sheet arithmetic check and says
nothing about data provenance. `Analyses.Status` is deliberately **not** touched either — it is
constrained by `CK_Analyses_Status` and a new value there would need its own DDL and a workflow
decision. The degraded marker belongs on the result, not on the workflow.

**The scoring calibration is unchanged by this release.** No weight, threshold or numeric output of
`ScoringService.Compute` moved; how much a degraded analysis should be penalised is a separate,
pending business decision. A dedicated regression test pins the exact composite for both the
included and excluded paths to prove it.

## DDL

Script location: `docs/database/039_analysisresults_add_enrichment_status.sql`

Objects created:

| Object | Type |
|---|---|
| `[dbo].[AnalysisResults].[EnrichmentStatus]` | column, `NVARCHAR(20) NOT NULL` |
| `DF_AnalysisResults_EnrichmentStatus` | default constraint — `N'Complete'` |
| `[dbo].[AnalysisResults].[EnrichmentNotes]` | column, `NVARCHAR(500) NULL` |
| `CK_AnalysisResults_EnrichmentStatus` | check — `Complete` \| `NotApplicable` \| `Degraded` |

The default constraint backfills every existing row to `Complete`. That is deliberate and imperfect:
the column is `NOT NULL` so EF can map it as a required string, and no honest per-row value can be
reconstructed for history. **Treat `EnrichmentStatus` as trustworthy only for analyses created after
this script ran.**

Unlike `CreditLines.Status` (see 029 and 036, where the vocabulary is validated in the API by design),
this column **does** get a CHECK constraint: it is read to decide whether a score can be trusted, so
a typo must fail the INSERT rather than quietly produce an unknown status no consumer branches on.

## The optional backfill, and why it is commented out

The script ends with an `UPDATE` that marks historical rows `Degraded` where
`JSON_VALUE(ScoreBreakdownJson, '$.bcraExcluded')` is true. **It is commented out and must not be run
without a deliberate decision**, because it over-marks and cannot be made precise:

`bcraExcluded` is true both when BCRA was queried and failed (genuinely `Degraded`) and when the
entity had no CUIT to query (`NotApplicable`, never penalised for a failure). The discriminator is
CUIT presence *at analysis time*, which was never persisted on the result and may since have changed
on the entity.

So: leaving history at `Complete` understates how many analyses were degraded; running the backfill
overstates it by marking legitimately-`NotApplicable` rows as `Degraded` — the same class of false
claim this column exists to eliminate, pointed the other way. Neither is the truth and the truth is
not recoverable. **The default is to do nothing.** If it is run, the `GeneratedAt` cutoff must be set
to the timestamp at which 039 was applied, so correctly-written post-039 rows are not overwritten.

## Pre-conditions

- **Must be executed BEFORE deploying the matching commit**, on **every** tenant database. This is a
  hard ordering requirement, not graceful degradation. Both columns are mapped in
  `AnalysisResultConfiguration` and `EnrichmentStatus` is `NOT NULL`, so EF Core emits them in every
  `SELECT` and `INSERT` against `AnalysisResults`. Against a database without them, analysis creation
  **and** retrieval fail outright with "Invalid column name", and no application-level try/catch can
  protect that. Same failure mode as `CatalogueAlerts` in 038.
- **Restart the App Service after deploying**, so EF's model cache is rebuilt.
- The script is idempotent — both columns and the check constraint are guarded, so it is safe to run
  multiple times. Re-running never resets a value already written.

## Rollback

Reverse order. Both columns are new and read only by this feature, so neither drop loses data outside
it.

```sql
ALTER TABLE [dbo].[AnalysisResults] DROP CONSTRAINT [CK_AnalysisResults_EnrichmentStatus];
GO
ALTER TABLE [dbo].[AnalysisResults] DROP COLUMN [EnrichmentNotes];
GO
ALTER TABLE [dbo].[AnalysisResults] DROP CONSTRAINT [DF_AnalysisResults_EnrichmentStatus];
GO
ALTER TABLE [dbo].[AnalysisResults] DROP COLUMN [EnrichmentStatus];
GO
```

Roll the application back **first**: the deployed code maps both columns, so dropping them under a
running deployment breaks analysis creation and retrieval.

## Verification

After applying the script, all four queries must return what is shown.

```sql
-- 1. Both columns exist, with the expected types → 2 rows
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'AnalysisResults'
  AND  COLUMN_NAME IN ('EnrichmentStatus', 'EnrichmentNotes')
ORDER  BY COLUMN_NAME;
-- expected: EnrichmentNotes  nvarchar 500 YES
--           EnrichmentStatus nvarchar  20 NO

-- 2. The check constraint exists → 1 row
SELECT name
FROM   sys.check_constraints
WHERE  name = 'CK_AnalysisResults_EnrichmentStatus';

-- 3. Every pre-existing row backfilled to 'Complete' → no other value present
SELECT EnrichmentStatus, COUNT(*) AS Rows
FROM   [dbo].[AnalysisResults]
GROUP  BY EnrichmentStatus;
-- expected immediately after the script: a single row, 'Complete'

-- 4. The constraint actually rejects an unknown value → must FAIL with a
--    constraint violation. Roll it back; this is a probe, not a change.
BEGIN TRAN;
    UPDATE TOP (1) [dbo].[AnalysisResults] SET [EnrichmentStatus] = N'Bogus';
ROLLBACK;
```

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
