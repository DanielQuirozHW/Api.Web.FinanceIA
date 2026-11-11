# Legajo extraction (M10 Phase 2b and 3a)

The background worker extracts the figures of a legajo document once it is `ReadyForExtraction`
(classified cleanly, or confirmed by an analyst). Seven types have an extractor:

- Phase 2b: Bienes Personales and Manifestación de bienes.
- Phase 3a: Ganancias, DDJJ IVA, DDJJ Ingresos Brutos, certificación de ingresos and monotributo.

**No DDL**: DDL 070 already created `LegajoExtractions` (Kind `Extraction`) and `LegajoCorrections`, and
admits every type.

> The model extracts, the code computes and checks, the analyst decides.

## Flow

```
ReadyForExtraction ──claim (type has an extractor)──▶ Extracting
    │                                                   │ consent missing / revoked ──▶ ReadyForExtraction
    │ (REIBP, flujo proyectado … wait here)             │ pass 1 (Document or Images) → checks
    │                                                   │ pass 2 as Images, ONCE, when the output was unusable
    │                                                   │   or a check failed on a Document read
    │                                                   ▼
    │                                   Ready | NeedsReview | Blocked | Failed (extraction_… code)
    │                                            │
    │            corrections (append-only) ◀─────┤  POST …/extraction/corrections (Ready → NeedsReview)
    │                                            └─ POST …/extraction/review  (NeedsReview → Ready)
```

- **Read mode**: Document when every page is Good, the file is under 5 MB and it has at most 40 pages;
  Images otherwise (`Legajo:ExtractionImageDpi`, each page rendered directly at a 1568 px long edge). More than
  40 pages, or a payload over 30 MB, fails without a call.
- **Kept pass**: fewer failed checks, then fewer missing required fields, then higher mean confidence.
  Both runs are stored; the kept one is `IsCurrent`.
- **Outcome**: Blocked when the holder CUIT read is another person's; Ready when no required field is
  missing, no check failed and nothing is flagged; NeedsReview otherwise. Flagged = confidence below
  `Legajo:FieldConfidenceThreshold` (on a field, or on a row) or part of a failed check.
- **Required fields v1**. A null `amountsUnit` is never assumed to be pesos by the code: it lands in
  `missing` (→ NeedsReview). A requirement that a path list cannot express is a schema requirement
  (`LegajoRequirement`), reported missing under its own path.

  | Type | Required fields | Requirement (missing under) |
  |---|---|---|
  | BP | `fiscalYear`, `amountsUnit` | at least one item or rubro total (`/items`); `totalBienes`, or both `bienesPais` and `bienesExterior` printed — a printed 0 counts (`/totals/totalBienes`) |
  | MB | `measurementDate`, `amountsUnit`, `totalActivo`, `totalPasivo`, `patrimonioNeto` | — |
  | Ganancias | `fiscalYear`, `amountsUnit` | `gananciaNeta` or a category result (`/totals/gananciaNeta`) |
  | DDJJ IVA | `amountsUnit` | a period row with its period and its sales or débito (`/periods`) |
  | DDJJ IIBB | `amountsUnit` | a row with its base or tax, or a DDJJ's ingresos totales (`/rows`) |
  | Certificación | `periodFrom`, `periodTo`, `amountsUnit` | `totalIngresos` or a certified amount (`/totals/totalIngresos`) |
  | Monotributo | `amountsUnit` | the categoría or a payment (`/header/categoria`) |
- **Runs are stored before anything that reads their content can throw.** A pass whose assessment
  throws is still stored (not current), then the document fails as `extraction_unexpected`.
- **Reprocess** of a Failed document returns it to the stage that failed: an `extraction_…` code goes
  back to ReadyForExtraction, never to classification, so a confirmed classification is never re-run.

## Schemas and prompts

Versioned in `Servicios/Legajo/LegajoExtractionPrompts.cs` and one partial file per Phase 3a type. All use
schema `1.0`; each prompt is pinned by a fingerprint in `LegajoExtractionPromptTests` (`mb-1.0` stays
registered for reading old runs).

| Type | PromptVersion | Tool | Unstated unit | max_tokens |
|---|---|---|---|---|
| BienesPersonales | `bp-1.0` | `record_bienes_personales` | Pesos | 16000 (option) |
| ManifestacionBienes | `mb-1.1` | `record_manifestacion_bienes` | null → review | 16000 (option) |
| Ganancias | `gan-1.0` | `record_ganancias` | Pesos | 16000 (option) |
| DdjjIva | `iva-1.0` | `record_ddjj_iva` | Pesos | **24000** (schema) |
| DdjjIibb | `iibb-1.0` | `record_ddjj_iibb` | Pesos | **24000** (schema) |
| CertificacionIngresos | `ci-1.0` | `record_certificacion_ingresos` | null → review | 16000 (option) |
| Monotributo | `mono-1.0` | `record_monotributo` | Pesos | 16000 (option) |

The AFIP forms are always in pesos, so an unstated unit is Pesos. A manifestación or a certificación must
print its unit, so an unstated one is null and the document goes to review. One schema tree generates the
tool's input_schema, validates the model's answer (`LegajoToolInputReader`) and types the analyst's corrections
(`LegajoCorrectionValidator`). Neither has type-specific code.

- **`Period`** (Phase 3a) is a month, `YYYY-MM` (1900–2100), for DDJJ periods, certified months and
  payments. The model's `03/2025` is `2025-03`; anything else is `expected_period` (→ `invalid_output`).
  A correction with a bad period is a 400 ("debe ser un período AAAA-MM o null").
- **`MaxTokens`** on a schema overrides `Legajo:ExtractionMaxTokens` for that type. A 12-month IVA or IIBB
  PDF has a few hundred rows, each with its row provenance.
- The Phase 3a prompts drop the BP/MB `matchKey` rule (they list no assets) and add the period rule. The
  bp-1.0 and mb-1.1 texts are byte-identical to before.

- Header and totals fields are `{value, page, quote, confidence}`; array rows carry that provenance once
  per printed row (per-field provenance on 10-field rows would not fit in 16k output tokens).
- Amounts are **as printed**; `amountsUnit` (Pesos / Miles / Millones) says what the document uses and
  C# scales to pesos for the checks. BP values are **fiscal**, never market values.
- Money and percent values are bounded at |value| ≤ 1e15 in the printed unit: beyond it the model's
  answer is `invalid_output` (pass 2, else Failed) and a correction is a 400. Text values have a
  maximum length: the model's text is truncated to it, an analyst's correction over it is a 400.
- **Strict tool use is off on purpose**: the schemas carry dozens of nullable union-typed leaves and a
  grammar the API rejected would fail every extraction; the tool call is still forced and C# validates
  the answer in full. The schemas stay strict-compatible (a test enforces it), so switching strict on
  is a one-line change once a live run proves the grammar compiles.

## Integrity checks (`Servicios/LegajoIntegrityService*.cs`)

The arithmetic is decimal, in pesos; percentages (`ownershipPct`, `alicuotaPct`) are never scaled.

- **Tolerance.** A comparison passes within 0.5 in a hundred of the larger figure, or within the rounding
  allowance (half a printed unit per summed operand, at least one unit).
- **Skips.** A check whose operand is missing is listed under `skipped`.
- **Messages** are es-AR, with no percent sign.
- **Month checks** (`*_period`) report expected and actual as YYYYMM and the difference in months.
- **Not checked:** tax-rule nuances (quebrantos, topes, prorrateos, exenciones, impuestos mínimos). A
  check that fires on a correct document costs the analyst time.

**What a failure flags.** A check that compares ONE printed total (or result) with its parts carries
`flaggedPaths`: only those paths go to the `flagged` queue as `check_failed`.
- `fieldPaths` still lists every operand, so opening the check highlights all of them.
- Peer comparisons, range rules and sign rules carry no `flaggedPaths` and flag every field path.
- `flaggedPaths` is additive and omitted when null; a run stored before it existed reads as before.
- Before this, the review of 2026-10-01 listed the país total AND all five rubro subtotals.

| Type | Check | Rule | Flags |
|---|---|---|---|
| BP | `bp_rubro_items` | items of each (rubro, location) = that rubro total | the rubro total |
| BP | `bp_rubro_totals_pais` / `_exterior` | rubro totals per location = bienesPais / bienesExterior | bienesPais / bienesExterior |
| BP | `bp_total_bienes` | bienesPais + bienesExterior = totalBienes | totalBienes |
| BP | `bp_ownership_pct` | every ownershipPct in (0, 100] | the offending values |
| BP | `bp_non_negative` | no negative value | the offending values |
| BP | `bp_fiscal_year_period` | fiscalYear = year of Documents.PeriodEnd | fiscalYear |
| MB | `mb_section_lines` | lines of each section = the section total | the section total |
| MB | `mb_total_activo` / `mb_total_pasivo` | corriente + no corriente = total | the total |
| MB | `mb_patrimonio_neto` | totalActivo − totalPasivo = patrimonioNeto | patrimonioNeto |
| MB | `mb_annex_items` | annex items of each annexRef = the lines that reference it | the referencing lines |
| MB | `mb_income_result` | ingresos − gastos = resultado (when present) | resultado |
| MB | `mb_ownership_pct` | every ownershipPct in (0, 100] | the offending values |
| Ganancias | `gan_justification_origenes` / `_aplicaciones` | the lines of a side of the justificación = that side's total | the side's total |
| Ganancias | `gan_patrimonio_variacion` | patrimonioCierre − patrimonioInicio = variacionPatrimonial (when printed) | variacionPatrimonial |
| Ganancias | `gan_fiscal_year_period` | fiscalYear = year of Documents.PeriodEnd | fiscalYear |
| IVA | `iva_alicuota_line` | each **débito** line with an alícuota: impuesto = neto × alícuota | impuesto, neto, alícuota |
| IVA | `iva_debito_lines` | a DDJJ's débito lines (same period + rectificativa) = its débito fiscal | debitoFiscal |
| IVA | `iva_saldo_tecnico` | débito − crédito − saldo técnico anterior = the saldo técnico (Fisco +, Contribuyente −) | saldoTecnico amount and side |
| IVA | `iva_period` | latest period = month of Documents.PeriodEnd | that period |
| IIBB | `iibb_row_tax` | each row: impuesto = base × alícuota | impuesto, base, alícuota |
| IIBB | `iibb_total_tax` | a DDJJ's rows (same period + rectificativa) = its impuesto total | impuestoTotal |
| IIBB | `iibb_period` | latest period = month of PeriodEnd; an annual DDJJ: fiscalYear = its year | that path |
| Certificación | `ci_rows_total` | the certified amounts = totalIngresos | totalIngresos |
| Certificación | `ci_period` | periodTo's month = PeriodEnd's month | periodTo |
| Monotributo | `mono_non_negative` | no negative cuota or payment | the offending values |
| Monotributo | `mono_period` | latest payment's month = PeriodEnd's month | that payment's period |
| all | `arithmetic_error` | added (failed) when scaling or summing overflows decimal — never a 500 | — |

**Deliberately not checked:**
- **The crédito fiscal lines.** Prorrateo and crédito no computable legitimately break neto × alícuota.
- **The category sum of Ganancias.** Cedular income, fuente extranjera and quebrantos break it.
- **A balance between the two sides of the Ganancias justificación.** On the AFIP form the columns are not
  meant to be equal: the monto consumido is computed from the ganancia, both columns and the variación.

## Review API (`Controllers/LegajoExtractionController.cs`)

`GET /api/documents/{id}/extraction` · `POST …/extraction/corrections` · `POST …/extraction/review`.
Errors are ProblemDetails with an `errorCode` extension:

| Status | `errorCode` | When |
|---|---|---|
| 400 | `validation_error` | bad path or type; a money value beyond 1e15 (`out_of_range`); a text over its max length (`too_long`); a reason under 5 or over 1000 characters; an override without a reason |
| 404 | `extraction_unavailable` | the document is Blocked (its extraction read another person's paperwork) — on GET, corrections and review alike; the legajo list shows `currentExtraction: null` for it |
| 409 | `extraction_changed` | review: a correction (or a new run) landed after the view the analyst approved — reload and review again |
| 409 | — | correction outside NeedsReview/Ready; review outside NeedsReview |

- **A correction and an approval never cross.** A correction inserts its row and, in the same
  transaction, always runs the conditional Ready → NeedsReview with the review stamp cleared. An
  approval is optimistic: it succeeds only while the document is still NeedsReview, the run is still
  current and the run's correction count equals the count the analyst evaluated. Either order ends in
  NeedsReview with the correction applied.
- **`bienesPersonalesSummary`** (GET view, Bienes Personales only; `null` for every other type). It is
  computed at read time from the EFFECTIVE values and never stored, in the document's `amountsUnit`, not
  scaled to pesos (`Servicios/Legajo/LegajoBienesPersonalesSummary.cs`). There is one group per rubro that
  has items, in `RubroDisplayOrder`. Each group carries its item pointers, its fiscal-value sums (total,
  país, exterior; an item with no fiscal value is left out and counted in `itemsWithoutFiscalValue`) and
  the matching printed rubro subtotals. `totals.bienesPais|bienesExterior|totalBienes` are
  `{fieldPath, printed, computed, source}`, where `source` is `printed` | `computed` | `none`.
  `computed.totalBienes` is printed país + printed exterior when both are printed, otherwise the sum of
  every item. A rubro printed only as a subtotal (no item rows) adds nothing to `computed`.
  `baseImponible`, `bienesExentos`, `minimoNoImponible` and `impuestoDeterminado` are printed only. A
  decimal overflow yields `null` and never fails the GET. A computed figure never feeds a check.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Anthropic:LegajoExtractionModel` | `claude-sonnet-4-6` | Must accept a forced `tool_choice` and `temperature 0`. Guarded (`LegajoModelSupport`): only `claude-sonnet-4-6` and `claude-haiku-4-5` are accepted; anything else (e.g. `claude-sonnet-5-5`, `claude-sonnet-5`) falls back to the default with an Error logged once. `Anthropic:LegajoClassificationModel` is guarded the same way. |
| `Legajo:ExtractionMaxTokens` | 16000 | A schema's own `MaxTokens` overrides it: DDJJ IVA and IIBB use 24000. |
| `Legajo:ExtractionImageDpi` | 200 | A page larger than a 1568 px long edge at that DPI is rendered directly at 1568 (never at full size first); absurd page sizes are clamped. |
| `Legajo:FieldConfidenceThreshold` | 0.7 | |
| `Legajo:ExtractionTimeoutSeconds` | 600 | On the `ClaudeLegajoExtractionClient` HttpClient. A timeout is **not retried** (5xx and network errors are) and ends the job as `extraction_timeout`, with its ApiCallLogs row and no pass 2. Streaming the response is the proper fix for long calls, later. |
| `Legajo:StaleMinutes` | 10 | With more than one App Service instance keep it ≥ 25 (two passes of up to 600 s each; a timeout is not retried). |

The attempt budget (`Billing:MaxExtractionAttemptsPerDocument`) is shared with classification and
counts per FILE; a failed extraction job counts once when it sent a billable request (a 429 does not).

## Personal data

Consent is re-checked before every model request and before completing. The request never carries the
legajo's name or CUIT. `ResultJson`, the corrections and the GET view are personal data; the view never
carries the holder CUIT (only `holderCuitMatches`), and nothing logs a value, a quote, a reason or a
name. Correction audits name the field and carry the reason, never the values.

## Live harness (paid; never in CI)

`Api.Web.FinanceIA.Tests/Servicios/LegajoExtractionHarnessTests.cs` runs the real extraction on one
local PDF and prints the passes, checks and queues. It is skipped unless `FINANCIA_LEGAJO_PDF` is set.

```bash
export FINANCIA_LEGAJO_PDF=/path/outside/the/repo/document.pdf   # NEVER commit this file
export FINANCIA_LEGAJO_TYPE=BienesPersonales                      # or ManifestacionBienes, Ganancias,
                                                                  # DdjjIva, DdjjIibb, CertificacionIngresos,
                                                                  # Monotributo
export FINANCIA_LEGAJO_CUIT=20-12345678-6                         # optional: compare the holder
export FINANCIA_LEGAJO_PERIOD_END=2025-12-31                      # optional: fiscal-year / period checks
export ANTHROPIC_API_KEY=...                                      # or Anthropic__ApiKey

dotnet test Api.Web.FinanceIA.Tests/Api.Web.FinanceIA.Tests.csproj -c Release \
  --filter "FullyQualifiedName~LegajoExtractionHarnessTests" --logger "console;verbosity=detailed"
```

It prints the type, prompt version and max_tokens, each pass, the checks (and what a failed check flags),
the missing and flagged queues and the estimated cost.

The printout quotes figures from the document: do not paste it into an issue, a PR or a chat.
