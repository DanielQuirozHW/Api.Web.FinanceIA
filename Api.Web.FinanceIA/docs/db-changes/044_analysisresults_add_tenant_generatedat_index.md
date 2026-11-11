# 044 — The analyses list stops scanning the tenant

## What changed and why

PR #137 made `GET /api/analyses` stop *shipping* the heavy columns: the response went from the full
`AnalysisResultResponse` to the narrow `AnalysisListItemResponse`, and a `pageSize=200` page fell
from 213 KB to about 4 KB. The endpoint stayed slow — 8 to 30 seconds against Azure SQL serverless.

The payload was never the problem. `AnalysisResultRepository.GetAllByTenantAsync` ran

```csharp
.Where(x => x.TenantId == tenantId).ToListAsync(ct)
```

— no projection, no `ORDER BY`, no paging. That is `SELECT *` over every row the tenant owns,
and `AnalysisResult` carries **fifteen** `NVARCHAR(MAX)` columns: `BalanceJson`, `RatiosJson`,
`BcraDataJson`, `MacroDataJson`, `ExecutiveSummary`, `ScoringNarrative`, `RiskAlerts`,
`CatalogueAlerts`, `Anomalies`, `ValidationNotes`, `ScoreBreakdownJson`, `LufeDataJson`,
`FiscalDataJson`, `NosisDataJson`, `CasfogDataJson`. SQL Server read all of them off disk for every
row, and the controller then threw nearly all of it away with an in-memory `Skip().Take()`. That
read was the 8–30s, and no DTO change could reach it.

The list query now projects six columns and pages in SQL:

```sql
SELECT ResultId, AnalysisId, GeneratedAt, AiSuggestedScore, AiSuggestedLevel, ScoreBreakdownJson
FROM   [dbo].[AnalysisResults]
WHERE  TenantId = @tenant
ORDER BY GeneratedAt DESC, ResultId
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
```

`ScoreBreakdownJson` is the one blob that survives, and it has to: the four scoring signals the
table renders — `sectorProfile`, `criticalRatiosCount`, `appliedFloor`, `bcraTrendDelta` — are
parsed out of it in C# and have no columns of their own. It is small, and only page-sized batches of
it are ever fetched.

## What this script does

Adds `IX_AnalysisResults_TenantId_GeneratedAt` on `(TenantId ASC, GeneratedAt DESC, ResultId ASC)`.

`[dbo].[AnalysisResults]` had **no nonclustered index whatsoever** before this — not even on
`TenantId`. Without the index the new query is correct but still scans the tenant's rows and sorts
them; with it, the query is a seek into an already-ordered range plus a key lookup for at most
`pageSize` rows.

There is no `INCLUDE` list on purpose. `ScoreBreakdownJson` must be fetched anyway, so a key lookup
happens either way; including an `NVARCHAR(MAX)` column would copy the blob into the index and about
double its size for nothing.

## Ordering is a visible change

The list had **no `ORDER BY` at all** before. Rows arrived in whatever order SQL Server chose, which
also meant the old in-memory paging could already show a row on two pages or on none — the bug was
just invisible because nobody was looking. The list is now newest-first with a `ResultId` tie-break,
which is both stable and what the Portal's analysis queue wants. This is the only user-visible
change in the release; no copy, no field names and no JSON types were touched.

## Execution

Not a deploy-blocker. Unlike scripts 039–042, this adds no column, so EF Core's model does not
reference anything the database might be missing and the endpoint works correctly without it — just
slowly. Run it before publishing the code anyway, per this folder's convention, so the release is
fast from its first request. **No App Service restart needed** — an index is invisible to EF's model
cache.

Run on every tenant business database. The script builds `WITH (ONLINE = ON)` so the table stays
writable; drop that clause on an edition that does not support online index builds.
