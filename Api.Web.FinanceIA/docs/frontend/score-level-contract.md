# Score level contract — one source of truth for risk levels

**Problem.** The engine classifies a score as Low / Medium / High / Critical with the tenant's
`score_threshold_low/medium/high`, which an Admin can edit. A Portal that recomputes levels or NOSIS
ranges from the raw score with hardcoded `35 / 60 / 80` contradicts the BFF's own level badge the day
a tenant changes a cutoff.

**Rule.** The level always comes from the BFF. Ranges are built only from the cutoffs the BFF sends.
No client constant, ever.

## What the BFF sends

### 1. Per analysis — the cutoffs that score was classified with

`GET /api/analyses/{id}` (and `/ratios`) → `scoreBreakdown.levelCutoffs`, top level, beside
`scoringAlgoVersion`:

```jsonc
"scoreBreakdown": {
  "...": "...",
  "levelCutoffs": { "low": 35, "medium": 60, "high": 80 },
  "scoringAlgoVersion": "10",
  "reliabilityFacts": { "...": "..." }
}
```

- Written by every new analysis and every re-score. Reporting only: no score moved, no
  `scoringAlgoVersion` bump.
- **Absent** on rows scored before it existed → treat as null and use (2).
- Values are motor-scale decimals as stored, e.g. `30.0000` from a DB row.
- It includes any sector-suffixed override, which is why it beats (2) whenever present.

### 2. Per tenant — the CURRENT cutoffs (fallback for old rows)

`GET /api/billing/usage` → `levelCutoffs` (readable by every role):

```jsonc
{ "...": "...", "levelCutoffs": { "low": 35, "medium": 60, "high": 80 } }
```

- The tenant's base config with code defaults per missing key.
- `null` means it could not be read. Render no range, not a hardcoded one. `isDegraded` is not
  affected.

### 3. Every displayed score comes with its stored level

| Surface | Score | Stored level |
|---|---|---|
| `GET /api/analyses` items, `GET /api/analyses/{id}` | `aiSuggestedScore`, `finalRiskScore` | `aiSuggestedLevel`, `finalRiskLevel` |
| Dashboard `my-work` / `approvals.waitingForMe` / `oversight.teamQueue` items | `aiSuggestedScore` | `aiSuggestedLevel` |
| Re-score summary / history / `oversight.recentRescores` | `previousScore`, `newScore` | `previousLevel`, `newLevel` |
| `GET /api/entities` items, `GET /api/entities/{id}` (header) | `latestScore` | `latestRiskLevel` |

For entities, the latest *scored* analysis result also provides `latestScoreAnalysisId` and
`latestScoredAt` (its re-score time, else its generation time). Score and level come from the same
row. All four fields are null exactly when the entity has no scored analysis. They are filled on the
list and on `GET /api/entities/{id}`; the mutation and CUIT responses are not enriched, same as
`latestAnalysisStatus`.

`riskScore` / `riskScoreUpdatedAt` on entities are **legacy**: `Entities.RiskScore` is never written
by this API and has no level. Do not render them.

## Rules for the Portal

1. **Badge and colour come from the stored level string.** Never derive them from a score.
2. **Cutoffs for a row:** use `scoreBreakdown.levelCutoffs`; if that is absent, use
   `billing/usage.levelCutoffs`; if that is null, render no range.
3. **Motor scale** (0–100, 0 = no risk): Low is `score ≤ low`, Medium is `≤ medium`, High is
   `≤ high`, Critical is anything above.
4. **NOSIS scale** uses `toNosis(s) = (100 − round(s)) × 10`, clamped to 0..1000 and inverted
   (`ScoreScale.ToNosis`):

   | Level | NOSIS range |
   |---|---|
   | Low | `toNosis(low)` – 1000 |
   | Medium | `toNosis(medium)` – `toNosis(low) − 10` |
   | High | `toNosis(high)` – `toNosis(medium) − 10` |
   | Critical | 0 – `toNosis(high) − 10` |

   Defaults give Low 650–1000, Medium 400–640, High 200–390 and Critical 0–190.
5. **Edge case:** `toNosis` rounds, so a fractional score just above a cutoff can show a NOSIS
   number inside the neighbouring range. For example, 35.4 is Medium but shows 650. This is why
   rule 1 holds: the badge comes from the stored level, never from the NOSIS value.
