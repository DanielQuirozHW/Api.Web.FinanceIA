-- ============================================================================
-- Script  : 079_person_installments_and_cash_kind.sql
-- Purpose : Two changes for persona humana engine 0.8:
--             - [dbo].[AnalysisParticipants].[ExistingInstallmentsAmount]
--               DECIMAL(18,2) NULL — the monthly cuotas vigentes the analyst
--               DECLARED for the person (solicitante or fiador), in pesos. When
--               present it replaces the estimate from the Central de Deudores
--               balance (pf_cuota_estimada_pct_saldo) in cuota_ingreso,
--               cobertura_servicio_deuda and the fiador's cuota_ingreso_estres;
--               plus CK_AnalysisParticipants_ExistingInstallmentsAmount (NULL or
--               >= 0);
--             - CK_LegajoItems_Kind widened with N'Efectivo': the Bienes
--               Personales rubro DineroEfectivo now reconciles into its own item
--               kind, so the fiador coverage can leave cash out with its own
--               reason (efectivo_sin_respaldo) whatever pf_haircut_otro says.
-- Date    : 2026-10-30
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, IN EVERY FINANCIA (TENANT BUSINESS) DATABASE
--           the DDL procedure lists (one per client, .github/clients.json), then
--           restart the App Service. Nothing below is scoped to one tenant.
--           EF maps the new column, and EF emits every mapped column in every
--           SELECT and INSERT against [AnalysisParticipants], so publishing first
--           breaks the persona analysis (create, detail, rescore) with "Invalid
--           column name". --verify-schema catches exactly that before the publish.
--           It does NOT see the widened CHECK: without it, the first rebuild of a
--           legajo whose Bienes Personales declares cash fails on
--           CK_LegajoItems_Kind. The post-check below is the evidence for that.
--           NOT for the Default database.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. The column is NULLABLE
--           with NO default, so the old INSERT (which does not name it) still
--           writes a valid row, and NULL satisfies its CHECK. The old code never
--           reads it. The widened CHECK admits a superset of what the old code
--           writes.
--
--           NULL MEANS "USE THE ESTIMATE" — today's behaviour. Every row written
--           before this release is NULL and a re-evaluation of it keeps
--           estimating. ZERO IS A DECLARATION: the analyst states the person pays
--           no installments at all, which is not the same as not stating it.
--
--           WHY BOTH CHANGES IN ONE SCRIPT. They ship in the same release and
--           belong to the same engine version; splitting them would only add a
--           deploy step.
--
--           WHY A TRANSACTION IN SECTION 3. A CHECK cannot be altered in place:
--           it is dropped and re-added. Doing both under XACT_ABORT in one
--           transaction means the table is never left without the constraint.
--           Every existing row holds one of the old kinds, so the new CHECK is
--           created WITH CHECK (trusted).
--
--           SET OPTIONS, THE FIRST BATCH. No object below is filtered, but the
--           session options match 069-078 so the script runs the same way in SSMS,
--           Azure Data Studio and sqlcmd -I.
--
--           PII: none. An amount keyed by participant row, and a list of kinds;
--           no name, no CUIT.
--
--           IDEMPOTENT: every object is guarded on its sys.* view and PRINTs in
--           both branches; a re-run changes nothing.
-- ============================================================================

-- ── Pre-check (DBA reference only; NOT executed; READ-ONLY, run BEFORE) ─────
--
-- 1. 071 and 078 have run here. Expected: 1 row, and 1 row.
--
--    SELECT name FROM sys.check_constraints WHERE name = N'CK_LegajoItems_Kind';
--
--    SELECT name FROM sys.check_constraints WHERE name = N'CK_AnalysisParticipants_CuotasPorAnio';
--
-- 2. The column does not exist yet. Expected: 0 rows (a re-run shows 1 and skips it).
--
--    SELECT name FROM sys.columns
--    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
--      AND  name = N'ExistingInstallmentsAmount';
--
-- 3. The kinds stored today. Expected: only Inmueble, Rodado, Participacion,
--    Deposito, Inversion, Credito, Deuda, Otro.
--
--    SELECT [Kind], COUNT(*) AS Items FROM [dbo].[LegajoItems] GROUP BY [Kind];
-- ============================================================================

-- ── 0. Session options and preconditions (see header) ───────────────────────

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[AnalysisParticipants]', N'U') IS NULL
   OR OBJECT_ID(N'[dbo].[LegajoItems]', N'U') IS NULL
BEGIN
    RAISERROR (N'079 not applied: [dbo].[AnalysisParticipants] or [dbo].[LegajoItems] does not exist in this database. Is this a Financia database, and have 071, 072 and 078 run? Nothing was altered.', 16, 1);
    -- RAISERROR ends only this batch; NOEXEC stops every later one (SSMS and sqlcmd without -b
    -- alike). The last batch turns it back OFF.
    SET NOEXEC ON;
END
ELSE IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'CuotasPorAnio'
)
BEGIN
    RAISERROR (N'079 not applied: [dbo].[AnalysisParticipants].[CuotasPorAnio] does not exist, so 078 has not run here. Run 078 first, then the whole of 079. Nothing was altered.', 16, 1);
    SET NOEXEC ON;
END
GO

-- ── 1. [dbo].[AnalysisParticipants].[ExistingInstallmentsAmount] ────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
      AND  name      = N'ExistingInstallmentsAmount'
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants]
        ADD [ExistingInstallmentsAmount] DECIMAL(18,2) NULL;
    PRINT 'Column ExistingInstallmentsAmount added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Column ExistingInstallmentsAmount already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 2. CK_AnalysisParticipants_ExistingInstallmentsAmount ───────────────────
-- Its own batch: a constraint cannot be compiled against a column added in the
-- same batch. NULL is legal (not declared); zero is legal (declared: none).

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_AnalysisParticipants_ExistingInstallmentsAmount'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
)
BEGIN
    ALTER TABLE [dbo].[AnalysisParticipants] WITH CHECK
        ADD CONSTRAINT [CK_AnalysisParticipants_ExistingInstallmentsAmount]
        CHECK ([ExistingInstallmentsAmount] IS NULL OR [ExistingInstallmentsAmount] >= 0);
    PRINT 'Constraint CK_AnalysisParticipants_ExistingInstallmentsAmount added to [dbo].[AnalysisParticipants].';
END
ELSE
BEGIN
    PRINT 'Constraint CK_AnalysisParticipants_ExistingInstallmentsAmount already exists on [dbo].[AnalysisParticipants] — skipped.';
END
GO

-- ── 3. CK_LegajoItems_Kind, widened with Efectivo ───────────────────────────
-- The code's list is LegajoItemKinds.Known; a test pins it to this definition.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name             = N'CK_LegajoItems_Kind'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
      AND  definition LIKE N'%Efectivo%'
)
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1
        FROM   sys.check_constraints
        WHERE  name             = N'CK_LegajoItems_Kind'
          AND  parent_object_id = OBJECT_ID(N'[dbo].[LegajoItems]')
    )
        ALTER TABLE [dbo].[LegajoItems] DROP CONSTRAINT [CK_LegajoItems_Kind];

    ALTER TABLE [dbo].[LegajoItems] WITH CHECK
        ADD CONSTRAINT [CK_LegajoItems_Kind]
            CHECK ([Kind] IN (N'Inmueble', N'Rodado', N'Participacion', N'Deposito', N'Efectivo', N'Inversion',
                              N'Credito', N'Deuda', N'Otro'));

    COMMIT TRANSACTION;
    PRINT 'Constraint CK_LegajoItems_Kind on [dbo].[LegajoItems] now admits Efectivo.';
END
ELSE
BEGIN
    PRINT 'Constraint CK_LegajoItems_Kind on [dbo].[LegajoItems] already admits Efectivo — skipped.';
END
GO

-- ── 4. Restore execution (see section 0) ────────────────────────────────────

SET NOEXEC OFF;
GO

-- ── Post-check (DBA reference only; NOT executed; READ-ONLY, run AFTER) ─────
-- --verify-schema checks only that the mapped TABLES and COLUMNS exist. It sees
-- neither the nullability nor the CHECKs below. These queries are the evidence.
--
-- 1. The column. Expected: 1 row, decimal 18 2, is_nullable = 1.
--
--    SELECT c.name, t.name AS type_name, c.precision, c.scale, c.is_nullable
--    FROM   sys.columns c
--    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
--    WHERE  c.object_id = OBJECT_ID(N'[dbo].[AnalysisParticipants]')
--      AND  c.name = N'ExistingInstallmentsAmount';
--
-- 2. Both CHECKs exist, enabled and TRUSTED, and the kind list names Efectivo.
--    Expected: 2 rows, every one 0 0; the second definition contains N'Efectivo'.
--
--    SELECT name, is_disabled, is_not_trusted, definition FROM sys.check_constraints
--    WHERE  name IN (N'CK_AnalysisParticipants_ExistingInstallmentsAmount',
--                    N'CK_LegajoItems_Kind');
-- ============================================================================

-- ── Rollback (DBA reference only; NOT executed) ─────────────────────────────
-- Roll the APPLICATION back first: the matching code maps the column and writes
-- Efectivo items. Run with the same SET options as the script.
--
-- (a) EXPORT FIRST the declared installments, so what the analyst stated is not
--     lost. No personal data: an amount keyed by row id.
--
--    SELECT [ParticipantId], [AnalysisId], [Role], [ExistingInstallmentsAmount]
--    FROM   [dbo].[AnalysisParticipants]
--    WHERE  [ExistingInstallmentsAmount] IS NOT NULL;
--
-- (b) Cash items back to Otro (the next rebuild by the old code writes Otro anyway):
--
--    UPDATE [dbo].[LegajoItems] SET [Kind] = N'Otro' WHERE [Kind] = N'Efectivo';
--
-- (c) ALTER TABLE [dbo].[AnalysisParticipants] DROP CONSTRAINT [CK_AnalysisParticipants_ExistingInstallmentsAmount];
--     ALTER TABLE [dbo].[AnalysisParticipants] DROP COLUMN [ExistingInstallmentsAmount];
--     ALTER TABLE [dbo].[LegajoItems] DROP CONSTRAINT [CK_LegajoItems_Kind];
--     ALTER TABLE [dbo].[LegajoItems] WITH CHECK ADD CONSTRAINT [CK_LegajoItems_Kind]
--         CHECK ([Kind] IN (N'Inmueble', N'Rodado', N'Participacion', N'Deposito', N'Inversion',
--                           N'Credito', N'Deuda', N'Otro'));
-- ============================================================================
