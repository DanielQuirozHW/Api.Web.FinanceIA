-- ============================================================================
-- Script  : 052_tenantbillingconfig_add_chatenabledmodels.sql
-- Purpose : Add [ChatEnabledModels] to [dbo].[TenantBillingConfig] — the set of
--           Claude chat models a tenant is allowed to use, as a comma-separated
--           list of model ids. A SuperAdmin edits it; the analyst picks one of
--           them per conversation.
-- Date    : 2026-09-02
-- Author  : Claude (assisted)
-- Notes   : WHY THIS COLUMN EXISTS AT ALL.
--           The analyst chat model is HARDCODED. ClaudeService held
--           `private const string ChatModel = "claude-sonnet-4-6"`, and
--           AnalysisController repeated that literal four more times when
--           recording what had supposedly been called. Every tenant got the same
--           model, and an analyst could not trade cost against depth for a given
--           conversation — a cheap model for a quick lookup, a capable one for a
--           hard question. This column is what makes that a per-tenant
--           entitlement instead of a deployment-wide constant.
--
--           WHAT IT IS NOT. It is not a quota and nothing is spent against it.
--           The three columns beside it are ALLOWANCES — how many analyses, how
--           many tokens per month, how large one conversation's context may get —
--           and every one of them is a number that gets consumed or exceeded.
--           This is a CAPABILITY SET: a list of what the tenant may reach for,
--           unchanged by using it. It shares the table because it is commercial
--           state sold with the plan and settable by the same SuperAdmin through
--           the same endpoint, not because it is the same kind of limit.
--
--           WHY A CSV COLUMN AND NOT A CHILD TABLE. The same argument script 047
--           makes for having no [BillingPlan] catalogue table. The catalogue of
--           supported models lives in application code — Models/Financia/
--           ChatModelCatalog.cs — because a tenant with no row must resolve to
--           something sensible and must never fail, and adding a model must not
--           require inserting rows for every existing tenant. This column stores
--           only WHICH of that catalogue a tenant bought, and the code discards
--           any id that is not in it. A child table would add a join, a foreign
--           key to a catalogue that does not exist in SQL, and a second write
--           path, to hold at most three short strings that are always read and
--           always written together.
--
--           NVARCHAR(400) against a payload of 48 characters today. The width is
--           for the catalogue to grow into, not for this list: it holds roughly a
--           dozen ids of the length Anthropic currently uses. It is deliberately
--           NOT NVARCHAR(MAX) — this is a short scalar read on the billing path,
--           and MAX would push it off-row for no reason.
--
--           WHY THE DEFAULT IS *EVERY* MODEL, unlike script 051. That script
--           backfilled to the SMALLEST preset, on the principle 047 documents:
--           an unconfigured tenant should get the least, never the most. That
--           principle governs ALLOWANCES, and it does not apply here. The chat
--           model was hardcoded ON for every tenant, so a row that predates this
--           script belongs to a tenant that already had a working chat; defaulting
--           it to a subset would REMOVE a capability as a side effect of a schema
--           change. The safe default for an allowance is the least; the safe
--           default for a capability the tenant already had is what they already
--           had. The in-code fallback (ChatModelCatalog.AllIds, reached through
--           BillingPlans and through SanitizeOrDefault) states the same list, so a
--           tenant with no row, an inactive row, or a blanked column all resolve
--           to these three.
--
--           NOT NULL with the named DEFAULT so every existing row backfills in the
--           ALTER itself — no data migration, no separate UPDATE. The name is
--           explicit (DF_TenantBillingConfig_ChatEnabledModels) rather than
--           system-generated so a future script can drop it by name, matching
--           every other DF_ on this table.
--
--           THE THREE IDS ARE NOT PLACEHOLDERS, unlike the figures in the columns
--           beside this one. They are exact Anthropic model ids, verified against
--           the Messages API, and they carry NO date suffix — appending one names
--           a model that does not exist. If a future script changes this list, the
--           values here and ChatModelCatalog.All must move together; the code
--           silently drops any id it does not recognise, so a typo here degrades
--           to "fewer models than intended" rather than to an error.
--
--           ⚠ RUN THIS SCRIPT BEFORE DEPLOYING THE MATCHING CODE, on every tenant
--           database. The column is mapped in TenantBillingConfigConfiguration, so
--           EF Core emits it in every SELECT and INSERT against
--           [dbo].[TenantBillingConfig] — including the effective-config read on
--           the billing path and on every chat turn. Publishing the code first
--           fails that read with 'Invalid column name', and no application-level
--           try/catch protects against it: TenantBillingConfigService CATCHES the
--           failure and degrades every tenant to the minimum plan. The chat would
--           keep working — the degraded plan still carries all three models — but
--           every analysis quota and every configured plan would silently vanish
--           until the column existed. Order: DDL first, then publish, then restart
--           the App Service so EF's model cache is rebuilt.
--
--           Idempotent: the column is guarded on sys.columns, so the script can be
--           re-run safely. Re-running never overwrites a list already set.
-- ============================================================================

-- ── TenantBillingConfig.ChatEnabledModels ───────────────────────────────────
-- NOT NULL with a named default constraint so existing rows backfill to the full
-- catalogue in the ALTER itself — the capability they already had.

IF NOT EXISTS (
    SELECT 1
    FROM   sys.columns
    WHERE  object_id = OBJECT_ID(N'[dbo].[TenantBillingConfig]')
      AND  name      = N'ChatEnabledModels'
)
BEGIN
    ALTER TABLE [dbo].[TenantBillingConfig]
        ADD [ChatEnabledModels] NVARCHAR(400) NOT NULL
            CONSTRAINT [DF_TenantBillingConfig_ChatEnabledModels]
            DEFAULT (N'claude-haiku-4-5,claude-sonnet-4-6,claude-sonnet-5');
    PRINT 'Column ChatEnabledModels added to [dbo].[TenantBillingConfig].';
END
ELSE
BEGIN
    PRINT 'Column ChatEnabledModels already exists on [dbo].[TenantBillingConfig] — skipped.';
END
GO

-- ── Seeding (DBA reference only — NOT executed) ─────────────────────────────
-- No row is required: a tenant with no row — or with IsActive = 0 — resolves to
-- BillingPlans.Minimum, whose model list is the full catalogue. The ALTER above
-- already backfilled every EXISTING row to that same list, so nothing below has
-- to be run for the feature to work.
--
-- The three supported ids, exactly as ChatModelCatalog declares them:
--
--   claude-haiku-4-5     — fastest and cheapest
--   claude-sonnet-4-6    — the chat's default, and what it used before this change
--   claude-sonnet-5      — most capable; thinks before answering
--
-- To RESTRICT a tenant to a subset, UPDATE its one row (replacing the slug). The
-- order written here does not matter — the code re-orders to the catalogue's own
-- order — but the list must name at least one supported id, or the code falls back
-- to all three rather than leaving the tenant with no model at all:
--
--   UPDATE [dbo].[TenantBillingConfig]
--   SET    [ChatEnabledModels] = N'claude-haiku-4-5,claude-sonnet-4-6',
--          [UpdatedAt]         = SYSUTCDATETIME(),
--          [UpdatedBy]         = N'dba'
--   WHERE  [TenantId] = N'SgrOneClickSandboxFB2';
--
-- To create a row that does not exist yet, all five configuration columns are set
-- at once (UQ_TenantBillingConfig_Tenant means a second INSERT for the same tenant
-- fails):
--
--   INSERT INTO [dbo].[TenantBillingConfig]
--       ([TenantId], [PlanName], [AnalysisMonthlyLimit], [ChatTokensMonthlyLimit],
--        [ChatContextBudgetTokens], [ChatEnabledModels], [UpdatedBy])
--   VALUES (N'SgrOneClickSandboxFB2', N'100 análisis', 100, 4000000, 60000,
--           N'claude-haiku-4-5,claude-sonnet-4-6,claude-sonnet-5', N'dba');
--
-- Prefer the SuperAdmin endpoint (PUT /api/billing/config) over hand-written SQL
-- where it is available: it validates every id against the catalogue, refuses an
-- empty list with a 400, and writes an audit row. This column has no CHECK
-- constraint — SQL has no way to validate against a catalogue that lives in code —
-- so an id misspelled here is accepted by the database and silently ignored by the
-- application, which is exactly the failure the endpoint prevents.
