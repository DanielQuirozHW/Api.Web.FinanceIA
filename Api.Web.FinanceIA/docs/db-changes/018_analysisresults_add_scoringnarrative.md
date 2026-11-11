# 018 — Add ScoringNarrative column to AnalysisResults

## What changed and why

`AnalysisResults.ExecutiveSummary` was being overwritten with the scoring narrative
when available, discarding the original financial-statements summary. A new nullable
`ScoringNarrative` column (`NVARCHAR(MAX) NULL`) is added so both texts are persisted
independently:

- **ExecutiveSummary** — financial-statements summary from document extraction.
- **ScoringNarrative** — score justification from `GenerateScoringNarrativeAsync`.

Existing rows keep their current `ExecutiveSummary` value (which may contain a scoring
narrative for historical analyses). `ScoringNarrative` stays `NULL` for them. New
analyses will populate both columns correctly going forward.

## DDL

```sql
-- ============================================================================
-- Script  : 018_analysisresults_add_scoringnarrative.sql
-- Purpose : Add nullable ScoringNarrative column to AnalysisResults
-- Date    : 2026-06-08
-- Author  : Claude (assisted)
-- Notes   : Separates scoring justification from ExecutiveSummary.
--           Existing rows keep ExecutiveSummary as-is; ScoringNarrative = NULL.
--           New analyses will populate both columns independently.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'ScoringNarrative'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [ScoringNarrative] NVARCHAR(MAX) NULL;
END
GO
```

Script location: `docs/database/018_analysisresults_add_scoringnarrative.sql`

## Pre-conditions

- Must be executed **before** deploying the commit that separates the two columns
  to production on **every** tenant database.
- The script is idempotent — safe to run multiple times.

## Rollback

The column is nullable with no default or constraint, so it can be safely dropped
without data loss:

```sql
ALTER TABLE [dbo].[AnalysisResults]
    DROP COLUMN [ScoringNarrative];
GO
```

## Verification

Run this query after applying the script. It must return exactly one row:

```sql
SELECT COLUMN_NAME
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'AnalysisResults'
  AND  COLUMN_NAME = 'ScoringNarrative';
```

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
