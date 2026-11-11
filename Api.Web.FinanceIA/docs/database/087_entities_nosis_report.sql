-- ============================================================================
-- Script  : 087_entities_nosis_report.sql
-- Purpose : The NOSIS Manager "Informe Individual" an analyst uploads on a PyME's
--           NOSIS tab, read in memory by a deterministic parser and kept as JSON:
--             - [dbo].[Entities].[NosisReportJson]       NVARCHAR(MAX) NULL — the
--               company-level sections (summary, 24-month evolution, debt per
--               entity, arrears, employer contributions, rejected cheques per
--               MONTH, acceptance criteria, lawsuits, SISA, SRT, commercial
--               references) plus the import's provenance;
--             - [dbo].[Entities].[NosisReportDate]       DATETIME2 NULL — the
--               report's own date and time, as printed in its footer (Argentina
--               wall-clock time, not UTC);
--             - [dbo].[Entities].[NosisReportImportedAt] DATETIME2 NULL — when it
--               was imported, UTC.
--           The PDF itself is never stored. NULL on all three = no report.
-- Date    : 2026-11-10
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, IN EVERY FINANCIA (TENANT BUSINESS) DATABASE
--           the DDL procedure lists (one per client, .github/clients.json), then
--           restart the App Service. Nothing below is scoped to one tenant.
--           EF maps the new columns, and EF emits every mapped column in every
--           SELECT and INSERT that materialises an [Entities] row, so publishing
--           first breaks entity creation, edition and every NOSIS/BCRA/CASFOG/
--           LUFE/ARCA refresh with "Invalid column name". --verify-schema catches
--           exactly that before the publish.
--           NOT for the Default database.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. The columns are
--           NULLABLE with NO default, so the old INSERT (which does not name
--           them) still writes a valid row. The old code never reads them.
--
--           PII: the parser stores no person. It skips the payroll, the
--           relationship tree, cheque signers, contact data, the per-cheque
--           detail, the lawsuits' parties and case numbers, the referencias'
--           contact persons and the CBU, and drops any stored string shaped like
--           a CUIT/CUIL, DNI, e-mail, phone or CBU. The subject's own CUIT and
--           business name are not in the JSON either. Bank and supplier names
--           (company names) are.
--
--           SET OPTIONS, THE FIRST BATCH. No object below is filtered, but the
--           session options match 069-080 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           IDEMPOTENT: every column is guarded on sys.columns and PRINTs in both
--           branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. This is a Financia database. Expected: 1 row.
--
--    SELECT name FROM sys.tables WHERE name = N'Entities';
--
-- 2. The columns do not exist yet. Expected: 0 rows (a re-run shows 3 and skips them).
--
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
--      AND  name IN (N'NosisReportJson', N'NosisReportDate', N'NosisReportImportedAt');
-- ============================================================================

-- ── 0. Session options and preconditions (see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[Entities]', N'U') IS NULL
BEGIN
    RAISERROR (N'087 not applied: [dbo].[Entities] does not exist in this database. Is this a Financia database? Nothing was altered.', 16, 1);
    -- RAISERROR ends only this batch; NOEXEC stops every later one (SSMS and sqlcmd without -b
    -- alike). The last batch turns it back OFF.
    SET NOEXEC ON;
END
GO

-- ── 1. [dbo].[Entities].[NosisReportJson] ───────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'NosisReportJson'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [NosisReportJson] NVARCHAR(MAX) NULL;
    PRINT 'Column NosisReportJson added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column NosisReportJson already exists on [dbo].[Entities] — skipped.';
END
GO

-- ── 2. [dbo].[Entities].[NosisReportDate] ───────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'NosisReportDate'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [NosisReportDate] DATETIME2 NULL;
    PRINT 'Column NosisReportDate added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column NosisReportDate already exists on [dbo].[Entities] — skipped.';
END
GO

-- ── 3. [dbo].[Entities].[NosisReportImportedAt] ─────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Entities]')
      AND  name      = N'NosisReportImportedAt'
)
BEGIN
    ALTER TABLE [dbo].[Entities]
        ADD [NosisReportImportedAt] DATETIME2 NULL;
    PRINT 'Column NosisReportImportedAt added to [dbo].[Entities].';
END
ELSE
BEGIN
    PRINT 'Column NosisReportImportedAt already exists on [dbo].[Entities] — skipped.';
END
GO

-- ── 4. Restore execution (see section 0) ────────────────────────────────────

SET NOEXEC OFF;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- neither the type nor the nullability. This query is the evidence.
--
-- 1. The columns. Expected: 3 rows —
--      NosisReportJson        nvarchar   max_length -1  is_nullable 1
--      NosisReportDate        datetime2  max_length  8  is_nullable 1
--      NosisReportImportedAt  datetime2  max_length  8  is_nullable 1
--
--    SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[Entities]')
--      AND  c.name IN (N'NosisReportJson', N'NosisReportDate', N'NosisReportImportedAt');
--
-- 2. No report yet. Expected: 0 (before the new code imports any).
--
--    SELECT COUNT(*) FROM [dbo].[Entities] WHERE [NosisReportJson] IS NOT NULL;
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps the columns. Rolling
-- back the code alone and keeping the columns is safe: the old code ignores them.
--
-- (a) Nothing to export that cannot be re-created: the analyst can upload the
--     report again. Count what is lost:
--
--    SELECT COUNT(*) FROM [dbo].[Entities] WHERE [NosisReportJson] IS NOT NULL;
--
-- (b) ALTER TABLE [dbo].[Entities] DROP COLUMN [NosisReportJson], [NosisReportDate], [NosisReportImportedAt];
-- ============================================================================
