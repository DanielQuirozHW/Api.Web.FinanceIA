# 071 — A legajo's reconciled inventory and its incomes by period

## What changed and why

M10 Phase 3b joins a persona humana's approved documents into two read models that the Portal's legajo
tab renders. The design's "Modelo de datos" names these tables; its "Conciliación" and "Reglas de
precedencia" sections set the rules.

- **Inventory.** Each asset or debt appears once, with the market value from the manifestación de
  bienes and the fiscal value from Bienes Personales side by side. Each carries the document it came
  from and a reconciliation status.
- **Incomes.** Sales and income are broken down by period and source: DDJJ IVA, IIBB, Ganancias,
  certificación, manifestación and monotributo.

Code builds both from the documents. There is **no model call**. The rows are derived data and could
be thrown away and rebuilt, **except** for the analyst's edits to an inventory item. Those edits are
the reason the inventory is stored rather than computed on every read.

| Change | Where |
|---|---|
| The reconciled inventory | new `[dbo].[LegajoItems]` |
| Incomes and sales by period and source | new `[dbo].[LegajoIncomes]` |
| Whether an approval was an override, and its reason | `[dbo].[LegajoExtractions]`: `ReviewOverride`, `ReviewReason` |

**No calculation is stored.** That means no totals, no patrimonio neto, no coherence ratio and no
indicators. `GET .../inventory` and `GET .../incomes` compute them at read time, and the person
analysis will freeze them in its own snapshot. Amounts are in **pesos**: the printed figure times the
document's `amountsUnit`. They are never multiplied by the ownership percentage, because both documents
already state the person's share.

## Deploy ordering

**RUN BEFORE DEPLOYING**, once on Financia-db, then restart the App Service. Run it on any other
business database the DDL procedure lists, as with 069 and 070. EF maps both tables and both new
`[LegajoExtractions]` columns. Publishing first therefore breaks every read of `[LegajoExtractions]`
with `Invalid column name`, which takes down the legajo documents list, the extraction review and the
extraction worker. The deploy job's `--verify-schema` step refuses that publish.

Running the DDL first is safe for the code still running:

- the two new tables are never read by it;
- the two new columns are nullable and have no default.

No Portal-first step is needed. The Portal gains four endpoints and additive fields
(`figureKind`, `subjectParts`, `reviewedWithOverride`, `reviewReason`, and an `errorCode` on one
existing 400). No enum value is added.

Nothing is backfilled. A legajo's rows are built by any of these:

- its first approval;
- a correction that reopens a Ready document;
- a completed extraction;
- a delete;
- an explicit `POST /api/person-legajos/{id}/reconciliation/rebuild`.

Until one of those happens, `GET .../inventory` and `GET .../incomes` answer an empty legajo.

### Execution history

- Pending.

## `[dbo].[LegajoItems]`

One row per asset or debt, identified across rebuilds by `ItemKey`. `UX_LegajoItems_Key` is
`(TenantId, PersonLegajoId, ItemKey)`, and its name is load-bearing: a rebuild that loses a race to
another one fails on it and retries once.

- **What feeds it.** Every live document of the legajo in `Ready` with a succeeded current extraction
  run, read on its *effective* values (corrections applied). That includes a clean run that went
  straight to Ready. NeedsReview, Blocked, Failed and deleted documents contribute nothing.
- **The snapshot.** It uses the latest Bienes Personales (fiscal year, then rectificativa number, then
  upload) and the latest manifestación (measurement date, then upload).
- **Precedence.**
  - Existence and ownership come from Bienes Personales (`OwnershipPct`, `Description`).
  - Value comes from the manifestación (`MarketValue`), with Bienes Personales' `FiscalValue` kept
    beside it as the floor.
  - Debts come from the manifestación. That includes a statement line whose annex was not extracted,
    so debts listed only as lines are not lost.
- **Matching.** Two rows pair only on an *exact normalized* `MatchKey` of the same kind:
  - Partida and Dominio are compared on upper-case letters and digits;
  - Cuit is compared on its 11 digits, Cbu on its digits;
  - `Otro` and `Ninguno` never pair;
  - a key that appears twice in one document pairs nothing.

  The design's "the model proposes doubtful pairings" is out of scope, so everything else stays
  unpaired with its status.
- **`ReconciliationStatus`.**
  - `Matched`.
  - `Conflict`, with `ConflictReasons` (CSV): `OwnershipDiffers`, `BelowFiscalFloor` (market <
    fiscal), `CurrencyDiffers`.
  - `OnlyInBienesPersonales`.
  - `OnlyInManifestacion`. Every debt is in this state, because Bienes Personales lists none.
  - `AnalystConfirmed`.
- **The analyst's edits survive a rebuild.** `PATCH .../items/{itemId}` sets `IsHousing`,
  `IsActivityAsset`, `OwnershipPct`, `AnalystNote` or confirms a conflict, and requires a reason
  (audited, never the values).
  - The edited fields are listed in `EditedFields`, and a rebuild never overwrites them.
  - A rebuild never touches `AnalystNote`.
  - A confirmation records `ConfirmedFingerprint`, a SHA-256 of market value, fiscal value, currency
    and conflict reasons. It stands only while a rebuild still finds a conflict with the same
    fingerprint; otherwise it lapses back to the computed status, and the rebuild counts it.
  - A row whose key no rebuild computes any more is deleted. Its edits remain in `[dbo].[AuditLogs]`.
- **`UpdatedAt` is the row's concurrency token.** A PATCH racing a rebuild answers 409 `item_changed`.
  `UpdatedBy` is NULL when a rebuild wrote the row last.
- **CHECKs**: `Side`, `Kind` (the manifestación's annex kinds), `MatchKeyKind` (the schemas'
  match-key kinds), `ReconciliationStatus`, and `OwnershipPct` 0–100. Each list is pinned by
  `LegajoReconciliationMappingTests` to a constants class.

## `[dbo].[LegajoIncomes]`

One row per source, concept and date range (`UX_LegajoIncomes_Key`). A rebuild updates a row in place
by that key, inserts new ones and deletes the rest. The table carries no analyst state.

| Source | Concept(s) | Read from |
|---|---|---|
| `Iva` | `Ventas` | per period: neto gravado + exentas y no gravadas |
| `Iibb` | `Ventas` | per DDJJ: ingresos totales, else Σ base imponible; an annual DDJJ is a year |
| `Certificacion` | `IngresoCertificado` | per month; without monthly rows, the total over the certified range |
| `Ganancias` | `Renta3ra`, `Renta4ta`, `Otro`, `MontoConsumido` | per fiscal year: the non-cedular 3ª and 4ª results, everything else, the monto consumido |
| `Manifestacion` | `Ventas`, `Otro` | its estado de resultados: ingresos and resultado |
| `Monotributo` | `Otro` | the payments per month (a cuota, not an income) |
| `Facturacion` | — | admitted by the design; no extractor yet |

- **Rectificativas.** When the same source and range appear more than once, the highest rectificativa
  wins, then the newest upload. This holds inside one document (an IVA file with an original and its
  rectificativa) and across documents. A later rectificativa therefore replaces the older document's
  rows.
- **`Period`** is `YYYY-MM` for a month and `YYYY` for a calendar year. It is NULL for any other range,
  for example a certificación of July–June, which keeps its exact dates in `PeriodFrom`/`PeriodTo`.
  `CK_LegajoIncomes_Period` pins the two formats and `CK_LegajoIncomes_PeriodRange` pins
  `PeriodFrom <= PeriodTo`.
- **Coherence.** `GET .../incomes` computes it per period, at read time, over the sales-like figures
  (IVA, IIBB, manifestación and facturación `Ventas`, and the certified `IngresoCertificado`):
  `maxRelativeDiff = (max − min) / max`. Each period also reports the prevailing sources: sales from
  Iva, then Facturacion, Iibb, Certificacion, Manifestacion; result and consumption from Ganancias,
  then Manifestacion.

## `[dbo].[LegajoExtractions]`: `ReviewOverride`, `ReviewReason`

An approval with failed checks or missing required fields is an *override*. Until now only the audit
row knew it. The approval now stamps both columns together with `ReviewedBy`/`ReviewedAt`, and a
correction that reopens the document clears all four. The extraction view and summary return them as
`reviewedWithOverride` and `reviewReason`. They are null while the run is not approved, and null for
a run approved before this script.

## FKs

All six FKs are `NO ACTION`:

- `FK_LegajoItems_PersonLegajos`, `FK_LegajoItems_MarketValueDocument`,
  `FK_LegajoItems_FiscalValueDocument`;
- `FK_LegajoIncomes_PersonLegajos`, `FK_LegajoIncomes_Documents`, `FK_LegajoIncomes_LegajoExtractions`.

The EF model maps none of them, as in 070. Documents are soft-deleted, so the FKs never block the
application. A hard delete of a document or a run by hand must first delete the rows that reference
it, or soft-delete the document and rebuild, which removes them.

## ⚠ PII

Both tables hold a natural person's patrimony and income: partidas, dominios, CBUs, the CUIT of the
companies they hold shares in, and free-text analyst notes. The API returns them only to the
tenant's users, and logs only ids and counts. The audit row of an edit names the fields and carries
the reason, never the values or the note.

## Verification

The script's post-checks 1–4 cover:

- the nine CHECKs, enabled and trusted;
- the six FKs;
- the four indexes;
- the two new nullable `[LegajoExtractions]` columns, NULL on every existing run.

Then:

```bash
dotnet run --project Api.Web.FinanceIA -- --verify-schema
```

`--verify-schema` expects both tables and both columns. After the deploy, approve one legajo document,
or `POST /api/person-legajos/{id}/reconciliation/rebuild`, and check that
`GET /api/person-legajos/{id}/inventory` and `/incomes` answer rows.

## Rollback

See the script's rollback block. Roll the application back first. Export what the analysts wrote
(`EditedFields`, `AnalystNote`, `ConfirmedFingerprint` and the values they protect, and the approval
overrides) **as personal data**. Then drop `[LegajoIncomes]`, then `[LegajoItems]`, then the two
columns. Every other row can be rebuilt from the documents.
