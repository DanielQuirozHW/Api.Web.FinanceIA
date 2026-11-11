-- ============================================================================
-- Script  : 028_notes_soft_delete.sql
-- Purpose : Add soft-delete (tombstone) support to [dbo].[Notes].
--           Notes remain append-only for content — they are never edited and the
--           row is never physically removed (credit-scoring audit requirement).
--           A deleted note keeps its row and is surfaced to the UI as a tombstone
--           ("Nota eliminada"); the API blanks the Content before it leaves the BFF.
-- Date    : 2026-07-18
-- Author  : Claude (assisted)
-- Notes   : Three new columns, all nullable except IsDeleted (which defaults to 0):
--             IsDeleted  BIT              — 0 = live, 1 = soft-deleted.
--             DeletedAt  DATETIME2        — UTC timestamp of the soft-delete.
--             DeletedBy  UNIQUEIDENTIFIER — [dbo].[Users].UserId that soft-deleted.
--           DeletedBy has no FK by design — kept simple, mirrors AnalysisId which
--           also carries no FK. Authorization (own note vs Admin) is enforced in
--           the repository, tenant-scoped like every other query.
--           Idempotent: each column add is guarded against sys.columns so the
--           script can be re-run safely.
-- ============================================================================

-- ── IsDeleted ────────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notes]')
      AND  name      = N'IsDeleted'
)
BEGIN
    ALTER TABLE [dbo].[Notes]
        ADD [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_Notes_IsDeleted] DEFAULT (0);
    PRINT 'Column [dbo].[Notes].[IsDeleted] added.';
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Notes].[IsDeleted] already exists — skipped.';
END
GO

-- ── DeletedAt ────────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notes]')
      AND  name      = N'DeletedAt'
)
BEGIN
    ALTER TABLE [dbo].[Notes]
        ADD [DeletedAt] DATETIME2 NULL;
    PRINT 'Column [dbo].[Notes].[DeletedAt] added.';
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Notes].[DeletedAt] already exists — skipped.';
END
GO

-- ── DeletedBy ────────────────────────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notes]')
      AND  name      = N'DeletedBy'
)
BEGIN
    ALTER TABLE [dbo].[Notes]
        ADD [DeletedBy] UNIQUEIDENTIFIER NULL;   -- UserId who soft-deleted; no FK by design
    PRINT 'Column [dbo].[Notes].[DeletedBy] added.';
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Notes].[DeletedBy] already exists — skipped.';
END
GO
