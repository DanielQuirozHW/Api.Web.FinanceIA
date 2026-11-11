-- ============================================================================
-- Script  : 042_entities_add_nosisdatajson.sql
-- Purpose : Add nullable NosisDataJson + NosisDataUpdatedAt columns to
--           [dbo].[Entities], mirroring the CasfogDataJson / CasfogDataUpdatedAt pair.
-- Date    : 2026-08-24
-- Author  : Claude (assisted)
-- Notes   : Stores the NOSIS (WS.01 rest/variables, "Riesgo" VR group) snapshot on
--           the live entity, plus the UTC timestamp of its most recent refresh.
--           NosisDataJson      -> NOSIS snapshot (25 projected variables + estado)
--           NosisDataUpdatedAt -> when NosisDataJson was last refreshed
--           Both nullable, no default, no backfill: existing rows and entities
--           never refreshed since this column existed stay NULL. Written by the
--           application only — no default, no trigger. NosisDataUpdatedAt is
--           distinct from [UpdatedAt] (which moves on any entity write).
--
--           [dbo].[AnalysisResults] ALREADY has its own NosisDataJson column,
--           reserved by script 026 and mapped in AnalysisResultConfiguration; it is
--           intentionally NOT touched here. This script is only about the live entity.
--           No data migration, no DROP.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. Both columns are mapped in EntityConfiguration, so EF Core emits
--           them in every SELECT and INSERT against Entities. Deploying the code
--           against a database without them breaks entity reads AND writes with
--           "Invalid column name", and no application-level try/catch can protect
--           against it. The columns being nullable means nothing needs backfilling,
--           but the ordering is NOT optional. Restart the App Service after deploying
--           so EF's model cache is rebuilt.
--
--           Idempotent: both columns are guarded, so the script can be re-run safely.
--           Re-running never resets a value already written.
-- ============================================================================

-- ── Entities.NosisDataJson ──────────────────────────────────────────────────
-- NVARCHAR(MAX) to match the sibling snapshot blobs. The payload is a small object
-- (25 scalar variables), but sizing it the same as its siblings keeps the column set
-- uniform and removes any chance of a truncation error aborting entity creation.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'NosisDataJson'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [NosisDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column NosisDataJson added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column NosisDataJson already exists on [dbo].[Entities] — skipped.';
END
GO

-- ── Entities.NosisDataUpdatedAt ─────────────────────────────────────────────
-- Nullable: only an entity that has actually been enriched has anything to record.
-- NULL means "never refreshed", never "refreshed at the epoch".
-- DATETIME2 per the convention in docs/database/README.md — never DATETIME.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'NosisDataUpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [NosisDataUpdatedAt] DATETIME2 NULL;
    PRINT 'Column NosisDataUpdatedAt added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column NosisDataUpdatedAt already exists on [dbo].[Entities] — skipped.';
END
GO
