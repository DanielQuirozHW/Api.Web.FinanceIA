-- ============================================================================
-- Script  : 026_analysisresults_add_source_snapshot_columns.sql
-- Purpose : Add external-source JSON snapshot columns to [dbo].[AnalysisResults]
--           so each analysis freezes the data context AS IT WAS at calification
--           time, independent of the live [dbo].[Entities] row (which keeps
--           changing via the per-source "consultar ahora" refresh endpoints).
-- Date    : 2026-07-16
-- Author  : Claude (assisted)
-- Notes   : Four nullable columns, written point-in-time when an analysis runs:
--           LufeDataJson   -> copy of Entities.LufeDataJson   (LUFE)
--           FiscalDataJson -> copy of Entities.FiscalDataJson (ARCA)
--           NosisDataJson  -> reserved for the future NOSIS integration
--           CasfogDataJson -> reserved for the future CASFOG integration
--           RatiosJson / BalanceJson / BcraDataJson / MacroDataJson /
--           ScoreBreakdownJson already snapshot the rest of the context; these
--           complete it. The snapshot is WRITE-ONCE at run time and is never
--           re-synced from the live entity afterwards.
--           NOSIS and CASFOG have no integration yet: the application writes
--           NULL for them today; the columns exist now only so the schema is
--           not altered again when those sources land.
--           All four are nullable, no default, no backfill: existing rows and
--           any analysis run against a null/never-refreshed entity stay NULL.
--           Written by the application only — no default, no trigger.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'LufeDataJson'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [LufeDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column LufeDataJson added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column LufeDataJson already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'FiscalDataJson'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [FiscalDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column FiscalDataJson added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column FiscalDataJson already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'NosisDataJson'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [NosisDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column NosisDataJson added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column NosisDataJson already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'CasfogDataJson'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [CasfogDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column CasfogDataJson added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column CasfogDataJson already exists on [dbo].[AnalysisResults] — skipped.';
END
GO
