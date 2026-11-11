-- ============================================================================
-- Script  : 077_analysis_participants_fiador.sql
-- Purpose : The FIADOR columns of [dbo].[AnalysisParticipants] (M10, Phase 5),
--           the ones 072 left out of the design ("GuaranteeType, SpouseConsent,
--           HousingInopposable ... left to Phase 5"):
--             - [FiadorId]              UNIQUEIDENTIFIER NULL — the [dbo].[Fiadores]
--               row a Fiador participant stands for; FK, NO ACTION;
--             - [GuaranteeType]         NVARCHAR(30)     NULL — the kind of
--               guarantee the fiador gives;
--             - [SpouseConsent]         BIT              NULL — the spouse's
--               assent (asentimiento conyugal) was obtained;
--             - [HousingInopposable]    BIT              NULL — the fiador's
--               home is protected (vivienda inoponible);
--             - [GuaranteesGivenAmount] DECIMAL(18,2)    NULL — the guarantees the
--               fiador already gives elsewhere;
--           plus two CHECKs: a Fiador row names its fiador and a Solicitante row
--           names none, and the amount is never negative.
-- Date    : 2026-10-29
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, IN EVERY FINANCIA (TENANT BUSINESS) DATABASE
--           the DDL procedure lists (one per client, .github/clients.json), then
--           restart the App Service. Nothing below is scoped to one tenant.
--           EF maps the five columns, and EF emits every mapped column in every
--           SELECT and INSERT against [AnalysisParticipants], so publishing first
--           breaks the persona analysis (create, detail, rescore) with "Invalid
--           column name". --verify-schema catches exactly that before the publish.
--           NOT for the Default database.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. Every column is
--           NULLABLE with NO default, so the old INSERT (which names none of them)
--           still writes a valid row. The old code writes only Role = 'Solicitante'
--           rows, and a Solicitante row with [FiadorId] NULL satisfies both new
--           CHECKs. The old code never reads the new columns. This release does
--           not write them either: they wait for the fiador evaluation (5a-2).
--
--           WHY THE CHECK IS AN IMPLICATION PER ROLE. CK_AnalysisParticipants_Role
--           (072) admits Solicitante and Fiador; the new CHECK says, for each of
--           them, whether the row names a fiador: Fiador ⇒ FiadorId IS NOT NULL,
--           Solicitante ⇒ FiadorId IS NULL. A Fiador row cannot exist without its
--           [Fiadores] row, and a solicitante is never anyone's fiador in the same
--           analysis. No code writes a Fiador row yet, so every existing row is a
--           Solicitante with FiadorId NULL and the CHECK is created WITH CHECK
--           (trusted). Should a Fiador row exist anyway, written by hand, section 0
--           STOPS with an error and alters nothing (SET NOEXEC ON, as 072).
--
--           NO VALUE CHECK ON [GuaranteeType]. Its vocabulary belongs to the phase
--           that writes it (5a-2), and is validated in code there, as [Origen] of
--           [Fiadores] is (046). A later script may add the CHECK once the list is
--           settled.
--
--           FKs. FK_AnalysisParticipants_Fiadores is NO ACTION and is not mapped in
--           EF, like the other two of this table (072). A fiador is never deleted
--           (removal is the soft Status = 'Removed', 046), so the FK is never
--           probed by a delete. NO INDEX on [FiadorId]: nothing reads by it yet.
--
--           SET OPTIONS, THE FIRST BATCH. No object below is filtered, but the
--           session options match 069-072 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           ⚠ PII. [SpouseConsent] implies the fiador's marital status and
--           [GuaranteesGivenAmount] is a natural person's financial figure. Neither
--           is logged nor sent to a prompt, and the row carries no name or CUIT.
--           Treat every row, and every export in the rollback, as personal data.
--
--           TRANSACTIONS: none. Every guarded block is one atomic statement.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 046 and 072 have run here. Expected: 2 rows.
--
--    SELECT name FROM sys.objects
--    WHERE  type = N'U' AND name IN (N'Fiadores', N'AnalysisParticipants');
--
-- 2. No participant is a Fiador yet (the new CHECK is created WITH CHECK).
--    Expected: 0. Anything else makes section 0 stop the whole script.
--
--    SELECT COUNT(*) FROM [dbo].[AnalysisParticipants] WHERE [Role] <> N'Solicitante';
-- ============================================================================

-- ── 0. Session options and preconditions (see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[AnalysisParticipants]', N'U') IS NULL
   OR OBJECT_ID(N'[dbo].[Fiadores]', N'U') IS NULL
BEGIN
    RAISERROR (N'077 not applied: [dbo].[AnalysisParticipants] or [dbo].[Fiadores] does not exist in this database. Is this a Financia database, and have 046 and 072 run? Nothing was altered.', 16, 1);
    -- RAISERROR ends only this batch; NOEXEC stops every later one (SSMS and sqlcmd without -b
    -- alike). The last batch turns it back OFF.
    SET NOEXEC ON;
END
ELSE IF EXISTS (SELECT 1 FROM [dbo].[AnalysisParticipants] WHERE [Role] <> N'Solicitante')
BEGIN
    RAISERROR (N'077 not applied: [dbo].[AnalysisParticipants] already holds a row whose Role is not Solicitante, which no released code writes. Nothing was altered: the DBA reviews those rows, then re-runs the whole script.', 16, 1);
    SET NOEXEC ON;
END
GO

-- ── 1. [dbo].[AnalysisParticipants].[FiadorId] ──────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'FiadorId'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [FiadorId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column FiadorId added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column FiadorId already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 2. [dbo].[AnalysisParticipants].[GuaranteeType] ─────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'GuaranteeType'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [GuaranteeType] NVARCHAR(30) NULL;
    PRINT 'Column GuaranteeType added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column GuaranteeType already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 3. [dbo].[AnalysisParticipants].[SpouseConsent] ─────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'SpouseConsent'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [SpouseConsent] BIT NULL;
    PRINT 'Column SpouseConsent added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column SpouseConsent already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 4. [dbo].[AnalysisParticipants].[HousingInopposable] ────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'HousingInopposable'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [HousingInopposable] BIT NULL;
    PRINT 'Column HousingInopposable added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column HousingInopposable already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 5. [dbo].[AnalysisParticipants].[GuaranteesGivenAmount] ─────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'GuaranteesGivenAmount'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [GuaranteesGivenAmount] DECIMAL(18, 2) NULL;
    PRINT 'Column GuaranteesGivenAmount added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column GuaranteesGivenAmount already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 6. FK_AnalysisParticipants_Fiadores ─────────────────────────────────────
-- Its own batch, like every object below: a constraint cannot be compiled against
-- a column added in the same batch.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.foreign_keys
    WHERE  name             = N'FK_AnalysisParticipants_Fiadores'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants] WITH CHECK
        ADD CONSTRAINT [FK_AnalysisParticipants_Fiadores]
        FOREIGN KEY ([FiadorId]) REFERENCES [dbo].[Fiadores] ([FiadorId]);
    PRINT 'Constraint FK_AnalysisParticipants_Fiadores added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Constraint FK_AnalysisParticipants_Fiadores already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 7. CK_AnalysisParticipants_FiadorId_Role ────────────────────────────────
-- Fiador ⇒ FiadorId IS NOT NULL; Solicitante ⇒ FiadorId IS NULL (see header).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_AnalysisParticipants_FiadorId_Role'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisParticipants_FiadorId_Role]
        CHECK (([Role] <> N'Fiador' OR [FiadorId] IS NOT NULL) AND ([Role] <> N'Solicitante' OR [FiadorId] IS NULL));
    PRINT 'Constraint CK_AnalysisParticipants_FiadorId_Role added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_AnalysisParticipants_FiadorId_Role already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 8. CK_AnalysisParticipants_GuaranteesGivenAmount ────────────────────────
-- Zero is legal (the fiador gives no other guarantee); NULL means not stated.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_AnalysisParticipants_GuaranteesGivenAmount'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisParticipants_GuaranteesGivenAmount]
        CHECK ([GuaranteesGivenAmount] IS NULL OR [GuaranteesGivenAmount] >= 0);
    PRINT 'Constraint CK_AnalysisParticipants_GuaranteesGivenAmount added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_AnalysisParticipants_GuaranteesGivenAmount already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 9. Restore execution (see section 0) ────────────────────────────────────

SET NOEXEC OFF;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- none of the nullability, CHECKs or FKs below. These queries are the evidence.
--
-- 1. The five columns. Expected: 5 rows, every one is_nullable = 1:
--    FiadorId uniqueidentifier, GuaranteeType nvarchar 60 (bytes), SpouseConsent bit,
--    HousingInopposable bit, GuaranteesGivenAmount decimal 18 2.
--
--    SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
--      AND  c.name IN (N'FiadorId', N'GuaranteeType', N'SpouseConsent',
--                      N'HousingInopposable', N'GuaranteesGivenAmount');
--
-- 2. Both CHECKs exist, enabled and TRUSTED. Expected: 2 rows, every one 0 0.
--
--    SELECT name, is_disabled, is_not_trusted FROM sys.check_constraints
--    WHERE  name IN (N'CK_AnalysisParticipants_FiadorId_Role',
--                    N'CK_AnalysisParticipants_GuaranteesGivenAmount');
--
-- 3. The foreign key. Expected: 1 row, 0 0 NO_ACTION.
--
--    SELECT name, is_disabled, is_not_trusted, delete_referential_action_desc
--    FROM   sys.foreign_keys
--    WHERE  name = N'FK_AnalysisParticipants_Fiadores';
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps the five columns. Run
-- with the same SET options as the script.
--
-- (a) EXPORT FIRST any row that uses them. ⚠ PERSONAL DATA (marital status, a
--     natural person's guarantees). This release writes none, so expect 0 rows.
--
--    SELECT * FROM [dbo].[AnalysisParticipants]
--    WHERE  [FiadorId] IS NOT NULL OR [GuaranteeType] IS NOT NULL OR [SpouseConsent] IS NOT NULL
--       OR  [HousingInopposable] IS NOT NULL OR [GuaranteesGivenAmount] IS NOT NULL;
--
-- (b) ALTER TABLE [dbo].[AnalysisParticipants] DROP CONSTRAINT [CK_AnalysisParticipants_GuaranteesGivenAmount];
--     ALTER TABLE [dbo].[AnalysisParticipants] DROP CONSTRAINT [CK_AnalysisParticipants_FiadorId_Role];
--     ALTER TABLE [dbo].[AnalysisParticipants] DROP CONSTRAINT [FK_AnalysisParticipants_Fiadores];
--     ALTER TABLE [dbo].[AnalysisParticipants] DROP COLUMN [GuaranteesGivenAmount], [HousingInopposable],
--                                                          [SpouseConsent], [GuaranteeType], [FiadorId];
-- ============================================================================
