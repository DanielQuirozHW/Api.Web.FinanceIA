-- ============================================================================
-- Script  : 051_tenantbillingconfig_add_chatcontextbudget.sql
-- Purpose : Add [ChatContextBudgetTokens] to [dbo].[TenantBillingConfig] — the
--           token ceiling for a SINGLE chat conversation's context, a sibling of
--           the two monthly limits script 047 created.
-- Date    : 2026-09-01
-- Author  : Claude (assisted)
-- Notes   : WHY THIS COLUMN EXISTS AT ALL.
--           The analyst chat rebuilds a conversation's history from EVERY
--           [dbo].[ChatMessages] row of that conversation — AnalysisController
--           loads them all and hands the lot to ClaudeService.ChatAsync — and
--           nothing caps it. A long conversation therefore grows its prompt
--           without bound until the model's window refuses it, and every turn
--           re-pays for the whole transcript.
--
--           Neither existing limit addresses that. [AnalysisMonthlyLimit] and
--           [ChatTokensMonthlyLimit] are MONTHLY POOLS, metered across the whole
--           tenant for a calendar month; this is a ceiling on ONE conversation's
--           context at ONE moment. The two answer different questions ("how much
--           may this tenant spend this month?" versus "how big may a single
--           prompt get?"), so this column sits BESIDE them rather than replacing
--           either.
--
--           It is per-tenant, and lives on this table rather than in appsettings,
--           because it is commercial state sold alongside the plan: a larger plan
--           buys a longer conversation. The preset figures in
--           Models/Financia/BillingPlans.cs scale with the tier for that reason.
--
--           INT, NOT BIGINT — the opposite of the choice 047 made for
--           [ChatTokensMonthlyLimit], and deliberately. That column is BIGINT
--           because a MONTHLY pool passes 2.1 billion at plausible volumes. A
--           single conversation's context cannot: the chat model is
--           claude-sonnet-4-6, whose context window is 200.000 tokens, so any
--           sane value here is five orders of magnitude below int.MaxValue.
--
--           NOT NULL with DEFAULT (40000) so every existing row backfills in the
--           ALTER itself, with no data migration and no separate UPDATE. 40000 is
--           BillingPlans.Minimum.ChatContextBudgetTokens — the smallest preset. A
--           tenant already configured under 047 never chose a budget, so it gets
--           the least, which is the same posture 047 documents for a missing or
--           inactive row: an unconfigured tenant should get the least, never the
--           most.
--
--           THE FIGURE IS A PLACEHOLDER. 40000 / 60000 / 80000 mirror the
--           TODO(billing) marker in BillingPlans.cs: like the monthly token
--           figures beside them, none of these has been agreed with product.
--           Settle them before anything is seeded into production.
--
--           NOTHING ENFORCES THIS YET. This delivery adds the column, its EF
--           mapping, its in-code default and the SuperAdmin read/write path, and
--           stops there. Trimming a conversation's context against this budget is
--           a later PR, and the Portal's usage chart is a later one still. Until
--           the first of those lands the column is stored and reported, and no
--           request behaves differently because of it.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. The column is mapped in TenantBillingConfigConfiguration, so
--           EF Core emits it in every SELECT and INSERT against
--           [dbo].[TenantBillingConfig] — including the effective-config read on
--           the billing path. Publishing the code first fails that read with
--           'Invalid column name', and no application-level try/catch protects
--           against it: TenantBillingConfigService catches the failure and
--           degrades every tenant to the minimum plan. Order: DDL first, then
--           publish, then restart the App Service so EF's model cache is rebuilt.
--
--           Idempotent: the column is guarded on sys.columns, so the script can be
--           re-run safely. Re-running never overwrites a budget already set.
-- ============================================================================

-- ── TenantBillingConfig.ChatContextBudgetTokens ─────────────────────────────
-- NOT NULL with a named default constraint so existing rows backfill to the
-- minimum preset in the ALTER itself. The name is explicit
-- (DF_TenantBillingConfig_ChatContextBudgetTokens) rather than system-generated so
-- a future script can drop it by name, matching every other DF_ on this table.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[TenantBillingConfig]')
      AND  name      = N'ChatContextBudgetTokens'
)
BEGIN
    ALTER TABLE [dbo].[TenantBillingConfig]
        ADD [ChatContextBudgetTokens] INT NOT NULL
            CONSTRAINT [DF_TenantBillingConfig_ChatContextBudgetTokens] DEFAULT (40000);
    PRINT 'Column ChatContextBudgetTokens added to [dbo].[TenantBillingConfig].';
END
ELSE
BEGIN
    PRINT 'Column ChatContextBudgetTokens already exists on [dbo].[TenantBillingConfig] — skipped.';
END
GO

-- ── Seeding (DBA reference only — NOT executed) ─────────────────────────────
-- No row is required: a tenant with no row — or with IsActive = 0 — resolves to
-- BillingPlans.Minimum, which now carries a budget of its own. The ALTER above
-- already backfilled every EXISTING row to that same 40000, so nothing below has
-- to be run for the feature to work.
--
-- To raise an existing tenant's budget, UPDATE its one row (replacing the slug):
--
--   UPDATE [dbo].[TenantBillingConfig]
--   SET    [ChatContextBudgetTokens] = 80000,
--          [UpdatedAt]               = SYSUTCDATETIME(),
--          [UpdatedBy]               = N'dba'
--   WHERE  [TenantId] = N'SgrOneClickSandboxFB2';
--
-- To create a row that does not exist yet, all four limit columns are set at once
-- (UQ_TenantBillingConfig_Tenant means a second INSERT for the same tenant fails):
--
--   INSERT INTO [dbo].[TenantBillingConfig]
--       ([TenantId], [PlanName], [AnalysisMonthlyLimit], [ChatTokensMonthlyLimit],
--        [ChatContextBudgetTokens], [UpdatedBy])
--   VALUES (N'SgrOneClickSandboxFB2', N'100 análisis', 100, 4000000, 60000, N'dba');
--
-- The three preset budgets are 40000 / 60000 / 80000 for the 50 / 100 / 200
-- análisis tiers. Like the token figures beside them they are PLACEHOLDERS and are
-- NOT final — see the TODO(billing) marker in BillingPlans.cs. Confirm with product
-- before seeding production.
