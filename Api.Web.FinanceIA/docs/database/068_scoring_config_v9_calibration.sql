-- ============================================================================
-- Script  : 068_scoring_config_v9_calibration.sql
-- Purpose : Move tenant ScoringConfig rows that still hold the PRE-v9 default
--           values of three keys onto the scoring algorithm version 9 values:
--             critical_floor_trigger_threshold   85         → 100
--             threshold_leverage_warn            1.50       → 2.33
--             threshold_leverage_danger          2.50       → 5.67
-- Date    : 2026-09-24
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS IS A DATA SCRIPT (DML), NOT A SCHEMA SCRIPT — the second of
--           its kind after 066. It changes no object, so the API does NOT depend
--           on it: the v9 deploy is safe before, after, or without it, and the
--           --verify-schema gate neither needs nor sees it. NO App Service
--           restart is needed (config is read per request, not cached by EF).
--
--           ⚠ RUN IT ONLY AFTER THE ANALYST HAS CONFIRMED THE NEW CALIBRATION
--           FOR THE TENANT. It changes scores: every analysis scored AFTER it
--           runs uses the v9 values. Analyses already saved keep their score
--           and their scoringAlgoVersion — nothing recomputes them.
--
--           WHY IT EXISTS. Version 9 changed the DEFAULTS in ScoringDefaults.cs
--           (a ratio is critical only at its DANGER threshold; leverage uses the
--           endeudamiento anchors 0.50 / 0.70 / 0.85 restated as
--           L = E / (1 − E) = 1.00 / 2.33 / 5.67). A default only reaches a
--           tenant with NO row for the key, and it does so PER KEY. Rows exist
--           wherever:
--             - 007 and 014 seeded them (SgrOneClickSandboxFB2: every threshold,
--               including leverage 1.0 / 1.5 / 2.5, AND trigger 85), or
--             - an Admin saved the Settings page, which writes a row for every
--               key it submits — at whatever value was showing, i.e. the old
--               default. SettingsService.BuildDto never exposes the trigger
--               (critical_floor_trigger_threshold), so that page writes leverage
--               rows but NO trigger row.
--
--           THREE TENANT STATES, and which one changes its score at the deploy:
--             A. No rows (trigger, leverage) → CHANGES at deploy: trigger 100 and
--                leverage 1.00 / 2.33 / 5.67 from the defaults. 068: nothing to do.
--             B. Full old rows (trigger 85 + leverage 1.0 / 1.5 / 2.5, e.g. the
--                sandbox) → NO change at deploy. 068 moves both.
--             C. Mixed (leverage 1.0 / 1.5 / 2.5, no trigger row — saved the
--                Settings page) → CHANGES at deploy, partly: trigger 100 at once,
--                leverage still 1.50 / 2.50. 068 moves the leverage pair.
--           (The mirror of C — trigger row, no leverage rows — moves leverage at
--           deploy and the trigger when 068 runs.) Rationale and a worked
--           example: docs/db-changes/068_scoring_config_v9_calibration.md.
--
--           WHAT IT TOUCHES — deliberately narrow:
--             - Only the three keys above, and only rows whose value is EXACTLY
--               the old default. A value an analyst chose on purpose (trigger 90,
--               leverage danger 3.00, …) is not a leftover default and is left
--               alone — list it with query 0 and decide by hand.
--             - Leverage moves as a PAIR, per tenant, and only when every leverage
--               row that tenant has is at the old default (ok 1.00, warn 1.50,
--               danger 2.50; a missing row counts as the default). Moving warn to
--               2.33 while a custom danger of, say, 2.00 stays would invert
--               warn < danger and break the interpolation; so a tenant with any
--               customised leverage row is skipped entirely.
--             - Only BASE keys. Sector overrides (…__sector__Comercio etc.) are
--               matched by exact key, so they are never updated: a sector row is a
--               deliberate calibration by definition. Query 0 lists them.
--             - …but sector rows ARE read, because they are merged over the base
--               row by row. A PARTIAL sector override (say only
--               threshold_leverage_danger__sector__Comercio = 2.00) is coherent over
--               a base warn of 1.50 and inverted over 2.33 — Comercio would score
--               on 1.00 / 2.33 / 2.00. So the leverage pair is moved only when
--               every merged triplet (base, and each sector over it) is still
--               strictly ordered AFTER the move; otherwise it is skipped with a
--               PRINT, and the trigger still moves.
--             - ok stays 1.00 — the v9 value is the same.
--
--           LEVERAGE ORDERING IS CHECKED THREE TIMES, on the MERGED triplets, with
--           a missing row read as the v9 default (1.00 / 2.33 / 5.67) exactly as
--           the engine reads it:
--             0b. before anything — what the v9 DEPLOY ALONE leaves out of order
--                 for this tenant. Shown, never fatal: it is information for the
--                 DBA and the analyst.
--             2.  as a simulation of the move, which decides whether it happens.
--             4.  inside the transaction, just before COMMIT. Any triplet out of
--                 order there THROWs, and XACT_ABORT rolls the whole run back.
--                 That includes an inversion the deploy alone caused (0b): the
--                 script refuses to commit on top of one, so fix those rows first.
--           Since v9 the engine scores a ratio whose merged triplet is out of
--           order on its complete default triplet (breakdown: thresholdFallbacks)
--           rather than on the inverted one, and Settings refuses to save one —
--           so an inversion costs a correct-but-uncalibrated ratio, not a floor.
--
--           ONE TRANSACTION, all or nothing, with XACT_ABORT so any error rolls
--           back. For a dry run, change the final COMMIT to ROLLBACK: the PRINTs
--           and the "after" SELECTs still show what it would have done.
--
--           RUN IT ONCE PER TENANT, WITH @TenantId SET. The business database is
--           SHARED — the API binds one static "FinanciaDefault" connection and
--           separates tenants by the TenantId column — so the tenant is chosen by
--           @TenantId, not by the database you connect to. Every statement below
--           is scoped to it: a run changes exactly one tenant, the one whose
--           analyst signed off. The placeholder N'<tenant>' makes the script THROW
--           before it reads or writes anything, so it cannot be run by accident as
--           a "pending numbered script" ahead of a deploy. (Where a tenant does
--           live in its own database, the same rule holds: set @TenantId, run it
--           there.) Same shape as the @t of 007 / 014.
--
--           IDEMPOTENT: a second run for the same tenant finds no row at the old
--           values and updates nothing.
-- ============================================================================

SET XACT_ABORT ON;

-- ⚠ Set this to the tenant slug the analyst signed off (e.g. N'SgrOneClickSandboxFB2').
DECLARE @TenantId NVARCHAR(100) = N'<tenant>';

IF @TenantId IS NULL OR LTRIM(RTRIM(@TenantId)) = N'' OR @TenantId = N'<tenant>'
    THROW 50068, N'068: set @TenantId to the tenant slug the analyst signed off before running this script. Nothing was read or changed.', 1;

-- ── 0. Before: every row of the keys v9 touches, sector overrides included ──
--       (the diagnostic SELECT from the v9 PR; run it alone, with @TenantId set,
--       to decide whether this tenant needs the script at all)

SELECT TenantId, ConfigKey, ConfigValue, UpdatedAt
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND (ConfigKey LIKE 'critical\_floor\_%'           ESCAPE '\'
   OR  ConfigKey LIKE 'threshold\_leverage\_%'       ESCAPE '\'
   OR  ConfigKey LIKE 'threshold\_endeudamiento\_%'  ESCAPE '\')
ORDER  BY ConfigKey;

-- ── 0b. Leverage ordering the v9 DEPLOY ALONE leaves this tenant with ────────
--       Every merged triplet — base, and each sector's rows over it — with a
--       missing row read as the v9 default. "OUT OF ORDER" here is a triplet the
--       engine now refuses (and scores on the default triplet instead); it exists
--       whether or not this script ever runs. Fix those rows before running it:
--       step 4 will not commit on top of one.

-- The v9 leverage defaults (ScoringDefaults.cs), read wherever a row is missing.
DECLARE @DefOk     DECIMAL(10,4) = 1.0000;
DECLARE @DefWarn   DECIMAL(10,4) = 2.3300;
DECLARE @DefDanger DECIMAL(10,4) = 5.6700;

DECLARE @BaseOk DECIMAL(10,4), @BaseWarn DECIMAL(10,4), @BaseDanger DECIMAL(10,4);

-- The tenant's leverage SECTOR rows, split into (sector, base key). The script
-- never writes a sector row, so this is read once and serves all three checks.
DECLARE @LeverageSectorRows TABLE (
    Sector      NVARCHAR(100) NOT NULL,
    BaseKey     NVARCHAR(100) NOT NULL,
    ConfigValue DECIMAL(10,4) NOT NULL);

INSERT INTO @LeverageSectorRows (Sector, BaseKey, ConfigValue)
SELECT SUBSTRING(ConfigKey, CHARINDEX(N'__sector__', ConfigKey) + LEN(N'__sector__'), 100),
       LEFT(ConfigKey, CHARINDEX(N'__sector__', ConfigKey) - 1),
       ConfigValue
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND  ConfigKey LIKE N'threshold\_leverage\_%\_\_sector\_\_%' ESCAPE N'\';

-- Merged triplets per check: Phase 'deploy' (0b), 'after-move' (2), 'commit' (4).
DECLARE @LeverageTriplets TABLE (
    Phase  NVARCHAR(20)  NOT NULL,
    Scope  NVARCHAR(120) NOT NULL,
    Ok     DECIMAL(10,4) NOT NULL,
    Warn   DECIMAL(10,4) NOT NULL,
    Danger DECIMAL(10,4) NOT NULL);

SELECT @BaseOk     = COALESCE(MAX(CASE WHEN ConfigKey = N'threshold_leverage_ok'     THEN ConfigValue END), @DefOk),
       @BaseWarn   = COALESCE(MAX(CASE WHEN ConfigKey = N'threshold_leverage_warn'   THEN ConfigValue END), @DefWarn),
       @BaseDanger = COALESCE(MAX(CASE WHEN ConfigKey = N'threshold_leverage_danger' THEN ConfigValue END), @DefDanger)
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND  ConfigKey IN (N'threshold_leverage_ok', N'threshold_leverage_warn', N'threshold_leverage_danger');

INSERT INTO @LeverageTriplets (Phase, Scope, Ok, Warn, Danger)
SELECT N'deploy', N'(base)', @BaseOk, @BaseWarn, @BaseDanger
UNION ALL
SELECT N'deploy', N'sector ' + Sector,
       COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_ok'     THEN ConfigValue END), @BaseOk),
       COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_warn'   THEN ConfigValue END), @BaseWarn),
       COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_danger' THEN ConfigValue END), @BaseDanger)
FROM   @LeverageSectorRows
GROUP  BY Sector;

SELECT Scope, Ok, Warn, Danger,
       CASE WHEN Ok < Warn AND Warn < Danger THEN N'ordered' ELSE N'OUT OF ORDER' END AS LeverageOrdering
FROM   @LeverageTriplets
WHERE  Phase = N'deploy'
ORDER  BY Scope;

-- No GO between here and COMMIT: a GO ends the batch, and @TenantId with it.

BEGIN TRANSACTION;

DECLARE @Now DATETIME2 = SYSUTCDATETIME();
DECLARE @Rows INT;

-- ── 1. Trigger: 85 → 100 (critical only at DANGER) ──────────────────────────

UPDATE [dbo].[ScoringConfig]
SET    ConfigValue = 100.0000,
       Description = N'Score parcial a partir del cual un ratio se considera crítico; 100 = el ratio alcanzó o superó su umbral de peligro (algoritmo v9)',
       UpdatedAt   = @Now
WHERE  TenantId    = @TenantId
  AND  ConfigKey   = N'critical_floor_trigger_threshold'
  AND  ConfigValue = 85.0000;

SET @Rows = @@ROWCOUNT;
PRINT 'critical_floor_trigger_threshold 85 -> 100: ' + CAST(@Rows AS NVARCHAR(20)) + ' row(s).';

-- ── 2. Leverage pair: 1.50 / 2.50 → 2.33 / 5.67, only for untouched tenants ─

DECLARE @LeverageTenants TABLE (TenantId NVARCHAR(100) PRIMARY KEY);

INSERT INTO @LeverageTenants (TenantId)
SELECT TenantId
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND  ConfigKey IN (N'threshold_leverage_ok', N'threshold_leverage_warn', N'threshold_leverage_danger')
GROUP  BY TenantId
HAVING SUM(CASE WHEN ConfigKey = N'threshold_leverage_ok'     AND ConfigValue <> 1.0000 THEN 1 ELSE 0 END) = 0
   AND SUM(CASE WHEN ConfigKey = N'threshold_leverage_warn'   AND ConfigValue <> 1.5000 THEN 1 ELSE 0 END) = 0
   AND SUM(CASE WHEN ConfigKey = N'threshold_leverage_danger' AND ConfigValue <> 2.5000 THEN 1 ELSE 0 END) = 0
   AND SUM(CASE WHEN ConfigKey IN (N'threshold_leverage_warn', N'threshold_leverage_danger') THEN 1 ELSE 0 END) > 0;

-- Simulate the move on the MERGED triplets before making it. After it the base is
-- ok (1.00 — the only value that passed the filter above) / 2.33 / 5.67, always
-- ordered; what can break is a sector whose partial override sits between the old
-- and the new values. If any merged triplet would end up out of order, the pair
-- does not move for this tenant: its leverage rows stay on the v8 values, which
-- that sector override was calibrated against, and the trigger still moves.
IF EXISTS (SELECT 1 FROM @LeverageTenants)
BEGIN
    INSERT INTO @LeverageTriplets (Phase, Scope, Ok, Warn, Danger)
    SELECT N'after-move', N'(base)', @BaseOk, 2.3300, 5.6700
    UNION ALL
    SELECT N'after-move', N'sector ' + Sector,
           COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_ok'     THEN ConfigValue END), @BaseOk),
           COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_warn'   THEN ConfigValue END), 2.3300),
           COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_danger' THEN ConfigValue END), 5.6700)
    FROM   @LeverageSectorRows
    GROUP  BY Sector;

    IF EXISTS (SELECT 1 FROM @LeverageTriplets
               WHERE  Phase = N'after-move' AND NOT (Ok < Warn AND Warn < Danger))
    BEGIN
        PRINT N'threshold_leverage_warn/danger: NOT moved - a sector override would be out of order '
            + N'over 2.33 / 5.67. See the after-move triplets below; the trigger still moves.';

        SELECT Scope, Ok, Warn, Danger, N'OUT OF ORDER after the move' AS LeverageOrdering
        FROM   @LeverageTriplets
        WHERE  Phase = N'after-move' AND NOT (Ok < Warn AND Warn < Danger)
        ORDER  BY Scope;

        DELETE FROM @LeverageTenants;
    END
END

UPDATE c
SET    ConfigValue = 2.3300,
       Description = N'Leverage Warning (score 50) — equivalente a endeudamiento 0,70 (algoritmo v9)',
       UpdatedAt   = @Now
FROM   [dbo].[ScoringConfig] c
JOIN   @LeverageTenants t ON t.TenantId = c.TenantId
WHERE  c.TenantId  = @TenantId
  AND  c.ConfigKey = N'threshold_leverage_warn';

SET @Rows = @@ROWCOUNT;
PRINT 'threshold_leverage_warn 1.50 -> 2.33: ' + CAST(@Rows AS NVARCHAR(20)) + ' row(s).';

UPDATE c
SET    ConfigValue = 5.6700,
       Description = N'Leverage Danger (score 100) — equivalente a endeudamiento 0,85 (algoritmo v9)',
       UpdatedAt   = @Now
FROM   [dbo].[ScoringConfig] c
JOIN   @LeverageTenants t ON t.TenantId = c.TenantId
WHERE  c.TenantId  = @TenantId
  AND  c.ConfigKey = N'threshold_leverage_danger';

SET @Rows = @@ROWCOUNT;
PRINT 'threshold_leverage_danger 2.50 -> 5.67: ' + CAST(@Rows AS NVARCHAR(20)) + ' row(s).';

-- ── 3. After: the same keys, to read back before committing ────────────────

SELECT TenantId, ConfigKey, ConfigValue, UpdatedAt
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND  ConfigKey IN (N'critical_floor_trigger_threshold',
                     N'threshold_leverage_ok', N'threshold_leverage_warn', N'threshold_leverage_danger')
ORDER  BY ConfigKey;

-- ── 4. Leverage ordering, inside the transaction, before COMMIT ─────────────
--       The state this run is about to commit, merged exactly as the engine will
--       read it. Any triplet out of order THROWs; XACT_ABORT rolls everything back.

SELECT @BaseOk     = COALESCE(MAX(CASE WHEN ConfigKey = N'threshold_leverage_ok'     THEN ConfigValue END), @DefOk),
       @BaseWarn   = COALESCE(MAX(CASE WHEN ConfigKey = N'threshold_leverage_warn'   THEN ConfigValue END), @DefWarn),
       @BaseDanger = COALESCE(MAX(CASE WHEN ConfigKey = N'threshold_leverage_danger' THEN ConfigValue END), @DefDanger)
FROM   [dbo].[ScoringConfig]
WHERE  TenantId = @TenantId
  AND  ConfigKey IN (N'threshold_leverage_ok', N'threshold_leverage_warn', N'threshold_leverage_danger');

INSERT INTO @LeverageTriplets (Phase, Scope, Ok, Warn, Danger)
SELECT N'commit', N'(base)', @BaseOk, @BaseWarn, @BaseDanger
UNION ALL
SELECT N'commit', N'sector ' + Sector,
       COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_ok'     THEN ConfigValue END), @BaseOk),
       COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_warn'   THEN ConfigValue END), @BaseWarn),
       COALESCE(MAX(CASE WHEN BaseKey = N'threshold_leverage_danger' THEN ConfigValue END), @BaseDanger)
FROM   @LeverageSectorRows
GROUP  BY Sector;

SELECT Scope, Ok, Warn, Danger,
       CASE WHEN Ok < Warn AND Warn < Danger THEN N'ordered' ELSE N'OUT OF ORDER' END AS LeverageOrdering
FROM   @LeverageTriplets
WHERE  Phase = N'commit'
ORDER  BY Scope;

IF EXISTS (SELECT 1 FROM @LeverageTriplets
           WHERE  Phase = N'commit' AND NOT (Ok < Warn AND Warn < Danger))
    THROW 50068, N'068: a merged leverage triplet (base or sector) would be committed out of order (ok < warn < danger fails) — see the "commit" triplets above. Rolled back; nothing changed. Fix those rows, then run again.', 2;

COMMIT TRANSACTION;   -- ROLLBACK TRANSACTION for a dry run
GO

-- ── Verification (DBA reference only — NOT executed) ────────────────────────
--
-- 1. No base row is left at an old default. Expected: 0 rows. A row here is a
--    leverage pair skipped on purpose (a customised leverage row, or a sector
--    override the move would invert) — check query 0 / 0b and the run's PRINTs.
--
--    SELECT TenantId, ConfigKey, ConfigValue
--    FROM   [dbo].[ScoringConfig]
--    WHERE  TenantId = N'<tenant>'
--      AND ((ConfigKey = N'critical_floor_trigger_threshold' AND ConfigValue = 85.0000)
--       OR  (ConfigKey = N'threshold_leverage_warn'          AND ConfigValue = 1.5000)
--       OR  (ConfigKey = N'threshold_leverage_danger'        AND ConfigValue = 2.5000));
--
-- 2. Leverage ordering is no longer a reference query: steps 0b and 4 above
--    RUN it, on the merged triplets (base and every sector over it), and step 4
--    THROWs rather than commit one out of order.
--
--    A rollback (below) moves the base back to 1.50 / 2.50; re-run step 0b on its
--    own afterwards if a sector override was added in between.
--
-- ROLLBACK (by hand, per tenant, only if the analyst reverses the decision):
--    UPDATE [dbo].[ScoringConfig] SET ConfigValue = 85.0000, UpdatedAt = SYSUTCDATETIME()
--    WHERE  TenantId = N'<tenant>' AND ConfigKey = N'critical_floor_trigger_threshold';
--    UPDATE [dbo].[ScoringConfig] SET ConfigValue = 1.5000, UpdatedAt = SYSUTCDATETIME()
--    WHERE  TenantId = N'<tenant>' AND ConfigKey = N'threshold_leverage_warn';
--    UPDATE [dbo].[ScoringConfig] SET ConfigValue = 2.5000, UpdatedAt = SYSUTCDATETIME()
--    WHERE  TenantId = N'<tenant>' AND ConfigKey = N'threshold_leverage_danger';
--    Or DELETE the three rows instead, which makes the tenant follow the code defaults from then on.
-- ============================================================================
