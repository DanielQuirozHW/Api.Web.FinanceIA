## Skills
@.claude/skills/dotnet-coding-standards.md
@.claude/skills/dotnet-security.md
@.claude/skills/multi-tenant-bff.md
@.claude/skills/ef-core-patterns.md
@.claude/skills/azure-integration.md
@.claude/skills/error-handling.md
@.claude/skills/financia-architecture.md
@.claude/skills/financia-security.md
@.claude/skills/financia-integrations.md

Load on demand, not imported: `.claude/skills/dynamics-integration.md` — Dynamics 365 is template
legacy here (see Architecture), so 373 lines of it do not earn always-on context.

## What this is
FinancIA — a credit-scoring engine for Argentine SMEs (PyMEs). An analyst uploads financial
statements, the API enriches them from external credit bureaus, scores the company, and returns an
analysis the companion Portal renders.

## Repo layout (this file sits at the repo root)
```
Api.Web.FinanceIA/            # THE BUSINESS PROJECT — this is what gets published
Api.Web.ServiciosFinancieros/ # Legacy auth project. Frozen. In the solution, NOT deployed.
Api.Web.FinanceIA.Tests/      # xUnit + hand-rolled stubs (no mocking library)
Api.Web.ServiciosFinancieros.slnx   # .slnx format — needs SDK 9 to parse (see Build & Run)
.claude/skills/               # the skill files imported above
.claude/hooks/                # see Hooks
.github/workflows/            # see CI/CD
```
Inside `Api.Web.FinanceIA/`: `Clases/` (services, validators, middleware), `Controllers/`,
`Data/Configurations/` + `Data/Repositories/`, `Models/`, `Servicios/` (external integrations),
`docs/`.

Beware the path collision: the repo root and the business project are both named
`Api.Web.FinanceIA`. `Api.Web.FinanceIA/docs/` is the project's docs; `docs/` is the root's.

## Tech Stack
- Framework  : ASP.NET Core 8 (`net8.0`), C#
- Database   : Azure SQL Server via **Entity Framework Core 9.0.14** (targeting net8.0)
- Auth       : Firebase Admin SDK + JWT Bearer
- MSAL       : Microsoft.Identity.Client (Dynamics 365 OAuth only)
- Caching    : IMemoryCache (in-process)
- Telemetry  : Azure Application Insights 3.x (OpenTelemetry-based) — ON only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set
- Docs       : Swagger via Swashbuckle (Development only)

## Telemetry (Application Insights)
- Wired in `Clases/Telemetry/TelemetryRegistration.cs`, called once from `Program.cs`. The 3.x SDK THROWS
  without a connection string, so with none NOTHING is registered (local, tests, CI) and one Information
  line says telemetry is off. Source: the `APPLICATIONINSIGHTS_CONNECTION_STRING` App Setting on EACH
  client App Service (then `ApplicationInsights:ConnectionString`) — never appsettings.json.
- No sampling (`SamplingRatio 1`, `EnableTraceBasedLogsSampler false`; the 3.x defaults drop the logs of
  a sampled-out request, Errors included). Levels: `Logging:OpenTelemetry:LogLevel` (ours Information,
  Microsoft/System Warning). Requests carry `customDimensions.TenantId`.
- Nothing identifying leaves: no query strings, path segments with ≥7 digits (CUIT/DNI) → `{id}`, no SQL
  parameters, no headers or bodies. `--verify-schema` never registers it.
- A Claude HTTP error's reason (`HTTP 400 invalid_request_error: <msg ≤300> [req_…]`, parsed by
  `Servicios/AnthropicErrorDiagnostics.cs`, never the raw body) is in `ClaudeApiException.DiagnosticMessage`
  and `ApiCallLogs.ErrorMessage` on every Claude path that writes a row.
- Guide, per-client App Settings and KQL: `Api.Web.FinanceIA/docs/guides/telemetry.md`.

## Namespace Convention
Root `Api.Web.FinanceIA`, then one segment per folder: `.Clases`, `.Controllers`, `.Data`,
`.Data.Configurations`, `.Data.Repositories`, `.Models`, `.Servicios`.

## Architecture
- Pattern      : BFF (Backend For Frontend)
- Multi-tenant : one Firebase project per tenant
- Tenant ID    : `X-Tenant-ID` header, JWT claim as fallback
- Auth flow    : Firebase ID Token → validate → issue Internal JWT

**Azure SQL is the system of record for all business data.** Two DbContexts, two connection strings:

| Context | Connection | Holds |
|---|---|---|
| `AppDbContext` | `Default` | `Credenciales`, `TenantFirebaseConfig`, `ErrorLogs` — tenant resolution and auth only |
| `FinanciaDbContext` | `FinanciaDefault` | all 20 business tables: Analyses, AnalysisResults, Entities, Documents, CreditLines, CreditLineOperations, Fiadores, Alerts, AlertRuleCatalogue, ChatConversations, ChatMessages, Notes, ScoringConfig, TenantSettings, Users, UserInvitations + audit/log tables |

- **Tenancy is row-level.** Every business table carries a `TenantId nvarchar(100)` column holding a
  Firebase tenant **slug** (e.g. `SgrOneClickSandboxFB2`) — **not a GUID**. DDL 005 migrated it off
  `UNIQUEIDENTIFIER`. There is no EF global query filter: every query must scope by TenantId itself.
- **One connection, but a per-tenant DB procedure.** Code binds a single static `FinanciaDefault`
  connection string — no per-tenant connection resolution exists. But the DDL procedure treats each
  tenant as its own database (`docs/database/README.md`, `deploy.yml`), so a schema change must be
  applied to every tenant DB. Both halves are true; do not "simplify" either away.
- **Vendor payloads are JSON in `nvarchar(max)` columns**, as plain C# `string` properties — not
  value converters, not EF owned entities. `Entities.{Fiscal,Lufe,Bcra,Casfog,Nosis}DataJson`,
  `AnalysisResults.*Json`, `Fiadores.NosisDataJson`. Serialization is manual, at the service
  boundary. `FinanciaDbContext` maps a `JSON_VALUE` DbFunction so a projection can read one scalar
  out of a blob without selecting it — prefer that over materialising the whole column.
- **Dynamics 365 is template legacy, still live.** DI-registered and health-checked, reachable only
  via `ConsultaFetchController` (2 endpoints) and `SearchController` (1). No FinancIA business
  feature uses it. It is **not** where business data lives — and it is not dead code to delete.

## Database Approach
- **Database First exclusively** — EF Core maps existing tables only.
- **NEVER** run `dotnet ef migrations add` / `database update` / `database drop` /
  `dbcontext scaffold`, or the PMC equivalents (`Add-Migration`, `Update-Database`). This is
  enforced, not just written down — see Hooks.
- There is no `Migrations/` folder and there must never be one. `Migrations/`, `*ModelSnapshot.cs`
  or `*.Designer.cs` appearing in a diff is a bug.
- Schema changes are **numbered DDL scripts in `Api.Web.FinanceIA/docs/database/`**, executed by the
  DBA by hand. Rationale notes for individual scripts live in `Api.Web.FinanceIA/docs/db-changes/`.
- **The DDL runs BEFORE the matching deploy.** EF maps new columns unconditionally, so publishing
  code first breaks production with `Invalid column name` on every SELECT and INSERT against that
  table. The deploy workflow's `ddl_aplicada` checkbox is the operator's attestation that it ran.
- The current schema is the 23 Fluent configurations in `Api.Web.FinanceIA/Data/Configurations/`.
  Read those, not a table list in this file — a list here fossilizes within weeks.

## AI Model Policy (Claude)
- **The scoring narrative is the one fixed model**: claude-haiku-4-5, the `NarrativeModel` constant in
  Servicios/ClaudeService.cs, never hardcoded inline elsewhere. To change it, edit that constant only.
- **The balance-extraction model is an App Setting**, so a client can be switched without a deploy
  (App Service restarts the app when a setting changes; the value is read once per process):
  - `Anthropic:ExtractionModel` — default `claude-sonnet-4-6`. Allowed ids and every per-model request
    difference live in `Models/Financia/ExtractionModelCatalog.cs` and nowhere else: `MaxTokens`,
    `Temperature` (null = OMIT the field), `SupportsEffort`, `TimeoutSeconds`. Today: claude-sonnet-4-6
    (16000, temperature 0, no effort, 120 s), claude-sonnet-5-5 and claude-sonnet-5 (32000, no
    temperature, effort supported, 300 s). Sonnet 4.6 went 8000 → 16000 on 2026-11-03 because complete
    balances already answered in 7.4k–8.4k tokens and one was cut off at 8000 on every run. The 5.x models think adaptively by default and `max_tokens`
    caps thinking + JSON together; no `thinking` field is ever sent.
  - `Anthropic:ExtractionEffort` — optional low|medium|high|xhigh|max, sent as `output_config.effort`
    only for a model with `SupportsEffort` (ignored, with a Warning, otherwise).
  - `Anthropic:ExtractionTimeoutSeconds` — optional per-attempt override (30..900). Extraction runs on
    its own HttpClient (`ClaudeExtractionClient`, no client-wide timeout); "ClaudeClient" stays 120 s.
    The code keeps Sonnet 4.6 at 120 s, but a 16000-token answer takes about 480 s: on a client with long
    balances operations set this to 480.
  - An unknown model or an invalid effort NEVER breaks an analysis: `ExtractionModelSelection` logs an
    Error once per process (setting name + value) and uses the default / sends no effort.
  - **With no setting the request is the pinned default** (fixed InputHash): `ClaudeExtractionPayloadTests`
    pins it, and proves it differs from the constant-model request only in `max_tokens` (8000 then) and the
    prompt insertions since (2.19 trial balances, 2.20 `JsonOnlyAnswerRule`), each stripped back to the
    previous pin. A change to that hash is deliberate and says why in the test. Keep it green.
  - Prompt 2.20 tells the model its answer starts with `{` and holds nothing but the JSON object (no
    explanation, no reasoning, no Markdown): long balances wrote visible reasoning first, overflowed 16000
    and escalated. Assistant prefill is NOT an option (400 on 4.6+), and structured outputs do not fit the
    schema (16 union-typed / 24 optional params max). The trimming below stays as the safety net.
  - Prompt 2.21 (`ClaudeService.AlertScopeRules`): no alert whose only content is a ratio level (the engine
    grades the eight), alerts only for what the document shows, and the catalogue's exact code for a shared
    fact, built from the `AlertRuleEngine` constants + `IngresosEnCaidaAlertCode` (balance-sheet revenue drop, in NO
    `AttentionOverlaps` list: VENTAS_EN_CAIDA is the LUFE fact, the PDF would hide it; a test pins it unlisted),
    never ENDEUDAMIENTO_ELEVADO. Pinned like 2.20: undo its three edits and the 2.20 hash comes back.
  - The response is read from the first TEXT block (a thinking block may precede it), and from it ONE JSON
    value: `ClaudeService.ExtractJsonObject` (fences stripped, then a `Utf8JsonReader` read from the first
    `{` when prose comes before it, anything after the value dropped, the first object winning). Every reader
    of the answer — the strict schema read, `ParseCashFlow`, `ParseTrialBalance`, the correction suggestion —
    reads that one value, never the raw text. Dropped prose logs a Warning with model, escalated and the
    preamble/trailer LENGTHS (never the text) and sets `integrity.extraction.answerTrimmed: true` (absent
    otherwise; a clean answer's BalanceJson is byte-identical). No readable value → `unparseable`. `stop_reason`
    `refusal` fails the pass with a `ClaudeApiException` (reason in `DiagnosticMessage`, never in the
    client message), not retried. `max_tokens` on a spec WITH a temperature (Sonnet 4.6) re-sends that
    pass ONCE: `max_tokens` doubled up to `ExtractionModelCatalog.MaxEscalatedTokens` (32000), the
    per-attempt timeout scaled alike up to 900 s, ONE transport attempt, the request otherwise unchanged
    (pinned). Never a second escalation: max_tokens again fails with "… (32000) after escalation …". Both
    calls' tokens are summed into the one Extraction cost row. The 5.x specs (already 32000) fail at once.
  - A failed extraction stores ONE code in `Documents.LastExtractionError` (codes live in
    `AnalysisRunFailureCodes`, set by `AnalysisPipeline.ExtractionFailureCode`). OUR failures do NOT
    increment `ExtractionAttempts`: `claude_max_tokens` (our cap), `claude_server_error` (5xx),
    `claude_network_error`, `claude_rate_limited` (429), `claude_auth_error` (401/403),
    `claude_unparseable_answer` (no readable JSON value in the answer — our reading; the 502 says "No pudimos
    interpretar la respuesta del modelo…", not "no disponible", and the diagnostic carries only
    Path/LineNumber/BytePositionInLine, never `JsonException.Message`), `claude_no_financial_statements`
    (the KEPT pass — after the second-pass decision, `ChoosePass` and the trial-balance normalisation — has
    none of assets, current_assets, current_liabilities, liabilities, equity, net_revenue, net_profit: a blank
    page, a CPCE cover alone. Thrown from `ClaudeService.Finish` with both passes' tokens, the model and the
    detector's usage; no Analysis, no score, no monthly quota; the file comes from LUFE as is and the budget is
    per file hash, so counting it would lock the file for good. Its ApiCallLogs row (+ the detector's row) keeps
    the spend; the sync 502 says "No encontramos estados contables legibles…"; GET /runs keeps `failureCode:
    model_error` and adds `failureReason: no_financial_statements`, `causeGroup: document`; a 409 refusal reports it as `unreadable`), an interrupted
    background run, and any fault after the extraction answered. The document's failures still count, so an
    endless loop stays impossible: `claude_timeout` (still billed), `claude_refusal`,
    `claude_unreadable_answer` (a JSON value that is not a balance / empty), `claude_api_error` (other 4xx, no text) and an
    unexplained fault mid-extraction. GET /runs reports every `claude_*` code as `failureCode: model_error` and,
    additively since the Análisis en curso PR, a finer `failureReason` + `causeGroup` + `attemptsRemaining`
    (`AnalysisRunFailureReasons`, the one mapping; the sync 502 carries the same three as ProblemDetails
    extensions): no_financial_statements, unreadable_document (refusal / unreadable answer), file_rejected
    (api_error) → `document`; timeout, service_unavailable (5xx / network / 429 / 401-403), reading_failed
    (unparseable / max_tokens), interrupted, consent_required, other → `ours`. `attemptsRemaining` =
    max(0, budget − attempts summed per file hash), null with the budget off or the hash unknown. Failed items
    also carry `fiscalYear` (Documents.FiscalYear, null when unknown): with `canRetry` and a `fiscalYear` the
    Portal retries in one click, POST /api/analyses `{ documentId, entityId, fiscalYear }` with NO
    requestedAmount (null is valid end to end; no column stores the earlier amount). The same three flags ride
    on every company `DocumentResponse` of GET /api/documents and GET /api/documents/{id}
    (`DocumentFailureFlags`, reusing /runs' helpers, one batched per-hash read per call; persona humana → all
    null; the LUFE import responses leave them null). When the
    budget is spent, POST /api/analyses answers 409 `errorCode: extraction_budget_exhausted` with
    `lastFailureCode` (`AnalysisRunFailureCodes.ForBudgetRefusal`, read from the file's newest failure):
    `unreadable` (refusal / unreadable answer / no financial statements) asks for a better file; anything else says "avisá a soporte".
    Every failure path leaves an ApiCallLogs row with `DocumentId`; the stale sweep alone writes none
    (tokens unknown, nothing charged, audit reason `stale_processing` → `interrupted`).
  - A successful pass using ≥ 75 % of its cap (`ExtractionModelCatalog.NearCapWarningRatio`) logs a
    Warning with TenantId, model, MaxTokens, OutputTokens and the share — written by `AnalysisPipeline`
    from `ClaudeAnalysisResult.ExtractionPasses`, since the service does not know the tenant.
  - `ModelUsed` (→ `Analyses.AiModel`, `ApiCallLogs.Model`) is the resolved id, and the extraction cost
    is `ClaudeModelPricing.EstimateCostUsd(ModelUsed, …)`; every catalogue entry must be priced (test).
    `Analyses.AiTemperature` stays 0.00 for every model: `CK_Analyses_AiTemperature` allows nothing
    else, so for a 5.x analysis it means "no override sent" — `AiModel` tells the cases apart.
  - **Before switching a client**, run the live `ExtractionModelComparisonHarnessTests` over 15–20 of
    its real balances (guide: `Api.Web.FinanceIA/docs/guides/extraction-model.md`). Watch its latency
    p95: the Portal calls the analysis synchronously and the App Service front end cuts a request at
    ~230 s, while a thinking pass can take longer. And with more than one instance,
    `Analysis:StaleProcessingMinutes` (code default 20) must exceed the worst case: 2 passes × (3
    attempts × T + 20 s backoff + for Sonnet 4.6 one escalated attempt of min(2T, 900 s)) — ≈ 21 min at
    the 120 s default, ≈ 79 min at the recommended 480 s (set ≥ 80), ≈ 31 min for 5.x at 300 s. The
    table is in the guide. A BACKGROUND run gets ONE automatic retry after `claude_server_error`,
    `claude_network_error` or `claude_rate_limited` (`AnalysisAutoRetryPolicy`: 60 s, a 429's retry-after up
    to 120 s; the document stays Processing, no attempt, no notification until the retry ends; audited
    `Analysis.AutoRetryScheduled` / `Analysis.AutoRetryFinished`; GET /runs `autoRetry`), so double the worst
    case for background runs. In memory like the queue: a restart loses it and the sweep releases the run.
  - To add an extraction model: an `ExtractionModelCatalog` entry, its `ClaudeModelPricing` price, and a
    harness run.
- **The legajo models are guarded.** `Anthropic:LegajoExtractionModel` / `LegajoClassificationModel`
  must accept the forced `tool_choice` + `temperature 0` every legajo request carries:
  `Models/LegajoModelSupport.cs` allow-lists claude-sonnet-4-6 and claude-haiku-4-5. Anything else —
  claude-sonnet-5-5 (forced tool use → 400), claude-sonnet-5 (temperature → 400), an unchecked id —
  falls back to the service's default with an Error logged once.
- **The analyst chat model is NOT a constant** — it is a per-tenant entitlement. The catalogue of
  supported models is `Models/Financia/ChatModelCatalog.cs`, the only place a chat model id lives;
  which of them a tenant may use is `TenantBillingConfig.ChatEnabledModels` (a CSV column, DDL 052,
  SuperAdmin-editable via PUT /api/billing/config and readable on GET /api/billing/usage); which one
  answers a given turn is resolved in `AnalysisController.ChatAsync` from the analyst's optional
  `ChatRequest.Model` filtered against that list, falling back to the list's first entry rather than
  erroring. `ClaudeService.ChatAsync` takes the id as a parameter and rejects anything outside the
  catalogue. Catalogue today: claude-haiku-4-5, claude-sonnet-4-6 (default), claude-sonnet-5.
- Temperature is 0.00 on every Claude call **except where the model rejects it**. Claude Sonnet 5 and
  5.5 return HTTP 400 for any non-default sampling parameter, and 0 is non-default, so their catalogue
  entries (chat and extraction) carry `Temperature = null` and the payload omits the field entirely.
  The same entries carry a larger `MaxTokens`, because those models run adaptive thinking by default
  and `max_tokens` caps thinking and answer together. Per-model call differences belong in the
  catalogues, nowhere else. Every call sends `anthropic-version: 2023-06-01`; on `2026-10-21` the API
  rejects `temperature` on every model, so a version switch must drop it from the 4.x paths too.
- Document extraction runs a SECOND, differently-read pass when the first is doubtful: its effective
  confidence is below Anthropic:ExtractionConfidenceFallbackThreshold (default 0.60), OR it fails a
  totals/structure integrity check (`BalanceIntegrityService.StructureChecks`: assets_components,
  liabilities_components, accounting_equation, the three *_items checks, column_swap), OR its
  net_profit_reconciliation failed as a COLUMN MIX (result before tax + income tax as read equal the
  stored `prior_year.net_profit` within the column-swap tolerance: both taken from the comparative
  column; trigger `column_mix`, recorded as `checks[].columnMix`). The cap a failed check puts on
  confidence (0.69) sits ABOVE the threshold, so the integrity trigger is the one that catches a
  misread balance. A column mix is a misread, never the `integrity_presentation` caveat (policy 6); an
  unchecked one (no comparative, a blob from before it was recorded) keeps today's verdict and is
  never re-judged on read. The second pass is page images with NO PDF document block (an
  unreadable text layer travels in that block), each page turned upright when its text is drawn
  rotated — `Servicios/PdfPageOrientation.cs`, local PdfPig glyph direction, no model call, no cost.
  A page with fewer than `MinOrientedLetters` glyphs (a SCAN) is not decided by the glyphs: those
  pages alone go, as ≤512 px thumbnails, to `IScanOrientationDetector` — ONE claude-haiku-4-5 call
  (`ClaudeScanOrientationDetector.Model`, the only place that id lives for it; forced tool +
  temperature 0, so it must stay on `LegajoModelSupport.ForcedToolUseModels`, a test pins it), one
  attempt, 30 s, on "ClaudeClient". Any failure leaves the page as it is and never fails the analysis.
  Tesseract OSD was rejected: its NuGet packages ship Windows DLLs only, so the ubuntu CI leg cannot
  load it. A page wide once upright (w/h ≥ 1.25) also goes as its left and right halves (6% overlap
  past the midline, JPEG 85) right after the full page, within min(100, tier pages) images and a
  64 MB base64 budget — turned pages first; more than 20 images caps every side at 3000 px. The
  second pass's closing text (`SecondPassTextBlock`, NOT the large-file first pass's) names which
  image is which half and adds two reading rules (figures from the half and row of their own label;
  '-'/'–'/empty is zero). Under 5 MB it renders at 220 DPI / 100 pages; above, at the first pass's
  own tier, and only when a page was turned or split (otherwise it would repeat the first request).
  Same model spec. The detector's tokens are NEVER added to the extraction's: `AnalysisPipeline`
  writes them as a second ApiCallLogs row (Operation `Extraction`, Model claude-haiku-4-5, priced by
  that model, same AnalysisId). `integrity.extraction` also records `splitPages` and
  `orientationSource` (glyphs | image | mixed | none). Live check on real scans:
  `ScanSecondPassHarnessTests` (FINANCIA_SCAN_CASES_DIR, baseline vs new, latency per pass).
  `ClaudeService.ChoosePass` keeps: integrity passed (and having evaluated what the other pass
  failed) > fewer failed structure checks (over checks both ran) > no column mix (over a pass that
  ran net_profit_reconciliation) > higher confidence > the first.
  Tokens of both passes are summed into the one Extraction cost row. A second pass that cannot render,
  fails at the API or throws keeps the first pass. The provenance lands in `BalanceJson.integrity.extraction`
  and, with the failed checks, reaches the Portal as `extractionIntegrity` on the analysis detail.
  `extractionIntegrity.verified` is separate from `passed` (which an all-skipped report passes
  vacuously): it requires the three structural checks (`BalanceIntegrityService.VerificationChecks`)
  to have RUN; `"integrity": null` reads as `integrity_unavailable`. `extractionIntegrity.fiscalYear`
  compares `Analysis.FiscalYear` with `fiscal_year_detected` under the closing-year convention, no ±1
  tolerance (`FiscalYearCheck`); a mismatch also appends an es-AR anomaly on a new analysis.
  A first pass that passes every check sends ONE request, byte-identical to before. Scoring and the
  critical floors do NOT react to a failed integrity yet (policy pending with the SGR).
- A TRIAL BALANCE ("Balance de Presentación" / sumas y saldos printed from the ledger) is detected by the
  model (prompt 2.19: `document_kind`, plus a `trial_balance` block of PRINTED ledger figures — credit-sign
  flag, printed equity, classified results accounts). All arithmetic is `Servicios/TrialBalanceNormalizer.cs`,
  run per pass before the integrity checks: credit signs → positive liabilities/rubros, and ONLY when the
  accounting equation proves it, a pre-closing equity + the year's result (= −Σ results accounts under a
  credit-negative ledger), net_profit / result_before_tax / income_tax / financial_expenses / operating_result
  from the classes, one es-AR anomaly. No proof → nothing moves (`not_reconciled`). BalanceJson root gets
  `document_kind` + `trial_balance` (audit, no account labels) for a trial balance only; financial statements
  persist byte-identically. Reliability: `trial_balance` CAVEAT (level pending the SGR), via facts version 5.
- To add or retire a CHAT model, edit ChatModelCatalog only — the entity, the validation, the pricing
  and the payload all read from it — and note that retiring one narrows every tenant at once (stored
  ids outside the catalogue are dropped on read), while adding one does NOT enable it for existing
  tenants, whose stored list is explicit. Keep any UI that displays a model in sync.

## Build & Run
```bash
# Build/test the SOLUTION. Needs BOTH SDKs installed: only SDK 9.0.200+ (or 10)
# can parse .slnx, and the projects target net8.0 so the 8 runtime is needed to
# run the tests. SDK 8 alone fails with MSB4068 before compiling anything —
# verified. CI installs 8.0.x + 9.0.x for exactly this reason.
# Note: Api.Web.FinanceIA/global.json pins SDK 8 for builds run from INSIDE that
# project dir (a plain csproj build, which SDK 8 handles fine). Build the .slnx
# from the repo root, where no global.json applies.
dotnet build Api.Web.ServiciosFinancieros.slnx -c Release
dotnet test  Api.Web.ServiciosFinancieros.slnx -c Release

# Run (HTTPS). Swagger is at /swagger in Development only.
dotnet run --launch-profile https
```
Warning counts only reflect a full compile — an incremental build reports 0. Use
`--no-incremental` when checking the warning baseline.

## CI/CD (`.github/workflows/`)
- `ci.yml` — every PR into DEVELOP and every push to DEVELOP: build + test, both hard gates, plus a
  `no-ef-migrations` job that fails if any EF migration artefact is in the repo.
- `deploy.yml` — a push to DEVELOP builds, tests and uploads an artifact but **never deploys**.
- **One deploy per client.** Each client has its own App Service and its own Financia DB; the
  Default DB is ONE, shared by all. The client list is `.github/clients.json` —
  `{ name, app_service, publish_profile_secret, schema_connection_secret, environment }`, the two
  `*_secret` fields holding secret NAMES, never values. The build runs ONCE (windows-latest, one
  artifact); the `deploy` job is a matrix over the selected clients with `fail-fast: false` and a
  per-client `concurrency: deploy-<name>`, so one client failing never stops the others. Each leg
  checks its secrets exist (an empty `secrets[...]` fails that client with the secret's name),
  verifies the schema, publishes with `restart: true`, and gates on `/health` at
  `https://<app_service>.azurewebsites.net`. The `resumen` job writes one table: schema
  OK/FALTA DDL/INCONCLUSO, deployed, health code per client. Seed client: `sgr-portales` →
  `api-sgr-portales-analisisderiesgo`, secrets `AZURE_WEBAPP_PUBLISH_PROFILE` +
  `SCHEMA_VERIFY_CONNECTIONS`, environment `production`.
- Production is manual: Run workflow from DEVELOP with `ddl_aplicada` ticked and `client` = `all`
  (default) or one `name` (an unknown name fails the run before anything deploys). That job's `if:`
  **is** the gate — `environment:` pauses nothing on this plan.
- **`ddl_aplicada` is now backed by evidence.** Before publishing, each deploy leg runs the built
  artifact as `dotnet Api.Web.FinanceIA.dll --verify-schema`, which compares this build's EF models
  against `sys.objects`/`sys.columns` and aborts if a mapped table or column is missing. It reads
  `FINANCIA_SCHEMA_VERIFY_CONNECTIONS` ← that client's `secrets[schema_connection_secret]` (one
  connection string per line), which is **mandatory** — an empty one fails that client. It verifies
  the client's **Financia DB only**: the workflow sets `FINANCIA_SCHEMA_VERIFY_SKIP_DEFAULT=true`, so
  the shared Default DB (shared by every company portal, not changed by this repo's releases) is
  left out explicitly, whatever connection string configuration might carry. The
  `SCHEMA_VERIFY_DEFAULT_CONNECTION` secret is no longer used. A `default-db/` script is therefore
  run AND checked by hand — no deploy catches it. It is
  READ-ONLY — it runs no DDL, so the pipeline never needs `ALTER` on production, and the DBA still
  executes the scripts. It also needs network access from the runner to Azure SQL;
  `omitir_verificacion_esquema` is the emergency opt-out. Code in `Clases/SchemaVerification/`,
  guide in `Api.Web.FinanceIA/docs/guides/schema-verification.md`.
- Release order: DBA runs the pending DDL in **every** client's Financia DB (and in Default if a
  script targets it — by hand, and checked by hand, since the gate skips Default) → this workflow with `client=all` (publish + App Service restart per client)
  → Portal release. **Exception:** if the change adds an enum value, the Portal goes BEFORE this API
  (`docs/database/README.md`). Only `Api.Web.FinanceIA/Api.Web.FinanceIA.csproj` is published;
  `/health` returning 200 is the post-gate.
- **Adding a client** (full steps in the `deploy.yml` header):
  1. App Service: Windows, .NET 8 stack, Always On, the global default hostname (not "unique
     default hostname", since the URL is derived from the name), and **SCM Basic Auth Publishing
     Credentials = On** so the publish profile can be downloaded.
  2. Secrets: its publish profile and its Financia connection string(s), e.g.
     `AZURE_WEBAPP_PUBLISH_PROFILE_<NAME>` / `SCHEMA_VERIFY_CONNECTIONS_<NAME>`, as repo secrets or
     as secrets of that client's environment.
  3. A `.github/clients.json` entry naming those secrets.
  4. An Azure SQL firewall rule letting the runner reach its Financia DB.
  5. All DDL run on the new DB before its first deploy.

## Companion Portal (front end)
- Repo `PortalesHWA/Portal-FinanceIA` — org **PortalesHWA**, not `jlnhwapplications`. Working base
  is `develop`: branch FROM `develop`, PR AGAINST `develop`. `main` and `staging` exist downstream
  for release only — never branch from them. Mind the clash: the Portal's working branch is
  `develop` (lowercase), this API's is `DEVELOP` — same idea, different repos, do not cross them.
- Next.js monorepo, npm. Its `lint` script is a **stub** (`echo '[lint disabled]'`) and proves
  nothing — verify with `npx tsc --noEmit`.
- PRs into the Portal's `develop` run CI — a type-check / lint / test job and a build-and-preview-
  deploy job — so the Portal gate is no longer just a local `npx tsc --noEmit`. `lint` is still the
  no-op stub above, so the type-check is the half with teeth.
- Coupled to this API by `Cors:AllowedOrigins` and `AppSettings:FrontendUrl`, and by services that
  are rule-for-rule ports of its TypeScript (`Servicios/BcraChequesEvolucion.cs`,
  `Servicios/PdfExportService.cs`). A breaking DTO change here needs a matching Portal deploy.

## Environment & Secrets
- Local dev: `dotnet user-secrets`. Staging/Production: Azure App Service Application Settings.
- Never store secrets in appsettings.json.
- AES encryption key: environment variable `ENCRYPTION_MASTER_KEY`.

## Available Commands
- /review-security {filepath} → security audit on a file
- /review-code {filepath}     → code quality review on a file
- /scaffold-dbcontext {table} → map existing SQL table to EF Core

## Available Tools
- scaffold-controller {name} {scope}            → generate controller
- scaffold-service {name} {type}                → generate service
- add-tenant-feature {name} {type} {scope}      → generate full feature slice

## Hooks
Two **real** PreToolUse hooks enforce the Database-First rule. They are wired in
`.claude/settings.json` under `"hooks"`; if that key is missing they are inert, so check it before
trusting them:
- `.claude/hooks/block-ef-migrations.sh` — denies any `dotnet ef` / `dotnet-ef` / PMC migration
  command, including behind `cd`, `&&`, `;` or a subshell, and blocks installing the EF CLI.
- `.claude/hooks/block-migration-writes.sh` — denies writing to `Migrations/`, `*ModelSnapshot.cs`
  or `*.Designer.cs`.
- Backstop: the `no-ef-migrations` job in `ci.yml`, which also catches a human typing `dotnet ef` in
  their own terminal and cannot be skipped with `--no-verify`.

`.claude/hooks/pre-commit.md` and `post-generate.md` are **prose specifications, not executable
hooks** — nothing runs them. Treat them as a manual checklist. `post-generate.md` is also stale: its
namespace table targets `Api.Web.ServiciosFinancieros.*`, contradicting the Namespace Convention
above. Do not automate it as written.

## Key Rules (summary)
- Every request must resolve TenantContext before authentication
- Never use FirebaseAuth.DefaultInstance or FirebaseApp.DefaultInstance
- Never hardcode tenant names, client IDs or secrets in code
- Never inject ITenantContextAccessor in services — pass TenantContext as parameter
- Always scope every FinanciaDbContext query by TenantId — there is no global query filter
- Role names live in `Clases/Roles.cs` and nowhere else. SuperAdmin is a real, consolidated role
  there — cross-tenant, granted by the DBA, and absent from `SettingsController.ValidRoles` so no
  portal Admin can invite one. It is the only role permitted to PUT /api/billing/config (the tenant
  plan, incl. `ChatEnabledModels`) and PUT tenant-profile; GET /api/billing/usage reads that back
  for any authenticated tenant user. Beyond those two writes it is a READ-ONLY support role: it sits
  in every constant the Auditor reads and exports through (`All`, `CreditLineReaders`,
  `FiadorReaders`, `TenantConfigReaders`, `AuditViewers`) and in NO write constant — nor in
  `AlertReadMarkers` (alert read state is tenant-wide) nor `IsConfirmer`. `SuperAdminReadOnlyAuthorizationTests`
  scans every write action to hold that line. A user row can only carry it once DDL 076 has run.
- The company upload (`POST /api/documents/upload`, no `personLegajoId`) is idempotent for 120 s
  (`DocumentsController.DuplicateUploadWindow`): the same file (entity + document type + SHA-256) again answers
  200 with the first document and writes nothing. Look-up + blob + row run under `KeyedAsyncLock` per (tenant,
  entity, hash) — ONE instance only; across instances the Portal guard is the other half. Not wider on purpose:
  `ReanalysisPolicy` is per document. Old duplicates: optional DML 085.
- The NOSIS Manager "Informe Individual" import (`POST|DELETE /api/entities/{id}/nosis/report`, DDL 087,
  guide `Api.Web.FinanceIA/docs/guides/nosis-manager-report.md`) parses the PDF IN MEMORY with
  `Servicios/NosisReport/` (PdfPig, deterministic, no model call) and DISCARDS the file: no blob, no
  Documents row. Only company-level JSON lands in `Entities.NosisReportJson`; the personal sections — and
  ANY heading the parser does not know, told by its printed style — are dropped by the splitter before any
  parser, and `NosisReportPrivacyGuard` drops identifier-shaped strings. Numbers are read by their own shape
  (formats vary by section; an ambiguous one with no section style is null). Bump
  `NosisManagerReportParser.ParserVersion` on any parser change: a stored report of an older version is
  re-read when the same file is uploaded again.
  Display only — no score, alert or prompt may read `NosisReportJson`, and it is not `NosisDataJson`
  (the WS.01 snapshot the score reads). Gated by the tenant's NOSIS entitlement only, not the WS
  credentials; companies only. Real reports are checked with the local `NosisManagerReportHarnessTests`
  (FINANCIA_NOSIS_REPORTS_DIR + optional FINANCIA_NOSIS_REPORTS_EXPECTED ground truth, both outside the
  repo; it prints counts, statuses and OK/DIFF/MISSING only, never a value).
- A new client's FIRST Admin comes from `POST /api/bff/setup/first-admin`: anonymous, off (404)
  unless the `Setup__BootstrapKey` App Setting (≥32 chars) is set, `X-Setup-Key` + `X-Tenant-ID`
  required, only while the tenant has no ACTIVE Admin. It writes `UserInvitations.InvitedByUserId =
  NULL` (DDL 075) — every reader must handle a null inviter. Remove the key once used. Guide:
  `Api.Web.FinanceIA/docs/guides/first-admin.md`.
- The PDF co-brand header (`Servicios/Pdf/`, guide `Api.Web.FinanceIA/docs/guides/pdf-branding.md`) switches ONLY
  on a usable `branding.logo_url`: no logo → byte-identical Financia export (pinned by `PdfLightFingerprintTests`
  and `PdfCoBrandRenderTests.CompanyDark/PersonaDark`); `branding.company_name` or `branding.accent_color` alone
  change nothing. The footer is always Financia. The logo is fetched server-side ONLY through
  `BrandingLogoFetcher` / the "BrandingLogoClient" (https:443, no redirect, no proxy, `BrandingLogoConnectPolicy`
  refuses every non-public address, 1 MB, PNG/JPEG by magic bytes, ≤ 4000 px, 5 s, cached 6 h / 15 min) — never
  fetch a tenant URL with another client. Logs name the URL's host only.
- Always use sealed classes unless inheritance is required
- Always use AsNoTracking() for read-only EF queries
- Always include CancellationToken in every async method
- Always use structured logging with TenantId parameter
- Always use ProblemDetails for error responses
- Never expose internal exception details in API responses
- Never log tokens, secrets, connection strings or PII
- Never send PII (entity name, CUIT, or any identifying data) inside a prompt to the
  Claude API. The model analyses figures, not identities. (Distinct from the logging
  rule above.)
  - The analysis chat enforces it at the boundary (K5): `AnalysisController.ChatAsync` runs the final
    system prompt (its executive summary is model-written from the PDF), every replayed history turn
    of both roles and the current message through `Servicios/Privacy/EntityIdentifierRedactor` —
    BusinessName, TradeName, the LUFE registry name, LUFE authorities/partners and their CUITs →
    "la empresa" / "[persona vinculada]" / "[CUIT]". Copies only: ChatMessages and the summary keep the
    real name. It FAILS CLOSED: a turn whose identifiers cannot be loaded or applied gets the chat's 502
    and is never sent. It logs counts only. The attachment carve-out below is unchanged.
  - Scoped exception — the chat attachment. A cash-flow .xlsx the analyst uploads to
    POST /api/analyses/{resultId}/chat/attachment often carries the company name or CUIT
    in a title row, and its Markdown rides the NEXT chat turn as a second text block. That
    is the analyst's own file for their own analysis, so it is allowed in the LIVE prompt
    for that one turn. It reaches the model ONCE: it is never replayed as history, never
    re-injected into a later prompt, and never logged as content — only its size and
    truncation flag are logged. Since DDL 055 the capped Markdown IS persisted, on the USER
    turn that carried it (ChatMessages.AttachmentMarkdown), and since DDL 056 its file name
    beside it (ChatMessages.AttachmentFileName, NULL exactly when the Markdown is), so the
    analyst can read back what they attached and which file it was; that is storage in their
    own tenant DB beside the analysis, not a prompt and not a log, and nothing reads either
    column into a prompt. GET .../chat/{conversationId}/messages/{messageId}/attachment
    serves the stored Markdown back to the analyst on demand — a read path, not a prompt
    path; it logs the length only, and re-using a spreadsheet still means the caller posting
    it back as an ordinary attachmentMarkdown on a new turn. Do not confuse any of this
    with ChatMessages.HadEphemeralAttachment (DDL 053), a billing marker on the ASSISTANT
    turn. This exception covers analyst-uploaded attachments and nothing else: every other
    path into a prompt still strips identifying data (see BuildChatBcraBlock).

## Known Errors
@Api.Web.FinanceIA/MISTAKES.md

## Documentation
- `Api.Web.FinanceIA/docs/database/`   → numbered DDL scripts + execution history (authoritative)
- `Api.Web.FinanceIA/docs/db-changes/` → per-script rationale and deploy ordering
- `Api.Web.FinanceIA/docs/guides/`     → operational guides
- `Api.Web.FinanceIA/docs/frontend/`   → components staged for the Portal repo
- `docs/tasks/`                        → one-off task specs

`Api.Web.FinanceIA/docs/architecture/README.md` is template-era and describes a generic
Dynamics-backed BFF. It is **not** the design of record — the code plus the DDL trail is. Do not
treat it as authoritative and do not extend it.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
