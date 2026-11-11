-- ============================================================================
-- Script  : 082_scoring_v11_acid_test_floor.sql
-- Purpose : Move a tenant's ScoringConfig row for critical_floor_prueba_acida
--           from the PRE-v11 default (60) to the scoring algorithm version 11
--           default (50).
-- Date    : 2026-11-04
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS IS A DATA SCRIPT (DML), NOT A SCHEMA SCRIPT — like 066 and
--           068. It changes no object, so the API does NOT depend on it: the v11
--           deploy is safe before, after, or without it, and the --verify-schema
--           gate neither needs nor sees it. NO App Service restart is needed
--           (config is read per request, not cached by EF).
--
--           WHY IT EXISTS. Version 11 lowered the DEFAULT of
--           critical_floor_prueba_acida in ScoringDefaults.cs from 60 to 50, and
--           made that floor conditional: it applies only while liquidez corriente
--           is measured and strictly below threshold_liquidez_corriente_warn
--           (switch: critical_floor_acid_test_requires_weak_current_ratio, code
--           default 1). A default reaches only a tenant with NO row for the key.
--           Floor rows exist only where 014 seeded them (the sandbox, at 60) or
--           the DBA / a direct API call wrote them — the Settings page never
--           exposes the floor keys. Those rows keep the floor at 60 until this
--           script runs.
--
--           WHAT IT TOUCHES — deliberately narrow:
--             - Only critical_floor_prueba_acida, and only a row whose value is
--               EXACTLY the old default 60. A value someone chose on purpose
--               (55, 70, …) is not a leftover default and is left alone.
--             - Only the BASE key, matched exactly: a …__sector__… row (none is
--               read for floors today) is never updated. Query 0 lists them.
--             - The new flag critical_floor_acid_test_requires_weak_current_ratio
--               needs NO row: its code default is 1. Query 0 shows whether one
--               exists (a row at 0 means that tenant chose version-10 behaviour).
--
--           ⚠ It BYPASSES the Settings audit trail: a direct UPDATE writes no
--           AuditLogs row, unlike a PUT /api/settings/scoring. Record the run
--           (tenant, date, who) in the execution history of README.md.
--
--           RUN IT ONCE PER TENANT, WITH @TenantId SET. Every statement is scoped
--           to it. The placeholder N'<tenant>' makes the script THROW before it
--           reads or writes anything, so it cannot be run by accident as a
--           "pending numbered script". Same shape as 068.
--
--           IDEMPOTENT: a second run for the same tenant finds no row at 60 and
--           updates nothing.
--
--           ONE TRANSACTION with XACT_ABORT. For a dry run, change the final
--           COMMIT to ROLLBACK: the PRINT and the "after" SELECT still show what
--           it would have done.
--
--           Rationale: docs/db-changes/082_scoring_v11_acid_test_floor.md
-- ============================================================================

SET XACT_ABORT ON;

-- ⚠ Set this to the tenant slug before running.
DECLARE @TenantId NVARCHAR(100) = N'<tenant>';

IF @TenantId IS NULL OR LTRIM(RTRIM(@TenantId)) = N'' OR @TenantId = N'<tenant>'
    THROW 50082, N'082: set @TenantId to the tenant slug before running this script. Nothing was read or changed.', 1;

-- ── 0. Before (read-only): the acid-test floor and its switch, sector rows included ──

SELECT TenantId, ConfigKey, ConfigValue, UpdatedAt
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND (ConfigKey LIKE N'critical\_floor\_prueba\_acida%'                         ESCAPE N'\'
   OR  ConfigKey LIKE N'critical\_floor\_acid\_test\_requires\_weak\_current\_ratio%' ESCAPE N'\')
ORDER  BY ConfigKey;

-- No GO between here and COMMIT: a GO ends the batch, and @TenantId with it.

BEGIN TRANSACTION;

-- ── 1. critical_floor_prueba_acida: 60 → 50, only a row still at the old default ──

UPDATE [dbo].[ScoringConfig]
SET    ConfigValue = 50,
       UpdatedAt   = SYSUTCDATETIME()
WHERE  TenantId    = @TenantId
  AND  ConfigKey   = N'critical_floor_prueba_acida'
  AND  ConfigValue = 60;

PRINT N'critical_floor_prueba_acida 60 -> 50: ' + CAST(@@ROWCOUNT AS NVARCHAR(20)) + N' row(s).';

-- ── 2. After (read-only): the same rows, to read back before committing ──

SELECT TenantId, ConfigKey, ConfigValue, UpdatedAt
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND (ConfigKey LIKE N'critical\_floor\_prueba\_acida%'                         ESCAPE N'\'
   OR  ConfigKey LIKE N'critical\_floor\_acid\_test\_requires\_weak\_current\_ratio%' ESCAPE N'\')
ORDER  BY ConfigKey;

COMMIT TRANSACTION;   -- ROLLBACK TRANSACTION for a dry run
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
--    No base row left at the old default. Expected: 0 rows.
--
--    SELECT TenantId, ConfigKey, ConfigValue
--    FROM   [dbo].[ScoringConfig]
--    WHERE  TenantId = N'<tenant>'
--      AND  ConfigKey = N'critical_floor_prueba_acida'
--      AND  ConfigValue = 60;
--
-- ROLLBACK (by hand, per tenant, only if the decision is reversed):
--    UPDATE [dbo].[ScoringConfig] SET ConfigValue = 60, UpdatedAt = SYSUTCDATETIME()
--    WHERE  TenantId = N'<tenant>' AND ConfigKey = N'critical_floor_prueba_acida';
--    To restore the version-10 RULE as well (floor on any critical acid test), add a row
--    critical_floor_acid_test_requires_weak_current_ratio = 0 for that tenant.
-- ============================================================================
