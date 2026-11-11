-- ============================================================================
-- Script  : 025_entities_add_source_updatedat_columns.sql
-- Purpose : Add per-source last-refresh timestamp columns to [dbo].[Entities],
--           one for each external-source JSON snapshot column.
-- Date    : 2026-07-15
-- Author  : Claude (assisted)
-- Notes   : Records when each source snapshot was last refreshed:
--           FiscalDataUpdatedAt -> FiscalDataJson (ARCA)
--           BcraDataUpdatedAt   -> BcraDataJson   (BCRA)
--           LufeDataUpdatedAt   -> LufeDataJson   (LUFE)
--           All three are nullable and remain NULL for existing rows and for
--           entities whose snapshot has never been refreshed via the
--           POST /api/entities/{id}/refresh-{arca|bcra|lufe} endpoints. These
--           are distinct from [UpdatedAt], which moves on any entity write and
--           says nothing about a specific source. No backfill: the capture time
--           of existing snapshots is not recoverable, and NULL correctly means
--           "never refreshed since this column existed".
--           Written by the application only — no default, no trigger.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'FiscalDataUpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [FiscalDataUpdatedAt] DATETIME2 NULL;
    PRINT 'Column FiscalDataUpdatedAt added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column FiscalDataUpdatedAt already exists on [dbo].[Entities] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'BcraDataUpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [BcraDataUpdatedAt] DATETIME2 NULL;
    PRINT 'Column BcraDataUpdatedAt added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column BcraDataUpdatedAt already exists on [dbo].[Entities] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'LufeDataUpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [LufeDataUpdatedAt] DATETIME2 NULL;
    PRINT 'Column LufeDataUpdatedAt added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column LufeDataUpdatedAt already exists on [dbo].[Entities] — skipped.';
END
GO
