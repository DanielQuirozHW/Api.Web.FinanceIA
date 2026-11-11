# 068 — Bringing stored scoring config onto the v9 calibration

## What changed and why

Scoring algorithm version 9 (`ScoringService.ScoringAlgoVersion = "9"`) moved three **defaults** in
`Models/Financia/ScoringDefaults.cs`:

| Key | v8 default | v9 default | Why |
|---|---|---|---|
| `critical_floor_trigger_threshold` | 85 | **100** | A ratio is critical, and floors the score, only at or beyond its DANGER threshold. At 85 it fired between WARN and DANGER: ROE was critical below 1.5 % instead of at 0. |
| `threshold_leverage_warn` | 1.50 | **2.33** | Leverage and endeudamiento are the same quantity, `L = E / (1 − E)`. Endeudamiento 0.70 ⇒ L 2.333. |
| `threshold_leverage_danger` | 2.50 | **5.67** | Endeudamiento 0.85 ⇒ L 5.667. |

The triggering case: a Comercio dealer (anonymised; `CasoComercioConcesionario` in the tests),
endeudamiento 0.70 ("Atención") and leverage 2.29, which at the old thresholds scored a partial of 89.5, cleared the 85 trigger and floored a composite
of 18.28 up to 65 — 817 NOSIS points became 350, from a ratio weighing 5 % of the total.

A default only reaches a tenant with **no row** for the key, and it reaches it **per key**. Which
tenants' scores move at deploy therefore depends on which of the three keys each tenant has rows for.

## The three tenant states — which one changes its score at deploy

Rows come from two places, and they do not write the same keys:

- **007 / 014** seeded `SgrOneClickSandboxFB2` with every threshold **and** the trigger (85).
- **The Settings page** writes a row for every key it submits, at whatever value was showing. But
  `SettingsService.BuildDto` exposes only five groups — ratio weights, ratio thresholds, component
  weights, score thresholds, macro — and **never** `critical_floor_trigger_threshold`, which lives in
  `CriticalFloorKeys`. A tenant that saved the page has leverage rows (1.00 / 1.50 / 2.50) and **no
  trigger row**.

| State | Rows it holds | At the v9 deploy | When 068 runs for it |
|---|---|---|---|
| **A. No rows** | none for the trigger or leverage | **Score changes.** Trigger 100 and leverage 1.00 / 2.33 / 5.67, straight from the defaults. | Nothing to do — query 0 shows no rows. |
| **B. Full old rows** | trigger 85 **and** leverage 1.00 / 1.50 / 2.50 (e.g. the sandbox, from 007 / 014) | **No change.** Scores exactly as under v8. | Score changes: trigger 85 → 100 and leverage → 2.33 / 5.67 (the pair only if every merged triplet stays ordered). |
| **C. Mixed** | leverage 1.00 / 1.50 / 2.50, **no** trigger row (saved the Settings page) | **Score changes, partly.** The trigger moves to 100 at once, while leverage stays 1.50 / 2.50 — a ratio is critical only at its DANGER, but leverage's DANGER is still the v8 2.50. | Leverage → 2.33 / 5.67. The trigger needs nothing (no row). |

The mirror of C — a trigger row at 85 but no leverage rows, which only a direct API call or the DBA
can produce — moves leverage at deploy and keeps the trigger until 068 runs.

State C on the case above: leverage 2.29 against 1.50 / 2.50 is still a partial of 89.5, but 89.5 no
longer reaches the trigger of 100, so no floor applies. The composite of 18.28 stands (820 NOSIS),
where state B still floors it to 65 (350) and state A scores 15.87 (840).

A fourth situation cuts across all three states: a **partial** leverage row, base or sector, that the
new default warn 2.33 inverts (a lone danger of 2.00). The engine does not score on it — see
"Leverage ordering" below — and query 0b shows it.

## ⚠ A DATA script, run only after analyst sign-off

No schema changes, so the API does not depend on it and `--verify-schema` does not see it. Deploying
v9 without it is safe, but it is not a no-op for every tenant: states A and C above change their score
at deploy, only state B waits for this script. It **changes scores** from
the moment it runs, so run it only after the analyst has confirmed the new calibration for that tenant.
Saved analyses are never recomputed; they keep their score and their `scoringAlgoVersion`.

**One tenant per run, chosen by `@TenantId`.** The business database is shared — the API binds one
static `FinanciaDefault` connection and separates tenants by the `TenantId` column — so connecting to
"the tenant's database" does not pick a tenant. Set `DECLARE @TenantId NVARCHAR(100) = N'<slug>'` at
the top of the script; every statement, query 0 included, is scoped to it. Left at the `N'<tenant>'`
placeholder the script `THROW`s before it reads or writes anything, which also stops it from being run
by accident as a "pending numbered script" ahead of a deploy.

## What it touches

- Only rows still at the **exact** old default. A value someone chose on purpose is not a leftover
  default and stays.
- Leverage moves as a **pair**, per tenant, only when every leverage row that tenant has is at the old
  default. Moving `warn` to 2.33 under a customised `danger` of 2.00 would invert `warn < danger`.
- Only base keys — never `__sector__` overrides, which are deliberate by definition. The script's
  query 0 lists them so the DBA and the analyst can review them by hand.
- …but sector rows are **read**, because the engine merges them over the base row by row. A partial
  override such as a lone `threshold_leverage_danger__sector__Comercio = 2.00` is coherent over a base
  warn of 1.50 and inverted over 2.33 (Comercio would score on 1.00 / 2.33 / 2.00). So the leverage
  pair only moves when every merged triplet — base, and each sector over it — is still strictly ordered
  after the move; otherwise it is skipped with a PRINT and the trigger still moves.

## Leverage ordering is checked, not just described

On the merged triplets, a missing row read as the v9 default (1.00 / 2.33 / 5.67), exactly as the
engine reads it:

| Step | When | What happens |
|---|---|---|
| 0b | before anything | Shows what the **v9 deploy alone** leaves out of order for this tenant (a partial row that the new default warn inverts). Information only. |
| 2 | simulating the move | Decides whether the leverage pair moves (see above). |
| 4 | inside the transaction, before `COMMIT` | Any triplet out of order → `THROW`, and `XACT_ABORT` rolls the whole run back. This includes an inversion the deploy alone caused: fix those rows first, then run. |

Independently of the script, v9 makes such an inversion harmless to the score and impossible to save:

- **Engine.** After base and sector rows merge, a ratio whose ok / warn / danger triplet is not strictly
  ordered is scored on its **complete default triplet** — all three values, never a mix — logged at
  Warning with tenant, sector and the refused values, and listed in the breakdown as
  `thresholdFallbacks` (e.g. `["leverage"]`).
- **Settings.** Base saves now apply the same ordering rule the sector saves always had, to the base
  triplets and to every sector profile layered over them.

## How to decide whether a tenant needs it

Run query 0 on its own, for that tenant:

```sql
DECLARE @TenantId NVARCHAR(100) = N'<slug>';

SELECT TenantId, ConfigKey, ConfigValue, UpdatedAt
FROM   dbo.ScoringConfig
WHERE  TenantId = @TenantId
  AND (ConfigKey LIKE 'critical\_floor\_%'          ESCAPE '\'
   OR  ConfigKey LIKE 'threshold\_leverage\_%'      ESCAPE '\'
   OR  ConfigKey LIKE 'threshold\_endeudamiento\_%' ESCAPE '\')
ORDER  BY ConfigKey;
```

No row for the trigger and leverage keys ⇒ the tenant already follows the v9 defaults and needs nothing.

## Rollback

Per tenant, set the three rows back to 85 / 1.50 / 2.50, or delete them to follow the code defaults.
The exact statements are at the foot of the script.
