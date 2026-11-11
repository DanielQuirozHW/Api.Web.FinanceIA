-- DATA FIX, no schema change
-- ============================================================================
-- Script  : 086_alert_rule_catalogue_seed_endeudamiento_elevado.sql
-- Purpose : Seed the ENDEUDAMIENTO_ELEVADO rule in [dbo].[AlertRuleCatalogue]
--           for every tenant that already has catalogue rows, DISABLED, so the
--           analyst can see it in Configuración, read its es-AR name and turn it
--           on with a threshold of their own.
-- Date    : 2026-11-09
-- Author  : Claude (assisted)
-- Notes   : ⚠ THIS IS A DATA SCRIPT (DML), NOT A SCHEMA SCRIPT — like 066, 068,
--           082, 084 and 085. It changes no object, so the API does NOT depend on
--           it and the --verify-schema gate neither needs nor sees it. It is
--           OPTIONAL PER TENANT, NOT A DEPLOY BLOCKER, and SAFE TO RUN BEFORE OR
--           AFTER THE DEPLOY. NO App Service restart.
--
--           WHY. AlertRuleEngine implements six rules (AlertRuleEngine.
--           ImplementedCodes) but DDL 038 seeded five: ENDEUDAMIENTO_ELEVADO
--           (total liabilities / total assets above ThresholdValue) has no row,
--           so no tenant can see or enable it. The engine evaluates ONLY the
--           tenant's ENABLED catalogue rows and has no code default for any rule,
--           so without a row the rule never fires.
--
--           BEHAVIOUR. Every row this script writes is DISABLED (Enabled = 0), as
--           038 seeded every rule. The engine skips a disabled row, so analyses
--           behave EXACTLY as before until an analyst enables the rule in
--           Configuración (PATCH /api/alert-rules/{id}).
--
--           VALUES (038's conventions — lowercase severity, es-AR text, a
--           threshold that is a PROVISIONAL placeholder, not a recommendation):
--             Severity       = high
--             ThresholdValue = 0.8500  (85 % of total assets) — the same
--                              quantity and figure as the scoring default
--                              threshold_endeudamiento_danger (ScoringDefaults).
--                              The analyst sets the real value; the screen
--                              accepts a proportion in (0, 1].
--             DisplayNameEs  = Endeudamiento elevado  (as Servicios/Pdf/AlertTitles.cs)
--             DescriptionTemplateEs uses {valor} and {umbral}, which the engine
--             replaces with es-AR percentages (one decimal, no % sign — the
--             template carries " %", like INVENTARIO_ELEVADO in 038).
--
--           WHO. Every TenantId that already has at least one row in the
--           catalogue of the database it runs in (the tenants 038 or a DBA set
--           up) and no ENDEUDAMIENTO_ELEVADO row. A tenant with no catalogue at
--           all is left alone: its catalogue is set up as a whole, not one rule.
--           RUN IT ONCE IN EACH Financia (tenant business) DATABASE whose tenants
--           should have the rule — optional per tenant: remove a TenantId from
--           the preview's list by adding it to @Excluded below.
--
--           TWO STEPS, ONE FILE. With @Apply = 0 (the default) it only READS:
--             Result set 1 — the TenantIds it WOULD seed, with how many catalogue
--             rows each already has.
--           Review it, then set @Apply = 1 and run again: the same tenants are
--           recomputed and seeded in ONE transaction (XACT_ABORT ON), and the
--           number of rows written is printed.
--
--           ⚠ It BYPASSES the application audit trail (no AuditLogs row).
--           Record the run (database, date, who, rows) in README.md's table.
--
--           IDEMPOTENT: the INSERT re-checks NOT EXISTS per tenant, and
--           UQ_AlertRuleCatalogue_Tenant_Code (TenantId, Code) is the backstop, so a
--           second run writes nothing and an analyst's edits are never reset.
--
--           Rationale: docs/db-changes/086_alert_rule_catalogue_seed_endeudamiento_elevado.md
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 0 = preview only (default). 1 = seed the tenants listed in result set 1.
DECLARE @Apply BIT = 0;

-- Tenants to leave out (optional per tenant). Example:
--   INSERT INTO @Excluded (TenantId) VALUES (N'SomeTenantSlug');
DECLARE @Excluded TABLE (TenantId NVARCHAR(100) NOT NULL PRIMARY KEY);

DECLARE @Code      NVARCHAR(50)   = N'ENDEUDAMIENTO_ELEVADO';
DECLARE @Severity  NVARCHAR(20)   = N'high';
DECLARE @Threshold DECIMAL(10,4)  = 0.8500;
DECLARE @Name      NVARCHAR(300)  = N'Endeudamiento elevado';
DECLARE @Template  NVARCHAR(1000) =
    N'El pasivo representa el {valor} % del activo total, por encima del umbral configurado de {umbral} %.';

IF OBJECT_ID(N'tempdb..#Targets') IS NOT NULL DROP TABLE #Targets;

CREATE TABLE #Targets
(
    TenantId          NVARCHAR(100) NOT NULL PRIMARY KEY,
    ExistingRuleCount INT           NOT NULL
);

INSERT INTO #Targets (TenantId, ExistingRuleCount)
SELECT c.TenantId, COUNT(*)
FROM   dbo.AlertRuleCatalogue c
WHERE  NOT EXISTS (SELECT 1 FROM dbo.AlertRuleCatalogue e
                   WHERE e.TenantId = c.TenantId AND e.Code = @Code)
  AND  NOT EXISTS (SELECT 1 FROM @Excluded x WHERE x.TenantId = c.TenantId)
GROUP  BY c.TenantId;

-- ── Result set 1: tenants this script seeds ────────────────────────────────
SELECT t.TenantId, t.ExistingRuleCount
FROM   #Targets t
ORDER  BY t.TenantId;

IF @Apply = 1
BEGIN
    BEGIN TRANSACTION;

    INSERT INTO dbo.AlertRuleCatalogue
        (TenantId, Code, Enabled, Severity, ThresholdValue, DisplayNameEs, DescriptionTemplateEs)
    SELECT t.TenantId, @Code, 0, @Severity, @Threshold, @Name, @Template
    FROM   #Targets t
    WHERE  NOT EXISTS (SELECT 1 FROM dbo.AlertRuleCatalogue e      -- unchanged since previewed
                       WHERE e.TenantId = t.TenantId AND e.Code = @Code);

    DECLARE @Inserted INT = @@ROWCOUNT;

    COMMIT TRANSACTION;

    PRINT CONCAT(N'086: ', @Inserted, N' tenant(s) seeded with ', @Code, N' (DISABLED).');
END
ELSE
BEGIN
    PRINT N'086: preview only (@Apply = 0) — nothing was written. Review result set 1, then set @Apply = 1 and run again.';
END;

DROP TABLE #Targets;
