-- ============================================================================
-- Script  : 047_tenant_billing_config.sql
-- Purpose : Create [dbo].[TenantBillingConfig] — per-tenant billing/quota limits
--           (billing v1, phase 1: config foundation only).
--           Holds the monthly caps a tenant was sold: how many analyses it may
--           run and how many Claude chat tokens it may spend. NOTHING reads
--           these to block anything yet — metering and enforcement are later PRs.
-- Date    : 2026-08-31
-- Author  : Claude (assisted)
-- Notes   : Tenant-scoped like every other table (TenantId NVARCHAR(100) NOT NULL,
--           filtered in the repository — there is no EF global query filter).
--
--           EXACTLY ONE row per tenant, enforced by UQ_TenantBillingConfig_Tenant
--           on ([TenantId]). The deployment model is one database per client, so
--           in practice this table holds a single row; it is tenant-scoped anyway
--           because every table here is, and because that is what makes the
--           repository's WHERE clause the same shape as everywhere else.
--
--           There is deliberately NO BillingPlan catalogue table. The commercial
--           presets (50 / 100 / 200 análisis) live in application code, in
--           Models/Financia/BillingPlans.cs, for the same reason TenantSettings
--           keeps its defaults in TenantSettingKeys: a tenant with no row must
--           resolve to a sensible minimum and must never fail, and a new preset
--           must not require inserting a row for every existing tenant.
--
--           PlanName is an INFORMATIONAL LABEL ONLY — the human-readable name of
--           what was sold (e.g. '100 análisis'). It is NOT a foreign key and it is
--           NOT looked up: the effective limits are read from this row's own
--           AnalysisMonthlyLimit / ChatTokensMonthlyLimit columns, which is why
--           both are NOT NULL. A tenant can therefore be priced off-preset without
--           a code change; the presets only supply the no-row default and a
--           convenient set of numbers when seeding.
--
--           ChatTokensMonthlyLimit is BIGINT, not INT, on purpose: a monthly chat
--           token pool passes 2.1 billion at plausible volumes, and widening an
--           INT column later is a migration nobody wants. AnalysisMonthlyLimit
--           stays INT — an analysis count has no such headroom problem.
--
--           IsActive lets a config be switched off without deleting the row, so
--           the commercial history survives. An inactive row resolves to the
--           in-code minimum plan, exactly as a missing row does.
--
--           UpdatedBy records who last changed the row, for audit. It is NVARCHAR
--           rather than a FK to [dbo].[Users] because the intended writer is a
--           SuperAdmin operating across tenants, and that role DOES NOT EXIST YET
--           — the roles today are Analyst | RiskManager | Admin | Auditor
--           (Clases/Roles.cs). Until the write path lands in a later PR, this
--           column is populated by the DBA, or left NULL.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. Every column below is mapped in TenantBillingConfigConfiguration,
--           so EF Core emits them in any SELECT against TenantBillingConfig. This
--           delivery DOES add a query that touches the table (the effective-config
--           read), so publishing the code first fails that read with
--           'Invalid object name'. Order: DDL first, then publish, then restart the
--           App Service so EF's model cache is rebuilt.
--
--           Idempotent: the table is guarded so the script can be re-run safely.
-- ============================================================================

-- ── TenantBillingConfig table ───────────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1
    FROM   sys.objects
    WHERE  object_id = OBJECT_ID(N'[dbo].[TenantBillingConfig]')
      AND  type      = N'U'
)
BEGIN
    CREATE TABLE [dbo].[TenantBillingConfig]
    (
        [BillingConfigId]        UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_TenantBillingConfig_BillingConfigId] DEFAULT (NEWSEQUENTIALID()),
        [TenantId]               NVARCHAR(100)    NOT NULL,
        [PlanName]               NVARCHAR(100)    NOT NULL,   -- label only ('100 análisis'); NOT a FK, never looked up
        [AnalysisMonthlyLimit]   INT              NOT NULL,   -- analyses per calendar month
        [ChatTokensMonthlyLimit] BIGINT           NOT NULL,   -- BIGINT: chat token pools scale past INT
        [IsActive]               BIT              NOT NULL
            CONSTRAINT [DF_TenantBillingConfig_IsActive] DEFAULT (1),  -- inactive resolves to the in-code minimum
        [UpdatedAt]              DATETIME2        NOT NULL
            CONSTRAINT [DF_TenantBillingConfig_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
        [UpdatedBy]              NVARCHAR(100)    NULL,       -- who last changed it; SuperAdmin role is a later PR

        CONSTRAINT [PK_TenantBillingConfig]
            PRIMARY KEY CLUSTERED ([BillingConfigId] ASC),

        CONSTRAINT [UQ_TenantBillingConfig_Tenant]
            UNIQUE ([TenantId] ASC)
    );
    PRINT 'Table [dbo].[TenantBillingConfig] created.';
END
ELSE
BEGIN
    PRINT 'Table [dbo].[TenantBillingConfig] already exists — skipped.';
END
GO

-- ── Seeding (DBA reference only — NOT executed) ─────────────────────────────
-- Defaults live in code (BillingPlans.Minimum, the 50-análisis preset), so no row
-- is required: a tenant with no row — or with IsActive = 0 — resolves to that
-- minimum. To put a tenant ON A LARGER PLAN, the DBA runs (replacing the slug):
--
--   INSERT INTO [dbo].[TenantBillingConfig]
--       ([TenantId], [PlanName], [AnalysisMonthlyLimit], [ChatTokensMonthlyLimit], [UpdatedBy])
--   VALUES (N'SgrOneClickSandboxFB2', N'100 análisis', 100, 4000000, N'dba');
--
-- UQ_TenantBillingConfig_Tenant means a second INSERT for the same tenant fails;
-- to change a plan, UPDATE the existing row instead:
--
--   UPDATE [dbo].[TenantBillingConfig]
--   SET    [PlanName]               = N'200 análisis',
--          [AnalysisMonthlyLimit]   = 200,
--          [ChatTokensMonthlyLimit] = 8000000,
--          [UpdatedAt]              = SYSUTCDATETIME(),
--          [UpdatedBy]              = N'dba'
--   WHERE  [TenantId] = N'SgrOneClickSandboxFB2';
--
-- The token figures above mirror the placeholders in BillingPlans.cs and are NOT
-- final — see the TODO(billing) marker there. Confirm with product before seeding
-- production.
