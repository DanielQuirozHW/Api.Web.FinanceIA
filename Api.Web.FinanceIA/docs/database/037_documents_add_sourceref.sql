-- ============================================================================
-- Script  : 037_documents_add_sourceref.sql
-- Purpose : Add nullable SourceRef column to [dbo].[Documents]
-- Date    : 2026-08-12
-- Author  : Claude (assisted)
-- Notes   : Durable link back to the exact LUFE listing entry a document was
--           imported from. Stores the source LUFE document's URL — the only field
--           LUFE exposes that distinguishes two entries sharing the same Nombre and
--           the same Periodo (e.g. two "Presentación Única de Balances" for 2025).
--           Written only by the LUFE import (EntitiesController.ImportLufeDocumentAsync);
--           NULL for direct uploads and for the pre-existing rows imported before this
--           column existed.
--
--           NULLABLE PERMANENTLY — DO NOT change to NOT NULL and DO NOT backfill.
--           The link cannot be reconstructed for existing rows (the source URL was
--           never persisted), so a guessed backfill would be a false claim. Legacy
--           rows stay NULL by design; the Portal falls back to its filename/year
--           heuristic only when SourceRef is NULL.
--
--           Length is NVARCHAR(2048): the value is a URL, not a 50-char code. A value
--           longer than the column would raise a truncation error on INSERT and abort
--           the import — importing must never fail because of this best-effort link,
--           so the column is sized to the conventional maximum URL length.
--
--           Idempotent: the column is guarded so the script can be re-run safely.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'SourceRef'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [SourceRef] NVARCHAR(2048) NULL;
    PRINT 'Column SourceRef added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column SourceRef already exists on [dbo].[Documents] — skipped.';
END
GO
