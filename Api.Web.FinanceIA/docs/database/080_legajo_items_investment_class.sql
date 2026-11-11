-- ============================================================================
-- Script  : 080_legajo_items_investment_class.sql
-- Purpose : The class of an investment, for persona humana engine 0.9:
--             - [dbo].[LegajoItems].[InvestmentClass] NVARCHAR(30) NULL — on an
--               Inversion item, TituloPublico, AccionCotizante,
--               ObligacionNegociable, CuotaparteFci or Otra; NULL when not
--               classified. It picks the fiador haircut: pf_haircut_titulo_publico,
--               pf_haircut_accion_on (acciones and ON), pf_haircut_fci, and
--               pf_haircut_inversion for Otra and NULL;
--             - CK_LegajoItems_InvestmentClass: NULL, or a known class on a row
--               whose Kind is Inversion. Kind itself does not change: the item key
--               of an unkeyed row contains the kind, so a new kind would make the
--               reconciliation delete and re-add those rows with their notes.
-- Date    : 2026-10-31
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, IN EVERY FINANCIA (TENANT BUSINESS) DATABASE
--           the DDL procedure lists (one per client, .github/clients.json), then
--           restart the App Service. Nothing below is scoped to one tenant.
--           EF maps the new column, and EF emits every mapped column in every
--           SELECT and INSERT against [LegajoItems], so publishing first breaks
--           the legajo inventory (read, rebuild, item edit) and every persona
--           analysis that reads it with "Invalid column name". --verify-schema
--           catches exactly that before the publish. It does NOT see the CHECK:
--           the post-check below is the evidence for it.
--           NOT for the Default database.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. The column is NULLABLE
--           with NO default, so the old INSERT (which does not name it) still
--           writes a valid row, and NULL satisfies its CHECK whatever the kind.
--           The old code never reads it.
--
--           NULL MEANS "NOT CLASSIFIED" — every row written before this release.
--           Such an investment keeps today's haircut (pf_haircut_inversion) until
--           a new document classifies it or an analyst edits its class. Documents
--           already Ready are not re-extracted.
--
--           WHY THE CHECK IS IN ITS OWN BATCH. A constraint cannot be compiled
--           against a column added in the same batch. Every existing row is NULL,
--           so the CHECK is created WITH CHECK (trusted).
--
--           SET OPTIONS, THE FIRST BATCH. No object below is filtered, but the
--           session options match 069-079 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           PII: none. A class keyed by item row; no name, no CUIT, no amount.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 079 has run here: the kind CHECK admits Efectivo. Expected: 1 row.
--
--    SELECT name FROM sys.check_constraints
--    WHERE  name = N'CK_LegajoItems_Kind' AND definition LIKE N'%Efectivo%';
--
-- 2. The column does not exist yet. Expected: 0 rows (a re-run shows 1 and skips it).
--
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
--      AND  name = N'InvestmentClass';
--
-- 3. The investments that will start unclassified. Informative: a count only.
--
--    SELECT COUNT(*) AS Inversiones FROM [dbo].[LegajoItems] WHERE [Kind] = N'Inversion';
-- ============================================================================

-- ── 0. Session options and preconditions (see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[LegajoItems]', N'U') IS NULL
BEGIN
    RAISERROR (N'080 not applied: [dbo].[LegajoItems] does not exist in this database. Is this a Financia database, and have 071 and 079 run? Nothing was altered.', 16, 1);
    -- RAISERROR ends only this batch; NOEXEC stops every later one (SSMS and sqlcmd without -b
    -- alike). The last batch turns it back OFF.
    SET NOEXEC ON;
END
ELSE IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_LegajoItems_Kind'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
      AND  definition LIKE N'%Efectivo%'
)
BEGIN
    RAISERROR (N'080 not applied: CK_LegajoItems_Kind does not admit Efectivo, so 079 has not run here. Run 079 first, then the whole of 080. Nothing was altered.', 16, 1);
    SET NOEXEC ON;
END
GO

-- ── 1. [dbo].[LegajoItems].[InvestmentClass] ────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
      AND  name      = N'InvestmentClass'
)
BEGIN
    ALTER TABLE [dbo].[LegajoItems]
        ADD [InvestmentClass] NVARCHAR(30) NULL;
    PRINT 'Column InvestmentClass added to [dbo].[LegajoItems].';
END
ELSE
BEGIN
    PRINT 'Column InvestmentClass already exists on [dbo].[LegajoItems] — skipped.';
END
GO

-- ── 2. CK_LegajoItems_InvestmentClass ───────────────────────────────────────
-- Its own batch (see header). The code's list is LegajoInvestmentClasses.Known;
-- a test pins it to this definition. NULL is legal on every kind.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_LegajoItems_InvestmentClass'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
)
BEGIN
    ALTER TABLE [dbo].[LegajoItems] WITH CHECK
        ADD CONSTRAINT [CK_LegajoItems_InvestmentClass]
        CHECK ([InvestmentClass] IS NULL
               OR ([Kind] = N'Inversion'
                   AND [InvestmentClass] IN (N'TituloPublico', N'AccionCotizante', N'ObligacionNegociable',
                                             N'CuotaparteFci', N'Otra')));
    PRINT 'Constraint CK_LegajoItems_InvestmentClass added to [dbo].[LegajoItems].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_LegajoItems_InvestmentClass already exists on [dbo].[LegajoItems] — skipped.';
END
GO

-- ── 3. Restore execution (see section 0) ────────────────────────────────────

SET NOEXEC OFF;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- neither the type, the nullability nor the CHECK below. These queries are the
-- evidence.
--
-- 1. The column. Expected: 1 row, nvarchar, max_length 60 (bytes: 30 characters),
--    is_nullable = 1.
--
--    SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
--      AND  c.name = N'InvestmentClass';
--
-- 2. The CHECK exists, enabled and TRUSTED. Expected: 1 row, 0 0.
--
--    SELECT name, is_disabled, is_not_trusted, definition FROM sys.check_constraints
--    WHERE  name = N'CK_LegajoItems_InvestmentClass';
--
-- 3. Every row is still unclassified. Expected: 0 (before the new code writes any).
--
--    SELECT COUNT(*) FROM [dbo].[LegajoItems] WHERE [InvestmentClass] IS NOT NULL;
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps the column. Run with
-- the same SET options as the script.
--
-- ⚠ ROLLING BACK THE CODE ALONE (keeping this DDL) needs step (b) first: the old
--   code may move an item away from Kind Inversion on a rebuild without clearing
--   its class, which CK_LegajoItems_InvestmentClass refuses, so that legajo's
--   rebuild would fail until the class is cleared.
--
-- (a) EXPORT FIRST the classes, so what the documents and the analysts set is not
--     lost. No personal data: a class keyed by row id.
--
--    SELECT [ItemId], [PersonLegajoId], [InvestmentClass], [EditedFields]
--    FROM   [dbo].[LegajoItems]
--    WHERE  [InvestmentClass] IS NOT NULL;
--
-- (b) UPDATE [dbo].[LegajoItems] SET [InvestmentClass] = NULL WHERE [InvestmentClass] IS NOT NULL;
--
-- (c) ALTER TABLE [dbo].[LegajoItems] DROP CONSTRAINT [CK_LegajoItems_InvestmentClass];
--     ALTER TABLE [dbo].[LegajoItems] DROP COLUMN [InvestmentClass];
--
-- An InvestmentClass token left in [EditedFields] is inert for the old code: it
-- reads the CSV as a set and writes back only the fields it knows.
-- ============================================================================
