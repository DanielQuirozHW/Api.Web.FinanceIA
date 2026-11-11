# financia-integrations.md — external vendors and the scoring engine

Every claim here was read out of the working tree. Where a number could drift, this file points at
the source instead of restating it.

## The six external integrations

All live in `Api.Web.FinanceIA/Servicios/` and are registered in `Program.cs`.

| Vendor | Service | Registration | Persisted to |
|---|---|---|---|
| **BCRA** (central bank: debt, rejected cheques, macro, FX) | `BcraService` | `Program.cs:91` scoped | `Entities.BcraDataJson`, `AnalysisResults.BcraDataJson` |
| **LUFE** (company/partner registry) | `LufeService` | `Program.cs:92` scoped | `Entities.LufeDataJson` |
| **CASFOG** (guarantee-fund standing) | `CasfogService` | `Program.cs:96` scoped | `Entities.CasfogDataJson`, `AnalysisResults.CasfogDataJson` |
| **NOSIS** (credit bureau) | `NosisService` | `Program.cs:94` scoped | `Entities.NosisDataJson`, `Fiadores.NosisDataJson` |
| **ARCA/AFIP** (tax authority — three SOAP services) | `ArcaWsaaService` (auth), `ArcaPadronA13Service`, `ArcaConstanciaService` | `Program.cs:222,233,244` typed HttpClients | `Entities.FiscalDataJson` |
| **Anthropic Claude** | `ClaudeService` | `Program.cs:90` scoped | narrative/extraction fields on `AnalysisResults` |

Serialization is manual and happens **in the controller**, not the service — every
`entity.*DataJson = JsonSerializer.Serialize(...)` assignment is in `EntitiesController`
(e.g. `:858` LUFE, `:880` BCRA, `:908` CASFOG, `:1719` NOSIS, `:1836` ARCA). The services return
typed models; they do not persist.

## Failure contracts differ per vendor — this is the main trap

There is no single convention. Check the signature before assuming null-vs-sentinel:

- **NOSIS and CASFOG** return a payload discriminated by an enum, never null:
  `NosisConsultaEstado` / `CasfogConsultaEstado` = `DataFound | NoDataReported | LookupFailed`.
- **BCRA is mixed.** `IBcraService` has five methods and they do not agree:
  - `GetDeudaAsync` → non-null `BcraDeudaData`, discriminated by
    `BcraDeudaConsultaEstado` = `DebtFound | NoDebtReported | LookupFailed`
  - `GetChequesRechazadosAsync` → non-null `BcraChequesData`, discriminated by
    `BcraChequesConsultaEstado` = `ChequesFound | NoChequesReported | LookupFailed`
  - `GetMacroAsync`, `GetCambiarioAsync`, `GetBcraDataForAnalysisAsync` → **nullable**, return null
- **LUFE** returns null on failure.

The distinction that matters: `NoDataReported` means the vendor answered and the company genuinely
has nothing on file. `LookupFailed` means the call failed. Collapsing the two silently converts an
outage into a clean bill of health. `AnalysisResults.EnrichmentStatus` exists precisely so a
transient BCRA outage — which costs the analysis its whole 0.35 BCRA weight — stays distinguishable.

`GetMacroAsync` fires **three** requests, not two, and variable 27 is asked for **twice**: once over
a rolling 60 days for the `InflacionMensual` scalar and once over a month-aligned 26-month window for
`InflacionMensualSerie`, the monthly series §7.3b deflates the LUFE sales figures by. They are
separate on purpose. `FetchWithStatusAsync` returns a null body on ANY non-success status, so one
request serving both would let a range the BCRA refused null the scalar — and `ScoringService` reads
a missing inflation figure as the NEUTRAL 50 rather than the 30 base, shifting the composite of every
analysis of every tenant. Keep the legs independent, and note the series is a SCORING INPUT riding
inside a vendor blob: it is frozen into `AnalysisResults.BcraDataJson` with everything else, which is
what makes a past score reproducible.

## Observability is incomplete — do not assume ApiCallLogs covers a vendor

`ApiCallLogs` is the shared seam, but only two services write to it themselves
(`LufeService`, `NosisService` take `IApiCallLogService`). ARCA and Claude rows are written by the
controllers (`EntitiesController`, `AnalysisController`), plus, for the legajo-scoped refresh-arca,
`PersonLegajoSnapshotRefreshService`. **`BcraService` and `CasfogService` write
no `ApiCallLogs` row at all** — two of six vendors are invisible in that table. If you are adding
logging or reading that table for coverage, start from this asymmetry rather than assuming parity.

## CASFOG uses a bespoke transport

`CasfogService` goes through `ICasfogTransport` / `HttpWebRequestCasfogTransport`, a verbatim port
of the legacy `Api.Web.Dynamics365` client's `HttpWebRequest` mechanism, because `HttpClient` hangs
that vendor's login POST. Its certificate-accept callback is set **on the request, not globally** —
setting it globally would weaken TLS for BCRA, ARCA, LUFE and SQL too. Do not "modernise" this to
`HttpClient` without reproducing the hang first.

## The scoring engine

- Defaults: `Api.Web.FinanceIA/Models/Financia/ScoringDefaults.cs`, the single source of truth.
  Read `ScoringDefaults.Entries` for the current key set — **the XML doc comment above the class
  states a stale count** and has not tracked the additions.
- Config is **DB-driven with a full code fallback**: every lookup is
  `Get(cfg, key, ScoringDefaults.Get(key))`. A tenant with zero `ScoringConfig` rows therefore
  scores silently on defaults rather than failing. DDL `007_scoring_config.sql` seeds only one
  tenant. "It scored, so config is loaded" is not a valid inference.
- To change scoring behaviour safely, three files move together: `ScoringDefaults.cs` (the default
  and its key-group array), `ScoringService.cs` (so it is actually consumed), and `SettingsService.cs`
  (validation, and the DTO group if the key is to reach the Settings UI). A numbered DDL is OPTIONAL
  and usually unnecessary: the code fallback means a tenant with no row scores on the default, which
  is why `critical_floor_*`, `bcra_trend_*`, `bcra_cheques_*`, `lufe_ventas_*` and `casfog_mora_*`
  ship without one for any tenant but the sandbox — script 017 is the precedent for a config change with no DDL at all.
  One caveat §7.3a added: `ValidateScoringConfig` opens with a blanket "every value must be >= 0" walk
  over the MERGED defaults+DB map, so a key whose DEFAULT is negative (`lufe_ventas_umbral_plena` is
  -0.25, because a sales drop is negative) breaks every PUT for every tenant on every key unless it is
  listed in `ScoringDefaults.SignedScoringKeys` — which buys it out of that rule and into a stricter
  -1..0 one. A new signed key must join that array.
- **Known gap, narrowed:** `ScoringDefaults` declares a dozen-plus key-group arrays (count them in the file, not here). `CriticalFloorKeys` IS
  used outside that file — `SettingsService.ValidateScoringConfig` walks it to bound every floor into
  0-100 — and `BcraChequesKeys` likewise (same validation loop, plus a 0/1 check on the enable flag),
  and `LufeVentasKeys` too (§7.3a: the same loop, with a 0/1 check on its enable flag, 0-100 on the
  plena and -1..0 on the two thresholds), and `CasfogMoraKeys` (§7.4: the same shape again, but its two
  umbrales are POSITIVE shares bounded 0..1 and therefore NOT in `SignedScoringKeys` — a new key only
  joins that array when its DEFAULT is negative). `ProfitabilityFloorKeys` (scoring v10: the one 0/1
  flag `critical_floor_profitability_requires_operating_loss`, kept OUT of `CriticalFloorKeys` because
  it is not a 0-100 floor) gets the same 0/1 check; its sibling `ProfitabilityFloorRatios` is read by
  `ScoringService`'s floor loop. `AcidTestFloorKeys` (scoring v11: the 0/1 flag
  `critical_floor_acid_test_requires_weak_current_ratio`, same reasons, same check) switches the rule
  that a critical prueba ácida floors the score only while liquidez corriente is below its warn
  threshold. `SignedScoringKeys` is read by that same validator, twice.
  `BcraTrendKeys` is still referenced nowhere else. Of these arrays `BuildDto` surfaces only
  `ProfitabilityFloorKeys` + `AcidTestFloorKeys`, as the `floorRules` group (K22); every other one is
  DBA- and API-writable but invisible in the Settings UI.
  Adding a key to one of those arrays does not make it appear there.

## Adding a new vendor — the shape to follow

1. `IXService` + `XService` in `Servicios/`, registered scoped (or as a typed HttpClient for SOAP).
2. A `XConsultaEstado`-style enum: prefer the `DataFound | NoDataReported | LookupFailed` sentinel
   over nullable returns — it is the contract that keeps outages visible.
3. A numbered DDL adding `Entities.XDataJson` (+ `XDataUpdatedAt`) as `nvarchar(max)`, plus the
   matching Fluent config. Never a migration.
4. Serialize in the controller, consistent with every existing vendor.
5. Take `IApiCallLogService` and write a row — and add the `Service` discriminator value in the same
   DDL, since `ApiCallLogs.Service` is constrained.
6. Read one scalar out of the blob with the `JSON_VALUE` DbFunction rather than materialising the
   whole `nvarchar(max)` column.
