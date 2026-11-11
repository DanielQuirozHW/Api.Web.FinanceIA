# 050 — A balance that cannot be read stops buying Claude calls

## What changed and why

The monthly analysis quota counts rows in `[dbo].[Analyses]`, and `AnalysisController.TriggerAsync`
writes that row only **after** Claude answers. So a trigger whose extraction fails returns 502
having spent real Claude money, and moves no counter.

Half of that is correct and stays: a failed extraction is not a delivered analysis, so it must not
consume one of the tenant's 50/100/200 monthly análisis. The `[BillingQuota(Analysis)]` filter on
`TriggerAsync` already refuses a tenant that is at its limit *before* the document flips to
`Processing` and before the Claude call, so the tenant at 50/50 is not the problem.

The other half is a leak. Nothing stopped the analyst — or a script — from pressing the same trigger
again, and again, on the same unreadable PDF. Each press is a fresh billable extraction, and it is
not one API call: `ClaudeService` re-sends the full request up to `MaxExtractionAttempts` on a
timeout (its own log says "this incurs another billable Claude call"), and a confidence below
`Anthropic:ExtractionConfidenceFallbackThreshold` runs a **second** full pass with high-DPI images.
A tenant at 49/50 could burn an unbounded amount against one remaining free slot.

**050 gives the failure somewhere to be remembered**, so the trigger can refuse the third attempt
before paying for it.

### Why a new column, rather than a column that already exists

Re-triggering the **same** document inserts no new row anywhere. `[ProcessingStatus]` flips to
`'Failed'` and stays `'Failed'`, so it carries exactly one bit — "failed at least once" — and can
never say how many times. `[FileHashSHA256]` identifies the file but counts nothing.
`[dbo].[AuditLogs]` does get an `Analysis.Failed` row per failure, but that write is fire-and-forget
(a lost write undercounts) and an audit trail is not a control input. Counting attempts therefore
needs a counter.

### Why not a new `ProcessingStatus` value

`'ExtractionFailed'` would have been schema-free only if `[dbo].[Documents]` carries no `CHECK`
constraint on that column — and the base `Documents` DDL is not in this repository (only the alters
004 / 005 / 022 / 037), so that cannot be established from here. It would also reach the Portal
through `DocumentResponse.ProcessingStatus`, making this a coordinated two-repo release. The status
vocabulary is left exactly as it was; the guard reads the counter instead.

## DDL

Script location: `docs/database/050_documents_add_extraction_attempts.sql`

| Object | Type |
|---|---|
| `[dbo].[Documents].[ExtractionAttempts]` | column, `INT NOT NULL DEFAULT (0)` |
| `[dbo].[Documents].[LastExtractionFailedAt]` | column, `DATETIME2 NULL` |
| `[dbo].[Documents].[LastExtractionError]` | column, `NVARCHAR(200) NULL` |
| `[DF_Documents_ExtractionAttempts]` | default constraint, named explicitly |

`ExtractionAttempts` is `NOT NULL` with a default so existing rows backfill to `0` inside the
`ALTER` itself — a document that never failed has made zero failed attempts, and no separate
`UPDATE` is needed. The two companion columns are nullable: `NULL` means "never failed", never
"failed at the epoch", matching script 042's convention.

`LastExtractionError` holds a short sanitized **code** (`claude_api_error`, an exception type name).
The application never writes exception text and never writes any part of a Claude payload — nothing
in these columns may carry PII or a document's contents.

**No index is added.** The application sums `ExtractionAttempts` over
`(TenantId, FileHashSHA256, IsDeleted)` once per trigger; that seeks on the tenant prefix and
residual-filters the rest, which is fine at design-partner volume. If it ever is not, the fix is an
index script in the shape of 044/045, not a change to this one.

## How the application uses it

The budget is spent **per file, not per row**. `IDocumentRepository.SumExtractionAttemptsByHashAsync`
sums the counter across every non-deleted document of the tenant sharing the same `FileHashSHA256`,
so re-uploading the identical bytes inherits the exhausted budget instead of resetting it — that
re-upload was the obvious way around a per-document guard.

`ReanalysisPolicy` compares that sum against `Billing:MaxExtractionAttemptsPerDocument` (default
**2**) and returns `DenyExtractionBudgetExhausted`, which `TriggerAsync` maps to `409` — evaluated
**before** the `Processing` flip and therefore before any Claude call. Two is deliberate: one
transient Claude blip is absorbed and the analyst can retry once; the second failure closes the
door until a genuinely different file is uploaded.

The check is ordered **last** in `ReanalysisPolicy`, below both the `Processing` lock and the
`Completed` arm. A completed document is proof that we did read that balance, so re-analysing it
after a rejection is never refused for budget; and a run genuinely in flight reports its own,
more urgent conflict rather than "budget spent".

Every failed extraction consumes budget, whatever the cause. A timeout costs money too (the pass is
re-sent), so exempting transient faults would have left an outage able to re-spend without limit.
Faults *before* the Claude call — a storage read, the PDF unwrap — cost nothing and do **not**
consume budget; `TriggerAsync` tells them apart with its existing `serverOwnsWork` flag.

Reading the sum is fault-isolated and **fails open**: if the count cannot be read the trigger
proceeds, matching the policy `BillingQuotaFilter` already states for the quota gate itself. Setting
the limit to `0` or a negative value disables the guard entirely.

## Pre-conditions

- **Execute 050 BEFORE deploying the matching commit**, on **every** tenant database. Hard ordering
  requirement. All three columns are mapped in `DocumentConfiguration`, so EF Core emits them in
  every `SELECT` and `INSERT` against `Documents`; against a database without them, document upload,
  the documents list and every analysis trigger fail with "Invalid column name", and no
  application-level try/catch can protect that.
- **Restart the App Service after deploying**, so EF's model cache is rebuilt.
- No Portal change is required — no response shape changes and no enum value is added. The new
  refusal is a `409` with a `ProblemDetails` body, which the Portal already renders.
- The script is idempotent and safe to re-run; re-running never resets a counter already written.

## Rollback

Roll the **application back first** — the deployed code maps all three columns, so dropping them
under a running deployment breaks document creation and retrieval.

```sql
ALTER TABLE [dbo].[Documents] DROP COLUMN [LastExtractionError];
GO
ALTER TABLE [dbo].[Documents] DROP COLUMN [LastExtractionFailedAt];
GO
ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [DF_Documents_ExtractionAttempts];
GO
ALTER TABLE [dbo].[Documents] DROP COLUMN [ExtractionAttempts];
GO
```

Dropping the columns loses the recorded attempt history, which is the point of the feature: after a
rollback every document is retriable from zero again.
