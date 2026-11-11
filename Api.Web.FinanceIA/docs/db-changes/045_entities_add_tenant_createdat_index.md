# 045 — The entities list stops scanning the tenant

## What changed and why

PR #138 made `GET /api/entities` stop *deserialising and shipping* the five source snapshots —
`FiscalDataJson`, `LufeDataJson`, `BcraDataJson`, `CasfogDataJson`, `NosisDataJson`. A `pageSize=100`
page went from 375 KB to about 2.4 KB. The endpoint stayed slow — 8 to 30 seconds against Azure SQL
serverless.

The payload was never the problem. `EntityRepository.GetAllByTenantAsync` ran

```csharp
.Where(x => x.TenantId == tenantId).ToListAsync(ct)
```

— no projection, no `ORDER BY`, no paging. That is `SELECT *` over every row the tenant owns, so SQL
Server still read all five `NVARCHAR(MAX)` snapshots off disk for every row, and the controller then
paged the materialised list in memory. **Not parsing a blob you already paid to read saves the CPU
and none of the I/O**, and the I/O was the wait.

The list query now names the eleven columns the table renders and pages in SQL:

```sql
SELECT EntityId, CUIT, BusinessName, TradeName, Sector, MipymeCategory,
       IsMonitored, RiskScore, RiskScoreUpdatedAt, CreatedAt, CreatedBy
FROM   [dbo].[Entities]
WHERE  TenantId = @tenant
ORDER BY CreatedAt DESC, EntityId
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
```

`EnrichProjectionAsync` is unchanged and still runs set-based over the page: it only ever needed
`EntityId` and `CreatedBy`, both of which the projection carries.

## What this script does

Adds `IX_Entities_TenantId_CreatedAt` on `(TenantId ASC, CreatedAt DESC, EntityId ASC)`.

`IX_Entities_TenantId` already exists, but it cannot order a page — there is no index involving
`CreatedAt`, so the new query would seek on tenant and then sort the whole tenant. With this index
the range is already in the required order and only page-sized batches are looked up.

**This makes `IX_Entities_TenantId` a redundant prefix.** Any plan that used it can use this one
instead. Dropping it would reclaim space and a write-time maintenance cost — but that is a separate
DBA decision and the script deliberately does not do it. Keeping both is correct, only slightly
wasteful.

No `INCLUDE` list: the nine remaining projected columns are narrow, but covering them would
duplicate most of the useful width of the table to save a lookup that is bounded by page size
(≤ 100 rows), not by tenant size.

## Ordering is a visible change

The list had **no `ORDER BY` at all** before. Rows arrived in whatever order SQL Server chose, which
also meant the old in-memory paging could already show an entity on two pages or on none — the bug
was just invisible because nobody was looking. The list is now newest-first with an `EntityId`
tie-break, which matters more here than it might look: `CreatedAt` is a non-unique `datetime2` and a
bulk import lands many entities in the same instant. This is the only user-visible change in the
release; no copy, field name or JSON type was touched.

## Execution

Not a deploy-blocker. Unlike scripts 039–042, this adds no column, so EF Core's model does not
reference anything the database might be missing and the endpoint works correctly without it — just
slowly. Run it before publishing the code anyway, per this folder's convention, so the release is
fast from its first request. **No App Service restart needed** — an index is invisible to EF's model
cache.

Run on every tenant business database. The script builds `WITH (ONLINE = ON)` so the table stays
writable; drop that clause on an edition that does not support online index builds.
