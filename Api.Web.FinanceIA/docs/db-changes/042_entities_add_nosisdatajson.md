# 042 / 043 — NOSIS lands on the entity, and the api-call log starts recording at all

## What changed and why

NOSIS was designed into this schema long before it was built. Script 026 reserved
`AnalysisResults.NosisDataJson`, `AnalysisResultConfiguration` has mapped it since, and
`AnalysisController` carried a literal `NosisDataJson = null, // reserved — NOSIS integration not yet
available`. The live entity had no such column at all, so there was nowhere for a NOSIS snapshot to
live between analyses.

**042** adds that missing half: `Entities.NosisDataJson` + `Entities.NosisDataUpdatedAt`, the exact
pair every other external source already has (`Fiscal*`, `Lufe*`, `Bcra*`, `Casfog*`). With them in
place, `POST /api/entities/{id}/refresh-nosis` persists a NOSIS snapshot on the live entity and
`AnalysisController` copies it write-once onto the analysis result, which is what the 026 column was
reserved for.

Unlike its four siblings, NOSIS is **not** enriched during entity creation: it is billed per query,
so it is fetched only when an analyst explicitly asks. An entity that has never been refreshed keeps
both columns NULL, and an analysis run before the first refresh snapshots NULL — permanently, since
the snapshot is write-once. Expect NOSIS coverage on analyses to be sparse rather than universal.

**043** is not about NOSIS features — it is about being able to *see* them. It repairs two
independent defects that have kept `[dbo].[ApiCallLogs]` empty since the table was created.

### Defect 1 — `ApiCallLogs.TenantId` is the wrong type

Script 005 migrated `TenantId` from `UNIQUEIDENTIFIER` to `NVARCHAR(100)` on `Entities`,
`Documents`, `AnalysisResults` and `Analyses` "so they accept Firebase tenant slugs (e.g.
`SgrOneClickSandboxFB2`) instead of GUIDs". Script 003 had already done `Users`. **`ApiCallLogs` was
in neither**, so it still declares `UNIQUEIDENTIFIER`.

Meanwhile `ApiCallLogConfiguration` maps `TenantId` as a string with `HasMaxLength(100)`, and every
caller passes `tenantContext.TenantId` — a slug, not a GUID. SQL Server cannot convert
`'SgrOneClickSandboxFB2'` to `UNIQUEIDENTIFIER`, so the INSERT fails. This affects **every** service
that logs, not just the new one: Claude, ClaudeChat and ARCA are rejected for the same reason.

### Defect 2 — `CK_ApiCallLogs_Service` never learned about LUFE

The constraint was last extended in script 012, to
`('BYMA','Dynamics','BCRA','Claude','ClaudeChat','ARCA')`. `LufeService` writes `service: "LUFE"` in
nine places and has done since it shipped; every one of those rows violates the constraint.
`NosisService` writes `"NOSIS"` and would hit the same wall.

### Why neither defect was ever noticed

`IApiCallLogService` is contractually fire-and-forget — its doc comment requires implementations to
"catch all exceptions internally and never propagate them to callers". `ApiCallLogService` does
exactly that, logging `ApiCallLog write failed ... continuing` and returning normally. Callers use it
with a discard (`_ = _apiCallLogService.LogAsync(...)`). So a total write failure looks identical to
a successful write from every angle except the table itself.

**Fixing defect 2 alone would change nothing** — defect 1 rejects the row first. Both halves are
needed for a single log row to land, which is why they ship in one script.

043 deliberately touches **only** `ApiCallLogs`. `AuditLogs`, `SessionLogs` and any other table that
may carry the same mismatch are out of scope and need their own script and their own verification.

## DDL

Script locations: `docs/database/042_entities_add_nosisdatajson.sql`,
`docs/database/043_apicallogs_fix_tenantid_and_add_services.sql`

| Object | Type | Script |
|---|---|---|
| `[dbo].[Entities].[NosisDataJson]` | column, `NVARCHAR(MAX) NULL` | 042 |
| `[dbo].[Entities].[NosisDataUpdatedAt]` | column, `DATETIME2 NULL` | 042 |
| `[dbo].[ApiCallLogs].[TenantId]` | column, `UNIQUEIDENTIFIER` → `NVARCHAR(100) NOT NULL` | 043 |
| `IX_ApiCallLogs_TenantId_CalledAt` | index, dropped and recreated around the ALTER | 043 |
| `CK_ApiCallLogs_Service` | check — adds `LUFE` and `NOSIS` to the existing six | 043 |

Both NOSIS columns are nullable with no default and no backfill: existing rows stay NULL, which is
the honest value — no NOSIS snapshot was ever taken for them. `NosisDataUpdatedAt` is distinct from
`[UpdatedAt]`, which moves on any entity write.

The `TenantId` conversion is lossless. Any pre-existing GUID row becomes its 36-character string
form, comfortably inside `NVARCHAR(100)`. No rows are deleted and none can be truncated.

## Pre-conditions

- **042 must be executed BEFORE deploying the matching commit**, on **every** tenant database. This
  is a hard ordering requirement. Both columns are mapped in `EntityConfiguration`, so EF Core emits
  them in every `SELECT` and `INSERT` against `Entities`; against a database without them, entity
  creation **and** retrieval fail outright with "Invalid column name", and no application-level
  try/catch can protect that. The columns being nullable means nothing needs backfilling, but it does
  not make the ordering optional.
- **043 is not a deploy-blocker.** Without it the api-call log stays exactly as empty as it is today,
  and no read or write path outside the log itself is affected. Run it first anyway, so the NOSIS
  release is observable from its first call.
- **Restart the App Service after deploying**, so EF's model cache is rebuilt.
- Both scripts are idempotent and safe to re-run. 043's type conversion is guarded on the column's
  current type, so if a DBA has already fixed `TenantId` out-of-band that half is skipped and only
  the constraint is rebuilt.

## A note on `Nosis:Enabled`

The application ships with `Nosis:Enabled = false`. Running these scripts does **not** start NOSIS
traffic; it only makes the columns exist. Until the API key and VR group are provisioned in the App
Service settings and the flag is flipped, `NosisService` returns `LookupFailed` without a network
call, nothing is persisted, and `entity.nosis` stays null in every response. That is deliberate: the
schema can go ahead of the credential.

## Rollback

Reverse order, and roll the **application back first** in 042's case — the deployed code maps both
columns, so dropping them under a running deployment breaks entity creation and retrieval.

```sql
-- 042
ALTER TABLE [dbo].[Entities] DROP COLUMN [NosisDataUpdatedAt];
GO
ALTER TABLE [dbo].[Entities] DROP COLUMN [NosisDataJson];
GO
```

043 should **not** normally be rolled back: reverting the constraint re-breaks LUFE logging, and
reverting `TenantId` to `UNIQUEIDENTIFIER` would fail outright on any slug row written since. If it
must be undone, the constraint half alone is enough:

```sql
ALTER TABLE [dbo].[ApiCallLogs] DROP CONSTRAINT [CK_ApiCallLogs_Service];
GO
ALTER TABLE [dbo].[ApiCallLogs]
    ADD CONSTRAINT [CK_ApiCallLogs_Service]
    CHECK ([Service] IN ('BYMA','Dynamics','BCRA','Claude','ClaudeChat','ARCA'));
GO
```

## Verification

After applying both scripts, all five queries must return what is shown.

```sql
-- 1. Both NOSIS columns exist with the expected types → 2 rows
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'Entities'
  AND  COLUMN_NAME IN ('NosisDataJson', 'NosisDataUpdatedAt')
ORDER  BY COLUMN_NAME;
-- expected: NosisDataJson      nvarchar -1 YES
--           NosisDataUpdatedAt datetime2   YES

-- 2. ApiCallLogs.TenantId is no longer a uniqueidentifier → 1 row, nvarchar / 100
SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME = 'ApiCallLogs' AND COLUMN_NAME = 'TenantId';

-- 3. The covering index survived the ALTER → 1 row
SELECT name FROM sys.indexes
WHERE  object_id = OBJECT_ID(N'[dbo].[ApiCallLogs]')
  AND  name      = N'IX_ApiCallLogs_TenantId_CalledAt';

-- 4. The constraint now admits LUFE and NOSIS → definition lists all eight
SELECT definition
FROM   sys.check_constraints
WHERE  name = 'CK_ApiCallLogs_Service';

-- 5. End-to-end proof that a log row can now land — the whole point of 043.
--    Both inserts must SUCCEED where they previously failed. Roll it back; this is a probe.
BEGIN TRAN;
    INSERT INTO [dbo].[ApiCallLogs]
        (TenantId, Service, Endpoint, IsSuccess, CalledAt)
    VALUES
        (N'SgrOneClickSandboxFB2', N'NOSIS', N'rest/variables', 1, SYSUTCDATETIME()),
        (N'SgrOneClickSandboxFB2', N'LUFE',  N'entidades/30123456789', 1, SYSUTCDATETIME());
    SELECT @@ROWCOUNT AS RowsInserted;   -- expected: 2
ROLLBACK;
```

After the application is deployed with `Nosis:Enabled = true` and a valid key, real rows should
appear:

```sql
SELECT TOP (20) Service, Endpoint, ResponseStatus, IsSuccess, DurationMs, CalledAt
FROM   [dbo].[ApiCallLogs]
WHERE  Service IN ('NOSIS', 'LUFE')
ORDER  BY CalledAt DESC;
```

## Tenant checklist

| Tenant | 042 run date | 043 run date | Run by | Verified |
|--------|--------------|--------------|--------|----------|
|        |              |              |        |          |
|        |              |              |        |          |
|        |              |              |        |          |
|        |              |              |        |          |
