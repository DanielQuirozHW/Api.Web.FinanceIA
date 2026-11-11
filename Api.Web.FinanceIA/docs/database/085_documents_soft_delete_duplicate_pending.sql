-- DATA FIX, no schema change
-- ============================================================================
-- Script  : 085_documents_soft_delete_duplicate_pending.sql
-- Purpose : Soft-delete the orphaned "Pending" company documents a DOUBLE
--           SUBMIT of the upload left behind (QA Q32): two [dbo].[Documents]
--           rows for the same file a few hundred milliseconds apart, one of
--           which was analysed while the other stayed "Pending" forever.
-- Date    : 2026-11-08
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS IS A DATA SCRIPT (DML), NOT A SCHEMA SCRIPT — like 066, 068,
--           082 and 084. It changes no object, so the API does NOT depend on it
--           and the --verify-schema gate neither needs nor sees it. It is
--           OPTIONAL, NOT A DEPLOY BLOCKER, and SAFE TO RUN BEFORE OR AFTER THE
--           DEPLOY: the new code answers a repeat upload of the same file within
--           120 s with the first document (DocumentsController.
--           DuplicateUploadWindow), so no NEW duplicates appear once it is out;
--           this script only cleans the old ones. NO App Service restart.
--
--           RUN IT ONCE IN EACH Financia (tenant business) DATABASE. It covers
--           every TenantId found in the database it runs in (the TenantId is in
--           every result set so the DBA sees whose rows they are).
--
--           WHAT IT TOUCHES. A document is soft-deleted when ALL of these hold:
--             - it is a COMPANY document: PersonLegajoId IS NULL (a persona
--               humana legajo document is never touched — its own unique index
--               UX_Documents_Legajo_Hash already prevents duplicates);
--             - ProcessingStatus = 'Pending' and IsDeleted = 0;
--             - no [dbo].[Analyses] row references it;
--             - it has a non-deleted SIBLING — same TenantId, EntityId,
--               DocumentType and FileHashSHA256 (never a blank hash), uploaded
--               within 120 seconds of it — that RANKS ABOVE it.
--           Ranking (the row to KEEP comes first): has an analysis, then is not
--           Pending, then was uploaded first, then the smaller DocumentId. The
--           top-ranked row of every group is therefore always kept, and a row
--           that has an analysis or is not Pending is never touched.
--
--           HOW. IsDeleted = 1 and DeletedAt = SYSUTCDATETIME() — the same soft
--           delete DELETE /api/documents/{id} performs. NEVER a physical DELETE.
--           The blob stays in storage, as it does for any soft-deleted document.
--
--           TWO STEPS, ONE FILE. With @Apply = 0 (the default) it only READS:
--             Result set 1 — the documents it WOULD soft-delete: TenantId,
--             DocumentId, EntityId, DocumentType, UploadedAt, and the DocumentId
--             / status / analysis flag of the sibling that outranks it.
--           Review it, then set @Apply = 1 and run again: the same rows are
--           recomputed and written in ONE transaction (XACT_ABORT ON), and the
--           number of rows written is printed.
--
--           ⚠ It BYPASSES the application audit trail (no AuditLogs row).
--           Record the run (database, date, who, rows) in README.md's table.
--
--           IDEMPOTENT: a soft-deleted row is no longer a candidate nor a
--           sibling, so a second run finds nothing and writes nothing.
--           CONCURRENCY: the UPDATE re-checks Pending / not deleted / no
--           analysis at write time, so a document analysed between the preview
--           and the apply is skipped, never deleted.
--
--           Rationale: docs/db-changes/085_documents_soft_delete_duplicate_pending.md
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 0 = preview only (default). 1 = soft-delete the rows listed in result set 1.
DECLARE @Apply BIT = 0;

-- Same window as DocumentsController.DuplicateUploadWindow.
DECLARE @WindowSeconds INT = 120;

IF OBJECT_ID(N'tempdb..#Doomed') IS NOT NULL DROP TABLE #Doomed;

CREATE TABLE #Doomed
(
    TenantId              NVARCHAR(100)    NOT NULL,
    DocumentId            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    EntityId              UNIQUEIDENTIFIER NOT NULL,
    DocumentType          NVARCHAR(50)     NOT NULL,
    UploadedAt            DATETIME2        NOT NULL,
    KeptDocumentId        UNIQUEIDENTIFIER NOT NULL,
    KeptProcessingStatus  NVARCHAR(50)     NOT NULL,
    KeptHasAnalysis       BIT              NOT NULL
);

;WITH Live AS
(
    -- Non-deleted company documents with a hash, and the two ranking facts.
    SELECT d.TenantId,
           d.DocumentId,
           d.EntityId,
           d.DocumentType,
           d.FileHashSHA256,
           d.UploadedAt,
           d.ProcessingStatus,
           CASE WHEN EXISTS (SELECT 1 FROM dbo.Analyses a
                             WHERE a.TenantId = d.TenantId AND a.DocumentId = d.DocumentId)
                THEN 1 ELSE 0 END                                        AS HasAnalysis,
           CASE WHEN d.ProcessingStatus = N'Pending' THEN 0 ELSE 1 END   AS NotPending
    FROM   dbo.Documents d
    WHERE  d.IsDeleted = 0
      AND  d.PersonLegajoId IS NULL
      AND  LEN(LTRIM(RTRIM(d.FileHashSHA256))) > 0
),
Pairs AS
(
    -- Every candidate paired with each sibling that outranks it.
    SELECT c.TenantId, c.DocumentId, c.EntityId, c.DocumentType, c.UploadedAt,
           s.DocumentId       AS KeptDocumentId,
           s.ProcessingStatus AS KeptProcessingStatus,
           s.HasAnalysis      AS KeptHasAnalysis,
           ROW_NUMBER() OVER (
               PARTITION BY c.DocumentId
               ORDER BY s.HasAnalysis DESC, s.NotPending DESC, s.UploadedAt ASC, s.DocumentId ASC) AS Pick
    FROM   Live c
    JOIN   Live s
      ON   s.TenantId       = c.TenantId
     AND   s.EntityId       = c.EntityId
     AND   s.DocumentType   = c.DocumentType
     AND   s.FileHashSHA256 = c.FileHashSHA256
     AND   s.DocumentId    <> c.DocumentId
     AND   ABS(DATEDIFF(SECOND, c.UploadedAt, s.UploadedAt)) <= @WindowSeconds
    WHERE  c.ProcessingStatus = N'Pending'
      AND  c.HasAnalysis = 0
      AND  (    s.HasAnalysis > c.HasAnalysis
            OR (s.HasAnalysis = c.HasAnalysis AND s.NotPending > c.NotPending)
            OR (s.HasAnalysis = c.HasAnalysis AND s.NotPending = c.NotPending AND s.UploadedAt < c.UploadedAt)
            OR (s.HasAnalysis = c.HasAnalysis AND s.NotPending = c.NotPending AND s.UploadedAt = c.UploadedAt
                AND s.DocumentId < c.DocumentId))
)
INSERT INTO #Doomed (TenantId, DocumentId, EntityId, DocumentType, UploadedAt,
                     KeptDocumentId, KeptProcessingStatus, KeptHasAnalysis)
SELECT TenantId, DocumentId, EntityId, DocumentType, UploadedAt,
       KeptDocumentId, KeptProcessingStatus, KeptHasAnalysis
FROM   Pairs
WHERE  Pick = 1;

-- ── Result set 1: documents this script soft-deletes, and the sibling kept ──
SELECT x.TenantId, x.DocumentId, x.EntityId, x.DocumentType, x.UploadedAt,
       x.KeptDocumentId, x.KeptProcessingStatus, x.KeptHasAnalysis
FROM   #Doomed x
ORDER  BY x.TenantId, x.EntityId, x.UploadedAt;

IF @Apply = 1
BEGIN
    BEGIN TRANSACTION;

    UPDATE d
    SET    d.IsDeleted = 1,
           d.DeletedAt = SYSUTCDATETIME()
    FROM   dbo.Documents d
    JOIN   #Doomed x
      ON   x.DocumentId = d.DocumentId
     AND   x.TenantId   = d.TenantId
    WHERE  d.IsDeleted = 0                       -- unchanged since previewed
      AND  d.ProcessingStatus = N'Pending'
      AND  d.PersonLegajoId IS NULL
      AND  NOT EXISTS (SELECT 1 FROM dbo.Analyses a
                       WHERE a.TenantId = d.TenantId AND a.DocumentId = d.DocumentId);

    DECLARE @Updated INT = @@ROWCOUNT;

    COMMIT TRANSACTION;

    PRINT CONCAT(N'085: ', @Updated, N' duplicate Pending document(s) soft-deleted.');
END
ELSE
BEGIN
    PRINT N'085: preview only (@Apply = 0) — nothing was written. Review result set 1, then set @Apply = 1 and run again.';
END;

DROP TABLE #Doomed;
