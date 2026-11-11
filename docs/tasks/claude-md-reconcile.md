# SPEC — Reconciling CLAUDE.md with reality

Branch `chore/claude-md-reconcile`. Docs, skills and hooks only — **zero feature-code changes**.

`Api.Web.FinanceIA/CLAUDE.md` describes a system that no longer exists. It was written in the
auth-only, template-era phase of this repo and never reconciled as FinancIA grew into a
credit-scoring engine with 23 SQL tables, six live vendor integrations and a scoring engine.

Three problems compound:

1. It asserts **the opposite of reality** on where business data lives — the most consequential
   fact in the repo.
2. It is **largely inert**. It sits at `Api.Web.FinanceIA/CLAUDE.md`, but sessions run with cwd at
   the repo root, where no CLAUDE.md exists. All 7 skill `@`-imports resolve to a directory that
   does not exist.
3. Its safety rules are **advisory only**. The sacred "never run EF migrations" rule is written in
   eight places and mechanically enforced by two bypassable prefix rules in a settings file that is
   not the tracked source of truth.

Method: 12 investigation agents across 6 dimensions, each dimension adversarially re-verified by a
second agent instructed to refute it. The verify pass corrected ~44 counts; every load-bearing
conclusion survived. Line citations below were re-read by hand.

---

## 1A. CONTRADICTORY — the data-location lie

`CLAUDE.md:52-53` says business data lives in Dynamics 365 and SQL is "auth/config only".
**Both halves are false.**

| Current claim | Reality | Evidence |
|---|---|---|
| "single shared Azure SQL DB for auth/config only" | Two DbContexts on two connection strings | `Program.cs:49-55` (`Default`), `Program.cs:57-67` (`FinanciaDefault`) |
| "Business data: Dynamics 365 API per tenant" | 20 business DbSets, all in SQL | `Data/FinanciaDbContext.cs:37-132` |
| "(no per-tenant DB)" | Analyses/entities persisted with `SaveChangesAsync` | `AnalysisResultRepository.cs:69-71`, `EntityRepository.cs:74-105` |

Vendor payloads are genuine `nvarchar(max)` JSON columns — plain C# `string` properties, **not**
`HasConversion` value converters and **not** EF `.ToJson()` owned entities:

- `Entities` — `FiscalDataJson`, `LufeDataJson`, `BcraDataJson`, `CasfogDataJson`, `NosisDataJson`
  (`Data/Configurations/Financia/EntityConfiguration.cs:89-123`)
- `AnalysisResults` — 15 `nvarchar(max)` columns, 9 of them `*Json`
  (`Data/Configurations/Financia/AnalysisResultConfiguration.cs:38-138`)
- `Fiadores.NosisDataJson` (`FiadorConfiguration.cs:62-66`)

Decisive proof the JSON is queried **inside SQL**: `FinanciaDbContext.cs:160-175` registers a
`JSON_VALUE` relational DbFunction so `AnalysisResultRepository.cs:332-333` can pull
`$.deuda.estado` out of `BcraDataJson` without selecting the blob.

**Dynamics 365 is not dead.** Still compiled, DI-registered (`Program.cs:80-81`), health-checked
(`Program.cs:259`), and reachable via three endpoints on two controllers
(`ConsultaFetchController:73` and `:150`, `SearchController:74`). No FinancIA business feature
calls it. CLAUDE.md must state SQL as system of record **and** pin Dynamics' residual role, so that
neither "business data is in Dynamics" nor "Dynamics is dead, delete it" can be concluded.

### The nuance that must survive the edit

`FinanciaDbContext` is *described* everywhere as the "per-tenant business database"
(`Program.cs:57`, `docs/database/README.md:72`, `.github/workflows/deploy.yml:17`
"en cada base de tenant"), but the code binds **one static `FinanciaDefault` connection string** —
there is no per-tenant connection resolution anywhere in the codebase. Isolation is **row-level**,
via a `TenantId nvarchar(100)` Firebase **slug** column on every business table. It is not a GUID:
DDL `005_fix_business_tenantid_to_nvarchar.sql` migrated every business table off
`UNIQUEIDENTIFIER` to accept slugs like `SgrOneClickSandboxFB2`.

Both halves matter. Assume per-tenant connections and you write code that does not exist; assume
pure row-level and you forget the DDL must be applied to each tenant database at deploy time.

## 1B. FOSSILIZED

| Line | Problem |
|---|---|
| `:73` "Two tables: Credenciales + TenantFirebaseConfig" | 23 tables across 23 Fluent configurations. `docs/database/` holds 47 `.sql` files — 45 numbered, up to `046_fiadores_table.sql`, with `011` used twice and `002`/`015` absent |
| `:13` "Entity Framework Core 8" | EF Core **9.0.14** targeting `net8.0` (`.csproj:15-16`); `FinanciaDbContext.cs:162` already says "EF Core 9" |
| `:103-105` "Available Hooks" | Nothing runs. `.claude/hooks/*.md` are prose documents; no `hooks` key in any settings file; all 14 `.git/hooks/` entries are `.sample` |
| `:141` "/docs/decisions/" | Does not exist. `docs/db-changes/` (9 notes) and `docs/frontend/` do exist and are unlisted |
| `:29-30` docs tree | Shows only `database/`; omits `architecture/`, `db-changes/`, `guides/`, `frontend/` |
| `:75-84` Build & Run | Silent on CI/CD (now a hard merge gate) and on the SDK-9-for-`.slnx` requirement |

The "Available Hooks" entry is the worst of these: it sits directly beneath "Available Commands"
and "Available Tools", which **are** backed by real files, so the parallelism actively misleads.
`post-generate.md:17-23` is also dangerous if ever automated as written — it maps folders to
`Api.Web.ServiciosFinancieros.*` namespaces, contradicting `CLAUDE.md:37-45`.

## 1C. BROKEN IMPORTS

All 7 `@.claude/skills/*.md` imports resolve against the **containing file's** directory →
`Api.Web.FinanceIA/.claude/skills/` → **does not exist**. The 9 skill files live at the repo root,
in `.claude/skills/`.

Control case that proves the rule: `@MISTAKES.md` (`:132`) *does* resolve, because
`MISTAKES.md` sits beside CLAUDE.md.

**Fix:** move CLAUDE.md to the repo root, where `@.claude/skills/x.md` resolves natively. This also
fixes the auto-load problem (1, above) and avoids `@../` syntax, which could not be confirmed as
supported — there is zero in-repo precedent and `claude` is not on PATH here to test it.

## 1D. WRONG FACTS INSIDE THE SKILLS

The two **most accurate** skill files are the two that are **never imported**:

| File | Problem |
|---|---|
| `financia-security.md:19` | "TenantId in FinanciaDbContext is a GUID (uniqueidentifier)" — **false and dangerous**; DDL `005` disproves it |
| `financia-architecture.md:16` | Same GUID error, and it **contradicts itself** eight lines later at `:24` ("string slug — NOT a GUID") |
| `financia-architecture.md:14-15` | Table list names `Thresholds`, `ApiKeys`, `WebhookEndpoints` — none exist |
| `multi-tenant-bff.md:99-101` | Says services receive `TenantContext` "as constructor parameter via DI", contradicting `CLAUDE.md:111` and both orphans. Its *controller* guidance at `:82-83` is correct and stays |
| `azure-integration.md:5-7`, `:74` | Assert Dynamics-backed business data |
| `dynamics-integration.md:4` | Same assertion; 373 always-loaded lines for the one integration no business flow uses |

Scope for this PR: **fix the wrong facts, leave the prose alone.** The 7 stale
"Api.Web.ServiciosFinancieros" titles are cosmetic and stay.

## 1E. MISSING

Six live vendor integrations and the scoring engine have **zero substantive** skill coverage:
`BcraService`, `LufeService`, ARCA (three SOAP services), `CasfogService`, `NosisService`,
`ClaudeService`.

Their failure contracts differ, which is exactly why a single skill is worth writing:

- **NOSIS, CASFOG** — return a `LookupFailed` payload
- **LUFE** — returns null
- **BCRA — mixed.** `GetDeudaAsync` and `GetChequesRechazadosAsync` return non-null, discriminated
  by an `Estado` enum (`DebtFound` / `NoDebtReported` / `LookupFailed`); the other three return null
- Only `LufeService` and `NosisService` write `ApiCallLogs` rows; `BcraService` and `CasfogService`
  write none, so two of six vendors are invisible in that table

Scoring is DB-driven with a **full code fallback**: every lookup passes `ScoringDefaults.Get(key)`
as its fallback, so a tenant with zero `ScoringConfig` rows still scores silently.
`ScoringDefaults.Entries` holds **54** keys — the XML doc at `ScoringDefaults.cs:7` still says 49.

The **companion portal** is heavily coupled but never named: 76 `portal` mentions across `.cs`
files, rule-for-rule TypeScript ports (`BcraChequesEvolucion.cs:14-15` ports
`apps/portal/src/lib/bcra-cheques-evolucion.ts`; `PdfExportService.cs` mirrors its palette),
`FrontendUrl: https://financia-portal.vercel.app` and `Cors:AllowedOrigins`
(`appsettings.json:46,90`), and the invite link `{FrontendUrl}/register/invite?token=`
(`UserInvitationService.cs:99-102`). No file records the repo **owner** — which is precisely how it
gets guessed wrong.

## 1F. THE DDL RULE IS NOT ENFORCED

The rule is written in eight places and mechanically enforced by **two prefix deny rules** in
`.claude/settings.local.json:55-56` — `Bash(dotnet ef migrations*)` and
`Bash(dotnet ef database update*)`.

Uncovered by those two rules:

- `cd Api.Web.FinanceIA && dotnet ef database update` (any prefix defeats a prefix match)
- `dotnet-ef …` (the hyphenated CLI name)
- `dotnet ef database drop` (the second rule is `database update*`, not `database*`)
- `dotnet ef dbcontext scaffold`
- PowerShell PMC cmdlets: `Add-Migration`, `Update-Database`, `Drop-Database`

Worse, `Write(*)` is allowed, so migration files can simply be typed out by hand.
`Microsoft.EntityFrameworkCore.Tools 9.0.14` **is** referenced (`.csproj:16`), so `dotnet ef` is a
real, runnable command here.

Current state is clean — zero `Migrations/` directories, zero `*ModelSnapshot.cs`, zero
`*.Designer.cs` anywhere in the tree — but that is held by habit, not by a gate.

## 1G. FLAGGED, NOT FIXED — feature code, out of scope

`AnalysisRepository.GetByIdAsync` (`:45-51`) and `GetByIdsAsync` (`:54-64`) query
`[dbo].[Analyses]` **by primary key with no TenantId predicate**, and there is no EF global query
filter to back them up. Those two reads are cross-tenant at the data layer.

Reported here only. This PR changes no feature code.

---

## What this PR changes

| # | Change |
|---|---|
| 0 | This SPEC. |
| 1 | **`CLAUDE.md` moves to the repo root**, reconciled per 1A/1B. Architecture rewritten; "two tables" replaced by a pointer; EF 8→9.0.14; `.slnx`/SDK-9 build note; new ≤5-line CI/CD and portal sections; honest hooks section; docs tree corrected. AI Model Policy, Namespace Convention, Key Rules and the PII/chat-attachment rule are **verified accurate and kept unchanged**. |
| 2 | Imports re-pointed to `@.claude/skills/x.md` (resolves natively at root). The two accurate orphans are **adopted**; `dynamics-integration.md` is **demoted** to load-on-demand. |
| 3 | Wrong facts in `financia-security.md`, `financia-architecture.md`, `multi-tenant-bff.md`, `azure-integration.md` corrected. New `.claude/skills/financia-integrations.md` covering the six vendors and the scoring engine. |
| 4 | **The DDL guard** — see below. |
| 5 | `Api.Web.ServiciosFinancieros/CLAUDE.md` reduced to a "legacy, frozen, not deployed" pointer. |

### The DDL guard (change 4)

Two layers, because neither alone is sufficient:

**Committed and enforced for everyone:**

- `.claude/hooks/block-ef-migrations.sh` — a `PreToolUse` hook on `Bash`. Reads the tool-call JSON
  from stdin, normalises the command, and denies via
  `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny",…}}`.
  Catches `dotnet ef` **anywhere** on the line (so `cd`-prefix, `&&`, `;` and subshells cannot slip
  past), `dotnet-ef`, and the PowerShell PMC cmdlets.
- `.claude/hooks/block-migration-writes.sh` — a `PreToolUse` hook on
  `Write|Edit|MultiEdit|NotebookEdit`. Denies any path under `Migrations/` and any
  `*ModelSnapshot.cs` / `*.Designer.cs`. Closes the hand-typed-migration hole that `Write(*)` leaves open.
- `.github/workflows/ci.yml` — a new `no-ef-migrations` job that fails any PR introducing those
  paths. **This is the layer the hooks cannot provide:** it also catches a human typing `dotnet ef`
  in their own terminal, and it cannot be skipped with `git commit --no-verify`.

**Not committed — paste this into `.claude/settings.json` yourself**, as a sibling of the existing
`permissions` key. It is kept out of the PR so your local permission edits do not ride along:

```json
"hooks": {
  "PreToolUse": [
    {
      "matcher": "Bash",
      "hooks": [
        {
          "type": "command",
          "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/block-ef-migrations.sh",
          "timeout": 10,
          "statusMessage": "Checking for EF migration commands..."
        }
      ]
    },
    {
      "matcher": "Write|Edit|MultiEdit|NotebookEdit",
      "hooks": [
        {
          "type": "command",
          "command": "${CLAUDE_PROJECT_DIR}/.claude/hooks/block-migration-writes.sh",
          "timeout": 10,
          "statusMessage": "Checking for EF migration artefacts..."
        }
      ]
    }
  ]
}
```

`${CLAUDE_PROJECT_DIR}` resolves to the repo root, so this keeps working regardless of the session's
working directory. Both scripts are committed with the execute bit set.

Keep the existing `Bash(dotnet ef migrations*)` / `Bash(dotnet ef database update*)` deny rules in
`settings.local.json` — they are redundant with the hook but cost nothing, and they still apply if
the hook is ever unwired.
