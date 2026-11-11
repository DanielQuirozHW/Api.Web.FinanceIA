# 080 — The class of an investment on LegajoItems

## What changed and why

Until engine 0.8 every investment of a fiador was one inventory kind, `Inversion`, discounted with one haircut,
`pf_haircut_inversion` (30 %). A Treasury bond, a listed share and a mutual-fund unit do not carry the same
execution risk, so engine 0.9 gives each class its own haircut. The class is stored on the reconciled item:

| Column | Type | Meaning |
|---|---|---|
| `InvestmentClass` | NVARCHAR(30) NULL | on an `Inversion` item, its class; NULL when not classified |

| Class | Means | Haircut (default) |
|---|---|---|
| `TituloPublico` | bonos, letras del Tesoro, títulos provinciales o municipales | `pf_haircut_titulo_publico` (0,15) |
| `AccionCotizante` | acciones with an exchange, a ticker or a market price printed | `pf_haircut_accion_on` (0,30) |
| `ObligacionNegociable` | obligaciones negociables con cotización | `pf_haircut_accion_on` (0,30) |
| `CuotaparteFci` | cuotapartes de fondos comunes de inversión | `pf_haircut_fci` (0,30) |
| `Otra` | an investment the document does not let one classify | `pf_haircut_inversion` (0,30) |
| NULL | not classified | `pf_haircut_inversion` (0,30) |

`CK_LegajoItems_InvestmentClass`: `[InvestmentClass] IS NULL OR ([Kind] = N'Inversion' AND [InvestmentClass]
IN (…the five…))`, created WITH CHECK (every existing row is NULL). The code's list is
`LegajoInvestmentClasses.Known`; `LegajoInvestmentClassMappingTests` pins one to the other.

### Why not new kinds

`Kind` stays `Inversion`. The item key of an unkeyed row (no partida, dominio, CUIT or CBU) contains the kind,
so turning one investment into, say, `TituloPublico` would make the next rebuild delete the row and add a new
one, dropping its `AnalystNote`, `EditedFields` and confirmation. A class beside the kind changes nothing about
the item's identity.

### What NULL means

NULL means "not classified": every row written before this release, a document extracted before mb-1.3 /
bp-1.1, or a document that gives no hint. It takes `pf_haircut_inversion`, today's haircut, which keeps its key
and default — tenant overrides survive — and now describes "inversiones sin clasificar".

## What ships with it (no further DDL)

- **Extraction mb-1.3 / bp-1.1.** The manifestación's annex items and Bienes Personales' `TitulosYAcciones` rows
  gain a nullable `investmentClass`. The manifestación prompt now says that a share with an exchange, a ticker or
  a market price printed is `Inversion` (class `AccionCotizante`) even when the issuer's CUIT is printed; an
  unlisted share or a participación social stays `Participacion`. On the Bienes Personales side a
  `TitulosYAcciones` row with a CUIT reconciles as `Inversion` only for a concrete class (`TituloPublico`,
  `AccionCotizante`, `ObligacionNegociable`, `CuotaparteFci`); `Otra` or no class keeps the old rule (a CUIT makes
  it a `Participacion`, with no class): a doubt never moves an item to the lower investment haircut. Without a
  CUIT the row is an `Inversion`, `Otra` included. Documents already Ready are not re-extracted.
- **Reconciliation.** The class is the manifestación's, else Bienes Personales'; only on an `Inversion` item.
  An item that stops being an `Inversion` on a rebuild loses its class (and the analyst's edit of it). A keyed
  item (CUIT) that moves from `Participacion` to `Inversion` keeps its row, note, edits and confirmation: its key
  does not contain the kind. A class change marks the row updated but does not lapse a confirmation, whose
  fingerprint is the conflicting figures.
- **Analyst edit.** `PATCH /api/person-legajos/{id}/items/{itemId}` accepts `investmentClass` (one of the five,
  or `""` to clear it) on an `Inversion` item; it is kept across rebuilds like the other edited fields and
  audited by name only. Another value or another kind: 400 `validation_error` on `investmentClass`.
- **Engine 0.9.** The fiador coverage applies the class haircut, plus the currency-mismatch haircut on top as
  before. The frozen asset gains `investmentClass`; a 100 % class haircut keeps `descuento_total` with the class
  named in its text. The frozen parameters gain `haircutTituloPublico`, `haircutAccionOn` and `haircutFci`;
  evaluations frozen before 0.9 lack them and read, and render, as they were frozen.

## Audit and PII

No personal data: a class keyed by item row. The item-edit audit names the field (`investmentClass`) and the
reason, never the value.

## Deploy order

1. The DBA runs `080_legajo_items_investment_class.sql` in **every** Financia database (one per client,
   `.github/clients.json`). It is not for the Default database. Running it first is safe for the code already
   deployed: the column is nullable with no default, the old INSERT does not name it, and NULL satisfies the
   CHECK. Section 0 stops the script, altering nothing, if `[LegajoItems]` is missing or 079 has not run. Run the
   post-check: `--verify-schema` sees the column but not its type, nullability or CHECK.
2. Run the deploy workflow from DEVELOP with `ddl_aplicada` ticked and `client = all`. `--verify-schema`
   refuses a client whose database lacks the column.
3. The App Service restart is part of the publish (`restart: true`); `/health` is the post-gate.
4. The Portal follows: no existing enum gains a value, so an older Portal only ignores the new fields.

Publishing the API before the script breaks every read and write of `[LegajoItems]` — the inventory, its
rebuild, the item edit and every persona analysis that reads it — with `Invalid column name`.

## Rollback

Roll the API back to a release that does not map the column, then run the commented rollback at the end of the
script: export the classes (no personal data), clear them, drop the CHECK and the column. Rolling back only the
code, keeping the column, still needs the classes cleared first: the old code can move an item away from
`Inversion` on a rebuild without clearing its class, which the CHECK refuses. Analyses evaluated under 0.9 stay
readable by the old code, which ignores the new members.
