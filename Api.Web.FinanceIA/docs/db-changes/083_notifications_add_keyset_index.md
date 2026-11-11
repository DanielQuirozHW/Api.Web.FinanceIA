# 083 — Keyset-paging index on Notifications

## What changed and why

`GET /api/notifications` used to return **every** notification of the user on every 60 s Portal
poll (no `TOP`), and computed `unreadCount` from that list. It is now a bounded, keyset-paged read:

- `ORDER BY CreatedAt DESC, NotificationId DESC`, `TOP (limit + 1)` (limit 1..100, default 50);
- the next page continues after the previous page's last row:
  `CreatedAt < @c OR (CreatedAt = @c AND NotificationId < @id)` — never `OFFSET`;
- optional filters (`unreadOnly`, `type`, `sourceType`, `sourceId`, `entityId`, `from`/`to`) are
  residual predicates within the user's `(TenantId, UserId)` range;
- `unreadCount` is its own `COUNT`, independent of page and filters; `totalCount` is the `COUNT`
  under the current filters.

049's `IX_Notifications_TenantId_UserId_CreatedAt` cannot serve that ORDER BY in index order: it has no
`NotificationId` key column, and the clustered key SQL Server appends to a non-unique nonclustered
index is ascending. 083 adds:

```sql
IX_Notifications_TenantId_UserId_CreatedAt_NotificationId
  ON (TenantId, UserId, CreatedAt DESC, NotificationId DESC)
  INCLUDE (IsRead, Type, SourceType, SourceId)
```

so a page is an ordered range seek that stops after `limit + 1` rows, and the filter columns are read
from the index. Only the rows returned pay a key lookup (Title, Body, ReadAt). Body is
`nvarchar(max)` and is not included.

The unread badge needs nothing new: 049's `IX_Notifications_TenantId_UserId_IsRead` serves
`WHERE TenantId = @t AND UserId = @u AND IsRead = 0`.

## Deploy order

**None.** An index is not something EF Core creates or depends on (Database First), and the deploy
gate's `--verify-schema` checks tables and columns only. The code is correct with or without 083;
without it a page costs a Top-N sort over the user's rows instead of a seek. No App Service restart.
Run it in every Financia (tenant business) database.

`NotificationConfiguration` carries matching `HasIndex` metadata, documentary only, as it does for
049's two indexes.

## What it does not do

It keeps 049's `IX_Notifications_TenantId_UserId_CreatedAt`, now redundant (same leading keys).
Dropping it is a separate, later script — this release makes no other schema change.
