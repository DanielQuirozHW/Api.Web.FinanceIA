# 073 — Re-score of a balance analysis ("Reanalizar con datos actualizados")

## What changed and why

An analysis scored while the BCRA was down is persisted with `EnrichmentStatus = Degraded` and is
scored without the whole 0.35 BCRA weight. The entity's LUFE and CASFOG snapshots, which feed the
sales-drop and guarantee-arrears penalties, can also be refreshed after the analysis ran. Until now the
only way to pick any of that up was a brand-new analysis: a second Claude extraction, a second quota
unit and a second `[Analyses]` row.

`POST /api/analyses/{resultId}/rescore` re-scores the SAME analysis in place. It starts from its
already-extracted, already-persisted balance, queries the BCRA live, and reads the entity's current
LUFE / CASFOG / ARCA / NOSIS snapshots. It then recomputes the ratios, the score, the breakdown and the
Haiku narrative through `AnalysisScoringStage`, which is the same code a fresh analysis runs. It makes
no extraction call, takes no quota unit and writes no new row in `[Analyses]`.

| Change | Where |
|---|---|
| When the latest re-score ran | `[dbo].[AnalysisResults].[RescoredAt]` DATETIME2 NULL |
| Who ran it | `[dbo].[AnalysisResults].[RescoredBy]` UNIQUEIDENTIFIER NULL (Users.UserId, no FK, like `ReviewedBy`) |
| How many, and the concurrency token | `[dbo].[AnalysisResults].[RescoreCount]` INT NOT NULL `DF_AnalysisResults_RescoreCount` DEFAULT 0 |
| The history, append-only | new `[dbo].[AnalysisRescores]` |

## What a re-score overwrites, and what it never touches

**Overwritten** on `[AnalysisResults]`. These are the integration-dependent outputs:

- `RatiosJson`, which is deterministic from the balance.
- `BcraDataJson` and `MacroDataJson`.
- `AiSuggestedScore` and `AiSuggestedLevel`.
- `ScoreBreakdownJson`, written with the current `scoringAlgoVersion`.
- `ScoringNarrative`, which becomes NULL when the narrative call fails. The old narrative describes a
  different score, so it must not survive.
- The four snapshot copies: `LufeDataJson`, `FiscalDataJson`, `NosisDataJson` and `CasfogDataJson`.
- `EnrichmentStatus` and `EnrichmentNotes`.

**Never touched.** These are extraction outputs, or the decision record:

- `BalanceJson`, `RiskAlerts`, `Anomalies`, `ExecutiveSummary` and `CatalogueAlerts`.
- The `[Alerts]` rows.
- `ValidationStatus` and `ValidationNotes`.
- The AI hashes and token counts, and `GeneratedAt`.
- Every `[Analyses]` column.

> **Later exception — balance corrections.** This list holds for a plain re-score. A balance correction
> (POST `/balance-corrections`, Phase 10-F3a) is a re-score that ALSO rewrites `BalanceJson`, and since
> fix/review-gate-calm `ValidationStatus` too: `Passed` when the corrected balance's re-evaluated integrity
> passes, `Warning` otherwise (the extraction's own rule, `BalanceValidationStatus`). Both ride the same guarded
> UPDATE and transaction, and both previous values go into `PreviousValuesJson` (`balanceJson`,
> `validationStatus`). No DDL: the values are the ones `CK_AnalysisResults_ValidationStatus` already admits.

Re-running the cash-flow reconciliation would append its anomalies a second time, which is why it is
not shared. The catalogue rule engine reads only the balance and the tenant's catalogue, never
integration data, so it is left alone too.

Before the update, every overwritten column is copied as stored into
`[AnalysisRescores].[PreviousValuesJson]`, in the same transaction as the update. That makes a
re-score reversible by hand.

## Rules the endpoint enforces

- **Who may re-score.** Only a balance analysis can be re-scored, never a persona humana one, and only
  while it is `PendingReview`. It must have no `[AnalysisApprovalVotes]` row, and no `PreApprovedBy`,
  `PreApprovedAt` or `ReviewedBy`. A return to review keeps those on the row, and a signature given
  against the old score must not end up pointing at a new one.
- **Document and balance.** The analysis document must not be `Processing`, and the result must carry
  a balance.
- **Cooldown.** One re-score per analysis every `Rescore:CooldownSeconds` (default 120). The cooldown is
  read off `RescoredAt`, so it holds across instances.
- **No degradation.** If the BCRA still fails, or the entity no longer resolves, the request is aborted
  with a 503 before anything is written or spent.
- **Concurrency.** The write is `UPDATE … WHERE ResultId = @id AND RescoreCount = @expected`, run under
  REPEATABLE READ after re-checking the decision gate. If the race is lost, or a decision lands while the
  score is computing, the request gets a 409 and nothing is written.

## Deploy ordering

**RUN BEFORE DEPLOYING**, once on Financia-db, then restart the App Service. Run it on any other
business database the DDL procedure lists, as with 069–072.

EF maps the three `[AnalysisResults]` columns, and EF emits every mapped column in every SELECT and
INSERT. Publishing first therefore breaks the analysis list, the detail, the chat, the PDF export, the
decision and the pipeline with `Invalid column name`. The deploy job's `--verify-schema` step refuses
that publish.

Running the DDL first is safe for the code still running:

- `RescoredAt` and `RescoredBy` are nullable with no default.
- `RescoreCount` defaults to 0, so the old INSERT, which names none of the three columns, still writes
  a valid row.
- The old code never reads `[AnalysisRescores]`.

**Portal.** The API change is additive:

- a new `POST {id}/rescore`;
- a new `GET {id}/rescores`;
- `rescoredAt`, `rescoredBy`, `rescoredByName`, `rescoreCount`, `canRescore`, `rescoreBlockedReason`
  and `sources` on the detail.

No enum value of an existing field changes, so no Portal-first step is needed. One behaviour to know:
after a failed narrative, `scoringNarrative` can be `null` on a scored analysis.

**Persona humana (M10, no new DDL).** The same endpoint now re-evaluates an OPEN persona humana analysis
in place, and records it in this same table. No script is needed: every column it writes already exists
(072, 073, 077).

- Overwritten on `[AnalysisResults]`: `BcraDataJson`, `MacroDataJson`, `FiscalDataJson`, `LufeDataJson`,
  `AiInputHash`, `AiOutputHash`, `EnrichmentStatus`, `EnrichmentNotes`, plus `RescoredAt` / `RescoredBy` /
  `RescoreCount` as above.
- Overwritten in place on every `[AnalysisParticipants]` row of the analysis: `DocumentVersionsJson`,
  `FiltersJson`, `IndicatorsJson`, `CoverageJson`, `EvaluationVersion` and `CreatedAt` (the evaluation
  instant). `ParticipantId`, `FiadorId`, `PersonLegajoId`, `Role` and the terms are never touched, so
  `UX_AnalysisParticipants_Analysis_Legajo_Role` is never involved.
- The `[AnalysisRescores]` row has NULL scores and levels. Its `PreviousValuesJson` has the
  `PersonRescorePreviousValues` shape: `kind`, `result`, one `participants` entry per row, each with the
  raw previous strings, and a small `suggestions { previous, new }` index that the history list reads
  with `JSON_VALUE`.
- No `[Analyses]` column is written, so no quota unit is consumed.

**Numbering.** 072's header reserved "073" for the fiador columns of M10 Phase 5. That script was never
written. This one takes 073, so Phase 5 takes the next free number.

### Execution history

- Pending.

## Rollback

See the script's rollback section. Roll the application back first. Export `[AnalysisRescores]` before
dropping it, because it is the only record of what each re-scored result held before. Dropping the
three columns does not restore any previous score.
