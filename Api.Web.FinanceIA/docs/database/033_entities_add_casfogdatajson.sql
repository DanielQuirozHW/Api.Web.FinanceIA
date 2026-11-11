-- ============================================================================
-- Script  : 033_entities_add_casfogdatajson.sql
-- Purpose : Add nullable CasfogDataJson + CasfogDataUpdatedAt columns to
--           [dbo].[Entities], mirroring the BcraDataJson / BcraDataUpdatedAt pair.
-- Date    : 2026-07-29
-- Author  : Claude (assisted)
-- Notes   : Stores the CASFOG (SGR central de deudores / garantías) snapshot on
--           the live entity, plus the UTC timestamp of its most recent refresh.
--           CasfogDataJson   -> CASFOG snapshot (Entrega 1: client + persistence)
--           CasfogDataUpdatedAt -> when CasfogDataJson was last refreshed
--           Both nullable, no default, no backfill: existing rows and entities
--           never refreshed since this column existed stay NULL. Written by the
--           application only — no default, no trigger. CasfogDataUpdatedAt is
--           distinct from [UpdatedAt] (which moves on any entity write).
--
--           [dbo].[AnalysisResults] already has its own CasfogDataJson column
--           (reserved in script 026); it is intentionally NOT touched here.
--           No data migration, no DROP.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'CasfogDataJson'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [CasfogDataJson] NVARCHAR(MAX) NULL;
    PRINT 'Column CasfogDataJson added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column CasfogDataJson already exists on [dbo].[Entities] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'CasfogDataUpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [CasfogDataUpdatedAt] DATETIME2 NULL;
    PRINT 'Column CasfogDataUpdatedAt added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column CasfogDataUpdatedAt already exists on [dbo].[Entities] — skipped.';
END
GO
