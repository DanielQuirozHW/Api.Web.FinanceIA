-- ============================================================================
-- Script  : 023_entities_add_bcradatajson.sql
-- Purpose : Add nullable BcraDataJson column to [dbo].[Entities]
-- Date    : 2026-06-15
-- Author  : Claude (assisted)
-- Notes   : Stores the BCRA (Central de Deudores) snapshot captured at
--           entity registration time. NULL for entities created before this
--           migration or when all BCRA calls fail.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'BcraDataJson'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [BcraDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column BcraDataJson added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column BcraDataJson already exists on [dbo].[Entities] — skipped.';
END
GO
