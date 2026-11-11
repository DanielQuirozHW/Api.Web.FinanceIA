-- ============================================================================
-- Script  : 074_fiadores_manual_edit.sql
-- Purpose : Fiadores — manual add and edit. LUFE's socios/autoridades are
--           often wrong or incomplete, so an analyst can now:
--             - add a fiador by hand (Origen = 'Manual'), with or without the
--               CUIT appearing in LUFE;
--             - correct Denominacion, Cargo and PorcAccionista of any ACTIVE
--               fiador, whatever its Origen.
--           This script adds the two columns that record the latest edit:
--             - [UpdatedAt]       DATETIME2        NULL — UTC, NULL = never edited
--             - [UpdatedByUserId] UNIQUEIDENTIFIER NULL — [dbo].[Users].UserId
-- Date    : 2026-10-23
-- Author  : Claude (assisted)
-- Notes   : ⚠ RUN BEFORE DEPLOYING, once on Financia-db, then restart the App
--           Service. The API binds ONE static FinanciaDefault connection
--           (Financia-db) and separates tenants by the TenantId column; if the
--           DDL procedure lists further business databases, run it on each of
--           them too, as with 069-073. Nothing below is scoped to one tenant.
--           EF maps both columns, and EF emits every mapped column in every
--           SELECT and INSERT against [Fiadores], so publishing first breaks
--           the fiadores list, add, remove and refresh-nosis with "Invalid
--           column name". --verify-schema catches exactly that before the
--           publish.
--
--           DDL FIRST IS SAFE FOR THE CODE STILL RUNNING. Both columns are
--           NULLABLE with no default, so the old INSERT (which names neither)
--           still writes a valid row, and every existing row reads "never
--           edited". The old code never reads them.
--
--           NOTHING ELSE IS NEEDED. The existing columns already hold a manual
--           fiador: Denominacion NVARCHAR(300), Cargo NVARCHAR(200),
--           PorcAccionista DECIMAL(5,2), and Origen NVARCHAR(20), whose value
--           'Manual' script 046 reserved for exactly this. Origen keeps NO CHECK
--           constraint: 'Lufe' | 'Manual' stays validated in the API, as 046
--           decided.
--
--           No FK on UpdatedByUserId, matching CreatedByUserId (046) and script
--           030. No index: nothing filters or sorts on either column.
--
--           ⚠ PII. The edit itself (who changed which name) is recorded in
--           [AuditLogs] as 'Fiador.Edited' with the CHANGED FIELD NAMES only —
--           never the values, which identify a natural person.
--
--           Idempotent: each column is guarded, so the script can be re-run.
--
-- Rollback: ALTER TABLE [dbo].[Fiadores] DROP COLUMN [UpdatedByUserId], [UpdatedAt];
--           only AFTER rolling the API back to a release that does not map them.
-- ============================================================================

-- ── 1. [dbo].[Fiadores].[UpdatedAt] ─────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  name      = N'UpdatedAt'
)
BEGIN
    ALTER TABLE [dbo].[Fiadores]
        ADD [UpdatedAt] DATETIME2 NULL;
    PRINT 'Column UpdatedAt added to [dbo].[Fiadores].';
END
ELSE
BEGIN
    PRINT 'Column UpdatedAt already exists on [dbo].[Fiadores] — skipped.';
END
GO

-- ── 2. [dbo].[Fiadores].[UpdatedByUserId] ───────────────────────────────────
-- Users.UserId of the analyst who made the latest edit. No FK, like CreatedByUserId.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[Fiadores]')
      AND  name      = N'UpdatedByUserId'
)
BEGIN
    ALTER TABLE [dbo].[Fiadores]
        ADD [UpdatedByUserId] UNIQUEIDENTIFIER NULL;
    PRINT 'Column UpdatedByUserId added to [dbo].[Fiadores].';
END
ELSE
BEGIN
    PRINT 'Column UpdatedByUserId already exists on [dbo].[Fiadores] — skipped.';
END
GO

-- ── Post-check ──────────────────────────────────────────────────────────────
-- Expect two rows, both is_nullable = 1.

SELECT c.name, t.name AS type_name, c.is_nullable
FROM   sys.columns c
JOIN   sys.types   t ON t.user_type_id = c.user_type_id
WHERE  c.object_id = OBJECT_ID(N'[dbo].[Fiadores]')
  AND  c.name IN (N'UpdatedAt', N'UpdatedByUserId');
GO
