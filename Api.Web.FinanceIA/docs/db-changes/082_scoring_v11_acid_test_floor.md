# 082 — Bringing a stored acid-test floor onto the v11 default

## What changed and why

Scoring algorithm version 11 (`ScoringService.ScoringAlgoVersion = "11"`) changes how a critical
**prueba ácida** floors the score:

| | v10 | v11 |
|---|---|---|
| A critical prueba ácida floors the score | always | only while **liquidez corriente is measured and strictly below** `threshold_liquidez_corriente_warn` (the merged value — `__sector__<Sector>` rows over the base rows) |
| Liquidez corriente not read | floor | floor (no evidence of coverage, so the conservative reading stands) |
| Zero current liabilities (best band) | — | not weak → no acid-test floor |
| Default `critical_floor_prueba_acida` | 60 | **50** |
| Switch | — | `critical_floor_acid_test_requires_weak_current_ratio`, 0/1, code default **1** (0 = v10) |

Why: a critical acid test says much of the current assets is stock. While the current ratio is at or
above its warning threshold, that stock still covers the current liabilities; flooring the score on
the acid test alone punished inventory-heavy companies (distributors, dealers) whose liquidity was
otherwise sound. The acid test still **scores in the composite** at its full weight and still reads
"Critico" in the breakdown — only the floor is waived, exactly as v10 does for a profitability ratio
on a positive operating result. A waived acid test is left out of `criticalRatios`; the breakdown
records `acidTestFloorWaived` and `currentRatioVsWarn` (`below` / `at_or_above`, null when the rule
was not consulted or the current ratio was not measured).

`critical_floor_liquidez_corriente` stays 60. No other floor moves.

## Which tenants change at deploy

A default reaches a tenant only for keys it has **no row** for. The Settings page never exposes
floor keys (`SettingsService.BuildDto`), so floor rows exist only where `014` seeded them (the
sandbox, at 60) or the DBA / a direct API call wrote them.

| Tenant state | At the v11 deploy | When 082 runs |
|---|---|---|
| No `critical_floor_prueba_acida` row | Rule on, floor **50** at once | Nothing to do |
| Row at exactly **60** (e.g. the sandbox) | Rule on, floor still **60** | Row → 50 |
| Row at a custom value (55, 70, …) | Rule on, floor at that value | Untouched — a deliberate calibration |

The new switch needs no row in any state: its code default is 1. A tenant that wants v10 behaviour
gets a row at 0 (Settings PUT validates it as 0 or 1, like the v10 flag).

## Order

**Code first is safe.** The script is DML only — no schema change, nothing for `--verify-schema` to
check, no App Service restart. A tenant without a row already scores with floor 50 on the deployed
code; the script only aligns tenants whose row still says 60. It can run before, after or never.

## Running it

- Once per tenant, with `@TenantId` set; left at `N'<tenant>'` it THROWs before reading anything.
- Idempotent: only a row at exactly 60 moves.
- One transaction, `XACT_ABORT ON`; swap the final `COMMIT` for `ROLLBACK` for a dry run.
- Read-only SELECT before and after (both the floor and the switch, sector rows included).

## ⚠ It bypasses the Settings audit trail

A direct `UPDATE` writes no `AuditLogs` row, unlike `PUT /api/settings/scoring`. Record the run
(tenant, date, operator) in the execution history table of `docs/database/README.md`.

## Saved analyses

Nothing is recomputed. Analyses already saved keep their score and their `scoringAlgoVersion`
("10" or earlier) until they are re-scored; only a re-score or a new analysis uses version 11.

## Rollback

Per tenant, by hand: set the row back to 60 (commented in the script). To restore the v10 rule
itself, add `critical_floor_acid_test_requires_weak_current_ratio = 0` for that tenant.
