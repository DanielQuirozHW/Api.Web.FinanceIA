# 084 — Strip quotes that wrap a stored Entities.BusinessName (data fix)

## Why

A PyME was created as `"silo Chico S.A."` — the quotes stored as part of the name. The name comes
from the Portal (`CreateEntityRequest.BusinessName`, autofilled from ARCA's `razonSocial`), and the
API only trimmed it, so every list, PDF and notification then repeated the quotes.

The application now normalizes the name on `POST /api/entities` and `PUT /api/entities/{id}`, and
normalizes the `razonSocial` the ARCA lookups return for autofill (`BusinessNameNormalizer`:
control characters out, whitespace collapsed, quotes that WRAP the whole name stripped, inner
quotes kept, case untouched). 084 brings rows written before that in line.

## What it does

- **DATA FIX (DML), no schema change.** Optional; safe before or after the deploy; no restart.
- Run once in **each Financia DB**; covers every TenantId in it.
- `@Apply = 0` (default) only **previews**: result set 1 = rows it would fix (TenantId, EntityId,
  BusinessName, ProposedName, layers peeled); result set 2 = rows it will NOT touch and that need a
  person (inner same-family quotes, or a name that would end up empty).
- `@Apply = 1` writes result set 1 in **one transaction** (XACT_ABORT ON). The UPDATE matches the
  previewed `BusinessName`, so a name edited meanwhile is skipped. Idempotent.

A row is fixed when its trimmed name starts and ends with quotes of the same family —
double `" “ ” „ ‟ « »`, single `' ‘ ’ ‚ ‛ ‹ ›` — and nothing of that family is left inside; layers are
peeled one at a time. Comparison is by code point (BIN2 collation), so the result never depends on
the database collation. The application's pairing rule, which can also unwrap
`"Sociedad "La Esperanza" S.A."`, is not reimplemented in T-SQL: those rows are listed for review.
Editing such a PyME in the Portal (a PUT) normalizes it with the full rule.

## What it does not touch

- `UpdatedAt` (list ordering does not jump), letter case, whitespace inside the name, any other column.
- Vendor payloads (`FiscalDataJson`, `LufeDataJson`, …) — stored as received, by design.
- Copies of the name:
  - `PersonLegajos.FullName` — copied from `BusinessName` when a persona humana's legajo is created.
    Persona humana names are composed "APELLIDO, NOMBRE", so a wrapped one is unlikely; check by hand
    if a fixed row is a persona humana.
  - `Notifications.Title` / `Body` — historical text, deliberately not rewritten.
  - `Fiadores.Denominacion` — a separate name (the fiador's); its ARCA prefill is now normalized,
    stored rows are untouched.
  - `Analyses`, `Users`, `AuditLogs` hold no copy (Entity.Created/Updated audit rows carry no name).

It bypasses the application audit trail: record the run (database, date, who, rows) in
`docs/database/README.md`.
