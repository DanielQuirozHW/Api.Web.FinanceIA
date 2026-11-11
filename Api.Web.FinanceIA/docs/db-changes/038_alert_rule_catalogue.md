# 038 — Alert rule catalogue and the second alert channel

## What changed and why

Alerts were authored entirely by the model. Analysing the SAME balance-sheet PDF six times
(extraction prompt v2.16) showed extraction is deterministic — all 16 `balance_check` fields and
all 8 computed ratios were byte-identical on every run — while the alert layer was not:

- the same finding was emitted under different `code` strings
  (`INCUMPLIMIENTO_RT54_PARRAFO659` vs `INCUMPLIMIENTO_RT54_PARR659`, and two others);
- four cash-flow/compliance findings oscillated between the alerts and anomalies channels,
  producing either a 7-alert or an 11-alert report for one document.

This change adds two things:

- **`[dbo].[AlertRuleCatalogue]`** — a per-tenant catalogue of deterministic alert rules. It is
  the closed identifier set the model's free-form `code` never had, plus an analyst-controlled
  severity and threshold. A rule engine evaluates every ENABLED row against the already-stable
  `BalanceJson`, so the same input always produces the same alerts.
- **`[dbo].[AnalysisResults].[CatalogueAlerts]`** — storage for that deterministic channel,
  alongside the existing `RiskAlerts`, which keeps its present meaning (the model's own findings)
  and its present content, unchanged.

**Every seeded rule ships DISABLED.** Nothing fires until an analyst enables it. The seeded
`ThresholdValue` figures are round synthetic placeholders, deliberately provisional — they are not
recommendations.

Existing rows are unaffected: `CatalogueAlerts` stays `NULL` and is deliberately **not**
backfilled, because the rules ship disabled and no historical verdict could be reconstructed
honestly. `RiskAlerts` is never repurposed or emptied, so the PDF export and the current Portal
keep rendering exactly as they do today.

## DDL

Script location: `docs/database/038_alert_rule_catalogue.sql`

Objects created:

| Object | Type |
|---|---|
| `[dbo].[AlertRuleCatalogue]` | table |
| `PK_AlertRuleCatalogue` | primary key (clustered, `RuleId`) |
| `UQ_AlertRuleCatalogue_Tenant_Code` | unique constraint (`TenantId`, `Code`) |
| `CK_AlertRuleCatalogue_Severity` | check — `low` \| `medium` \| `high` \| `critical` |
| `IX_AlertRuleCatalogue_TenantId_Enabled` | nonclustered index (the engine's only query) |
| `[dbo].[AnalysisResults].[CatalogueAlerts]` | column, `NVARCHAR(MAX) NULL` |
| `CK_AnalysisResults_CatalogueAlerts` | check — `NULL OR ISJSON() = 1` |

Seeded rules for `SgrOneClickSandboxFB2`, all with `Enabled = 0`:

| Code | Severity | ThresholdValue |
|---|---|---|
| `PASIVO_100_CORRIENTE` | high | `NULL` |
| `INVENTARIO_ELEVADO` | medium | `0.5000` |
| `COBERTURA_INTERESES_INDETERMINABLE` | medium | `NULL` |
| `FLUJO_NO_CONCILIA` | high | `NULL` |
| `FLUJO_FINANCIACION_CERO` | low | `NULL` |

Audit columns (`CreatedAt`, `UpdatedAt`, `UpdatedBy`) are not optional: a threshold change alters
which alerts fire and therefore what the SGR decides on, so who moved a number and when is
recorded.

## Pre-conditions

- **Must be executed BEFORE deploying the matching commit**, on **every** tenant database.
  This is a hard ordering requirement, not a graceful degradation. The two halves differ:
  - The **table** is read inside a fault-isolated block. If it is missing, the block catches, logs
    a warning, and the analysis completes with the model's findings intact.
  - The **column** is mapped in `AnalysisResultConfiguration`, so EF Core emits it in every
    `SELECT` and `INSERT` against `AnalysisResults`. If it is missing, analysis creation **and**
    retrieval fail outright, and no application-level try/catch can protect that.
- The script is idempotent — table, index, column, check constraint and every seed row are
  guarded, so it is safe to run multiple times. Re-running never duplicates a seed row and never
  resets an analyst's `Enabled` flag or `ThresholdValue` back to the placeholder.

## Rollback

Reverse order. The column is nullable with no dependency, and the table is only read by the rule
engine, so neither drop loses data outside this feature.

```sql
ALTER TABLE [dbo].[AnalysisResults] DROP CONSTRAINT [CK_AnalysisResults_CatalogueAlerts];
GO
ALTER TABLE [dbo].[AnalysisResults] DROP COLUMN [CatalogueAlerts];
GO
DROP TABLE [dbo].[AlertRuleCatalogue];
GO
```

Roll the application back first: the deployed code maps `CatalogueAlerts`, so dropping the column
under a running deployment breaks analysis creation and retrieval.

## Verification

After applying the script, all three queries must return the row counts shown.

```sql
-- 1. The table exists → 1 row
SELECT TABLE_NAME
FROM   INFORMATION_SCHEMA.TABLES
WHERE  TABLE_NAME = 'AlertRuleCatalogue';

-- 2. The column exists → 1 row
SELECT COLUMN_NAME
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'AnalysisResults'
  AND  COLUMN_NAME = 'CatalogueAlerts';

-- 3. Five seeded rules, ALL disabled → 5 rows, every Enabled = 0
SELECT Code, Enabled, Severity, ThresholdValue
FROM   [dbo].[AlertRuleCatalogue]
WHERE  TenantId = N'SgrOneClickSandboxFB2'
ORDER  BY Code;
```

## Tenant checklist

| Tenant | Run date | Run by | Verified |
|--------|----------|--------|----------|
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
|        |          |        |          |
