-- ============================================================================
-- Script  : 050_documents_add_extraction_attempts.sql
-- Purpose : Add [ExtractionAttempts], [LastExtractionFailedAt] and
--           [LastExtractionError] to [dbo].[Documents] so a balance whose
--           extraction keeps failing stops buying Claude calls.
-- Date    : 2026-08-31
-- Author  : Claude (assisted)
-- Notes   : WHY THIS COLUMN EXISTS AT ALL.
--           The monthly analysis quota counts rows in [dbo].[Analyses], and that
--           row is only written AFTER Claude answers (AnalysisController writes it
--           well past the extraction call). A trigger whose extraction fails
--           therefore costs the tenant real Claude money and moves no counter —
--           which is the correct billing answer (a failed extraction is not a
--           delivered analysis) but leaves nothing at all standing between the
--           analyst and an unlimited retry loop on the same unreadable PDF.
--
--           Nothing already in the schema can close that. Re-triggering the SAME
--           document does not insert a new row anywhere: [ProcessingStatus] flips
--           to 'Failed' and stays 'Failed', so it carries exactly one bit
--           ("failed at least once") and can never say HOW MANY times. Hence a
--           counter column. [dbo].[AuditLogs] does receive an 'Analysis.Failed'
--           row per failure, but that write is fire-and-forget and the table is
--           an audit trail, not a control input.
--
--           ExtractionAttempts     -> failed extraction attempts for this document
--           LastExtractionFailedAt -> UTC timestamp of the most recent failure
--           LastExtractionError    -> short sanitized reason code for that failure
--
--           THE BUDGET IS SPENT PER FILE, NOT PER ROW. The application sums
--           [ExtractionAttempts] across every non-deleted document of the tenant
--           sharing the same [FileHashSHA256], so re-uploading the identical bytes
--           inherits the exhausted budget instead of resetting it. That is why no
--           index is added here: the read is
--           (TenantId, FileHashSHA256, IsDeleted) and is served by the existing
--           tenant-prefixed index with a residual filter, once per trigger. If it
--           ever needs its own index, that is a script in the shape of 044/045,
--           not a change to this one.
--
--           LastExtractionError HOLDS A CODE, NEVER A MESSAGE. The application
--           writes a short discriminator ('claude_api_error', an exception type
--           name); it never writes exception text and never writes any part of a
--           Claude payload. Nothing here may carry PII or a document's contents.
--
--           ExtractionAttempts is NOT NULL with DEFAULT 0 so every existing row
--           backfills to zero with no data migration and no separate UPDATE: a
--           document that has never failed has made zero failed attempts. The two
--           companion columns are nullable — NULL means "never failed", never
--           "failed at the epoch", matching the convention in script 042.
--
--           NO CHANGE TO [ProcessingStatus]. Deliberately: this script adds no
--           new status value, so the vocabulary the Portal renders
--           (DocumentResponse.ProcessingStatus) is untouched and no CHECK
--           constraint that may exist on that column is at risk. The guard reads
--           the counter, not the status.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. All three columns are mapped in DocumentConfiguration, so EF
--           Core emits them in every SELECT and INSERT against [dbo].[Documents].
--           Deploying the code against a database without them breaks document
--           reads AND writes with "Invalid column name" — document upload, the
--           documents list and every analysis trigger — and no application-level
--           try/catch can protect against it. Restart the App Service after
--           deploying so EF's model cache is rebuilt.
--
--           Idempotent: each column is guarded on sys.columns, so the script can
--           be re-run safely. Re-running never resets a counter already written.
-- ============================================================================

-- ── Documents.ExtractionAttempts ────────────────────────────────────────────
-- NOT NULL with a named default constraint so existing rows backfill to 0 in the
-- ALTER itself. The name is explicit (DF_Documents_ExtractionAttempts) rather than
-- system-generated so a future script can drop it by name.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'ExtractionAttempts'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [ExtractionAttempts] INT NOT NULL
            CONSTRAINT [DF_Documents_ExtractionAttempts] DEFAULT (0);
    PRINT 'Column ExtractionAttempts added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column ExtractionAttempts already exists on [dbo].[Documents] — skipped.';
END
GO

-- ── Documents.LastExtractionFailedAt ────────────────────────────────────────
-- Nullable: only a document that has actually failed has anything to record.
-- NULL means "never failed", never "failed at the epoch".
-- DATETIME2 per the convention in docs/database/README.md — never DATETIME.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'LastExtractionFailedAt'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [LastExtractionFailedAt] DATETIME2 NULL;
    PRINT 'Column LastExtractionFailedAt added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column LastExtractionFailedAt already exists on [dbo].[Documents] — skipped.';
END
GO

-- ── Documents.LastExtractionError ───────────────────────────────────────────
-- A short sanitized reason CODE, not a message: 'claude_api_error', an exception
-- type name. NVARCHAR(200) is generous for that and small enough that nobody is
-- tempted to store a stack trace, an exception message or a Claude payload here.
-- Nullable for the same reason as the timestamp above.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'LastExtractionError'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [LastExtractionError] NVARCHAR(200) NULL;
    PRINT 'Column LastExtractionError added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column LastExtractionError already exists on [dbo].[Documents] — skipped.';
END
GO
