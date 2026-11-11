-- ============================================================================
-- Script  : 070_legajo_documents_and_extractions.sql
-- Purpose : Let a persona humana's LEGAJO (DDL 069) receive typed documents,
--           record the person's data-processing authorization, and keep one
--           immutable row per model run over those documents (M10, Phase 2a):
--             - widen CK_Documents_DocumentType with the ten legajo types and
--               CK_Documents_PdfType with 'Mixed';
--             - add to [dbo].[Documents]: PersonLegajoId (+ FK + filtered
--               index + filtered UNIQUE (PersonLegajoId, FileHashSHA256)
--               UX_Documents_Legajo_Hash), PeriodEnd, EngagementType (+ CHECK),
--               SupersedesDocumentId (+ self FK), HolderCuit, LegajoStatus
--               (+ CHECK + owner CHECK), LegajoStatusUpdatedAt,
--               PageQualityJson;
--             - add the consent triple to [dbo].[PersonLegajos]:
--               ConsentRecordedAt, ConsentDocumentId (+ FK), ConsentRecordedBy;
--             - create [dbo].[LegajoExtractions] and [dbo].[LegajoCorrections];
--             - add DocumentId to [dbo].[ApiCallLogs];
--             - widen the out-of-band CK_ApiCallLogs_Operation, where one
--               exists, to admit the two legajo operations.
-- Date    : 2026-09-28
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, once on Financia-db, then restart the App
--           Service. The API binds ONE static FinanciaDefault connection
--           (Financia-db) and separates tenants by the TenantId column; if the
--           DDL procedure lists further business databases, run it on each of
--           them too, as with 069. Nothing below is scoped to one tenant.
--           EF maps every new column and both new tables, so publishing first
--           breaks every SELECT and INSERT against [dbo].[Documents],
--           [dbo].[PersonLegajos] and [dbo].[ApiCallLogs] with "Invalid column
--           name": the document list, every upload, every analysis (it reads its
--           document), the entity create of a persona humana and every Claude
--           cost row. --verify-schema catches exactly that before the publish.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. Every new column is
--           nullable, every widened CHECK is a superset of the old one, and
--           the old code writes none of the new columns, so its INSERTs satisfy
--           every CHECK below (LegajoStatus NULL satisfies the owner rule).
--
--           SET OPTIONS, THE FIRST BATCH. Two indexes below are FILTERED
--           (IX_Documents_TenantId_PersonLegajoId, UX_LegajoExtractions_Current).
--           SQL Server refuses to create one, and refuses any INSERT, UPDATE or
--           DELETE on a table that has one, unless ANSI_NULLS and
--           QUOTED_IDENTIFIER are ON (error 1934). 001 set both database
--           defaults OFF. Run in SSMS / Azure Data Studio or with sqlcmd -I;
--           the SETs at the top cover a session that did not. The application
--           is unaffected (SqlClient connects with both ON).
--           ⚠ From now on hand-written DML on [dbo].[Documents] from a session
--           with either option OFF fails with 1934, as it already does on
--           [Entities] since 069.
--
--           THE CHECK WIDENINGS (sections 1 and 7). There is no ALTER for a CHECK's
--           expression, so each is dropped and recreated (060's pattern). This
--           script does not trust the NAME alone to find the old constraint:
--           001 was exported from a live database, and an environment rebuilt
--           by hand may carry the same rule under a system-generated name. It
--           drops EVERY check constraint that is BOUND TO THE COLUMN
--           (sys.check_constraints.parent_column_id = that column), whatever its
--           name and definition, plus any constraint that holds the canonical
--           name, and then recreates the rule under the canonical name.
--           TABLE-LEVEL checks (parent_column_id = 0) are NEVER touched, so the
--           two-column CK_Documents_LegajoStatus_Owner this script adds is safe
--           from the loop on a re-run. Drop and recreate run in ONE transaction
--           under XACT_ABORT: if WITH CHECK finds a row outside the new list
--           the drop rolls back too, and the old rule stays in force (pre-check
--           1 shows such a row before you start).
--
--           THE FK CYCLE. [Documents].PersonLegajoId → [PersonLegajos] and
--           [PersonLegajos].ConsentDocumentId → [Documents] reference each
--           other. Both columns are NULLABLE and both FKs are NO ACTION, so
--           either row can exist first; the API never writes both in one
--           statement (a document is stored first, the consent is recorded
--           against it in a later request). The EF model maps neither FK on
--           purpose — see DocumentConfiguration / PersonLegajoConfiguration.
--           Deleting a document is a SOFT delete (IsDeleted), so the consent FK
--           never blocks it; a HARD delete of the consent document by hand must
--           clear ConsentDocumentId first.
--
--           THE NEW TABLES.
--             - [LegajoExtractions]: one IMMUTABLE row per model run over a
--               legajo document: 'Classification' in Phase 2a, 'Extraction' in
--               2b. Only IsCurrent (swapped when a newer run of the same Kind
--               lands) and the 2b review stamp ever change.
--               UX_LegajoExtractions_Current (filtered, IsCurrent = 1) admits
--               ONE current run per (DocumentId, Kind). IsCurrent defaults to 0
--               so a hand-written row never steals the slot. CreatedBy NULL
--               means the background worker ran it.
--             - [LegajoCorrections]: 2b's append-only analyst corrections, one
--               row per corrected field. Rows are NEVER updated or deleted. A
--               reason of at least five non-blank characters is enforced here.
--               Created now so Phase 2b ships without DDL.
--           Neither table is written by the old code; they are empty after
--           this script.
--
--           [ApiCallLogs].DocumentId: no FK, like AnalysisId — a log row must
--           never block a document operation. The legajo calls log
--           Service = 'Claude' (admitted since 001) and Operation =
--           'LegajoClassification' or 'LegajoExtraction'.
--
--           [ApiCallLogs].[Operation] AND CK_ApiCallLogs_Operation (section 7,
--           the LAST batch). No numbered script ever created a CHECK on
--           [Operation] (that column predates this folder), but Financia-db
--           carries one made OUT OF BAND, and it rejected both legajo values.
--           It also admits 'DynamicsSync' and 'BcraLookup', which no code in
--           this repo writes — another application may — so section 7 widens
--           it to a SUPERSET: those legacy values, every value in
--           ApiCallLogOperations.Known, and NULL. It uses section 1's drop loop
--           (column-bound checks plus the canonical name, one transaction,
--           WITH CHECK). A database with NO check on [Operation] is left
--           without one: section 7 creates nothing there and PRINTs so.
--           Financia-db history (2026-09-29): the first run applied sections
--           1-6 and stopped on the old section 7, a RAISERROR guard; the DBA
--           then recreated CK_ApiCallLogs_Operation by hand with exactly this
--           list, so a re-run there only rewrites it with the same list.
--
--           ⚠ PII. [Documents].HolderCuit is the CUIT the classification READ,
--           which for a blocked document is a THIRD PARTY's.
--           [LegajoExtractions].ResultJson is what the model read from a
--           person's paperwork. Treat both — and every export in the rollback —
--           as personal data. The API never returns HolderCuit (only whether it
--           matches) and never logs either column.
--
--           CHECK CONSTRAINTS here follow 069: WITH CHECK, each list mirrored by
--           a constants class the tests pin to this script (DocumentTypes,
--           PdfTypes, EngagementTypes, LegajoStatuses, LegajoExtractionKinds,
--           LegajoReadModes, LegajoExtractionStatuses).
--
--           TRANSACTIONS: three (the CHECK swaps), for the reason given above.
--           Every other guarded block is one atomic statement.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run rewrites the widened CHECKs (two, or three
--           where [Operation] has one) with the same lists and changes no row.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. Every live value fits the widened lists. Expected: 0 rows each. A row here
--    would make the WITH CHECK recreate fail; section 1 would then roll back and
--    leave the old rule in place. Resolve it with the DBA first.
--
--    SELECT [DocumentType], COUNT(*) AS Documents
--    FROM   [dbo].[Documents]
--    WHERE  [DocumentType] NOT IN (N'Balance', N'Complemento', N'Certificado', N'Otro')
--    GROUP  BY [DocumentType];
--
--    SELECT [PdfType], COUNT(*) AS Documents
--    FROM   [dbo].[Documents]
--    WHERE  [PdfType] IS NOT NULL AND [PdfType] NOT IN (N'Scanned', N'Native')
--    GROUP  BY [PdfType];
--
-- 2. The check constraints section 1 will drop and recreate: every one bound to
--    [DocumentType] or [PdfType]. Expected: CK_Documents_DocumentType and
--    CK_Documents_PdfType, one each. Any other name is dropped too (see header).
--
--    SELECT cc.name, c.name AS ColumnName, cc.definition
--    FROM   sys.check_constraints cc
--    JOIN   sys.columns c ON c.object_id = cc.parent_object_id
--                        AND c.column_id = cc.parent_column_id
--    WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
--      AND  c.name IN (N'DocumentType', N'PdfType');
--
-- 3. The check constraints section 7 will drop and recreate: every one bound to
--    [ApiCallLogs].[Operation], plus any named CK_ApiCallLogs_Operation.
--    Expected: 0 rows (section 7 then creates nothing), or the out-of-band
--    CK_ApiCallLogs_Operation (Financia-db). Any other name is dropped too.
--
--    SELECT cc.name, c.name AS ColumnName, cc.definition
--    FROM   sys.check_constraints cc
--    LEFT   JOIN sys.columns c ON c.object_id = cc.parent_object_id
--                             AND c.column_id = cc.parent_column_id
--    WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
--      AND  (c.name = N'Operation' OR cc.name = N'CK_ApiCallLogs_Operation');
--
--    Where that returns a row, every live value must fit the widened list.
--    Expected: 0 rows. A row here makes section 7's WITH CHECK fail; it rolls
--    back and the old rule stays in force. Resolve it with the DBA first.
--
--    SELECT [Operation], COUNT(*) AS Calls
--    FROM   [dbo].[ApiCallLogs]
--    WHERE  [Operation] IS NOT NULL
--      AND  [Operation] NOT IN (N'DynamicsSync', N'BcraLookup', N'Extraction', N'Narrative',
--                               N'Chat', N'LegajoClassification', N'LegajoExtraction')
--    GROUP  BY [Operation];
--
-- 4. No file is stored twice in one legajo. Expected: 0 rows (no legajo document
--    exists before this script, so this can only matter on a RE-run). A row here
--    makes UX_Documents_Legajo_Hash fail to build: soft-delete the duplicates with
--    the DBA, keeping the oldest, then re-run.
--    On a FIRST run the PersonLegajoId column does not exist yet and this query
--    fails to compile: skip it then. It matters on a re-run.
--
--    SELECT PersonLegajoId, FileHashSHA256, COUNT(*) AS Copies
--    FROM   [dbo].[Documents]
--    WHERE  PersonLegajoId IS NOT NULL AND IsDeleted = 0
--    GROUP  BY PersonLegajoId, FileHashSHA256
--    HAVING COUNT(*) > 1;
-- ============================================================================

-- ── 0. Session options (filtered indexes; see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. Widen the two CHECKs on [dbo].[Documents] ────────────────────────────

-- (a) CK_Documents_DocumentType: the four 001 types + the ten legajo types.
--     The code's list is DocumentTypes.Known; a test pins it to this one.
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @DropDocumentType NVARCHAR(MAX) = N'';

SELECT @DropDocumentType = @DropDocumentType
       + N'ALTER TABLE [dbo].[Documents] DROP CONSTRAINT ' + QUOTENAME(cc.name) + N'; '
       + N'PRINT N''Check ' + REPLACE(cc.name, N'''', N'''''') + N' dropped from [dbo].[Documents].[DocumentType].''; '
FROM   sys.check_constraints cc
LEFT   JOIN sys.columns c
       ON  c.object_id = cc.parent_object_id
       AND c.column_id = cc.parent_column_id
WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
  AND  (c.name = N'DocumentType'                       -- column-bound only: parent_column_id <> 0
        OR cc.name = N'CK_Documents_DocumentType');    -- the canonical name, wherever it is bound

IF @DropDocumentType = N''
    PRINT 'No check constraint on [dbo].[Documents].[DocumentType] — nothing to drop.';
ELSE
    EXEC sys.sp_executesql @DropDocumentType;

ALTER TABLE [dbo].[Documents] WITH CHECK
    ADD CONSTRAINT [CK_Documents_DocumentType]
    CHECK ([DocumentType] IN (N'Balance', N'Complemento', N'Certificado', N'Otro',
                              N'ManifestacionBienes', N'BienesPersonales', N'Reibp', N'Ganancias',
                              N'CertificacionIngresos', N'DdjjIva', N'DdjjIibb', N'Monotributo',
                              N'FlujoProyectado', N'AutorizacionDatos'));

COMMIT TRANSACTION;

PRINT 'Constraint CK_Documents_DocumentType recreated with the ten legajo types.';
GO

-- (b) CK_Documents_PdfType: Scanned, Native + Mixed. The code's list is PdfTypes.Known.
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @DropPdfType NVARCHAR(MAX) = N'';

SELECT @DropPdfType = @DropPdfType
       + N'ALTER TABLE [dbo].[Documents] DROP CONSTRAINT ' + QUOTENAME(cc.name) + N'; '
       + N'PRINT N''Check ' + REPLACE(cc.name, N'''', N'''''') + N' dropped from [dbo].[Documents].[PdfType].''; '
FROM   sys.check_constraints cc
LEFT   JOIN sys.columns c
       ON  c.object_id = cc.parent_object_id
       AND c.column_id = cc.parent_column_id
WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
  AND  (c.name = N'PdfType'                            -- column-bound only: parent_column_id <> 0
        OR cc.name = N'CK_Documents_PdfType');         -- the canonical name, wherever it is bound

IF @DropPdfType = N''
    PRINT 'No check constraint on [dbo].[Documents].[PdfType] — nothing to drop.';
ELSE
    EXEC sys.sp_executesql @DropPdfType;

ALTER TABLE [dbo].[Documents] WITH CHECK
    ADD CONSTRAINT [CK_Documents_PdfType]
    CHECK ([PdfType] IS NULL OR [PdfType] IN (N'Scanned', N'Native', N'Mixed'));

COMMIT TRANSACTION;

PRINT 'Constraint CK_Documents_PdfType recreated with Mixed allowed.';
GO

-- ── 2. [dbo].[Documents]: the legajo columns ────────────────────────────────
-- One object per batch: a CHECK, FK or index never sits in the batch that adds
-- its column. Every column is NULL on every existing row and on every row the
-- legacy upload and the LUFE import write.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'PersonLegajoId'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [PersonLegajoId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column PersonLegajoId added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column PersonLegajoId already exists on [dbo].[Documents] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Documents_PersonLegajos]')
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD CONSTRAINT [FK_Documents_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]);
    PRINT 'Foreign key [FK_Documents_PersonLegajos] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Documents_PersonLegajos] already exists — skipped.';
END
GO

-- Serves "every document of this legajo". Filtered: every company document holds NULL.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'IX_Documents_TenantId_PersonLegajoId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Documents_TenantId_PersonLegajoId]
        ON [dbo].[Documents] ([TenantId] ASC, [PersonLegajoId] ASC)
        WHERE [PersonLegajoId] IS NOT NULL;
    PRINT 'Index [IX_Documents_TenantId_PersonLegajoId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Documents_TenantId_PersonLegajoId] already exists — skipped.';
END
GO

-- The same file at most ONCE per legajo among live rows. The upload checks first
-- and answers 409; this index is the database side of that rule, so two requests
-- racing (a double-click) cannot both store it. Filtered: company documents and
-- soft-deleted rows are outside the rule (a deleted file may be uploaded again).
-- The name is load-bearing: the API recognises the violation by it and returns
-- the same 409 duplicate_document. Pre-check 4 must return 0 rows first.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'UX_Documents_Legajo_Hash'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_Documents_Legajo_Hash]
        ON [dbo].[Documents] ([PersonLegajoId] ASC, [FileHashSHA256] ASC)
        WHERE [PersonLegajoId] IS NOT NULL AND [IsDeleted] = 0;
    PRINT 'Index [UX_Documents_Legajo_Hash] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_Documents_Legajo_Hash] already exists — skipped.';
END
GO

-- Period-close or measurement date, as the classification read it.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'PeriodEnd'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [PeriodEnd] DATE NULL;
    PRINT 'Column PeriodEnd added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column PeriodEnd already exists on [dbo].[Documents] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'EngagementType'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [EngagementType] NVARCHAR(20) NULL;
    PRINT 'Column EngagementType added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column EngagementType already exists on [dbo].[Documents] — skipped.';
END
GO

-- The code's list is EngagementTypes.Known.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Documents_EngagementType'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
)
BEGIN
    ALTER TABLE [dbo].[Documents] WITH CHECK
        ADD CONSTRAINT [CK_Documents_EngagementType]
        CHECK ([EngagementType] IS NULL OR [EngagementType] IN (N'Certificacion', N'Compilacion', N'Ninguno'));
    PRINT 'Constraint CK_Documents_EngagementType added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Documents_EngagementType already exists on [dbo].[Documents] — skipped.';
END
GO

-- Rectificativas: the document this one supersedes. Nothing sets it in Phase 2a.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'SupersedesDocumentId'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [SupersedesDocumentId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column SupersedesDocumentId added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column SupersedesDocumentId already exists on [dbo].[Documents] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_Documents_SupersedesDocument]')
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD CONSTRAINT [FK_Documents_SupersedesDocument]
            FOREIGN KEY ([SupersedesDocumentId]) REFERENCES [dbo].[Documents] ([DocumentId]);
    PRINT 'Foreign key [FK_Documents_SupersedesDocument] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_Documents_SupersedesDocument] already exists — skipped.';
END
GO

-- XX-XXXXXXXX-X, what the classification read. PII; possibly a third party's.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'HolderCuit'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [HolderCuit] NVARCHAR(13) NULL;
    PRINT 'Column HolderCuit added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column HolderCuit already exists on [dbo].[Documents] — skipped.';
END
GO

-- NULL for every non-legajo document; see the owner CHECK below.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'LegajoStatus'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [LegajoStatus] NVARCHAR(30) NULL;
    PRINT 'Column LegajoStatus added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column LegajoStatus already exists on [dbo].[Documents] — skipped.';
END
GO

-- The code's list is LegajoStatuses.Known.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Documents_LegajoStatus'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
)
BEGIN
    ALTER TABLE [dbo].[Documents] WITH CHECK
        ADD CONSTRAINT [CK_Documents_LegajoStatus]
        CHECK ([LegajoStatus] IS NULL OR [LegajoStatus] IN (N'Uploaded', N'Queued', N'Classifying', N'Blocked',
                                                            N'ReadyForExtraction', N'Extracting', N'NeedsReview',
                                                            N'Ready', N'Failed'));
    PRINT 'Constraint CK_Documents_LegajoStatus added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Documents_LegajoStatus already exists on [dbo].[Documents] — skipped.';
END
GO

-- Only a legajo document carries a legajo status. TABLE-LEVEL (two columns), so
-- the column-bound drop loop of section 1 never matches it.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Documents_LegajoStatus_Owner'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
)
BEGIN
    ALTER TABLE [dbo].[Documents] WITH CHECK
        ADD CONSTRAINT [CK_Documents_LegajoStatus_Owner]
        CHECK ([LegajoStatus] IS NULL OR [PersonLegajoId] IS NOT NULL);
    PRINT 'Constraint CK_Documents_LegajoStatus_Owner added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Documents_LegajoStatus_Owner already exists on [dbo].[Documents] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'LegajoStatusUpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [LegajoStatusUpdatedAt] DATETIME2 NULL;
    PRINT 'Column LegajoStatusUpdatedAt added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column LegajoStatusUpdatedAt already exists on [dbo].[Documents] — skipped.';
END
GO

-- [{page, chars, digits, suspiciousRatio, verdict}] — figures only, never page text.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Documents]')
      AND  name      = N'PageQualityJson'
)
BEGIN
    ALTER TABLE [dbo].[Documents]
        ADD [PageQualityJson] NVARCHAR(MAX) NULL;
    PRINT 'Column PageQualityJson added to [dbo].[Documents].';
END
ELSE
BEGIN
    PRINT 'Column PageQualityJson already exists on [dbo].[Documents] — skipped.';
END
GO

-- ── 3. [dbo].[PersonLegajos]: the data-processing authorization ─────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[PersonLegajos]')
      AND  name      = N'ConsentRecordedAt'
)
BEGIN
    ALTER TABLE [dbo].[PersonLegajos]
        ADD [ConsentRecordedAt] DATETIME2 NULL;
    PRINT 'Column ConsentRecordedAt added to [dbo].[PersonLegajos].';
END
ELSE
BEGIN
    PRINT 'Column ConsentRecordedAt already exists on [dbo].[PersonLegajos] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[PersonLegajos]')
      AND  name      = N'ConsentDocumentId'
)
BEGIN
    ALTER TABLE [dbo].[PersonLegajos]
        ADD [ConsentDocumentId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column ConsentDocumentId added to [dbo].[PersonLegajos].';
END
ELSE
BEGIN
    PRINT 'Column ConsentDocumentId already exists on [dbo].[PersonLegajos] — skipped.';
END
GO

-- Closes the FK cycle with FK_Documents_PersonLegajos; both ends nullable (see header).
IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  object_id = OBJECT_ID(N'[dbo].[FK_PersonLegajos_ConsentDocument]')
)
BEGIN
    ALTER TABLE [dbo].[PersonLegajos]
        ADD CONSTRAINT [FK_PersonLegajos_ConsentDocument]
            FOREIGN KEY ([ConsentDocumentId]) REFERENCES [dbo].[Documents] ([DocumentId]);
    PRINT 'Foreign key [FK_PersonLegajos_ConsentDocument] added.';
END
ELSE
BEGIN
    PRINT 'Foreign key [FK_PersonLegajos_ConsentDocument] already exists — skipped.';
END
GO

-- [dbo].[Users].UserId; no FK, per 030/069.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[PersonLegajos]')
      AND  name      = N'ConsentRecordedBy'
)
BEGIN
    ALTER TABLE [dbo].[PersonLegajos]
        ADD [ConsentRecordedBy] UNIQUEIDENTIFIER NULL;
    PRINT 'Column ConsentRecordedBy added to [dbo].[PersonLegajos].';
END
ELSE
BEGIN
    PRINT 'Column ConsentRecordedBy already exists on [dbo].[PersonLegajos] — skipped.';
END
GO

-- ── 4. [dbo].[LegajoExtractions] ────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[LegajoExtractions]
    (
        [ExtractionId]    UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_LegajoExtractions_ExtractionId] DEFAULT (NEWSEQUENTIALID()),   -- the API supplies its own
        [TenantId]        NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005; NOT a GUID
        [PersonLegajoId]  UNIQUEIDENTIFIER NOT NULL,
        [DocumentId]      UNIQUEIDENTIFIER NOT NULL,
        [Kind]            NVARCHAR(20)     NOT NULL,   -- Classification | Extraction
        [DocumentType]    NVARCHAR(50)     NOT NULL,   -- detected (classification) or schema type (extraction)
        [SchemaVersion]   NVARCHAR(20)     NOT NULL,
        [PromptVersion]   NVARCHAR(20)     NOT NULL,
        [Model]           NVARCHAR(100)    NOT NULL,
        [ReadMode]        NVARCHAR(20)     NOT NULL,   -- Document | Images | Mixed
        [Status]          NVARCHAR(20)     NOT NULL,   -- Succeeded | Failed
        [ResultJson]      NVARCHAR(MAX)    NOT NULL,   -- fields carry their own provenance; PII
        [ChecksJson]      NVARCHAR(MAX)    NULL,       -- Phase 2b arithmetic checks
        [Confidence]      DECIMAL(5, 4)    NULL,
        [InputTokens]     INT              NOT NULL,
        [OutputTokens]    INT              NOT NULL,
        [CostUsd]         DECIMAL(12, 6)   NULL,
        [InputHash]       NVARCHAR(64)     NOT NULL,   -- SHA-256 hex of the request body
        [OutputHash]      NVARCHAR(64)     NOT NULL,   -- SHA-256 hex of the response body
        [IsCurrent]       BIT              NOT NULL
            CONSTRAINT [DF_LegajoExtractions_IsCurrent] DEFAULT (0),   -- a hand-written row never steals the slot
        [CreatedAt]       DATETIME2        NOT NULL
            CONSTRAINT [DF_LegajoExtractions_CreatedAt] DEFAULT (GETUTCDATE()),
        [CreatedBy]       UNIQUEIDENTIFIER NULL,       -- NULL = the background worker
        [ReviewedBy]      UNIQUEIDENTIFIER NULL,       -- Phase 2b
        [ReviewedAt]      DATETIME2        NULL,       -- Phase 2b

        CONSTRAINT [PK_LegajoExtractions]
            PRIMARY KEY CLUSTERED ([ExtractionId] ASC),

        CONSTRAINT [FK_LegajoExtractions_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]),

        CONSTRAINT [FK_LegajoExtractions_Documents]
            FOREIGN KEY ([DocumentId]) REFERENCES [dbo].[Documents] ([DocumentId]),

        -- The code's lists are LegajoExtractionKinds / LegajoReadModes / LegajoExtractionStatuses.
        CONSTRAINT [CK_LegajoExtractions_Kind]
            CHECK ([Kind] IN (N'Classification', N'Extraction')),

        CONSTRAINT [CK_LegajoExtractions_ReadMode]
            CHECK ([ReadMode] IN (N'Document', N'Images', N'Mixed')),

        CONSTRAINT [CK_LegajoExtractions_Status]
            CHECK ([Status] IN (N'Succeeded', N'Failed')),

        CONSTRAINT [CK_LegajoExtractions_Confidence]
            CHECK ([Confidence] IS NULL OR ([Confidence] >= 0 AND [Confidence] <= 1))
    );
    PRINT 'Table [dbo].[LegajoExtractions] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[LegajoExtractions] already exists — skipped.';
END
GO

-- ONE current run per (document, kind). Filtered: superseded runs keep IsCurrent = 0.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
      AND  name      = N'UX_LegajoExtractions_Current'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_LegajoExtractions_Current]
        ON [dbo].[LegajoExtractions] ([DocumentId] ASC, [Kind] ASC)
        WHERE [IsCurrent] = 1;
    PRINT 'Index [UX_LegajoExtractions_Current] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_LegajoExtractions_Current] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoExtractions]')
      AND  name      = N'IX_LegajoExtractions_TenantId_PersonLegajoId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_LegajoExtractions_TenantId_PersonLegajoId]
        ON [dbo].[LegajoExtractions] ([TenantId] ASC, [PersonLegajoId] ASC);
    PRINT 'Index [IX_LegajoExtractions_TenantId_PersonLegajoId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_LegajoExtractions_TenantId_PersonLegajoId] already exists — skipped.';
END
GO

-- ── 5. [dbo].[LegajoCorrections] (Phase 2b; append-only) ────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoCorrections]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[LegajoCorrections]
    (
        [CorrectionId]       UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_LegajoCorrections_CorrectionId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]           NVARCHAR(100)    NOT NULL,
        [ExtractionId]       UNIQUEIDENTIFIER NOT NULL,
        [FieldPath]          NVARCHAR(300)    NOT NULL,
        [OriginalValueJson]  NVARCHAR(MAX)    NULL,
        [CorrectedValueJson] NVARCHAR(MAX)    NULL,
        [Reason]             NVARCHAR(1000)   NOT NULL,
        [CorrectedBy]        UNIQUEIDENTIFIER NOT NULL,   -- [dbo].[Users].UserId; no FK, per 030/069
        [CorrectedAt]        DATETIME2        NOT NULL
            CONSTRAINT [DF_LegajoCorrections_CorrectedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_LegajoCorrections]
            PRIMARY KEY CLUSTERED ([CorrectionId] ASC),

        CONSTRAINT [FK_LegajoCorrections_LegajoExtractions]
            FOREIGN KEY ([ExtractionId]) REFERENCES [dbo].[LegajoExtractions] ([ExtractionId]),

        CONSTRAINT [CK_LegajoCorrections_Reason]
            CHECK (LEN(LTRIM(RTRIM([Reason]))) >= 5)
    );
    PRINT 'Table [dbo].[LegajoCorrections] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[LegajoCorrections] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoCorrections]')
      AND  name      = N'IX_LegajoCorrections_ExtractionId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_LegajoCorrections_ExtractionId]
        ON [dbo].[LegajoCorrections] ([ExtractionId] ASC);
    PRINT 'Index [IX_LegajoCorrections_ExtractionId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_LegajoCorrections_ExtractionId] already exists — skipped.';
END
GO

-- ── 6. [dbo].[ApiCallLogs].DocumentId ───────────────────────────────────────
-- No FK, like AnalysisId.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
      AND  name      = N'DocumentId'
)
BEGIN
    ALTER TABLE [dbo].[ApiCallLogs]
        ADD [DocumentId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column DocumentId added to [dbo].[ApiCallLogs].';
END
ELSE
BEGIN
    PRINT 'Column DocumentId already exists on [dbo].[ApiCallLogs] — skipped.';
END
GO

-- ── 7. Widen CK_ApiCallLogs_Operation, if this database has one (the LAST batch) ──
-- No numbered script created a CHECK on [Operation], but Financia-db carries one
-- made out of band, CK_ApiCallLogs_Operation, which admitted NULL, 'DynamicsSync',
-- 'BcraLookup', 'Chat', 'Narrative' and 'Extraction' and so rejected the legajo
-- cost rows (the API swallows a failed log write: they would vanish silently).
-- Same drop-and-recreate as section 1: every check BOUND to [Operation], plus any
-- constraint holding the canonical name, then the rule under the canonical name,
-- in ONE transaction under XACT_ABORT. The new list is a SUPERSET:
--   * legacy values admitted by the out-of-band CHECK, written by NO code in this
--     repo (another application may write them — keep them):
--       'DynamicsSync', 'BcraLookup'
--   * the values this API writes — ApiCallLogOperations.Known, which a test pins
--     as a subset of this list:
--       'Extraction', 'Narrative', 'Chat', 'LegajoClassification', 'LegajoExtraction'
--   * NULL (ARCA, LUFE and NOSIS rows carry no operation).
-- A database with NO check on [Operation] stays without one: nothing is created.
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @DropOperation NVARCHAR(MAX) = N'';

SELECT @DropOperation = @DropOperation
       + N'ALTER TABLE [dbo].[ApiCallLogs] DROP CONSTRAINT ' + QUOTENAME(cc.name) + N'; '
       + N'PRINT N''Check ' + REPLACE(cc.name, N'''', N'''''') + N' dropped from [dbo].[ApiCallLogs].[Operation].''; '
FROM   sys.check_constraints cc
LEFT   JOIN sys.columns c
       ON  c.object_id = cc.parent_object_id
       AND c.column_id = cc.parent_column_id
WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
  AND  (c.name = N'Operation'                          -- column-bound only: parent_column_id <> 0
        OR cc.name = N'CK_ApiCallLogs_Operation');     -- the canonical name, wherever it is bound

IF @DropOperation = N''
BEGIN
    PRINT 'No check constraint on [dbo].[ApiCallLogs].[Operation] — none created (this database never had one).';
END
ELSE
BEGIN
    EXEC sys.sp_executesql @DropOperation;

    ALTER TABLE [dbo].[ApiCallLogs] WITH CHECK
        ADD CONSTRAINT [CK_ApiCallLogs_Operation]
        CHECK ([Operation] IS NULL OR [Operation] IN (N'DynamicsSync', N'BcraLookup',
                                                      N'Extraction', N'Narrative', N'Chat',
                                                      N'LegajoClassification', N'LegajoExtraction'));

    PRINT 'Constraint CK_ApiCallLogs_Operation recreated with the legacy values and every operation the API writes.';
END

COMMIT TRANSACTION;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the CHECKs, FKs or indexes below. These queries are the evidence.
--
-- 1. Exactly ONE check governs each widened column, with the new list, enabled
--    and TRUSTED.
--
--    SELECT cc.name, c.name AS ColumnName, cc.is_disabled, cc.is_not_trusted, cc.definition
--    FROM   sys.check_constraints cc
--    JOIN   sys.columns c ON c.object_id = cc.parent_object_id
--                        AND c.column_id = cc.parent_column_id
--    WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[Documents]')
--      AND  c.name IN (N'DocumentType', N'PdfType');
--    -- expected: CK_Documents_DocumentType (14 values) and CK_Documents_PdfType
--    -- (NULL + 3 values), each 0 0.
--
-- 2. Every new CHECK exists, enabled and TRUSTED. Expected: 9 rows where
--    [ApiCallLogs].[Operation] had a check before this script (Financia-db:
--    CK_ApiCallLogs_Operation included), 8 where it had none; every one 0 0, and
--    CK_Documents_LegajoStatus_Owner with parent_column_id 0 (table-level).
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name, parent_column_id,
--           is_disabled, is_not_trusted
--    FROM   sys.check_constraints
--    WHERE  name IN (N'CK_Documents_EngagementType', N'CK_Documents_LegajoStatus',
--                    N'CK_Documents_LegajoStatus_Owner', N'CK_LegajoExtractions_Kind',
--                    N'CK_LegajoExtractions_ReadMode', N'CK_LegajoExtractions_Status',
--                    N'CK_LegajoExtractions_Confidence', N'CK_LegajoCorrections_Reason',
--                    N'CK_ApiCallLogs_Operation');
--
--    Where it exists, CK_ApiCallLogs_Operation must be the ONLY check bound to
--    [Operation] and name all seven values (DynamicsSync, BcraLookup, Extraction,
--    Narrative, Chat, LegajoClassification, LegajoExtraction) plus NULL:
--
--    SELECT cc.name, cc.definition
--    FROM   sys.check_constraints cc
--    JOIN   sys.columns c ON c.object_id = cc.parent_object_id
--                        AND c.column_id = cc.parent_column_id
--    WHERE  cc.parent_object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
--      AND  c.name = N'Operation';
--
-- 3. The foreign keys. Expected: 6 rows, every one 0 0 NO_ACTION.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name,
--           is_disabled, is_not_trusted, delete_referential_action_desc
--    FROM   sys.foreign_keys
--    WHERE  name IN (N'FK_Documents_PersonLegajos', N'FK_Documents_SupersedesDocument',
--                    N'FK_PersonLegajos_ConsentDocument', N'FK_LegajoExtractions_PersonLegajos',
--                    N'FK_LegajoExtractions_Documents', N'FK_LegajoCorrections_LegajoExtractions');
--
-- 4. The indexes. Expected: these five rows.
--
--    SELECT OBJECT_NAME(i.object_id) AS TableName, i.name, i.is_unique,
--           i.has_filter, i.filter_definition
--    FROM   sys.indexes i
--    WHERE  i.name IN (N'IX_Documents_TenantId_PersonLegajoId', N'UX_Documents_Legajo_Hash',
--                      N'UX_LegajoExtractions_Current',
--                      N'IX_LegajoExtractions_TenantId_PersonLegajoId',
--                      N'IX_LegajoCorrections_ExtractionId')
--    ORDER  BY TableName, i.name;
--    -- expected: Documents          IX_Documents_TenantId_PersonLegajoId          0 1 ([PersonLegajoId] IS NOT NULL)
--    --           Documents          UX_Documents_Legajo_Hash                      1 1 ([PersonLegajoId] IS NOT NULL AND [IsDeleted]=(0))
--    --           LegajoCorrections  IX_LegajoCorrections_ExtractionId             0 0 NULL
--    --           LegajoExtractions  IX_LegajoExtractions_TenantId_PersonLegajoId  0 0 NULL
--    --           LegajoExtractions  UX_LegajoExtractions_Current                  1 1 ([IsCurrent]=(1))
--
-- 5. Nothing changed any existing row. Expected: 0 and 0.
--
--    SELECT COUNT(*) AS DocumentsWithLegajoColumns
--    FROM   [dbo].[Documents]
--    WHERE  PersonLegajoId IS NOT NULL OR LegajoStatus IS NOT NULL OR PageQualityJson IS NOT NULL;
--
--    SELECT COUNT(*) AS LegajosWithConsent
--    FROM   [dbo].[PersonLegajos]
--    WHERE  ConsentRecordedAt IS NOT NULL;
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps every column and both
-- tables below. Run with the same SET options as the script. Reverse order;
-- every constraint, index or default goes BEFORE the column it sits on.
--
-- (a) EXPORT FIRST whatever the legajo pipeline wrote. ⚠ PERSONAL DATA: the
--     holder CUITs (possibly third parties'), the model's reading of a person's
--     paperwork, and each person's consent record.
--
--    SELECT * FROM [dbo].[LegajoCorrections];
--    SELECT * FROM [dbo].[LegajoExtractions];
--    SELECT DocumentId, TenantId, PersonLegajoId, DocumentType, PeriodEnd, EngagementType,
--           SupersedesDocumentId, HolderCuit, LegajoStatus, LegajoStatusUpdatedAt, PageQualityJson
--    FROM   [dbo].[Documents] WHERE PersonLegajoId IS NOT NULL;
--    SELECT PersonLegajoId, TenantId, ConsentRecordedAt, ConsentDocumentId, ConsentRecordedBy
--    FROM   [dbo].[PersonLegajos] WHERE ConsentRecordedAt IS NOT NULL;
--
-- (b) The tables:
--
--    DROP TABLE [dbo].[LegajoCorrections];
--    DROP TABLE [dbo].[LegajoExtractions];
--    GO
--
-- (c) ApiCallLogs and PersonLegajos:
--
--    ALTER TABLE [dbo].[ApiCallLogs]   DROP COLUMN [DocumentId];
--    ALTER TABLE [dbo].[PersonLegajos] DROP CONSTRAINT [FK_PersonLegajos_ConsentDocument];
--    ALTER TABLE [dbo].[PersonLegajos] DROP COLUMN [ConsentRecordedBy];
--    ALTER TABLE [dbo].[PersonLegajos] DROP COLUMN [ConsentDocumentId];
--    ALTER TABLE [dbo].[PersonLegajos] DROP COLUMN [ConsentRecordedAt];
--    GO
--
-- (d) Documents:
--
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [CK_Documents_LegajoStatus_Owner];
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [CK_Documents_LegajoStatus];
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [CK_Documents_EngagementType];
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [FK_Documents_SupersedesDocument];
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [FK_Documents_PersonLegajos];
--    DROP INDEX [UX_Documents_Legajo_Hash] ON [dbo].[Documents];
--    DROP INDEX [IX_Documents_TenantId_PersonLegajoId] ON [dbo].[Documents];
--    ALTER TABLE [dbo].[Documents] DROP COLUMN [PageQualityJson], [LegajoStatusUpdatedAt],
--          [LegajoStatus], [HolderCuit], [SupersedesDocumentId], [EngagementType], [PeriodEnd],
--          [PersonLegajoId];
--    GO
--
-- (e) The CHECK narrowings — ONLY IF no row holds a new value. Each must return
--     NOTHING first; a legajo document row (kept, soft-deleted or not) blocks it.
--
--    SELECT DocumentType, COUNT(*) FROM [dbo].[Documents]
--    WHERE  DocumentType NOT IN (N'Balance', N'Complemento', N'Certificado', N'Otro')
--    GROUP  BY DocumentType;
--    SELECT COUNT(*) FROM [dbo].[Documents] WHERE PdfType = N'Mixed';
--
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [CK_Documents_DocumentType];
--    ALTER TABLE [dbo].[Documents] WITH CHECK ADD CONSTRAINT [CK_Documents_DocumentType]
--        CHECK ([DocumentType] IN (N'Balance', N'Complemento', N'Certificado', N'Otro'));
--    ALTER TABLE [dbo].[Documents] DROP CONSTRAINT [CK_Documents_PdfType];
--    ALTER TABLE [dbo].[Documents] WITH CHECK ADD CONSTRAINT [CK_Documents_PdfType]
--        CHECK ([PdfType] IS NULL OR [PdfType] IN (N'Scanned', N'Native'));
--    GO
--
--     The old code runs fine on the WIDER lists, so (e) can simply be skipped.
--
-- (f) CK_ApiCallLogs_Operation is NOT rolled back. Its pre-070 definition was
--     created out of band, by no numbered script, and may differ between
--     databases, so this script cannot know what to restore it to (on Financia-db
--     it admitted NULL, 'DynamicsSync', 'BcraLookup', 'Chat', 'Narrative' and
--     'Extraction'). The widened CHECK is a superset of that, the old code writes
--     fine against it, and any legajo cost row would block a narrowing anyway.
--     The rollback KEEPS it.
-- ============================================================================
