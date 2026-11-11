-- ============================================================================
-- Script  : 083_notifications_add_keyset_index.sql
-- Purpose : Create IX_Notifications_TenantId_UserId_CreatedAt_NotificationId
--           on [dbo].[Notifications], the index behind the keyset-paged
--           GET /api/notifications (limit / cursor / filters).
-- Date    : 2026-11-06
-- Author  : Claude (assisted)
-- Notes   : INDEX ONLY — NO TABLE OR COLUMN CHANGE. EF Core does not create
--           or depend on indexes (Database First), and the deploy gate's
--           --verify-schema compares tables and columns only, so this script
--           is SAFE TO RUN BEFORE OR AFTER THE DEPLOY of the matching code.
--           Without it the endpoint still answers correctly, just with a
--           Top-N sort over the user's rows instead of an ordered seek.
--           NO App Service restart is needed.
--
--           Run it in EVERY Financia (tenant business) database the DDL
--           procedure lists. Not in the shared Default database.
--
--           WHY A NEW INDEX. The listing now orders by
--             CreatedAt DESC, NotificationId DESC
--           and continues after a cursor with
--             CreatedAt < @c OR (CreatedAt = @c AND NotificationId < @id).
--           049's IX_Notifications_TenantId_UserId_CreatedAt has no
--           NotificationId key column; the clustered key SQL Server appends to
--           it is ASCENDING, so that index cannot deliver the DESC tie-break
--           in order. This one carries it as a DESC key column, so a page is
--           a range seek that stops after limit + 1 rows.
--
--           INCLUDE (IsRead, Type, SourceType, SourceId): the filter columns
--           of the listing (unreadOnly, type, sourceType, sourceId, entityId)
--           are evaluated on the index itself; only the returned page pays a
--           key lookup for Title / Body / ReadAt. Body is NVARCHAR(MAX) and is
--           deliberately NOT included.
--
--           THE UNREAD COUNT needs nothing new: 049's
--           IX_Notifications_TenantId_UserId_IsRead already answers
--           WHERE TenantId = @t AND UserId = @u AND IsRead = 0.
--
--           049's IX_Notifications_TenantId_UserId_CreatedAt becomes redundant
--           (this index has the same leading keys) but is KEPT: this release
--           makes no other schema change. A later script may drop it.
--
--           Idempotent: guarded on sys.indexes, re-runnable, prints what it
--           did or skipped.
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM   sys.indexes
    WHERE  object_id = OBJECT_ID(N'[dbo].[Notifications]')
      AND  name      = N'IX_Notifications_TenantId_UserId_CreatedAt_NotificationId'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Notifications_TenantId_UserId_CreatedAt_NotificationId]
        ON [dbo].[Notifications] ([TenantId] ASC, [UserId] ASC, [CreatedAt] DESC, [NotificationId] DESC)
        INCLUDE ([IsRead], [Type], [SourceType], [SourceId]);
    PRINT 'Index [IX_Notifications_TenantId_UserId_CreatedAt_NotificationId] created.';
END
ELSE
BEGIN
    PRINT 'Index [IX_Notifications_TenantId_UserId_CreatedAt_NotificationId] already exists — skipped.';
END
GO

-- ── Verification (DBA reference only — reads nothing sensitive) ─────────────
--
--   SELECT i.name, c.name AS column_name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
--   FROM   sys.indexes i
--   JOIN   sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
--   JOIN   sys.columns c        ON c.object_id  = ic.object_id AND c.column_id = ic.column_id
--   WHERE  i.object_id = OBJECT_ID(N'[dbo].[Notifications]')
--     AND  i.name      = N'IX_Notifications_TenantId_UserId_CreatedAt_NotificationId'
--   ORDER  BY ic.is_included_column, ic.key_ordinal;
--
-- Expected: four key columns in order TenantId, UserId, CreatedAt (descending),
-- NotificationId (descending), and four included columns IsRead, Type,
-- SourceType, SourceId.
