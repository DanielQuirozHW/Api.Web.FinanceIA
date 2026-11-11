-- ============================================================================
-- Script  : 022_documents_add_source.sql
-- Purpose : Add nullable Source column to [dbo].[Documents]
-- Date    : 2026-06-15
-- Author  : Claude (assisted)
-- Notes   : Identifies the origin of a document.
--           'Lufe' for documents imported via the Lufe integration.
--           NULL for direct uploads (legacy and new).
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'Source'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [Source] NVARCHAR(50) NULL;
    PRINT 'Column Source added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column Source already exists on [dbo].[Documents] — skipped.';
END
GO
