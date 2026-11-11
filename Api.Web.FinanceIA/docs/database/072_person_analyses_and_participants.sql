-- ============================================================================
-- Script  : 072_person_analyses_and_participants.sql
-- Purpose : The persona humana ANALYSIS (M10, Phase 4a "v0"): an evaluation of
--           the applicant (solicitante) that runs on the legajo instead of a
--           balance, with pre-filters, indicators and a documentary coverage
--           index — and NO score, NO risk level and NO model call:
--             - add [AnalysisKind] to [dbo].[Analyses] (Balance by default |
--               PersonaHumana), with its CHECK;
--             - make [dbo].[Analyses].[DocumentId] NULLABLE: a persona analysis
--               reads many legajo documents, not one balance file. A CHECK keeps
--               NULL legal ONLY on a PersonaHumana row;
--             - add PreApprovalChecksJson and ConfirmationChecksJson to
--               [dbo].[Analyses] (NULLABLE, ISJSON): the counter-guarantee and
--               Fondo de Riesgo confirmations and the reason each approval act
--               of a persona analysis carried;
--             - create [dbo].[AnalysisParticipants]: the role of each person in
--               an analysis (only Solicitante is written in this phase), with the
--               evaluation FROZEN on the row — the exact extraction runs it read,
--               its filters, indicators and coverage.
-- Date    : 2026-10-20
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, once on Financia-db, then restart the App
--           Service. The API binds ONE static FinanciaDefault connection
--           (Financia-db) and separates tenants by the TenantId column; if the
--           DDL procedure lists further business databases, run it on each of
--           them too, as with 069-071. Nothing below is scoped to one tenant.
--           EF maps [Analyses].[AnalysisKind] and the new table, so publishing
--           first breaks EVERY SELECT and INSERT against [dbo].[Analyses] with
--           "Invalid column name" — the analysis list, the detail, the decision,
--           the pipeline and the credit lines. --verify-schema catches exactly
--           that before the publish.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. [AnalysisKind] is NOT
--           NULL with DEFAULT N'Balance', so the old INSERT (which does not name
--           it) still writes a valid Balance row and every existing row reads
--           Balance. A NULLABLE [DocumentId] accepts every value the old code
--           writes, and CK_Analyses_DocumentId_Kind is satisfied by every Balance
--           row because each one has a document. The two checks columns are
--           NULLABLE with no default. The old code never reads the new table.
--
--           WHY NO INDEX IS DROPPED. SQL Server refuses ALTER COLUMN on a column
--           an index depends on (a NOT NULL → NULL change is allowed over statistics). No numbered script creates
--           an index on [Analyses].[DocumentId] (001 creates only
--           IX_Analyses_TenantId_EntityId and IX_Analyses_TenantId_Status), so
--           none is expected. The script does NOT guess, unlike 048: if one exists
--           anyway, created by hand, it STOPS with an error naming it and alters
--           nothing, and SET NOEXEC ON stops every later batch — dropping an index the
--           DBA added is the DBA's call. Re-run the whole script after deciding.
--           FK_Analyses_DocumentId needs no attention: a nullability change does
--           not require dropping a foreign key, and NULL bypasses the FK check,
--           the same as 048 for FK_Alerts_EntityId.
--
--           SET OPTIONS, THE FIRST BATCH. No object below is filtered, but the
--           session options match 069-071 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           WHAT IS STORED. The evaluation is computed by the API
--           (PersonEvaluationService, version "0.1") from the legajo at the moment
--           of the analysis and frozen here, so a later correction, upload or
--           rebuild of the legajo never changes a past analysis. DocumentVersionsJson
--           records every document the evaluation read with its extraction run and
--           the corrections applied to it; the thresholds used ride inside each
--           filter and indicator. ScoreJson is RESERVED for the score that a later
--           phase adds once the SGR's thresholds arrive; this phase never writes
--           it. The fiador columns of the design (GuaranteeType, SpouseConsent,
--           HousingInopposable) are left to Phase 5 (077).
--
--           FKs. Both new FKs are NO ACTION and are not mapped in EF, as in 070
--           and 071. An analysis is never deleted by the API.
--
--           ⚠ PII. The participant row carries figures and document ids, not the
--           person's name or CUIT, but the figures are a natural person's income
--           and patrimony. The persona analysis's [AnalysisResults] row DOES hold
--           identity: it freezes the legajo's BCRA snapshot (identificacion,
--           denominacion) and ARCA snapshot (domicilio fiscal), exactly as a
--           balance analysis freezes the company's. Treat every row of both — and
--           every export in the rollback — as personal data.
--
--           CHECK CONSTRAINTS follow 069-071: WITH CHECK, each list mirrored by a
--           constants class the tests pin to this script (AnalysisKinds,
--           ParticipantRoles).
--
--           TRANSACTIONS: none. Every guarded block is one atomic statement, except
--           the DocumentId block, whose guard and ALTER sit in one batch.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 069 has run here (the FK below targets PersonLegajos). Expected: 2 rows.
--
--    SELECT name FROM sys.objects
--    WHERE  type = N'U' AND name IN (N'PersonLegajos', N'Analyses');
--
-- 2. No index depends on [Analyses].[DocumentId]. Expected: 0 rows. A row here
--    makes section 3 stop the whole script (see header).
--
--    SELECT i.name FROM sys.indexes i
--    JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--    JOIN   sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[Analyses]') AND c.name = N'DocumentId';
--
-- 3. Every existing analysis has a document (the new CHECK is created WITH
--    CHECK). Expected: 0.
--
--    SELECT COUNT(*) FROM [dbo].[Analyses] WHERE DocumentId IS NULL;
-- ============================================================================

-- ── 0. Session options (see header) ─────────────────────────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. [dbo].[Analyses].[AnalysisKind] ──────────────────────────────────────
-- NOT NULL with a default: SQL Server fills every existing row with N'Balance'
-- in the same statement, and the old INSERT keeps working (see header).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'AnalysisKind'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [AnalysisKind] NVARCHAR(20) NOT NULL
            CONSTRAINT [DF_Analyses_AnalysisKind] DEFAULT (N'Balance');
    PRINT 'Column AnalysisKind added to [dbo].[Analyses] (existing rows: Balance).';
END
ELSE
BEGIN
    PRINT 'Column AnalysisKind already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── 2. CK_Analyses_AnalysisKind ─────────────────────────────────────────────
-- Its own batch: a CHECK cannot be compiled against a column added in the same
-- batch. The code's list is AnalysisKinds; a test pins it to this script.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Analyses_AnalysisKind'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
)
BEGIN
    ALTER TABLE [dbo].[Analyses] WITH CHECK
        ADD CONSTRAINT [CK_Analyses_AnalysisKind]
        CHECK ([AnalysisKind] IN (N'Balance', N'PersonaHumana'));
    PRINT 'Constraint CK_Analyses_AnalysisKind added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Analyses_AnalysisKind already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── 3. [dbo].[Analyses].[DocumentId] → NULL ─────────────────────────────────

IF EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id   = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name        = N'DocumentId'
      AND  is_nullable = 0
)
BEGIN
    DECLARE @blocker SYSNAME =
    (
        SELECT TOP (1) i.name
        FROM   sys.indexes i
        JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE  i.object_id = OBJECT_ID(N'[dbo].[Analyses]')
          AND  c.name      = N'DocumentId'
    );

    IF @blocker IS NOT NULL
    BEGIN
        RAISERROR (N'[dbo].[Analyses].[DocumentId] is used by index %s, which no numbered script creates. DocumentId was NOT altered and the rest of this script will NOT run: the DBA decides whether to drop the index, then re-runs the whole script.', 16, 1, @blocker);
        -- RAISERROR ends only this batch; NOEXEC stops every later one from running (SSMS and sqlcmd
        -- without -b alike), so the CHECK, the table and the indexes are never created over a
        -- DocumentId that is still NOT NULL. The last batch turns it back OFF.
        SET NOEXEC ON;
    END
    ELSE
    BEGIN
        ALTER TABLE [dbo].[Analyses]
            ALTER COLUMN [DocumentId] UNIQUEIDENTIFIER NULL;
        PRINT 'Column [dbo].[Analyses].[DocumentId] is now NULLABLE.';
    END
END
ELSE
BEGIN
    PRINT 'Column [dbo].[Analyses].[DocumentId] is already NULLABLE — skipped.';
END
GO

-- ── 4. CK_Analyses_DocumentId_Kind ──────────────────────────────────────────
-- A NULL document is legal ONLY on a persona humana analysis. A balance
-- analysis without its balance file is a bug the database refuses.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Analyses_DocumentId_Kind'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
)
BEGIN
    ALTER TABLE [dbo].[Analyses] WITH CHECK
        ADD CONSTRAINT [CK_Analyses_DocumentId_Kind]
        CHECK ([DocumentId] IS NOT NULL OR [AnalysisKind] = N'PersonaHumana');
    PRINT 'Constraint CK_Analyses_DocumentId_Kind added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Analyses_DocumentId_Kind already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── 5. [dbo].[Analyses]: the confirmations behind each approval record ──────
-- Persona humana only (M10 Phase 4a). Every approval of a persona analysis must
-- confirm the counter-guarantees and the Fondo de Riesgo limit; the API stores
-- those confirmations and the reason as JSON, paired like ProposedOperationsJson /
-- ConfirmedOperationsJson (061): the pre-approval writes PreApprovalChecksJson, the
-- final approval writes ConfirmationChecksJson, and neither rewrites the other.
-- NULLABLE with no default: the old code never names them. The CHECKs go in their
-- own batches, after the columns they reference.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'PreApprovalChecksJson'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [PreApprovalChecksJson] NVARCHAR(MAX) NULL;   -- the confirmations of the PRE-APPROVAL
    PRINT 'Column PreApprovalChecksJson added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column PreApprovalChecksJson already exists on [dbo].[Analyses] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
      AND  name      = N'ConfirmationChecksJson'
)
BEGIN
    ALTER TABLE [dbo].[Analyses]
        ADD [ConfirmationChecksJson] NVARCHAR(MAX) NULL;   -- the confirmations of the FINAL APPROVAL
    PRINT 'Column ConfirmationChecksJson added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Column ConfirmationChecksJson already exists on [dbo].[Analyses] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Analyses_PreApprovalChecksJson'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
)
BEGIN
    ALTER TABLE [dbo].[Analyses] WITH CHECK
        ADD CONSTRAINT [CK_Analyses_PreApprovalChecksJson]
        CHECK ([PreApprovalChecksJson] IS NULL OR ISJSON([PreApprovalChecksJson]) = 1);
    PRINT 'Constraint CK_Analyses_PreApprovalChecksJson added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Analyses_PreApprovalChecksJson already exists on [dbo].[Analyses] — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_Analyses_ConfirmationChecksJson'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[Analyses]')
)
BEGIN
    ALTER TABLE [dbo].[Analyses] WITH CHECK
        ADD CONSTRAINT [CK_Analyses_ConfirmationChecksJson]
        CHECK ([ConfirmationChecksJson] IS NULL OR ISJSON([ConfirmationChecksJson]) = 1);
    PRINT 'Constraint CK_Analyses_ConfirmationChecksJson added to [dbo].[Analyses].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_Analyses_ConfirmationChecksJson already exists on [dbo].[Analyses] — skipped.';
END
GO

-- ── 6. [dbo].[AnalysisParticipants] ─────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[AnalysisParticipants]
    (
        [ParticipantId]        UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_AnalysisParticipants_ParticipantId] DEFAULT (NEWSEQUENTIALID()),   -- the API supplies its own
        [TenantId]             NVARCHAR(100)    NOT NULL,   -- Firebase tenant SLUG, per script 005; NOT a GUID
        [AnalysisId]           UNIQUEIDENTIFIER NOT NULL,
        [PersonLegajoId]       UNIQUEIDENTIFIER NOT NULL,
        [Role]                 NVARCHAR(20)     NOT NULL,   -- Solicitante | Fiador (Phase 5)
        [AmountAtCharge]       DECIMAL(18, 2)   NOT NULL,   -- the solicitante: the requested amount
        [Currency]             NVARCHAR(3)      NOT NULL,   -- ISO 4217; validated in code (Currencies), per 036/046/052
        [InstallmentAmount]    DECIMAL(18, 2)   NULL,       -- cuota of the requested operation, when stated
        [DocumentVersionsJson] NVARCHAR(MAX)    NOT NULL,   -- the documents, runs and corrections the evaluation read
        [FiltersJson]          NVARCHAR(MAX)    NOT NULL,
        [IndicatorsJson]       NVARCHAR(MAX)    NOT NULL,
        [CoverageJson]         NVARCHAR(MAX)    NOT NULL,
        [ScoreJson]            NVARCHAR(MAX)    NULL,       -- RESERVED: no score in this phase
        [EvaluationVersion]    NVARCHAR(20)     NOT NULL,   -- PersonEvaluationService.Version
        [CreatedAt]            DATETIME2        NOT NULL
            CONSTRAINT [DF_AnalysisParticipants_CreatedAt] DEFAULT (GETUTCDATE()),

        CONSTRAINT [PK_AnalysisParticipants]
            PRIMARY KEY CLUSTERED ([ParticipantId] ASC),

        CONSTRAINT [FK_AnalysisParticipants_Analyses]
            FOREIGN KEY ([AnalysisId]) REFERENCES [dbo].[Analyses] ([AnalysisId]),

        CONSTRAINT [FK_AnalysisParticipants_PersonLegajos]
            FOREIGN KEY ([PersonLegajoId]) REFERENCES [dbo].[PersonLegajos] ([PersonLegajoId]),

        -- The code's list is ParticipantRoles; a test pins it to this script.
        CONSTRAINT [CK_AnalysisParticipants_Role]
            CHECK ([Role] IN (N'Solicitante', N'Fiador')),

        CONSTRAINT [CK_AnalysisParticipants_AmountAtCharge]
            CHECK ([AmountAtCharge] > 0),

        CONSTRAINT [CK_AnalysisParticipants_InstallmentAmount]
            CHECK ([InstallmentAmount] IS NULL OR [InstallmentAmount] > 0),

        CONSTRAINT [CK_AnalysisParticipants_DocumentVersionsJson]
            CHECK (ISJSON([DocumentVersionsJson]) = 1),

        CONSTRAINT [CK_AnalysisParticipants_FiltersJson]
            CHECK (ISJSON([FiltersJson]) = 1),

        CONSTRAINT [CK_AnalysisParticipants_IndicatorsJson]
            CHECK (ISJSON([IndicatorsJson]) = 1),

        CONSTRAINT [CK_AnalysisParticipants_CoverageJson]
            CHECK (ISJSON([CoverageJson]) = 1),

        CONSTRAINT [CK_AnalysisParticipants_ScoreJson]
            CHECK ([ScoreJson] IS NULL OR ISJSON([ScoreJson]) = 1)
    );
    PRINT 'Table [dbo].[AnalysisParticipants] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[AnalysisParticipants] already exists — skipped.';
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'IX_AnalysisParticipants_TenantId_AnalysisId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_AnalysisParticipants_TenantId_AnalysisId]
        ON [dbo].[AnalysisParticipants] ([TenantId] ASC, [AnalysisId] ASC);
    PRINT 'Index [IX_AnalysisParticipants_TenantId_AnalysisId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_AnalysisParticipants_TenantId_AnalysisId] already exists — skipped.';
END
GO

-- ONE row per person and role in an analysis.
IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'UX_AnalysisParticipants_Analysis_Legajo_Role'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_AnalysisParticipants_Analysis_Legajo_Role]
        ON [dbo].[AnalysisParticipants] ([TenantId] ASC, [AnalysisId] ASC, [PersonLegajoId] ASC, [Role] ASC);
    PRINT 'Index [UX_AnalysisParticipants_Analysis_Legajo_Role] created.';
END
ELSE
BEGIN
    PRINT 'Index [UX_AnalysisParticipants_Analysis_Legajo_Role] already exists — skipped.';
END
GO

-- ── 7. Restore execution (see section 3) ────────────────────────────────────

SET NOEXEC OFF;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the nullability, CHECKs, FKs or indexes below. These queries are the
-- evidence.
--
-- 1. The [Analyses] columns. Expected: AnalysisKind 0, DocumentId 1,
--    PreApprovalChecksJson 1, ConfirmationChecksJson 1; then 0.
--
--    SELECT name, is_nullable FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[Analyses]')
--      AND  name IN (N'AnalysisKind', N'DocumentId', N'PreApprovalChecksJson', N'ConfirmationChecksJson');
--    SELECT COUNT(*) FROM [dbo].[Analyses] WHERE AnalysisKind <> N'Balance';
--
-- 2. Every CHECK exists, enabled and TRUSTED. Expected: 12 rows, every one 0 0.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name, is_disabled, is_not_trusted
--    FROM   sys.check_constraints
--    WHERE  name IN (N'CK_Analyses_AnalysisKind', N'CK_Analyses_DocumentId_Kind',
--                    N'CK_Analyses_PreApprovalChecksJson', N'CK_Analyses_ConfirmationChecksJson',
--                    N'CK_AnalysisParticipants_Role', N'CK_AnalysisParticipants_AmountAtCharge',
--                    N'CK_AnalysisParticipants_InstallmentAmount',
--                    N'CK_AnalysisParticipants_DocumentVersionsJson', N'CK_AnalysisParticipants_FiltersJson',
--                    N'CK_AnalysisParticipants_IndicatorsJson', N'CK_AnalysisParticipants_CoverageJson',
--                    N'CK_AnalysisParticipants_ScoreJson');
--
-- 3. The foreign keys. Expected: 3 rows, every one 0 0 NO_ACTION.
--
--    SELECT OBJECT_NAME(parent_object_id) AS TableName, name,
--           is_disabled, is_not_trusted, delete_referential_action_desc
--    FROM   sys.foreign_keys
--    WHERE  name IN (N'FK_Analyses_DocumentId', N'FK_AnalysisParticipants_Analyses',
--                    N'FK_AnalysisParticipants_PersonLegajos');
--
-- 4. The indexes. Expected: these two rows.
--
--    SELECT i.name, i.is_unique, i.has_filter FROM sys.indexes i
--    WHERE  i.object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]') AND i.type = 2;
--    -- expected: IX_AnalysisParticipants_TenantId_AnalysisId      0 0
--    --           UX_AnalysisParticipants_Analysis_Legajo_Role     1 0
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps AnalysisKind and the
-- new table. Run with the same SET options as the script.
--
-- (a) EXPORT FIRST the persona analyses. ⚠ PERSONAL DATA (a person's income and
--     patrimony). Their [Analyses] rows have DocumentId NULL, and the old
--     NOT NULL column cannot hold them: they must be exported and deleted (with
--     their AnalysisResults, AnalysisApprovalVotes, CreditLines that point at them
--     and AnalysisParticipants rows) before (d) can run.
--
--    SELECT * FROM [dbo].[AnalysisParticipants];
--    SELECT * FROM [dbo].[Analyses] WHERE AnalysisKind = N'PersonaHumana';
--    SELECT r.* FROM [dbo].[AnalysisResults] r
--    JOIN   [dbo].[Analyses] a ON a.AnalysisId = r.AnalysisId
--    WHERE  a.AnalysisKind = N'PersonaHumana';
--
-- (b) DROP TABLE [dbo].[AnalysisParticipants];
--
-- (c) ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [CK_Analyses_ConfirmationChecksJson];
--     ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [CK_Analyses_PreApprovalChecksJson];
--     ALTER TABLE [dbo].[Analyses] DROP COLUMN [ConfirmationChecksJson];
--     ALTER TABLE [dbo].[Analyses] DROP COLUMN [PreApprovalChecksJson];
--     ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [CK_Analyses_DocumentId_Kind];
--     ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [CK_Analyses_AnalysisKind];
--     ALTER TABLE [dbo].[Analyses] DROP CONSTRAINT [DF_Analyses_AnalysisKind];
--     ALTER TABLE [dbo].[Analyses] DROP COLUMN [AnalysisKind];
--
-- (d) Only once no row has DocumentId NULL:
--     ALTER TABLE [dbo].[Analyses] ALTER COLUMN [DocumentId] UNIQUEIDENTIFIER NOT NULL;
-- ============================================================================
