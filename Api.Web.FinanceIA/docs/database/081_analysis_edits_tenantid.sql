-- ============================================================================
-- Script  : 081_analysis_edits_tenantid.sql
-- Purpose : Make [dbo].[AnalysisEdits] usable for the manual correction of
--           extracted balance figures (POST /api/analyses/{resultId}/balance-
--           corrections, Phase 10-F3a). The table exists since 001 but was
--           never mapped, and 005 did not migrate it, so its [TenantId] is still
--           UNIQUEIDENTIFIER and rejects a Firebase tenant SLUG. This script:
--             - CREATEs the table when it is missing, with TenantId NVARCHAR(100);
--             - otherwise retypes [TenantId] UNIQUEIDENTIFIER → NVARCHAR(100) NOT
--               NULL, only while it is still uniqueidentifier and only when the
--               table is EMPTY (it has never been written: nothing mapped it);
--             - replaces IX_AnalysisEdits_TenantId_AnalysisId (001) by
--               IX_AnalysisEdits_TenantId_AnalysisId_EditedAt, the history read:
--               WHERE TenantId = @t AND AnalysisId = @a ORDER BY EditedAt DESC.
-- Date    : 2026-11-02
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, IN EVERY FINANCIA (TENANT BUSINESS) DATABASE
--           the DDL procedure lists (one per client, .github/clients.json), then
--           restart the App Service. Nothing below is scoped to one tenant.
--           NOT for the Default database.
--
--           WHY IT IS A DEPLOY-BLOCKER EVEN THOUGH THE TABLE EXISTS. EF now maps
--           [AnalysisEdits] with TenantId as a string. On a database where 081
--           has not run, the table and every column already exist (001), so a
--           name-only comparison would pass — and the first correction would fail
--           with a uniqueidentifier conversion error. --verify-schema therefore
--           also checks the TYPE of this one column (Clases/SchemaVerification,
--           RequiredColumnTypes) and refuses the publish until 081 has run.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. The running code never
--           reads or writes [AnalysisEdits].
--
--           APPEND-ONLY. The API INSERTs rows and never updates or deletes them,
--           like the audit tables. One row per corrected figure: FieldPath (the
--           BalanceJson path), OldValue / NewValue (invariant-culture numbers, or
--           NULL), EditReason, EditedBy (Users.UserId), EditedAt (UTC).
--
--           PII: EditReason is the analyst's free text. It is stored here and in
--           BalanceJson.manual_corrections, returned to the tenant's own users,
--           and never logged nor sent to a prompt.
--
--           NON-EMPTY TABLE. If the table holds rows while TenantId is still a
--           uniqueidentifier, block 2 RAISEs an error and changes NOTHING, and the
--           later blocks skip: those rows were written by something outside this
--           API and need a DBA decision before their tenant is rewritten.
--
--           TRANSACTION. Block 2 drops the indexes on TenantId (SQL Server cannot
--           alter an indexed column) and alters the column inside one explicit
--           transaction with SET XACT_ABORT ON, as 011 does, so a partial run
--           never leaves the table without its type change or without a rollback.
--
--           SET OPTIONS, THE FIRST BATCH. The new index has a DESC key; the
--           session options match 069-080 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           IDEMPOTENT: every block is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. Does the table exist, and with which TenantId type? Expected on a database
--    built from 001: 1 row — uniqueidentifier. 0 rows: block 1 creates it.
--
--    SELECT t.name AS type_name, c.max_length
--    FROM   sys.columns c
--    JOIN   sys.types t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]') AND c.name = N'TenantId';
--
-- 2. The row count. Expected: 0 (nothing has ever written the table).
--
--    SELECT COUNT(*) FROM [dbo].[AnalysisEdits];
-- ============================================================================

-- ── 0. Session options (see header) ─────────────────────────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. [dbo].[AnalysisEdits], when it is missing ────────────────────────────
-- 001's columns verbatim, except TenantId: a Firebase tenant SLUG, per 005.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[AnalysisEdits]
    (
        [AnalysisEditId] BIGINT           IDENTITY(1, 1) NOT NULL,
        [AnalysisId]     UNIQUEIDENTIFIER NOT NULL,
        [TenantId]       NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG; NOT a GUID
        [EditedBy]       UNIQUEIDENTIFIER NOT NULL,   -- Users.UserId
        [FieldPath]      NVARCHAR(200)    NOT NULL,   -- BalanceJson path, e.g. current_assets_items[2].amount
        [OldValue]       NVARCHAR(MAX)    NULL,
        [NewValue]       NVARCHAR(MAX)    NULL,
        [EditReason]     NVARCHAR(MAX)    NULL,
        [EditedAt]       DATETIME2(7)     NOT NULL
            CONSTRAINT [DF_AnalysisEdits_EditedAt] DEFAULT (GETUTCDATE()),   -- the API supplies its own

        CONSTRAINT [PK_AnalysisEdits]
            PRIMARY KEY CLUSTERED ([AnalysisEditId] ASC)
    );
    PRINT 'Table [dbo].[AnalysisEdits] created with TenantId NVARCHAR(100).';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[AnalysisEdits] already exists — skipped.';
END
GO

-- ── 2. [dbo].[AnalysisEdits].[TenantId] UNIQUEIDENTIFIER → NVARCHAR(100) ────
-- Only while it is still uniqueidentifier, and only on an empty table.

IF EXISTS (
    SELECT 1
    FROM   sys.columns c
    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]')
      AND  c.name      = N'TenantId'
      AND  t.name      = N'uniqueidentifier'
)
BEGIN
    DECLARE @Rows BIGINT = (SELECT COUNT_BIG(*) FROM [dbo].[AnalysisEdits]);
    PRINT 'AnalysisEdits.TenantId is uniqueidentifier; rows in the table: ' + CAST(@Rows AS NVARCHAR(20)) + '.';

    IF @Rows > 0
    BEGIN
        RAISERROR (N'081: [dbo].[AnalysisEdits] holds rows while TenantId is still uniqueidentifier. Nothing was changed; a DBA must decide how to map those rows to a tenant slug before re-running.', 16, 1);
    END
    ELSE
    BEGIN
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        DROP INDEX IF EXISTS [IX_AnalysisEdits_TenantId_AnalysisId]          ON [dbo].[AnalysisEdits];
        DROP INDEX IF EXISTS [IX_AnalysisEdits_TenantId_AnalysisId_EditedAt] ON [dbo].[AnalysisEdits];

        ALTER TABLE [dbo].[AnalysisEdits]
            ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;

        COMMIT TRANSACTION;
        PRINT 'Column [dbo].[AnalysisEdits].[TenantId] altered to NVARCHAR(100) NOT NULL.';
    END
END
ELSE
BEGIN
    PRINT 'Column [dbo].[AnalysisEdits].[TenantId] is not uniqueidentifier — skipped.';
END
GO

-- ── 3. The 001 index, superseded ────────────────────────────────────────────
-- IX_AnalysisEdits_TenantId_AnalysisId (TenantId, AnalysisId) is a prefix of
-- the index below, so it is not kept. Skipped while TenantId is still a
-- uniqueidentifier (block 2 refused): the table is then left exactly as found.

IF EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]')
      AND  name      = N'IX_AnalysisEdits_TenantId_AnalysisId'
)
AND NOT EXISTS (
    SELECT 1
    FROM   sys.columns c
    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]')
      AND  c.name      = N'TenantId'
      AND  t.name      = N'uniqueidentifier'
)
BEGIN
    DROP INDEX [IX_AnalysisEdits_TenantId_AnalysisId] ON [dbo].[AnalysisEdits];
    PRINT 'Index [IX_AnalysisEdits_TenantId_AnalysisId] dropped (superseded).';
END
ELSE
BEGIN
    PRINT 'Index [IX_AnalysisEdits_TenantId_AnalysisId] absent, or TenantId still uniqueidentifier — skipped.';
END
GO

-- ── 4. IX_AnalysisEdits_TenantId_AnalysisId_EditedAt ────────────────────────
-- The history read: WHERE TenantId = @t AND AnalysisId = @a ORDER BY EditedAt DESC.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]')
      AND  name      = N'IX_AnalysisEdits_TenantId_AnalysisId_EditedAt'
)
AND EXISTS (
    SELECT 1
    FROM   sys.columns c
    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]')
      AND  c.name      = N'TenantId'
      AND  t.name      = N'nvarchar'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_AnalysisEdits_TenantId_AnalysisId_EditedAt]
        ON [dbo].[AnalysisEdits] ([TenantId] ASC, [AnalysisId] ASC, [EditedAt] DESC);
    PRINT 'Index [IX_AnalysisEdits_TenantId_AnalysisId_EditedAt] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_AnalysisEdits_TenantId_AnalysisId_EditedAt] already exists, or TenantId is not nvarchar yet — skipped.';
END
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks the mapped tables and columns, and the TYPE of
-- AnalysisEdits.TenantId. It sees neither the default nor the index. These
-- queries are the evidence.
--
-- 1. TenantId. Expected: 1 row — nvarchar 200 0 (max_length is in bytes).
--
--    SELECT t.name AS type_name, c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]') AND c.name = N'TenantId';
--
-- 2. The 9 columns. Expected: 9.
--
--    SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]');
--
-- 3. The indexes. Expected: PK_AnalysisEdits and
--    IX_AnalysisEdits_TenantId_AnalysisId_EditedAt only; no
--    IX_AnalysisEdits_TenantId_AnalysisId.
--
--    SELECT name FROM sys.indexes
--    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]') AND name IS NOT NULL;
--
-- 4. The index keys, EditedAt descending. Expected: 3 rows —
--    TenantId 0, AnalysisId 0, EditedAt 1.
--
--    SELECT c.name, ic.is_descending_key
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.name = N'IX_AnalysisEdits_TenantId_AnalysisId_EditedAt'
--    ORDER BY ic.key_ordinal;
--
-- 5. The default. Expected: 1 row — DF_AnalysisEdits_EditedAt.
--
--    SELECT dc.name FROM sys.default_constraints dc
--    WHERE  dc.parent_object_id = OBJECT_ID(N'[dbo].[AnalysisEdits]');
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps the table with a
-- string TenantId and writes it on every balance correction.
--
-- (a) EXPORT FIRST the corrections (EditReason is the analyst's free text):
--
--     SELECT * FROM [dbo].[AnalysisEdits];
--
-- (b) The rows carry tenant slugs, which a uniqueidentifier column cannot hold,
--     so they must be deleted before the type goes back:
--
--     DROP INDEX IF EXISTS [IX_AnalysisEdits_TenantId_AnalysisId_EditedAt] ON [dbo].[AnalysisEdits];
--     DELETE FROM [dbo].[AnalysisEdits];
--     ALTER TABLE [dbo].[AnalysisEdits] ALTER COLUMN [TenantId] UNIQUEIDENTIFIER NOT NULL;
--     CREATE NONCLUSTERED INDEX [IX_AnalysisEdits_TenantId_AnalysisId]
--         ON [dbo].[AnalysisEdits] ([TenantId] ASC, [AnalysisId] ASC);
--
--     The corrected figures stay in AnalysisResults.BalanceJson: rolling 081 back
--     does not undo a correction. To undo one, copy the "balanceJson" member of
--     the AnalysisRescores.PreviousValuesJson row whose "trigger" is
--     "balance_correction" back into AnalysisResults.BalanceJson.
-- ============================================================================
