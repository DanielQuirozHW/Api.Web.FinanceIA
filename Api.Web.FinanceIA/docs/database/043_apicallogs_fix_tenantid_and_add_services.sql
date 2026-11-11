-- ============================================================================
-- Script  : 043_apicallogs_fix_tenantid_and_add_services.sql
-- Purpose : Make [dbo].[ApiCallLogs] actually writable by the application:
--           (1) convert [TenantId] from UNIQUEIDENTIFIER to NVARCHAR(100), and
--           (2) extend CK_ApiCallLogs_Service to allow 'LUFE' and 'NOSIS'.
-- Date    : 2026-08-24
-- Author  : Claude (assisted)
-- Notes   : TWO independent defects keep rows out of this table today. Both are
--           invisible at runtime because IApiCallLogService is contractually
--           fire-and-forget — ApiCallLogService catches every exception and logs
--           "ApiCallLog write failed ... continuing". Nothing surfaces to the caller,
--           so the table has simply been staying empty.
--
--           (1) TenantId type. Script 005 migrated TenantId from UNIQUEIDENTIFIER to
--               NVARCHAR(100) on Entities / Documents / AnalysisResults / Analyses so
--               they would accept Firebase tenant slugs (e.g. SgrOneClickSandboxFB2)
--               instead of GUIDs. ApiCallLogs was NOT included, and script 003 (Users)
--               did not cover it either. Meanwhile ApiCallLogConfiguration maps
--               TenantId as a string with HasMaxLength(100), and every caller passes
--               tenantContext.TenantId — a slug. Inserting a non-GUID string into a
--               UNIQUEIDENTIFIER column fails conversion, so EVERY api-call log write
--               is rejected — Claude, ClaudeChat, ARCA, LUFE alike, not just the new one.
--
--           (2) Service whitelist. CK_ApiCallLogs_Service was last extended in script
--               012 to ('BYMA','Dynamics','BCRA','Claude','ClaudeChat','ARCA').
--               LufeService has been writing service: "LUFE" in nine places since it
--               shipped, and the constraint has been rejecting every one. NosisService
--               writes "NOSIS" and would hit the same wall.
--
--           Fixing (2) alone would change nothing — (1) rejects the row first. Both
--           halves are needed for a single log row to land, which is why they ship
--           together.
--
--           This script does NOT touch TenantId on any other table. AuditLogs,
--           SessionLogs and the rest are out of scope; if they show the same mismatch
--           they need their own script and their own verification.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. Unlike 042 this one is not a hard deploy-blocker — the failure
--           mode without it is the same silent no-op the table already has, and no
--           read or write path outside the log itself is affected. Run it first anyway,
--           so the NOSIS release is observable from its first call.
--
--           Idempotent: the type conversion is guarded on the column's current type and
--           the constraint is dropped only if present, so the script can be re-run
--           safely. If a DBA has already converted TenantId out-of-band, part (1) is
--           skipped and only the constraint is rebuilt.
--
--           Data safety: converting UNIQUEIDENTIFIER -> NVARCHAR(100) is lossless. Any
--           pre-existing GUID row becomes its 36-character string form, which fits
--           comfortably. No rows are deleted and none can be truncated.
-- ============================================================================

-- ── (1) ApiCallLogs.TenantId : UNIQUEIDENTIFIER -> NVARCHAR(100) ────────────
-- SQL Server requires dropping any index on a column before altering its type, so the
-- covering index is dropped and recreated around the ALTER — the same shape script 005
-- used for the business tables. Guarded on the current type so a re-run is a no-op.

IF EXISTS (
    SELECT 1
    FROM   sys.columns c
    JOIN   sys.types   t ON t.user_type_id = c.user_type_id
    WHERE  c.object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
      AND  c.name      = N'TenantId'
      AND  t.name      = N'uniqueidentifier'
)
BEGIN
    DROP INDEX IF EXISTS [IX_ApiCallLogs_TenantId_CalledAt] ON [dbo].[ApiCallLogs];

    ALTER TABLE [dbo].[ApiCallLogs]
        ALTER COLUMN [TenantId] NVARCHAR(100) NOT NULL;

    CREATE NONCLUSTERED INDEX [IX_ApiCallLogs_TenantId_CalledAt]
        ON [dbo].[ApiCallLogs] ([TenantId] ASC, [CalledAt] ASC);

    PRINT 'Column TenantId on [dbo].[ApiCallLogs] converted to NVARCHAR(100); index recreated.';
END
ELSE
BEGIN
    PRINT 'Column TenantId on [dbo].[ApiCallLogs] is not UNIQUEIDENTIFIER — conversion skipped.';
END
GO

-- Safety net: if the column was already NVARCHAR but the index had been dropped by an
-- earlier partial run, put it back. Cheap, and leaves the table in the documented shape.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
      AND  name      = N'IX_ApiCallLogs_TenantId_CalledAt'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_ApiCallLogs_TenantId_CalledAt]
        ON [dbo].[ApiCallLogs] ([TenantId] ASC, [CalledAt] ASC);
    PRINT 'Index IX_ApiCallLogs_TenantId_CalledAt recreated on [dbo].[ApiCallLogs].';
END
ELSE
BEGIN
    PRINT 'Index IX_ApiCallLogs_TenantId_CalledAt already present — skipped.';
END
GO

-- ── (2) CK_ApiCallLogs_Service : allow 'LUFE' and 'NOSIS' ───────────────────
-- Drop and recreate with the new allowed values, as scripts 009 and 012 did.
-- WITH CHECK is deliberate: any row already in the table must satisfy the new list.
-- Since the table cannot have accepted a LUFE/NOSIS row, this can only fail if some
-- other unexpected value is present — in which case failing loudly is correct.

IF EXISTS (
    SELECT 1
    FROM   sys.check_constraints
    WHERE  name = N'CK_ApiCallLogs_Service'
      AND  parent_object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
)
BEGIN
    ALTER TABLE [dbo].[ApiCallLogs]
        DROP CONSTRAINT [CK_ApiCallLogs_Service];
    PRINT 'Constraint CK_ApiCallLogs_Service dropped.';
END
ELSE
BEGIN
    PRINT 'Constraint CK_ApiCallLogs_Service not present — nothing to drop.';
END
GO

ALTER TABLE [dbo].[ApiCallLogs] WITH CHECK
    ADD CONSTRAINT [CK_ApiCallLogs_Service]
    CHECK ([Service] IN ('BYMA', 'Dynamics', 'BCRA', 'Claude', 'ClaudeChat', 'ARCA', 'LUFE', 'NOSIS'));
GO

PRINT 'Constraint CK_ApiCallLogs_Service recreated with LUFE and NOSIS allowed.';
GO
