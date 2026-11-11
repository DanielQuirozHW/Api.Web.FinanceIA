# 051 — A single conversation gets a context ceiling of its own

## What changed and why

The analyst chat has no cap on how large one conversation's prompt may grow.
`AnalysisController.ChatAsync` loads **every** `[dbo].[ChatMessages]` row for the conversation,
rebuilds the full history from them, and hands the lot to `ClaudeService.ChatAsync`. Turn 30
therefore re-sends turns 1 through 29 — every one of them billed again — and the conversation keeps
growing until `claude-sonnet-4-6`'s 200.000-token window refuses it outright, which surfaces to the
analyst as a failed question rather than as a limit.

Neither limit script 047 created addresses this. `[AnalysisMonthlyLimit]` and
`[ChatTokensMonthlyLimit]` are **monthly pools**: a tenant-wide budget for a calendar month,
measured by `BillingUsageService` over `[dbo].[ApiCallLogs]` / `[dbo].[Analyses]`. What is missing is
orthogonal to both — a ceiling on how big a **single** prompt may get at a **single** moment. A
tenant can sit comfortably inside its monthly pool and still walk one conversation into the model's
window.

**051 adds the number.** It adds nothing that reads it.

### Why a column, rather than a setting in appsettings

Because it is commercial state, not an operational switch. It is sold with the plan — a bigger tier
buys a longer conversation, which is why the presets in `Models/Financia/BillingPlans.cs` scale
40.000 / 60.000 / 80.000 across the 50 / 100 / 200 análisis tiers — and it must be settable per
tenant by the SuperAdmin who sets the rest of the plan, through the endpoint that already exists.
`BillingOptions` (`Billing:EnforceChatTokens`, `Billing:MaxExtractionAttemptsPerDocument`) holds the
things that are the same for every tenant on the stack. This is not one of those.

### Why `INT` here when 047 chose `BIGINT` next door

`[ChatTokensMonthlyLimit]` is `BIGINT` because a monthly token pool passes `int.MaxValue` at
plausible volumes, and widening an `INT` later is a migration nobody wants. A single conversation's
context cannot get there: the chat model's window is 200.000 tokens, five orders of magnitude below
`int.MaxValue`. `INT` is the honest type, and it matches `[AnalysisMonthlyLimit]` beside it.

### Why `NOT NULL DEFAULT (40000)` rather than nullable

A nullable column would make "no budget chosen" a third state the resolution path has to model,
alongside "no row" and "inactive row" — both of which already resolve to `BillingPlans.Minimum`.
`NOT NULL` with the minimum preset as the default collapses it: a row that predates this script gets
exactly what a tenant with no row gets. The `ALTER` backfills every existing row in place, so there
is no data migration and no separate `UPDATE`.

40.000 is `BillingPlans.Minimum.ChatContextBudgetTokens` — `Minimum` is `Plan50`, the smallest
preset, on the same principle 047 documents: an unconfigured tenant should get the least, never the
most.

## DDL

`docs/database/051_tenantbillingconfig_add_chatcontextbudget.sql`, one guarded block:

```sql
ALTER TABLE [dbo].[TenantBillingConfig]
    ADD [ChatContextBudgetTokens] INT NOT NULL
        CONSTRAINT [DF_TenantBillingConfig_ChatContextBudgetTokens] DEFAULT (40000);
```

Guarded on `sys.columns`, `PRINT` in both branches, `GO`, no transaction — the conventions in
`docs/database/README.md`. Re-running never overwrites a budget already set. The script also carries
the usual non-executed DBA seeding reference (the `UPDATE` to raise one tenant, and the four-column
`INSERT` for a tenant with no row yet).

## How the application uses it

Only to store and report it. In this delivery:

- `TenantBillingConfig.ChatContextBudgetTokens` is mapped in `TenantBillingConfigConfiguration`
  exactly like `AnalysisMonthlyLimit`. The column's `DEFAULT` is deliberately **not** mapped,
  matching the existing `IsActive` note — `TenantBillingConfigRepository.UpsertAsync` always sets it
  explicitly, so the store default only ever applies to the backfill.
- `BillingPlan` carries it, so `BillingPlans.Minimum` supplies it for a tenant with no row, an
  inactive row, or a failed read — the same three paths, unchanged.
- `PUT /api/billing/config` (SuperAdmin) resolves it preset-or-explicit and validates it is greater
  than zero. Unlike the two monthly limits it can always be resolved: an off-preset plan that omits
  it falls back to the minimum's figure rather than 400-ing, so existing callers keep working.
- `GET /api/billing/usage` returns it, so the Portal has the denominator before anything meters
  against it. Reading is open to any authenticated tenant user; only writing is SuperAdmin.

Nothing trims a prompt, nothing refuses a turn, and no request behaves differently because of this
column. That is the next PR; the Portal's chart is the one after.

## Pre-conditions

**Run this script before publishing the matching code, on every tenant database.** The column is
mapped and `NOT NULL`, so EF Core emits it in every `SELECT` and `INSERT` against
`[dbo].[TenantBillingConfig]`. Against a database without it, the effective-config read fails with
`Invalid column name 'ChatContextBudgetTokens'` — and that failure is *swallowed*:
`TenantBillingConfigService.GetEffectiveAsync` catches it, logs a Warning and returns
`BillingPlans.Minimum` marked `ReadFailed`. The visible symptom is therefore not an error page but
every tenant silently demoted to the 50-análisis plan with its quotas failing open, which is worse
than a loud failure and harder to spot.

Deploy order: **DDL 051 → publish → restart the App Service** (so EF's model cache is rebuilt) →
verify the field in Swagger on `GET /api/billing/usage` and `PUT /api/billing/config`.

The Portal does **not** need to ship first: this adds two optional JSON fields and no new enum
value, so a Portal that ignores them is unaffected.

## Rollback

Drop the constraint and the column:

```sql
ALTER TABLE [dbo].[TenantBillingConfig]
    DROP CONSTRAINT [DF_TenantBillingConfig_ChatContextBudgetTokens];
ALTER TABLE [dbo].[TenantBillingConfig] DROP COLUMN [ChatContextBudgetTokens];
```

Safe in the sense that nothing else references the column and no other table points at it, but it
must be preceded by rolling the API back to a build without the mapping — otherwise the deploy-order
failure above is exactly what happens. Any per-tenant budget a SuperAdmin had set is lost; the plan
label and the two monthly limits are untouched.
