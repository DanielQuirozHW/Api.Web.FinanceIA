# Tenant profile (§7.0 — Layer 1 of the scoring redesign)

A tenant profile says **what kind of entity the tenant is** and **which optional integrations it
may use**. It lives in `[dbo].[TenantSettings]` as ordinary key/value rows — there is no
dedicated table and no DDL for it — and its defaults live in code, in
`Models/Financia/TenantProfilePresets.cs`. It is read and written through `ITenantProfileService`
(`Clases/`), never key by key. The read never throws and fails open; the write throws.

ARCA and BCRA are **base integrations**: always on, never parametrised, and deliberately absent
from this profile.

## Keys

All keys are constants in `Models/Financia/TenantSettingKeys.cs`. Use those, not the literals.

| Key | Values | Default when the row is absent |
|---|---|---|
| `entity.type` | `sgr`, `non_sgr` | `sgr` |
| `integration.lufe.enabled` | `true`, `false` | the preset for the tenant's entity type |
| `integration.casfog.enabled` | `true`, `false` | the preset for the tenant's entity type |
| `integration.nosis.enabled` | `true`, `false` | the preset for the tenant's entity type |

Booleans are stored as the literals `true` / `false`. The reader is lenient (trimmed,
case-insensitive), but a writer should store exactly those.

## Presets

| Preset | `entity.type` | LUFE | CASFOG | NOSIS |
|---|---|---|---|---|
| SGR (**default**) | `sgr` | on | on | on |
| non-SGR | `non_sgr` | on | **off** | **off** |

LUFE is the public company/partner registry and is useful to any lender. CASFOG is the SGR
guarantee-fund registry and has no meaning for a lender that is not an SGR. NOSIS is a general
credit bureau, billed per query, so the non-SGR preset leaves it off: the default that costs
nothing is the safe one to ship, and a tenant that wants it turns it on with one
`integration.nosis.enabled` row. (This settled the `TODO(profile)` that used to stand in
`TenantProfilePresets.cs`.)

## How a value is resolved

For each key, in order:

1. An explicit `TenantSettings` row for that key wins.
2. Otherwise the preset for the tenant's entity type supplies the value.
3. The entity type itself is the `entity.type` row, or `sgr` when there is none.

So an SGR tenant needs **no rows at all**, a non-SGR tenant needs **one row** (`entity.type`),
and any single integration can be flipped for any tenant with one more row. A preset change in
code reaches every tenant that has not overridden that key — the same semantics
`ScoringDefaults` already has for `ScoringConfig`.

A row that cannot be parsed (an unknown entity type, a non-boolean flag) resolves to the
default for that key and logs a Warning naming the tenant and the key. A failed database read
resolves to the SGR preset marked `ReadFailed`; consumers must **fail open** on that — a degraded
answer means "assume the preset", never "assume everything is off".

## Changing the profile through the API

`GET`/`PUT /api/bff/settings/tenant-profile` read and set the profile. The GET is open to
`Admin`, `RiskManager`, `Auditor` and `SuperAdmin` (`Roles.TenantConfigReadersWithOperator`);
the PUT is `SuperAdmin` only (`Roles.TenantConfigWriters`).

The write sits with the cross-tenant **operator** rather than the tenant's own `Admin` because the
entity type is OUR configuration, not the client's: it decides which external vendors are called
during enrichment, so moving it commits the tenant to per-query vendor billing — the same reason
`PUT /api/billing/config` is SuperAdmin-only. Reading stays broad: a RiskManager has to see which
integrations the tenant is entitled to, since that explains the enrichment an analysis carries, and
an Auditor has to be able to check it. `SuperAdmin` is in the read set because it is the role that
sets the profile and has to be able to read back what it set. `Analyst` is in neither.

```
GET /api/bff/settings/tenant-profile
{ "entityType": "non_sgr", "source": "Configured",
  "lufeEnabled": true, "casfogEnabled": false, "nosisEnabled": false }

PUT /api/bff/settings/tenant-profile
{ "entityType": "non_sgr" }
```

Three things worth knowing about the PUT:

- It writes the `entity.type` row **only**. Any `integration.*.enabled` override the tenant
  already carries survives, and every key it has not overridden follows the new preset.
- It **always writes a row**, including for `sgr`. A tenant that had none therefore moves from
  `source: "NoConfiguration"` to `"Configured"` with an identical effective profile — and, having
  been pinned, no longer follows a future change to the in-code default.
- It cannot express "back to the preset" for an integration key. That is still a `DELETE`, below.

Unlike the GET, the PUT does **not** fail open: a failed write answers 500 rather than reporting a
profile change that did not happen.

## Onboarding a tenant (DBA step)

Tenant creation is a DBA procedure (the `EncryptionHelper` console prints the two master-DB
`INSERT`s; the tenant business DB is created by hand and the numbered DDL scripts run on it).
The profile adds one step to that procedure, **on the tenant business database**:

- **SGR tenant** — nothing to insert. The code default is the SGR preset.
- **Non-SGR tenant** — one row (replace the slug):

```sql
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description])
VALUES (N'<tenant-slug>', N'entity.type', N'non_sgr',
        N'Entidad no-SGR: aplica el preset non_sgr (CASFOG deshabilitado).');
```

`EncryptionHelper` asks for the entity type and prints this statement when it is `non_sgr`.

## Overriding one integration for a tenant

`UQ_TenantSettings_Tenant_Key` allows one row per (tenant, key), so a second `INSERT` for the
same key fails: `INSERT` the first time, `UPDATE` afterwards.

```sql
-- Turn NOSIS off for one tenant (first time)
INSERT INTO [dbo].[TenantSettings] ([TenantId], [SettingKey], [SettingValue], [Description])
VALUES (N'<tenant-slug>', N'integration.nosis.enabled', N'false',
        N'NOSIS deshabilitado para este tenant.');

-- Change it later
UPDATE [dbo].[TenantSettings]
SET    [SettingValue] = N'true',
       [UpdatedAt]    = SYSUTCDATETIME()
WHERE  [TenantId]     = N'<tenant-slug>'
  AND  [SettingKey]   = N'integration.nosis.enabled';

-- Or delete the row to go back to the preset value
DELETE FROM [dbo].[TenantSettings]
WHERE  [TenantId]   = N'<tenant-slug>'
  AND  [SettingKey] = N'integration.nosis.enabled';
```

## Entitlement, not availability

A profile flag says the tenant **may** use the vendor. Whether the installation **can** reach it
is still decided by the installation's own configuration: `Nosis:Enabled` plus its key and VR,
the CASFOG credentials, and `Tenants:{tenantId}:LufeApiKey`.

§7.0b wires the profile into `EntitiesController`, **in front of** those availability checks —
entitlement first (may the tenant use it at all), availability second (can this installation
reach it). A disabled profile short-circuits before any config lookup or vendor call:

- **Entity creation** (`POST /api/entities`) and the **ARCA lookup** (`POST /api/entities/lookup-arca`)
  skip a disabled LUFE / CASFOG step silently: no vendor call, an Information log naming tenant
  and integration, no error. For a `non_sgr` tenant this is the first observable change — its
  entities are created without CASFOG data.
- **`refresh-lufe`, `refresh-casfog`, `refresh-nosis`, `lufe-document` and `lufe-document/import`**
  answer **400** with `"<VENDOR> no está habilitado para este tenant."` — the same status the
  missing-LUFE-key and unconfigured-NOSIS cases already use, so the Portal keeps treating it as
  terminal rather than retryable. The detail text is distinct from those two, so a support
  ticket can tell "not entitled" from "not configured". Checked after the 404, so a bad id is
  still reported as a bad id.

The profile is resolved **once per request** and every gate in that request reads the same
answer. It **fails open**: a degraded resolution (`ReadFailed`) or an unexpected exception from
the resolver treats LUFE, CASFOG and NOSIS as entitled and lets the availability check alone
decide. ARCA and BCRA are never gated.

Both halves live in one place: `IIntegrationAccessService` (`Clases/IntegrationAccessService.cs`).
`ResolveEntitlementsAsync` is the fail-open profile read (a thrown resolver logs a Warning with the
TenantId; a degraded one logs Information, because `TenantProfileService` already warned about the
failed read); `GetLufeApiKey`, `IsCasfogConfigured` and `IsNosisConfigured` are the availability
checks, reading the same setting names `CasfogService` and `NosisService` read. `EntitiesController`,
`FiadoresController` and the operations dashboard all go through it, so they cannot disagree.

**Operations dashboard** (`GET /api/dashboard/operations`): an optional integration is listed in
`integrations.services` and `freshness.sources` only when it is entitled AND available
(`ResolveUsableAsync`). Otherwise "never queried" would equal every PyME for a source the tenant
cannot use — NOSIS while it is unconfigured, CASFOG and NOSIS for a `non_sgr` tenant — and the
Portal's "Datos de PyMEs" tile would report that source as the worst forever. A hidden source is
simply absent from the arrays; no field is added or renamed. The same fail-open rule applies: a
degraded or unreadable profile lists everything the installation can reach. ARCA, BCRA and Claude
are always listed.

Scoring does not consult the profile: the first real score
signal (§7.1, the rejected-cheques penalty inside the BCRA component) is switched per tenant through
the `ScoringConfig` key `bcra_cheques_enabled` — the same mechanism as every other scoring parameter,
with `bcra_cheques_penalizacion_plena` beside it — not through this profile, because there is no
per-signal profile flag and BCRA is deliberately un-keyed here. Generalising the engine's
renormalisation to the profile remains future work.

§7.3a (the LUFE sales-drop penalty on the final score, algorithm version 6) is gated the same way,
and the reasoning is worth stating because LUFE — unlike BCRA — *is* keyed in this profile, so
reaching for `LufeEnabled` here would look natural. It is still the wrong gate. The profile decides
whether `ILufeService` is **called**; it says nothing about consuming a snapshot already persisted on
the entity, which is all scoring does. And a tenant with `integration.lufe.enabled = false` never
persists a `VentasComprasMensual` series in the first place, so the signal is already dormant for
them by construction — no series, no basis, no penalty. What the profile cannot express is a
LUFE-*enabled* tenant that wants this one signal off, and that is exactly what the ScoringConfig key
`lufe_ventas_enabled` is for (with `lufe_ventas_penalizacion_plena`, `lufe_ventas_umbral_inicio` and
`lufe_ventas_umbral_plena` beside it). Adding a profile read to `ScoringService` would also cost it
the purity its determinism rests on: it takes no dependency today beyond `ScoringConfig`, and a
score must stay a pure function of the analysis payload and the tenant's scoring parameters.

§7.4 (the CASFOG guarantee-arrears penalty on the final score, algorithm version 8) is gated the same
way, and here the temptation to reach for the profile is stronger still: CASFOG is one of the two
flags the non-SGR preset switches off (NOSIS is the other), so `CasfogEnabled` looks like the
natural gate. It is still the wrong one, for the same two
reasons. The profile decides whether `ICasfogService` is **called**; scoring never calls it, and in
fact `AnalysisController` does not inject it at all. It only consumes the snapshot already persisted
on the entity. And a tenant with `integration.casfog.enabled = false` never persists a CASFOG blob in
the first place — entity creation skips the step silently and `refresh-casfog` answers 400 — so the
signal is already dormant for them by construction: no blob, no per-SGR rows, no penalty. What the
profile cannot express is a CASFOG-*enabled* tenant that wants this one signal off, and that is what
the ScoringConfig key `casfog_mora_enabled` is for (with `casfog_mora_penalizacion_plena`,
`casfog_mora_umbral_inicio` and `casfog_mora_umbral_plena` beside it).

Note the ordering that makes this safe to ship to every tenant at once: an SGR tenant that has never
refreshed CASFOG has a null `Entities.CasfogDataJson`, and a null snapshot is a zero penalty, not a
missing one. No analysis changes score until real CASFOG rows exist on the entity.
