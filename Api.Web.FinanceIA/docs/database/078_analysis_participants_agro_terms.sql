-- ============================================================================
-- Script  : 078_analysis_participants_agro_terms.sql
-- Purpose : The agro annual mode terms of the SOLICITANTE on [dbo].[AnalysisParticipants]
--           (persona humana engine 0.7):
--             - [CuotasPorAnio]    TINYINT NULL — how many cuotas a year the stated
--               cuota ([InstallmentAmount]) is: 1 (annual, or a single payment at
--               maturity), 2, 4 or 12; the value the evaluation RESOLVED when the
--               analysis was created;
--             - [AgroModeOverride] BIT     NULL — the analyst's agro annual mode
--               override (1 forces it on, 0 forces it off); NULL detects it from
--               the main ARCA activity;
--           plus two CHECKs: the cuotas a year are one of 1, 2, 4, 12, and a Fiador
--           row carries neither column (a fiador is never evaluated in agro mode).
-- Date    : 2026-10-30
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, IN EVERY FINANCIA (TENANT BUSINESS) DATABASE
--           the DDL procedure lists (one per client, .github/clients.json), then
--           restart the App Service. Nothing below is scoped to one tenant.
--           EF maps both columns, and EF emits every mapped column in every SELECT
--           and INSERT against [AnalysisParticipants], so publishing first breaks
--           the persona analysis (create, detail, rescore) with "Invalid column
--           name". --verify-schema catches exactly that before the publish.
--           NOT for the Default database.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. Both columns are
--           NULLABLE with NO default, so the old INSERT (which names neither)
--           still writes a valid row, and NULL satisfies both new CHECKs. The old
--           code never reads them.
--
--           NULL MEANS THE 0.6 BEHAVIOUR. A row written before this release has
--           [CuotasPorAnio] NULL: its cuota was stated as monthly, and a
--           re-evaluation reads it as 12. [AgroModeOverride] NULL means "detect
--           from ARCA". The new code writes [CuotasPorAnio] on every new
--           Solicitante row and leaves both NULL on Fiador rows.
--
--           WHY A VALUE CHECK ON [CuotasPorAnio]. The set is closed and settled
--           (monthly, quarterly, semiannual, annual or at maturity) and validated
--           in code by the request (400 validation_error) with the same list. The
--           CHECK is the backstop for a hand-written row. Every existing row is
--           NULL, so both CHECKs are created WITH CHECK (trusted).
--
--           SET OPTIONS, THE FIRST BATCH. No object below is filtered, but the
--           session options match 069-077 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           PII: none. A periodicity and a flag; no name, no CUIT, no amount.
--
--           TRANSACTIONS: none. Every guarded block is one atomic statement.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 072 and 077 have run here. Expected: 1 row, and 1 row.
--
--    SELECT name FROM sys.objects WHERE type = N'U' AND name = N'AnalysisParticipants';
--
--    SELECT name FROM sys.check_constraints WHERE name = N'CK_AnalysisParticipants_FiadorId_Role';
--
-- 2. Neither column exists yet. Expected: 0 rows (a re-run shows 2 and skips them).
--
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
--      AND  name IN (N'CuotasPorAnio', N'AgroModeOverride');
-- ============================================================================

-- ── 0. Session options and preconditions (see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[AnalysisParticipants]', N'U') IS NULL
BEGIN
    RAISERROR (N'078 not applied: [dbo].[AnalysisParticipants] does not exist in this database. Is this a Financia database, and have 072 and 077 run? Nothing was altered.', 16, 1);
    -- RAISERROR ends only this batch; NOEXEC stops every later one (SSMS and sqlcmd without -b
    -- alike). The last batch turns it back OFF.
    SET NOEXEC ON;
END
ELSE IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'FiadorId'
)
BEGIN
    RAISERROR (N'078 not applied: [dbo].[AnalysisParticipants].[FiadorId] does not exist, so 077 has not run here. Run 077 first, then the whole of 078. Nothing was altered.', 16, 1);
    SET NOEXEC ON;
END
GO

-- ── 1. [dbo].[AnalysisParticipants].[CuotasPorAnio] ─────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'CuotasPorAnio'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [CuotasPorAnio] TINYINT NULL;
    PRINT 'Column CuotasPorAnio added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column CuotasPorAnio already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 2. [dbo].[AnalysisParticipants].[AgroModeOverride] ──────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'AgroModeOverride'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [AgroModeOverride] BIT NULL;
    PRINT 'Column AgroModeOverride added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column AgroModeOverride already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 3. CK_AnalysisParticipants_CuotasPorAnio ────────────────────────────────
-- Its own batch, like every object below: a constraint cannot be compiled against
-- a column added in the same batch. NULL is legal (a row written before 078).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_AnalysisParticipants_CuotasPorAnio'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisParticipants_CuotasPorAnio]
        CHECK ([CuotasPorAnio] IS NULL OR [CuotasPorAnio] IN (1, 2, 4, 12));
    PRINT 'Constraint CK_AnalysisParticipants_CuotasPorAnio added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_AnalysisParticipants_CuotasPorAnio already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 4. CK_AnalysisParticipants_AgroTerms_Role ───────────────────────────────
-- Fiador ⇒ both NULL: the agro terms are the solicitante's (see header).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_AnalysisParticipants_AgroTerms_Role'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisParticipants_AgroTerms_Role]
        CHECK ([Role] <> N'Fiador' OR ([CuotasPorAnio] IS NULL AND [AgroModeOverride] IS NULL));
    PRINT 'Constraint CK_AnalysisParticipants_AgroTerms_Role added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_AnalysisParticipants_AgroTerms_Role already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 5. Restore execution (see section 0) ────────────────────────────────────

SET NOEXEC OFF;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the nullability or CHECKs below. These queries are the evidence.
--
-- 1. The two columns. Expected: 2 rows, both is_nullable = 1:
--    CuotasPorAnio tinyint, AgroModeOverride bit.
--
--    SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
--      AND  c.name IN (N'CuotasPorAnio', N'AgroModeOverride');
--
-- 2. Both CHECKs exist, enabled and TRUSTED. Expected: 2 rows, every one 0 0.
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.check_constraints
--    WHERE  name IN (N'CK_AnalysisParticipants_CuotasPorAnio',
--                    N'CK_AnalysisParticipants_AgroTerms_Role');
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps both columns. Run with
-- the same SET options as the script.
--
-- (a) EXPORT FIRST the rows that use them, so the periodicity of each analysis is
--     not lost. No personal data: a periodicity and a flag, keyed by row id.
--
--    SELECT [ParticipantId], [AnalysisId], [CuotasPorAnio], [AgroModeOverride]
--    FROM   [dbo].[AnalysisParticipants]
--    WHERE  [CuotasPorAnio] IS NOT NULL OR [AgroModeOverride] IS NOT NULL;
--
-- (b) ALTER TABLE [dbo].[AnalysisParticipants] DROP CONSTRAINT [CK_AnalysisParticipants_AgroTerms_Role];
--     ALTER TABLE [dbo].[AnalysisParticipants] DROP CONSTRAINT [CK_AnalysisParticipants_CuotasPorAnio];
--     ALTER TABLE [dbo].[AnalysisParticipants] DROP COLUMN [AgroModeOverride], [CuotasPorAnio];
-- ============================================================================
