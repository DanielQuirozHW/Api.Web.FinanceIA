# 086 — Seed ENDEUDAMIENTO_ELEVADO in the alert rule catalogue (data fix)

## Why

`AlertRuleEngine` implements six deterministic rules (`AlertRuleEngine.ImplementedCodes`), but DDL 038 seeded
only five. `ENDEUDAMIENTO_ELEVADO` (total liabilities / total assets strictly above `ThresholdValue`) has no
`AlertRuleCatalogue` row, so no tenant has a `DisplayNameEs` for it and Configuración cannot show or enable it.

The engine evaluates only the tenant's **enabled** catalogue rows and has no code default for any rule: with no
row, the rule never fires.

## What it does

- **DATA FIX (DML), no schema change.** Optional per tenant; **not a deploy blocker**; safe before or after the
  deploy; no restart. `--verify-schema` neither needs nor sees it.
- Run once in **each Financia DB** whose tenants should have the rule. It seeds every TenantId that already has
  at least one catalogue row and no `ENDEUDAMIENTO_ELEVADO` row; `@Excluded` leaves a tenant out.
- One row per tenant, following 038's conventions:

  | Column | Value |
  |---|---|
  | `Enabled` | `0` (disabled, like every 038 seed) |
  | `Severity` | `high` |
  | `ThresholdValue` | `0.8500` — a provisional placeholder, the same quantity and figure as the scoring default `threshold_endeudamiento_danger` |
  | `DisplayNameEs` | `Endeudamiento elevado` (as `Servicios/Pdf/AlertTitles.cs`) |
  | `DescriptionTemplateEs` | `El pasivo representa el {valor} % del activo total, por encima del umbral configurado de {umbral} %.` |

- `@Apply = 0` (default) **previews** the tenants (result set 1). `@Apply = 1` writes them in **one
  transaction** (XACT_ABORT ON). Idempotent: the INSERT re-checks `NOT EXISTS`, and
  `UQ_AlertRuleCatalogue_Tenant_Code` is the backstop.

## Behaviour

The engine behaves **the same with and without the row** while it stays disabled: `GetEnabledAsync` filters
it out and `AlertRuleEngine.Evaluate` checks `Enabled` again (pinned by `AlertRuleEngineTests`). Once an
analyst enables it (PATCH `/api/alert-rules/{id}`, a proportion in (0, 1]), new analyses get the alert when
liabilities exceed that share of assets. Past analyses are not re-evaluated.

## What it does not touch

- Tenants with no catalogue rows at all (their catalogue is set up as a whole).
- Any existing row, including a hand-made `ENDEUDAMIENTO_ELEVADO` row and an analyst's edits.

⚠ It bypasses the application audit trail (no AuditLogs row). Record the run in `docs/database/README.md`.
