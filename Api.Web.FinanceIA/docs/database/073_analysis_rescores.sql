-- ============================================================================
-- Script  : 073_analysis_rescores.sql
-- Purpose : "Reanalizar con datos actualizados" — re-score a balance analysis
--           in place, from its already-extracted accounts, with a live BCRA
--           lookup and the entity's CURRENT LUFE / CASFOG / ARCA / NOSIS
--           snapshots, WITHOUT a new Claude extraction:
--             - add [RescoredAt], [RescoredBy] and [RescoreCount] to
--               [dbo].[AnalysisResults]: when the latest re-score ran, who ran
--               it, and how many there have been. [RescoreCount] is also the
--               optimistic concurrency token of the write
--               (UPDATE … WHERE ResultId = @id AND RescoreCount = @expected);
--             - create [dbo].[AnalysisRescores]: one APPEND-ONLY row per
--               re-score, with the score / level / enrichment status before
--               and after, the previous value of every overwritten column
--               (PreviousValuesJson) and the source dates the new score used
--               (SourcesJson).
-- Date    : 2026-10-22
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, once on Financia-db, then restart the App
--           Service. The API binds ONE static FinanciaDefault connection
--           (Financia-db) and separates tenants by the TenantId column; if the
--           DDL procedure lists further business databases, run it on each of
--           them too, as with 069-072. Nothing below is scoped to one tenant.
--           EF maps the three [AnalysisResults] columns, and EF emits every
--           mapped column in every SELECT and INSERT against that table, so
--           publishing first breaks the analysis list, the detail, the chat, the
--           PDF export, the decision and the pipeline itself with "Invalid
--           column name". --verify-schema catches exactly that before the
--           publish.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. [RescoredAt] and
--           [RescoredBy] are NULLABLE with no default, and [RescoreCount] is
--           NOT NULL with DEFAULT 0, so the old INSERT (which names none of
--           them) still writes a valid row and every existing row reads "never
--           re-scored". The old code never reads the new table.
--
--           WHAT IS NOT CHANGED BY A RE-SCORE. BalanceJson, RiskAlerts,
--           Anomalies, ExecutiveSummary, CatalogueAlerts, the per-row Alerts,
--           ValidationStatus and the AI hashes/tokens are extraction outputs and
--           are never touched; neither is any [Analyses] column. What IS
--           overwritten on [AnalysisResults] — RatiosJson, BcraDataJson,
--           MacroDataJson, AiSuggestedScore, AiSuggestedLevel,
--           ScoreBreakdownJson, ScoringNarrative, the four snapshot copies,
--           EnrichmentStatus and EnrichmentNotes — is copied first, as stored,
--           into [AnalysisRescores].[PreviousValuesJson], in the SAME
--           transaction as the update. A re-score is therefore reversible by
--           hand from that row.
--
--           APPEND-ONLY. The API inserts [AnalysisRescores] rows and never
--           updates or deletes them, like the audit tables.
--
--           FK. FK_AnalysisRescores_AnalysisId is NO ACTION and not mapped in
--           EF, as in 070-072. An analysis is never deleted by the API.
--           [ResultId] carries no FK: [AnalysisResults] has one row per
--           analysis (UQ_AnalysisResults_AnalysisId), so the AnalysisId FK
--           already ties the row to its result.
--
--           ⚠ PII. [PreviousValuesJson] holds the previous BCRA and the four
--           frozen snapshots — the company's identity (CUIT, denominación,
--           related parties) exactly as [AnalysisResults] holds it. Treat every
--           row, and every export in the rollback, as that data. No endpoint
--           returns [PreviousValuesJson]; [SourcesJson] carries dates only.
--
--           CHECK CONSTRAINTS follow 069-072: WITH CHECK. The two JSON columns
--           carry ISJSON checks. Score / level / enrichment status are not
--           re-checked here: the API copies them from [AnalysisResults], whose
--           own CHECKs (001, 039) already bound them.
--
--           SET OPTIONS, THE FIRST BATCH. The index below has a DESC key; the
--           session options match 069-072 so the script runs the same way in
--           SSMS, Azure Data Studio and sqlcmd -I.
--
--           TRANSACTIONS: none. Every guarded block is one atomic statement.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 072 has run here (the API that ships with this script maps AnalysisKind).
--    Expected: 1 row.
--
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]') AND name = N'AnalysisKind';
--
-- 2. None of the new objects exists yet (a re-run is harmless anyway).
--    Expected: 0 rows.
--
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
--      AND  name IN (N'RescoredAt', N'RescoredBy', N'RescoreCount');
-- ============================================================================

-- ── 0. Session options (see header) ─────────────────────────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. [dbo].[AnalysisResults].[RescoredAt] ─────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'RescoredAt'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [RescoredAt] DATETIME2 NULL;
    PRINT 'Column RescoredAt added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column RescoredAt already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

-- ── 2. [dbo].[AnalysisResults].[RescoredBy] ─────────────────────────────────
-- Users.UserId of the analyst. No FK, like Analyses.RequestedBy / ReviewedBy.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'RescoredBy'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [RescoredBy] UNIQUEIDENTIFIER NULL;
    PRINT 'Column RescoredBy added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column RescoredBy already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

-- ── 3. [dbo].[AnalysisResults].[RescoreCount] ───────────────────────────────
-- NOT NULL with a default: SQL Server fills every existing row with 0 in the
-- same statement, and the old INSERT keeps working (see header).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
      AND  name      = N'RescoreCount'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisResults]
        ADD [RescoreCount] INT NOT NULL
            CONSTRAINT [DF_AnalysisResults_RescoreCount] DEFAULT (0);
    PRINT 'Column RescoreCount added to [dbo].[AnalysisResults].';
END
ELSE
BEGIN
    PRINT 'Column RescoreCount already exists on [dbo].[AnalysisResults] — skipped.';
END
GO

-- ── 4. [dbo].[AnalysisRescores] ─────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisRescores]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[AnalysisRescores]
    (
        [RescoreId]                UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_AnalysisRescores_RescoreId] DEFAULT (NEWSEQUENTIALID()),   -- the API supplies its own
        [TenantId]                 NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005; NOT a GUID
        [AnalysisId]               UNIQUEIDENTIFIER NOT NULL,
        [ResultId]                 UNIQUEIDENTIFIER NOT NULL,
        [RescoredAt]               DATETIME2        NOT NULL,   -- UTC
        [RescoredBy]               UNIQUEIDENTIFIER NOT NULL,   -- Users.UserId
        [PreviousScore]            DECIMAL(5, 2)    NULL,       -- same type as AnalysisResults.AiSuggestedScore
        [NewScore]                 DECIMAL(5, 2)    NULL,
        [PreviousLevel]            NVARCHAR(20)     NULL,       -- same type as AnalysisResults.AiSuggestedLevel
        [NewLevel]                 NVARCHAR(20)     NULL,
        [PreviousEnrichmentStatus] NVARCHAR(20)     NOT NULL,   -- same type as AnalysisResults.EnrichmentStatus
        [NewEnrichmentStatus]      NVARCHAR(20)     NOT NULL,
        [PreviousValuesJson]       NVARCHAR(MAX)    NOT NULL,   -- every overwritten column, as stored before
        [SourcesJson]              NVARCHAR(MAX)    NOT NULL,   -- [{source, asOf, live}] — dates only

        CONSTRAINT [PK_AnalysisRescores]
            PRIMARY KEY CLUSTERED ([RescoreId] ASC),

        CONSTRAINT [FK_AnalysisRescores_AnalysisId]
            FOREIGN KEY ([AnalysisId]) REFERENCES [dbo].[Analyses] ([AnalysisId]),

        CONSTRAINT [CK_AnalysisRescores_PreviousValuesJson]
            CHECK (ISJSON([PreviousValuesJson]) = 1),

        CONSTRAINT [CK_AnalysisRescores_SourcesJson]
            CHECK (ISJSON([SourcesJson]) = 1)
    );
    PRINT 'Table [dbo].[AnalysisRescores] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[AnalysisRescores] already exists — skipped.';
END
GO

-- The history read: WHERE TenantId = @t AND AnalysisId = @a ORDER BY RescoredAt DESC.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisRescores]')
      AND  name      = N'IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt]
        ON [dbo].[AnalysisRescores] ([TenantId] ASC, [AnalysisId] ASC, [RescoredAt] DESC);
    PRINT 'Index [IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt] already exists — skipped.';
END
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the nullability, defaults, CHECKs, FKs or indexes below. These
-- queries are the evidence.
--
-- 1. The [AnalysisResults] columns. Expected: 3 rows —
--    RescoreCount int 0 DF_AnalysisResults_RescoreCount, RescoredAt datetime2 1 NULL,
--    RescoredBy uniqueidentifier 1 NULL.
--
--    SELECT c.name, t.name AS type_name, c.is_nullable, dc.name AS default_name
--    FROM   sys.columns c
--    JOIN   sys.types t ON t.user_type_id = c.user_type_id
--    LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisResults]')
--      AND  c.name IN (N'RescoredAt', N'RescoredBy', N'RescoreCount')
--    ORDER BY c.name;
--
-- 2. Every existing result reads "never re-scored". Expected: 0.
--
--    SELECT COUNT(*) FROM [dbo].[AnalysisResults]
--    WHERE  RescoreCount <> 0 OR RescoredAt IS NOT NULL OR RescoredBy IS NOT NULL;
--
-- 3. The table and its 14 columns. Expected: 14.
--
--    SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[AnalysisRescores]');
--
-- 4. The CHECKs exist, enabled and TRUSTED. Expected: 2 rows, every one 0 0.
--
--    SELECT name, is_disabled, is_not_trusted
--    FROM   sys.check_constraints
--    WHERE  name IN (N'CK_AnalysisRescores_PreviousValuesJson', N'CK_AnalysisRescores_SourcesJson');
--
-- 5. The foreign key. Expected: 1 row, 0 0 NO_ACTION.
--
--    SELECT name, is_disabled, is_not_trusted, delete_referential_action_desc
--    FROM   sys.foreign_keys
--    WHERE  name = N'FK_AnalysisRescores_AnalysisId';
--
-- 6. The index, with RescoredAt descending. Expected: 3 rows —
--    TenantId 0, AnalysisId 0, RescoredAt 1.
--
--    SELECT c.name, ic.is_descending_key
--    FROM   sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.name = N'IX_AnalysisRescores_TenantId_AnalysisId_RescoredAt'
--    ORDER BY ic.key_ordinal;
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps the three columns
-- and the new table. Run with the same SET options as the script.
--
-- (a) EXPORT FIRST the history. ⚠ PII (see header). It is the only record of
--     what each re-scored result held before; dropping it makes every re-score
--     irreversible.
--
--    SELECT * FROM [dbo].[AnalysisRescores];
--
-- (b) DROP TABLE [dbo].[AnalysisRescores];
--
-- (c) ALTER TABLE [dbo].[AnalysisResults] DROP CONSTRAINT [DF_AnalysisResults_RescoreCount];
--     ALTER TABLE [dbo].[AnalysisResults] DROP COLUMN [RescoreCount];
--     ALTER TABLE [dbo].[AnalysisResults] DROP COLUMN [RescoredBy];
--     ALTER TABLE [dbo].[AnalysisResults] DROP COLUMN [RescoredAt];
--
--     The re-scored values stay on [AnalysisResults]: dropping the columns does
--     not restore the previous score. To restore a result, copy the fields of
--     its OLDEST [AnalysisRescores].[PreviousValuesJson] back before (b).
-- ============================================================================
