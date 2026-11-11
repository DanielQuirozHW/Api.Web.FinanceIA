# Balance-extraction model: settings, switching a client, comparison harness

The balance extraction (`ClaudeService.AnalyzeDocumentAsync`, both the document pass and the
image fallback) calls the Claude model named by an App Setting. With no setting it sends the default
request: `claude-sonnet-4-6`, `temperature 0`, `max_tokens 16000`, prompt 2.21, 120 s per attempt —
pinned byte for byte, `InputHash` included, by `ClaudeExtractionPayloadTests`. It differs from the
request sent before 2026-11-03 **only** in `max_tokens` (8000 then) and the prompt edits since
(2.19–2.21), each undone back to the previous pin; the test proves it.

## Settings (App Service → Configuration → Application settings)

| Setting | Default | Notes |
|---|---|---|
| `Anthropic__ExtractionModel` | `claude-sonnet-4-6` | An id from `Models/Financia/ExtractionModelCatalog.cs`. Unknown → the default + an Error logged once. |
| `Anthropic__ExtractionEffort` | unset (API default `high`) | `low` \| `medium` \| `high` \| `xhigh` \| `max`, sent as `output_config.effort` only for a model that supports it. Invalid → none sent + an Error once; on Sonnet 4.6 → ignored + a Warning. |
| `Anthropic__ExtractionTimeoutSeconds` | the model's (120 / 300) | Per attempt, clamped to 30..900. Timeouts are retried (3 attempts, 5 s / 15 s backoff), as before. **Set `480` for Sonnet 4.6** — see below. |

Saving a setting restarts the app; the value is read once per process. A bad value never fails an
analysis — it degrades to today's behaviour and is loud in Application Insights.

## What differs per model

| | `claude-sonnet-4-6` (default) | `claude-sonnet-5-5` | `claude-sonnet-5` |
|---|---|---|---|
| `temperature` | `0` | **omitted** (any value → HTTP 400) | omitted |
| `max_tokens` | 16000 (one escalation to 32000) | 32000 (thinking + JSON share it) | 32000 |
| thinking | none | adaptive, on by default (no `thinking` field sent) | adaptive |
| `output_config.effort` | never sent | when `ExtractionEffort` is set | when set |
| per-attempt timeout | 120 s | 300 s | 300 s |
| price (USD / MTok in / out) | 3 / 15 | 2 / 10 | 2 / 10 |

Both passes read the first **text** block of the response (a thinking block comes first on 5.x). A
response with `refusal` fails the pass (`ClaudeApiException`, reason in `DiagnosticMessage`), without a
retry. A response that stops at `max_tokens` is handled as below.

## Sonnet 4.6 output: 16000 tokens, one escalation, the timeout it needs

**Why 16000.** Complete Argentine balances with full notes already answered in 7.4k–8.4k output tokens,
and a 10-page native balance (EL OMBU, 2026-11-03) was cut off at the old 8000 on every run. Only the
tokens produced are billed, so a balance that finishes earlier costs exactly what it did.

**The timeout it needs.** Sonnet 4.6 writes this JSON at roughly 35 tokens/s (EL OMBU: ~8000 tokens in
~3.5 min), so a 16000-token answer takes **about 480 s** — far beyond the spec's 120 s, which is kept in code so nothing changes for a
client until operations decide. On a client whose balances are long, set
`Anthropic__ExtractionTimeoutSeconds=480`. With the 120 s default such a balance times out three times
(≈ 380 s) and fails as a timeout, as EL OMBU did.

**One escalation on `max_tokens`.** When a pass (first or second) stops at its cap, that pass is re-sent
**once** with:

- `max_tokens` doubled, capped at 32000 (`ExtractionModelCatalog.MaxEscalatedTokens`);
- the per-attempt timeout scaled by the same factor, capped at 900 s (120 → 240 s; 480 → 900 s);
- **one** transport attempt (a timeout or 5xx on it is not retried);
- nothing else changed: the escalated request is the first one with a different `max_tokens`
  (`ClaudeExtractionPayloadTests` pins it).

Only for a spec that sends a temperature (Sonnet 4.6); the 5.x specs already sit at 32000. Never more than
one escalation per pass. The tokens of both calls are summed into the pass and so into the one
Extraction `ApiCallLogs` row, also when the escalated call fails. If the escalated answer stops at
`max_tokens` too, the pass fails with `DiagnosticMessage` "… hit max_tokens (32000) after escalation …",
which is what `ApiCallLogs.ErrorMessage` records (never the client text). `AiInputHash`/`AiOutputHash`
describe the call that answered — the escalated one, after an escalation.

**Near-cap warning.** A successful pass whose output reaches 75 % of the `max_tokens` it was sent with
(`ExtractionModelCatalog.NearCapWarningRatio`) logs a Warning
`Claude extraction near max_tokens: tenant … model … max_tokens … output_tokens … pct …` — the early sign
that a client's balances are outgrowing the cap. Query it in Application Insights before it turns into
failures.

**The extraction budget.** A run that fails ONLY because of `max_tokens` (after the escalation, or on a
5.x model at 32000) records `Documents.LastExtractionError = claude_max_tokens` and does **not** increment
`Documents.ExtractionAttempts`: the cap is ours, not the document's, so it must never end in the trigger's
409 `extraction_budget_exhausted`. The same holds for every other failure on our side —
`claude_server_error` (5xx), `claude_network_error`, `claude_rate_limited` (429), `claude_auth_error`
(401/403) and an interrupted run. A timeout (`claude_timeout`), a refusal (`claude_refusal`), an answer that
is not a balance (`claude_unreadable_answer`) and any other API error (`claude_api_error`) keep counting.
`GET /api/analyses/runs` reports every `claude_*` code as `failureCode: model_error`, plus a finer
`failureReason` / `causeGroup` (`document` | `ours`) and the file's `attemptsRemaining`
(`AnalysisRunFailureReasons`; the synchronous 502 carries the same three); the 409's `lastFailureCode` says
`unreadable` for a refusal or an unreadable answer (and asks for a better file), and otherwise tells the
analyst to contact support. The flip side: a balance that keeps hitting 32000, or a provider that keeps
failing, has no budget stop, and each re-trigger of a max_tokens balance costs about 48k output tokens
(≈ USD 0.75) — watch the failed Extraction rows in `ApiCallLogs`.

`Analyses.AiModel` and `ApiCallLogs.Model` record the model that ran, and `ApiCallLogs.EstimatedCostUsd`
uses its price. `Analyses.AiTemperature` stays `0.00` for every model: the `CK_Analyses_AiTemperature`
constraint allows nothing else, so on a 5.x analysis it means "no temperature override sent".

## The second pass (integrity-driven)

A first pass is re-read once, differently, when its confidence is below
`Anthropic__ExtractionConfidenceFallbackThreshold` (0.60) **or** it fails a totals/structure integrity
check (`assets_components`, `liabilities_components`, `accounting_equation`, `current_assets_items`,
`current_liabilities_items`, `non_current_liabilities_items`, `column_swap`). The second pass sends
page images only — no PDF document block, so a garbage text layer (Type 3 fonts with no ToUnicode)
cannot mislead it — and turns upright any page whose text is drawn rotated inside an unrotated page
(`PdfPageOrientation`: PdfPig glyph direction, local, free, deterministic; a scanned page without a
text layer is rendered as it is).

| `ExtractionMethod` / `integrity.extraction.method` | Meaning |
|---|---|
| `document` / `image` | first pass kept (`image` = file over 5 MB) |
| `image-fallback` | second pass kept, no page turned |
| `image-oriented` | second pass kept, at least one page turned upright |

Kept pass: integrity passed (having evaluated every structure check the other pass failed) > fewer
failed structure checks (over checks both ran) > higher confidence (capped at 0.69 for a pass that
does not count as passed) > the first pass. `integrity.extraction` records `secondPassRan`,
`secondPassTriggers`, `keptPass`, `keptReason` and `rotatedPages`; the logs carry check names and
counts, never a figure. Cost: one more extraction call, summed into the same ApiCallLogs row — only
for analyses whose first pass does not add up. Latency: one more pass, against the same ~230 s
front-end cut as before. A second pass that throws for any reason keeps the first pass
(`keptReason = second_pass_failed`).

## What the analysis detail exposes (`extractionIntegrity`)

Read at response time from `BalanceJson`; additive, and none of it moves the score.

| Member | Meaning |
|---|---|
| `passed`, `failedChecks`, `affectedRatios` | as before. `passed` still means "no check that ran failed", so a report whose checks were all skipped passes |
| `verified` | the report exists AND `assets_components`, `liabilities_components` and `accounting_equation` all RAN (passed or failed). Independent of `passed` |
| `notVerifiedReasons` | `integrity_unavailable` (the evaluation threw: `"integrity": null`, which now reads as `passed=false`, `verified=false` instead of a null block), or one `<check>_skipped` per structural check that did not run. The `integrity_unverified` reliability reason names them, one clause each, in the Portal's `NOT_VERIFIED_REASON_ES` wording (`ScoreReliabilityMessages.IntegrityUnverified`); an unknown code keeps the generic sentence |

**A null component subtotal.** `assets_components` / `liabilities_components` still run when one
component's subtotal is null: with no rubros, and the other component alone equal to the total, it is
taken as zero (`checks[].inferredZero`); with rubros, every amount read and summing to ≥ 0, it is taken as
the sum of those rubros (`checks[].inferredFromItems`, zero rubros included). A rubro that contradicts the
total therefore FAILS the check, a structure check that triggers the second pass, instead of leaving the
extraction unverified. A negative sum (a trial balance's credit-signed rubros under a null subtotal, which the
normalizer never flips), a rubro without an amount, or both subtotals null keep the skip. The figures
themselves stay as read.
| `amountsUnit`, `amountsUnitAssumed` | the printed unit the figures were scaled from (`pesos`/`miles`/`millones`) and whether pesos was assumed; null when unavailable |
| `fiscalYear` | `{ requested, detected, status }`: `Analysis.FiscalYear` vs `fiscal_year_detected`, `match` / `mismatch` / `not_detected` |
| `secondPassRan`, `keptPass` | as before |

**Fiscal-year rule.** A fiscal year is identified by its CLOSING year (a 31/08/2025 close is 2025),
as the prompt and the legajo integrity check already assume. There is no ±1 tolerance: the
commonest real error is last year's balance, and the extraction does not report the closing month.
On a mismatch a new analysis gets an es-AR anomaly; when the document is exactly one year ahead (a
non-calendar ejercicio labelled with its starting year) the anomaly adds that the ejercicio is
identified by its closing year.

The per-ratio score breakdown (`scoreBreakdown.breakdown.<ratio>`) gains `exclusionReason` on
excluded ratios: `unread` (an operand was not extracted) or `not_meaningful` (operands read, the
formula has no meaning: equity <= 0, a zero denominator, negative inventory). It is omitted when
null.

When the scoring engine fails, the analysis is saved WITHOUT a score (`aiSuggestedScore: null`)
instead of a neutral 50, and a re-score refuses with 500 `rescore_failed`, keeping the previous
score. A failed extraction writes one `ApiCallLogs` row (`IsSuccess = 0`, `Operation = Extraction`,
`DocumentId` set, the tokens and cost when the API reported usage).

## Before switching a client

1. Run the comparison harness (below) over 15–20 of the client's real balances, with
   `claude-sonnet-4-6` as the baseline and the candidate model (and efforts).
2. Read the summary: fields identical / within 0.5 %, the worst fields, score and band changes, failures,
   fallback rate, cost per balance, **latency p50/p95**.
3. **Latency:** the Portal calls the analysis synchronously, and the App Service front end cuts an HTTP
   request at about 230 s. The analysis still completes on the server, but the analyst sees an error.
   If the candidate's p95 is near that (the report flags > 200 s), try a lower effort, or move the Portal
   to `Prefer: respond-async` first.
4. With more than one App Service instance, `Analysis:StaleProcessingMinutes` (default 20) must exceed the
   worst-case run. Per pass that is 3 attempts × T + 20 s of backoff, plus — Sonnet 4.6 only — one
   escalated attempt of min(2T, 900 s); two passes per run:

   | Model, per-attempt timeout T | Worst case | Set `StaleProcessingMinutes` ≥ |
   |---|---|---|
   | Sonnet 4.6, 120 s (default) | 2 × (380 + 240) s ≈ 21 min | 25 |
   | Sonnet 4.6, 240 s | 2 × (740 + 480) s ≈ 41 min | 45 |
   | **Sonnet 4.6, 480 s (recommended)** | 2 × (1460 + 900) s ≈ 79 min | **80** |
   | Sonnet 5.x, 300 s (no escalation) | 2 × (900 + 20) s ≈ 31 min | 35 |

   A BACKGROUND run whose first attempt fails with a 5xx, a network error or a 429 is retried once
   automatically after 60 s (a 429's retry-after, up to 120 s), with its document kept in Processing — so
   on another instance it can look stuck for two worst-case runs plus 120 s. Double the figures above
   when the Portal runs analyses with `Prefer: respond-async` on more than one instance.

   The code default stays 20: one instance is the assumed deployment, and the sweep never releases a run
   the instance itself holds. Raise it together with `ExtractionTimeoutSeconds` when you scale out.
   Every one of these is far past the ~230 s front-end cut, so a client on long balances wants the Portal
   on `Prefer: respond-async`.
5. Set `Anthropic__ExtractionModel` (and optionally `Anthropic__ExtractionEffort`) on that client's App
   Service only, and watch `ApiCallLogs` (Operation `Extraction`) and the Errors for the first analyses.

To roll back, delete the setting.

## The comparison harness

`Api.Web.FinanceIA.Tests/Servicios/ExtractionModelComparisonHarnessTests.cs` — opt-in, **live, paid**;
in CI it reports as skipped. For every PDF × variant × run it calls the real `AnalyzeDocumentAsync`
(both passes, same fallback rule), computes the ratios with `RatioCalculationService`, and scores them
with BCRA and macro **held fixed** (no debt; inflation 5.0) on `ScoringDefaults`, identical for every
variant, so any score difference comes from the extraction alone.

| Variable | Required | Meaning |
|---|---|---|
| `FINANCIA_MODELCOMPARE_DIR` | yes (the gate) | Folder with the balance PDFs, **outside the repo** (enforced). |
| `FINANCIA_MODELCOMPARE_OUT` | yes | Output folder, **outside the repo** (enforced). |
| `FINANCIA_MODELCOMPARE_MODELS` | no | CSV, default `claude-sonnet-4-6,claude-sonnet-5-5`. The first is the baseline. |
| `FINANCIA_MODELCOMPARE_EFFORTS` | no | CSV, e.g. `high,medium`: each becomes its own variant for a model that supports effort (`claude-sonnet-5-5@high`). |
| `FINANCIA_MODELCOMPARE_RUNS` | no | Runs per variant, default 1. With 2+, the baseline against itself gives the noise floor. |
| `FINANCIA_MODELCOMPARE_DOCTYPE` / `_FISCALYEAR` | no | As the determinism harness. Default `Balance` / none. |
| `Anthropic__ApiKey` or `ANTHROPIC_API_KEY` | yes | |
| `FINANCIA_MODELCOMPARE_CONFIRM` | to spend | Must be `yes`. Without it the test prints the plan and an upper-bound cost and **fails without sending anything**. |
| `FINANCIA_MODELCOMPARE_BASELINE_RUNS` | no | A previous run's `…-runs.json` (outside the repo): it becomes the baseline group, so two COMMITS (e.g. a prompt change) are compared with nothing re-sent. Same PDFs enforced by SHA-256 before anything is spent. |

```bash
export FINANCIA_MODELCOMPARE_DIR=~/balances-cliente-x      # outside the repo
export FINANCIA_MODELCOMPARE_OUT=~/comparaciones
export FINANCIA_MODELCOMPARE_EFFORTS=high,medium
export ANTHROPIC_API_KEY=...
dotnet test Api.Web.FinanceIA.Tests -c Release \
  --filter FullyQualifiedName~ExtractionModelComparisonHarnessTests \
  --logger "console;verbosity=detailed"                      # 1st run: read the plan and the bound
export FINANCIA_MODELCOMPARE_CONFIRM=yes                     # then spend it
```

The cost bound assumes both passes at full `max_tokens` for every run and the 5.x tokenizer; retries
and the Sonnet 4.6 max_tokens escalation are not included.

**Output** (timestamped, in `_OUT`; all three open with "⚠ DATOS DE CLIENTES: no subir al repo ni
compartir"):

- `model-comparison-{ts}.md` — method, the summary per variant against the baseline (fields identical /
  within tolerance, the 10 worst fields by path, score delta max / p95, band changes, failures by type,
  fallback rate, USD per balance, latency p50 / p95), then the detail per balance. PDFs appear only as
  `doc-01`, `doc-02`, ….
- `model-comparison-{ts}.csv` — every run metric and every compared field, ratio and score, **with
  values**. Numbers are InvariantCulture (`.` decimal, `,` separator, RFC 4180 quoting).
- `model-comparison-{ts}-mapa.csv` — `doc-NN` ↔ file name, SHA-256, size, pages.

- `model-comparison-{ts}-runs.json` — this run alone, to be the next commit's
  `FINANCIA_MODELCOMPARE_BASELINE_RUNS`.

The test output carries only counts, aliases, error types and the paths written, plus one line per run
(tokens, latency, escalation, trimming, integrity, `confidence 0.00` and `alerts [CODE, …]`) and one
per comparison (each key `balance_check` figure Identical / WithinTolerance / Different / NullFlip,
never a value). Alerts are listed by CODE only, never title or description; a code field holding
anything but an UPPER_SNAKE code prints as `<non-code>`. A snapshot written before codes were recorded
shows `alerts —`. These lines are safe to paste into a PR; the three files are not.
