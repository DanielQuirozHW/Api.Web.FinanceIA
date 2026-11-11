# 052 — The chat model stops being a deployment-wide constant

## What changed and why

The analyst chat model was **hardcoded**. `ClaudeService` held
`private const string ChatModel = "claude-sonnet-4-6"`, and `AnalysisController` repeated that same
literal four more times — on the assistant `ChatMessages` row, on the success `ApiCallLogs` row, on
the failure `ApiCallLogs` row, and in the comment above the cost arithmetic. Every tenant on every
stack got the same model, and an analyst had no way to trade cost against depth: the same model
answered "¿cuál fue el ratio de liquidez?" and "¿por qué el score cayó pese a que mejoró el margen?".

**052 adds the per-tenant allow-list.** A SuperAdmin sets which models a tenant may use; the analyst
picks one of them per conversation, ephemerally.

### Why this column is not like the three beside it

`[AnalysisMonthlyLimit]`, `[ChatTokensMonthlyLimit]` and `[ChatContextBudgetTokens]` are all
**allowances** — quantities that get consumed, where the interesting question is "how much is left?"
and exceeding one refuses a request.

`[ChatEnabledModels]` is a **capability set**. Nothing is spent against it, there is no running total,
and calling a model does not use up the right to call it again. It shares the table because it is
commercial state sold with the plan and set by the same SuperAdmin through the same endpoint — not
because it is the same kind of number. That difference is why it appears on `BillingUsageSnapshot`
with no "used" counterpart, and why `BillingPlan` carries it while `BillingQuotaFilter` ignores it
entirely.

### Why a CSV column rather than a child table

The same argument script 047 makes for having no `[BillingPlan]` catalogue table. The catalogue of
**models that exist** lives in application code, `Models/Financia/ChatModelCatalog.cs`, for the reason
`TenantSettingKeys` holds the settings defaults: a tenant with no row must resolve to something
sensible and must never fail, and adding a model must not require inserting a row for every existing
tenant.

This column records only **which of that catalogue** a tenant bought. A child table would add a join,
a foreign key to a catalogue that does not exist in SQL, and a second write path — to hold at most
three short strings that are always read together and always written together.

`NVARCHAR(400)` is width for the catalogue to grow into, not for this list: it holds roughly a dozen
ids at the length Anthropic currently uses, against 48 characters today. Deliberately not
`NVARCHAR(MAX)`, which would push a short scalar off-row for nothing.

### Why the default is *every* model, when 051's default was the smallest

051 backfilled to the minimum preset, on the principle 047 documents: *an unconfigured tenant should
get the least, never the most*.

That principle governs **allowances**, and it does not transfer here. The chat model was hardcoded
**on** for every tenant, so a row predating this script belongs to a tenant with a working chat.
Defaulting the column to a subset would **remove a capability** as a side effect of a schema change —
the analyst would open the model picker and find fewer options than the day before, with no
commercial decision behind it.

The safe default for an allowance is the least. The safe default for a capability the tenant already
had is what they already had. `BillingPlans` (all three presets) and
`ChatModelCatalog.SanitizeOrDefault` state the same list, so a tenant with no row, an inactive row,
and a blanked column all resolve to the same three models the `ALTER` backfills.

### Why `NOT NULL`

A nullable column would make "no list chosen" a third state the resolution path has to model,
alongside "no row" and "inactive row" — both of which already resolve to `BillingPlans.Minimum`.
`NOT NULL` with the default collapses it, and the `ALTER` backfills every existing row in place, so
there is no data migration and no separate `UPDATE`.

### The ids are real, unlike the numbers beside them

The figures in 047 and 051 are flagged `TODO(billing)` placeholders awaiting product. These three ids
are not: they are exact Anthropic model ids verified against the Messages API, and they carry **no
date suffix** — appending one names a model that does not exist.

What *is* still open is whether model access should become a commercial lever ("Sonnet 5 only on the
200 plan"). It deliberately is not one yet: all three `BillingPlans` presets carry the full
catalogue, and a SuperAdmin narrows an individual tenant through the endpoint. Tiering it is a
product decision, not a default this delivery should invent.

## DDL

`docs/database/052_tenantbillingconfig_add_chatenabledmodels.sql`, one guarded block:

```sql
ALTER TABLE [dbo].[TenantBillingConfig]
    ADD [ChatEnabledModels] NVARCHAR(400) NOT NULL
        CONSTRAINT [DF_TenantBillingConfig_ChatEnabledModels]
        DEFAULT (N'claude-haiku-4-5,claude-sonnet-4-6,claude-sonnet-5');
```

Guarded on `sys.columns`, `PRINT` in both branches, `GO`, no transaction — the conventions in
`docs/database/README.md`. Re-running never overwrites a list already set.

**There is no `CHECK` constraint, and cannot be one:** SQL has no way to validate against a catalogue
that lives in C#. An id misspelled by hand is accepted by the database and silently dropped by the
application on read. That is why `PUT /api/billing/config` — which validates every id against
`ChatModelCatalog` and answers 400 naming the offending one — is the preferred write path over SQL.

## Deploy order

**DDL first, on every tenant database, then publish, then restart the App Service.**

The column is mapped in `TenantBillingConfigConfiguration`, so EF Core emits it in every `SELECT` and
`INSERT` against `[dbo].[TenantBillingConfig]` — including the effective-config read that now runs on
every chat turn. Publishing first fails that read with `Invalid column name`, and nothing catches it
usefully: `TenantBillingConfigService` swallows the failure and degrades **every** tenant to the
minimum plan.

Note the failure is asymmetric, and the harmless-looking half is the dangerous one. The **chat would
keep working** — a degraded resolution carries `BillingPlans.Minimum`, whose model list is the whole
catalogue — while every tenant's analysis quota and configured plan silently reverted to the
50-análisis minimum. The symptom would be quota refusals on paying tenants, not a chat outage, so the
cause would not be obvious. The restart is required for EF's model cache to pick the column up.
